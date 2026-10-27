using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Gestures;
using GlideShell.Core.Motion;
using GlideShell.Core.Overview;
using GlideShell.Core.Switcher;

namespace GlideShell.Tests.Switcher;

public class SwitcherLayoutTests
{
    private static readonly RectD Area = new(0, 0, 2560, 1528);

    private static SwitcherLayout Layout(int n) => SwitcherLayout.Create(Enumerable.Range(0, n).Select(i => (1600.0 + 100 * i, 1000.0)).ToList(), Area);

    [Fact]
    public void Item_at_integer_position_is_centered_and_emphasized()
    {
        var layout = Layout(4);
        var r = layout.ItemRect(2, 2.0);

        Assert.Equal(Area.CenterX, r.CenterX, 6);
        Assert.Equal(Area.CenterY, r.CenterY, 6);
        Assert.Equal(layout.ItemHeight * SwitcherLayout.FocusScale, r.Height, 6);
        Assert.Equal(layout.ItemHeight, layout.ItemRect(0, 2.0).Height, 6); // 远处的项不放大
    }

    [Fact]
    public void Strip_moves_continuously_and_keeps_order()
    {
        var layout = Layout(5);
        double prev = double.MaxValue;
        for (double p = -0.3; p <= 4.3; p += 0.05)
        {
            double x = layout.ItemRect(2, p).CenterX;
            Assert.True(x < prev, $"p={p:F2} 时第 2 项没有继续向左移动");
            prev = x;
        }
        for (int i = 1; i < 5; i++) Assert.True(layout.ItemRect(i, 1.5).Left > layout.ItemRect(i - 1, 1.5).Right);
    }

    [Fact]
    public void Nearest_is_clamped_to_valid_items()
    {
        var layout = Layout(3);
        Assert.Equal(0, layout.Nearest(-0.4));
        Assert.Equal(1, layout.Nearest(1.4));
        Assert.Equal(2, layout.Nearest(2.7));
    }
}

public class SwitcherControllerTests
{
    private sealed class FakeHost : ISwitcherHost
    {
        public readonly List<string> Calls = [];
        public void Prewarm() => Calls.Add("Prewarm");
        public void CancelPrewarm() => Calls.Add("CancelPrewarm");
        public void Show() => Calls.Add("Show");
        public void Hide(long? activateWindowId) => Calls.Add($"Hide:{activateWindowId?.ToString() ?? "-"}");
    }

    private const double MmPerWindow = 20;

    private static (SwitcherController C, FakeHost Host) Create(int windows = 4)
    {
        var host = new FakeHost();
        var c = new SwitcherController(host, () => new SwitcherConfig { MmPerWindow = MmPerWindow });
        var list = Enumerable.Range(0, windows)
            .Select(i => new OverviewWindow(100 + i, $"W{i}", new RectD(0, 0, 1600, 1000), false, i == 0, 0, "p"))
            .ToList();
        c.SetScene(new SwitcherScene(list, SwitcherLayout.Create(list.Select(w => (w.Source.Width, w.Source.Height)).ToList(), new RectD(0, 0, 2560, 1528))), 0);
        return (c, host);
    }

    private static GestureEvent Ev(GesturePhase phase, double dx, double vx, long t)
        => new(GestureKind.Swipe, phase, 3, GestureAxis.Horizontal, dx <= 0 ? GestureDirection.Left : GestureDirection.Right, dx, 0, vx, 0, t, t);

    private static long Swipe(SwitcherController c, double dx, double releaseVelocity, long start)
    {
        c.Prepare(start);
        c.Begin(Ev(GesturePhase.Began, 0, 0, start));
        for (int k = 1; k <= 10; k++) c.Update(Ev(GesturePhase.Changed, dx * k / 10, releaseVelocity, start + k * 10_000));
        long end = start + 110_000;
        c.End(Ev(GesturePhase.Ended, dx, releaseVelocity, end));
        return end;
    }

    [Fact]
    public void Swiping_left_moves_to_later_windows_one_to_one()
    {
        var (c, _) = Create();
        c.Begin(Ev(GesturePhase.Began, 0, 0, 0));
        c.Update(Ev(GesturePhase.Changed, -30, 0, 10_000)); // 30mm = 1.5 个窗口

        var f = c.Sample(20_000);
        Assert.Equal(1.5, f.Position, 9);
        Assert.Equal(TransitionPhase.Tracking, f.Phase);
    }

    [Fact]
    public void Release_snaps_to_nearest_window_and_activates_it()
    {
        var (c, host) = Create();
        long end = Swipe(c, dx: -42, releaseVelocity: 0, start: 0); // 2.1 个窗口

        var mid = c.Sample(end + 80_000);
        Assert.Equal(2, mid.LaunchIndex); // 松手就开始飞回窗口原位（与吸附同时），还没切换
        Assert.InRange(mid.Launch, 0.05, 0.99);
        Assert.Equal(new[] { "Prewarm", "Show" }, host.Calls);

        var last = c.Sample(end + 5_000_000);
        Assert.Equal(2, last.Position);
        Assert.Equal(1, last.Launch, 6); // 落定这一帧仍按飞回终点画
        Assert.True(last.Visible);
        Assert.Equal(new[] { "Prewarm", "Show", "Hide:102" }, host.Calls);
    }

