using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;

namespace GlideShell.Tests.Overview;

public class OverviewLayoutTests
{
    private static readonly RectD Area = new(0, 0, 2560, 1528); // 本机工作区（任务栏之外）

    private static void AssertValid(IReadOnlyList<RectD> sources, LayoutSlot[] slots, RectD area)
    {
        Assert.Equal(sources.Count, slots.Length);
        for (int i = 0; i < slots.Length; i++)
        {
            var t = slots[i].Tile;
            Assert.True(t.Left >= area.Left - 0.5 && t.Right <= area.Right + 0.5 && t.Top >= area.Top - 0.5 && slots[i].Label.Bottom <= area.Bottom + 0.5,
                $"窗口 {i} 越出工作区：{t}");
            Assert.Equal(sources[i].Width / sources[i].Height, t.Width / t.Height, 6); // 纵横比不变
            for (int j = i + 1; j < slots.Length; j++)
            {
                var u = slots[j].Tile;
                bool overlap = t.Left < u.Right - 0.5 && u.Left < t.Right - 0.5 && t.Top < u.Bottom - 0.5 && u.Top < t.Bottom - 0.5;
                Assert.False(overlap, $"窗口 {i} 与 {j} 重叠：{t} / {u}");
            }
        }
    }

    [Fact]
    public void Single_maximized_window_still_visibly_shrinks_and_is_centered()
    {
        var sources = new[] { new RectD(0, 0, 2560, 1528) };
        var slot = Assert.Single(OverviewLayout.Compute(sources, Area));

        Assert.True(slot.Tile.Width <= 2560 * 0.65 + 0.5);
        Assert.Equal(Area.CenterX, slot.Tile.CenterX, 3);
        AssertValid(sources, [slot], Area);
    }

    [Fact]
    public void Side_by_side_windows_keep_their_left_right_order()
    {
        var sources = new[] { new RectD(1300, 100, 1200, 1300), new RectD(20, 120, 1200, 1300) };
        var slots = OverviewLayout.Compute(sources, Area);

        AssertValid(sources, slots, Area);
        Assert.True(slots[1].Tile.CenterX < slots[0].Tile.CenterX);
    }

    [Fact]
    public void Larger_windows_stay_larger()
    {
        var sources = new[] { new RectD(0, 0, 2000, 1400), new RectD(300, 300, 800, 600) };
        var slots = OverviewLayout.Compute(sources, Area);

        AssertValid(sources, slots, Area);
        Assert.True(slots[0].Tile.Width > slots[1].Tile.Width);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(24)]
    public void Random_window_sets_fit_without_overlap(int count)
    {
        var rng = new Random(count * 7919);
        var sources = Enumerable.Range(0, count).Select(_ =>
        {
            double w = rng.Next(400, 2560), h = rng.Next(300, 1528);
            return new RectD(rng.Next(0, (int)(2560 - w) + 1), rng.Next(0, (int)(1528 - h) + 1), w, h);
        }).ToArray();

        AssertValid(sources, OverviewLayout.Compute(sources, Area), Area);
    }

    [Fact]
    public void Works_on_a_secondary_monitor_with_offset_origin()
    {
        var area = new RectD(2560, -200, 1920, 1040);
        var sources = new[] { new RectD(2600, -150, 1000, 800), new RectD(3500, 0, 900, 700) };

        AssertValid(sources, OverviewLayout.Compute(sources, area), area);
    }

    [Fact]
    public void Visuals_are_linear_while_tracking_and_bounded()
    {
        var src = new RectD(0, 0, 1000, 800);
        var tile = new RectD(500, 400, 500, 400);

        Assert.Equal(src, OverviewVisuals.WindowRect(src, tile, 0));
        Assert.Equal(tile, OverviewVisuals.WindowRect(src, tile, 1));
        Assert.Equal(new RectD(250, 200, 750, 600), OverviewVisuals.WindowRect(src, tile, 0.5));
        Assert.Equal(0.85, OverviewVisuals.BackdropOpacity(1.2));
        Assert.Equal(0, OverviewVisuals.LabelOpacity(0.5));
        Assert.Equal(1, OverviewVisuals.LabelOpacity(1));
    }
}

public class OverviewNavigationTests
{
    // 四个窗口分别在屏幕的四个象限：就近布局排成 2×2
    private static OverviewScene Grid()
    {
        var windows = new[]
        {
            new OverviewWindow(1, "左上", new RectD(0, 0, 1200, 700), false, true, 0, "a"),
            new OverviewWindow(2, "右上", new RectD(1300, 0, 1200, 700), false, false, 0, "b"),
            new OverviewWindow(3, "左下", new RectD(0, 800, 1200, 700), false, false, 0, "c"),
            new OverviewWindow(4, "右下", new RectD(1300, 800, 1200, 700), false, false, 0, "d"),
        };
        return OverviewScene.Build(windows, [new OverviewArea(new RectD(0, 0, 2560, 1528), 1.5)]);
    }

