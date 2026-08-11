using System.Runtime.InteropServices;
using System.Windows.Forms;
using static System.Net.Mime.MediaTypeNames;

/// <summary>
/// 辅助类：注册表自启动 / 输入锁定 / 锁屏 / 关机重启 / 远程鼠标键盘注入
/// </summary>
static class NativeSessionHelper
{
    // ── 鼠标位置锁定 / 锁屏 / 强制关机 ──────────────────────────────────

    private static System.Threading.Timer? _clipTimer;

    public static void BlockInput(bool block)
    {
        if (block)
        {
            _clipTimer ??= new System.Threading.Timer(_ =>
            {
                GetCursorPos(out var pt);
                var rect = new RECT { Left = pt.X, Top = pt.Y, Right = pt.X + 1, Bottom = pt.Y + 1 };
                ClipCursor(ref rect);
                SendKeyInput("n", null);
                SendMouseScroll(1);
            }, null, 0, 200);
        }
        else
        {
            _clipTimer?.Dispose();
            _clipTimer = null;
            ClipCursor(IntPtr.Zero);
        }
    }

    public static void LockWorkStation() => NativeLockWorkStation();

    public static void ForceRestart()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "shutdown", "/r /f /t 0") { UseShellExecute = false, CreateNoWindow = true });
    }

    public static void ForceShutdown()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "shutdown", "/s /f /t 0") { UseShellExecute = false, CreateNoWindow = true });
    }

    // ── 远程鼠标输入 ──────────────────────────────────────────────────

    /// <summary>
    /// 发送鼠标点击。nx/ny 为 JPEG 归一化坐标 [0,1]，maxWidth 为截图时的 _maxWidth 值。
    /// clickType: 0=左键单击, 1=左键双击, 2=右键单击, 3=中键单击
    /// </summary>
    public static void SendMouseClick(double nx, double ny, int clickType, string[] mods, int maxWidth)
    {
        var (ax, ay) = NormalizedToScreen(nx, ny, maxWidth);
        var modVks = GetModVKeys(mods);
        foreach (var vk in modVks) PressKey(vk, false);
        MoveMouseAbsolute(ax, ay);
        System.Threading.Thread.Sleep(20);
        switch (clickType)
        {
            case 0: ClickButton(ax, ay, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP); break;
            case 1:
                ClickButton(ax, ay, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
                System.Threading.Thread.Sleep(60);
                ClickButton(ax, ay, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
                break;
            case 2: ClickButton(ax, ay, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP); break;
            case 3: ClickButton(ax, ay, MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP); break;
        }
        for (int i = modVks.Length - 1; i >= 0; i--) PressKey(modVks[i], true);
    }

    /// <summary>仅移动鼠标，不点击。</summary>
    public static void SendMouseMove(double nx, double ny, int maxWidth)
    {
        var (ax, ay) = NormalizedToScreen(nx, ny, maxWidth);
        MoveMouseAbsolute(ax, ay);
    }

    /// <summary>按下鼠标按键（不抬起），用于拖拽开始。button: 0=左键,2=右键,3=中键</summary>
    public static void SendMouseButtonDown(double nx, double ny, int button, int maxWidth)
    {
        var (ax, ay) = NormalizedToScreen(nx, ny, maxWidth);
        MoveMouseAbsolute(ax, ay);
        System.Threading.Thread.Sleep(20);
        uint flag = button == 2 ? MOUSEEVENTF_RIGHTDOWN : button == 3 ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_LEFTDOWN;
        var (absX, absY) = ToAbsolute(ax, ay);
        var inp = new INPUT { type = INPUT_MOUSE };
        inp.U.mi.dx = absX; inp.U.mi.dy = absY;
        inp.U.mi.dwFlags = flag | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>抬起鼠标按键，用于拖拽结束。button: 0=左键,2=右键,3=中键</summary>
    public static void SendMouseButtonUp(double nx, double ny, int button, int maxWidth)
    {
        var (ax, ay) = NormalizedToScreen(nx, ny, maxWidth);
        MoveMouseAbsolute(ax, ay);
        uint flag = button == 2 ? MOUSEEVENTF_RIGHTUP : button == 3 ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_LEFTUP;
        var (absX, absY) = ToAbsolute(ax, ay);
        var inp = new INPUT { type = INPUT_MOUSE };
        inp.U.mi.dx = absX; inp.U.mi.dy = absY;
        inp.U.mi.dwFlags = flag | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>发送滚轮事件。delta 正数=向上滚，负数=向下滚。不移动光标位置。</summary>
    public static void SendMouseScroll(int delta)
    {
        var inp = new INPUT { type = INPUT_MOUSE };
        inp.U.mi.mouseData = (uint)(delta * 120); // WHEEL_DELTA = 120
        inp.U.mi.dwFlags = MOUSEEVENTF_WHEEL;
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 发送键盘按键。key 为键名字符串（见 VkMap），mods 为修饰键数组。
    /// </summary>
    public static void SendKeyInput(string key, string[]? mods)
    {
        if (string.IsNullOrEmpty(key)) return;
        var modVks = GetModVKeys(mods);
        foreach (var vk in modVks) PressKey(vk, false);
        if (VkMap.TryGetValue(key, out ushort vkCode) && vkCode != 0)
        {
            PressKey(vkCode, false);
            System.Threading.Thread.Sleep(10);
            PressKey(vkCode, true);
        }
        for (int i = modVks.Length - 1; i >= 0; i--) PressKey(modVks[i], true);
    }

    /// <summary>
    /// 用 KEYEVENTF_UNICODE 逐字发送文本，支持中英文及所有 Unicode 字符（BMP 范围）。
    /// </summary>
    public static void SendTextInput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char ch in text)
        {
            var down = new INPUT { type = INPUT_KEYBOARD };
            down.U.ki.wScan = (ushort)ch;
            down.U.ki.dwFlags = KEYEVENTF_UNICODE;
            inputs.Add(down);

            var up = new INPUT { type = INPUT_KEYBOARD };
            up.U.ki.wScan = (ushort)ch;
            up.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            inputs.Add(up);
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    // ── 坐标转换 ─────────────────────────────────────────────────────

    /// <summary>
    /// 将 JPEG 归一化坐标 (nx, ny) 转换为实际屏幕绝对坐标，
    /// 使用与 ScreenCapture.Capture 相同的 scale 计算逻辑。
    /// </summary>
    static (int x, int y) NormalizedToScreen(double nx, double ny, int maxWidth)
    {
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.X).ToArray();
        int maxNativeW = screens.Max(s => s.Bounds.Width);
        float scale = maxWidth < maxNativeW ? (float)maxWidth / maxNativeW : 1.0f;

        double totalW = screens.Sum(s => s.Bounds.Width * scale);
        double totalH = screens.Max(s => s.Bounds.Height * scale);

        double jx = nx * totalW;
        double jy = ny * totalH;

        double xOff = 0;
        foreach (var screen in screens)
        {
            double sw = screen.Bounds.Width * scale;
            if (jx <= xOff + sw)
            {
                double localX = (jx - xOff) / scale;
                double localY = jy / scale;
                int ax = screen.Bounds.X + (int)Math.Round(Math.Clamp(localX, 0, screen.Bounds.Width - 1));
                int ay = screen.Bounds.Y + (int)Math.Round(Math.Clamp(localY, 0, screen.Bounds.Height - 1));
                return (ax, ay);
            }
            xOff += sw;
        }
        var last = screens.Last();
        return (last.Bounds.Right - 1, last.Bounds.Bottom - 1);
    }

    /// <summary>
    /// 将屏幕绝对坐标转换为 SendInput 所需的虚拟桌面归一化坐标 (0-65535)。
    /// 使用 VIRTUALDESK flag 支持多显示器扩展桌面。
    /// </summary>
    static (int absX, int absY) ToAbsolute(int x, int y)
    {
        int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vTop  = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vW    = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vH    = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return (
            (int)Math.Round((x - vLeft) * 65535.0 / vW),
            (int)Math.Round((y - vTop)  * 65535.0 / vH)
        );
    }

    static void MoveMouseAbsolute(int x, int y)
    {
        var (absX, absY) = ToAbsolute(x, y);
        var inp = new INPUT { type = INPUT_MOUSE };
        inp.U.mi.dx = absX; inp.U.mi.dy = absY;
        inp.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    static void ClickButton(int x, int y, uint downFlag, uint upFlag)
    {
        var (absX, absY) = ToAbsolute(x, y);
        var down = new INPUT { type = INPUT_MOUSE };
        down.U.mi.dx = absX; down.U.mi.dy = absY;
        down.U.mi.dwFlags = downFlag | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        var up = new INPUT { type = INPUT_MOUSE };
        up.U.mi.dx = absX; up.U.mi.dy = absY;
        up.U.mi.dwFlags = upFlag | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    static void PressKey(ushort vk, bool keyUp)
    {
        var inp = new INPUT { type = INPUT_KEYBOARD };
        inp.U.ki.wVk = vk;
        inp.U.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u;
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    static ushort[] GetModVKeys(string[]? mods)
    {
        if (mods == null || mods.Length == 0) return Array.Empty<ushort>();
        var list = new List<ushort>(mods.Length);
        foreach (var m in mods)
            switch (m.ToLower())
            {
                case "ctrl":  list.Add(VK_LCONTROL); break;
                case "alt":   list.Add(VK_LMENU);    break;
                case "shift": list.Add(VK_LSHIFT);   break;
                case "win":   list.Add(VK_LWIN);     break;
            }
        return list.ToArray();
    }

    // ── P/Invoke 常量 ────────────────────────────────────────────────

    const int  INPUT_MOUSE    = 0;
    const int  INPUT_KEYBOARD = 1;

    const uint MOUSEEVENTF_MOVE        = 0x0001;
    const uint MOUSEEVENTF_LEFTDOWN    = 0x0002;
    const uint MOUSEEVENTF_LEFTUP      = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN   = 0x0008;
    const uint MOUSEEVENTF_RIGHTUP     = 0x0010;
    const uint MOUSEEVENTF_MIDDLEDOWN  = 0x0020;
    const uint MOUSEEVENTF_MIDDLEUP    = 0x0040;
    const uint MOUSEEVENTF_WHEEL       = 0x0800;
    const uint MOUSEEVENTF_ABSOLUTE    = 0x8000;
    const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    const uint KEYEVENTF_KEYUP   = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;

    const ushort VK_LSHIFT   = 0xA0;
    const ushort VK_LCONTROL = 0xA2;
    const ushort VK_LMENU    = 0xA4;
    const ushort VK_LWIN     = 0x5B;

    const int SM_XVIRTUALSCREEN  = 76;
    const int SM_YVIRTUALSCREEN  = 77;
    const int SM_CXVIRTUALSCREEN = 78;
    const int SM_CYVIRTUALSCREEN = 79;

    // ── 虚拟键码表 ───────────────────────────────────────────────────

    static readonly Dictionary<string, ushort> VkMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // 基本控制键
        ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["tab"] = 0x09,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["numenter"] = 0x0D,
        ["back"] = 0x08, ["backspace"] = 0x08,
        ["del"] = 0x2E, ["delete"] = 0x2E,
        ["ins"] = 0x2D, ["insert"] = 0x2D,
        ["space"] = 0x20,
        ["caps"] = 0x14, ["capslock"] = 0x14,
        ["numlock"] = 0x90,
        ["scrolllock"] = 0x91,
        ["pause"] = 0x13,
        ["prtsc"] = 0x2C, ["printscreen"] = 0x2C,
        // 方向键
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["home"] = 0x24, ["end"] = 0x23,
        ["pgup"] = 0x21, ["pageup"] = 0x21,
        ["pgdn"] = 0x22, ["pgdown"] = 0x22, ["pagedown"] = 0x22,
        // 功能键
        ["f1"]  = 0x70, ["f2"]  = 0x71, ["f3"]  = 0x72, ["f4"]  = 0x73,
        ["f5"]  = 0x74, ["f6"]  = 0x75, ["f7"]  = 0x76, ["f8"]  = 0x77,
        ["f9"]  = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
        ["f13"] = 0x7C, ["f14"] = 0x7D, ["f15"] = 0x7E, ["f16"] = 0x7F,
        // Win/系统键
        ["win"] = 0x5B, ["lwin"] = 0x5B, ["rwin"] = 0x5C,
        ["apps"] = 0x5D,
        // 字母键
        ["a"]=0x41,["b"]=0x42,["c"]=0x43,["d"]=0x44,["e"]=0x45,
        ["f"]=0x46,["g"]=0x47,["h"]=0x48,["i"]=0x49,["j"]=0x4A,
        ["k"]=0x4B,["l"]=0x4C,["m"]=0x4D,["n"]=0x4E,["o"]=0x4F,
        ["p"]=0x50,["q"]=0x51,["r"]=0x52,["s"]=0x53,["t"]=0x54,
        ["u"]=0x55,["v"]=0x56,["w"]=0x57,["x"]=0x58,["y"]=0x59,["z"]=0x5A,
        // 数字键（主键盘）
        ["0"]=0x30,["1"]=0x31,["2"]=0x32,["3"]=0x33,["4"]=0x34,
        ["5"]=0x35,["6"]=0x36,["7"]=0x37,["8"]=0x38,["9"]=0x39,
        // 数字键盘
        ["num0"]=0x60,["num1"]=0x61,["num2"]=0x62,["num3"]=0x63,["num4"]=0x64,
        ["num5"]=0x65,["num6"]=0x66,["num7"]=0x67,["num8"]=0x68,["num9"]=0x69,
        ["numdiv"]=0x6F,["nummul"]=0x6A,["numsub"]=0x6D,["numadd"]=0x6B,["numdec"]=0x6E,
    };

    // ── P/Invoke 结构体 & 声明 ────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION U; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    static extern bool ClipCursor(ref RECT lpRect);

    [DllImport("user32.dll")]
    static extern bool ClipCursor(IntPtr lpRect);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "LockWorkStation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool NativeLockWorkStation();
}
