using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Gestures;
using GlideShell.Core.Motion;
using GlideShell.Core.Overview;

namespace GlideShell.Tests.Overview;

public class OverviewControllerTests
{
    private sealed class FakeHost : IOverviewHost
    {
        public readonly List<string> Calls = [];
        public void Prewarm() => Calls.Add("Prewarm");
        public void CancelPrewarm() => Calls.Add("CancelPrewarm");
        public void Show() => Calls.Add("Show");
        public void Activate() => Calls.Add("Activate");
        public void Hide(long? focusWindowId) => Calls.Add($"Hide:{focusWindowId?.ToString() ?? "-"}");
    }

    private const double Travel = 30;

    private static (OverviewController C, FakeHost Host) Create()
    {
        var host = new FakeHost();
        return (new OverviewController(host, () => new OverviewConfig { TravelMm = Travel }), host);
    }

    private static GestureEvent Ev(GesturePhase phase, GestureDirection dir, double upMm, double upVelocity, long t)
        => new(GestureKind.Swipe, phase, 3, GestureAxis.Vertical, dir, 0, -upMm, 0, -upVelocity, t, t);

    /// <summary>一次竖直手势：从 0 滑到 upMm（相对开始点，向上为正），以 releaseVelocity（mm/s）松手。</summary>
    private static long Swipe(OverviewController c, double upMm, double releaseVelocity, long start, GestureDirection? dir = null)
    {
        var d = dir ?? (upMm >= 0 ? GestureDirection.Up : GestureDirection.Down);
        c.Prepare(start);
        c.Begin(Ev(GesturePhase.Began, d, 0, 0, start));
        for (int k = 1; k <= 10; k++) c.Update(Ev(GesturePhase.Changed, d, upMm * k / 10, releaseVelocity, start + k * 10_000));
        long end = start + 110_000;
        c.End(Ev(GesturePhase.Ended, d, upMm, releaseVelocity, end));
        return end;
    }

    [Fact]
    public void Prewarms_when_fingers_land_and_cancels_when_it_was_not_an_overview()
    {
        var (c, host) = Create();
        c.Prepare(0);
        c.Unprepare(10);
        Assert.Equal(new[] { "Prewarm", "CancelPrewarm" }, host.Calls);
    }

    [Fact]
    public void Swipe_up_tracks_one_to_one_and_opens_after_release()
    {
        var (c, host) = Create();
        c.Prepare(0);
        c.Begin(Ev(GesturePhase.Began, GestureDirection.Up, 0, 0, 0));
        c.Update(Ev(GesturePhase.Changed, GestureDirection.Up, 18, 0, 50_000));
        Assert.Equal(0.6, c.Sample(60_000).Progress, 9); // 18mm / 30mm

        c.End(Ev(GesturePhase.Ended, GestureDirection.Up, 18, 0, 100_000));
        var final = c.Sample(5_000_000);
        Assert.Equal(1, final.Progress);
        Assert.Equal(new[] { "Prewarm", "Show", "Activate" }, host.Calls);
    }

    [Fact]
    public void Holding_at_full_extent_never_commits_until_release()
    {
        var (c, host) = Create();
        c.Begin(Ev(GesturePhase.Began, GestureDirection.Up, 0, 0, 0));
        c.Update(Ev(GesturePhase.Changed, GestureDirection.Up, Travel, 0, 50_000));

        var held = c.Sample(10_000_000); // 停在顶端 10 秒
        Assert.Equal(TransitionPhase.Tracking, held.Phase);
        Assert.DoesNotContain("Activate", host.Calls);
    }

    [Fact]
    public void Reversing_before_release_cancels_and_hides()
    {
        var (c, host) = Create();
        c.Begin(Ev(GesturePhase.Began, GestureDirection.Up, 0, 0, 0));
        c.Update(Ev(GesturePhase.Changed, GestureDirection.Up, 24, 0, 50_000));
        c.Update(Ev(GesturePhase.Changed, GestureDirection.Up, 6, 0, 100_000));
        Assert.Equal(0.2, c.Sample(100_000).Progress, 9);

        c.End(Ev(GesturePhase.Ended, GestureDirection.Up, 6, 0, 120_000));
        Assert.Equal(0, c.Sample(5_000_000).Progress);
        Assert.Equal("Hide:-", host.Calls[^1]);
        Assert.False(c.IsVisible);
    }

