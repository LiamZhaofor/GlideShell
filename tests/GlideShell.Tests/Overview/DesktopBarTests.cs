using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Gestures;
using GlideShell.Core.Overview;

namespace GlideShell.Tests.Overview;

public class DesktopBarTests
{
    private static readonly RectD Area = new(0, 0, 2560, 1528);

    [Fact]
    public void Thumbnails_keep_the_screen_aspect_are_centered_and_line_up_with_the_names()
    {
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        Assert.Equal(3, bar.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(Area.Width / Area.Height, bar.Thumbs[i].Width / bar.Thumbs[i].Height, 6);
            Assert.Equal(bar.Thumbs[i].CenterX, bar.Pills[i].CenterX, 6); // 收起时的名字就在缩略图那一列
            Assert.Equal(bar.Thumbs[i].CenterX, bar.Labels[i].CenterX, 6);
            Assert.True(bar.Labels[i].Top >= bar.Thumbs[i].Bottom);
            Assert.True(bar.Labels[i].Bottom <= Area.Top + bar.ExpandedHeight);
        }
        Assert.Equal(Area.CenterX, (bar.Thumbs[0].Left + bar.Thumbs[2].Right) / 2, 6);
        Assert.True(bar.ExpandedHeight > bar.CollapsedHeight);
    }

    [Fact]
    public void Many_desktops_shrink_to_fit_the_width()
    {
        var bar = DesktopBarGeometry.Compute(Area, 20, 1.5);
        Assert.True(bar.Thumbs[0].Left >= Area.Left && bar.Thumbs[^1].Right <= Area.Right);
    }

    [Fact]
    public void Content_area_shrinks_from_the_top_keeping_the_bottom_edge_when_expanded()
    {
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        var tile = new RectD(400, 600, 800, 500);
        Assert.Equal(tile, bar.MapContent(tile, 0));

        var a0 = bar.ContentArea;
        var whole = bar.MapContent(a0, 1);
        Assert.Equal(bar.ExpandedContentArea.Top, whole.Top, 6);
        Assert.Equal(a0.Bottom, whole.Bottom, 6);
        Assert.Equal(a0.CenterX, whole.CenterX, 6);
        var moved = bar.MapContent(tile, 1);
        Assert.True(moved.Top > tile.Top && moved.Width < tile.Width);
    }

    [Fact]
    public void Zoom_grows_the_chosen_thumbnail_into_the_whole_screen()
    {
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        Assert.Equal(bar.Thumbs[1], bar.ZoomRect(1, 1, 0));
        Assert.Equal(Area, bar.ZoomRect(1, 1, 1));
    }

    [Fact]
    public void Hit_test_uses_names_when_collapsed_and_thumbnails_when_expanded()
    {
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        var pill = bar.Pills[2];
        var thumb = bar.Thumbs[2];
        Assert.Equal(2, bar.HitTest(pill.CenterX, pill.CenterY, 0));
        Assert.Equal(2, bar.HitTest(thumb.CenterX, thumb.CenterY, 1));
        Assert.Equal(-1, bar.HitTest(Area.CenterX, Area.Bottom - 10, 1));
        Assert.True(bar.Contains(pill.CenterX, pill.CenterY, 0));
        Assert.False(bar.Contains(thumb.CenterX, thumb.Bottom + 1, 0)); // 收起时只有桌面名那一条
        Assert.True(bar.Contains(thumb.CenterX, thumb.Bottom + 1, 1));
    }

    // ---------- 控制器：悬停展开、点击放大进入 ----------

    private sealed class FakeHost : IOverviewHost
    {
        public readonly List<string> Calls = [];
        public void Prewarm() { }
        public void CancelPrewarm() { }
        public void Show() { }
        public void Activate() => Calls.Add("Activate");
        public void Closing(long? focusWindowId) => Calls.Add($"Closing:{focusWindowId?.ToString() ?? "-"}");
        public void Hide(long? focusWindowId) => Calls.Add($"Hide:{focusWindowId?.ToString() ?? "-"}");
        public void SwitchDesktop(int desktopIndex) => Calls.Add($"SwitchDesktop:{desktopIndex}");
    }