    [Fact]
    public void Arrow_keys_move_to_the_nearest_window_in_that_direction()
    {
        var scene = Grid();
        Assert.Equal(1, scene.Navigate(0, NavigationDirection.Right));
        Assert.Equal(2, scene.Navigate(0, NavigationDirection.Down));
        Assert.Equal(3, scene.Navigate(1, NavigationDirection.Down));
        Assert.Equal(2, scene.Navigate(3, NavigationDirection.Left));
        Assert.Equal(0, scene.Navigate(2, NavigationDirection.Up));
    }

    [Fact]
    public void Arrow_keys_stay_put_at_the_edge_and_start_from_the_top_window()
    {
        var scene = Grid();
        Assert.Equal(0, scene.Navigate(0, NavigationDirection.Left));
        Assert.Equal(0, scene.Navigate(0, NavigationDirection.Up));
        Assert.Equal(0, scene.Navigate(-1, NavigationDirection.Right));
        Assert.Equal(-1, OverviewScene.Empty.Navigate(0, NavigationDirection.Right));
    }

    [Fact]
    public void Tab_cycles_in_reading_order()
    {
        var scene = Grid();
        Assert.Equal(new[] { 1, 2, 3, 0 }, new[] { 0, 1, 2, 3 }.Select(i => scene.Cycle(i, backwards: false)));
        Assert.Equal(3, scene.Cycle(0, backwards: true));
        Assert.Equal(0, scene.Cycle(-1, backwards: false));
    }
}

public class MultiMonitorSceneTests
{
    // 左：2560×1600 @150%（工作区去掉任务栏）；右：1920×1080 @100%，在左屏右边
    private static readonly OverviewArea Left = new(new RectD(0, 0, 2560, 1528), 1.5);
    private static readonly OverviewArea Right = new(new RectD(2560, 0, 1920, 1032), 1.0);

    private static List<OverviewWindow> Windows() =>
    [
        new(1, "左 A", new RectD(100, 100, 1200, 800), false, true, 0, "a"),
        new(2, "左 B", new RectD(1300, 600, 1100, 800), false, false, 0, "b"),
        new(3, "右 C", new RectD(2700, 100, 900, 700), false, false, 1, "c"),
        new(4, "右 D", new RectD(3600, 300, 800, 600), true, false, 1, "d"), // 最小化窗口照样按所在屏排
    ];

    [Fact]
    public void Each_monitor_lays_out_its_own_windows_inside_its_work_area()
    {
        var scene = OverviewScene.Build(Windows(), [Left, Right]);

        for (int i = 0; i < scene.Count; i++)
        {
            var area = scene.Windows[i].MonitorIndex == 0 ? Left.WorkArea : Right.WorkArea;
            var tile = scene.Slots[i].Tile;
            Assert.True(tile.Left >= area.Left && tile.Right <= area.Right && tile.Top >= area.Top && tile.Bottom <= area.Bottom,
                $"{scene.Windows[i].Title} 的平铺位 {tile} 超出了所在屏的工作区 {area}");
        }
    }

    [Fact]
    public void Primary_only_puts_every_window_on_the_primary_monitor()
    {
        var scene = OverviewScene.Build(Windows(), [Left, Right], primaryOnly: true, primaryIndex: 0);

        Assert.All(scene.Slots, s => Assert.True(s.Tile.Right <= Left.WorkArea.Right && s.Tile.Left >= Left.WorkArea.Left));
    }

    [Fact]
    public void Monitor_index_out_of_range_falls_back_to_an_existing_monitor()
    {
        var windows = Windows();
        windows[2] = windows[2] with { MonitorIndex = 5 }; // 显示器拔掉后残留的编号
        var scene = OverviewScene.Build(windows, [Left, Right]);

        Assert.Equal(4, scene.Count);
        Assert.All(scene.Slots, s => Assert.True(s.Tile.Width > 0 && s.Tile.Height > 0));
    }

    [Fact]
    public void Arrow_keys_cross_to_the_next_monitor()
    {
        var scene = OverviewScene.Build(Windows(), [Left, Right]);
        int rightmostOnLeft = Enumerable.Range(0, scene.Count).Where(i => scene.Windows[i].MonitorIndex == 0)
            .MaxBy(i => scene.Slots[i].Tile.CenterX);

        int next = scene.Navigate(rightmostOnLeft, NavigationDirection.Right);

        Assert.Equal(1, scene.Windows[next].MonitorIndex);
    }
}
