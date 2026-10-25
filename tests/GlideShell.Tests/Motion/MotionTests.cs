using GlideShell.Core.Motion;

namespace GlideShell.Tests.Motion;

public class VelocityTrackerTests
{
    [Fact]
    public void Constant_motion_gives_exact_velocity()
    {
        var v = new VelocityTracker();
        for (int i = 0; i <= 25; i++) v.Add(i * 4_000, i * 0.4, -i * 0.2); // 100mm/s 向右、50mm/s 向上

        var (vx, vy) = v.Velocity(100_000);

        Assert.Equal(100, vx, 6);
        Assert.Equal(-50, vy, 6);
    }

    [Fact]
    public void Velocity_is_zero_when_fingers_stopped_before_lifting()
    {
        var v = new VelocityTracker(windowMs: 100);
        for (int i = 0; i <= 25; i++) v.Add(i * 4_000, i * 0.4, 0);

        // 停住 300ms 后才抬起（期间设备没有报文）
        Assert.Equal((0.0, 0.0), v.Velocity(100_000 + 300_000));
    }

    [Fact]
    public void Only_recent_window_counts()
    {
        var v = new VelocityTracker(windowMs: 50);
        for (int i = 0; i <= 50; i++) v.Add(i * 4_000, i * 0.4, 0);      // 前 200ms：100mm/s
        for (int i = 1; i <= 25; i++) v.Add(200_000 + i * 4_000, 20 + i * 0.8, 0); // 后 100ms：200mm/s

        var (vx, _) = v.Velocity(300_000);

        Assert.Equal(200, vx, 6);
    }

    [Fact]
    public void Time_going_backwards_resets_history()
    {
        var v = new VelocityTracker();
        v.Add(100_000, 0, 0);
        v.Add(104_000, 10, 0);
        v.Add(50_000, 0, 0);

        Assert.Equal((0.0, 0.0), v.Velocity(50_000));
    }
}

public class SpringTests
{
    public static TheoryData<double> DampingRatios => new() { 0.5, 0.86, 1.0, 1.6 };

    [Theory]
    [MemberData(nameof(DampingRatios))]
    public void Starts_at_initial_state_and_converges(double damping)
    {
        var p = new SpringParameters(0.35, damping);

        Assert.Equal((1.0, 2.0), Spring.Evaluate(p, 1, 2, 0));
        var (x, v) = Spring.Evaluate(p, 1, 2, 5);
        Assert.InRange(Math.Abs(x), 0, 1e-6);
        Assert.InRange(Math.Abs(v), 0, 1e-5);
    }

    [Theory]
    [MemberData(nameof(DampingRatios))]
    public void Velocity_is_the_derivative_of_position(double damping)
    {
        var p = new SpringParameters(0.35, damping);
        const double h = 1e-6;
        foreach (double t in new[] { 0.01, 0.05, 0.1, 0.2, 0.4 })
        {
            var (x1, _) = Spring.Evaluate(p, 0.7, -3, t - h);
            var (x2, _) = Spring.Evaluate(p, 0.7, -3, t + h);
            var (_, v) = Spring.Evaluate(p, 0.7, -3, t);
            Assert.Equal((x2 - x1) / (2 * h), v, 4);
        }
    }

    [Fact]
    public void Critical_damping_never_overshoots_from_rest()
    {
        var p = SpringParameters.Critical;
        for (double t = 0; t < 2; t += 0.005)
            Assert.True(Spring.Evaluate(p, 1, 0, t).X >= -1e-12, $"t={t} 越过目标");
    }

    [Fact]
    public void Underdamped_spring_overshoots()
    {
        var p = new SpringParameters(0.35, 0.5);
        double min = Enumerable.Range(0, 400).Select(i => Spring.Evaluate(p, 1, 0, i * 0.005).X).Min();
        Assert.True(min < -0.05);
    }

    [Fact]
    public void Animation_settles_exactly_on_target()
    {
        var a = new SpringAnimation(SpringParameters.Default, From: 0, Target: 1, InitialVelocity: 0, StartUs: 0);

        Assert.False(a.Sample(10_000).Settled);
        var end = a.Sample(3_000_000);
        Assert.True(end.Settled);
        Assert.Equal(1, end.Value);
        Assert.Equal(0, end.Velocity);
    }
}

public class RubberBandTests
{
    [Fact]
    public void Starts_with_coefficient_slope_and_never_exceeds_extent()
    {
        Assert.Equal(0, RubberBand.Apply(0, 0.1));
        Assert.Equal(RubberBand.DefaultCoefficient, RubberBand.Slope(0, 0.1), 9);

        double prev = 0;
        for (double x = 0.01; x < 100; x *= 1.5)
        {
            double y = RubberBand.Apply(x, 0.1);
            Assert.True(y > prev && y < 0.1);
            prev = y;
        }
    }

