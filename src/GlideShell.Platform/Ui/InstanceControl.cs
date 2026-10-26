using System.Diagnostics;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>
/// 让正在运行的 GlideShell 正常退出（安装、升级、卸载时用；正常退出会还原系统手势，不必等看门狗）。
/// 请求是一条注册的窗口消息，发给运行实例 UI 线程的隐藏主窗口（单实例互斥保证只有一个）。
/// </summary>
public static unsafe class InstanceControl
{
    public static uint QuitMessage { get; } = User32.RegisterWindowMessageW("GlideShell.Quit");

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
