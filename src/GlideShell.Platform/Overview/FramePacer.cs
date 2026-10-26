using System.Runtime.InteropServices;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 帧内取样时机（S4 结论）：合成器时钟 tick 就是 DWM 一帧的起点，tick 一到就提交只能赶上下一帧，
/// 取样到上屏固定 2 帧。改为睡到「下一帧起点 − lead」再取样，延迟降到约 1 帧：
/// 60Hz 33ms → 21ms，165Hz 12ms → 8–10ms。lead 要盖住本帧工作量；经典缩略图每秒一次的阻塞盖不住时，
/// 接受那一帧晚一个周期。
/// 睡眠用高精度可等待定时器（不占 CPU），最后不到 0.3ms 自旋补齐。
/// </summary>
internal sealed unsafe partial class FramePacer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;
    private const double SpinMs = 0.3;

    private readonly nint _timer;
    private readonly double _leadMs;
    private double _periodMs;
    private long _periodCheckedUs;

    public FramePacer(double leadMs = 3.0)
    {
        _leadMs = leadMs;
        _timer = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
    }

    /// <summary>当前刷新周期（ms），取不到时为 0（此时不做晚取样）。</summary>
    public double PeriodMs => _periodMs;

    /// <summary>在 tick 醒来后调用：等到本周期内的取样点再返回。</summary>
    public void WaitForSamplePoint(long tickUs)
    {
        if (tickUs - _periodCheckedUs > 1_000_000) RefreshPeriod(tickUs); // 刷新率可能变（插拔电源、换屏）
        if (_periodMs <= 0) return;
        double lead = Math.Min(_leadMs, _periodMs * 0.5);
        long targetUs = tickUs + (long)((_periodMs - lead) * 1000);
        long remainingUs = targetUs - HostClock.NowUs();
        if (remainingUs <= 0) return;

        long sleepUs = remainingUs - (long)(SpinMs * 1000);
        if (sleepUs > 0 && _timer != 0)
        {
            long due = -sleepUs * 10; // 负数 = 相对时间，单位 100ns
            if (SetWaitableTimer(_timer, &due, 0, 0, 0, 0)) WaitForSingleObject(_timer, 100);
        }
        while (HostClock.NowUs() < targetUs) Thread.SpinWait(20);
    }

    public void Dispose()
    {
        if (_timer != 0) CloseHandle(_timer);
    }

    private void RefreshPeriod(long nowUs)
    {
        _periodCheckedUs = nowUs;
        // DWM_TIMING_INFO 按 1 字节对齐打包，共 292 字节；qpcRefreshPeriod 在偏移 12
        byte* info = stackalloc byte[320];
        new Span<byte>(info, 320).Clear();
        *(uint*)info = 292;
        if (DwmGetCompositionTimingInfo(0, info) != 0) return;
        ulong periodQpc = *(ulong*)(info + 12);
        double ms = HostClock.ToUs((long)periodQpc) / 1000.0;
        if (ms is > 2 and < 50) _periodMs = ms;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetCompositionTimingInfo(nint hwnd, byte* timingInfo);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW")]
    private static partial nint CreateWaitableTimerExW(nint attributes, char* name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(nint timer, long* dueTime, int period, nint completion, nint arg, int resume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
