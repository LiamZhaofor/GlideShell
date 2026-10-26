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
//   calibrate   S1 校准：从轨迹量出轻点 / 落指特征，给出识别参数建议，并按场景回放对比当前参数与建议参数
//   live        用 GlideShell.Platform 的输入线程实时识别，打印手势（真机验证 P2 管线）
//   windows     列出显示器与总览会收录的窗口（及被排除的原因）
//   native      只读对照系统手势设置的注册表值与 Explorer 生效值，以及是否有未还原的接管记录
//   desktops    只读：虚拟桌面接口自检结果、桌面列表与当前桌面
//   thumbnails  缩略图后端自检：屏幕左下角显示约 1 秒的测试小窗，确认共享缩略图视觉（--classic 为经典缩略图）真的画得出来
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
    "calibrate" when args.Length > 1 => Calibrate(args[1], args.Contains("--apply"), args.Contains("--fixtures")),
    "live" => Live(args),
    "windows" => Windows(),
    "native" => Native(),
    "desktops" => Desktops(),
    "thumbnails" => Thumbnails(!args.Contains("--classic"), args.Contains("--fade")),
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
          PtpProbe live [同上参数]          实时识别：用 GlideShell 的输入线程 + 识别器打印手势，Enter 退出
          PtpProbe calibrate <目录> [--apply] [--fixtures]
                                           S1 校准：给出识别参数建议并按场景回放验证；--apply 写进 GlideShell 配置，
                                           --fixtures 把轨迹复制到 tests/GlideShell.Tests/Traces 作为回放测试夹具
          PtpProbe windows                 列出显示器与总览会收录的窗口（及被排除的原因）
          PtpProbe native                  只读：系统手势设置的注册表值 / Explorer 生效值
          PtpProbe desktops                只读：虚拟桌面接口自检与桌面列表
          PtpProbe thumbnails [--classic]  缩略图后端自检（左下角显示约 1 秒测试小窗）
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

static GestureSettings ParseSettings(string[] args)
{
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
    return settings;
}

// 打印识别事件：Changed 只计数，其余逐条输出
static Action<GestureEvent> GesturePrinter(long originUs)
{
    int changed = 0;
    return e =>
    {
        if (e.Phase == GesturePhase.Changed) { changed++; return; }
        bool swipe = e.Kind == GestureKind.Swipe;
        string what = swipe ? $"{e.Phase} {e.Fingers}指" : $"Tap {e.Fingers}指";
        string motion = swipe && e.Phase is GesturePhase.Began or GesturePhase.Ended ? $" {e.Direction}" : "";
        if (swipe && e.Phase == GesturePhase.Ended)
        {
            double v = Math.Abs(e.AlongVelocityMmS) < 0.5 ? 0 : e.AlongVelocityMmS;
            motion += $" 位移 {e.AlongMm:F1}mm 速度 {v:F0}mm/s（Changed {changed} 次）";
        }
        if (e.Phase == GesturePhase.Began) changed = 0;
        Console.WriteLine($"   @{(e.HostTimeUs - originUs) / 1e6:F2}s {what}{motion}");
    };
}

static int Replay(string path, string[] args)
{
    var files = Directory.Exists(path)
        ? Directory.GetFiles(path, "*.jsonl").Order(StringComparer.Ordinal).ToList()
        : [path];
    var settings = ParseSettings(args);
    foreach (var file in files)
    {
        var trace = PtpTrace.Load(file);
        Console.WriteLine($"== {Path.GetFileName(file)} — {trace.Header.Title}");
        var recognizer = new GestureRecognizer(settings, GesturePrinter(0));
        trace.Replay(new FrameAssembler(trace.Header.Device.ToGeometry(), recognizer.Process));
    }
    return 0;
}

