using GlideShell.Core.Configuration;
using GlideShell.Core.Desktops;
using GlideShell.Core.Gestures;
using GlideShell.Core.Motion;

namespace GlideShell.Tests.Desktops;

public class DesktopSwitchControllerTests
{
    private sealed class FakeHost : IDesktopSwitchHost
    {
        public readonly List<string> Calls = [];
        public void Prewarm() => Calls.Add("Prewarm");
        public void CancelPrewarm() => Calls.Add("CancelPrewarm");
        public void Show() => Calls.Add("Show");
        public void Commit(int offset) => Calls.Add($"Commit:{offset}");
        public void Hide(int committedOffset) => Calls.Add($"Hide:{committedOffset}");
    }

    private const double Travel = 50;

    private static (DesktopSwitchController C, FakeHost Host) Create(bool left = true, bool right = true)
    {
        var host = new FakeHost();
        var c = new DesktopSwitchController(host, () => new DesktopConfig { TravelMm = Travel });
        c.SetNeighbors(left, right, 0);
        return (c, host);
    }

    private static GestureEvent Ev(GesturePhase phase, double dx, double vx, long t)
        => new(GestureKind.Swipe, phase, 4, GestureAxis.Horizontal, dx <= 0 ? GestureDirection.Left : GestureDirection.Right, dx, 0, vx, 0, t, t);

    private static long Swipe(DesktopSwitchController c, double dx, double v, long start)
    {
        c.Prepare(start);
        c.Begin(Ev(GesturePhase.Began, 0, 0, start));
        for (int k = 1; k <= 10; k++) c.Update(Ev(GesturePhase.Changed, dx * k / 10, v, start + k * 10_000));
        long end = start + 110_000;
        c.End(Ev(GesturePhase.Ended, dx, v, end));
        return end;
    }

    [Fact]
    public void Fingers_left_slide_the_right_desktop_in_one_to_one()
    {
        var (c, _) = Create();
        c.Begin(Ev(GesturePhase.Began, 0, 0, 0));
        c.Update(Ev(GesturePhase.Changed, -20, 0, 10_000));

        var f = c.Sample(20_000);
        Assert.Equal(0.4, f.Position, 9);
        Assert.Equal(TransitionPhase.Tracking, f.Phase);
    }

    [Fact]
    public void Release_past_halfway_commits_immediately_and_hides_after_settling()
    {
        var (c, host) = Create();
        long end = Swipe(c, dx: -30, v: 0, start: 0);

        Assert.Equal("Commit:1", host.Calls[^1]); // 松手就切，不等动画
        c.Sample(end + 5_000_000);
        Assert.Equal(new[] { "Prewarm", "Show", "Commit:1", "Hide:1" }, host.Calls);
    }

    [Fact]
    public void Short_swipe_springs_back_without_switching()
    {
        var (c, host) = Create();
        long end = Swipe(c, dx: -15, v: 0, start: 0);

        c.Sample(end + 5_000_000);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("Commit", StringComparison.Ordinal));
        Assert.Equal("Hide:0", host.Calls[^1]);
    }

    [Fact]
    public void Flick_switches_from_a_short_distance_and_backwards_flick_cancels()
    {
        var (a, hostA) = Create();
        Swipe(a, dx: -8, v: -250, start: 0); // 0.16 + 5 × 0.499 → 切
        Assert.Contains("Commit:1", hostA.Calls);

        var (b, hostB) = Create();
        b.Begin(Ev(GesturePhase.Began, 0, 0, 0));
        b.Update(Ev(GesturePhase.Changed, -35, 0, 10_000));
        b.End(Ev(GesturePhase.Ended, -35, 200, 20_000)); // 过半但反向甩 → 不切
        Assert.DoesNotContain(hostB.Calls, x => x.StartsWith("Commit", StringComparison.Ordinal));
    }

    [Fact]
    public void Edge_desktop_rubber_bands_and_never_commits_outward()
    {
        var (c, host) = Create(left: true, right: false);
        c.Begin(Ev(GesturePhase.Began, 0, 0, 0));
        c.Update(Ev(GesturePhase.Changed, -60, 0, 10_000)); // 右边没有桌面
        Assert.InRange(c.Sample(10_000).Position, 0, 0.12);
        c.End(Ev(GesturePhase.Ended, -60, -300, 20_000));

        Assert.DoesNotContain(host.Calls, x => x.StartsWith("Commit", StringComparison.Ordinal));
    }

    [Fact]
    public void Left_swipe_to_the_left_desktop_commits_minus_one()
    {
        var (c, host) = Create();
        Swipe(c, dx: 30, v: 0, start: 0);
        Assert.Contains("Commit:-1", host.Calls);
    }

    [Fact]
    public void Gestures_during_a_committed_animation_are_ignored()
    {
        var (c, host) = Create();
        long end = Swipe(c, dx: -30, v: 0, start: 0);
        c.Begin(Ev(GesturePhase.Began, 0, 0, end + 20_000));
        c.Update(Ev(GesturePhase.Changed, 40, 0, end + 30_000));
        c.End(Ev(GesturePhase.Ended, 40, 0, end + 40_000));

        Assert.Single(host.Calls, x => x.StartsWith("Commit", StringComparison.Ordinal));
        c.Sample(end + 5_000_000);
        Assert.Equal("Hide:1", host.Calls[^1]);
    }

    [Fact]
    public void Slide_offset_places_neighbours_one_screen_apart()
    {
        Assert.Equal(0, DesktopSwitchController.SlideOffset(0, 0, 2560, 40));
        Assert.Equal(2600, DesktopSwitchController.SlideOffset(1, 0, 2560, 40));
        Assert.Equal(0, DesktopSwitchController.SlideOffset(1, 1, 2560, 40));
        Assert.Equal(-1300, DesktopSwitchController.SlideOffset(0, 0.5, 2560, 40));
    }
}
