using GlideShell.Core.Geometry;

namespace GlideShell.Core.Overview;

/// <summary>
/// 参与总览 / 切换的一个顶层窗口（平台无关的数据）。<see cref="Source"/> 是屏幕物理像素下的可见边界；
/// 最小化窗口用还原后的位置（总览里从那里「飞」出来）。<see cref="DesktopId"/> 只在跨桌面枚举时填写。
/// </summary>
public sealed record OverviewWindow(
    long Id,
    string Title,
    RectD Source,
    bool IsMinimized,
    bool IsForeground,
    int MonitorIndex,
    string ProcessName,
    Guid? DesktopId = null)
{
    /// <summary>
    /// 此刻有没有一部分画在 <paramref name="area"/>（某块屏的覆盖层范围）里。最小化窗口不在屏幕上，不算。
    /// 每块屏的覆盖层除了画「归它管」的窗口，也要画这些窗口在本屏上的那一部分，否则跨屏的窗口在覆盖层出现 / 撤掉的那一刻会缺一块。
    /// </summary>
    public bool Overlaps(RectD area) => !IsMinimized && Source.Intersects(area);
}
