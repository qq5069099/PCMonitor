using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

/// <summary>
/// 屏幕截图工具：支持多显示器，拼接为一张水平图，JPEG 压缩。
/// </summary>
static class ScreenCapture
{
    // 缓存 JPEG 编码器，避免每帧查找
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    // 光标空闲时间追踪
    private static Point _lastCursorPos = Point.Empty;
    private static DateTime _lastMoveTime = DateTime.Now;

    // 反作弊：近期真实移动时间戳（60秒滑动窗口），防止自动抖鼠标工具欺骗空闲检测
    private static bool _isWorking = false;// 当前是否上班时间
    private static readonly List<DateTime> _moveLog = new();

    /// <summary>
    /// 定时调用，持续统计空闲时间（即使无截图请求时也能统计）。
    /// </summary>
    public static void UpdateIdleState(string employeeUrl, string[] workPeriods)
    {
        var cursorPos = Cursor.Position;
        bool isWorking = InWorkTime(workPeriods);

        // 首次调用：初始化参考位置和工作状态
        if (_lastCursorPos == Point.Empty)
        {
            _lastCursorPos = cursorPos;
            _isWorking = isWorking;
            return;
        }

        // 每次调用都检测工作时间段是否切换（下班/上班边界立即重置，不依赖鼠标移动）
        if (_isWorking != isWorking)
        {
            // 切换到下班：若工作段内有超过 6 分钟的空闲，补发一次警告再重置
            if (!isWorking) TrySendWarning(employeeUrl, _lastMoveTime, DateTime.Now);
            _isWorking = isWorking;
            _lastMoveTime = DateTime.Now;
            _moveLog.Clear();
            return;
        }

        int dx = cursorPos.X - _lastCursorPos.X;
        int dy = cursorPos.Y - _lastCursorPos.Y;
        if (dx * dx > 900 || dy * dy > 900) // 移动距离 > 30px 才算真实移动
        {
            _lastCursorPos = cursorPos;  // 始终更新位置参考点，避免重复触发同一位移

            var now = DateTime.Now;
            _moveLog.RemoveAll(t => (now - t).TotalSeconds > 60);                    // 清理 60 秒前的旧记录
            if (!_moveLog.Any(t => (now - t).TotalSeconds < 1)) _moveLog.Add(now);   // 同一秒内已有记录则跳过，避免重复计次

            // 反作弊：60 秒内少于 6 次真实移动，视为自动抖鼠标 / 单次晃动欺骗，
            // 不重置空闲计时，也不发警告（_lastMoveTime 保持不变，空闲继续累积）
            if (_moveLog.Count < 6) return;

            // 上班时间有真实移动，且空闲时间超过 6 分钟，发警告后重置空闲计时
            if (isWorking) TrySendWarning(employeeUrl, _lastMoveTime, DateTime.Now);

            _lastMoveTime = now;
        }
    }

    // 判断当前是否在配置的工作时间段内
    private static bool InWorkTime(string[] workPeriods)
    {
        if (DateTime.Now.DayOfWeek == DayOfWeek.Sunday) return false;       // 周日全天不发送警告
        if (workPeriods == null || workPeriods.Length == 0) return false;   // 不配置则不发送警告

        var now = DateTime.Now.TimeOfDay;
        foreach (var period in workPeriods)
        {
            var parts = period.Split('~');
            if (parts.Length == 2 &&
                TimeSpan.TryParse(parts[0], out var start) &&
                TimeSpan.TryParse(parts[1], out var end))
            {
                if (start <= now && now <= end) return true;
            }
        }

        return false;
    }

    private static void TrySendWarning(string employeeUrl, DateTime idleStart, DateTime idleEnd)
    {
        if (string.IsNullOrEmpty(employeeUrl)) return;
        var effective = idleEnd - idleStart;
        if (effective.TotalSeconds < 60 * 6) return;
        
        string durationText = effective.TotalHours >= 1
            ? $"{(int)effective.TotalHours}小时{effective.Minutes}分钟"
            : $"{effective.TotalMinutes:F1}分钟";
        string prefix = effective.TotalMinutes switch
        {
            < 10 => $"[⚠ 离岗提醒（{durationText}）]",
            < 20 => $"[⚠⚠ 离岗预警（{durationText}）]",
            < 30 => $"[⚠⚠⚠ 违规离岗（{durationText}）]",
            < 45 => $"[⚠⚠⚠ 违规离岗（{durationText}）]",
            < 60 => $"[⚠⚠⚠⚠ 违规离岗（{durationText}）]",
            _    => $"[⚠⚠⚠⚠⚠ 严重违规离岗（{durationText}）]"
        };
        string suffix = effective.TotalMinutes switch
        {
            < 10 => " 请立即返回工位。持续离岗将触发上报流程。",
            < 20 => " 本次记录已留存备查。请立即回到岗位，否则将通知主管。",
            < 30 => " 严重违反在岗规定。系统已记录本次违规，请立即恢复工作。",
            < 45 => " 已达违规旷工标准。主管将收到自动提醒，请立即返回并主动说明情况。",
            < 60 => " 本次旷工记录将直接计入考核扣分项，请立即联系主管报备。",
            _ => " 系统已自动上报至管理层，后果严重，请立即联系主管说明原因。"
        };
        string content = $"{prefix} {DateTime.Now:yyyy-MM-dd} {idleStart:HH:mm:ss}~{idleEnd:HH:mm:ss},{suffix}";
        _ = PostEmployeeWarningAsync(employeeUrl, content);
    }

