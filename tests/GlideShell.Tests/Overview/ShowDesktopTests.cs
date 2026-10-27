using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Gestures;
using GlideShell.Core.Overview;
using GlideShell.Core.Pointer;

namespace GlideShell.Tests.Overview;

/// <summary>1.3：显示桌面、按程序分组、触发角、Ctrl+Win+方向键翻页。</summary>
public class ShowDesktopTests
{
    private static readonly RectD Area = new(0, 0, 2560, 1528);

    private sealed class FakeHost : IOverviewHost
    {
        public readonly List<string> Calls = [];
        public void Prewarm() => Calls.Add("Prewarm");
        public void CancelPrewarm() => Calls.Add("CancelPrewarm");
        public void Show() => Calls.Add("Show");
        public void Activate() => Calls.Add("Activate");
        public void Closing(long? focusWindowId) => Calls.Add($"Closing:{focusWindowId?.ToString() ?? "-"}");
        public void Hide(long? focusWindowId) => Calls.Add($"Hide:{focusWindowId?.ToString() ?? "-"}");
        public void SwitchDesktop(int desktopIndex) => Calls.Add($"SwitchDesktop:{desktopIndex}");
        public void PageChanged(int desktopIndex) => Calls.Add($"PageChanged:{desktopIndex}");
    }

    private static GestureEvent Ev(GesturePhase phase, GestureDirection dir, double downMm, double downVelocity, long t)
        => new(GestureKind.Swipe, phase, 4, GestureAxis.Vertical, dir, 0, downMm, 0, downVelocity, t, t);

    private static long Swipe(IGestureTarget target, double downMm, double velocity, long start)
    {
        var dir = downMm >= 0 ? GestureDirection.Down : GestureDirection.Up;
        target.Prepare(start);
        target.Begin(Ev(GesturePhase.Began, dir, 0, 0, start));
        for (int k = 1; k <= 10; k++) target.Update(Ev(GesturePhase.Changed, dir, downMm * k / 10, velocity, start + k * 10_000));
        long end = start + 110_000;
        target.End(Ev(GesturePhase.Ended, dir, downMm, velocity, end));
        return end;
    }

    private static (OverviewController C, FakeHost Host) Create(bool desktopShown = false)
    {
        var host = new FakeHost();
        var c = new OverviewController(host, () => new OverviewConfig { TravelMm = 30 }) { DesktopShown = () => desktopShown };
        return (c, host);
    }

    // ---------- 控制器 ----------

    [Fact]
    public void Four_finger_down_shows_the_desktop_tracking_the_fingers_and_activates_when_settled()
    {
        var (c, host) = Create();
        var g = c.DesktopGesture;
        g.Prepare(0);
        g.Begin(Ev(GesturePhase.Began, GestureDirection.Down, 0, 0, 0));
        Assert.Equal(OverviewMode.Desktop, c.Mode);
        g.Update(Ev(GesturePhase.Changed, GestureDirection.Down, 15, 100, 50_000));
        Assert.Equal(0.5, c.Sample(60_000).Progress, 3); // 1:1 跟手
        g.End(Ev(GesturePhase.Ended, GestureDirection.Down, 15, 100, 70_000));
        c.Sample(5_000_000);
        Assert.Equal("Activate", host.Calls[^1]); // 宿主在这里真正最小化窗口并撤层

        c.EndDesktop();
        Assert.False(c.IsVisible);
        Assert.False(c.Sample(5_100_000).Visible);
    }

    [Fact]
    public void Four_finger_up_pulls_the_windows_back_when_the_desktop_is_shown()
    {
        var (c, host) = Create(desktopShown: true);
        var g = c.DesktopGesture;
        g.Prepare(0);
        g.Begin(Ev(GesturePhase.Began, GestureDirection.Up, 0, 0, 0));
        Assert.Equal(OverviewMode.Desktop, c.Mode);
        Assert.Equal(1, c.Sample(10_000).Progress, 3); // 从「窗口全在屏幕外」开始
        g.Update(Ev(GesturePhase.Changed, GestureDirection.Up, -15, -100, 50_000));
        Assert.Equal(0.5, c.Sample(60_000).Progress, 3);
        g.End(Ev(GesturePhase.Ended, GestureDirection.Up, -15, -100, 70_000));
        Assert.Equal("Closing:-", host.Calls[^1]); // 宿主立刻开始还原窗口
        c.Sample(5_000_000);
        Assert.Equal("Hide:-", host.Calls[^1]);
    }

