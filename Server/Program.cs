using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// 启动时确保已加入 Windows 启动组（注册表 Run 键）
Utility.EnsureInStartup();

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ─── 全局状态 ─────────────────────────────────────────────────
// 所有已知 PC（在线 + 离线），key = pcId
var pcs = new ConcurrentDictionary<string, PCObject>();
var viewerClients = new ConcurrentDictionary<string, ViewerEntry>();
var db = new TextDB(Path.Combine(AppContext.BaseDirectory, "data.db"));

// ─── data.db 结构迁移：旧版顶层 pc_xxx 键 → pcs 子对象 ──────
if (db.GetObject("pcs") == null)
{
    var legacyPcs = db.Keys.Where(k => k.StartsWith("pc_")).ToArray();
    if (legacyPcs.Length > 0)
    {
        var pcsObj = new JsonObject();
        foreach (var key in legacyPcs)
        {
            pcsObj[key[3..]] = db.Get(key)?.DeepClone();
            db.Remove(key);
        }
        db.Set("pcs", pcsObj);
        Console.WriteLine($"[DB] 已迁移旧版 PC 数据 {legacyPcs.Length} 条到 pcs 子对象");
    }
    else
    {
        db.Set("pcs", new JsonObject());
    }
}

// 启动时从 TextDB 恢复 PC 列表及锁定状态
// 兼容处理：pcs 子对象内的 key 若带 "pc_" 前缀则自动去掉（与用户权限列表保持一致）
var pcsData = db.GetObject("pcs");
bool pcsKeyNormalized = false;
if (pcsData != null)
{
    var keys = pcsData.Select(kv => kv.Key).ToArray();
    foreach (var key in keys)
    {
        if (!key.StartsWith("pc_")) continue;
        if (pcsData[key] is not JsonObject obj) continue;
        pcsData.Remove(key);
        pcsData[key[3..]] = obj;
        pcsKeyNormalized = true;
    }
    if (pcsKeyNormalized) db.Set("pcs", pcsData);

    foreach (var kv in pcsData)
    {
        if (kv.Value is not JsonObject obj) continue;
        var id = kv.Key;
        if (string.IsNullOrEmpty(id)) continue;
        var name = obj["name"]?.GetValue<string>() ?? "";
        var locked = obj["locked"]?.GetValue<bool>() ?? false;
        var remark = obj["remark"]?.GetValue<string>() ?? "";
        var scheduleLock = obj["scheduleLock"]?.GetValue<string>() ?? "";
        pcs[id] = new PCObject { Id = id, Name = name, Locked = locked, Remark = remark, ScheduleLock = scheduleLock };
    }
}
if (pcsKeyNormalized) Console.WriteLine("[DB] 已去除 pcs 子对象中 pc_ 前缀");
Console.WriteLine($"[DB] 恢复已知 PC {pcs.Count} 台，锁定 {pcs.Values.Count(p => p.Locked)} 台");

