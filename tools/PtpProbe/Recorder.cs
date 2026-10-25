using System.Diagnostics;
using System.Text;

namespace GlideShell.Tools.PtpProbe;

/// <summary>按场景引导录制：每个场景一个轨迹文件，定时自动结束（横滑等手势可能把焦点抢走，不能只靠按键停止）。</summary>
internal static class Recorder
{
    public static int Run(string? outDir, IReadOnlyList<Scenario> scenarios)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("record 需要在交互式终端里运行（要读按键）。");
            return 2;
        }

        var devices = PtpDevice.EnumerateAll();
        if (devices.Count == 0)
        {
            Console.Error.WriteLine("未找到精确式触摸板。");
            return 1;
        }
        foreach (var other in devices.Skip(1)) other.Dispose();
        using var device = devices[0];

        string dir = outDir ?? DefaultOutputDir();
        Console.WriteLine($"触摸板 VID={device.VendorId:X4} PID={device.ProductId:X4}，{device.Slots.Length} 个触点槽");
        Console.WriteLine($"轨迹输出到：{dir}");
        Console.WriteLine("提示：录制前请退出旧的 MissionFlow 托盘程序；系统手势（切换应用、任务视图）被触发属正常现象，");
        Console.WriteLine("      每个场景到时会自动结束，焦点被抢走时点回本窗口再继续即可。");

        using var listener = new RawInputListener(device);
        if (!listener.Registered)
        {
            Console.Error.WriteLine($"注册原始输入失败：{listener.Error}");
            return 1;
        }

        var written = new List<string>();
        for (int i = 0; i < scenarios.Count; i++)
        {
            var sc = scenarios[i];
            Console.WriteLine();
            Console.WriteLine($"[{i + 1}/{scenarios.Count}] {sc.Title}（{sc.Seconds} 秒）");
            Console.WriteLine($"  做法：{sc.Instructions}");
            Console.WriteLine($"  用途：{sc.Purpose}");
            Console.Write("  按 Enter 开始 · s 跳过 · q 结束 > ");
            char choice = WaitChoice();
            Console.WriteLine();
            if (choice == 'q') break;
            if (choice == 's') continue;

            string path = Path.Combine(dir, $"{i + 1:00}-{sc.Slug}.jsonl");
            RecordOne(listener, device, sc, path);
            written.Add(path);
        }

        Console.WriteLine();
        if (written.Count == 0)
        {
            Console.WriteLine("没有录制任何场景。");
            return 0;
        }
        Console.WriteLine($"完成，共 {written.Count} 个轨迹文件，目录：{dir}");
        Console.WriteLine($"汇总分析：dotnet run --project tools\\PtpProbe -- analyze \"{dir}\"");
        return 0;
    }

    private static void RecordOne(RawInputListener listener, PtpDevice device, Scenario sc, string path)
    {
        var header = new TraceHeader(TraceWriter.FormatName, TraceWriter.FormatVersion, DateTimeOffset.Now,
            sc.Slug, sc.Title, sc.Instructions,
            new TraceDevice($"{device.VendorId:X4}", $"{device.ProductId:X4}", device.Path, device.Slots.Length,
                device.X, device.Y, device.ScanBits));

        var reports = new List<TraceReport>();
        long freq = Stopwatch.Frequency;
        listener.Clear();
        long start = Stopwatch.GetTimestamp();
        long deadline = start + sc.Seconds * freq;
        long nextStatus = 0;
        int lastWidth = 0;
        TraceReport? latest = null;

        using (var writer = new TraceWriter(path, header))
        {
            while (true)
            {
                while (listener.TryDequeue(out long qpc, out var r))
                {
                    if (qpc < start) continue; // 开始前就排在队列里的报文不算
                    var stamped = r with { ArrivalUs = (qpc - start) * 1_000_000 / freq };
                    writer.Write(stamped);
                    reports.Add(stamped);
                    latest = stamped;
                }

                long now = Stopwatch.GetTimestamp();
                if (now >= deadline) break;
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter) break;
                if (now >= nextStatus)
                {
                    nextStatus = now + freq / 10;
                    lastWidth = WriteStatus(Status(reports, latest, (deadline - now) / (double)freq, (now - start) * 1_000_000 / freq), lastWidth);
                }
                Thread.Sleep(10);
            }
        }

        WriteStatus("", lastWidth);
        Console.Write('\r');
        var stats = TraceAnalyzer.Analyze(path, header, reports);
        Console.WriteLine($"  已保存 {Path.GetFileName(path)}：{TraceAnalyzer.QuickSummary(stats)}");
        if (listener.ParseFailures > 0) Console.WriteLine($"  （解析失败累计 {listener.ParseFailures} 份）");
    }

    private static string Status(List<TraceReport> reports, TraceReport? latest, double remainingS, long nowUs)
    {
        int recent = 0;
        for (int i = reports.Count - 1; i >= 0 && nowUs - reports[i].ArrivalUs <= 1_000_000; i--) recent++;
        var sb = new StringBuilder();
        sb.Append($"  ● REC {remainingS,4:F1}s | {reports.Count} 份 | {recent} Hz");
        if (latest is not null)
        {
            sb.Append($" | cc={latest.ContactCount} |");
            int valid = Math.Min(latest.ContactCount, latest.Slots.Length);
            for (int s = 0; s < valid; s++)
            {
                var c = latest.Slots[s];
                sb.Append($" #{c.Id}{(c.Tip ? "v" : "^")}{(c.Confidence ? "" : "?")}");
            }
        }
        return sb.ToString();
    }

    // 整行覆盖：按显示宽度（中日韩字符占 2 列）补空格，盖掉上一次更长的内容
    private static int WriteStatus(string text, int lastWidth)
    {
        int width = text.Sum(ch => ch >= 0x2E80 ? 2 : 1);
        Console.Write('\r');
        Console.Write(text);
        if (lastWidth > width) Console.Write(new string(' ', lastWidth - width));
        return width;
    }

    private static char WaitChoice()
    {
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return '\n';
            char c = char.ToLowerInvariant(key.KeyChar);
            if (c is 's' or 'q') return c;
        }
    }

    // 仓库内运行时放到 artifacts/traces/（已被 .gitignore 忽略），挑选后再拷进测试夹具
    private static string DefaultOutputDir()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "GlideShell.slnx")))
                    return Path.Combine(d.FullName, "artifacts", "traces", stamp);
            }
        }
        return Path.Combine(Environment.CurrentDirectory, "traces", stamp);
    }
}