    private static (OverviewController C, FakeHost Host, long T) Opened()
    {
        var host = new FakeHost();
        var c = new OverviewController(host, () => new OverviewConfig());
        c.SetDesktops(new DesktopBarState(["桌面 1", "桌面 2", "桌面 3"], 0));
        c.Open(0);
        c.OnShown(0);
        long t = 2_000_000;
        Assert.True(c.Sample(t).Visible);
        return (c, host, t);
    }

    [Fact]
    public void Hovering_the_bar_expands_it_and_leaving_collapses_it()
    {
        var (c, _, t) = Opened();
        c.SetBarHovered(true, t);
        Assert.Equal(1, c.Sample(t + 2_000_000).BarExpansion, 3);
        c.SetBarHovered(false, t + 2_000_000);
        Assert.Equal(0, c.Sample(t + 4_000_000).BarExpansion, 3);
    }

    [Fact]
    public void Bar_does_not_expand_while_the_overview_is_closing()
    {
        var (c, _, t) = Opened();
        c.Close(null, t);
        c.SetBarHovered(true, t + 1000);
        Assert.Equal(0, c.Sample(t + 50_000).BarExpansion, 6);
    }

    [Fact]
    public void Clicking_another_desktop_switches_at_once_zooms_in_then_hides()
    {
        var (c, host, t) = Opened();
        c.SetBarHovered(true, t);
        c.Sample(t + 2_000_000);
        c.GoToDesktop(2, t + 2_000_000);
        Assert.Equal("SwitchDesktop:2", host.Calls[^1]); // 真实桌面立刻切换，不等动画

        var mid = c.Sample(t + 2_100_000);
        Assert.Equal(2, mid.ZoomDesktop);
        Assert.InRange(mid.Zoom, 0.01, 0.99);
        Assert.True(mid.Visible);

        // 动画中手势与 Esc（Close）都不接管
        c.Close(null, t + 2_100_000);
        c.Begin(new GestureEvent(GestureKind.Swipe, GesturePhase.Began, 3, GestureAxis.Vertical, GestureDirection.Down, 0, 0, 0, 0, t + 2_100_000, 0));
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("Closing", StringComparison.Ordinal));

        var end = c.Sample(t + 4_000_000);
        Assert.Equal("Hide:-", host.Calls[^1]);
        Assert.True(end.Visible); // 撤层那一帧仍要画（停在全屏）
        Assert.Equal(1, end.Zoom, 3);

        var after = c.Sample(t + 4_010_000);
        Assert.False(after.Visible);
        Assert.Equal(0, after.Progress, 6); // 复位：下次从收起态开始
        Assert.Equal(-1, after.ZoomDesktop);
    }

    [Fact]
    public void Clicking_the_current_desktop_just_closes()
    {
        var (c, host, t) = Opened();
        c.GoToDesktop(0, t);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("SwitchDesktop", StringComparison.Ordinal));
        Assert.Equal("Closing:-", host.Calls[^1]);
    }

    [Fact]
    public void Windows_fade_out_while_zooming_into_another_desktop()
    {
        var windows = new[] { new OverviewWindow(1, "a", new RectD(100, 100, 800, 600), false, true, 0, "p") };
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        var scene = OverviewScene.Build(windows, [new OverviewArea(Area, 1.5, bar)]);
        Assert.True(scene.Slots[0].Tile.Top >= bar.ContentArea.Top); // 平铺在桌面条下面
        var rects = new RectD[1];
        var opacity = new double[1];
        OverviewController.WindowRects(scene, new OverviewFrame(1, 0, 0, default, true, null, 1, 2, 0.75), rects, opacity);
        Assert.Equal(0.25, opacity[0], 6);
        Assert.Equal(scene.Tile(0, 1), rects[0]);
    }
}
