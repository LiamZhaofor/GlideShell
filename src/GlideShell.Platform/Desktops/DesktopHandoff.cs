using GlideShell.Core.Overview;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;

namespace GlideShell.Platform.Desktops;

/// <summary>
/// 在覆盖层下面切换虚拟桌面后的收尾（四指切桌面与总览桌面条共用）：等真实桌面完全切好，再把焦点交给新桌面。
/// </summary>
internal static unsafe class DesktopHandoff
{
    /// <summary>
    /// 新桌面的窗口都没有外壳 cloak、旧桌面的窗口都有（只看外壳那一位，应用自己的 cloak 不算）。
    /// 只等切换通知不够：explorer 逐个解除 / 加上 cloak 要再晚几帧，撤层早了会看到旧窗口消失、新窗口出现（真机：有窗口的桌面之间切换闪一下）。
    /// </summary>
    public static bool CloakSettled(IEnumerable<OverviewWindow> windows, Guid target, Guid? previous)
    {
        const int DWM_CLOAKED_SHELL = 2;
        foreach (var w in windows)
        {
            if (w.DesktopId != target && w.DesktopId != previous) continue;
            int cloaked = 0;
            if (Dwmapi.DwmGetWindowAttribute((nint)w.Id, Dwmapi.DWMWA_CLOAKED, &cloaked, sizeof(int)) != 0) continue; // 窗口已关闭
            bool shellCloaked = (cloaked & DWM_CLOAKED_SHELL) != 0;
            if (w.DesktopId == target ? shellCloaked : !shellCloaked) return false;
        }
        return true;
    }

    /// <summary>
    /// 焦点交给新桌面 Z 序最上面的窗口（<paramref name="windows"/> 最上层在前）；空桌面交给桌面本身，
    /// 避免键盘输入打进旧桌面上看不见的窗口（S3）。返回交给了谁（日志用）。
    /// </summary>
    public static string ActivateTop(IEnumerable<OverviewWindow> windows, Guid target)
    {
        var top = windows.FirstOrDefault(w => w.DesktopId == target);
        nint hwnd = top is not null ? (nint)top.Id : User32.FindWindowW("Progman", null);
        string who = top?.Title ?? "桌面";
        if (!WindowActivator.Activate(hwnd)) Diagnostics.Log.Warn($"切桌面后未能把焦点交给 {who}");
        return who;
    }
}
