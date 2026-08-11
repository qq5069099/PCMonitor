using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// 启动时确保已加入 Windows 启动组（注册表 Run 键）
Utility.EnsureInStartup();

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
var _ctp = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
_ctp.Mappings[".exe"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = _ctp });
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// ─── 全局状态 ─────────────────────────────────────────────────
// 所有已知 PC（在线 + 离线），key = pcId
var pcs = new ConcurrentDictionary<string, PCObject>();
var viewerClients = new ConcurrentDictionary<string, ViewerEntry>();
var db = new TextDB(Path.Combine(AppContext.BaseDirectory, "data.db"));

// 启动时从 TextDB 恢复 PC 列表及锁定状态
foreach (var key in db.Keys.Where(k => k.StartsWith("pc_")))
{
    var id = key[3..];
    if (string.IsNullOrEmpty(id)) continue;
    var obj = db.GetObject(key);
    var name = obj?["name"]?.GetValue<string>() ?? "";
    var locked = obj?["locked"]?.GetValue<bool>() ?? false;
    var remark = obj?["remark"]?.GetValue<string>() ?? "";
    var scheduleLock = obj?["scheduleLock"]?.GetValue<string>() ?? "";
    pcs[id] = new PCObject { Id = id, Name = name, Locked = locked, Remark = remark, ScheduleLock = scheduleLock };
}
Console.WriteLine($"[DB] 恢复已知 PC {pcs.Count} 台，锁定 {pcs.Values.Count(p => p.Locked)} 台");

// ─── 自动更新：计算客户端 exe 的 MD5 ──────────────────────────
var clientExePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "PCMonitorClient.exe");
var clientMd5 = File.Exists(clientExePath) ? Utility.Md5File(Path.Combine(AppContext.BaseDirectory, "wwwroot", "PCMonitorClient.exe")) : "";
app.MapGet("/update/check", (HttpContext ctx) =>
{
    return Results.Json(new
    {
        md5 = clientMd5,
        url = string.IsNullOrEmpty(clientMd5) ? "" : $"{ctx.Request.Scheme}://{ctx.Request.Host}/PCMonitorClient.exe"
    });
});

// ─── WebSocket 入口 ──────────────────────────────────────────
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

    var role = ctx.Request.Query["role"].ToString();
    var ws = await ctx.WebSockets.AcceptWebSocketAsync();

    if (role == "pc") await HandlePcAsync(ws);
    else if (role == "viewer") await HandleViewerAsync(ws);
    else await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unknown role", default);
});

// ─── 定时锁定后台任务 ──────────────────────────────────────────
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        foreach (var pc in pcs.Values.Where(p => !string.IsNullOrEmpty(p.ScheduleLock)))
        {
            bool shouldBeLocked = IsInScheduleLock(pc.ScheduleLock);
            if (shouldBeLocked == pc.IsLockActive) continue;
            // 离线时不处理，等 PC 重连后由重连逻辑重新计算并同步
            if (!pc.IsOnline) continue;
            var cmd = shouldBeLocked ? "lock_input" : "unlock_input";
            bool sent = await ForwardControlToPcAsync(pc.Id, cmd);
            if (sent)
            {
                pc.IsLockActive = shouldBeLocked;
                Console.WriteLine($"[定时锁] PC {pc.Name} 定时{(shouldBeLocked ? "锁定" : "解锁")}");
            }
        }
    }
});

app.Run($"http://0.0.0.0:{app.Configuration.GetValue("ServerPort", 999)}");

