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
        Assert.Equal(Area.CenterX, (bar.Thumbs[0].Left + bar.AddTile.Right) / 2, 6); // 连同末尾的「+」整排居中
        Assert.True(bar.AddTile.Left > bar.Thumbs[2].Right);
        Assert.Equal(bar.Thumbs[0].Height, bar.AddTile.Height, 6);
        Assert.True(bar.ExpandedHeight > bar.CollapsedHeight);
    }

    [Fact]
    public void Many_desktops_shrink_to_fit_the_width()
    {
        var bar = DesktopBarGeometry.Compute(Area, 20, 1.5);
        Assert.True(bar.Thumbs[0].Left >= Area.Left && bar.Thumbs[^1].Right <= Area.Right);
    }

    [Fact]
    public void Plus_and_remove_buttons_are_hit_only_where_they_are_shown()
    {
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        Assert.Equal(DesktopBarPart.Add, bar.Hit(bar.AddPill.CenterX, bar.AddPill.CenterY, 0).Part);
        Assert.Equal(DesktopBarPart.Add, bar.Hit(bar.AddTile.CenterX, bar.AddTile.CenterY, 1).Part);

        var x = bar.RemoveButton(1);
        Assert.True(x.Right <= bar.Labels[1].Right && x.Left > bar.Labels[1].CenterX); // 名字右端
        Assert.Equal(new DesktopBarHit(DesktopBarPart.Remove, 1), bar.Hit(x.CenterX, x.CenterY, 1));
        Assert.Equal(new DesktopBarHit(DesktopBarPart.Desktop, 1), bar.Hit(x.CenterX, x.CenterY, 1, removable: false));
        Assert.Equal(new DesktopBarHit(DesktopBarPart.Desktop, 1), bar.Hit(bar.Labels[1].Left + 2, bar.Labels[1].CenterY, 1));

        // 只剩一个桌面：不能删
        var single = DesktopBarGeometry.Compute(Area, 1, 1.5);
        var only = single.RemoveButton(0);
        Assert.Equal(DesktopBarPart.Desktop, single.Hit(only.CenterX, only.CenterY, 1).Part);
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
        public void PageChanged(int desktopIndex) => Calls.Add($"PageChanged:{desktopIndex}");
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

    private static GestureEvent Swipe4(GesturePhase phase, double dxMm, double vx, long t, GestureDirection direction = GestureDirection.Left)
        => new(GestureKind.Swipe, phase, 4, GestureAxis.Horizontal, direction, dxMm, 0, vx, 0, t, t);

    /// <summary>展开好的总览，三个桌面各一页（每页一个窗口），当前第 1 个桌面。</summary>
    private static (OverviewController C, FakeHost Host, long T, OverviewScene[] Pages) OpenedWithPages()
    {
        var (c, host, t) = Opened();
        var pages = Enumerable.Range(0, 3)
            .Select(d => OverviewScene.Build([new OverviewWindow(d + 1, $"w{d}", new RectD(100, 100, 800, 600), false, d == 0, 0, "p")],
                [new OverviewArea(Area, 1.5)]))
            .ToArray();
        c.SetPages(pages, 0);
        return (c, host, t, pages);
    }

    [Fact]
    public void Four_finger_swipe_in_the_overview_pages_to_the_neighbor_and_stays_in_the_overview()
    {
        var (c, host, t, pages) = OpenedWithPages();
        c.DesktopSwipe.Begin(Swipe4(GesturePhase.Began, 0, 0, t));
        c.DesktopSwipe.Update(Swipe4(GesturePhase.Changed, -27, -200, t + 100_000));
        var mid = c.Sample(t + 110_000);
        Assert.Equal(27 / 54.0, mid.PagePosition, 3); // 1:1 跟手（一页 = 总览行程 × 1.8）
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("PageChanged", StringComparison.Ordinal)); // 手指在板上不切

        c.DesktopSwipe.End(Swipe4(GesturePhase.Ended, -27, -200, t + 120_000));
        Assert.Equal("PageChanged:1", host.Calls[^1]); // 松手就切真实桌面
        Assert.Same(pages[1], c.Scene); // 之后收起、选窗口都按新桌面那一页
        Assert.Equal(1, c.Desktops.Current); // 桌面条高亮跟过去

        var settled = c.Sample(t + 3_000_000);
        Assert.Equal(1, settled.PagePosition, 3);
        Assert.True(settled.Visible); // 留在总览里
        Assert.Equal(1, settled.Progress, 3);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("Hide", StringComparison.Ordinal));

        // 换页后照常收起
        c.Close(null, t + 3_000_000);
        c.Sample(t + 6_000_000);
        Assert.Equal("Hide:-", host.Calls[^1]);
    }

    [Fact]
    public void Short_four_finger_swipe_in_the_overview_springs_back_to_the_same_page()
    {
        var (c, host, t, pages) = OpenedWithPages();
        c.DesktopSwipe.Begin(Swipe4(GesturePhase.Began, 0, 0, t));
        c.DesktopSwipe.Update(Swipe4(GesturePhase.Changed, -8, 0, t + 100_000));
        c.DesktopSwipe.End(Swipe4(GesturePhase.Ended, -8, 0, t + 120_000));
        Assert.Equal(0, c.Sample(t + 3_000_000).PagePosition, 3);
        Assert.Same(pages[0], c.Scene);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("PageChanged", StringComparison.Ordinal));
    }

    [Fact]
    public void Four_finger_swipe_past_the_first_desktop_rubber_bands_back()
    {
        var (c, host, t, _) = OpenedWithPages();
        const GestureDirection right = GestureDirection.Right; // 手指向右 = 左边的桌面，第 1 个桌面左边没有
        c.DesktopSwipe.Begin(Swipe4(GesturePhase.Began, 0, 0, t, right));
        c.DesktopSwipe.Update(Swipe4(GesturePhase.Changed, 40, 0, t + 100_000, right));
        var pulled = c.Sample(t + 110_000);
        Assert.InRange(pulled.PagePosition, -0.2, -0.01); // 橡皮筋：跟一点但有阻尼
        c.DesktopSwipe.End(Swipe4(GesturePhase.Ended, 40, 300, t + 120_000, right));
        Assert.Equal(0, c.Sample(t + 3_000_000).PagePosition, 3);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("PageChanged", StringComparison.Ordinal));
    }

    [Fact]
    public void Four_finger_swipe_is_ignored_without_pages()
    {
        var (c, host, t) = Opened(); // 有桌面条但没有多页（例如经典缩略图模式）
        c.DesktopSwipe.Begin(Swipe4(GesturePhase.Began, 0, 0, t));
        c.DesktopSwipe.Update(Swipe4(GesturePhase.Changed, -40, 0, t + 100_000));
        c.DesktopSwipe.End(Swipe4(GesturePhase.Ended, -40, -300, t + 120_000));
        Assert.Equal(0, c.Sample(t + 200_000).PagePosition, 6);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("PageChanged", StringComparison.Ordinal));
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