// ─── 用户配置（登录账号）─────────────────────────────────────
// 结构: users 子对象，每个账号可配置可观察的 PC 列表（pcs 数组），空数组 = 所有 PC
var users = new Dictionary<string, UserConfig>(StringComparer.OrdinalIgnoreCase);
var usersData = db.GetObject("users");
if (usersData != null)
{
    foreach (var kv in usersData)
    {
        if (kv.Value is not JsonObject obj) continue;
        var password = obj["password"]?.GetValue<string>() ?? "";
        HashSet<string>? allowed = null;
        if (obj["pcs"] is JsonArray arr && arr.Count > 0)
            allowed = arr.Select(x => x?.GetValue<string>() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToHashSet();
        users[kv.Key] = new UserConfig { Username = kv.Key, Password = password, AllowedPcIds = allowed };
    }
}
if (users.Count == 0)
{
    users["admin"] = new UserConfig { Username = "admin", Password = "admin123", AllowedPcIds = null };
    db.Set("users", new JsonObject
    {
        ["admin"] = new JsonObject { ["password"] = "admin123", ["pcs"] = new JsonArray() }
    });
    Console.WriteLine("[DB] 未配置任何登录用户，已创建默认账号 admin / admin123，请尽快在 data.db 修改！");
}

// ─── 登录会话 ────────────────────────────────────────────────
// 支持同一账号多处同时登录：每次登录生成独立 token，互不挤下线
var sessions = new ConcurrentDictionary<string, ViewerSession>();

bool TryGetSession(HttpContext ctx, out ViewerSession session)
{
    if (ctx.Request.Cookies.TryGetValue("pcm_token", out var token)
        && sessions.TryGetValue(token, out var s) && s != null)
    {
        session = s;
        return true;
    }
    session = null!;
    return false;
}

// 永久记住密码：从 remember cookie 中还原账号密码，若与 data.db 当前配置一致
// 则自动重建会话（服务器修改密码后自动登录将失败，从而回到登录页）
bool TryAutoLogin(HttpContext ctx, out ViewerSession session)
{
    session = null!;
    if (!ctx.Request.Cookies.TryGetValue("pcm_remember", out var remember)) return false;
    try
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(remember));
        var sep = decoded.IndexOf('\n');
        if (sep <= 0) return false;
        var username = decoded[..sep];
        var password = decoded[(sep + 1)..];
        // 密码必须与 data.db 当前配置完全一致才允许自动登录
        if (!users.TryGetValue(username, out var user) || user.Password != password) return false;
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        session = new ViewerSession { Username = username, AllowedPcIds = user.AllowedPcIds };
        sessions[token] = session;
        // 重新下发会话 cookie，后续请求直接用会话，不必每次重建
        ctx.Response.Cookies.Append("pcm_token", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(30)
        });
        return true;
    }
    catch { return false; }
}

// 无需登录即可访问的路径（PCClient 更新 / 登录页 / 登录接口）
bool IsPublicPath(HttpContext ctx)
{
    var path = ctx.Request.Path.Value ?? "";
    if (path is "/login.html" or "/login" or "/update/check" or "/PCMonitorClient.exe" or "/api/login" or "/api/remembered-user") return true;
    if (path == "/ws" && ctx.Request.Query["role"].ToString() == "pc") return true;
    return false;
}

// WebSocket 中间件需先注册，认证中间件才能正确识别 WebSocket 请求
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// ─── 认证中间件：未登录用户无法访问任何页面 / API / viewer WebSocket ──
// 1. 已有有效会话 → 通过
// 2. 无会话但记住的密码仍与 data.db 一致 → 自动重建会话通过（永久记住密码）
// 3. 否则拦截：页面跳登录页，API 返回 401，viewer WebSocket 拒绝
app.Use(async (ctx, next) =>
{
    if (IsPublicPath(ctx)) { await next(); return; }
    if (TryGetSession(ctx, out _) || TryAutoLogin(ctx, out _)) { await next(); return; }
    if (ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 401; return; }
    if ((ctx.Request.Path.Value ?? "").StartsWith("/api/"))
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "未登录" });
        return;
    }
    // 已记住密码但自动登录失败（如服务器修改了密码）→ 登录页提示重新登录
    if (ctx.Request.Cookies.ContainsKey("pcm_remember"))
        ctx.Response.Redirect("/login.html?msg=autologin_failed");
    else
        ctx.Response.Redirect("/login.html");
});

// 登录
app.MapPost("/api/login", async (HttpContext ctx) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var root = doc.RootElement;
        var username = root.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
        var password = root.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
        if (!users.TryGetValue(username, out var user) || user.Password != password)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "账号或密码错误" });
            return;
        }
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        sessions[token] = new ViewerSession { Username = username, AllowedPcIds = user.AllowedPcIds };
        ctx.Response.Cookies.Append("pcm_token", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(30)
        });
        // 永久记住密码：10 年有效，之后打开直接自动登录，无需再输密码
        ctx.Response.Cookies.Append("pcm_remember",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}\n{password}")),
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddYears(10)
            });
        await ctx.Response.WriteAsJsonAsync(new { ok = true, username });
    }
    catch (JsonException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "请求格式错误" }); }
});

// 登录页回填账号密码：读取 remember cookie 中记住的账号与密码，供登录页自动回填
app.MapGet("/api/remembered-user", (HttpContext ctx) =>
{
    if (ctx.Request.Cookies.TryGetValue("pcm_remember", out var remember))
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(remember));
            var sep = decoded.IndexOf('\n');
            if (sep > 0)
                return Results.Json(new { ok = true, username = decoded[..sep], password = decoded[(sep + 1)..] });
        }
        catch { }
    }
    return Results.Json(new { ok = false });
});