// ═══════════════════════════════════════════════════════════════
// PC 端连接处理
// ═══════════════════════════════════════════════════════════════
async Task HandlePcAsync(WebSocket ws)
{
    string pcId = "", pcName = "";
    try
    {
        var buf = new byte[4 * 1024 * 1024]; // 4 MB，容纳完整帧
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;

            // 收集完整消息（可能分多帧传输）
            using var ms = new MemoryStream();
            ms.Write(buf, 0, result.Count);
            while (!result.EndOfMessage)
            {
                result = await ws.ReceiveAsync(buf, CancellationToken.None);
                ms.Write(buf, 0, result.Count);
            }
            var payload = ms.ToArray();

            if (result.MessageType == WebSocketMessageType.Text)
            {
                // 注册消息: {"type":"register","id":"...","name":"..."}
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.GetProperty("type").GetString() == "register")
                {
                    pcId = root.GetProperty("id").GetString()!;
                    pcName = root.GetProperty("name").GetString()!;
                    var ver = root.TryGetProperty("version", out var vp) ? vp.GetString() ?? "" : "";
                    // 获取或创建 PcRecord，更新在线状态
                    var rec = pcs.GetOrAdd(pcId, id => new PCObject { Id = id });
                    if (rec.Name != pcName)
                    {
                        rec.Name = pcName;
                        SavePc(rec);
                    }
                    if (!string.IsNullOrEmpty(ver)) rec.Version = ver;
                    rec.Socket = ws;
                    Console.WriteLine($"[+] PC 注册: {pcName} ({pcId})");
                    await BroadcastPcListAsync();

                    // 将新 PC 加入所有处于 watchAll 模式的观看者（跳过已被该观看者临时排除的 PC）
                    foreach (var v in viewerClients.Values.Where(v => v.IsWatchingAll && !v.ExcludedPcIds.Contains(pcId)))
                        v.WatchingPcIds.Add(pcId);

                    // PC 重连后，如果已有观看者正在等待该 PC，自动恢复推流
                    var interestedViewers = viewerClients.Values
                        .Where(v => v.WatchingPcIds.Contains(pcId))
                        .ToArray();
                    if (interestedViewers.Length > 0)
                    {
                        var best = interestedViewers.OrderByDescending(v => v.Quality).First();
                        await NotifyPcStartAsync(pcId, best.Quality, best.Fps);
                    }

                    // PC 重连后检查定时锁定时段，计算当前是否应处于锁定状态
                    if (!string.IsNullOrEmpty(rec.ScheduleLock))
                    {
                        rec.IsLockActive = IsInScheduleLock(rec.ScheduleLock);
                    }

                    // 综合判断：手动锁定或定时锁定任一为 true 则应锁定，否则应解锁
                    // 重连时必须明确发送锁定或解锁指令，确保客户端状态与服务器一致
                    // 解决：PC 在锁定/解锁交界时刻离线，重连后状态不一致的问题
                    bool shouldLock = rec.Locked || rec.IsLockActive;
                    if (shouldLock)
                    {
                        var reason = rec.Locked && rec.IsLockActive ? "手动+定时锁定"
                                   : rec.Locked ? "手动锁定"
                                   : "定时锁定时段";
                        Console.WriteLine($"[锁] PC 重连，恢复锁定（{reason}）: {pcName}");
                        await ForwardControlToPcAsync(pcId, "lock_input");
                    }
                    else
                    {
                        Console.WriteLine($"[锁] PC 重连，解除锁定: {pcName}");
                        await ForwardControlToPcAsync(pcId, "unlock_input");
                    }
                }
            }
            else if (result.MessageType == WebSocketMessageType.Binary && !string.IsNullOrEmpty(pcId))
            {
                // 屏幕帧 → 转发给所有正在观看该 PC 的浏览器
                var watchers = viewerClients.Values
                    .Where(v => v.WatchingPcIds.Contains(pcId))
                    .ToArray();
                await Task.WhenAll(watchers.Select(v => SafeSendBinaryAsync(v, pcId, payload)));
            }
        }
    }
    catch (Exception ex) { Console.WriteLine($"PC 异常 [{pcName}]: {ex.Message}"); }
    finally
    {
        if (!string.IsNullOrEmpty(pcId) && pcs.TryGetValue(pcId, out var offline))
        {
            // 只有当 Socket 仍是本次连接时才清除，防止新连接的 Socket 被旧连接的 finally 覆盖清空
            if (ReferenceEquals(offline.Socket, ws))
            {
                offline.Socket = null;
                Console.WriteLine($"[-] PC 断开: {pcName}");
                await BroadcastPcListAsync();
            }
            else
            {
                Console.WriteLine($"[-] PC 旧连接关闭（已被新连接替代，不清除 Socket）: {pcName}");
            }
        }
    }
}

