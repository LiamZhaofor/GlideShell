using System.Runtime.InteropServices;
using GlideShell.Core.Configuration;
using GlideShell.Core.Gestures;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Input;

/// <summary>
/// 鼠标按键手势的 Windows 实现，都在一个专门的线程上（钩子慢了整个系统的指针都会卡，不能放在会忙的 UI 线程上）：
/// <list type="bullet">
/// <item>低级鼠标钩子：按下配置的键时吞掉它（松开也吞）；没拖开就松开时补发一次原来的按下 + 松开（带标记，钩子放行），
/// 后退 / 前进 / 中键照常工作。指针照常移动。</item>
/// <item>原始输入（RIDEV_INPUTSINK）：按住期间的位移取鼠标的原始位移（不带指针加速、指针到了屏幕边上也照样有），
/// 与 Mac Mouse Fix 一样；收不到原始输入（远程桌面、绘图板等绝对坐标设备）时退回钩子里的指针坐标差。</item>
/// </list>
/// 位移按按下时所在屏的尺寸换算（<see cref="MouseDragRecognizer.Scale"/>）。暂停、排除的程序在前台、全屏时（<c>suppressed</c>）按下不拦截。
/// </summary>
public sealed unsafe partial class MouseButtonGestures : IDisposable
{
    private const int WhMouseLl = 14;
    private const uint WmMouseMove = 0x0200, WmMButtonDown = 0x0207, WmMButtonUp = 0x0208, WmXButtonDown = 0x020B, WmXButtonUp = 0x020C;
    private const uint WmQuit = 0x0012, WmInput = 0x00FF, WmApp = 0x8000, WmRawOn = WmApp + 1, WmRawOff = WmApp + 2;
    private const uint LlmhfInjected = 0x1;
    private const nint Marker = 0x47534D42; // 补发的点击带这个 dwExtraInfo，钩子认出来放行
    private const uint MouseeventfMiddleDown = 0x0020, MouseeventfMiddleUp = 0x0040, MouseeventfXDown = 0x0080, MouseeventfXUp = 0x0100;
    private const uint RidInput = 0x10000003, RidevInputSink = 0x00000100, RidevRemove = 0x00000001, RimTypeMouse = 0;
    private const ushort MouseMoveAbsolute = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public uint Type;
        private uint _pad;
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public nint Target;
    }

    /// <summary>RAWINPUTHEADER（24 字节）+ RAWMOUSE。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouseInput
    {
        public uint Type, Size;
        public nint Device, WParam;
        public ushort Flags;
        private ushort _pad;
        public uint Buttons, RawButtons;
        public int LastX, LastY;
        public uint ExtraInformation;
    }

    private static MouseButtonGestures? s_instance; // 钩子回调是静态的

    private readonly Func<MouseGesturesConfig> _config;
    private readonly Func<(double DesktopTravelMm, double OverviewTravelMm)> _travel;
    private readonly Func<bool> _suppressed;
    private readonly MouseDragRecognizer _recognizer;
    private readonly Thread _thread;
    private uint _threadId;
    private nint _hook;

    /// <summary>鼠标钩子装上了（诊断用）。</summary>
    public bool Hooked => _hook != 0;
    private nint _sink; // 收原始输入的仅消息窗口
    private MouseDragButton _held = MouseDragButton.None; // 正按着、被拦截的键
    private bool _rawSeen; // 这次按住期间收到过相对位移的原始输入
    private bool _rawRegistered, _rawFailureLogged; // 钩子线程
    private int _lastX, _lastY;

    /// <param name="travel">切一个桌面、完全展开总览各要多少毫米（与触摸板同一套配置）。</param>
    /// <param name="onGesture">在本线程上调用（必须很快返回，同触摸板手势的输入线程约定）。</param>
    public MouseButtonGestures(Func<MouseGesturesConfig> config, Func<(double DesktopTravelMm, double OverviewTravelMm)> travel, Func<bool> suppressed,
        Action<GestureEvent> onGesture)
    {
        _config = config;
        _travel = travel;
        _suppressed = suppressed;
        _recognizer = new MouseDragRecognizer(onGesture);
        s_instance = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "GlideShell.MouseHook", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = User32.SetWindowsHookExW(WhMouseLl, &Proc, Kernel32.GetModuleHandleW(null), 0);
        if (_hook == 0)
        {
            Log.Warn($"鼠标按键手势：安装鼠标钩子失败（{Marshal.GetLastPInvokeError()}）");
            return;
        }
        CreateSink();
        User32.MSG msg;
        while (User32.GetMessageW(&msg, 0, 0, 0) > 0)
        {
            if (msg.message == WmApp) SendClick((MouseDragButton)(int)msg.wParam);
            else if (msg.message == WmInput) OnRawInput(msg.lParam);
            else if (msg.message == WmRawOn) SetRawInput(true);
            else if (msg.message == WmRawOff) SetRawInput(false);
            else User32.DispatchMessageW(&msg);
        }
        User32.UnhookWindowsHookEx(_hook);
        _hook = 0;
        if (_sink != 0) User32.DestroyWindow(_sink);
    }

    /// <summary>收鼠标原始输入的仅消息窗口。</summary>
    private void CreateSink()
    {
        fixed (char* cls = "STATIC")
            _sink = User32.CreateWindowExW(0, cls, null, 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, Kernel32.GetModuleHandleW(null), 0);
    }

    /// <summary>
    /// 只在按住手势键期间收鼠标的原始输入（后台也收）：一直开着的话，鼠标、触摸板指针每动一下都要把这个线程多唤醒一次
    /// （每秒几百上千次）。失败时只用钩子里的指针坐标。
    /// </summary>
    private void SetRawInput(bool on)
    {
        if (_sink == 0 || on == _rawRegistered) return;
        var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = on ? RidevInputSink : RidevRemove, Target = on ? _sink : 0 };
        if (RegisterRawInputDevices(&device, 1, (uint)sizeof(RawInputDevice))) _rawRegistered = on;
        else if (on && !_rawFailureLogged)
        {
            _rawFailureLogged = true;
            Log.Warn($"鼠标按键手势：注册原始输入失败（{Marshal.GetLastPInvokeError()}），改用指针坐标");
        }
    }

    private void OnRawInput(nint handle)
    {
        RawMouseInput raw;
        uint size = (uint)sizeof(RawMouseInput);
        if (GetRawInputData(handle, RidInput, &raw, &size, 24) == unchecked((uint)-1) || raw.Type != RimTypeMouse) return;
        if (_held == MouseDragButton.None || (raw.Flags & MouseMoveAbsolute) != 0) return;
        if (raw.LastX == 0 && raw.LastY == 0) return;
        if (!_rawSeen)
        {
            // 按下后才开始收原始输入：第一包对应的那次移动钩子已经按指针坐标算过了，不重复算
            _rawSeen = true;
            return;
        }
        _recognizer.Move(raw.LastX, raw.LastY, HostClock.NowUs());
    }

    [UnmanagedCallersOnly]
    private static nint Proc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && s_instance is { } self)
        {
            try
            {
                if (self.OnMouse((uint)wParam, (MsLlHookStruct*)lParam)) return 1;
            }
            catch (Exception ex)
            {
                Log.Error("鼠标钩子异常", ex);
            }
        }
        return User32.CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>钩子回调：返回 true 吞掉这次事件。</summary>
    private bool OnMouse(uint message, MsLlHookStruct* m)
    {
        if ((m->Flags & LlmhfInjected) != 0 && m->ExtraInfo == Marker) return false; // 自己补发的点击
        long now = HostClock.NowUs();
        if (_held != MouseDragButton.None)
        {
            if (message == WmMouseMove)
            {
                // 收不到原始输入时退回指针坐标差（到屏幕边上就没有位移了）；指针照常移动
                if (!_rawSeen) _recognizer.Move(m->X - _lastX, m->Y - _lastY, now);
                _lastX = m->X;
                _lastY = m->Y;
                return false;
            }
            if (ButtonOf(message, m->MouseData, out bool down) == _held && !down)
            {
                var button = _held;
                _held = MouseDragButton.None;
                PostThreadMessageW(_threadId, WmRawOff, 0, 0);
                // 没拖开：补发这次点击（在钩子回调之外发，见 Run）
                if (!_recognizer.Release(now)) PostThreadMessageW(_threadId, WmApp, (nint)(int)button, 0);
                return true;
            }
            return false;
        }

        var cfg = _config();
        if (cfg.Button == MouseDragButton.None) return false;
        if (ButtonOf(message, m->MouseData, out bool pressed) != cfg.Button || !pressed) return false;
        if (_suppressed()) return false;
        _held = cfg.Button;
        _rawSeen = false;
        PostThreadMessageW(_threadId, WmRawOn, 0, 0);
        _lastX = m->X;
        _lastY = m->Y;
        var (kx, ky) = ScaleAt(m->X, m->Y, cfg.Sensitivity);
        _recognizer.Press(now, kx, ky);
        return true;
    }

    /// <summary>按下处所在屏的工作区尺寸 → 每像素多少毫米。</summary>
    private (double, double) ScaleAt(int x, int y, double sensitivity)
    {
        nint monitor = MonitorFromPoint(new User32.POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new User32.MONITORINFO { cbSize = (uint)sizeof(User32.MONITORINFO) };
        double w = 1920, h = 1080, dpi = 1;
        if (User32.GetMonitorInfo(monitor, &info))
        {
            w = info.rcWork.Right - info.rcWork.Left;
            h = info.rcWork.Bottom - info.rcWork.Top;
        }
        uint dx, dy;
        if (Shcore.GetDpiForMonitor(monitor, Shcore.MDT_EFFECTIVE_DPI, &dx, &dy) == 0 && dx > 0) dpi = dx / 96.0;
        var (desktopMm, overviewMm) = _travel();
        return MouseDragRecognizer.Scale(w, h, Desktops.DesktopStrip.Gap(dpi), desktopMm, overviewMm, sensitivity);
    }

    private static MouseDragButton ButtonOf(uint message, uint mouseData, out bool down)
    {
        down = message is WmMButtonDown or WmXButtonDown;
        return message switch
        {
            WmMButtonDown or WmMButtonUp => MouseDragButton.Middle,
            WmXButtonDown or WmXButtonUp => (mouseData >> 16) == 1 ? MouseDragButton.Back : MouseDragButton.Forward,
            _ => MouseDragButton.None,
        };
    }

    /// <summary>
    /// 发一次点击（按下 + 松开），带标记让钩子放行：按键手势没拖开时补发原来的点击，三指轻点的中键也用它
    /// （按住中键拖动的手势开着时，不带标记的中键会被钩子当成真的按下）。
    /// </summary>
    public static void SendClick(MouseDragButton button)
    {
        var inputs = stackalloc MouseInput[2];
        (uint downFlag, uint upFlag, uint data) = button switch
        {
            MouseDragButton.Middle => (MouseeventfMiddleDown, MouseeventfMiddleUp, 0u),
            MouseDragButton.Forward => (MouseeventfXDown, MouseeventfXUp, 2u),
            _ => (MouseeventfXDown, MouseeventfXUp, 1u),
        };
        inputs[0] = new MouseInput { Type = 0, Flags = downFlag, MouseData = data, ExtraInfo = Marker };
        inputs[1] = new MouseInput { Type = 0, Flags = upFlag, MouseData = data, ExtraInfo = Marker };
        SendInput(2, inputs, sizeof(MouseInput));
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(1));
        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, MouseInput* inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(User32.POINT point, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterRawInputDevices(RawInputDevice* devices, uint count, uint size);

    [LibraryImport("user32.dll")]
    private static partial uint GetRawInputData(nint rawInput, uint command, void* data, uint* size, uint headerSize);
}
