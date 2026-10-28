using GlideShell.Core.Gestures;
using GlideShell.Core.NativeGestures;

namespace GlideShell.Tests.NativeGestures;

public class TakeoverPlannerTests
{
    // 本机接手时的实际注册表（S2 记录）
    private static readonly Dictionary<string, int> ThisMachine = new()
    {
        ["ThreeFingerSlideEnabled"] = 0xFFFF,
        ["ThreeFingerUp"] = 0, ["ThreeFingerDown"] = 0, ["ThreeFingerLeft"] = 1, ["ThreeFingerRight"] = 1,
        ["ThreeFingerTapEnabled"] = 4, ["FourFingerTapEnabled"] = 4,
    };

    private static Func<string, int?> Reg(Dictionary<string, int> values) => name => values.TryGetValue(name, out int v) ? v : null;

    private static Dictionary<string, int> Apply(Dictionary<string, int> values, IReadOnlyList<TakeoverChange> changes)
    {
        var copy = new Dictionary<string, int>(values);
        foreach (var c in changes) copy[c.Name] = c.Written;
        return copy;
    }

    [Fact]
    public void Default_map_takes_over_all_three_and_four_finger_swipes()
    {
        var (changes, warnings) = TakeoverPlanner.Plan(GestureMap.Default, Reg(ThisMachine));
        var after = Apply(ThisMachine, changes);

        Assert.Empty(warnings);
        Assert.Equal(0, after["ThreeFingerLeft"]);
        Assert.Equal(0, after["ThreeFingerRight"]);
        Assert.Equal(0xFFFF, after["FourFingerSlideEnabled"]);
        Assert.Equal(0, after["FourFingerLeft"]);
        Assert.Equal(0, after["FourFingerRight"]);
        // 四指上下绑定了显示桌面：系统的任务视图 / 显示桌面也关掉
        Assert.Equal(0, after["FourFingerUp"]);
        Assert.Equal(0, after["FourFingerDown"]);
    }

    [Fact]
    public void Taps_are_not_touched_unless_glideshell_does_the_three_finger_tap()
    {
        var (changes, _) = TakeoverPlanner.Plan(GestureMap.Default, Reg(ThisMachine));
        Assert.DoesNotContain(changes, c => c.Name.Contains("Tap", StringComparison.Ordinal));
    }

    [Fact]
    public void Three_finger_tap_is_turned_off_while_glideshell_sends_the_middle_click_and_restored_after()
    {
        var (changes, _) = TakeoverPlanner.Plan(GestureMap.Default, Reg(ThisMachine), threeFingerTap: true);
        var tap = Assert.Single(changes, c => c.Name.Contains("Tap", StringComparison.Ordinal));
        Assert.Equal("ThreeFingerTapEnabled", tap.Name);
        Assert.Equal(4, tap.Original); // 用户原来的中键
        Assert.Equal(0, tap.Written); // 接管期间系统不再点一次

        var during = Apply(ThisMachine, changes);
        var restore = TakeoverPlanner.PlanRestore(new TakeoverRecord(DateTimeOffset.Now, "test", changes), Reg(during));
        Assert.Equal(RestoreAction.WriteOriginal, restore.Single(p => p.Change.Name == "ThreeFingerTapEnabled").Action);

        // 系统本来就是「无」：不用改
        var off = new Dictionary<string, int>(ThisMachine) { ["ThreeFingerTapEnabled"] = 0 };
        Assert.DoesNotContain(TakeoverPlanner.Plan(GestureMap.Default, Reg(off), threeFingerTap: true).Changes, c => c.Name == "ThreeFingerTapEnabled");
    }

    [Fact]
    public void Values_already_in_the_wanted_state_are_not_rewritten()
    {
        var overviewOnly = new GestureMap([new(3, GestureAxis.Vertical, GestureAction.Overview)]);
        var (changes, _) = TakeoverPlanner.Plan(overviewOnly, Reg(ThisMachine));

        Assert.Empty(changes); // 本机三指上下本来就是「无」，左右保留切换应用
    }

    [Fact]
    public void Preset_mode_is_expanded_so_unowned_directions_keep_working()
    {
        var factory = new Dictionary<string, int>(); // 出厂：注册表里什么都没有
        var overviewOnly = new GestureMap([new(3, GestureAxis.Vertical, GestureAction.Overview)]);
        var (changes, _) = TakeoverPlanner.Plan(overviewOnly, Reg(factory));
        var after = Apply(factory, changes);

        Assert.Equal(0xFFFF, after["ThreeFingerSlideEnabled"]);
        Assert.Equal(0, after["ThreeFingerUp"]);
        Assert.Equal(0, after["ThreeFingerDown"]);
        Assert.Equal(1, after["ThreeFingerLeft"]); // 出厂预设「切换应用」的左右保留
        Assert.Equal(1, after["ThreeFingerRight"]);
        Assert.All(changes, c => Assert.Null(c.Original)); // 原本都不存在 → 还原时删除
    }

    [Fact]
    public void Restore_puts_back_originals_deletes_new_values_and_keeps_user_changes()
    {
        var factory = new Dictionary<string, int>();
        var (changes, _) = TakeoverPlanner.Plan(GestureMap.Default, Reg(factory));
        var record = new TakeoverRecord(DateTimeOffset.Now, "test", changes);
        var during = Apply(factory, changes);
        during["FourFingerLeft"] = 4; // 用户在接管期间去「设置」里改回了切换桌面

        var plan = TakeoverPlanner.PlanRestore(record, Reg(during)).ToDictionary(p => p.Change.Name, p => p.Action);

        Assert.Equal(RestoreAction.Delete, plan["ThreeFingerSlideEnabled"]);
        Assert.Equal(RestoreAction.KeepUserChange, plan["FourFingerLeft"]);
    }

    [Fact]
    public void Restore_is_idempotent()
    {
        var (changes, _) = TakeoverPlanner.Plan(GestureMap.Default, Reg(ThisMachine));
        var record = new TakeoverRecord(DateTimeOffset.Now, "test", changes);

        var plan = TakeoverPlanner.PlanRestore(record, Reg(ThisMachine)); // 已经是原值
        Assert.All(plan, p => Assert.Equal(RestoreAction.AlreadyRestored, p.Action));
    }

    [Fact]
    public void Three_finger_drag_takes_over_all_three_finger_swipes_but_not_the_tap()
    {
        var (changes, _) = TakeoverPlanner.Plan(GestureMap.WithThreeFingerDrag, Reg(ThisMachine));
        var after = Apply(ThisMachine, changes);

        Assert.Equal(0, after["ThreeFingerLeft"]); // 拖移时系统不能再切换应用
        Assert.Equal(0, after["ThreeFingerRight"]);
        Assert.Equal(4, after["ThreeFingerTapEnabled"]); // 三指轻点中键保留
        Assert.Contains(changes, c => c.Name == "FourFingerUp" && c.Written == 0);
    }
}