// ═══════════════════════════════════════════════════════════════
// 浏览器端连接处理
// ═══════════════════════════════════════════════════════════════
async Task HandleViewerAsync(WebSocket ws)
{
    var viewerId = Guid.NewGuid().ToString("N")[..8];
    var viewer = new ViewerEntry { Id = viewerId, Socket = ws };
    viewerClients[viewerId] = viewer;
    Console.WriteLine($"[+] 观看者连接: {viewerId}");

    // 立即推送当前 PC 列表
    await SendPcListAsync(viewer);

    try
    {
        var buf = new byte[512];
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var msg = Encoding.UTF8.GetString(buf, 0, result.Count);
            using var doc = JsonDocument.Parse(msg);
            var action = doc.RootElement.GetProperty("action").GetString();

            if (action == "watch")
            {
                var newPcId = doc.RootElement.GetProperty("pcId").GetString()!;
                // 停止所有当前观看，切换到单 PC 模式
                var oldIds = viewer.WatchingPcIds.ToArray();
                viewer.WatchingPcIds.Clear();
                viewer.IsWatchingAll = false;
                foreach (var id in oldIds) await CheckAndStopAsync(id);

                viewer.WatchingPcIds.Add(newPcId);
                int quality = doc.RootElement.TryGetProperty("quality", out var qProp) ? qProp.GetInt32() : 55;
                int fps = doc.RootElement.TryGetProperty("fps", out var fProp) ? fProp.GetInt32() : 2;
                viewer.Quality = quality;
                viewer.Fps = fps;
                await NotifyPcStartAsync(newPcId, quality, fps);
            }
            else if (action == "watchAll")
            {
                // 全屏监控模式：观看所有已连接的 PC
                var oldIds = viewer.WatchingPcIds.ToArray();
                viewer.WatchingPcIds.Clear();
                foreach (var id in oldIds) await CheckAndStopAsync(id);

                int quality = doc.RootElement.TryGetProperty("quality", out var qProp) ? qProp.GetInt32() : 55;
                int fps = doc.RootElement.TryGetProperty("fps", out var fProp) ? fProp.GetInt32() : 2;
                viewer.Quality = quality;
                viewer.Fps = fps;
                viewer.IsWatchingAll = true;
                viewer.ExcludedPcIds.Clear(); // 重新进入全屏模式时清除临时排除列表

                foreach (var p in pcs.Values.Where(p => p.IsOnline))
                {
                    viewer.WatchingPcIds.Add(p.Id);
                    await NotifyPcStartAsync(p.Id, quality, fps);
                }
            }
            else if (action == "unwatch")
            {
                var oldIds = viewer.WatchingPcIds.ToArray();
                viewer.WatchingPcIds.Clear();
                viewer.IsWatchingAll = false;
                foreach (var id in oldIds) await CheckAndStopAsync(id);
            }
            else if (action == "control")
            {
                var targetPcId = doc.RootElement.TryGetProperty("pcId", out var pidProp) ? pidProp.GetString() : null;
                var ctrlCmd = doc.RootElement.TryGetProperty("cmd", out var cmdProp) ? cmdProp.GetString() : null;
                if (!string.IsNullOrEmpty(targetPcId) && !string.IsNullOrEmpty(ctrlCmd)
                    && pcs.TryGetValue(targetPcId, out var target))
                {
                    if (ctrlCmd == "lock_input")
                    {
                        target.Locked = true;
                        SavePc(target);
                    }
                    else if (ctrlCmd == "unlock_input")
                    {
                        target.Locked = false;
                        SavePc(target);
                    }
                    await ForwardControlToPcFullAsync(targetPcId, doc.RootElement);
                    // 锁定状态变化后广播列表，让所有观看者 UI 刷新
                    if (ctrlCmd == "lock_input" || ctrlCmd == "unlock_input")
                        await BroadcastPcListAsync();
                }
            }
            else if (action == "excludePc")
            {
                // 在全屏监控模式下临时排除某台 PC（右上角叉掉）
                var exPcId = doc.RootElement.TryGetProperty("pcId", out var exProp) ? exProp.GetString() : null;
                if (!string.IsNullOrEmpty(exPcId))
                {
                    viewer.WatchingPcIds.Remove(exPcId);
                    viewer.ExcludedPcIds.Add(exPcId);
                    await CheckAndStopAsync(exPcId);
                }
            }
            else if (action == "setRemark")
            {
                var targetPcId = doc.RootElement.TryGetProperty("pcId", out var rpidProp) ? rpidProp.GetString() : null;
                var remark = doc.RootElement.TryGetProperty("remark", out var remProp) ? remProp.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(targetPcId) && pcs.TryGetValue(targetPcId, out var remTarget))
                {
                    remTarget.Remark = remark;
                    SavePc(remTarget);
                    await BroadcastPcListAsync();
                }
            }
            else if (action == "setScheduleLock")
            {
                // 设置或清除定时锁定时段，格式 "HH:mm-HH:mm"（可跨越0点），空字符串表示取消
                var targetPcId = doc.RootElement.TryGetProperty("pcId", out var slPidProp) ? slPidProp.GetString() : null;
                var scheduleLock = doc.RootElement.TryGetProperty("scheduleLock", out var slProp) ? slProp.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(targetPcId) && pcs.TryGetValue(targetPcId, out var slTarget))
                {
                    slTarget.ScheduleLock = scheduleLock;
                    SavePc(slTarget);
                    await BroadcastPcListAsync();
                }
            }
        }
    }
    catch { }
    finally
    {
        viewerClients.TryRemove(viewerId, out _);
        // 观看者断开（含浏览器刷新/关闭），停止所有被观看的 PC 推流
        var disconnectedIds = viewer.WatchingPcIds.ToArray();
        foreach (var id in disconnectedIds) await CheckAndStopAsync(id);
        Console.WriteLine($"[-] 观看者断开: {viewerId}");
    }
}

