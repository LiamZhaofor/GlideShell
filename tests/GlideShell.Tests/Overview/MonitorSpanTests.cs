using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;

namespace GlideShell.Tests.Overview;

/// <summary>多显示器：每块屏的覆盖层除了画归它管的窗口，还要画有一部分落在本屏上的窗口。</summary>
public class MonitorSpanTests
{
    // 左屏 2560×1528 工作区，右屏在它右边（1920×1040），上沿对不齐
    private static readonly RectD Left = new(0, 0, 2560, 1528);
    private static readonly RectD Right = new(2560, -200, 1920, 1040);

    private static OverviewWindow Window(RectD source, bool minimized = false)
        => new(1, "w", source, minimized, false, 0, "p");

    [Fact]
    public void Window_straddling_two_monitors_overlaps_both()
    {
        var w = Window(new RectD(2200, 100, 800, 600));
        Assert.True(w.Overlaps(Left));
        Assert.True(w.Overlaps(Right));
    }

    [Fact]
    public void Window_inside_one_monitor_overlaps_only_that_one()
    {
        var w = Window(new RectD(100, 100, 800, 600));
        Assert.True(w.Overlaps(Left));
        Assert.False(w.Overlaps(Right));
    }

    [Fact]
    public void Touching_the_edge_or_being_minimized_does_not_count()
    {
        Assert.False(Window(new RectD(1760, 100, 800, 600)).Overlaps(Right)); // 右边沿正好贴着
        Assert.False(Window(new RectD(2200, 100, 800, 600), minimized: true).Overlaps(Right));
        Assert.False(Window(new RectD(2600, 900, 0, 0)).Overlaps(Right)); // 空矩形
    }
}
