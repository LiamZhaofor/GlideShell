using GlideShell.Core.Gestures;
using GlideShell.Core.Input;
using GlideShell.Core.Tracing;

namespace GlideShell.Tests.Tracing;

public class TraceReplayTests
{
    private static readonly TraceDevice Device = new("2808", "0218", "synthetic", 5,
        new TraceAxis(0, 4010, 124.2), new TraceAxis(0, 2443, 75.7), 16);

    private static TraceHeader Header(string scenario) => new(PtpTrace.FormatName, PtpTrace.FormatVersion,
        new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.FromHours(8)), scenario, "合成", "三指上滑", Device);

    /// <summary>按设备逻辑坐标生成一次三指上滑（250Hz），最后一份报文 TipSwitch=0。</summary>
    private static List<RawReport> SwipeUpReports(double distanceMm, int frames, int startScan = 65_000)
    {
        var reports = new List<RawReport>();
        int scan = startScan;
        long t = 0;
        int Lx(double mm) => (int)Math.Round(mm / 124.2 * 4010);
        int Ly(double mm) => (int)Math.Round(mm / 75.7 * 2443);
        RawContact[] Slots(double yMm, bool tip) =>
        [
            new(0, tip, true, Lx(40), Ly(yMm)), new(1, tip, true, Lx(55), Ly(yMm)), new(2, tip, true, Lx(70), Ly(yMm)),
            new(0, false, false, 0, 0), new(0, false, false, 0, 0),
        ];
        for (int k = 0; k <= frames; k++)
        {
            reports.Add(new RawReport(t, scan, 3, 0, Slots(60 - distanceMm * k / frames, tip: true)));
            t += 4_000;
            scan = (scan + 40) % 65_536;
        }
        reports.Add(new RawReport(t, scan, 3, 0, Slots(60 - distanceMm, tip: false)));
        return reports;
    }

    [Fact]
    public void Trace_round_trips_through_the_jsonl_format()
    {
        var reports = SwipeUpReports(30, 75);
        var text = new StringWriter();
        using (var writer = new PtpTraceWriter(text, Header("three-up-release")))
            foreach (var r in reports) writer.Write(r);

        var trace = PtpTrace.Read(new StringReader(text.ToString()));

        Assert.Equal("three-up-release", trace.Header.Scenario);
        Assert.Equal(Device, trace.Header.Device);
        Assert.Equal(reports.Count, trace.Reports.Count);
        Assert.Equal(reports[10].Slots, trace.Reports[10].Slots);
        Assert.Equal(reports[^1].ScanTime, trace.Reports[^1].ScanTime);
    }

    [Fact]
    public void Replaying_a_trace_through_assembler_and_recognizer_reproduces_the_swipe()
    {
        var trace = new PtpTrace(Header("three-up-release"), SwipeUpReports(30, 75)); // 300ms，100mm/s，跨过 ScanTime 回绕
        var events = new List<GestureEvent>();
        var recognizer = new GestureRecognizer(new GestureSettings(), events.Add);
        trace.Replay(new FrameAssembler(trace.Header.Device.ToGeometry(), recognizer.Process));

        var end = Assert.Single(events, e => e.Phase == GesturePhase.Ended);
        Assert.Equal(GestureDirection.Up, end.Direction);
        Assert.Equal(3, end.Fingers);
        Assert.Equal(100, end.AlongVelocityMmS, 0);
        Assert.InRange(end.AlongMm, 25.5, 26.5); // 逻辑坐标取整带来亚毫米误差
    }
}

/// <summary>
/// 真机轨迹回放：把 PtpProbe 录的轨迹放进 tests/GlideShell.Tests/Traces/（随测试一起复制到输出目录），
/// 按场景检查识别结果。没有夹具时跳过。
/// </summary>
public class RecordedTraceTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Traces");

    [Fact]
    public void Recorded_traces_are_recognized_as_their_scenario()
    {
        var files = Directory.Exists(FixtureDir) ? Directory.GetFiles(FixtureDir, "*.jsonl") : [];
        if (files.Length == 0) Assert.Skip("还没有真机轨迹夹具（S1 录制后放入 Traces/）");

        var failures = new List<string>();
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var trace = PtpTrace.Load(file);
            var events = new List<GestureEvent>();
            var recognizer = new GestureRecognizer(new GestureSettings(), events.Add);
            trace.Replay(new FrameAssembler(trace.Header.Device.ToGeometry(), recognizer.Process));
            string? problem = Check(trace.Header.Scenario, events);
            if (problem is not null) failures.Add($"{Path.GetFileName(file)}（{trace.Header.Scenario}）：{problem}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static string? Check(string scenario, List<GestureEvent> events)
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
