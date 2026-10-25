using System.Text;

namespace GlideShell.Tools.PtpProbe;

internal readonly record struct Stat(double P50, double P95, double Max, int Count)
{
    public static Stat Of(List<double> values)
    {
        if (values.Count == 0) return new Stat(double.NaN, double.NaN, double.NaN, 0);
        var sorted = values.Order().ToList();
        return new Stat(sorted[sorted.Count / 2], sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))], sorted[^1], sorted.Count);
    }
}

/// <summary>一次连续触摸：从第一份「有触点按下」的报文，到第一份「所有触点都抬起」的报文。</summary>
internal sealed record TouchSegment(
    double StartMs, double DurationMs, int MaxFingers, string CountSequence, double LandingSpreadMs,
    string Ending, double MaxGapMs, int GapsOver20Ms, double DxMm, double DyMm, double P95SpeedMmS,
    int LowConfidenceReports);

internal sealed record TraceStats(
    string File, TraceHeader Header, int Reports, double DurationS,
    Stat ScanIntervalMs, Stat ArrivalIntervalMs, double BatchedFraction,
    int LiftsWithTipUp, int VanishedWithoutLift, int ZeroCountReports, int OverflowCountReports,
    int StaleSlotTips, int ButtonReports, List<TouchSegment> Segments);

/// <summary>S1 轨迹分析：回答「抬指信号是什么、按住不动会不会停发、报告率与成批到达、轻点/落指/误触的特征」。</summary>
internal static class TraceAnalyzer
{
    private const double IdleGapMs = 100;  // 超过它的间隔视为空闲，不计入报告率统计
    private const double WrapGuardMs = 3000; // 到达间隔超过它时 ScanTime 可能已回绕，改用到达时刻

    public static TraceStats Analyze(string file, TraceHeader header, List<TraceReport> reports)
    {
        var dev = header.Device;
        int scanMod = dev.ScanBits is > 0 and < 31 ? 1 << dev.ScanBits : 1 << 16;

        // 统一时间轴（ms）：优先硬件 ScanTime（100µs，处理回绕），空闲过久时退回到达时刻
        var t = new double[reports.Count];
        var scanIntervals = new List<double>();
        var arrivalIntervals = new List<double>();
        int batched = 0;
        for (int i = 1; i < reports.Count; i++)
        {
            double arrival = (reports[i].ArrivalUs - reports[i - 1].ArrivalUs) / 1000.0;
            double scan = (((reports[i].ScanTime - reports[i - 1].ScanTime) % scanMod + scanMod) % scanMod) / 10.0;
            double dt = arrival > WrapGuardMs ? arrival : scan;
            t[i] = t[i - 1] + dt;
            if (dt <= IdleGapMs)
            {
                scanIntervals.Add(scan);
                arrivalIntervals.Add(arrival);
                if (arrival < 0.5 && scan >= 1) batched++;
            }
        }

        int zeroCount = 0, overflow = 0, stale = 0, buttons = 0, lifts = 0, vanished = 0;
        var segments = new List<TouchSegment>();
        var prevValid = new Dictionary<int, bool>(); // 上一份报文的有效触点：id → tip
        SegmentBuilder? seg = null;

        for (int i = 0; i < reports.Count; i++)
        {
            var r = reports[i];
            if (r.ContactCount == 0) zeroCount++;
            if (r.ContactCount > r.Slots.Length) overflow++;
            if (r.Buttons != 0) buttons++;
            int valid = Math.Min(r.ContactCount, r.Slots.Length);
            for (int s = valid; s < r.Slots.Length; s++)
                if (r.Slots[s].Tip) stale++;

            var current = new Dictionary<int, bool>();
            for (int s = 0; s < valid; s++) current[r.Slots[s].Id] = r.Slots[s].Tip;
            // 按触点逐个判定抬指：tip 由 1 变 0 = 带 TipSwitch=0 的正常抬指；
            // tip=1 时直接从报文里消失 = 没有抬指信号。（触点 ID 会复用，所以不能只看「消失」）
            foreach (var (id, tip) in current)
                if (!tip && prevValid.TryGetValue(id, out bool wasDown) && wasDown) lifts++;
            foreach (var (id, tip) in prevValid)
                if (tip && !current.ContainsKey(id)) vanished++;
            prevValid = current;

            int touching = current.Values.Count(tip => tip);
            if (seg is null)
            {
                if (touching > 0) seg = new SegmentBuilder(t[i], r.ArrivalUs);
                else continue;
            }

            seg.Add(r, t[i], touching, valid, dev);
            if (touching == 0)
            {
                segments.Add(seg.Build(r.ContactCount == 0 ? "cc=0 报文" : "TipSwitch=0 报文"));
                seg = null;
            }
        }
        if (seg is not null) segments.Add(seg.Build("未见抬指报文（轨迹结束时仍在按）"));

        double duration = reports.Count > 1 ? (reports[^1].ArrivalUs - reports[0].ArrivalUs) / 1e6 : 0;
        return new TraceStats(file, header, reports.Count, duration,
            Stat.Of(scanIntervals), Stat.Of(arrivalIntervals),
            arrivalIntervals.Count == 0 ? 0 : (double)batched / arrivalIntervals.Count,
            lifts, vanished, zeroCount, overflow, stale, buttons, segments);
    }

