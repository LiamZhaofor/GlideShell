using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GlideShell.Core.Gestures;
using GlideShell.Core.Input;
using GlideShell.Core.Tracing;
using GlideShell.Tools.PtpProbe;
using static GlideShell.Tools.PtpProbe.Native;

// PtpProbe：精确式触摸板（PTP）的只读探针与 S1 轨迹录制工具。不读写任何系统设置。
//   （无参数）   打印报告描述符：触点槽数、TipSwitch/Confidence、物理尺寸、ScanTime
//   record      按场景引导录制轨迹（JSON Lines），见 Scenarios.cs
//   analyze     分析轨迹文件或目录，目录会额外输出 S1 结论草稿
//   replay      把轨迹跑过 GlideShell.Core 的组帧器与手势识别器，打印识别结果（调参用）
//   scenarios   列出录制场景
//   selftest    自检原始输入管道：注入一次 1 像素的鼠标移动（随即移回），确认消息窗口能收到 WM_INPUT
Console.OutputEncoding = Encoding.UTF8;

string command = args.Length > 0 ? args[0] : "describe";
return command switch
{
    "describe" => Describe(),
    "record" => Recorder.Run(Option(args, "--out"), SelectScenarios(Option(args, "--only"))),
    "analyze" when args.Length > 1 => Analyze(args[1]),
    "replay" when args.Length > 1 => Replay(args[1], args),
    "scenarios" => ListScenarios(),
    "selftest" => SelfTest(),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        用法：
          PtpProbe                         打印触摸板报告描述符
          PtpProbe record [--out 目录] [--only 2,3,8]
                                           按场景引导录制轨迹（默认输出到 artifacts/traces/<时间>/）
          PtpProbe analyze <文件或目录>     分析轨迹；目录会额外输出 S1 结论草稿
          PtpProbe replay <文件或目录> [--slop 3] [--settle 40] [--tap-ms 250] [--tap-move 2]
                                           把轨迹跑过 GlideShell 手势识别器，打印识别出的手势（可调参数对比）
          PtpProbe scenarios               列出录制场景
          PtpProbe selftest                自检原始输入管道
        """);
    return 2;
}

static string? Option(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static IReadOnlyList<Scenario> SelectScenarios(string? only)
{
    if (only is null) return Scenarios.All;
    var picked = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(int.Parse).Where(n => n >= 1 && n <= Scenarios.All.Length)
                     .Select(n => Scenarios.All[n - 1]).ToList();
    return picked.Count > 0 ? picked : Scenarios.All;
}

static int ListScenarios()
{
    for (int i = 0; i < Scenarios.All.Length; i++)
    {
        var s = Scenarios.All[i];
        Console.WriteLine($"{i + 1,2}. {s.Title}（{s.Seconds}s）— {s.Instructions}");
        Console.WriteLine($"    用途：{s.Purpose}");
    }
    return 0;
}

static int Analyze(string path)
{
    var files = Directory.Exists(path)
        ? Directory.GetFiles(path, "*.jsonl").Order(StringComparer.Ordinal).ToList()
        : [path];
    if (files.Count == 0)
    {
        Console.Error.WriteLine($"{path} 下没有 .jsonl 轨迹文件。");
        return 1;
    }

    var all = new List<TraceStats>();
    foreach (var file in files)
    {
        var trace = PtpTrace.Load(file);
        var stats = TraceAnalyzer.Analyze(file, trace.Header, trace.Reports.ToList());
        TraceAnalyzer.Print(stats, Console.Out);
        all.Add(stats);
    }
    if (files.Count > 1) TraceAnalyzer.PrintConclusions(all, Console.Out);
    return 0;
}

static int Replay(string path, string[] args)
{
    var files = Directory.Exists(path)
        ? Directory.GetFiles(path, "*.jsonl").Order(StringComparer.Ordinal).ToList()
        : [path];
    double Num(string name, double fallback)
        => Option(args, name) is { } v && double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : fallback;
    var defaults = new GestureSettings();
    var settings = defaults with
    {
        SlopMm = Num("--slop", defaults.SlopMm),
        FingerSettleMs = Num("--settle", defaults.FingerSettleMs),
        TapMaxDurationMs = Num("--tap-ms", defaults.TapMaxDurationMs),
        TapMaxMoveMm = Num("--tap-move", defaults.TapMaxMoveMm),
    };
    Console.WriteLine($"识别参数：阈值 {settings.SlopMm}mm · 指数稳定 {settings.FingerSettleMs}ms · 轻点 ≤{settings.TapMaxDurationMs}ms 且 ≤{settings.TapMaxMoveMm}mm");

    foreach (var file in files)
    {
        var trace = PtpTrace.Load(file);
        Console.WriteLine($"== {Path.GetFileName(file)} — {trace.Header.Title}");
        int changed = 0;
        var recognizer = new GestureRecognizer(settings, e =>
        {
            if (e.Phase == GesturePhase.Changed) { changed++; return; }
            string what = e.Kind == GestureKind.Tap ? $"Tap {e.Fingers}指" : $"{e.Phase} {e.Fingers}指";
            bool swipe = e.Kind == GestureKind.Swipe;
            string motion = swipe && e.Phase is GesturePhase.Began or GesturePhase.Ended ? $" {e.Direction}" : "";
            if (swipe && e.Phase == GesturePhase.Ended)
            {
                double v = Math.Abs(e.AlongVelocityMmS) < 0.5 ? 0 : e.AlongVelocityMmS;
                motion += $" 位移 {e.AlongMm:F1}mm 速度 {v:F0}mm/s（Changed {changed} 次）";
            }
            if (e.Phase == GesturePhase.Began) changed = 0;
            Console.WriteLine($"   @{e.HostTimeUs / 1e6:F2}s {what}{motion}");
        });
        trace.Replay(new FrameAssembler(trace.Header.Device.ToGeometry(), recognizer.Process));
    }
    return 0;
}

static int SelfTest()
{
    var devices = PtpDevice.EnumerateAll();
    if (devices.Count == 0)
    {
        Console.Error.WriteLine("未找到精确式触摸板。");
        return 1;
    }
    foreach (var other in devices.Skip(1)) other.Dispose();
    using var device = devices[0];
    using var listener = new RawInputListener(device, includeMouse: true);
    Console.WriteLine($"注册触摸板 + 鼠标原始输入（INPUTSINK，消息窗口）：{(listener.Registered ? "成功" : $"失败 {listener.Error}")}");
    if (!listener.Registered) return 1;

    var moves = new[]
    {
        new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = 1, dwFlags = MOUSEEVENTF_MOVE } },
        new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = -1, dwFlags = MOUSEEVENTF_MOVE } },
    };
    uint sent = SendInput((uint)moves.Length, moves, Marshal.SizeOf<INPUT>());
    var sw = Stopwatch.StartNew();
    while (Interlocked.Read(ref listener.MouseEvents) == 0 && sw.ElapsedMilliseconds < 1000) Thread.Sleep(10);
    long received = Interlocked.Read(ref listener.MouseEvents);
    Console.WriteLine($"注入鼠标移动 {sent} 次，{sw.ElapsedMilliseconds}ms 内收到 WM_INPUT {received} 次 → 管道{(received > 0 ? "正常" : "异常")}");
    return received > 0 ? 0 : 1;
}

static int Describe()
{
    var devices = PtpDevice.EnumerateAll();
    if (devices.Count == 0)
    {
        Console.WriteLine("未找到精确式触摸板（UsagePage 0x0D / Usage 0x05）。");
        return 1;
    }
    foreach (var d in devices)
    {
        using (d) PrintDescriptor(d);
        Console.WriteLine();
    }
    return 0;
}

static void PrintDescriptor(PtpDevice d)
{
    Console.WriteLine($"== PTP  VID={d.VendorId:X4}  PID={d.ProductId:X4}");
    Console.WriteLine($"   {d.Path}");
    Console.WriteLine($"   InputReportLen={d.Caps.InputReportByteLength}  LinkCollections={d.Caps.NumberLinkCollectionNodes}  " +
                      $"ValueCaps={d.Caps.NumberInputValueCaps}  ButtonCaps={d.Caps.NumberInputButtonCaps}");
    Console.WriteLine($"   触点槽数 = {d.Slots.Length}（每份报文最多携带的触点数；小于设备最大触点数即为混合模式，一帧会拆成多份报文）");

    foreach (var slot in d.Slots)
    {
        var x = d.Values.First(v => v.LinkCollection == slot && v.UsagePage == 0x01 && v.UsageMin == 0x30);
        var y = d.Values.FirstOrDefault(v => v.LinkCollection == slot && v.UsagePage == 0x01 && v.UsageMin == 0x31);
        Console.WriteLine($"   槽 L{slot}: TipSwitch={(d.SlotHasButton(slot, 0x42) ? "有" : "无")}  Confidence={(d.SlotHasButton(slot, 0x47) ? "有" : "无")}  " +
                          $"X[{x.LogicalMin},{x.LogicalMax}] {Mm(d.X)}  Y[{y.LogicalMin},{y.LogicalMax}] {Mm(d.Y)}");
    }

    var scan = d.Values.FirstOrDefault(v => v.LinkCollection == 0 && v.UsagePage == 0x0D && v.UsageMin == 0x56);
    Console.WriteLine(scan.BitSize > 0
        ? $"   ScanTime: {scan.BitSize} 位，单位指数 {PtpDevice.DecodeUnitExponent(scan.UnitsExp)}（PTP 规范为 100µs，按 {1 << scan.BitSize} 回绕）"
        : "   ScanTime: 未声明");

    bool hasContactCount = d.Values.Any(v => v.LinkCollection == 0 && v.UsagePage == 0x0D && v.UsageMin == 0x54);
    bool hasMaxFeature = d.Features.Any(v => v.UsagePage == 0x0D && v.UsageMin == 0x55);
    Console.WriteLine($"   ContactCount(0D/54)={(hasContactCount ? "有" : "无")}  ContactCountMaximum 特性报告(0D/55)={(hasMaxFeature ? "有" : "无")}");

    Console.WriteLine("   -- 全部输入 value caps（page/usage @集合 : 逻辑范围, 位宽 x 个数）");
    foreach (var v in d.Values)
        Console.WriteLine($"      {v.UsagePage:X2}/{v.UsageMin:X2} @L{v.LinkCollection} : [{v.LogicalMin},{v.LogicalMax}]  {v.BitSize}b x{v.ReportCount}  rid={v.ReportID}");
    Console.WriteLine("   -- 全部输入 button caps（page/usage 范围 @集合）");
    foreach (var b in d.Buttons)
        Console.WriteLine($"      {b.UsagePage:X2}/{b.UsageMin:X2}-{(b.IsRange ? b.UsageMax : b.UsageMin):X2} @L{b.LinkCollection}  rid={b.ReportID}");
}

static string Mm(TraceAxis axis) => axis.Mm is { } mm ? $"≈{mm:F1}mm" : "(物理尺寸未知)";
