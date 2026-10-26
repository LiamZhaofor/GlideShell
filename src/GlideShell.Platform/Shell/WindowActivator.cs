using GlideShell.Platform.Native;

namespace GlideShell.Platform.Shell;

/// <summary>
/// 把某个窗口置为前台。GlideShell 的覆盖层从不激活，所以本进程通常不是前台进程，
/// 直接 SetForegroundWindow 会被前台锁规则拒绝（只闪任务栏）。这里临时把输入状态挂到当前前台线程上再切换——
/// 不注入任何按键（旧原型的 ALT 技巧会让目标程序的菜单栏被激活）。P7 改为 UIAccess 后可直接切换。
/// </summary>
public static unsafe partial class WindowActivator
{
    /// <param name="instant">
    /// 最小化窗口无动画还原（临时关掉它的 DWM 过渡动画）：总览在覆盖层下面还原它，撤层前就要已经在原位，
    /// 不能让系统的「从任务栏升起」动画在撤层后才播。别的进程的窗口不一定允许设置，失败就照常还原。
    /// </param>
    public static bool Activate(nint hwnd, bool instant = false)
    {
        if (hwnd == 0 || !User32.IsWindow(hwnd)) return false;
        if (User32.IsIconic(hwnd))
        {
            int disable = 1;
            bool noTransition = instant && Dwmapi.DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, &disable, sizeof(int)) == 0;
            User32.ShowWindow(hwnd, User32.SW_RESTORE);
            if (noTransition)
            {
                int enable = 0;
                Dwmapi.DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, &enable, sizeof(int));
            }
        }

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

    private const uint DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, int doAttach);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint hwnd);
}
