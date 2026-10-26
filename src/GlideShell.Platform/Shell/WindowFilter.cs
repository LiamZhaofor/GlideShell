namespace GlideShell.Platform.Shell;

/// <summary>判断一个顶层窗口是否进入总览所需的原始属性（由 <see cref="WindowCatalog"/> 从 Win32 读取）。</summary>
public readonly record struct WindowProbe(
    bool Visible,
    bool ToolWindow,
    bool AppWindow,
    bool NoActivate,
    bool HasOwner,
    int Cloaked,
    string ClassName,
    int TitleLength,
    int Width,
    int Height,
    bool? OnCurrentDesktop,
    bool IsOwnWindow);

/// <summary>
/// 窗口收录规则（纯函数，可测）。规则来自旧原型的实测与 Alt+Tab 的惯例：
/// 只要用户在任务栏 / Alt+Tab 里能看到的窗口，包括最小化窗口；其他虚拟桌面、工具窗、系统外壳一律排除。
/// </summary>
public static class WindowFilter
{
    public const int MinWidth = 160;
    public const int MinHeight = 120;

    // DWM cloak 位：1 = 应用自己隐藏（挂起的 UWP，任务视图照样显示），2 = 外壳隐藏（其他虚拟桌面），4 = 继承
    private const int CloakedShell = 2;
    private const int CloakedInherited = 4;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "tooltips_class32", "TaskListThumbnailWnd",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    /// <summary>返回 null 表示收录；否则返回排除原因（--dump 诊断用）。</summary>
    public static string? Reject(in WindowProbe w)
    {
        if (w.IsOwnWindow) return "GlideShell 自己的窗口";
        if (!w.Visible) return "不可见";
        if (w.ToolWindow && !w.AppWindow) return "工具窗口";
        if (w.NoActivate && !w.AppWindow) return "不可激活";
        if (w.HasOwner && !w.AppWindow) return "有 owner 的附属窗口";
        if ((w.Cloaked & CloakedShell) != 0) return "其他虚拟桌面";
        if ((w.Cloaked & CloakedInherited) != 0) return "继承隐藏";
        if (ShellClasses.Contains(w.ClassName)) return "系统外壳";
        if (w.TitleLength == 0) return "无标题";
        if (w.Width < MinWidth || w.Height < MinHeight) return $"尺寸过小 {w.Width}×{w.Height}";
        if (w.OnCurrentDesktop == false) return "其他虚拟桌面（VDM）";
        return null;
    }
}