static int Calibrate(string dir, bool apply, bool fixtures)
{
    var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jsonl").Order(StringComparer.Ordinal).ToList() : [];
    if (files.Count == 0)
    {
        Console.Error.WriteLine($"{dir} 里没有轨迹文件（先运行 PtpProbe record）");
        return 1;
    }
    var traces = files.Select(f => (File: f, Trace: PtpTrace.Load(f))).ToList();
    var configPath = GlideShell.Core.Configuration.ConfigStore.DefaultPath;
    var (config, _) = GlideShell.Core.Configuration.ConfigStore.Load(configPath);
    var current = config.Recognition;

    var stats = GlideShell.Core.Tracing.Calibration.Measure(traces.Select(t => t.Trace));
    var (suggested, notes) = GlideShell.Core.Tracing.Calibration.Suggest(stats, current);
    Console.WriteLine($"== 量得：轻点 {stats.TapDurationsMs.Count} 次，落指 {stats.LandingSpreadsMs.Count} 次");
    if (notes.Count == 0) Console.WriteLine("   数据不足（需要 three-tap 与三指滑动场景），保持当前参数。");
    foreach (var n in notes)
        Console.WriteLine($"   {n.Parameter,-18} {n.Current,6:0.##} → {n.Suggested,6:0.##}   依据：{n.Reason}");

    Console.WriteLine();
    Console.WriteLine($"== 按场景回放（当前 / 建议）");
    int passCurrent = 0, passSuggested = 0;
    foreach (var (file, trace) in traces)
    {
        string scenario = trace.Header.Scenario;
        string? a = GlideShell.Core.Tracing.ScenarioExpectations.Check(scenario, GlideShell.Core.Tracing.ScenarioExpectations.Recognize(trace, current));
        string? b = GlideShell.Core.Tracing.ScenarioExpectations.Check(scenario, GlideShell.Core.Tracing.ScenarioExpectations.Recognize(trace, suggested));
        if (a is null) passCurrent++;
        if (b is null) passSuggested++;
        Console.WriteLine($"   {(a is null ? "✓" : "✗")} {(b is null ? "✓" : "✗")}  {scenario,-18} {Path.GetFileName(file)}");
        if (a is not null) Console.WriteLine($"         当前：{a}");
        if (b is not null) Console.WriteLine($"         建议：{b}");
    }
    Console.WriteLine($"   通过：当前 {passCurrent}/{traces.Count}，建议 {passSuggested}/{traces.Count}");

    bool better = passSuggested >= passCurrent && notes.Count > 0;
    Console.WriteLine();
    Console.WriteLine(better ? "== 建议写进配置的 recognition（其余字段保持不变）：" : "== 建议参数没有更好，保留当前参数。");
    if (better)
        Console.WriteLine($"   \"slopMm\": {suggested.SlopMm}, \"fingerSettleMs\": {suggested.FingerSettleMs}, " +
                          $"\"tapMaxDurationMs\": {suggested.TapMaxDurationMs}, \"tapMaxMoveMm\": {suggested.TapMaxMoveMm}");

    if (apply && better)
    {
        var (updated, problems) = (config with { Recognition = suggested }).Sanitize();
        foreach (var p in problems) Console.WriteLine($"   配置：{p}");
        GlideShell.Core.Configuration.ConfigStore.Save(updated, configPath);
        Console.WriteLine($"   已写入 {configPath}（运行中的 GlideShell 会自动重载）");
    }
    if (fixtures)
    {
        string? root = FindRepoRoot();
        if (root is null) Console.Error.WriteLine("找不到仓库根目录（GlideShell.slnx），没有复制夹具");
        else
        {
            string target = Path.Combine(root, "tests", "GlideShell.Tests", "Traces");
            Directory.CreateDirectory(target);
            foreach (var (file, _) in traces) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            Console.WriteLine($"   已复制 {traces.Count} 个轨迹到 {target}（dotnet test 会按场景回放它们）");
        }
    }
    return passSuggested == traces.Count || passCurrent == traces.Count ? 0 : 3;

    static string? FindRepoRoot()
    {
        for (var d = new DirectoryInfo(Environment.CurrentDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GlideShell.slnx"))) return d.FullName;
        return null;
    }
}

static int Live(string[] args)
{
    var settings = ParseSettings(args);
    long origin = GlideShell.Platform.HostClock.NowUs();
    var recognizer = new GestureRecognizer(settings, GesturePrinter(origin));
    using var input = new GlideShell.Platform.Input.PtpInputThread(
        recognizer.Process, recognizer.Tick, recognizer.Cancel, log: m => Console.WriteLine($"   [输入] {m}"));
    input.Start();
    foreach (var d in input.Devices)
        Console.WriteLine($"   触摸板 VID={d.VendorId:X4} PID={d.ProductId:X4}：{d.Geometry.X.SizeMm:F1}×{d.Geometry.Y.SizeMm:F1}mm，{d.Geometry.ContactSlots} 个触点槽");
    Console.WriteLine("实时识别中（三指及以上手势会打印出来），按 Enter 退出。");
    if (Console.IsInputRedirected) Thread.Sleep(1500); // 非交互运行（自检）时只跑一下就退出
    else Console.ReadLine();
    return 0;
}

static int Windows()
{
    var monitors = GlideShell.Platform.Shell.Monitors.Enumerate();
    foreach (var m in monitors)
        Console.WriteLine($"显示器 #{m.Index}{(m.IsPrimary ? "（主）" : "")}：{m.Bounds}，工作区 {m.WorkArea}，缩放 {m.DpiScale:P0}");
    var rejected = new List<(string Window, string Reason)>();
    var sw = Stopwatch.StartNew();
    var windows = GlideShell.Platform.Shell.WindowCatalog.Snapshot(monitors, rejections: rejected);
    Console.WriteLine($"收录 {windows.Count} 个窗口（枚举耗时 {sw.Elapsed.TotalMilliseconds:F1}ms，虚拟桌面查询{(GlideShell.Platform.Shell.WindowCatalog.VirtualDesktopQueryAvailable ? "可用" : "不可用")}）：");
    foreach (var w in windows)
        Console.WriteLine($"  ✓ {(w.IsForeground ? "[前台] " : "")}{(w.IsMinimized ? "[最小化] " : "")}{Short(w.Title)} — {w.ProcessName} @#{w.MonitorIndex} {w.Source}");
    Console.WriteLine($"排除 {rejected.Count} 个（只列有标题的）：");
    foreach (var (window, reason) in rejected.Where(r => !r.Window.StartsWith(" [", StringComparison.Ordinal)).Take(40))
        Console.WriteLine($"  ✗ {Short(window)} — {reason}");
    return 0;

    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
}

static int Native()
{
    var manager = new GlideShell.Platform.NativeGestures.NativeGestureManager();
    Console.WriteLine($"接管记录：{(manager.HasPendingTakeover ? $"存在未还原的接管（{manager.RecordPath}）" : "无")}");
    Console.WriteLine($"{"设置",-26}{"注册表",10}{"生效值",10}");
    foreach (var (name, reg, eff) in manager.Describe())
        Console.WriteLine($"{name,-26}{(reg?.ToString() ?? "（无）"),10}{(eff?.ToString() ?? "?"),10}{(reg is { } r && eff is { } e && r != e ? "   ← 不一致" : "")}");
    return 0;
}

static int Desktops()
{
    var sw = Stopwatch.StartNew();
    using var service = GlideShell.Platform.VirtualDesktops.VirtualDesktopService.Start(m => Console.WriteLine($"   [日志] {m}"));
    Console.WriteLine($"自检：{(service.Available ? "通过" : "未通过")}（{service.Status}，{sw.Elapsed.TotalMilliseconds:F0}ms）");
    var state = service.State;
    for (int i = 0; i < state.Count; i++)
        Console.WriteLine($"  {(i == state.CurrentIndex ? "▶" : " ")} 桌面 {i + 1}  {state.Desktops[i]}");
    return service.Available ? 0 : 1;
}

static int Thumbnails(bool shared, bool fade)
{
    bool ok = GlideShell.Platform.Overview.ThumbnailSelfTest.Run(shared, m => Console.WriteLine($"   {m}"), fade);
    Console.WriteLine($"{(shared ? "共享缩略图视觉" : "经典缩略图")}：{(ok ? "通过" : "未通过")}");
    return ok ? 0 : 1;
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
