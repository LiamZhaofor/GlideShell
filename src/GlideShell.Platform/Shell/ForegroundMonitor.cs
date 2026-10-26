using System.Runtime.InteropServices;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Shell;

/// <summary>前台窗口状态：用于「排除程序」与「全屏时暂停」。</summary>
public sealed record ForegroundState(nint Window, string ProcessName);

/// <summary>
/// 跟踪前台窗口（EVENT_SYSTEM_FOREGROUND 事件驱动，进程名只在切换时取一次）。
/// 必须在有消息循环的线程上创建（WINEVENT_OUTOFCONTEXT 通过该线程的消息队列回调）。
/// 跳过本进程的事件：GlideShell 覆盖层成为前台时，仍以用户原来的程序为准。
/// </summary>
public sealed unsafe class ForegroundMonitor : IDisposable
{
    private static ForegroundMonitor? s_instance;
    private nint _hook;
    private volatile ForegroundState _current = new(0, "");

    public ForegroundMonitor()
    {
        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
            throw new InvalidOperationException("一个进程只能有一个 ForegroundMonitor");
        _hook = User32.SetWinEventHook(User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND, 0, &OnEvent, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);
        Update(User32.GetForegroundWindow());
    }

    /// <summary>任意线程可读。</summary>
    public ForegroundState Current => _current;

    public event Action<ForegroundState>? Changed;

    /// <summary>
    /// 当前是否处于全屏应用中（任意线程可调用，开销约十几微秒，所以只在手指落下时问一次）：
    /// 外壳的「免打扰」状态（全屏 / D3D 独占 / 演示模式），或前台窗口铺满所在显示器。
    /// </summary>
    public bool IsFullscreenNow()
    {
        int state = 0;
        if (Shell32.SHQueryUserNotificationState(&state) == 0 &&
            state is Shell32.QUNS_BUSY or Shell32.QUNS_RUNNING_D3D_FULL_SCREEN or Shell32.QUNS_PRESENTATION_MODE)
            return true;

        nint fg = _current.Window;
        if (fg == 0 || !User32.IsWindow(fg)) return false;
        string cls = WindowCatalog.ClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        User32.RECT r;
        if (!User32.GetWindowRect(fg, &r)) return false;
        var mi = new User32.MONITORINFO { cbSize = (uint)sizeof(User32.MONITORINFO) };
        if (!User32.GetMonitorInfo(User32.MonitorFromWindow(fg, User32.MONITOR_DEFAULTTONEAREST), &mi)) return false;
        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            User32.UnhookWinEvent(_hook);
            _hook = 0;
        }
        Interlocked.CompareExchange(ref s_instance, null, this);
    }

    private void Update(nint hwnd)
    {
        if (hwnd == 0) return;
        uint pid = 0;
        User32.GetWindowThreadProcessId(hwnd, &pid);
        var state = new ForegroundState(hwnd, WindowCatalog.ProcessName(pid));
        _current = state;
        Changed?.Invoke(state);
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (idObject == 0) s_instance?.Update(hwnd);
        }
        catch
        {
            // 非托管回调里不能抛出；状态保持上一次的值
        }
    }
}
