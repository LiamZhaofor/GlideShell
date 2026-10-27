using System.Runtime.InteropServices;
using GlideShell.Core.Configuration;
using GlideShell.Core.Gestures;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Input;

/// <summary>
/// 鼠标按键手势的 Windows 实现：低级鼠标钩子（专门的线程，不受 UI 线程忙碌影响——钩子慢了整个系统的指针都会卡）。
/// 按下配置的键时吞掉它并记下位置；按住期间指针原地不动（吞掉移动），每次移动的位移交给 <see cref="MouseDragRecognizer"/>；
/// 松开时如果没拖开，就补发一次原来的按下 + 松开（带标记，钩子放行），后退 / 前进 / 中键照常工作。
/// 暂停、排除的程序在前台、全屏时（<c>suppressed</c>）按下不拦截。
/// </summary>
public sealed unsafe partial class MouseButtonGestures : IDisposable
{
    private const int WhMouseLl = 14;
    private const uint WmMouseMove = 0x0200, WmMButtonDown = 0x0207, WmMButtonUp = 0x0208, WmXButtonDown = 0x020B, WmXButtonUp = 0x020C;
    private const uint WmQuit = 0x0012, WmApp = 0x8000;
    private const uint LlmhfInjected = 0x1;
    private const nint Marker = 0x47534D42; // 补发的点击带这个 dwExtraInfo，钩子认出来放行
    private const uint MouseeventfMiddleDown = 0x0020, MouseeventfMiddleUp = 0x0040, MouseeventfXDown = 0x0080, MouseeventfXUp = 0x0100;

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

    private static MouseButtonGestures? s_instance; // 钩子回调是静态的

    private readonly Func<MouseGesturesConfig> _config;
    private readonly Func<bool> _suppressed;
    private readonly MouseDragRecognizer _recognizer;
    private readonly Thread _thread;
    private uint _threadId;
    private nint _hook;
    private MouseDragButton _held = MouseDragButton.None; // 钩子线程：正按着、被拦截的键
    private int _anchorX, _anchorY;
    private double _dpi = 1;

    /// <param name="onGesture">在钩子线程上调用（必须很快返回，同触摸板手势的输入线程约定）。</param>
    public MouseButtonGestures(Func<MouseGesturesConfig> config, Func<bool> suppressed, Action<GestureEvent> onGesture)
    {
        _config = config;
        _suppressed = suppressed;
        _recognizer = new MouseDragRecognizer(config, onGesture);
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
        User32.MSG msg;
        while (User32.GetMessageW(&msg, 0, 0, 0) > 0)
        {
            if (msg.message == WmApp) Replay((MouseDragButton)(int)msg.wParam);
        }
        User32.UnhookWindowsHookEx(_hook);
        _hook = 0;
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

    /// <summary>钩子线程：返回 true 吞掉这次事件。</summary>
    private bool OnMouse(uint message, MsLlHookStruct* m)
    {
        if ((m->Flags & LlmhfInjected) != 0 && m->ExtraInfo == Marker) return false; // 自己补发的点击
        long now = HostClock.NowUs();
        if (_held != MouseDragButton.None)
        {
            if (message == WmMouseMove)
            {
                // 指针原地不动：位移交给手势（被吞掉的移动不改变指针位置，下一次的坐标仍相对按下的位置）
                int dx = m->X - _anchorX, dy = m->Y - _anchorY;
                if (dx != 0 || dy != 0) _recognizer.Move(dx / _dpi, dy / _dpi, now);
                return true;
            }
            if (ButtonOf(message, m->MouseData, out bool down) == _held && !down)
            {
                var button = _held;
                _held = MouseDragButton.None;
                // 没拖开：补发这次点击（在钩子回调之外发，见 Run）
                if (!_recognizer.Release(now)) PostThreadMessageW(_threadId, WmApp, (nint)(int)button, 0);
                return true;
            }
            return false;
        }

        var configured = _config().Button;
        if (configured == MouseDragButton.None) return false;
        if (ButtonOf(message, m->MouseData, out bool pressed) != configured || !pressed) return false;
        if (_suppressed()) return false;
        _held = configured;
        _anchorX = m->X;
        _anchorY = m->Y;
        _dpi = DpiAt(m->X, m->Y);
        _recognizer.Press(now);
        return true;
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

    private static double DpiAt(int x, int y)
    {
        nint monitor = MonitorFromPoint(new User32.POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
        uint dx, dy;
        return Shcore.GetDpiForMonitor(monitor, Shcore.MDT_EFFECTIVE_DPI, &dx, &dy) == 0 && dx > 0 ? dx / 96.0 : 1;
    }

    /// <summary>补发一次点击（按下 + 松开），带标记让钩子放行。</summary>
    private static void Replay(MouseDragButton button)
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
}
