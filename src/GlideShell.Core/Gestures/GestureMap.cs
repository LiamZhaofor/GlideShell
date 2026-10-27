namespace GlideShell.Core.Gestures;

/// <summary>GlideShell 接管的动作。没有绑定的手势一律留给系统。</summary>
public enum GestureAction
{
    None,

    /// <summary>窗口总览：沿锁定轴正向展开、反向收起，全程跟手。</summary>
    Overview,

    /// <summary>横滑跟手切换窗口。</summary>
    SwitchWindows,

    /// <summary>连续切换虚拟桌面。</summary>
    SwitchDesktops,

    /// <summary>三指拖移：按住左键拖动（三指拖移模式下三指的两个轴都绑定它）。</summary>
    Drag,

    /// <summary>
    /// 显示桌面（竖直轴）：下滑把窗口跟手推出屏幕、落定后最小化；桌面已显示时上滑把它们拉回来，
    /// 否则上滑打开窗口总览（macOS 四指上滑 = 调度中心）。
    /// </summary>
    ShowDesktop,
}

/// <summary>一条绑定：几根手指、沿哪个轴 → 哪个动作。</summary>
public sealed record GestureBinding(int Fingers, GestureAxis Axis, GestureAction Action);

/// <summary>手势映射表。同一（指数，轴）只能绑定一个动作。</summary>
public sealed record GestureMap(IReadOnlyList<GestureBinding> Bindings)
{
    /// <summary>默认：三指上下 = 总览，三指左右 = 切换窗口，四指上下 = 显示桌面，四指左右 = 切换桌面。</summary>
    public static GestureMap Default { get; } = new(
    [
        new(3, GestureAxis.Vertical, GestureAction.Overview),
        new(3, GestureAxis.Horizontal, GestureAction.SwitchWindows),
        new(4, GestureAxis.Vertical, GestureAction.ShowDesktop),
        new(4, GestureAxis.Horizontal, GestureAction.SwitchDesktops),
    ]);

    /// <summary>
    /// 开启三指拖移时三指整组让给拖移（macOS 同款取舍）：总览改四指上下，切桌面保持四指左右，
    /// 窗口切换没有手势（用 Alt+Tab）。三指轻点（中键）仍归系统。
    /// </summary>
    public static GestureMap WithThreeFingerDrag { get; } = new(
    [
        new(3, GestureAxis.Vertical, GestureAction.Drag),
        new(3, GestureAxis.Horizontal, GestureAction.Drag),
        new(4, GestureAxis.Vertical, GestureAction.Overview),
        new(4, GestureAxis.Horizontal, GestureAction.SwitchDesktops),
    ]);

    public GestureAction Resolve(int fingers, GestureAxis axis)
    {
        foreach (var b in Bindings)
            if (b.Fingers == fingers && b.Axis == axis) return b.Action;
        return GestureAction.None;
    }

    /// <summary>这组手指落下时可能触发的动作（两个轴都算），用于预热。</summary>
    public IEnumerable<GestureAction> Candidates(int fingers)
        => Bindings.Where(b => b.Fingers == fingers && b.Action != GestureAction.None).Select(b => b.Action).Distinct();

    /// <summary>冲突与无效项；空列表表示可用。</summary>
    public IReadOnlyList<string> Validate(bool threeFingerDrag = false)
    {
        var problems = new List<string>();
        foreach (var g in Bindings.GroupBy(b => (b.Fingers, b.Axis)).Where(g => g.Count() > 1))
            problems.Add($"{g.Key.Fingers} 指{AxisName(g.Key.Axis)}绑定了多个动作：{string.Join("、", g.Select(b => b.Action))}");
        foreach (var b in Bindings.Where(b => b.Fingers is < 3 or > 5))
            problems.Add($"{b.Fingers} 指不支持（1–2 指留给系统的指针、滚动和缩放）");
        if (threeFingerDrag)
            foreach (var b in Bindings.Where(b => b.Fingers == 3 && b.Action is not (GestureAction.None or GestureAction.Drag)))
                problems.Add($"已开启三指拖移，三指{AxisName(b.Axis)}不能再绑定 {b.Action}");
        foreach (var b in Bindings.Where(b => b.Action == GestureAction.Drag && b.Fingers != 3))
            problems.Add($"拖移只支持三指（现在绑在了 {b.Fingers} 指上）");
        // 切换窗口 / 桌面是横向排列的，只跟随左右位移
        foreach (var b in Bindings.Where(b => b.Action is GestureAction.SwitchWindows or GestureAction.SwitchDesktops && b.Axis == GestureAxis.Vertical))
            problems.Add($"{b.Action} 只支持左右滑动（现在绑在了 {b.Fingers} 指上下）");
        foreach (var b in Bindings.Where(b => b.Action == GestureAction.ShowDesktop && b.Axis == GestureAxis.Horizontal))
            problems.Add($"{b.Action} 只支持上下滑动（现在绑在了 {b.Fingers} 指左右）");
        return problems;
    }

    private static string AxisName(GestureAxis axis) => axis == GestureAxis.Vertical ? "上下" : "左右";
}
