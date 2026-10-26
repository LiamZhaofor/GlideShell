using GlideShell.Core.Gestures;
using GlideShell.Core.Input;
using GlideShell.Core.Tracing;

namespace GlideShell.Tests.Tracing;

public class CalibrationTests
{
    private static readonly TraceDevice Device = new("2808", "0218", "synthetic", 5,
        new TraceAxis(0, 4010, 124.2), new TraceAxis(0, 2443, 75.7), 16);

    private static PtpTrace Trace(string scenario, List<RawReport> reports) => new(new TraceHeader(PtpTrace.FormatName, PtpTrace.FormatVersion,
        new DateTimeOffset(2026, 10, 26, 16, 0, 0, TimeSpan.FromHours(8)), scenario, "合成", "", Device), reports);

    private static int Lx(double mm) => (int)Math.Round(mm / 124.2 * 4010);
    private static int Ly(double mm) => (int)Math.Round(mm / 75.7 * 2443);

    /// <summary>
    /// 一次三指触摸（250Hz）：第 i 根手指在 i × <paramref name="staggerMs"/> 时落下，按 <paramref name="path"/> 移动，
    /// <paramref name="durationMs"/> 时全部抬起（最后一份报文 TipSwitch=0）。
    /// </summary>
    private static void Touch(List<RawReport> reports, ref long t, double staggerMs, double durationMs, Func<double, (double X, double Y)> path)
    {
        long start = t;
        for (double ms = 0; ; ms += 4)
        {
            bool lift = ms >= durationMs;
            var (x, y) = path(Math.Min(ms, durationMs));
            var slots = new RawContact[5];
            int down = 0;
            for (int i = 0; i < 3; i++)
            {
                if (ms < i * staggerMs) continue;
                slots[down++] = new RawContact(i, !lift, true, Lx(x + 15 * i), Ly(y));
            }
            for (int k = down; k < 5; k++) slots[k] = new RawContact(0, false, false, 0, 0);
            long now = start + (long)(ms * 1000);
            reports.Add(new RawReport(now, (int)(now / 100 % 65_536), down, 0, slots));
            if (lift) break;
        }
        t = start + (long)(durationMs * 1000) + 600_000; // 两次触摸之间停 0.6 秒
    }

    private static PtpTrace Taps(double durationMs, double jitterMm, double staggerMs)
    {
        var reports = new List<RawReport>();
        long t = 0;
        for (int k = 0; k < 5; k++)
            Touch(reports, ref t, staggerMs, durationMs, ms => (40 + jitterMm * Math.Sin(ms / 20), 40 + jitterMm * Math.Cos(ms / 25) - jitterMm));
        return Trace("three-tap", reports);
    }

    private static PtpTrace SwipesUp(double staggerMs)
    {
        var reports = new List<RawReport>();
        long t = 0;
        for (int k = 0; k < 3; k++)
            Touch(reports, ref t, staggerMs, 300, ms => (40, 60 - 30 * ms / 300)); // 300ms 上滑 30mm
        return Trace("three-up-release", reports);
    }

    [Fact]
    public void Measures_taps_and_finger_landing()
    {
        var stats = Calibration.Measure([Taps(durationMs: 180, jitterMm: 0.5, staggerMs: 12), SwipesUp(staggerMs: 20)]);

        Assert.Equal(5, stats.TapDurationsMs.Count);
        Assert.All(stats.TapDurationsMs, d => Assert.InRange(d, 176, 184));
        Assert.All(stats.TapMovesMm, m => Assert.InRange(m, 0.2, 1.45)); // 两个轴各 ±0.5mm 的抖动，合起来最多约 1.4mm
        Assert.Contains(stats.LandingSpreadsMs, s => s is >= 36 and <= 44); // 三指上滑：0 / 20 / 40ms 落下
    }

    [Fact]
    public void Suggestions_leave_margin_above_measured_values_and_still_pass_the_scenarios()
    {
        var traces = new[] { Taps(durationMs: 280, jitterMm: 0.8, staggerMs: 15), SwipesUp(staggerMs: 25) };
        var current = new GestureSettings();
        // 当前默认值下，280ms 的轻点超过了 250ms 上限，不会被认作轻点
        Assert.NotNull(ScenarioExpectations.Check("three-tap", ScenarioExpectations.Recognize(traces[0], current)));

        var (suggested, notes) = Calibration.Suggest(Calibration.Measure(traces), current);

        Assert.Equal(4, notes.Count);
        Assert.True(suggested.TapMaxDurationMs >= 280 * 1.25);
        Assert.True(suggested.SlopMm >= suggested.TapMaxMoveMm + 1);
        Assert.True(suggested.FingerSettleMs >= 50 * 1.2);
        foreach (var trace in traces)
            Assert.Null(ScenarioExpectations.Check(trace.Header.Scenario, ScenarioExpectations.Recognize(trace, suggested)));
    }

    [Fact]
    public void Without_enough_data_the_current_settings_are_kept()
    {
        var current = new GestureSettings();
        var (suggested, notes) = Calibration.Suggest(Calibration.Measure([SwipesUp(staggerMs: 0)]), current);

        Assert.Equal(current with { FingerSettleMs = suggested.FingerSettleMs }, suggested); // 只有落指数据：只动指数稳定窗口
        Assert.Single(notes);
    }
}
