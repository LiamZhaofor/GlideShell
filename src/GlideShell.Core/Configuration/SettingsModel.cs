using GlideShell.Core.Gestures;

namespace GlideShell.Core.Configuration;

/// <summary>设置窗口里可以配置的一个手势位置（几指、哪个方向）。</summary>
public readonly record struct GestureSlot(int Fingers, GestureAxis Axis, string Label);

/// <summary>灵敏度预设：同时调整滑动阈值和各动作的行程（越灵敏，手指走得越短）。</summary>
public sealed record SensitivityPreset(string Label, double SlopMm, double OverviewTravelMm, double MmPerWindow, double DesktopTravelMm)
{
    public static IReadOnlyList<SensitivityPreset> All { get; } =
    [
        new("高", 1.5, 24, 18, 45),
        new("标准", 2.0, 30, 22, 55),
        new("低", 3.0, 38, 28, 68),
    ];

    public GlideShellConfig ApplyTo(GlideShellConfig c) => c with
    {
        Recognition = c.Recognition with { SlopMm = SlopMm, TapMaxMoveMm = Math.Min(c.Recognition.TapMaxMoveMm, SlopMm) },
        Overview = c.Overview with { TravelMm = OverviewTravelMm },
        Switcher = c.Switcher with { MmPerWindow = MmPerWindow },
        Desktops = c.Desktops with { TravelMm = DesktopTravelMm },
    };

    public bool Matches(GlideShellConfig c) => ApplyTo(c) == c;

    /// <summary>配置对应的预设；手改过、不属于任何预设时返回 null（「自定义」）。</summary>
    public static SensitivityPreset? Of(GlideShellConfig c) => All.FirstOrDefault(p => p.Matches(c));
}

/// <summary>
/// 设置窗口编辑的状态，与界面控件一一对应；和 <see cref="GlideShellConfig"/> 互相转换。
/// 窗口只改这个模型，保存时 <see cref="ApplyTo"/> 回配置——界面以外的字段（弹簧、5 指绑定等）原样保留。
/// </summary>
public sealed record SettingsModel
{
    /// <summary>界面里可配置的四个位置。5 指手势只能在配置文件里写。</summary>
    public static IReadOnlyList<GestureSlot> Slots { get; } =
    [
        new(3, GestureAxis.Vertical, "三指上下滑"),
        new(3, GestureAxis.Horizontal, "三指左右滑"),
        new(4, GestureAxis.Vertical, "四指上下滑"),
        new(4, GestureAxis.Horizontal, "四指左右滑"),
    ];

    /// <summary>与 <see cref="Slots"/> 同序。<see cref="GestureAction.None"/> = 留给系统。</summary>
    public required GestureAction[] Actions { get; init; }

    public bool ThreeFingerDrag { get; init; }
    public bool PauseInFullscreen { get; init; }
    public double SlopMm { get; init; }
    public double OverviewTravelMm { get; init; }
    public double MmPerWindow { get; init; }
    public double DesktopTravelMm { get; init; }
    public double BackdropDim { get; init; }
    public bool SharedThumbnails { get; init; }
    public double DragPixelsPerMm { get; init; }
    public double DragAcceleration { get; init; }
    public required IReadOnlyList<string> ExcludedProcesses { get; init; }

    /// <summary>开机启动不在配置文件里（是注册表 Run 项），由宿主读写。</summary>
    public bool Autostart { get; init; }

    public static string ActionLabel(GestureAction action) => action switch
    {
        GestureAction.Overview => "窗口总览",
        GestureAction.SwitchWindows => "切换窗口",
        GestureAction.SwitchDesktops => "切换虚拟桌面",
        GestureAction.Drag => "三指拖移",
        _ => "系统默认（不接管）",
    };

    /// <summary>某个位置可选的动作。开启三指拖移后三指两个方向固定为拖移。</summary>
    public static IReadOnlyList<GestureAction> Choices(GestureSlot slot, bool threeFingerDrag)
    {
        if (threeFingerDrag && slot.Fingers == 3) return [GestureAction.Drag];
        return slot.Axis == GestureAxis.Vertical
            ? [GestureAction.None, GestureAction.Overview]
            : [GestureAction.None, GestureAction.Overview, GestureAction.SwitchWindows, GestureAction.SwitchDesktops];
    }

    public static SettingsModel FromConfig(GlideShellConfig c, bool autostart = false)
    {
        var map = c.EffectiveMap;
        return new SettingsModel
        {
            // 关掉了四指切桌面（Desktops.Enabled=false）时那一格实际归系统
            Actions = Slots.Select(s => map.Resolve(s.Fingers, s.Axis) is var a && a == GestureAction.SwitchDesktops && !c.Desktops.Enabled
                ? GestureAction.None : a).ToArray(),
            ThreeFingerDrag = c.ThreeFingerDrag,
            PauseInFullscreen = c.PauseInFullscreen,
            SlopMm = c.Recognition.SlopMm,
            OverviewTravelMm = c.Overview.TravelMm,
            MmPerWindow = c.Switcher.MmPerWindow,
            DesktopTravelMm = c.Desktops.TravelMm,
            BackdropDim = c.Overview.BackdropDim,
            SharedThumbnails = c.Overview.SharedThumbnails,
            DragPixelsPerMm = c.Drag.PixelsPerMm,
            DragAcceleration = c.Drag.Acceleration,
            ExcludedProcesses = [.. c.ExcludedProcesses],
            Autostart = autostart,
        };
    }

