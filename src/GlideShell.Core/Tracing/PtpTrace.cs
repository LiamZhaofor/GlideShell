using System.Text;
using System.Text.Json;
using GlideShell.Core.Input;
using GlideShell.Core.Serialization;

namespace GlideShell.Core.Tracing;

// 输入轨迹文件（JSON Lines，UTF-8），由 tools/PtpProbe 录制，供分析与回放测试使用：
//   第 1 行 {"type":"header","header":{...}}       设备几何 + 场景说明
//   之后每行一份报文 {"t":到达µs,"s":ScanTime原值,"cc":ContactCount,"b":按钮位,"c":[[id,tip,conf,x,y],...]}
//   最后一行 {"type":"end","reports":N}
// 报文按原样记录（逻辑坐标、全部触点槽、包括 cc 之外的槽），保证无损；换算交给 FrameAssembler。

/// <summary>一份原始报文。到达时间从录制开始计（µs）。</summary>
public sealed record RawReport(long ArrivalUs, int ScanTime, int ContactCount, int Buttons, RawContact[] Slots);

public sealed record TraceAxis(int Min, int Max, double? Mm);

public sealed record TraceDevice(string Vid, string Pid, string Path, int Slots, TraceAxis X, TraceAxis Y, int ScanBits)
{
    public PtpGeometry ToGeometry() => new(
        PtpAxis.Create(X.Min, X.Max, X.Mm),
        PtpAxis.Create(Y.Min, Y.Max, Y.Mm),
        Slots,
        ScanBits > 0 ? ScanBits : 16);
}

public sealed record TraceHeader(
    string Format, int Version, DateTimeOffset RecordedAt,
    string Scenario, string Title, string Instructions, TraceDevice Device);

/// <summary>轨迹文件的首行与末行。</summary>
public sealed record TraceHeaderLine(string Type, TraceHeader Header);

public sealed record TraceEndLine(string Type, int Reports);

public sealed record PtpTrace(TraceHeader Header, IReadOnlyList<RawReport> Reports)
{
    public const string FormatName = "glideshell-ptp-trace";
    public const int FormatVersion = 1;

    public static PtpTrace Load(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        return Read(reader, path);
    }

    public static PtpTrace Read(TextReader reader, string sourceName = "<stream>")
    {
        TraceHeader? header = null;
        var reports = new List<RawReport>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type))
            {
                if (type.GetString() == "header")
                    header = root.GetProperty("header").Deserialize(CoreJson.TraceHeader);
                continue;
            }

            var slotsJson = root.GetProperty("c");
            var slots = new RawContact[slotsJson.GetArrayLength()];
            int i = 0;
            foreach (var s in slotsJson.EnumerateArray())
                slots[i++] = new RawContact(s[0].GetInt32(), s[1].GetInt32() != 0, s[2].GetInt32() != 0, s[3].GetInt32(), s[4].GetInt32());
            reports.Add(new RawReport(root.GetProperty("t").GetInt64(), root.GetProperty("s").GetInt32(),
                root.GetProperty("cc").GetInt32(), root.GetProperty("b").GetInt32(), slots));
        }
        if (header is null) throw new InvalidDataException($"{sourceName}: 缺少 header 行，不是 {FormatName} 文件");
        if (header.Format != FormatName || header.Version != FormatVersion)
            throw new InvalidDataException($"{sourceName}: 不支持的格式 {header.Format} v{header.Version}");
        return new PtpTrace(header, reports);
    }

    /// <summary>按录制时的到达时刻把报文逐份推给组帧器，模拟实时输入。</summary>
    public void Replay(FrameAssembler assembler, long hostStartUs = 0)
    {
        foreach (var r in Reports)
            assembler.Push(hostStartUs + r.ArrivalUs, r.ScanTime, r.ContactCount, r.Buttons, r.Slots);
    }
}

/// <summary>流式写轨迹：录制时每份报文一行，Dispose 时补上结束行。</summary>
public sealed class PtpTraceWriter : IDisposable
{
    private readonly TextWriter _writer;
    private readonly StringBuilder _line = new(256);
    public int Count { get; private set; }

    public PtpTraceWriter(string path, TraceHeader header)
        : this(CreateFile(path), header)
    {
    }

    public PtpTraceWriter(TextWriter writer, TraceHeader header)
    {
        _writer = writer;
        _writer.Write(JsonSerializer.Serialize(new TraceHeaderLine("header", header), CoreJson.TraceHeaderLine));
        _writer.Write('\n');
    }

    public void Write(RawReport r)
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
        _line.Append("]}\n");
        _writer.Write(_line);
        Count++;
    }

    public void Dispose()
    {
        _writer.Write(JsonSerializer.Serialize(new TraceEndLine("end", Count), CoreJson.TraceEndLine));
        _writer.Write('\n');
        _writer.Dispose();
    }

    private static StreamWriter CreateFile(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        return new StreamWriter(path, append: false, new UTF8Encoding(false));
    }
}