    private static async Task PostEmployeeWarningAsync(string employeeUrl, string content)
    {
        var uri = new Uri(employeeUrl);
        var apiBase = $"{uri.Scheme}://{uri.Authority}";
        var employee = "";
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "user") { employee = Uri.UnescapeDataString(kv[1]); break; }
        }
        if (string.IsNullOrEmpty(employee)) return;

        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            employee,
            day = "current",
            type = "warning",
            content,
            @operator = employee
        });
        using var http = new HttpClient();
        using var sc = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        await http.PostAsync($"{apiBase}/api/Plan/item/add", sc);
    }

    /// <summary>
    /// 截取所有屏幕并拼接为单张 JPEG 字节数组。
    /// </summary>
    /// <param name="maxWidthPerScreen">每个屏幕的最大宽度（像素），超出则等比缩放。</param>
    /// <param name="quality">JPEG 质量 1-100，值越小体积越小。</param>
    public static byte[]? Capture(int maxWidthPerScreen, int quality, bool isControlMode = false)
    {
        try
        {
            // 按 X 坐标从左到右排序所有显示器
            var screens = Screen.AllScreens.OrderBy(s => s.Bounds.X).ToArray();

            // 计算缩放比例（以所有屏幕中最宽的为基准）
            int maxNativeW = screens.Max(s => s.Bounds.Width);
            float scale = maxWidthPerScreen < maxNativeW
                           ? (float)maxWidthPerScreen / maxNativeW
                           : 1.0f;

            // 计算拼接后的画布尺寸
            int totalW = (int)Math.Round(screens.Sum(s => s.Bounds.Width * scale));
            int totalH = (int)Math.Round(screens.Max(s => s.Bounds.Height * scale));

            using var canvas = new Bitmap(totalW, totalH, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(canvas);
            g.Clear(Color.Black);
            // 速度优先，不需要高质量缩放
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

            int xOffset = 0;
            foreach (var screen in screens)
            {
                var b = screen.Bounds;
                using var shot = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
                using var sg = Graphics.FromImage(shot);
                sg.CopyFromScreen(b.Location, Point.Empty, b.Size,
                                  CopyPixelOperation.SourceCopy);

                int sw = (int)Math.Round(b.Width * scale);
                int sh = (int)Math.Round(b.Height * scale);
                g.DrawImage(shot, xOffset, 0, sw, sh);
                xOffset += sw;
            }

            // 在拼接图上标注鼠标光标位置
            DrawCursorHighlight(g, screens, scale, isControlMode);

            using var ms = new MemoryStream();
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            canvas.Save(ms, JpegCodec, ep);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 在拼接画布上高亮标注当前鼠标光标位置：黑色阴影描边 + 黄色圆圈 + 断开式十字线 + 中心填充圆。
    /// 固定像素尺寸，确保在全屏监控缩略图下仍清晰可见。
    /// </summary>
    private static void DrawCursorHighlight(Graphics g, Screen[] screens, float scale, bool isControlMode)
    {
        var cursorPos = Cursor.Position;

        int xOff = 0;
        foreach (var screen in screens)
        {
            if (screen.Bounds.Contains(cursorPos))
            {
                float cx = xOff + (cursorPos.X - screen.Bounds.X) * scale;
                float cy = (cursorPos.Y - screen.Bounds.Y) * scale;

                // 临时开启抗锯齿，绘完后恢复
                var prevSmooth = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                // 远程控制模式：60 秒内绘制标准箭头光标，不绘制自定义高亮
                if (isControlMode)
                {
                    DrawArrowCursor(g, cx, cy);
                    g.SmoothingMode = prevSmooth;
                    break;
                }

                const float R = 36f;  // 外圆半径（像素，图像坐标）
                const float r = 10f;  // 中心填充圆半径
                const float crossExt = 56f;  // 十字线从圆心到末端距离

                // ① 黑色阴影（让高亮在亮色/暗色背景均可见）
                using var shadowPen = new Pen(Color.FromArgb(210, 0, 0, 0), 9f);
                shadowPen.StartCap = shadowPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawEllipse(shadowPen, cx - R, cy - R, R * 2, R * 2);
                g.DrawLine(shadowPen, cx - crossExt, cy, cx + crossExt, cy);
                g.DrawLine(shadowPen, cx, cy - crossExt, cx, cy + crossExt);

                // ② 亮黄色外圆
                using var ringPen = new Pen(Color.FromArgb(255, 255, 224, 0), 5f);
                ringPen.StartCap = ringPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawEllipse(ringPen, cx - R, cy - R, R * 2, R * 2);

                // ③ 断开式十字线（圆外部分，使圆心区域干净）
                using var crossPen = new Pen(Color.FromArgb(255, 255, 224, 0), 4f);
                crossPen.StartCap = crossPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(crossPen, cx - crossExt, cy, cx - R, cy);
                g.DrawLine(crossPen, cx + R, cy, cx + crossExt, cy);
                g.DrawLine(crossPen, cx, cy - crossExt, cx, cy - R);
                g.DrawLine(crossPen, cx, cy + R, cx, cy + crossExt);

                // ④ 半透明黄色中心圆（精确指示光标位置）
                using var dotBrush = new SolidBrush(Color.FromArgb(200, 255, 224, 0));
                g.FillEllipse(dotBrush, cx - r, cy - r, r * 2, r * 2);
                using var dotPen = new Pen(Color.FromArgb(220, 0, 0, 0), 2.4f);
                g.DrawEllipse(dotPen, cx - r, cy - r, r * 2, r * 2);

                // ⑤ 空闲时间标签（超过60秒未移动显示时间）
                var idle = DateTime.Now - _lastMoveTime;
                if (idle.TotalSeconds >= 60)
                {
                    string idleText = idle.TotalHours >= 1
                        ? $"{(int)idle.TotalHours:D2}:{idle.Minutes:D2}:{idle.Seconds:D2}"
                        : $"{idle.Minutes:D2}:{idle.Seconds:D2}";
                    DrawIdleLabel(g, cx, cy, crossExt, idleText, g.VisibleClipBounds.Height);
                }

                g.SmoothingMode = prevSmooth;
                break;
            }
            xOff += (int)Math.Round(screen.Bounds.Width * scale);
        }
    }

    /// <summary>
    /// 绘制标准箭头光标形状（远程控制模式下替代自定义高亮）。
    /// </summary>
    private static void DrawArrowCursor(Graphics g, float cx, float cy)
    {
        // 标准箭头光标轮廓（相对于热点原点）
        PointF[] arrow =
        [
            new(cx,        cy),
            new(cx,        cy + 13.3f),
            new(cx + 2.7f, cy + 9.3f),
            new(cx + 4.7f, cy + 14f),
            new(cx + 6.7f, cy + 13f),
            new(cx + 4.7f, cy + 8.3f),
            new(cx + 8f,   cy + 8.3f),
        ];
        // 白色填充 + 黑色描边，模拟系统默认光标外观
        using var fill = new SolidBrush(Color.White);
        g.FillPolygon(fill, arrow);
        using var outline = new Pen(Color.Black, 1.2f);
        g.DrawPolygon(outline, arrow);
    }

    /// <summary>
    /// 在光标正下方绘制空闲时长标签：深靛蓝底板 + 亮青绿边框 + 亮黄文字。
    /// 若底部越界则翻转到光标上方显示。
    /// </summary>
    private static void DrawIdleLabel(Graphics g, float cx, float cy, float crossExt, string text, float imgHeight)
    {
        using var font = new Font("Courier New", 96f, FontStyle.Bold, GraphicsUnit.Pixel);
        SizeF sz = g.MeasureString(text, font);
        float pw = sz.Width + 28f;
        float ph = sz.Height + 12f;
        float px = cx - pw / 2f;
        float py = cy + crossExt + 10f;  // 默认：光标下方

        // 超出图像底部则翻转到光标上方
        if (py + ph > imgHeight - 2f)
            py = cy - crossExt - ph - 10f;

        // 背景：深靛蓝，半透明 0.7（alpha ≈ 178）
        using var bgBrush = new SolidBrush(Color.FromArgb(178, 12, 12, 24));
        g.FillRectangle(bgBrush, px, py, pw, ph);

        // 边框：亮青绿色
        using var borderPen = new Pen(Color.FromArgb(255, 0, 228, 168), 3f);
        g.DrawRectangle(borderPen, px, py, pw, ph);

        // 文字：亮黄色，GenericTypographic 消除 GDI+ 内部留白，居中对齐
        using var textBrush = new SolidBrush(Color.FromArgb(255, 255, 230, 0));
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(text, font, textBrush, new RectangleF(px, py, pw, ph + 2), sf);
    }
}