// 退出登录：仅退出当前会话，保留记住的密码（下次仍自动登录）
// 若要彻底忘记密码，可在浏览器中清除 pcm_remember cookie
app.MapPost("/api/logout", (HttpContext ctx) =>
{
    if (ctx.Request.Cookies.TryGetValue("pcm_token", out var token)) sessions.TryRemove(token, out _);
    ctx.Response.Cookies.Delete("pcm_token");
    return Results.Json(new { ok = true });
});

// 当前登录用户
app.MapGet("/api/me", (HttpContext ctx) =>
{
    if (TryGetSession(ctx, out var s))
    {
        // 通过响应头告知前端该用户可观察的 PC 列表（null = 所有 PC）
        if (s.AllowedPcIds != null)
            ctx.Response.Headers["x-allowed-pcs"] = JsonSerializer.Serialize(s.AllowedPcIds);
        return Results.Json(new { ok = true, username = s.Username });
    }
    return Results.Json(new { ok = false }, statusCode: 401);
});

app.UseDefaultFiles();
var _ctp = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
_ctp.Mappings[".exe"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = _ctp });

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
    else if (role == "viewer")
    {
        // viewer 必须已登录（认证中间件已校验），此处取得会话用于权限过滤
        if (!TryGetSession(ctx, out var session))
        {
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", default);
            return;
        }
        await HandleViewerAsync(ws, session);
    }
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
async Task HandleViewerAsync(WebSocket ws, ViewerSession session)
{
    var viewerId = Guid.NewGuid().ToString("N")[..8];
    var viewer = new ViewerEntry { Id = viewerId, Socket = ws, AllowedPcIds = session.AllowedPcIds };
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
                if (!CanAccessPc(viewer, newPcId)) continue; // 无权限的 PC 直接忽略
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

                foreach (var p in pcs.Values.Where(p => p.IsOnline && CanAccessPc(viewer, p.Id)))
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
                    && CanAccessPc(viewer, targetPcId)
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
                if (!string.IsNullOrEmpty(targetPcId) && CanAccessPc(viewer, targetPcId)
                    && pcs.TryGetValue(targetPcId, out var remTarget))
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
                if (!string.IsNullOrEmpty(targetPcId) && CanAccessPc(viewer, targetPcId)
                    && pcs.TryGetValue(targetPcId, out var slTarget))
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
    var pcsObj = db.GetObject("pcs") ?? new JsonObject();
    pcsObj[pc.Id] = new JsonObject
    {
        ["name"] = JsonValue.Create(pc.Name),
        ["locked"] = JsonValue.Create(pc.Locked),
        ["remark"] = JsonValue.Create(pc.Remark),
        ["scheduleLock"] = JsonValue.Create(pc.ScheduleLock)
    };
    db.Set("pcs", pcsObj);
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

// 判断某 viewer 是否有权观察指定 PC（AllowedPcIds 为 null 表示所有 PC）
bool CanAccessPc(ViewerEntry viewer, string pcId)
    => viewer.AllowedPcIds == null || viewer.AllowedPcIds.Contains(pcId);

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
    // 合并已知 PC（在线 + 离线），在线排前；按用户权限过滤可观察的 PC
    IEnumerable<PCObject> allowedPcs = pcs.Values;
    if (viewer.AllowedPcIds != null)
        allowedPcs = allowedPcs.Where(p => viewer.AllowedPcIds.Contains(p.Id));
    var list = allowedPcs
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
    public HashSet<string>? AllowedPcIds { get; init; } = null; // null = 可看所有 PC
    public HashSet<string> WatchingPcIds { get; } = new();
    public HashSet<string> ExcludedPcIds { get; } = new();
    public bool IsWatchingAll { get; set; } = false;
    public int Quality { get; set; } = 55;
    public int Fps { get; set; } = 2;
    public SemaphoreSlim SendLock { get; } = new(1, 1);
}

// 登录会话（服务端内存态，重启后需重新登录）
class ViewerSession
{
    public string Username { get; init; } = "";
    public HashSet<string>? AllowedPcIds { get; init; } = null; // null = 所有 PC
}

// data.db 中 users 子对象里每个账号的配置
class UserConfig
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public HashSet<string>? AllowedPcIds { get; init; } = null; // null = 所有 PC
}
