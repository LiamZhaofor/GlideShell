using GlideShell.Platform.Native;

namespace GlideShell.Platform.Shell;

/// <summary>
/// 把某个窗口置为前台。GlideShell 的覆盖层从不激活，所以本进程通常不是前台进程，
/// 直接 SetForegroundWindow 会被前台锁规则拒绝（只闪任务栏）。这里临时把输入状态挂到当前前台线程上再切换——
/// 不注入任何按键（旧原型的 ALT 技巧会让目标程序的菜单栏被激活）。P7 改为 UIAccess 后可直接切换。
/// </summary>
public static unsafe partial class WindowActivator
{
    public static bool Activate(nint hwnd)
    {
        if (hwnd == 0 || !User32.IsWindow(hwnd)) return false;
        if (User32.IsIconic(hwnd)) User32.ShowWindow(hwnd, User32.SW_RESTORE);

        nint foreground = User32.GetForegroundWindow();
        uint fgThread = foreground != 0 ? User32.GetWindowThreadProcessId(foreground, null) : 0;
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, 1);
        try
        {
            BringWindowToTop(hwnd);
            return User32.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, 0);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, int doAttach);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint hwnd);
}
