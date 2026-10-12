using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class Worker : BackgroundService
{
    // ═══════════════ 配置（直接修改这里）═══════════════
    private const string DefaultServerUrl = "ws://fjwqgame.com:999/ws";
    private const string Version = "v1.0.8.5";

    private const int DefaultFps = 5;
    private const int DefaultJpegQuality = 55;
    private const int MaxWidthPerScreen = 960;

    // 定时截图归档默认参数（可在 appsettings.json 中覆盖）
    private const int DefaultScreenshotMaxWidth = 1600;  // 每屏像素宽（多屏仍拼成一张图）
    private const int DefaultScreenshotQuality = 50;     // JPEG 起始质量
    private const int DefaultScreenshotMaxBytes = 300 * 1024; // 单张（含双屏拼接）不超过 300KB
    private const int DefaultScreenshotIntervalMinutes = 5;
    // ════════════════════════════════════════════════════

    private readonly ILogger<Worker> _log;
    private readonly string _serverUrl;
    private readonly string _employeeUrl;
    private readonly string[] _workPeriods;
    private readonly int _screenshotMaxWidth;
    private readonly int _screenshotQuality;
    private readonly int _screenshotMaxBytes;
    private readonly int _screenshotIntervalMinutes;
    private volatile bool _streaming;
    private volatile int _quality = DefaultJpegQuality;
    private volatile int _interval = 1000 / DefaultFps;
    private volatile int _maxWidth = MaxWidthPerScreen;
    private DateTime _lastControlTime = DateTime.MinValue;
    private int _updating = 0; // 0=空闲 1=更新中

    // 截图上传 / 更新检查共用的 HTTP 客户端（静态复用，避免每次 new）
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public Worker(ILogger<Worker> log, IConfiguration configuration)
    {
        _log = log;
        _serverUrl = configuration["ServerUrl"] ?? DefaultServerUrl;
        _employeeUrl = configuration["EmployeeUrl"] ?? "";
        _workPeriods = configuration.GetSection("WorkPeriods").Get<string[]>() ?? Array.Empty<string>();
        _screenshotMaxWidth = Math.Clamp(configuration.GetValue("ScreenshotMaxWidth", DefaultScreenshotMaxWidth), 320, 3840);
        _screenshotQuality = Math.Clamp(configuration.GetValue("ScreenshotQuality", DefaultScreenshotQuality), 1, 100);
        _screenshotMaxBytes = Math.Clamp(configuration.GetValue("ScreenshotMaxBytes", DefaultScreenshotMaxBytes), 30 * 1024, 4 * 1024 * 1024);
        _screenshotIntervalMinutes = Math.Clamp(configuration.GetValue("ScreenshotIntervalMinutes", DefaultScreenshotIntervalMinutes), 1, 60);

        // 没有 appsettings.json 的部署（例如只放 exe 的目录）WorkPeriods 会为空，
        // 这里明确提示：定时截图会全天上传，离岗告警不会触发
        if (_workPeriods.Length == 0)
            _log.LogWarning("未配置 WorkPeriods：定时截图将全天上传（不受工作时间段限制），离岗告警也不会触发");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 两条长期任务并行：WebSocket 实时推流（含断线重连） + 定时截图归档
        // 归档循环独立于连接状态，断线重连不影响整点上传节奏
        await Task.WhenAll(RunConnectionLoopAsync(ct), RunScreenshotLoopAsync(ct));
    }

    // ─── 连接循环：断线每 5 秒重连 ────────────────────────────
    private async Task RunConnectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RunSessionAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning("连接失败，5 秒后重试: {Msg}", ex.Message);
                try { await Task.Delay(5_000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    // ─── 定时截图归档 ────────────────────────────────────────────
    // 在"分钟数 % 间隔 == 0"的整分时刻触发（间隔 1-6 分钟，默认 5 → :00 :05 :10 …），
    // 仅工作时间段内执行，且需服务器端已为该机开启上传开关（默认关闭）
    private async Task RunScreenshotLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var httpBase = HttpBaseUrl();
                if (httpBase.Length == 0) { await Task.Delay(TimeSpan.FromSeconds(30), ct); continue; }

                // 每轮拉一次配置（推送之外的兜底），拿到最新的间隔
                _shotCfg = await FetchShotConfigAsync(httpBase, ct);

                var wait = DelayToNextWindowStart(_shotCfg.IntervalMin);
                // 等待期间若服务器推来新配置，立刻重算（配置改动立即生效）
                if (wait > TimeSpan.Zero && !await SleepOrConfigChangedAsync(wait, ct)) continue;

                var cfg = _shotCfg;   // 以最新配置为准
                if (!cfg.Enabled || ct.IsCancellationRequested) continue;
                // 服务器可要求"仅工作时间段内上传"（默认要求）
                if (!InShotWindow(cfg)) continue;

                // ① 窗口起点先截图：截图时刻保持整齐（同一台机器每 N 分钟准时一张）
                var jpeg = CaptureShot(cfg);
                if (jpeg == null) continue;

                // ② 拍完**不立即上传**：在窗口内随机等一段时间再传，把多台机器的上传流量打散
                var uploadDelay = RandomUploadDelay(cfg.IntervalMin);
                if (uploadDelay > TimeSpan.Zero && !await SleepOrConfigChangedAsync(uploadDelay, ct))
                {
                    // 等待期间配置有变动：开关被关就丢弃这张，否则按新配置照常上传
                    if (!_shotCfg.Enabled) { _log.LogDebug("等待上传期间开关被关闭，丢弃本张截图"); continue; }
                    cfg = _shotCfg;
                }

                await UploadWithRetryAsync(httpBase, jpeg, cfg, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning("定时截图异常: {Msg}", ex.Message); }
        }
    }

    /// <summary>等待到点；期间收到新的配置推送则提前返回 false，让循环立即重算。</summary>
    private async Task<bool> SleepOrConfigChangedAsync(TimeSpan wait, CancellationToken ct)
    {
        var signal = _shotCfgSignal;
        var delay = Task.Delay(wait, ct);
        var done = await Task.WhenAny(delay, signal.Task);
        if (done == signal.Task) return false;
        await delay;   // 未被打断：正常等待结束（取消时由此抛出）
        return true;
    }

    /// <summary>
    /// 距离下一个间隔窗口**起点**的等待时长：分钟数能被间隔整除的整分（如间隔 5 → :00 :05 :10）。
    /// 截图固定在这个整齐的时刻拍；打散流量交给截图之后的随机上传延时。
    /// 每轮重新计算，免疫系统时间调整。
    /// </summary>
    private static TimeSpan DelayToNextWindowStart(int intervalMin)
    {
        var interval = Math.Clamp(intervalMin, 1, 60);
        var now = DateTime.Now;
        var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(1);
        while (next.Minute % interval != 0) next = next.AddMinutes(1);
        var wait = next - now;
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    /// <summary>
    /// 截图后到上传之间的随机延时：在窗口内随机取一点（两端各留 5 秒，避免压到相邻窗口），
    /// 把多台机器的上传流量打散，避免所有机器在同一秒挤在一起。
    /// </summary>
    private static TimeSpan RandomUploadDelay(int intervalMin)
    {
        var interval = Math.Clamp(intervalMin, 1, 60);
        var windowSec = interval * 60;
        const int margin = 5;
        var latest = Math.Max(margin + 1, windowSec - margin);
        return TimeSpan.FromSeconds(Random.Shared.Next(margin, latest));
    }

    /// <summary>
    /// 是否允许上传截图。服务器勾了"仅工作时间段"才判断时段；
    /// 另外未配置 WorkPeriods 时视为全天允许 —— 否则只放了 exe、
    /// 没有 appsettings.json 的客户端会永远不上传（静默失败）。
    /// </summary>
    private bool InShotWindow(ShotConfig cfg)
        => !cfg.WorkTimeOnly || _workPeriods.Length == 0 || ScreenCapture.IsWorkPeriod(_workPeriods);

    // 服务器为该机下发的截图配置（用类以便整体原子替换）
    private sealed class ShotConfig
    {
        public bool Enabled { get; init; }
        public int MaxKB { get; init; }
        public int IntervalMin { get; init; } = 5;
        public bool WorkTimeOnly { get; init; } = true;
    }

    // 最近一次生效的截图配置：HTTP 拉取或服务器推送都会更新它
    private volatile ShotConfig _shotCfg = new() { IntervalMin = 5, WorkTimeOnly = true };

    // 配置变更信号：用于打断截图循环的等待，让改动立即生效
    private volatile TaskCompletionSource _shotCfgSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ShotConfig ParseShotConfig(JsonElement root) => new()
    {
        Enabled = root.TryGetProperty("enabled", out var ep) && ep.ValueKind == JsonValueKind.True,
        MaxKB = root.TryGetProperty("maxKB", out var kbp) && kbp.TryGetInt32(out var kb) ? kb : 0,
        IntervalMin = root.TryGetProperty("intervalMin", out var ivp) && ivp.TryGetInt32(out var iv) && iv is >= 1 and <= 60
                        ? iv : 5,
        // 老服务端不返回该字段时按"仅工作时间段"处理，保持原有行为
        WorkTimeOnly = !root.TryGetProperty("workTimeOnly", out var wtp) || wtp.ValueKind != JsonValueKind.False
    };

    // 服务器推送新配置：立即替换并唤醒截图循环
    private void ApplyShotConfig(JsonElement root)
    {
        _shotCfg = ParseShotConfig(root);
        var old = _shotCfgSignal;
        _shotCfgSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }

    private async Task<ShotConfig> FetchShotConfigAsync(string httpBase, CancellationToken ct)
    {
        try
        {
            var pcId = Uri.EscapeDataString(Environment.MachineName);
            var json = await _http.GetStringAsync($"{httpBase}/api/screenshot/config?pcId={pcId}", ct);
            using var doc = JsonDocument.Parse(json);
            return ParseShotConfig(doc.RootElement);
        }
        catch (Exception ex)
        {
            // 网络抖动时沿用上次配置，避免被误判成"已关闭"而中断重试
            _log.LogDebug("截图配置查询失败: {Msg}", ex.Message);
            return _shotCfg;
        }
    }

    // 截图：多屏拼接为同一张图 + JPEG 压缩；体积档位同时决定画质起点
    private byte[]? CaptureShot(ShotConfig cfg)
    {
        var maxBytes = cfg.MaxKB > 0 ? Math.Clamp(cfg.MaxKB, 30, 4096) * 1024 : _screenshotMaxBytes;
        var (width, quality) = cfg.MaxKB > 0 ? PresetForKB(cfg.MaxKB) : (_screenshotMaxWidth, _screenshotQuality);
        return ScreenCapture.CaptureSnapshot(width, quality, maxBytes);
    }

    // 上传失败就重试，直到成功为止（退避 10/20/40/60 秒）；期间若开关被关掉则放弃
    private async Task UploadWithRetryAsync(string httpBase, byte[] jpeg, ShotConfig cfg, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (await UploadShotAsync(httpBase, jpeg, ct))
            {
                _log.LogInformation("定时截图已上传（{KB} KB / 上限 {Max} KB / 每 {Min} 分钟{Extra}）",
                    jpeg.Length / 1024, cfg.MaxKB, cfg.IntervalMin,
                    attempt > 0 ? $"，失败 {attempt} 次后成功" : "");
                return;
            }

            attempt++;
            var waitSec = Math.Min(60, 10 * Math.Pow(2, Math.Min(attempt - 1, 3))); // 10/20/40/60…
            _log.LogWarning("定时截图上传失败，{Sec} 秒后进行第 {N} 次重试", (int)waitSec, attempt);
            try { await Task.Delay(TimeSpan.FromSeconds(waitSec), ct); } catch (OperationCanceledException) { return; }

            // 重试前确认开关仍然打开（服务器改配置会实时推送，所以这里读到的一定是最新值）
            if (!_shotCfg.Enabled)
            {
                _log.LogInformation("定时截图开关已关闭，放弃重试");
                return;
            }
        }
    }

    // 单次上传，返回是否成功
    private async Task<bool> UploadShotAsync(string httpBase, byte[] jpeg, CancellationToken ct)
    {
        try
        {
            var pcId = Uri.EscapeDataString(Environment.MachineName);
            using var content = new ByteArrayContent(jpeg);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            using var resp = await _http.PostAsync($"{httpBase}/api/screenshot/upload?pcId={pcId}", content, ct);
            if (resp.IsSuccessStatusCode) return true;
            _log.LogWarning("定时截图上传失败: HTTP {Code}", (int)resp.StatusCode);
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("定时截图上传异常: {Msg}", ex.Message);
            return false;
        }
    }

    // 体积档位（KB）→ (每屏像素宽, JPEG 起始质量)：档位越大分辨率与画质越高；
    // 上限由 CaptureSnapshot 兜底（桌面内容复杂时会自动降质/降分辨率，保证不超）
    private static (int Width, int Quality) PresetForKB(int maxKB) => maxKB switch
    {
        <= 100 => (1280, 50),
        <= 200 => (1600, 55),
        <= 300 => (1920, 60),
        <= 500 => (2560, 65),
        _      => (3840, 75),
    };

    // 由 WebSocket 地址推出 HTTP 基地址（origin），供截图上传与更新检查共用
    private string HttpBaseUrl()
    {
        try
        {
            var httpUrl = _serverUrl.Replace("wss://", "https://").Replace("ws://", "http://");
            var uri = new Uri(httpUrl);
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        }
        catch { return ""; }
    }

    // ─── 单次连接会话 ────────────────────────────────────────
    private async Task RunSessionAsync(CancellationToken ct)
    {
        var pcId = Environment.MachineName;
        var pcName = Environment.MachineName;

        _quality = DefaultJpegQuality;
        _interval = Math.Max(50, 1000 / DefaultFps);

        var uri = new Uri($"{_serverUrl}?role=pc");
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15); // 防止防火墙断开空闲连接
        ws.Options.Proxy = null; // 不使用系统代理，避免 VPN 开关时代理残留导致连接失败

        // 连接超时：20 秒内未能建立连接则放弃，交由外层重试，不永久阻塞
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await ws.ConnectAsync(uri, connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("连接服务器超时（20s）");
        }
        _log.LogInformation("已连接服务器: {Uri}", uri);

        // 发送注册消息
        var reg = JsonSerializer.Serialize(new { type = "register", id = pcId, name = pcName, version = Version });
        await ws.SendAsync(Encoding.UTF8.GetBytes(reg), WebSocketMessageType.Text, true, ct);

        // 异步检查更新，不阻塞连接流程；已在检查中则跳过
        if (Interlocked.CompareExchange(ref _updating, 1, 0) == 0)
            _ = CheckAndApplyUpdateAsync().ContinueWith(_ => Interlocked.Exchange(ref _updating, 0));

        _streaming = false;

        // 后台接收命令（接收循环异常退出时主动取消 linked，唤醒主循环重连）
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var recvTask = ReceiveLoopAsync(ws, linked);

        // 主循环：截图 → 发送
        try
        {
            while (ws.State == WebSocketState.Open && !linked.Token.IsCancellationRequested)
            {
                // 无论是否有观察者，都定期统计空闲时间
                ScreenCapture.UpdateIdleState(_employeeUrl, _workPeriods);

                if (_streaming)
                {
                    bool isControlMode = (DateTime.Now - _lastControlTime).TotalSeconds < 60;
                    var frame = ScreenCapture.Capture(_maxWidth, _quality, isControlMode);
                    if (frame is { Length: > 0 } && ws.State == WebSocketState.Open)
                    {
                        // 帧头：8字节小端序客户端 UTC 毫秒时间戳
                        var tsBytes = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        var payload = new byte[8 + frame.Length];
                        tsBytes.CopyTo(payload, 0);
                        frame.CopyTo(payload, 8);
                        await ws.SendAsync(payload, WebSocketMessageType.Binary, true, linked.Token);
                        _log.LogDebug("已发送帧 {Bytes} 字节", payload.Length);
                    }
                    await Task.Delay(_interval, linked.Token);
                }
                else
                {
                    await Task.Delay(500, linked.Token);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex)
        {
            _log.LogWarning("发送循环异常: {Msg}", ex.Message);
        }
        finally
        {
            linked.Cancel();
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { }
        }

        await recvTask;
    }
    // ─── 自动更新 ──────────────────────────────────────────────────
    private async Task CheckAndApplyUpdateAsync()
    {
        try
        {
            Log($"开始检查更新，当前版本: {Version}，进程路径: {Environment.ProcessPath}");

            var origin = HttpBaseUrl();
            if (origin.Length == 0) { Log("服务器地址无效，跳过更新检查"); return; }
            var checkUrl = $"{origin}/update/check";
            Log($"请求更新检查接口: {checkUrl}");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            string json;
            try
            {
                json = await http.GetStringAsync(checkUrl);
                Log($"服务器返回: {json}");
            }
            catch (Exception ex)
            {
                Log($"请求更新接口失败: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var serverMd5 = doc.RootElement.GetProperty("md5").GetString() ?? "";
            var downloadUrl = doc.RootElement.TryGetProperty("url", out var up) ? up.GetString() ?? "" : "";
            Log($"服务器 MD5: {serverMd5}，下载地址: {downloadUrl}");

            if (string.IsNullOrEmpty(serverMd5) || string.IsNullOrEmpty(downloadUrl))
            {
                Log("服务器返回的 md5 或 url 为空，跳过更新");
                return;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Log("无法获取当前进程路径，跳过更新");
                return;
            }

            var localMd5 = Utility.Md5File(exePath);
            Log($"本地 MD5: {localMd5}，exe 路径: {exePath}");

            if (string.Equals(localMd5, serverMd5, StringComparison.OrdinalIgnoreCase))
            {
                Log("本地版本与服务器一致，无需更新");
                return;
            }

            Log("MD5 不同，需要更新，开始准备下载...");

            // MD5 不同，下载新版并热替换
            var newPath = exePath + ".new";
            Log($"目标临时文件: {newPath}");

            // 若 .new 已存在且 MD5 吻合（上次 bat 替换失败的残留），直接重试应用，跳过下载
            // 避免因反复替换失败导致无限循环下载
            bool needDownload = true;
            if (File.Exists(newPath))
            {
                var existingMd5 = Utility.Md5File(newPath);
                Log($".new 文件已存在，MD5: {existingMd5}");
                if (string.Equals(existingMd5, serverMd5, StringComparison.OrdinalIgnoreCase))
                {
                    Log(".new 文件 MD5 与服务器一致，跳过下载，直接应用");
                    needDownload = false;
                }
                else
                {
                    Log(".new 文件 MD5 不匹配，删除后重新下载");
                    File.Delete(newPath);
                }
            }

            if (needDownload)
            {
                Log($"开始下载新版本: {downloadUrl}");
                try
                {
                    // 流式写入，边下边存
                    using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    Log($"下载响应状态: {(int)response.StatusCode} {response.StatusCode}，Content-Length: {response.Content.Headers.ContentLength?.ToString() ?? "未知"}");
                    response.EnsureSuccessStatusCode();
                    using var contentStream = await response.Content.ReadAsStreamAsync();
                    using (var fileStream = new FileStream(newPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await contentStream.CopyToAsync(fileStream);
                    }
                    var downloadedSize = new FileInfo(newPath).Length;
                    Log($"下载完成，写入字节数: {downloadedSize}");
                }
                catch (Exception ex)
                {
                    Log($"下载失败: {ex.GetType().Name}: {ex.Message}");
                    if (File.Exists(newPath)) File.Delete(newPath);
                    return;
                }

                // 校验下载文件的 MD5，不一致则删除并放弃本次更新
                var downloadedMd5 = Utility.Md5File(newPath);
                Log($"下载文件 MD5 校验: 期望={serverMd5}，实际={downloadedMd5}");
                if (!string.Equals(downloadedMd5, serverMd5, StringComparison.OrdinalIgnoreCase))
                {
                    Log("MD5 校验不通过，删除下载文件，放弃本次更新");
                    File.Delete(newPath);
                    return;
                }
                Log("MD5 校验通过");
            }

            // 启动批处理：等待 3 秒（确保原进程完全退出释放文件锁），
            // 用 move 重命名而非 copy 覆盖（更可靠）：先把旧 exe 改为 .old，再把 .new 改为 exe；
            // 若 move 失败则回滚，保留 .new 以便下次直接重试，不再重复下载。
            var oldPath = exePath + ".old";
            var bat = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bat");
            Log($"准备启动更新批处理，bat 路径: {bat}，旧版备份: {oldPath}");

            var batContent =
                "@echo off\r\n" +
                "timeout /t 3 /nobreak >nul\r\n" +
                $"del \"{oldPath}\" 2>nul\r\n" +
                $"move /y \"{exePath}\" \"{oldPath}\"\r\n" +
                "if errorlevel 1 goto :eof\r\n" +
                $"move /y \"{newPath}\" \"{exePath}\"\r\n" +
                "if errorlevel 1 (\r\n" +
                $"  move /y \"{oldPath}\" \"{exePath}\"\r\n" +
                "  goto :eof\r\n" +
                ")\r\n" +
                $"start \"\" \"{exePath}\"\r\n" +
                "timeout /t 3 /nobreak >nul\r\n" +
                $"del \"{oldPath}\" 2>nul\r\n" +
                "del \"%~f0\"\r\n";
            await File.WriteAllTextAsync(bat, batContent, System.Text.Encoding.Default);
            Log("批处理文件已写入，即将启动并退出当前进程...");

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            Log("批处理已启动，进程即将退出");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log($"更新过程发生未处理异常: {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}");
        }
    }

    private static void Log(string msg)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "update.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [更新] {msg}{Environment.NewLine}";
            File.AppendAllText(logPath, line, Encoding.UTF8);
        }
        catch { }
    }

    // ─── 接收服务器命令 ──────────────────────────────────────
    // 注意：接收循环退出（无论正常/异常）时取消 cts，确保主循环感知断开并重连
    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var buf = new byte[65536];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;

                var msg = Encoding.UTF8.GetString(buf, 0, result.Count);
                using var doc = JsonDocument.Parse(msg);
                var cmd = doc.RootElement.GetProperty("command").GetString();
                if (cmd == "start")
                {
                    if (doc.RootElement.TryGetProperty("quality", out var qp))
                        _quality = Math.Clamp(qp.GetInt32(), 1, 100);
                    if (doc.RootElement.TryGetProperty("fps", out var fp))
                        _interval = Math.Max(200, 1000 / Math.Max(1, fp.GetInt32()));
                    // 按画质档位动态调整截图宽度（每屏）：省流→800，均衡→1280，高清→1920，超清→2048，极清→2560
                    // 高清(78%) 起步即 1920px 原生 1080p，不再降采样；极清支持 2K 原生分辨率
                    _maxWidth = _quality <= 50 ? 800 : _quality <= 70 ? 1280 : _quality <= 83 ? 1920 : _quality < 100 ? 2048 : 2560;
                    _streaming = true;
                    _log.LogInformation("收到指令: {Cmd}，quality={Q} interval={I}ms", cmd, _quality, _interval);
                }
                else if (cmd == "control")
                {
                    var subCmd = doc.RootElement.TryGetProperty("cmd", out var subP) ? subP.GetString() : "";
                    _log.LogInformation("收到控制指令: {Sub}", subCmd);
                    _lastControlTime = DateTime.Now; // 控制模式：60 秒内绘制普通光标
                    switch (subCmd)
                    {
                        case "lock_input": NativeSessionHelper.BlockInput(true); break;
                        case "unlock_input": NativeSessionHelper.BlockInput(false); break;
                        case "lock_screen": NativeSessionHelper.LockWorkStation(); break;
                        case "restart": NativeSessionHelper.ForceRestart(); break;
                        case "shutdown": NativeSessionHelper.ForceShutdown(); break;
                        case "mouse_input":
                            {
                                double nx = doc.RootElement.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0.5;
                                double ny = doc.RootElement.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0.5;
                                int type = doc.RootElement.TryGetProperty("type", out var tp) ? tp.GetInt32() : 0;
                                var mods = doc.RootElement.TryGetProperty("mods", out var mp)
                                              ? mp.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                                              : Array.Empty<string>();
                                NativeSessionHelper.SendMouseClick(nx, ny, type, mods, _maxWidth);
                                break;
                            }
                        case "mouse_scroll":
                            {
                                int delta = doc.RootElement.TryGetProperty("delta", out var dp) ? dp.GetInt32() : 3;
                                NativeSessionHelper.SendMouseScroll(delta);
                                break;
                            }
                        case "mouse_down":
                            {
                                double nx = doc.RootElement.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0.5;
                                double ny = doc.RootElement.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0.5;
                                int button = doc.RootElement.TryGetProperty("button", out var bp) ? bp.GetInt32() : 0;
                                NativeSessionHelper.SendMouseButtonDown(nx, ny, button, _maxWidth);
                                break;
                            }
                        case "mouse_move":
                            {
                                double nx = doc.RootElement.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0.5;
                                double ny = doc.RootElement.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0.5;
                                NativeSessionHelper.SendMouseMove(nx, ny, _maxWidth);
                                break;
                            }
                        case "mouse_up":
                            {
                                double nx = doc.RootElement.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0.5;
                                double ny = doc.RootElement.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0.5;
                                int button = doc.RootElement.TryGetProperty("button", out var bp) ? bp.GetInt32() : 0;
                                NativeSessionHelper.SendMouseButtonUp(nx, ny, button, _maxWidth);
                                break;
                            }
                        case "key_input":
                            {
                                string key = doc.RootElement.TryGetProperty("key", out var kp) ? kp.GetString() ?? "" : "";
                                var mods = doc.RootElement.TryGetProperty("mods", out var mp)
                                             ? mp.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                                             : Array.Empty<string>();
                                NativeSessionHelper.SendKeyInput(key, mods);
                                break;
                            }
                        case "text_input":
                            {
                                string text = doc.RootElement.TryGetProperty("text", out var tp) ? tp.GetString() ?? "" : "";
                                NativeSessionHelper.SendTextInput(text);
                                break;
                            }
                    }
                }
                else if (cmd == "screenshot_config")
                {
                    // 服务器一改配置就下发，这里立即生效（打断当前等待，按新配置重算下一次上传时刻）
                    ApplyShotConfig(doc.RootElement);
                    _log.LogInformation("截图配置已更新: 开关={On} 间隔={Min}分钟 上限={KB}KB 仅工作时间段={Wt}",
                        _shotCfg.Enabled, _shotCfg.IntervalMin, _shotCfg.MaxKB, _shotCfg.WorkTimeOnly);
                }
                else
                {
                    _streaming = false;
                    _log.LogInformation("收到指令: {Cmd}", cmd);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning("接收循环异常: {Msg}", ex.Message); }
        finally
        {
            // 无论何种原因退出接收循环，都取消 linked，唤醒主循环重新连接
            cts.Cancel();
        }
    }
}
