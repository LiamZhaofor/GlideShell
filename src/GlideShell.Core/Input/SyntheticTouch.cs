namespace GlideShell.Core.Input;

/// <summary>
/// 合成触点帧（开发与自动化验证用）：一组手指按「步骤」移动、停住、抬起，按真实时间节奏回放到识别器，
/// 不需要真机手指就能走完整条「识别 → 路由 → 控制器 → 呈现」管线。
/// </summary>
public static class SyntheticTouch
{
    /// <summary>一个步骤：在 <see cref="DurationMs"/> 内匀速移动 (Dx, Dy) 毫米（都为 0 即原地按住）；<see cref="Lift"/> 为 true 时在此后抬起。</summary>
    public sealed record Step(double DurationMs, double DxMm = 0, double DyMm = 0, bool Lift = false);

    /// <summary>预设脚本（名称 → 指数与步骤）。坐标方向与触摸板一致：上滑 = Dy 为负。</summary>
    public static IReadOnlyDictionary<string, (int Fingers, Step[] Steps)> Scripts { get; } = new Dictionary<string, (int, Step[])>
    {
        ["overview"] = (3, [new(300, 0, -32), new(300), new(0, Lift: true)]),
        ["overview-cancel"] = (3, [new(250, 0, -9), new(250), new(0, Lift: true)]),
        ["overview-flick"] = (3, [new(70, 0, -12), new(0, Lift: true)]),
        ["switch"] = (3, [new(600, -46, 0), new(400), new(0, Lift: true)]),
        ["switch-back"] = (3, [new(400, -46, 0), new(400, 40, 0), new(300), new(0, Lift: true)]),
        ["switch-one"] = (3, [new(300, -24, 0), new(200), new(0, Lift: true)]),
    };

    /// <summary>
    /// 生成帧：每 <paramref name="intervalMs"/> 一帧，返回（相对开始的毫秒, 触点）。抬起帧里所有触点 TipSwitch=0。
    /// 手指横向间隔 15mm，从 (<paramref name="startX"/>, <paramref name="startY"/>) 开始。
    /// </summary>
    public static IEnumerable<(double AtMs, Contact[] Contacts)> Frames(int fingers, IEnumerable<Step> steps,
        double startX = 40, double startY = 55, double intervalMs = 4)
    {
        double t = 0, x = startX, y = startY;
        Contact[] At(double cx, double cy, bool tip) =>
            Enumerable.Range(0, fingers).Select(i => new Contact(i, cx + 15 * i, cy, tip, true)).ToArray();

        yield return (t, At(x, y, true));
        foreach (var step in steps)
        {
            int n = Math.Max(1, (int)Math.Round(step.DurationMs / intervalMs));
            double sx = x, sy = y;
            if (step.DurationMs > 0)
            {
                for (int k = 1; k <= n; k++)
                {
                    t += step.DurationMs / n;
                    x = sx + step.DxMm * k / n;
                    y = sy + step.DyMm * k / n;
                    yield return (t, At(x, y, true));
                }
            }
            if (step.Lift)
            {
                t += intervalMs;
                yield return (t, At(x, y, false));
                yield break;
            }
        }
    }
}
