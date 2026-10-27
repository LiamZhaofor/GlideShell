using GlideShell.Core.Gestures;

namespace GlideShell.Tests.Gestures;

/// <summary>鼠标按键手势：拖开才算手势、锁定方向、按屏幕尺寸换算成毫米；没拖开是普通点击。</summary>
public class MouseDragRecognizerTests
{
    private static (MouseDragRecognizer R, List<GestureEvent> Events) Create()
    {
        var events = new List<GestureEvent>();
        return (new MouseDragRecognizer(events.Add), events);
    }

    [Fact]
    public void Scale_follows_mac_mouse_fix_one_screen_width_per_desktop()
    {
        // 2560×1528 工作区、桌面间隙 72px：拖过 2632px 正好切过一个桌面（55mm），竖向拖过 764px 完全展开总览（30mm）
        var (kx, ky) = MouseDragRecognizer.Scale(2560, 1528, 72, 55, 30, 1);
        Assert.Equal(55, 2632 * kx, 6);
        Assert.Equal(30, 1528 * MouseDragRecognizer.VerticalFraction * ky, 6);
        var (kx2, _) = MouseDragRecognizer.Scale(2560, 1528, 72, 55, 30, 2);
        Assert.Equal(2 * kx, kx2, 9); // 灵敏度是倍数
    }

    [Fact]
    public void A_click_without_dragging_is_not_a_gesture()
    {
        var (r, events) = Create();
        r.Press(0, 0.02, 0.04);
        r.Move(2, 1, 10_000);
        Assert.False(r.Release(20_000));
        Assert.Empty(events);
    }

    [Fact]
    public void A_small_wobble_prepares_then_fails()
    {
        var (r, events) = Create();
        r.Press(0, 0.02, 0.04);
        r.Move(4, 0, 10_000);
        Assert.False(r.Release(20_000));
        Assert.Equal([GesturePhase.Possible, GesturePhase.Failed], events.Select(e => e.Phase));
        Assert.All(events, e => Assert.Equal(MouseDragRecognizer.Fingers, e.Fingers));
    }

    [Fact]
    public void Dragging_up_locks_the_vertical_axis_and_reports_millimetres_from_the_lock()
    {
        var (r, events) = Create();
        r.Press(0, 0.02, 0.04);
        for (int i = 1; i <= 20; i++) r.Move(0.5, -10, i * 8_000); // 向上拖，略带横向
        Assert.True(r.IsDragging);
        Assert.True(r.Release(170_000));

        var began = events.Single(e => e.Phase == GesturePhase.Began);
        Assert.Equal(GestureAxis.Vertical, began.Axis);
        Assert.Equal(GestureDirection.Up, began.Direction);
        Assert.Equal(0, began.DyMm);
        var last = events.Last(e => e.Phase == GesturePhase.Changed);
        Assert.Equal(-19 * 10 * 0.04, last.DyMm, 6); // 第 1 次移动锁定方向，之后 19 次各 10px，按竖向比例换算
        Assert.True(last.AlongMm > 0);
        var end = events.Last();
        Assert.Equal(GesturePhase.Ended, end.Phase);
        Assert.True(end.VyMmS < -40); // 1250 px/s × 0.04 = 50 mm/s 向上
    }

    [Fact]
    public void Dragging_sideways_uses_the_horizontal_scale_and_cancel_ends_the_gesture()
    {
        var (r, events) = Create();
        r.Press(0, 0.02, 0.04);
        r.Move(-12, 3, 8_000);
        r.Move(-100, 0, 16_000);
        var began = events.Single(e => e.Phase == GesturePhase.Began);
        Assert.Equal(GestureAxis.Horizontal, began.Axis);
        Assert.Equal(GestureDirection.Left, began.Direction);
        Assert.Equal(-100 * 0.02, events.Last().DxMm, 6);
        r.Cancel(20_000);
        Assert.Equal(GesturePhase.Cancelled, events.Last().Phase);
        Assert.False(r.IsPressed);
    }
}