    private sealed class SegmentBuilder(double startMs, long startArrivalUs)
    {
        private readonly double _startMs = startMs;
        private double _lastMs = startMs;
        private long _lastArrivalUs = startArrivalUs;
        private int _maxFingers;
        private double? _maxReachedMs;
        private readonly List<int> _sequence = [];
        private double _maxGap;
        private int _gaps20;
        private int _lowConfidence;
        private (double X, double Y)? _firstAtMax, _lastAtMax;
        private (double X, double Y, double Ms, string Ids)? _prevAtMax;
        private readonly List<double> _speeds = [];

        public void Add(TraceReport r, double ms, int touching, int valid, TraceDevice dev)
        {
            double gap = (r.ArrivalUs - _lastArrivalUs) / 1000.0;
            _lastArrivalUs = r.ArrivalUs;
            _maxGap = Math.Max(_maxGap, gap);
            if (gap > 20) _gaps20++;

            if (_sequence.Count == 0 || _sequence[^1] != touching) _sequence.Add(touching);
            if (touching > _maxFingers)
            {
                _maxFingers = touching;
                _maxReachedMs = ms;
                _firstAtMax = _lastAtMax = null;
                _prevAtMax = null;
                _speeds.Clear();
            }

            bool lowConfidence = false;
            double sx = 0, sy = 0;
            var ids = new StringBuilder();
            for (int s = 0; s < valid; s++)
            {
                var c = r.Slots[s];
                if (!c.Tip) continue;
                if (!c.Confidence) lowConfidence = true;
                sx += ToMm(c.X, dev.X);
                sy += ToMm(c.Y, dev.Y);
                ids.Append(c.Id).Append(',');
            }
            if (lowConfidence) _lowConfidence++;

            if (touching > 0 && touching == _maxFingers)
            {
                var centroid = (X: sx / touching, Y: sy / touching);
                _firstAtMax ??= centroid;
                _lastAtMax = centroid;
                string idSet = ids.ToString();
                if (_prevAtMax is { } p && p.Ids == idSet && ms > p.Ms)
                {
                    double dist = Math.Sqrt(Math.Pow(centroid.X - p.X, 2) + Math.Pow(centroid.Y - p.Y, 2));
                    _speeds.Add(dist / ((ms - p.Ms) / 1000.0));
                }
                _prevAtMax = (centroid.X, centroid.Y, ms, idSet);
            }
            _lastMs = ms;
        }

        public TouchSegment Build(string ending)
        {
            var first = _firstAtMax ?? (0, 0);
            var last = _lastAtMax ?? first;
            return new TouchSegment(
                _startMs, _lastMs - _startMs, _maxFingers, string.Join("→", _sequence),
                (_maxReachedMs ?? _startMs) - _startMs, ending, _maxGap, _gaps20,
                last.X - first.X, last.Y - first.Y, Stat.Of(_speeds).P95, _lowConfidence);
        }
    }

    private static double ToMm(int value, AxisInfo axis)
    {
        double span = Math.Max(1, axis.Max - axis.Min);
        return (value - axis.Min) / span * (axis.Mm ?? span);
    }

