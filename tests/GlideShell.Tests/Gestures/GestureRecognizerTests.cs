using GlideShell.Core.Gestures;
using GlideShell.Core.Input;
using GlideShell.Tests.Support;
using static GlideShell.Tests.Support.TouchScript;

namespace GlideShell.Tests.Gestures;

public class GestureRecognizerTests
{
    private static GesturePhase[] Phases(List<GestureEvent> events)
        => events.Select(e => e.Phase).Distinct().ToArray();

    [Fact]
    public void Three_finger_swipe_up_reports_continuous_motion_and_release_velocity()
    {
        var events = new TouchScript()
            .Down(0, Fingers(3, 40, 60))
            .Move(3, 40, 60, 0, -1, distanceMm: 30, durationMs: 300) // 100mm/s 上滑
            .Lift(4, Ids(3))
            .Run();

        Assert.Equal(new[] { GesturePhase.Possible, GesturePhase.Began, GesturePhase.Changed, GesturePhase.Ended }, Phases(events));
        var began = events.Single(e => e.Phase == GesturePhase.Began);
        Assert.Equal(3, began.Fingers);
        Assert.Equal(GestureAxis.Vertical, began.Axis);
        Assert.Equal(GestureDirection.Up, began.Direction);

        var end = events.Single(e => e.Phase == GesturePhase.Ended);
        // 位移从 Began 起算：总行程 30mm 减去开始前的那一段（越过 3mm 阈值且等满 40ms 指数稳定窗口：100mm/s 下是 4mm）
        Assert.Equal(26, end.AlongMm, 6);
        Assert.Equal(100, end.AlongVelocityMmS, 0);

        // 跟手：Changed 序列单调推进，每帧增量等于手指增量（0.4mm）
        var along = events.Where(e => e.Phase == GesturePhase.Changed).Select(e => e.AlongMm).ToList();
        for (int i = 1; i < along.Count; i++) Assert.Equal(0.4, along[i] - along[i - 1], 6);
    }

    [Fact]
    public void Stopping_before_lifting_releases_with_zero_velocity_even_if_device_goes_silent()
    {
        var events = new TouchScript()
            .Down(0, Fingers(3, 40, 60))
            .Move(3, 40, 60, 0, -1, distanceMm: 20, durationMs: 200)
            .Silence(2000) // 手指静止，设备停发 2 秒
            .Lift(4, Ids(3))
            .Run();

        // 静默期间不产生任何「松手」或「取消」
        Assert.Single(events, e => e.Phase == GesturePhase.Ended);
        Assert.DoesNotContain(events, e => e.Phase == GesturePhase.Cancelled);
        Assert.Equal(0, events.Last().AlongVelocityMmS);
    }

    [Fact]
    public void Reversing_direction_mid_gesture_goes_negative_without_ending()
    {
        var events = new TouchScript()
            .Down(0, Fingers(3, 40, 50))
            .Move(3, 40, 50, 0, -1, distanceMm: 15, durationMs: 150)
            .Move(3, 40, 35, 0, 1, distanceMm: 30, durationMs: 300)
            .Lift(4, Ids(3))
            .Run();

        var changed = events.Where(e => e.Phase == GesturePhase.Changed).ToList();
        Assert.Equal(GestureDirection.Up, changed[0].Direction);
        Assert.True(changed.Max(e => e.AlongMm) > 10);
        Assert.True(changed.Min(e => e.AlongMm) < -10);
        Assert.Single(events, e => e.Phase == GesturePhase.Ended);
    }

    [Fact]
    public void Horizontal_swipe_locks_horizontal_axis()
    {
        var events = new TouchScript()
            .Down(0, Fingers(3, 60, 40))
            .Move(3, 60, 40, -1, 0.3, distanceMm: 25, durationMs: 200)
            .Lift(4, Ids(3))
            .Run();

        var began = events.Single(e => e.Phase == GesturePhase.Began);
        Assert.Equal(GestureAxis.Horizontal, began.Axis);
        Assert.Equal(GestureDirection.Left, began.Direction);
    }

