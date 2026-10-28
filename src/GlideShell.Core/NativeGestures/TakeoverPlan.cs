using GlideShell.Core.Gestures;

namespace GlideShell.Core.NativeGestures;

/// <summary>接管时改动的一项：原值（null = 原本不存在）与写入值。</summary>
public sealed record TakeoverChange(string Name, int Id, int? Original, int Written);

/// <summary>
/// 一次接管的记录。持久化到磁盘，主进程退出、托盘「退回原生」、看门狗发现崩溃时都按它还原。
/// 只记录改过的项：还原不会碰用户没让我们改的设置。
/// </summary>
public sealed record TakeoverRecord(DateTimeOffset TakenAt, string OsVersion, IReadOnlyList<TakeoverChange> Changes)
{
    public bool IsEmpty => Changes.Count == 0;
}

/// <summary>还原时对一项要做的事。</summary>
public enum RestoreAction
{
    /// <summary>写回原值。</summary>
    WriteOriginal,

    /// <summary>原本不存在：删除该值（Explorer 回到缺省值）。</summary>
    Delete,

    /// <summary>当前值已不是我们写的（用户在接管期间自己改过）：保留用户的设置。</summary>
    KeepUserChange,

    /// <summary>当前值已经是原值：无需改动。</summary>
    AlreadyRestored,
}

/// <summary>
/// 计算「要接管哪些系统手势」与「怎样还原」（纯函数）。原则：
/// <list type="bullet">
/// <item>只接管映射表里 GlideShell 绑定的（指数, 轴）；同指数的其他方向保持用户当前的实际效果。</item>
/// <item>轻点不动，只有一个例外：GlideShell 自己做三指轻点中键时，系统的三指轻点置为「无」（否则会各点一次）。</item>
/// <item>某指数的总开关是预设模式时，先展开成自定义模式下的等效方向值，再把被接管的方向置为「无」。</item>
/// </list>
/// </summary>
public static class TakeoverPlanner
{
    /// <param name="current">注册表当前值（null = 不存在，Explorer 使用缺省值）。</param>
    /// <param name="threeFingerTap">GlideShell 自己处理三指轻点（中键）：系统的三指轻点置为「无」。</param>
    /// <returns>需要写入的项（已与当前值相同的不列出）与警告。</returns>
    public static (IReadOnlyList<TakeoverChange> Changes, IReadOnlyList<string> Warnings) Plan(
        GestureMap map, Func<string, int?> current, bool threeFingerTap = false)
    {
        var changes = new List<TakeoverChange>();
        var warnings = new List<string>();
        if (threeFingerTap && current(TouchpadSettings.ThreeFingerTap.Name) != TouchpadSettings.ActionNothing)
            changes.Add(new TakeoverChange(TouchpadSettings.ThreeFingerTap.Name, TouchpadSettings.ThreeFingerTap.Id,
                current(TouchpadSettings.ThreeFingerTap.Name), TouchpadSettings.ActionNothing));
        foreach (int fingers in new[] { 3, 4 })
        {
            bool vertical = map.Resolve(fingers, GestureAxis.Vertical) != GestureAction.None;
            bool horizontal = map.Resolve(fingers, GestureAxis.Horizontal) != GestureAction.None;
            if (!vertical && !horizontal) continue;

            var g = TouchpadSettings.Group(fingers);
            int Effective(TouchpadSettings.Setting s) => current(s.Name) ?? s.Default;
            int mode = Effective(g.Slide);

            (int Left, int Up, int Right, int Down) dirs;
            if (mode == TouchpadSettings.CustomMode)
            {
                dirs = (Effective(g.Left), Effective(g.Up), Effective(g.Right), Effective(g.Down));
            }
            else if (TouchpadSettings.ExpandPreset(mode) is { } expanded)
            {
                dirs = expanded;
                if (mode == 3 && !(vertical && horizontal))
                    warnings.Add($"{fingers} 指当前是「音频和音量」预设，保留未接管方向时按推断展开（未经验证）");
            }
            else
            {
                dirs = (0, 0, 0, 0);
                warnings.Add($"{fingers} 指总开关是未知值 {mode}，未接管的方向一并置为「无」");
            }

            if (vertical) dirs = dirs with { Up = TouchpadSettings.ActionNothing, Down = TouchpadSettings.ActionNothing };
            if (horizontal) dirs = dirs with { Left = TouchpadSettings.ActionNothing, Right = TouchpadSettings.ActionNothing };

            void Want(TouchpadSettings.Setting s, int value)
            {
                int? now = current(s.Name);
                if (now != value) changes.Add(new TakeoverChange(s.Name, s.Id, now, value));
            }
            Want(g.Slide, TouchpadSettings.CustomMode);
            Want(g.Left, dirs.Left);
            Want(g.Up, dirs.Up);
            Want(g.Right, dirs.Right);
            Want(g.Down, dirs.Down);
        }
        return (changes, warnings);
    }

    /// <summary>还原计划：逐项比较当前值与当初写入的值，用户改过的不覆盖。</summary>
    public static IReadOnlyList<(TakeoverChange Change, RestoreAction Action)> PlanRestore(TakeoverRecord record, Func<string, int?> current)
    {
        var result = new List<(TakeoverChange, RestoreAction)>();
        foreach (var c in record.Changes)
        {
            int? now = current(c.Name);
            RestoreAction action =
                now == c.Original ? RestoreAction.AlreadyRestored :
                now != c.Written ? RestoreAction.KeepUserChange :
                c.Original is null ? RestoreAction.Delete :
                RestoreAction.WriteOriginal;
            result.Add((c, action));
        }
        return result;
    }
}