// ─── 辅助函数 ─────────────────────────────────────────────────
void SavePc(PCObject pc)
{
    db.Set("pc_" + pc.Id, new JsonObject
    {
        ["name"] = JsonValue.Create(pc.Name),
        ["locked"] = JsonValue.Create(pc.Locked),
        ["remark"] = JsonValue.Create(pc.Remark),
        ["scheduleLock"] = JsonValue.Create(pc.ScheduleLock)
    });
}

bool IsInScheduleLock(string scheduleLock)
{
    if (string.IsNullOrEmpty(scheduleLock)) return false;
    var parts = scheduleLock.Split('-');
    if (parts.Length != 2) return false;
    if (!TimeSpan.TryParse(parts[0].Trim(), out var start) ||
        !TimeSpan.TryParse(parts[1].Trim(), out var end)) return false;
    var now = DateTime.Now.TimeOfDay;
    // 跨越0点时（如 23:30-07:00），now >= start 或 now < end 均为锁定时段
    return start <= end ? now >= start && now < end : now >= start || now < end;
}

async Task CheckAndStopAsync(string pcId)
{
    bool stillWatched = viewerClients.Values.Any(v => v.WatchingPcIds.Contains(pcId));
    if (!stillWatched) await NotifyPcAsync(pcId, "stop");
}

async Task NotifyPcAsync(string pcId, string command)
{
    if (!pcs.TryGetValue(pcId, out var pc) || !pc.IsOnline) return;
    await SafeSendTextAsync(pc.Socket!, JsonSerializer.Serialize(new { command }));
}

async Task NotifyPcStartAsync(string pcId, int quality, int fps)
{
    if (!pcs.TryGetValue(pcId, out var pc) || !pc.IsOnline) return;
    await SafeSendTextAsync(pc.Socket!, JsonSerializer.Serialize(new { command = "start", quality, fps }));
}

// 转发简化的控制命令，返回是否发送成功
async Task<bool> ForwardControlToPcAsync(string pcId, string cmd)
{
    if (!pcs.TryGetValue(pcId, out var pc) || !pc.IsOnline) return false;
    return await SafeSendTextAsync(pc.Socket!, JsonSerializer.Serialize(new { command = "control", cmd }));
}

