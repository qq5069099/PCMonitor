using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;

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
        // 定时截图上传开关：老数据没有该键 → 默认 false（不上传）
        var screenshotUpload = obj["screenshotUpload"]?.GetValue<bool>() ?? false;
        var screenshotMaxKB = NormalizeShotMaxKB(obj["screenshotMaxKB"]?.GetValue<int>() ?? 300);
        var screenshotIntervalMin = NormalizeShotInterval(obj["screenshotIntervalMin"]?.GetValue<int>() ?? 5);
        var screenshotWorkTimeOnly = obj["screenshotWorkTimeOnly"]?.GetValue<bool>() ?? true;
        pcs[id] = new PCObject { Id = id, Name = name, Locked = locked, Remark = remark, ScheduleLock = scheduleLock, ScreenshotUpload = screenshotUpload, ScreenshotMaxKB = screenshotMaxKB, ScreenshotIntervalMin = screenshotIntervalMin, ScreenshotWorkTimeOnly = screenshotWorkTimeOnly };
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
    // 定时截图：PC 端未登录，查询开关 / 上传截图两个接口对外开放
    if (path is "/api/screenshot/config" or "/api/screenshot/upload") return true;
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

// ─── 定时截图归档：目录与静态访问 ──────────────────────────────
// 磁盘结构: screenshots/{pcId}-{yyyy-MM-dd}/{HHmmss}.jpg（目录/文件名本身即时间排序键）
// 通过 /shots/{目录}/{文件} 访问，位于认证中间件之后 → 必须登录才能看图
var screenshotRoot = Path.Combine(AppContext.BaseDirectory, "screenshots");
Directory.CreateDirectory(screenshotRoot);
var retentionDays = app.Configuration.GetValue("ScreenshotRetentionDays", 14);
const int MaxScreenshotBytes = 4 * 1024 * 1024; // 单张截图上传体积上限（防滥用，非业务限制）
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(screenshotRoot),
    RequestPath = "/shots"
});

// ─── 自动更新：计算客户端 exe 的 MD5 ──────────────────────────
var clientExePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "PCMonitorClient.exe");
// MD5 按文件修改时间缓存：替换 wwwroot 里的客户端 exe 后无需重启服务即可生效
// （直接在启动时算一次的老做法会让新客户端永远发不出去，直到手工重启）
DateTime clientMd5Stamp = DateTime.MinValue;
var clientMd5 = "";
string GetClientMd5()
{
    try
    {
        var fi = new FileInfo(clientExePath);
        if (!fi.Exists) { clientMd5Stamp = DateTime.MinValue; clientMd5 = ""; return clientMd5; }
        if (fi.LastWriteTimeUtc == clientMd5Stamp && clientMd5.Length > 0) return clientMd5;
        clientMd5 = Utility.Md5File(clientExePath);
        clientMd5Stamp = fi.LastWriteTimeUtc;
        Console.WriteLine($"[更新] 客户端 exe 已变更，新 MD5 = {clientMd5}");
        return clientMd5;
    }
    catch { return clientMd5; }
}
app.MapGet("/update/check", (HttpContext ctx) =>
{
    var md5 = GetClientMd5();
    return Results.Json(new
    {
        md5,
        url = string.IsNullOrEmpty(md5) ? "" : $"{ctx.Request.Scheme}://{ctx.Request.Host}/PCMonitorClient.exe"
    });
});

// ─── 定时截图归档：接口 ────────────────────────────────────────
// PC 端未登录，故「查询开关 / 上传截图」两个接口对外开放（见 IsPublicPath）
// 上传接口只接受 data.db 中已存在的 pcId + 4MB 体积上限，避免任意名称在磁盘建目录

app.MapGet("/api/screenshot/config", (HttpContext ctx) =>
{
    var pcId = SanitizePcId(ctx.Request.Query["pcId"].ToString());
    var pc = pcId.Length > 0 && pcs.TryGetValue(pcId, out var found) ? found : null;
    return Results.Json(new
    {
        enabled = pc?.ScreenshotUpload ?? false,
        maxKB = pc?.ScreenshotMaxKB ?? 300,           // 单张截图体积上限（KB）
        intervalMin = pc?.ScreenshotIntervalMin ?? 5, // 上传间隔（分钟）
        workTimeOnly = pc?.ScreenshotWorkTimeOnly ?? true // 是否仅工作时间段内上传
    });
});

