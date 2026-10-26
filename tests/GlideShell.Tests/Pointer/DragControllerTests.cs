using GlideShell.Core.Configuration;
using GlideShell.Core.Gestures;
using GlideShell.Core.Pointer;
using GlideShell.Tests.Support;
using static GlideShell.Tests.Support.TouchScript;

namespace GlideShell.Tests.Pointer;

public class DragControllerTests
{
    private sealed class FakeMouse : IDragHost
    {
        public readonly List<string> Calls = [];
        public double X, Y;
        public void Press() => Calls.Add("Press");
        public void MoveBy(double dxPx, double dyPx)
        {
            X += dxPx;
            Y += dyPx;
            if (Calls.LastOrDefault() != "Move") Calls.Add("Move");
        }
        public void Release() => Calls.Add("Release");
    }

    private static readonly DragConfig NoAcceleration = new() { PixelsPerMm = 10, Acceleration = 0 };

    private static (FakeMouse Mouse, GestureRouter Router) Setup(DragConfig cfg)
    {
        var mouse = new FakeMouse();
        var drag = new DragController(mouse, () => cfg);
        var router = new GestureRouter(() => GestureMap.WithThreeFingerDrag, a => a == GestureAction.Drag ? drag : null);
        return (mouse, router);
    }

    private static void Feed(TouchScript script, GestureRouter router)
    {
        var recognizer = new GestureRecognizer(new GestureSettings(), router.OnGesture);
        foreach (var f in script.Frames) recognizer.Process(f);
    }

    [Fact]
    public void Three_finger_slide_presses_drags_and_releases()
    {
        var (mouse, router) = Setup(NoAcceleration);
        Feed(new TouchScript().Down(0, Fingers(3, 40, 40)).Move(3, 40, 40, 1, 0, 20, 200).Lift(4, Ids(3)), router);

        Assert.Equal(new[] { "Press", "Move", "Release" }, mouse.Calls);
        // 20mm 里越过阈值前的 4mm 不算：16mm × 10px/mm
        Assert.Equal(160, mouse.X, 0);
        Assert.Equal(0, mouse.Y, 0);
    }

    [Fact]
    public void Slow_sub_pixel_motion_is_not_lost_to_rounding()
    {
        var (mouse, router) = Setup(new DragConfig { PixelsPerMm = 1, Acceleration = 0 });
        // 每帧 0.04mm × 1px/mm = 0.04px：逐帧取整会一动不动；累积余数后走满 29mm − 越过阈值前的 3mm = 26px
        Feed(new TouchScript().Down(0, Fingers(3, 40, 40)).Move(3, 40, 40, 1, 0, 29, 2900).Lift(4, Ids(3)), router);

        Assert.InRange(mouse.X, 25, 26);
    }

    [Fact]
    public void Faster_motion_moves_further_for_the_same_distance()
    {
        var cfg = new DragConfig { PixelsPerMm = 10, Acceleration = 0.6 };
        Assert.Equal(10, DragController.Gain(cfg, 0));
        Assert.Equal(16, DragController.Gain(cfg, 100), 9);
        Assert.Equal(34, DragController.Gain(cfg, 1000), 9); // 封顶 400mm/s
    }

    [Fact]
    public void Three_finger_tap_never_presses_the_button()
    {
        var (mouse, router) = Setup(NoAcceleration);
        Feed(new TouchScript().Down(0, Fingers(3, 40, 40)).Down(60, Fingers(3, 40.2, 40)).Lift(30, Ids(3)), router);

        Assert.Empty(mouse.Calls); // 轻点交给系统（中键）
    }
}
