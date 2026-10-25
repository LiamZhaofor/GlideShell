using System.Diagnostics;

namespace GlideShell.Platform;

/// <summary>主机单调时钟（QPC），单位 µs。输入到达时刻、渲染帧时刻共用这一条时间轴。</summary>
public static class HostClock
{
    private static readonly double UsPerTick = 1_000_000.0 / Stopwatch.Frequency;

    public static long NowUs() => (long)(Stopwatch.GetTimestamp() * UsPerTick);

    public static long ToUs(long qpcTicks) => (long)(qpcTicks * UsPerTick);
}