    // ---------- 输出 ----------

    public static string QuickSummary(TraceStats s)
    {
        int lifted = s.Segments.Count(g => !g.Ending.StartsWith("未见", StringComparison.Ordinal));
        double maxGap = s.Segments.Count == 0 ? 0 : s.Segments.Max(g => g.MaxGapMs);
        return $"报文 {s.Reports} · ≈{Hz(s.ScanIntervalMs.P50)}Hz · 触摸段 {s.Segments.Count}（有抬指报文 {lifted}）· " +
               $"抬指带 TipSwitch=0 {s.LiftsWithTipUp} 次 / 无信号消失 {s.VanishedWithoutLift} 次 · 段内最长空档 {maxGap:F1}ms";
    }

    public static void Print(TraceStats s, TextWriter o)
    {
        var h = s.Header;
        o.WriteLine($"== {Path.GetFileName(s.File)} — {h.Title}（{h.RecordedAt.LocalDateTime:yyyy-MM-dd HH:mm}）");
        o.WriteLine($"   报文 {s.Reports} · 时长 {s.DurationS:F1}s · ScanTime 间隔 P50 {s.ScanIntervalMs.P50:F1}ms P95 {s.ScanIntervalMs.P95:F1}ms（≈{Hz(s.ScanIntervalMs.P50)}Hz）");
        o.WriteLine($"   到达间隔 P50 {s.ArrivalIntervalMs.P50:F1}ms P95 {s.ArrivalIntervalMs.P95:F1}ms max {s.ArrivalIntervalMs.Max:F1}ms · 成批到达 {s.BatchedFraction:P0}");
        o.WriteLine($"   触点抬起带 TipSwitch=0 {s.LiftsWithTipUp} 次 · 按下状态直接消失 {s.VanishedWithoutLift} 次 · cc=0 报文 {s.ZeroCountReports} 份 · " +
                    $"cc 超出槽数 {s.OverflowCountReports} · 槽外残留 tip {s.StaleSlotTips} · 物理按键报文 {s.ButtonReports}");
        o.WriteLine($"   触摸段 {s.Segments.Count} 个：");
        int n = 0;
        foreach (var g in s.Segments)
        {
            n++;
            string dy = Signed(-g.DyMm, "↑", "↓"); // PTP 的 Y 向下增大
            string dx = Signed(g.DxMm, "→", "←");
            o.WriteLine($"     #{n,-2} @{g.StartMs / 1000:F2}s 时长 {g.DurationMs:F0}ms · 指数 {g.CountSequence} · 落指错开 {g.LandingSpreadMs:F0}ms · " +
                        $"位移 {dy}mm {dx}mm · P95 速度 {g.P95SpeedMmS:F0}mm/s · 段内最长空档 {g.MaxGapMs:F1}ms（>20ms {g.GapsOver20Ms} 次）· " +
                        $"结束于 {g.Ending}{(g.LowConfidenceReports > 0 ? $" · 低置信报文 {g.LowConfidenceReports}" : "")}");
        }
    }

