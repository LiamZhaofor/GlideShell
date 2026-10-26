using System.Diagnostics;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>
/// 与正在运行的 GlideShell 实例通信：让它正常退出（安装、升级、卸载时用；正常退出会还原系统手势，不必等看门狗），
/// 或者打开设置窗口（再次启动 GlideShell 时，例如从开始菜单）。
/// 请求是注册的窗口消息，发给运行实例 UI 线程的隐藏主窗口（单实例互斥保证只有一个）。
/// </summary>
public static unsafe class InstanceControl
{
    public static uint QuitMessage { get; } = User32.RegisterWindowMessageW("GlideShell.Quit");

    public static uint ShowSettingsMessage { get; } = User32.RegisterWindowMessageW("GlideShell.ShowSettings");

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