    [Fact]
    public void Three_finger_tap_is_reported_as_tap_not_swipe()
    {
        var events = new TouchScript()
            .Down(0, Fingers(3, 40, 40))
            .Down(40, Fingers(3, 40.3, 40.2))
            .Down(40, Fingers(3, 40.5, 40.4))
            .Lift(30, Ids(3))
            .Run();

        Assert.Equal(new[] { GesturePhase.Possible, GesturePhase.Ended }, events.Select(e => e.Phase));
        Assert.Equal(GestureKind.Tap, events[1].Kind);
        Assert.Equal(3, events[1].Fingers);
    }

    [Fact]
    public void Tap_with_fingers_lifting_one_by_one_is_still_a_tap()
    {
        var events = new TouchScript()
            .Down(0, Fingers(3, 40, 40))
            .Down(60, Fingers(3, 40, 40))
            .Lift(8, [2], Fingers(2, 40, 40))
            .Lift(10, [1], Fingers(1, 40, 40))
            .Lift(10, [0])
            .Run();

        Assert.Equal(GestureKind.Tap, events.Last().Kind);
    }

    [Fact]
    public void Long_press_without_moving_fails_instead_of_tapping()
    {
        var events = new TouchScript()
            .Down(0, Fingers(3, 40, 40))
            .Silence(800)
            .Down(0, Fingers(3, 40, 40))
            .Lift(4, Ids(3))
            .Run();

        Assert.Equal(new[] { GesturePhase.Possible, GesturePhase.Failed }, events.Select(e => e.Phase));
    }

    [Fact]
    public void Staggered_landing_waits_for_the_final_finger_count()
    {
        // 第一根手指落下后先动了 4mm（超过阈值），但那时只有 1 指，不应开始；
        // 三指到齐后的位移才算数
        var events = new TouchScript()
            .Down(0, (0, 40, 60))
            .Down(10, (0, 40, 58)).Down(10, (0, 40, 56))
            .Down(10, (0, 40, 56), (1, 55, 56))
            .Down(10, (0, 40, 56), (1, 55, 56), (2, 70, 56))
            .Move(3, 40, 56, 0, -1, distanceMm: 10, durationMs: 100)
            .Lift(4, Ids(3))
            .Run();

        var began = events.Single(e => e.Phase == GesturePhase.Began);
        Assert.Equal(3, began.Fingers);
        Assert.Equal(6, events.Single(e => e.Phase == GesturePhase.Ended).AlongMm, 6); // 10mm − 开始前的 4mm
    }

    [Fact]
    public void Adding_and_removing_fingers_mid_gesture_does_not_jump()
    {
        var script = new TouchScript()
            .Down(0, Fingers(3, 40, 60))
            .Move(3, 40, 60, 0, -1, distanceMm: 10, durationMs: 100);
        // 第四根手指落在很远的地方：如果按质心算会让位移跳一大截
        script.Down(4, [.. Fingers(3, 40, 50), (3, 100, 10)]);
        for (int k = 1; k <= 25; k++)
            script.Down(4, [.. Fingers(3, 40, 50 - 0.4 * k), (3, 100, 10 - 0.4 * k)]);
        // 抬起两根，剩两根继续滑
        script.Lift(4, [2, 3], Fingers(2, 40, 40));
        script.Move(2, 40, 40, 0, -1, distanceMm: 10, durationMs: 100);
        script.Lift(4, Ids(2));
        var events = script.Run();

        var along = events.Where(e => e.Phase is GesturePhase.Changed or GesturePhase.Ended).Select(e => e.AlongMm).ToList();
        double maxStep = along.Zip(along.Skip(1), (a, b) => Math.Abs(b - a)).Max();
        Assert.InRange(maxStep, 0, 0.41);
        Assert.Single(events, e => e.Phase == GesturePhase.Ended);
    }

    [Fact]
    public void Dropping_to_one_finger_ends_the_gesture()
    {
        var events = new TouchScript()
            .Down(0, Fingers(3, 40, 60))
            .Move(3, 40, 60, 0, -1, distanceMm: 10, durationMs: 100)
            .Lift(4, [1, 2], Fingers(1, 40, 50))
            .Move(1, 40, 50, 0, -1, distanceMm: 10, durationMs: 100) // 剩下的单指移动属于系统（指针）
            .Lift(4, [0])
            .Run();

        Assert.Equal(GesturePhase.Ended, events.Last().Phase);
        Assert.Single(events, e => e.Phase == GesturePhase.Ended);
    }