    public static GestureSlot SlotOf(int fingers, GestureAxis axis) => Slots[IndexOf(fingers, axis)];

    public GestureAction ActionAt(GestureSlot slot) => Actions[IndexOf(slot)];

    public SettingsModel WithAction(GestureSlot slot, GestureAction action)
    {
        var actions = (GestureAction[])Actions.Clone();
        actions[IndexOf(slot)] = action;
        return this with { Actions = actions };
    }

    /// <summary>
    /// 切换三指拖移（macOS 同款取舍）：开启时三指两个方向都给拖移，原来三指上下的总览挪到空着的四指上下；
    /// 关闭时三指恢复为默认的总览 / 切换窗口。
    /// </summary>
    public SettingsModel WithThreeFingerDrag(bool on)
    {
        if (on == ThreeFingerDrag) return this;
        var actions = (GestureAction[])Actions.Clone();
        int v3 = IndexOf(3, GestureAxis.Vertical), h3 = IndexOf(3, GestureAxis.Horizontal), v4 = IndexOf(4, GestureAxis.Vertical);
        if (on)
        {
            if (actions[v3] == GestureAction.Overview && actions[v4] == GestureAction.None) actions[v4] = GestureAction.Overview;
            actions[v3] = actions[h3] = GestureAction.Drag;
        }
        else
        {
            actions[v3] = GestureAction.Overview;
            actions[h3] = GestureAction.SwitchWindows;
        }
        return this with { ThreeFingerDrag = on, Actions = actions };
    }

    public SettingsModel WithPreset(SensitivityPreset p) => this with
    {
        SlopMm = p.SlopMm,
        OverviewTravelMm = p.OverviewTravelMm,
        MmPerWindow = p.MmPerWindow,
        DesktopTravelMm = p.DesktopTravelMm,
    };

    /// <summary>当前数值对应的预设；不属于任何预设时返回 null。</summary>
    public SensitivityPreset? Preset => SensitivityPreset.All.FirstOrDefault(p =>
        p.SlopMm == SlopMm && p.OverviewTravelMm == OverviewTravelMm && p.MmPerWindow == MmPerWindow && p.DesktopTravelMm == DesktopTravelMm);

    /// <summary>恢复默认设置；排除的程序与开机启动属于用户数据，保留。</summary>
    public SettingsModel Defaults() => FromConfig(new GlideShellConfig(), Autostart) with { ExcludedProcesses = ExcludedProcesses };

    /// <summary>把界面状态写回配置（界面以外的字段保留），并做与加载时相同的范围修正。</summary>
    public (GlideShellConfig Config, IReadOnlyList<string> Problems) ApplyTo(GlideShellConfig c)
    {
        var bindings = Slots.Select((s, i) => new GestureBinding(s.Fingers, s.Axis, Actions[i]))
            .Where(b => b.Action != GestureAction.None)
            .Concat((c.Bindings ?? []).Where(b => b.Fingers > 4)) // 界面管不到的 5 指绑定原样保留
            .ToList();
        var defaults = ThreeFingerDrag ? GestureMap.WithThreeFingerDrag : GestureMap.Default;
        bool isDefault = bindings.Count == defaults.Bindings.Count && bindings.All(defaults.Bindings.Contains);

        var updated = c with
        {
            // 与默认映射一致时不写自定义映射，以后默认映射调整时自动跟上
            Bindings = isDefault ? null : bindings,
            ThreeFingerDrag = ThreeFingerDrag,
            PauseInFullscreen = PauseInFullscreen,
            Recognition = c.Recognition with { SlopMm = SlopMm, TapMaxMoveMm = Math.Min(c.Recognition.TapMaxMoveMm, SlopMm) },
            Overview = c.Overview with { TravelMm = OverviewTravelMm, BackdropDim = BackdropDim, SharedThumbnails = SharedThumbnails },
            Switcher = c.Switcher with { MmPerWindow = MmPerWindow },
            // 界面用「系统默认」表达不接管切桌面，旧的 Enabled 开关统一置为 true，免得两处说法不一致
            Desktops = c.Desktops with { TravelMm = DesktopTravelMm, Enabled = true },
            Drag = c.Drag with { PixelsPerMm = DragPixelsPerMm, Acceleration = DragAcceleration },
            ExcludedProcesses = [.. ExcludedProcesses],
        };
        return updated.Sanitize();
    }

    /// <summary>保存前的检查（例如同一个动作的映射冲突）；空列表表示可以保存。</summary>
    public IReadOnlyList<string> Validate()
        => new GestureMap(Slots.Select((s, i) => new GestureBinding(s.Fingers, s.Axis, Actions[i])).Where(b => b.Action != GestureAction.None).ToList())
            .Validate(ThreeFingerDrag);

    private static int IndexOf(GestureSlot slot) => IndexOf(slot.Fingers, slot.Axis);

    private static int IndexOf(int fingers, GestureAxis axis)
    {
        for (int i = 0; i < Slots.Count; i++)
            if (Slots[i].Fingers == fingers && Slots[i].Axis == axis) return i;
        throw new ArgumentOutOfRangeException(nameof(fingers), $"{fingers} 指 {axis} 不是可配置的位置");
    }
}