app.MapPost("/api/screenshot/upload", async (HttpContext ctx) =>
{
    var pcId = SanitizePcId(ctx.Request.Query["pcId"].ToString());
    if (pcId.Length == 0)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "pcId 非法" });
        return;
    }
    if (!pcs.ContainsKey(pcId))
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "未知 PC" });
        return;
    }
    if (ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value > MaxScreenshotBytes)
    {
        ctx.Response.StatusCode = 413;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "图片过大" });
        return;
    }

    // 手工读取并限制大小（请求体即裸 JPEG 字节）
    using var ms = new MemoryStream();
    var buf = new byte[81920];
    int read;
    while ((read = await ctx.Request.Body.ReadAsync(buf)) > 0)
    {
        if (ms.Length + read > MaxScreenshotBytes)
        {
            ctx.Response.StatusCode = 413;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "图片过大" });
            return;
        }
        ms.Write(buf, 0, read);
    }
    if (ms.Length == 0)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "空请求体" });
        return;
    }

    // 目录/文件名用服务器本地时间，同一秒重复上传只保留最后一张
    var now = DateTime.Now;
    var date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var name = now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".jpg";
    var dir = Path.Combine(screenshotRoot, $"{pcId}-{date}");
    try
    {
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, name), ms.ToArray());
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[截图] 保存失败 {pcId}: {ex.Message}");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "保存失败" });
        return;
    }
    Console.WriteLine($"[截图] {pcId} {date} {name} ({ms.Length} 字节)");
    await ctx.Response.WriteAsJsonAsync(new { ok = true, file = $"{pcId}-{date}/{name}", bytes = ms.Length });
});

// 列出某台 PC 的截图（需登录 + 有该 PC 的观察权限）；date 省略时取最新一天
app.MapGet("/api/screenshots", (HttpContext ctx) =>
{
    if (!TryGetSession(ctx, out var session)) return Results.Json(new { ok = false, error = "未登录" }, statusCode: 401);
    var pcId = SanitizePcId(ctx.Request.Query["pcId"].ToString());
    if (pcId.Length == 0) return Results.Json(new { ok = false, error = "pcId 非法" }, statusCode: 400);
    if (session.AllowedPcIds != null && !session.AllowedPcIds.Contains(pcId))
        return Results.Json(new { ok = false, error = "无权访问" }, statusCode: 403);

    var dates = ListShotDates(pcId);
    var reqDate = ctx.Request.Query["date"].ToString();
    var date = IsShotDate(reqDate) && dates.Contains(reqDate) ? reqDate : (dates.Count > 0 ? dates[0] : "");

    var shots = new List<object>();
    if (date.Length > 0)
    {
        var dir = Path.Combine(screenshotRoot, $"{pcId}-{date}");
        if (Directory.Exists(dir))
        {
            // 正序（按时间从早到晚），页面按时间顺序展示
            foreach (var f in Directory.EnumerateFiles(dir, "*.jpg").OrderBy(f => Path.GetFileName(f)))
            {
                var fi = new FileInfo(f);
                shots.Add(new { name = fi.Name, size = fi.Length, url = $"/shots/{pcId}-{date}/{fi.Name}" });
            }
        }
    }
    return Results.Json(new { ok = true, pcId, dates, date, shots });
});

