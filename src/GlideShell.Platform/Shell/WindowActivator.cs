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

    /// <summary>
    /// 显示桌面：异步最小化（不激活别的窗口），先关掉它的 DWM 过渡动画——覆盖层下面不需要「缩到任务栏」的动画，
    /// 而任务栏不在覆盖层下面，动画会露出来。动画开关之后用 <see cref="EnableTransitions"/> 恢复。
    /// </summary>
    public static void MinimizeQuietly(nint hwnd)
    {
        if (hwnd == 0 || !User32.IsWindow(hwnd)) return;
        int disable = 1;
        Dwmapi.DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, &disable, sizeof(int));
        ShowWindowAsync(hwnd, 7 /* SW_SHOWMINNOACTIVE */);
    }

    /// <summary>显示桌面后拉回：异步还原（会激活它，从下往上依次还原，最后一个就在最上层），同样先关掉过渡动画。</summary>
    public static void RestoreQuietly(nint hwnd)
    {
        if (hwnd == 0 || !User32.IsWindow(hwnd) || !User32.IsIconic(hwnd)) return;
        int disable = 1;
        Dwmapi.DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, &disable, sizeof(int));
        ShowWindowAsync(hwnd, User32.SW_RESTORE);
    }

    /// <summary>恢复 <see cref="MinimizeQuietly"/> / <see cref="RestoreQuietly"/> 关掉的过渡动画。</summary>
    /// <summary>
    /// 不激活、不播系统动画地还原一个最小化窗口，并放到最下层（被别的窗口挡着，程序在后面重绘）。
    /// 两步都投递给窗口自己的线程（ShowWindowAsync + SWP_ASYNCWINDOWPOS），顺序不变，程序没响应也不会卡住调用方。
    /// DWM 过渡动画保持关闭，调用方稍后用 <see cref="EnableTransitions"/> 恢复。
    /// </summary>
    public static void RestoreBehind(nint hwnd)
    {
        if (hwnd == 0 || !User32.IsWindow(hwnd) || !User32.IsIconic(hwnd)) return;
        int disable = 1;
        Dwmapi.DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, &disable, sizeof(int));
        ShowWindowAsync(hwnd, 4 /* SW_SHOWNOACTIVATE */);
        // 置顶窗口压到最下层会失去置顶属性：它们留在原来的层次（置顶窗口本来就在最上面，挡不住也无妨）
        if ((User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE) & 0x00000008 /* WS_EX_TOPMOST */) != 0) return;
        User32.SetWindowPos(hwnd, 1 /* HWND_BOTTOM */, 0, 0, 0, 0,
            User32.SWP_NOACTIVATE | 0x0001 /* SWP_NOSIZE */ | 0x0002 /* SWP_NOMOVE */ | 0x4000 /* SWP_ASYNCWINDOWPOS */);
    }

    public static void EnableTransitions(nint hwnd)
    {
        if (hwnd == 0 || !User32.IsWindow(hwnd)) return;
        int enable = 0;
        Dwmapi.DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, &enable, sizeof(int));
    }

    /// <summary>
    /// 按 <paramref name="topFirst"/>（最上层在前）排好这些顶层窗口的层次，不激活（从下往上逐个提到最上层，跳过没响应的程序）：
    /// 异步还原的窗口各在自己的线程里完成，谁先谁后不确定，还原完要按原来的层次重排。
    /// </summary>
    public static void Stack(IReadOnlyList<nint> topFirst)
    {
        var windows = topFirst.Where(h => h != 0 && User32.IsWindow(h)).ToList();
        if (windows.Count == 0) return;
        const uint flags = 0x0001 | 0x0002 | 0x0010; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        // 一次性的 DeferWindowPos 里只要有一个窗口不让动（例如更高权限的程序），整批都不生效：逐个从下往上提到最上层
        for (int i = windows.Count - 1; i >= 0; i--)
            if (!IsHungAppWindow(windows[i])) User32.SetWindowPos(windows[i], 0 /* HWND_TOP */, 0, 0, 0, 0, flags);
    }

    private const uint DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool IsHungAppWindow(nint hwnd);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint hwnd, int command);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, int doAttach);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint hwnd);
}
