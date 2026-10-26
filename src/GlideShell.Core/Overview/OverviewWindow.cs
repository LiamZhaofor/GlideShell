using GlideShell.Core.Geometry;

namespace GlideShell.Core.Overview;

/// <summary>
/// 参与总览 / 切换的一个顶层窗口（平台无关的数据）。<see cref="Source"/> 是屏幕物理像素下的可见边界；
/// 最小化窗口用还原后的位置（总览里从那里「飞」出来）。
/// </summary>
public sealed record OverviewWindow(
    long Id,
    string Title,
    RectD Source,
    bool IsMinimized,
    bool IsForeground,
    int MonitorIndex,
    string ProcessName);