    private sealed class LaunchHost : ISwitcherHost
    {
        public readonly List<long> Launched = [];
        public void Prewarm() { }
        public void CancelPrewarm() { }
        public void Show() { }
        public void Hide(long? activateWindowId) { }
        public void Launching(long windowId) => Launched.Add(windowId);
    }

    [Fact]
    public void Release_tells_the_host_which_window_is_launching_so_minimized_ones_can_restore_early()
    {
        var host = new LaunchHost();
        var c = new SwitcherController(host, () => new SwitcherConfig { MmPerWindow = MmPerWindow });
        var list = Enumerable.Range(0, 4).Select(i => new OverviewWindow(100 + i, "", new RectD(0, 0, 1600, 1000), false, false, 0, "")).ToList();
        c.SetScene(new SwitcherScene(list, SwitcherLayout.Create(list.Select(w => (w.Source.Width, w.Source.Height)).ToList(), new RectD(0, 0, 2560, 1528))), 0);
        Swipe(c, dx: -42, releaseVelocity: 0, start: 0);
        Assert.Equal([102L], host.Launched); // 松手就通知（不等落定）
        c.Sample(5_000_000); // 落定、撤层

        Swipe(c, dx: -4, releaseVelocity: 0, start: 10_000_000); // 回到当前窗口：不用通知
        Assert.Single(host.Launched);
    }

    [Fact]
    public void A_new_swipe_during_launch_takes_over_from_the_current_position()
    {
        var (c, host) = Create();
        long end = Swipe(c, dx: -42, releaseVelocity: 0, start: 0);
        c.Sample(end + 50_000); // 飞回途中
        c.Begin(Ev(GesturePhase.Began, 0, 0, end + 60_000));
        var f = c.Sample(end + 70_000);
        Assert.Equal(-1, f.LaunchIndex);
        Assert.Equal(0, f.Launch);
        Assert.InRange(f.Position, 1.5, 2.2);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("Hide"));
    }

    [Fact]
    public void Small_swipe_returns_to_current_window_without_switching()
    {
        var (c, host) = Create();
        long end = Swipe(c, dx: -6, releaseVelocity: 0, start: 0);

        c.Sample(end + 5_000_000);
        c.Sample(end + 10_000_000);
        Assert.Equal("Hide:-", host.Calls[^1]);
    }

    [Fact]
    public void Flick_goes_further_than_the_finger_and_is_clamped_to_the_last_window()
    {
        var (c, host) = Create(windows: 3);
        long end = Swipe(c, dx: -10, releaseVelocity: -400, start: 0); // 投影 0.5 + 20 × 0.499 ≫ 2

        c.Sample(end + 5_000_000);
        c.Sample(end + 10_000_000);
        Assert.Equal("Hide:102", host.Calls[^1]);
    }

    [Fact]
    public void Overscroll_past_the_ends_is_rubber_banded()
    {
        var (c, _) = Create(windows: 2);
        c.Begin(Ev(GesturePhase.Began, 0, 0, 0));
        c.Update(Ev(GesturePhase.Changed, 40, 0, 10_000)); // 向右 2 个窗口：越过第 0 个

        double p = c.Sample(20_000).Position;
        Assert.InRange(p, -0.35, 0);
    }

    [Fact]
    public void Scene_arriving_after_begin_still_bounds_the_position()
    {
        var host = new FakeHost();
        var c = new SwitcherController(host, () => new SwitcherConfig { MmPerWindow = MmPerWindow });
        c.Begin(Ev(GesturePhase.Began, 0, 0, 0));
        c.Update(Ev(GesturePhase.Changed, -100, 0, 10_000)); // 场景还没来：范围是 [0,0]

        var list = Enumerable.Range(0, 3).Select(i => new OverviewWindow(100 + i, "", new RectD(0, 0, 1600, 1000), false, false, 0, "")).ToList();
        c.SetScene(new SwitcherScene(list, SwitcherLayout.Create(list.Select(w => (w.Source.Width, w.Source.Height)).ToList(), new RectD(0, 0, 2560, 1528))), 20_000);
        c.Update(Ev(GesturePhase.Changed, -100, 0, 30_000));

        Assert.InRange(c.Sample(30_000).Position, 2, 2.35);
    }

    [Fact]
    public void Cancelled_gesture_never_switches()
    {
        var (c, host) = Create();
        c.Begin(Ev(GesturePhase.Began, 0, 0, 0));
        c.Update(Ev(GesturePhase.Changed, -60, 0, 10_000));
        c.End(Ev(GesturePhase.Cancelled, -60, -300, 20_000));

        c.Sample(5_000_000);
        c.Sample(10_000_000);
        Assert.Equal("Hide:-", host.Calls[^1]);
    }
}