    [Fact]
    public void Quick_flick_opens_from_a_short_distance()
    {
        var (c, _) = Create();
        long end = Swipe(c, upMm: 6, releaseVelocity: 150, start: 0); // 0.2 + 5 × 0.499 投影远超一半
        Assert.Equal(1, c.Sample(end + 5_000_000).Progress);
    }

    [Fact]
    public void Swipe_down_while_hidden_is_not_an_overview_gesture()
    {
        var (c, host) = Create();
        Swipe(c, upMm: -20, releaseVelocity: -100, start: 0);

        Assert.Equal(new[] { "Prewarm", "CancelPrewarm" }, host.Calls);
        Assert.False(c.IsVisible);
    }

    [Fact]
    public void Swipe_down_from_open_closes_and_focuses_the_hovered_window()
    {
        var (c, host) = Create();
        long t = Swipe(c, upMm: 25, releaseVelocity: 0, start: 0);
        t += 5_000_000;
        c.Sample(t);
        c.SetHovered(42);

        t = Swipe(c, upMm: -22, releaseVelocity: 0, start: t);
        var closing = c.Sample(t + 50_000);
        Assert.Equal(42, closing.EmphasizedWindow); // 收起途中指针下的窗口放大
        c.Sample(t + 5_000_000);

        Assert.Equal("Hide:42", host.Calls[^1]);
    }

    [Fact]
    public void New_gesture_takes_over_a_running_animation_without_reshowing()
    {
        var (c, host) = Create();
        long t = Swipe(c, upMm: 20, releaseVelocity: 0, start: 0);
        double mid = c.Sample(t + 60_000).Progress;
        Assert.InRange(mid, 0.67, 0.99);

        c.Begin(Ev(GesturePhase.Began, GestureDirection.Down, 0, 0, t + 60_000));
        Assert.Equal(mid, c.Sample(t + 60_000).Progress, 9);
        Assert.Single(host.Calls, "Show");
    }

    [Fact]
    public void Cancelled_gesture_returns_to_where_it_started()
    {
        var (c, host) = Create();
        c.Begin(Ev(GesturePhase.Began, GestureDirection.Up, 0, 0, 0));
        c.Update(Ev(GesturePhase.Changed, GestureDirection.Up, 27, 0, 50_000));
        c.End(Ev(GesturePhase.Cancelled, GestureDirection.Up, 27, 300, 60_000));

        Assert.Equal(0, c.Sample(5_000_000).Progress);
        Assert.Equal("Hide:-", host.Calls[^1]);
    }

    [Fact]
    public void Toggle_opens_and_closes_without_gestures()
    {
        var (c, host) = Create();
        c.Toggle(0);
        Assert.Equal(1, c.Sample(5_000_000).Progress);
        c.Toggle(5_000_000);
        Assert.Equal(0, c.Sample(10_000_000).Progress);
        Assert.Equal(new[] { "Prewarm", "Show", "Activate", "Hide:-" }, host.Calls);
    }

    [Fact]
    public void Window_rects_interpolate_between_source_and_tile()
    {
        var windows = new[]
        {
            new OverviewWindow(1, "A", new RectD(0, 0, 1280, 1500), false, true, 0, "a"),
            new OverviewWindow(2, "B", new RectD(1280, 0, 1280, 1500), false, false, 0, "b"),
        };
        var scene = OverviewScene.Build(windows, [new OverviewArea(new RectD(0, 0, 2560, 1528), 1.5)]);
        var rects = new RectD[2];

        OverviewController.WindowRects(scene, new OverviewFrame(0, 0, 0, TransitionPhase.Resting, true, null), rects);
        Assert.Equal(windows[0].Source, rects[0]);

        OverviewController.WindowRects(scene, new OverviewFrame(1, 0, 0, TransitionPhase.Resting, true, null), rects);
        Assert.Equal(scene.Slots[1].Tile, rects[1]);
        Assert.Equal(1, scene.HitTest(rects[1].CenterX, rects[1].CenterY, 1));
    }
}
