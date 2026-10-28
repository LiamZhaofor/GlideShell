using System.Diagnostics;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>
/// 与正在运行的 GlideShell 实例通信：让它正常退出（安装、升级、卸载时用；正常退出会还原系统手势，不必等看门狗），
/// 或者打开设置窗口（再次启动 GlideShell 时，例如从开始菜单），或者生成诊断报告（GlideShell.exe --diagnostics）。
/// 请求是注册的窗口消息，发给运行实例 UI 线程的隐藏主窗口（单实例互斥保证只有一个）。
/// </summary>
public static unsafe partial class InstanceControl
{
    public static uint QuitMessage { get; } = User32.RegisterWindowMessageW("GlideShell.Quit");

    public static uint ShowSettingsMessage { get; } = User32.RegisterWindowMessageW("GlideShell.ShowSettings");

    public static uint DiagnosticsMessage { get; } = User32.RegisterWindowMessageW("GlideShell.Diagnostics");

    public static uint UpgradeMessage { get; } = User32.RegisterWindowMessageW("GlideShell.Upgrade");

    /// <summary>
    /// 运行实例调用：允许普通进程把这几条请求发给本窗口。UIAccess 版受 UIPI 保护，
    /// 不放行的话安装 / 卸载脚本发出的 --quit、再次启动时的「打开设置」都会被系统丢掉。
    /// </summary>
    public static void AllowRequests(nint hwnd)
    {
        const uint MSGFLT_ALLOW = 1;
        ChangeWindowMessageFilterEx(hwnd, QuitMessage, MSGFLT_ALLOW, null);
        ChangeWindowMessageFilterEx(hwnd, ShowSettingsMessage, MSGFLT_ALLOW, null);
        ChangeWindowMessageFilterEx(hwnd, DiagnosticsMessage, MSGFLT_ALLOW, null);
        ChangeWindowMessageFilterEx(hwnd, UpgradeMessage, MSGFLT_ALLOW, null);
    }

    /// <summary>让运行实例升级到最新版本（有的话；还没查过就先查）。返回 false = 找不到运行实例。</summary>
    public static bool RequestUpgrade()
    {
        nint hwnd = User32.FindWindowW(UiThread.WindowClass, null);
        return hwnd != 0 && User32.PostMessageW(hwnd, UpgradeMessage, 0, 0);
    }

    /// <summary>让运行实例生成诊断报告（它会用记事本打开）。返回 false = 找不到运行实例。</summary>
    public static bool RequestDiagnostics()
    {
        nint hwnd = User32.FindWindowW(UiThread.WindowClass, null);
        if (hwnd == 0) return false;
        uint pid = 0;
        User32.GetWindowThreadProcessId(hwnd, &pid);
        User32.AllowSetForegroundWindow(pid); // 记事本才能来到前台
        return User32.PostMessageW(hwnd, DiagnosticsMessage, 0, 0);
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, void* changeFilterStruct);

    /// <summary>
    /// 让运行实例打开设置窗口。本进程是用户刚启动的、拥有前台权限，先把权限让给运行实例，
    /// 它的设置窗口才能来到前台。返回 false = 找不到运行实例（例如它还在启动中）。
    /// </summary>
    public static bool RequestShowSettings()
    {
        nint hwnd = User32.FindWindowW(UiThread.WindowClass, null);
        if (hwnd == 0) return false;
        uint pid = 0;
        User32.GetWindowThreadProcessId(hwnd, &pid);
        User32.AllowSetForegroundWindow(pid);
        return User32.PostMessageW(hwnd, ShowSettingsMessage, 0, 0);
    }

    /// <summary>
    /// 请求退出并等它结束。返回 true = 已退出或本来就没在运行；false = 超时仍未退出。
    /// </summary>
    public static bool RequestQuit(TimeSpan timeout)
    {
        nint hwnd = User32.FindWindowW(UiThread.WindowClass, null);
        if (hwnd == 0) return true;
        uint pid = 0;
        User32.GetWindowThreadProcessId(hwnd, &pid);
        if (!User32.PostMessageW(hwnd, QuitMessage, 0, 0)) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // 已经退出
        }
    }
}