// 手动删除：带 name 删单张；仅带 date 删当天；都不带则删该机全部
app.MapDelete("/api/screenshots", (HttpContext ctx) =>
{
    if (!TryGetSession(ctx, out var session)) return Results.Json(new { ok = false, error = "未登录" }, statusCode: 401);
    var pcId = SanitizePcId(ctx.Request.Query["pcId"].ToString());
    if (pcId.Length == 0) return Results.Json(new { ok = false, error = "pcId 非法" }, statusCode: 400);
    if (session.AllowedPcIds != null && !session.AllowedPcIds.Contains(pcId))
        return Results.Json(new { ok = false, error = "无权访问" }, statusCode: 403);

    var date = ctx.Request.Query["date"].ToString();
    var name = ctx.Request.Query["name"].ToString();
    var deleted = 0;

    if (IsShotDate(date))
    {
        var dir = Path.Combine(screenshotRoot, $"{pcId}-{date}");
        if (name.Length > 0)
        {
            if (!IsShotName(name)) return Results.Json(new { ok = false, error = "文件名非法" }, statusCode: 400);
            var file = Path.Combine(dir, name);
            if (File.Exists(file)) { File.Delete(file); deleted = 1; }
        }
        else deleted = DeleteShotDir(dir);
    }
    else if (date.Length == 0 && name.Length == 0)
    {
        foreach (var d in ListShotDates(pcId))
            deleted += DeleteShotDir(Path.Combine(screenshotRoot, $"{pcId}-{d}"));
    }
    else return Results.Json(new { ok = false, error = "日期非法" }, statusCode: 400);

    Console.WriteLine($"[截图] 已删除 {pcId} 的 {deleted} 张截图");
    return Results.Json(new { ok = true, deleted });
});