    /// <summary>整个目录的 S1 结论草稿：把各场景的统计归并成对 ROADMAP 问题的直接回答。</summary>
    public static void PrintConclusions(List<TraceStats> all, TextWriter o)
    {
        var segs = all.SelectMany(s => s.Segments.Select(g => (s, g))).ToList();
        o.WriteLine();
        o.WriteLine("== S1 结论草稿");

        int ended = segs.Count(x => x.g.Ending.StartsWith("TipSwitch", StringComparison.Ordinal));
        int endedZero = segs.Count(x => x.g.Ending.StartsWith("cc=0", StringComparison.Ordinal));
        int lifts = all.Sum(s => s.LiftsWithTipUp), vanished = all.Sum(s => s.VanishedWithoutLift);
        o.WriteLine($"1. 抬指信号：{segs.Count} 个触摸段中，以 TipSwitch=0 报文结束 {ended} 个、以 cc=0 报文结束 {endedZero} 个、" +
                    $"未见抬指 {segs.Count - ended - endedZero} 个；逐触点看，带 TipSwitch=0 抬起 {lifts} 次、按下状态直接消失 {vanished} 次；" +
                    $"cc=0 报文共 {all.Sum(s => s.ZeroCountReports)} 份。");

        var holds = all.Where(s => s.Header.Scenario == "three-up-hold").SelectMany(s => s.Segments).ToList();
        if (holds.Count > 0)
        {
            double holdGap = holds.Max(g => g.MaxGapMs);
            o.WriteLine($"2. 按住不动：three-up-hold 段内最长空档 {holdGap:F1}ms，>20ms 的空档 {holds.Sum(g => g.GapsOver20Ms)} 次 → " +
                        (holdGap > 20 ? "设备在手指静止时会停发报文，「没有报文」不能当作抬指。" : "手指静止时设备仍持续报告。"));
        }
        double worstGap = segs.Count == 0 ? 0 : segs.Max(x => x.g.MaxGapMs);
        o.WriteLine($"   全部场景触摸期间最长空档 {worstGap:F1}ms。");

        var scanP50 = Stat.Of(all.Where(s => s.ScanIntervalMs.Count > 0).Select(s => s.ScanIntervalMs.P50).ToList());
        o.WriteLine($"3. 报告率：各场景 ScanTime 间隔 P50 的中位数 {scanP50.P50:F1}ms（≈{Hz(scanP50.P50)}Hz）；" +
                    $"成批到达比例最高 {all.Max(s => s.BatchedFraction):P0}（本工具的输入线程只做解析，成批说明是系统投递层面的）。");

        var taps = all.Where(s => s.Header.Scenario == "three-tap").SelectMany(s => s.Segments).Where(g => g.MaxFingers >= 3).ToList();
        if (taps.Count > 0)
            o.WriteLine($"4. 三指轻点：{taps.Count} 次，时长 {taps.Min(g => g.DurationMs):F0}–{taps.Max(g => g.DurationMs):F0}ms，" +
                        $"质心位移最大 {taps.Max(g => Math.Sqrt(g.DxMm * g.DxMm + g.DyMm * g.DyMm)):F1}mm，落指错开最大 {taps.Max(g => g.LandingSpreadMs):F0}ms。");

        var landings = segs.Where(x => x.g.MaxFingers >= 3 && x.s.Header.Scenario != "staggered-landing").Select(x => x.g.LandingSpreadMs).ToList();
        if (landings.Count > 0)
        {
            var l = Stat.Of(landings);
            o.WriteLine($"5. 三指以上「同时」落下的错开时间：P50 {l.P50:F0}ms P95 {l.P95:F0}ms max {l.Max:F0}ms（指数稳定窗口的依据）。");
        }

        var flicks = all.Where(s => s.Header.Scenario == "three-flick-up").SelectMany(s => s.Segments).Where(g => g.MaxFingers >= 3).ToList();
        if (flicks.Count > 0)
            o.WriteLine($"6. 三指快甩：P95 速度 {flicks.Min(g => g.P95SpeedMmS):F0}–{flicks.Max(g => g.P95SpeedMmS):F0}mm/s，时长 {flicks.Min(g => g.DurationMs):F0}–{flicks.Max(g => g.DurationMs):F0}ms。");

        int palmLow = all.Where(s => s.Header.Scenario == "palm").Sum(s => s.Segments.Sum(g => g.LowConfidenceReports));
        int otherLow = all.Where(s => s.Header.Scenario != "palm").Sum(s => s.Segments.Sum(g => g.LowConfidenceReports));
        o.WriteLine($"7. Confidence：palm 场景低置信报文 {palmLow} 份，其他场景 {otherLow} 份。");
        o.WriteLine($"8. 固件怪癖：cc 超出槽数 {all.Sum(s => s.OverflowCountReports)} 份，槽外残留 tip {all.Sum(s => s.StaleSlotTips)} 次。");
    }

    private static string Signed(double mm, string positive, string negative)
        => Math.Abs(mm) < 0.05 ? "0.0" : mm > 0 ? $"{positive}{mm:F1}" : $"{negative}{-mm:F1}";

    private static string Hz(double intervalMs) => double.IsNaN(intervalMs) || intervalMs <= 0 ? "?" : $"{1000 / intervalMs:F0}";
}
