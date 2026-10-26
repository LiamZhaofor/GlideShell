using GlideShell.Core.Gestures;
using GlideShell.Core.Input;

namespace GlideShell.Core.Tracing;

/// <summary>
/// S1 录制场景的期望识别结果。回放测试（真机轨迹夹具）与 <c>PtpProbe calibrate</c> 共用同一套判定。
/// </summary>
public static class ScenarioExpectations
{
    /// <summary>按录制时的到达时刻回放轨迹，收集识别出的全部手势事件。</summary>
    public static List<GestureEvent> Recognize(PtpTrace trace, GestureSettings settings)
    {
        var events = new List<GestureEvent>();
        var recognizer = new GestureRecognizer(settings, events.Add);
        trace.Replay(new FrameAssembler(trace.Header.Device.ToGeometry(), recognizer.Process));
        return events;
    }

    /// <summary>识别结果是否符合场景期望；符合返回 null，否则返回问题描述。未知场景一律视为符合。</summary>
    public static string? Check(string scenario, IReadOnlyList<GestureEvent> events)
    {
        var swipes = events.Where(e => e.Kind == GestureKind.Swipe && e.Phase == GesturePhase.Ended).ToList();
        var taps = events.Where(e => e.Kind == GestureKind.Tap).ToList();
        if (events.Any(e => e.Phase == GesturePhase.Cancelled)) return "出现了 Cancelled（正常录制里不应该有）";

        string? Expect(bool ok, string message) => ok ? null : $"{message}；实际 滑动 {Describe(swipes)}，轻点 {taps.Count} 次";
        bool All(int fingers, params GestureDirection[] dirs) => swipes.All(s => s.Fingers >= fingers && dirs.Contains(s.Direction));

        return scenario switch
        {
            "three-up-release" => Expect(swipes.Count >= 3 && All(3, GestureDirection.Up), "应有 ≥3 次三指上滑"),
            "three-up-hold" => Expect(swipes.Count >= 3 && All(3, GestureDirection.Up) && swipes.All(s => Math.Abs(s.AlongVelocityMmS) < 30),
                "应有 ≥3 次三指上滑，且停住后松手速度 < 30mm/s"),
            "three-flick-up" => Expect(swipes.Count >= 3 && All(3, GestureDirection.Up) && swipes.All(s => s.AlongVelocityMmS > 150),
                "应有 ≥3 次三指上甩，松手速度 > 150mm/s"),
            "three-down" => Expect(swipes.Count >= 3 && All(3, GestureDirection.Down), "应有 ≥3 次三指下滑"),
            "three-horizontal" => Expect(swipes.Count >= 4 && All(3, GestureDirection.Left, GestureDirection.Right), "应有 ≥4 次三指横滑"),
            "three-tap" => Expect(taps.Count >= 4 && swipes.Count == 0, "应有 ≥4 次三指轻点、没有滑动"),
            "three-reverse" => Expect(swipes.Count >= 2 && All(3, GestureDirection.Up), "应有 ≥2 次三指上滑（含反向）"),
            "staggered-landing" => Expect(swipes.Count >= 2 && swipes.All(s => s.Fingers == 3), "应有 ≥2 次三指滑动"),
            "finger-change" => Expect(swipes.Count >= 2, "应有 ≥2 次滑动且不中断"),
            "four-up" => Expect(swipes.Count >= 2 && All(4, GestureDirection.Up), "应有 ≥2 次四指上滑"),
            "four-horizontal" => Expect(swipes.Count >= 2 && All(4, GestureDirection.Left, GestureDirection.Right), "应有 ≥2 次四指横滑"),
            "two-scroll" or "palm" or "warmup" => Expect(swipes.Count == 0 && taps.Count == 0, "不应识别出任何三指以上手势"),
            _ => null,
        };
    }

    private static string Describe(List<GestureEvent> swipes)
        => swipes.Count == 0 ? "0 次" : string.Join(" ", swipes.Select(s => $"{s.Fingers}指{s.Direction}({s.AlongMm:F0}mm,{s.AlongVelocityMmS:F0}mm/s)"));
}

/// <summary>从轨迹里量出来的、与识别阈值直接相关的手指特征。</summary>
/// <param name="TapDurationsMs">三指轻点场景里每次轻点：第一根手指落下到全部抬起。</param>
/// <param name="TapMovesMm">每次轻点期间质心离开起点的最大距离（只在指数达到最大后计）。</param>
/// <param name="LandingSpreadsMs">三指以上「同时」落下时，第一根到最后一根手指落下的间隔（不含故意依次落指、加减指的场景）。</param>
public sealed record TouchStats(IReadOnlyList<double> TapDurationsMs, IReadOnlyList<double> TapMovesMm, IReadOnlyList<double> LandingSpreadsMs);

/// <summary>某个参数的建议值与依据。</summary>
public sealed record CalibrationNote(string Parameter, double Current, double Suggested, string Reason);

/// <summary>
/// S1 校准：从真机轨迹量出轻点时长 / 抖动、落指错开时间，推出识别参数的建议值。
/// 规则偏保守（在实测最大值上留余量），再由 <see cref="ScenarioExpectations"/> 回放全部场景验证，不只看统计。
/// </summary>
public static class Calibration
{
    private static readonly HashSet<string> SkipLanding = ["staggered-landing", "finger-change", "palm", "two-scroll", "warmup"];