    [Fact]
    public void Four_finger_up_opens_the_overview_when_the_desktop_is_not_shown()
    {
        var (c, _) = Create();
        long t = Swipe(c.DesktopGesture, -25, 0, 0);
        Assert.Equal(OverviewMode.All, c.Mode);
        Assert.Equal(1, c.Sample(t + 5_000_000).Progress, 3);
    }

    [Fact]
    public void Hot_corner_toggles_the_desktop_without_gestures()
    {
        var (c, host) = Create();
        c.ToggleDesktop(0);
        Assert.Equal(OverviewMode.Desktop, c.Mode);
        c.OnShown(0);
        c.Sample(3_000_000);
        Assert.Equal("Activate", host.Calls[^1]);
        c.EndDesktop();

        c.DesktopShown = () => true;
        c.ToggleDesktop(4_000_000);
        c.OnShown(4_000_000);
        Assert.Equal("Closing:-", host.Calls[^1]); // 拉回：一显示就开始还原
        c.Sample(8_000_000);
        Assert.Equal("Hide:-", host.Calls[^1]);
    }

    [Fact]
    public void Hot_corner_opens_the_app_view()
    {
        var (c, _) = Create();
        c.Open(0, OverviewMode.App);
        Assert.Equal(OverviewMode.App, c.Mode);
    }

    [Fact]
    public void Keyboard_steps_pages_in_the_overview()
    {
        var (c, host) = Create();
        var pages = Enumerable.Range(0, 3)
            .Select(d => OverviewScene.Build([new OverviewWindow(d + 1, $"w{d}", new RectD(100, 100, 800, 600), false, d == 0, 0, "p")], [new OverviewArea(Area, 1.5)]))
            .ToArray();
        Assert.False(c.StepPage(1, 0)); // 总览没开
        c.SetScene(pages[0]);
        c.Open(0);
        c.OnShown(0);
        c.Sample(2_000_000);
        c.SetPages(pages, 0);
        Assert.True(c.StepPage(1, 2_000_000));
        Assert.Equal("PageChanged:1", host.Calls[^1]);
        Assert.Equal(1, c.Sample(5_000_000).PagePosition, 3);
        Assert.True(c.StepPage(1, 5_000_000));
        Assert.True(c.StepPage(1, 5_500_000)); // 到头了：处理了但不翻
        Assert.Equal(2, c.Page);
    }

    // ---------- 场景 ----------

    [Fact]
    public void Pushing_aside_moves_each_window_fully_off_the_nearest_edge()
    {
        var scene = OverviewScene.Build(
        [
            new OverviewWindow(1, "left", new RectD(100, 500, 600, 400), false, true, 0, "p"),
            new OverviewWindow(2, "top", new RectD(1000, 50, 600, 300), false, false, 0, "p"),
            new OverviewWindow(3, "min", new RectD(1000, 600, 600, 400), true, false, 0, "p"),
            new OverviewWindow(4, "right-bottom", new RectD(1900, 1000, 600, 500), false, false, 0, "p"),
        ], [new OverviewArea(Area, 1.5)]);
        var pushed = scene.PushAside([new OverviewArea(Area, 1.5)]);
        var t0 = pushed.Slots[0].Tile;
        Assert.True(t0.Right <= Area.Left); // 往左推出
        Assert.Equal(scene.Windows[0].Source.Top, t0.Top);
        Assert.True(pushed.Slots[1].Tile.Bottom <= Area.Top); // 往上
        Assert.True(pushed.IsExcluded(2)); // 最小化的不在桌面上
        Assert.True(pushed.Slots[3].Tile.Left >= Area.Right || pushed.Slots[3].Tile.Top >= Area.Bottom);
        Assert.Equal(scene.Windows[0].Source.Width, t0.Width); // 大小不变
        Assert.Equal(scene.Windows[0].Source.Height, t0.Height);
    }

