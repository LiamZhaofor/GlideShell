using GlideShell.Core.Gestures;
using GlideShell.Tests.Support;
using static GlideShell.Tests.Support.TouchScript;

namespace GlideShell.Tests.Gestures;

public class GestureMapTests
{
    [Fact]
    public void Default_map_routes_three_and_four_finger_gestures()
    {
        var map = GestureMap.Default;
        Assert.Equal(GestureAction.Overview, map.Resolve(3, GestureAxis.Vertical));
        Assert.Equal(GestureAction.SwitchWindows, map.Resolve(3, GestureAxis.Horizontal));
        Assert.Equal(GestureAction.SwitchDesktops, map.Resolve(4, GestureAxis.Horizontal));
        Assert.Equal(GestureAction.None, map.Resolve(4, GestureAxis.Vertical)); // 未绑定 = 留给系统
        Assert.Empty(map.Validate());
    }

    [Fact]
    public void Three_finger_drag_mode_gives_all_three_finger_gestures_to_drag()
    {
        var map = GestureMap.WithThreeFingerDrag;
        Assert.Equal(new[] { GestureAction.Drag }, map.Candidates(3));
        Assert.Equal(GestureAction.Overview, map.Resolve(4, GestureAxis.Vertical));
        Assert.Empty(map.Validate(threeFingerDrag: true));
        Assert.NotEmpty(GestureMap.Default.Validate(threeFingerDrag: true));
    }

    [Fact]
    public void Duplicate_and_unsupported_bindings_are_reported()
    {
        var map = new GestureMap(
        [
            new(3, GestureAxis.Vertical, GestureAction.Overview),
            new(3, GestureAxis.Vertical, GestureAction.SwitchDesktops),
            new(2, GestureAxis.Horizontal, GestureAction.SwitchWindows),
        ]);
        Assert.Equal(2, map.Validate().Count);
    }
}

public class GestureRouterTests
{
    private sealed class Recorder(string name, List<string> log) : IGestureTarget
    {
        public void Prepare(long hostUs) => log.Add($"{name}.Prepare");
        public void Unprepare(long hostUs) => log.Add($"{name}.Unprepare");
        public void Begin(in GestureEvent e) => log.Add($"{name}.Begin");
        public void Update(in GestureEvent e) { if (log.LastOrDefault() != $"{name}.Update") log.Add($"{name}.Update"); }
        public void End(in GestureEvent e) => log.Add($"{name}.End:{e.Phase}");
    }

    private static (List<string> Log, GestureRouter Router) Setup(Func<bool>? suppressed = null)
    {
        var log = new List<string>();
        var overview = new Recorder("overview", log);
        var switcher = new Recorder("switcher", log);
        var desktops = new Recorder("desktops", log);
        var router = new GestureRouter(() => GestureMap.Default, a => a switch
        {
            GestureAction.Overview => overview,
            GestureAction.SwitchWindows => switcher,
            GestureAction.SwitchDesktops => desktops,
            _ => null,
        }, suppressed);
        return (log, router);
    }

    private static void Feed(TouchScript script, GestureRouter router)
    {
        var recognizer = new GestureRecognizer(new GestureSettings(), router.OnGesture);
        foreach (var f in script.Frames) recognizer.Process(f);
    }

    [Fact]
    public void Vertical_three_finger_swipe_goes_to_overview_and_releases_the_other_candidate()
    {
        var (log, router) = Setup();
        Feed(new TouchScript().Down(0, Fingers(3, 40, 60)).Move(3, 40, 60, 0, -1, 20, 200).Lift(4, Ids(3)), router);

        Assert.Equal(new[]
        {
            "overview.Prepare", "switcher.Prepare", "switcher.Unprepare",
            "overview.Begin", "overview.Update", "overview.End:Ended",
        }, log);
    }

    [Fact]
    public void Tap_unprepares_everything_and_begins_nothing()
    {
        var (log, router) = Setup();
        Feed(new TouchScript().Down(0, Fingers(3, 40, 40)).Down(50, Fingers(3, 40.2, 40)).Lift(30, Ids(3)), router);

        Assert.Equal(new[] { "overview.Prepare", "switcher.Prepare", "overview.Unprepare", "switcher.Unprepare" }, log);
    }

    [Fact]
    public void Suppression_blocks_gestures_without_per_report_checks()
    {
        int checks = 0;
        var (log, router) = Setup(() => { checks++; return true; });
        Feed(new TouchScript().Down(0, Fingers(3, 40, 60)).Move(3, 40, 60, 0, -1, 30, 300).Lift(4, Ids(3)), router);

        Assert.Empty(log);
        Assert.Equal(2, checks); // 只在 Possible 与 Began 各查一次，而不是每帧
    }

    [Fact]
    public void Replacing_the_recognizer_mid_gesture_cancels_the_orphaned_action()
    {
        var (log, router) = Setup();
        // 第一个识别器开始了总览手势后被丢弃（配置重载），新识别器从手指还在板上的状态接着识别出切换窗口
        Feed(new TouchScript().Down(0, Fingers(3, 40, 60)).Move(3, 40, 60, 0, -1, 20, 200), router);
        Feed(new TouchScript().Down(0, Fingers(3, 40, 40)).Move(3, 40, 40, 1, 0, 20, 200).Lift(4, Ids(3)), router);

        Assert.Equal(new[]
        {
            "overview.Prepare", "switcher.Prepare", "switcher.Unprepare", "overview.Begin", "overview.Update",
            "overview.Prepare", "switcher.Prepare",
            "overview.End:Cancelled", "overview.Unprepare", "switcher.Begin", "switcher.Update", "switcher.End:Ended",
        }, log);
    }

    [Fact]
    public void Unbound_gesture_is_ignored()
    {
        var (log, router) = Setup();
        Feed(new TouchScript().Down(0, Fingers(4, 30, 60)).Move(4, 30, 60, 0, -1, 20, 200).Lift(4, Ids(4)), router);

        Assert.Equal(new[] { "desktops.Prepare", "desktops.Unprepare" }, log);
        Assert.Equal(GestureAction.None, router.ActiveAction);
    }
}