// 转发含完整附加字段的控制命令（鼠标/键盘坐标等），保留 viewer 发来的所有属性
async Task ForwardControlToPcFullAsync(string pcId, JsonElement sourceMsg)
{
    if (!pcs.TryGetValue(pcId, out var pc) || !pc.IsOnline) return;
    using var ms = new System.IO.MemoryStream();
    using (var writer = new Utf8JsonWriter(ms))
    {
        writer.WriteStartObject();
        writer.WriteString("command", "control");
        foreach (var prop in sourceMsg.EnumerateObject())
        {
            if (prop.Name is "action" or "pcId") continue; // 去掉 viewer 专用字段
            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
    await SafeSendTextAsync(pc.Socket!, Encoding.UTF8.GetString(ms.ToArray()));
}

async Task BroadcastPcListAsync()
{
    await Task.WhenAll(viewerClients.Values.Select(v => SendPcListAsync(v)));
}

async Task SendPcListAsync(ViewerEntry viewer)
{
    // 合并已知 PC（在线 + 离线），在线排前
    var list = pcs.Values
        .Select(p => new { p.Id, p.Name, p.Remark, Online = p.IsOnline, p.Locked, p.Version, p.ScheduleLock })
        .OrderByDescending(p => p.Online)
        .ThenBy(p => p.Name)
        .ToList();
    var json = JsonSerializer.Serialize(new { type = "pcList", pcs = list });
    // 使用 SendLock 防止与二进制帧并发发送同一 WebSocket 导致 InvalidOperationException
    if (!await viewer.SendLock.WaitAsync(200)) return;
    try
    {
        if (viewer.Socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await viewer.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }
    catch { }
    finally { viewer.SendLock.Release(); }
}

async Task<bool> SafeSendTextAsync(WebSocket ws, string text)
{
    try
    {
        if (ws.State != WebSocketState.Open) return false;
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        return true;
    }
    catch { return false; }
}

async Task SafeSendBinaryAsync(ViewerEntry viewer, string pcId, byte[] data)
{
    // 非阻塞：若上一帧还未发完，直接丢弃当前帧（已过时）
    if (!await viewer.SendLock.WaitAsync(0)) return;
    try
    {
        if (viewer.Socket.State != WebSocketState.Open) return;
        // 帧格式: [8字节服务器UTC毫秒ts][1字节pcId长度][N字节pcId][原始PC数据(8字节客户端ts+JPEG)]
        var tsBytes = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var pcIdBytes = Encoding.UTF8.GetBytes(pcId);
        var buf = new byte[8 + 1 + pcIdBytes.Length + data.Length];
        tsBytes.CopyTo(buf, 0);
        buf[8] = (byte)Math.Min(pcIdBytes.Length, 255);
        pcIdBytes.CopyTo(buf, 9);
        data.CopyTo(buf, 9 + pcIdBytes.Length);
        await viewer.Socket.SendAsync(buf, WebSocketMessageType.Binary, true, CancellationToken.None);
    }
    catch { }
    finally { viewer.SendLock.Release(); }
}

// ─── 数据模型 ──────────────────────────────────────────────────
class PCObject
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public string Remark { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Locked { get; set; } = false;
    public string ScheduleLock { get; set; } = ""; // 定时锁定时段，格式 "HH:mm-HH:mm"，可跨越0点
    public bool IsLockActive { get; set; } = false; // 当前是否处于定时锁定状态
    public WebSocket? Socket { get; set; } = null;
    public bool IsOnline => Socket is { State: WebSocketState.Open };
}

class ViewerEntry
{
    public string Id { get; init; } = "";
    public WebSocket Socket { get; init; } = null!;
    public HashSet<string> WatchingPcIds { get; } = new();
    public HashSet<string> ExcludedPcIds { get; } = new();
    public bool IsWatchingAll { get; set; } = false;
    public int Quality { get; set; } = 55;
    public int Fps { get; set; } = 2;
    public SemaphoreSlim SendLock { get; } = new(1, 1);
}
