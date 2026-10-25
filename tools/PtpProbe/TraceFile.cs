using System.Text;
using System.Text.Json;

namespace GlideShell.Tools.PtpProbe;

// 轨迹文件（JSON Lines，UTF-8）：
//   第 1 行 {"type":"header","header":{...}}       设备几何 + 场景说明
//   之后每行一份报文 {"t":到达µs,"s":ScanTime原值,"cc":ContactCount,"b":按钮位,"c":[[id,tip,conf,x,y],...]}
//   最后一行 {"type":"end","reports":N}
// 报文按原样记录（逻辑坐标、所有触点槽、包括 cc 之外的槽），换算和解释留给分析与回放，保证无损。

internal readonly record struct SlotSample(int Id, bool Tip, bool Confidence, int X, int Y);

internal sealed record TraceReport(long ArrivalUs, int ScanTime, int ContactCount, int Buttons, SlotSample[] Slots);

internal sealed record TraceDevice(string Vid, string Pid, string Path, int Slots, AxisInfo X, AxisInfo Y, int ScanBits);

internal sealed record TraceHeader(
    string Format, int Version, DateTimeOffset RecordedAt,
    string Scenario, string Title, string Instructions, TraceDevice Device);

internal sealed class TraceWriter : IDisposable
{
    public const string FormatName = "glideshell-ptp-trace";
    public const int FormatVersion = 1;

    private readonly StreamWriter _writer;
    private readonly StringBuilder _line = new(256);
    public int Count { get; private set; }

    public TraceWriter(string path, TraceHeader header)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { NewLine = "\n" };
        _writer.WriteLine(JsonSerializer.Serialize(new { type = "header", header }, TraceJson.Options));
    }

    public void Write(TraceReport r)
    {
        _line.Clear();
        _line.Append("{\"t\":").Append(r.ArrivalUs)
             .Append(",\"s\":").Append(r.ScanTime)
             .Append(",\"cc\":").Append(r.ContactCount)
             .Append(",\"b\":").Append(r.Buttons)
             .Append(",\"c\":[");
        for (int i = 0; i < r.Slots.Length; i++)
        {
            var c = r.Slots[i];
            if (i > 0) _line.Append(',');
            _line.Append('[').Append(c.Id).Append(',').Append(c.Tip ? 1 : 0).Append(',').Append(c.Confidence ? 1 : 0)
                 .Append(',').Append(c.X).Append(',').Append(c.Y).Append(']');
        }
        _line.Append("]}");
        _writer.WriteLine(_line);
        Count++;
    }

    public void Dispose()
    {
        _writer.WriteLine(JsonSerializer.Serialize(new { type = "end", reports = Count }, TraceJson.Options));
        _writer.Dispose();
    }
}

internal static class TraceReader
{
    public static (TraceHeader Header, List<TraceReport> Reports) Read(string path)
    {
        TraceHeader? header = null;
        var reports = new List<TraceReport>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type))
            {
                if (type.GetString() == "header")
                    header = root.GetProperty("header").Deserialize<TraceHeader>(TraceJson.Options);
                continue;
            }

            var slotsJson = root.GetProperty("c");
            var slots = new SlotSample[slotsJson.GetArrayLength()];
            int i = 0;
            foreach (var s in slotsJson.EnumerateArray())
            {
                slots[i++] = new SlotSample(s[0].GetInt32(), s[1].GetInt32() != 0, s[2].GetInt32() != 0, s[3].GetInt32(), s[4].GetInt32());
            }
            reports.Add(new TraceReport(root.GetProperty("t").GetInt64(), root.GetProperty("s").GetInt32(),
                root.GetProperty("cc").GetInt32(), root.GetProperty("b").GetInt32(), slots));
        }
        if (header is null) throw new InvalidDataException($"{path}: 缺少 header 行，不是 {TraceWriter.FormatName} 文件");
        if (header.Format != TraceWriter.FormatName || header.Version != TraceWriter.FormatVersion)
            throw new InvalidDataException($"{path}: 不支持的格式 {header.Format} v{header.Version}");
        return (header, reports);
    }
}

internal static class TraceJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文说明保持可读
    };
}