    [Fact]
    public void Clamp_leaves_in_range_values_untouched()
    {
        Assert.Equal(0.42, RubberBand.Clamp(0.42, 0, 1, 0.1, 0.1));
        Assert.InRange(RubberBand.Clamp(1.5, 0, 1, 0.1, 0.1), 1, 1.1);
        Assert.InRange(RubberBand.Clamp(-0.5, 0, 1, 0.1, 0.1), -0.1, 0);
    }

    [Fact]
    public void Projection_matches_ios_normal_deceleration()
    {
        // 0.998/ms：1 单位/秒的速度滑行 0.499 单位
        Assert.Equal(0.499, Projection.Project(0, 1), 9);
        Assert.Equal(0.25, Projection.Project(0.25, 0), 9);
    }
}

public class InteractiveTransitionTests
{
    private static InteractiveTransition Overview() => new(0, 1, 0);

    [Fact]
    public void Tracking_follows_the_finger_exactly_and_never_commits_by_itself()
    {
        var tr = Overview();
        tr.BeginTracking(0);
        tr.Track(0.5);
        Assert.Equal(0.5, tr.Sample(10_000).Value);

        // 手指停在 100% 处很久：仍然是 Tracking，不会自动进入
        tr.Track(1.0);
        var s = tr.Sample(5_000_000);
        Assert.Equal(1.0, s.Value);
        Assert.Equal(TransitionPhase.Tracking, s.Phase);
    }

    [Fact]
    public void Tracking_is_reversible()
    {
        var tr = Overview();
        tr.BeginTracking(0);
        tr.Track(0.8);
        tr.Track(0.3);
        Assert.Equal(0.3, tr.Sample(0).Value);
    }

    [Fact]
    public void Slow_release_past_halfway_completes_and_short_of_halfway_cancels()
    {
        var a = Overview();
        a.BeginTracking(0);
        a.Track(0.6);
        Assert.Equal(1, a.Release(velocity: 0, hostUs: 0));

        var b = Overview();
        b.BeginTracking(0);
        b.Track(0.4);
        Assert.Equal(0, b.Release(velocity: 0, hostUs: 0));
    }

    [Fact]
    public void Flick_completes_even_when_short_of_halfway()
    {
        var tr = Overview();
        tr.BeginTracking(0);
        tr.Track(0.2);
        Assert.Equal(1, tr.Release(velocity: 2.0, hostUs: 0)); // 投影 0.2 + 2×0.499 ≈ 1.2
    }

    [Fact]
    public void Flick_backwards_cancels_even_past_halfway()
    {
        var tr = Overview();
        tr.BeginTracking(0);
        tr.Track(0.7);
        Assert.Equal(0, tr.Release(velocity: -1.5, hostUs: 0));
    }

    [Fact]
    public void Release_velocity_carries_into_the_animation()
    {
        var tr = Overview();
        tr.BeginTracking(0);
        tr.Track(0.5);
        tr.Release(velocity: 3.0, hostUs: 1_000_000);

        var s = tr.Sample(1_000_100); // 松手 0.1ms 后
        Assert.Equal(3.0, s.Velocity, 1);
        Assert.Equal(0.5, s.Value, 3);
    }

    [Fact]
    public void New_touch_during_animation_takes_over_from_the_current_frame()
    {
        var tr = Overview();
        tr.AnimateTo(1, hostUs: 0);
        double mid = tr.Sample(80_000).Value;
        Assert.InRange(mid, 0.05, 0.95);

        tr.BeginTracking(80_000);
        Assert.Equal(mid, tr.Sample(80_000).Value);
        tr.Track(-0.1);
        Assert.Equal(mid - 0.1, tr.Sample(90_000).Value, 9);
    }

    [Fact]
    public void Overscroll_is_rubber_banded_and_springs_back()
    {
        var tr = Overview();
        tr.BeginTracking(0);
        tr.Track(1.5);
        double v = tr.Sample(0).Value;
        Assert.InRange(v, 1.0, 1.0 + tr.OverscrollAbove);

        tr.Release(0, 0);
        var settled = tr.Sample(5_000_000);
        Assert.Equal(1, settled.Value);
        Assert.True(settled.JustSettled);
        Assert.False(tr.Sample(5_100_000).JustSettled);
    }

    [Fact]
    public void Custom_target_chooser_receives_projection()
    {
        var tr = new InteractiveTransition(0, 4, 1);
        tr.BeginTracking(0);
        tr.Track(0.4);
        double target = tr.Release(1.0, 0, (current, projected) => Math.Round(projected));

        Assert.Equal(2, target); // 1.4 + 0.499 ≈ 1.9 → 2
    }
}