// ─── 定时截图归档：过期清理（保留 ScreenshotRetentionDays 天，默认 14 天）──
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-retentionDays);
            foreach (var dir in Directory.EnumerateDirectories(screenshotRoot))
            {
                var dirName = Path.GetFileName(dir);
                // 目录名必须以 -yyyy-MM-dd 结尾才按保留期判定，不认识的目录一律不动
                if (dirName.Length <= 11 || dirName[^11] != '-') continue;
                var datePart = dirName[^10..];
                if (!DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d)) continue;
                if (d >= cutoff) continue;
                var n = DeleteShotDir(dir);
                Console.WriteLine($"[截图] 清理过期目录 {dirName}（{n} 张）");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[截图] 清理任务异常: {ex.Message}"); }
        await Task.Delay(TimeSpan.FromMinutes(30));
    }
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

                    // 上线（含重连）后同步一次定时截图配置：离线期间改的设置在这里补发
                    await PushShotConfigAsync(pcId);
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
            else if (action == "setScreenshotUpload")
            {
                // 定时截图上传配置：开关（默认关闭）+ 单张体积上限（KB 档位）
                var targetPcId = doc.RootElement.TryGetProperty("pcId", out var suPidProp) ? suPidProp.GetString() : null;
                var enabled = doc.RootElement.TryGetProperty("enabled", out var suProp) && suProp.ValueKind == JsonValueKind.True;
                if (!string.IsNullOrEmpty(targetPcId) && CanAccessPc(viewer, targetPcId)
                    && pcs.TryGetValue(targetPcId, out var suTarget))
                {
                    suTarget.ScreenshotUpload = enabled;
                    if (doc.RootElement.TryGetProperty("maxKB", out var kbProp) && kbProp.TryGetInt32(out var kb))
                        suTarget.ScreenshotMaxKB = NormalizeShotMaxKB(kb);
                    if (doc.RootElement.TryGetProperty("intervalMin", out var ivProp) && ivProp.TryGetInt32(out var iv))
                        suTarget.ScreenshotIntervalMin = NormalizeShotInterval(iv);
                    if (doc.RootElement.TryGetProperty("workTimeOnly", out var wtProp)
                        && (wtProp.ValueKind == JsonValueKind.True || wtProp.ValueKind == JsonValueKind.False))
                        suTarget.ScreenshotWorkTimeOnly = wtProp.GetBoolean();
                    SavePc(suTarget);
                    await BroadcastPcListAsync();
                    // 立刻推给被控 PC，让开关/间隔/档位/时段限制立即生效
                    await PushShotConfigAsync(targetPcId);
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
        ["scheduleLock"] = JsonValue.Create(pc.ScheduleLock),
        ["screenshotUpload"] = JsonValue.Create(pc.ScreenshotUpload),
        ["screenshotMaxKB"] = JsonValue.Create(pc.ScreenshotMaxKB),
        ["screenshotIntervalMin"] = JsonValue.Create(pc.ScreenshotIntervalMin),
        ["screenshotWorkTimeOnly"] = JsonValue.Create(pc.ScreenshotWorkTimeOnly)
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

// ─── 截图归档辅助 ──────────────────────────────────────────────
// 清洗 pcId 作为路径段：只保留字母数字与 . _ -，拒绝空值 / ".." / 纯点，防止路径穿越
string SanitizePcId(string raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return "";
    var sb = new StringBuilder();
    foreach (var ch in raw)
        if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.') sb.Append(ch);
    var s = sb.ToString();
    if (s.Length == 0 || s.Contains("..") || s.Trim('.').Length == 0) return "";
    return s.Length > 64 ? s[..64] : s; // 路径段长度保护
}

bool IsShotDate(string s) => System.Text.RegularExpressions.Regex.IsMatch(s ?? "", @"^\d{4}-\d{2}-\d{2}$");
bool IsShotName(string s) => System.Text.RegularExpressions.Regex.IsMatch(s ?? "", @"^\d{6}\.jpg$");

// 单张截图体积档位（KB），与前端"勾选一项"的选项保持一致；其它值一律回落默认 300
int NormalizeShotMaxKB(int kb) => kb is 100 or 200 or 300 or 500 or 800 ? kb : 300;

// 上传间隔（分钟），与前端下拉框的选项保持一致；其它值一律回落默认 5
int NormalizeShotInterval(int min) => min is >= 1 and <= 6 ? min : 5;

// 列出该 PC 已有截图的日期（倒序，最新在前）
List<string> ListShotDates(string pcId)
{
    var list = new List<string>();
    if (!Directory.Exists(screenshotRoot)) return list;
    var prefix = pcId + "-";
    foreach (var dir in Directory.EnumerateDirectories(screenshotRoot, prefix + "*"))
    {
        var dirName = Path.GetFileName(dir);
        if (!dirName.StartsWith(prefix, StringComparison.Ordinal)) continue;
        var d = dirName[prefix.Length..];
        if (IsShotDate(d)) list.Add(d);
    }
    list.Sort((a, b) => string.CompareOrdinal(b, a));
    return list;
}

// 删除整个日期目录，返回删除的文件数
int DeleteShotDir(string dir)
{
    if (!Directory.Exists(dir)) return 0;
    var n = 0;
    try
    {
        foreach (var f in Directory.EnumerateFiles(dir)) { File.Delete(f); n++; }
        Directory.Delete(dir, true);
    }
    catch (Exception ex) { Console.WriteLine($"[截图] 删除目录失败 {dir}: {ex.Message}"); }
    return n;
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

// 把该机的定时截图配置实时推给 PC —— 改动立即生效；
// PC 离线时这里什么都不做，等它重新注册上线时会在注册流程里再推一次
async Task PushShotConfigAsync(string pcId)
{
    if (!pcs.TryGetValue(pcId, out var pc) || !pc.IsOnline) return;
    await SafeSendTextAsync(pc.Socket!, JsonSerializer.Serialize(new
    {
        command = "screenshot_config",
        enabled = pc.ScreenshotUpload,
        maxKB = pc.ScreenshotMaxKB,
        intervalMin = pc.ScreenshotIntervalMin,
        workTimeOnly = pc.ScreenshotWorkTimeOnly
    }));
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
        .Select(p => new { p.Id, p.Name, p.Remark, Online = p.IsOnline, p.Locked, p.Version, p.ScheduleLock, p.ScreenshotUpload, p.ScreenshotMaxKB, p.ScreenshotIntervalMin, p.ScreenshotWorkTimeOnly })
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
    public bool ScreenshotUpload { get; set; } = false; // 是否每 5 分钟上传定时截图（默认关闭）
    public int ScreenshotMaxKB { get; set; } = 300; // 单张截图体积上限（KB），档位 100/200/300/500/800
    public int ScreenshotIntervalMin { get; set; } = 5; // 上传间隔（分钟），可选 1-6
    public bool ScreenshotWorkTimeOnly { get; set; } = true; // 是否仅在客户端配置的工作时间段内上传（默认是）
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