    [Fact]
    public void Two_finger_scroll_is_left_to_the_system()
    {
        var events = new TouchScript()
            .Down(0, Fingers(2, 40, 60))
            .Move(2, 40, 60, 0, -1, distanceMm: 30, durationMs: 300)
            .Lift(4, Ids(2))
            .Run();

        Assert.Empty(events);
    }

    [Fact]
    public void Palm_contacts_are_ignored_for_finger_count_and_motion()
    {
        var palm = new Contact(9, 110, 70, Tip: true, Confidence: false);
        var script = new TouchScript();
        script.Raw(0, [.. Fingers(2, 40, 60).Select(f => new Contact(f.Id, f.X, f.Y, true, true)), palm]);
        for (int k = 1; k <= 50; k++)
        {
            var palmMoved = palm with { YMm = 70 - 0.8 * k };
            script.Raw(4, [.. Fingers(2, 40, 60 - 0.4 * k).Select(f => new Contact(f.Id, f.X, f.Y, true, true)), palmMoved]);
        }

        Assert.Empty(script.Run()); // 两指 + 手掌 ≠ 三指
    }

    [Fact]
    public void An_unconfident_finger_landing_beside_the_others_counts_right_away()
    {
        // 真机（日常四指轨迹）：第 4 根手指晚 58ms 落下，Confidence=0 持续约 170ms；以前在它变确定前就按三指开始了
        var script = new TouchScript();
        Contact Finger(int id, double x, double y) => new(id, x, y, true, true);
        script.Raw(0, [Finger(0, 40, 60), Finger(1, 58, 58), Finger(2, 76, 60)]);
        for (int k = 1; k <= 60; k++)
        {
            double dy = k < 15 ? 0 : -0.4 * (k - 15); // 第 4 根落下之前三指没动（同真机），之后 100mm/s 上滑
            List<Contact> frame = [Finger(0, 40, 60 + dy), Finger(1, 58, 58 + dy), Finger(2, 76, 60 + dy)];
            if (k >= 15) frame.Add(new Contact(3, 93, 63 + dy, Tip: true, Confidence: k >= 58)); // 离邻指约 17mm
            script.Raw(4, [.. frame]);
        }

        var began = Assert.Single(script.Run(), e => e.Phase == GesturePhase.Began);
        Assert.Equal(4, began.Fingers);
    }

    [Fact]
    public void Contacts_vanishing_without_tip_up_still_end_the_gesture()
    {
        var events = new TouchScript()
            .Down(0, Fingers(3, 40, 60))
            .Move(3, 40, 60, 0, -1, distanceMm: 10, durationMs: 100)
            .Raw(4) // 空帧：触点直接消失
            .Run();

        Assert.Equal(GesturePhase.Ended, events.Last().Phase);
    }

    [Fact]
    public void Only_a_very_long_silence_cancels_and_only_through_tick()
    {
        var events = new List<GestureEvent>();
        var recognizer = new GestureRecognizer(new GestureSettings(), events.Add);
        var script = new TouchScript()
            .Down(0, Fingers(3, 40, 60))
            .Move(3, 40, 60, 0, -1, distanceMm: 10, durationMs: 100);
        foreach (var f in script.Frames) recognizer.Process(f);

        recognizer.Tick(script.NowUs + 9_000_000);
        Assert.DoesNotContain(events, e => e.Phase == GesturePhase.Cancelled);

        recognizer.Tick(script.NowUs + 11_000_000);
        Assert.Equal(GesturePhase.Cancelled, events.Last().Phase);
        Assert.False(recognizer.IsActive);
    }

    [Fact]
    public void Four_finger_swipe_reports_four_fingers()
    {
        var events = new TouchScript()
            .Down(0, Fingers(4, 30, 40))
            .Move(4, 30, 40, 1, 0, distanceMm: 20, durationMs: 150)
            .Lift(4, Ids(4))
            .Run();

        var began = events.Single(e => e.Phase == GesturePhase.Began);
        Assert.Equal(4, began.Fingers);
        Assert.Equal(GestureDirection.Right, began.Direction);
    }
}
