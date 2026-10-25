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