    public static TouchStats Measure(IEnumerable<PtpTrace> traces)
    {
        var tapDurations = new List<double>();
        var tapMoves = new List<double>();
        var landings = new List<double>();
        foreach (var trace in traces)
        {
            string scenario = trace.Header.Scenario;
            var segments = Segments(trace);
            foreach (var s in segments.Where(s => s.MaxFingers >= 3))
            {
                if (scenario == "three-tap")
                {
                    tapDurations.Add(s.DurationMs);
                    tapMoves.Add(s.MaxMoveMm);
                }
                if (!SkipLanding.Contains(scenario)) landings.Add(s.LandingSpreadMs);
            }
        }
        return new TouchStats(tapDurations, tapMoves, landings);
    }

    /// <summary>建议的识别参数（只调整与轨迹有关的四项，其余保持 <paramref name="current"/>）。数据不足的项保持原值。</summary>
    public static (GestureSettings Settings, IReadOnlyList<CalibrationNote> Notes) Suggest(TouchStats stats, GestureSettings current)
    {
        var notes = new List<CalibrationNote>();
        double settle = current.FingerSettleMs, tapMs = current.TapMaxDurationMs, tapMove = current.TapMaxMoveMm, slop = current.SlopMm;

        if (stats.LandingSpreadsMs.Count >= 3)
        {
            double p95 = Percentile(stats.LandingSpreadsMs, 0.95);
            settle = Math.Clamp(CeilTo(p95 * 1.2 + 5, 5), 20, 150);
            notes.Add(new("FingerSettleMs", current.FingerSettleMs, settle, $"落指错开 P95 {p95:F0}ms（{stats.LandingSpreadsMs.Count} 次）× 1.2 + 5"));
        }
        if (stats.TapDurationsMs.Count >= 3)
        {
            double max = stats.TapDurationsMs.Max();
            tapMs = Math.Clamp(CeilTo(max * 1.25, 10), 150, 500);
            notes.Add(new("TapMaxDurationMs", current.TapMaxDurationMs, tapMs, $"轻点最长 {max:F0}ms（{stats.TapDurationsMs.Count} 次）× 1.25"));
        }
        if (stats.TapMovesMm.Count >= 3)
        {
            double max = stats.TapMovesMm.Max();
            tapMove = Math.Clamp(CeilTo(max * 1.5 + 0.3, 0.1), 0.5, 5);
            // 滑动阈值必须明显大于轻点抖动，否则轻点会被当成滑动；在此之上越小越跟手
            slop = Math.Clamp(CeilTo(Math.Max(2.0, tapMove + 1.0), 0.5), 2, 6);
            notes.Add(new("TapMaxMoveMm", current.TapMaxMoveMm, tapMove, $"轻点抖动最大 {max:F2}mm × 1.5 + 0.3"));
            notes.Add(new("SlopMm", current.SlopMm, slop, $"轻点判定上限 {tapMove:F1}mm + 1mm 余量（不低于 2mm）"));
        }
        return (current with { FingerSettleMs = settle, TapMaxDurationMs = tapMs, TapMaxMoveMm = Math.Min(tapMove, slop), SlopMm = slop }, notes);
    }

    private readonly record struct Segment(double DurationMs, int MaxFingers, double LandingSpreadMs, double MaxMoveMm);

    /// <summary>按「有手指按下 → 全部抬起」切分触摸段（设备时间）。</summary>
    private static List<Segment> Segments(PtpTrace trace)
    {
        var result = new List<Segment>();
        bool active = false;
        long start = 0, maxReachedAt = 0;
        int maxFingers = 0;
        double originX = 0, originY = 0, maxMove = 0;
        var assembler = new FrameAssembler(trace.Header.Device.ToGeometry(), frame =>
        {
            int touching = frame.TouchingCount;
            long t = frame.DeviceTimeUs;
            if (!active)
            {
                if (touching == 0) return;
                active = true;
                start = t;
                maxFingers = 0;
                maxMove = 0;
            }
            if (touching == 0)
            {
                result.Add(new Segment((t - start) / 1000.0, maxFingers, (maxReachedAt - start) / 1000.0, maxMove));
                active = false;
                return;
            }
            var (cx, cy) = Centroid(frame);
            if (touching > maxFingers)
            {
                maxFingers = touching;
                maxReachedAt = t;
                (originX, originY) = (cx, cy);
                maxMove = 0;
            }
            else if (touching == maxFingers)
            {
                maxMove = Math.Max(maxMove, Math.Sqrt((cx - originX) * (cx - originX) + (cy - originY) * (cy - originY)));
            }
        });
        trace.Replay(assembler);
        return result;
    }

    private static (double X, double Y) Centroid(ContactFrame frame)
    {
        double x = 0, y = 0;
        int n = 0;
        foreach (var c in frame.Contacts)
        {
            if (!c.Tip) continue;
            x += c.XMm;
            y += c.YMm;
            n++;
        }
        return n == 0 ? (0, 0) : (x / n, y / n);
    }

    private static double Percentile(IReadOnlyList<double> values, double p)
    {
        var sorted = values.Order().ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(sorted.Count * p))];
    }

    private static double CeilTo(double value, double step) => Math.Round(Math.Ceiling(value / step - 1e-9) * step, 3);
}