    [Fact]
    public void Grouping_by_app_puts_windows_of_the_same_app_next_to_each_other()
    {
        // a、b、a、b 交错分布在一排：就近排是 a b a b；按程序分组后两个 a 挨着
        var windows = new[]
        {
            new OverviewWindow(1, "a1", new RectD(0, 500, 500, 400), false, true, 0, "a"),
            new OverviewWindow(2, "b1", new RectD(600, 500, 500, 400), false, false, 0, "b"),
            new OverviewWindow(3, "a2", new RectD(1200, 500, 500, 400), false, false, 0, "a"),
            new OverviewWindow(4, "b2", new RectD(1800, 500, 500, 400), false, false, 0, "b"),
        };
        var plain = OverviewScene.Build(windows, [new OverviewArea(Area, 1.5)]);
        var grouped = OverviewScene.Build(windows, [new OverviewArea(Area, 1.5)], groupByApp: true);
        static int[] Order(OverviewScene s) => Enumerable.Range(0, s.Count).OrderBy(i => s.Slots[i].Tile.CenterY).ThenBy(i => s.Slots[i].Tile.CenterX).ToArray();
        Assert.Equal(new[] { 0, 1, 2, 3 }, Order(plain));
        var order = Order(grouped);
        Assert.Equal(1, Math.Abs(Array.IndexOf(order, 0) - Array.IndexOf(order, 2))); // 两个 a 挨着
        Assert.Equal(1, Math.Abs(Array.IndexOf(order, 1) - Array.IndexOf(order, 3))); // 两个 b 挨着
    }

    // ---------- 触发角 ----------

    private static readonly HotCornersConfig Corners = new() { TopLeft = HotCornerAction.Overview, BottomRight = HotCornerAction.ShowDesktop, DwellMs = 100 };

    [Fact]
    public void Hot_corner_fires_once_after_dwelling_and_rearms_after_leaving()
    {
        var d = new HotCornerDetector();
        var screens = new[] { new RectD(0, 0, 2560, 1600) };
        Assert.Equal(HotCornerAction.None, d.Update(0, 0, false, 0, screens, Corners));
        Assert.Equal(HotCornerAction.None, d.Update(0, 0, false, 50_000, screens, Corners)); // 还没停够
        Assert.Equal(HotCornerAction.Overview, d.Update(1, 0, false, 120_000, screens, Corners));
        Assert.Equal(HotCornerAction.None, d.Update(0, 0, false, 300_000, screens, Corners)); // 一次停留只触发一次
        d.Update(200, 200, false, 400_000, screens, Corners); // 离开
        d.Update(0, 0, false, 500_000, screens, Corners);
        Assert.Equal(HotCornerAction.Overview, d.Update(0, 0, false, 650_000, screens, Corners));

        Assert.Equal(HotCornerAction.None, d.Update(2559, 0, false, 900_000, screens, Corners)); // 右上角没设
        var e = new HotCornerDetector();
        e.Update(2559, 1599, true, 0, screens, Corners);
        Assert.Equal(HotCornerAction.None, e.Update(2559, 1599, true, 500_000, screens, Corners)); // 按着鼠标键（在拖东西）
        e.Update(2559, 1599, false, 600_000, screens, Corners);
        Assert.Equal(HotCornerAction.ShowDesktop, e.Update(2559, 1599, false, 800_000, screens, Corners));
    }

    [Fact]
    public void Corners_where_two_screens_meet_are_not_hot()
    {
        var screens = new[] { new RectD(0, 0, 2560, 1600), new RectD(2560, 0, 1920, 1080) };
        Assert.Equal((-1, -1), HotCornerDetector.Find(2559, 0, screens)); // 主屏右上角：右边接着副屏，指针会滑过去
        Assert.Equal((1, 1), HotCornerDetector.Find(4479, 0, screens)); // 副屏右上角
        Assert.Equal((0, 3), HotCornerDetector.Find(2559, 1599, screens)); // 主屏右下角：副屏比它矮，这里挡得住
    }
}
