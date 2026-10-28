using System.Runtime.InteropServices;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 渲染线程：按合成器时钟（显示器刷新）逐帧回调，回调里取最新的手势 / 动画状态并更新缩略图。
/// Windows 11 用 DCompositionWaitForCompositorClock；不可用时退回 DwmFlush。
/// 合成器时钟下用 <see cref="FramePacer"/> 晚取样（下一帧起点前留出余量再取样），取样到上屏约 1 帧。
/// 回调返回 false（覆盖层已隐藏）后线程休眠，直到 <see cref="Wake"/>。
/// 渲染期间线程登记到多媒体类调度服务（MMCSS「Games」）：回调本身很短（不到 1ms），掉的帧几乎都是线程没能按时被调度
/// （合成器时钟或晚取样的定时器醒来时已经晚了），普通的「高于正常」优先级挡不住后台程序抢占。只在渲染时登记，停下就退出。
/// </summary>
public sealed unsafe partial class RenderLoop : IDisposable
{
    private readonly Func<long, bool> _renderFrame;
    private readonly Action<FrameStats>? _onSessionEnd;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _running = true;
    private bool _useCompositorClock = true;
    private const long WokeLateUs = 500; // 比取样点晚这么多才算「醒晚了」
    private readonly FramePacer _pacer = new();

    /// <param name="renderFrame">参数为本帧的主机时间（µs）；返回是否继续逐帧渲染。</param>
    /// <param name="onSessionEnd">一段连续渲染结束时（在渲染线程上）回调帧间隔统计。</param>
    public RenderLoop(Func<long, bool> renderFrame, Action<FrameStats>? onSessionEnd = null)
    {
        _renderFrame = renderFrame;
        _onSessionEnd = onSessionEnd;
        _thread = new Thread(Run) { IsBackground = true, Name = "GlideShell.Render", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>帧间隔统计（最近一次会话），诊断用。</summary>
    public FrameStats LastSession { get; private set; } = FrameStats.Empty;

    public void Wake() => _wake.Set();

    /// <summary>晚取样用的刷新周期（ms）；0 表示没有晚取样（DwmFlush 退路或取不到周期）。</summary>
    public double RefreshPeriodMs => _pacer.PeriodMs;

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
        _wake.Dispose();
        _pacer.Dispose();
    }

    private void Run()
    {
        var intervals = new List<double>(1024);
        var work = new List<double>(1024);
        while (_running)
        {
            _wake.WaitOne();
            nint mmcss = EnterMmcss();
            intervals.Clear();
            work.Clear();
            long last = 0;
            int wokeLate = 0, overran = 0;
            while (_running)
            {
                bool onClock = WaitForNextFrame();
                long tick = HostClock.NowUs();
                if (last != 0) intervals.Add((tick - last) / 1000.0);
                last = tick;
                if (onClock && _pacer.WaitForSamplePoint(tick) > WokeLateUs) wokeLate++;
                long now = HostClock.NowUs();
                bool keepGoing;
                try
                {
                    keepGoing = _renderFrame(now);
                }
                catch (Exception ex)
                {
                    Log.Error("渲染帧异常", ex);
                    keepGoing = false;
                }
                long end = HostClock.NowUs();
                work.Add((end - now) / 1000.0);
                if (onClock && _pacer.PeriodMs > 0 && end - tick > _pacer.PeriodMs * 1000) overran++; // 下一个 tick 之前没做完：下一帧一定赶不上
                if (!keepGoing) break;
            }
            if (mmcss != 0) AvRevertMmThreadCharacteristics(mmcss);
            LastSession = FrameStats.From(intervals, work, _pacer.PeriodMs, wokeLate, overran);
            _onSessionEnd?.Invoke(LastSession);
        }
    }

    /// <summary>当前线程登记为 MMCSS「Games」任务并设为高优先级；不可用（服务关着等）时返回 0，照常用线程自己的优先级。</summary>
    private static nint EnterMmcss()
    {
        uint task = 0;
        nint handle = AvSetMmThreadCharacteristicsW("Games", &task);
        if (handle != 0) AvSetMmThreadPriority(handle, 1 /* AVRT_PRIORITY_HIGH */);
        return handle;
    }

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AvSetMmThreadCharacteristicsW(string taskName, uint* taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvSetMmThreadPriority(nint handle, int priority);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(nint handle);

    /// <returns>是否由合成器时钟唤醒（此时才能按帧起点晚取样）。</returns>
    private bool WaitForNextFrame()
    {
        if (_useCompositorClock)
        {
            try
            {
                uint r = Dcomp.DCompositionWaitForCompositorClock(0, null, 100);
                if (r != Dcomp.WAIT_TIMEOUT) return true;
            }
            catch (EntryPointNotFoundException)
            {
                _useCompositorClock = false; // Windows 10：没有合成器时钟 API
            }
        }
        if (Dwmapi.DwmFlush() != 0) Thread.Sleep(4);
        return false;
    }
}

/// <param name="MaxAt">最长的那个间隔在第几帧之后（0 起）。</param>
/// <param name="MaxWorkMs">渲染回调本身最长花了多久（间隔超长而回调不长，说明是合成器那边慢）。</param>
/// <param name="Late">间隔超过 1.5 个刷新周期的帧数（少取样了一帧，动画中就是画面重复一帧）。</param>
/// <param name="WokeLate">晚取样的定时器醒来时已比取样点晚 0.5ms 以上的帧数（被抢占或定时器不准）。</param>
/// <param name="Overran">下一个 tick 到来时这一帧还没做完的帧数。</param>
public readonly record struct FrameStats(int Frames, double P50Ms, double P95Ms, double MaxMs, int MaxAt = -1, double MaxWorkMs = 0, int MaxWorkAt = -1,
    int Late = 0, int WokeLate = 0, int Overran = 0)
{
    public static FrameStats Empty => new(0, 0, 0, 0);

    /// <param name="periodMs">刷新周期（0 = 不知道，用中位间隔代替）。</param>
    public static FrameStats From(List<double> intervals, List<double>? work = null, double periodMs = 0, int wokeLate = 0, int overran = 0)
    {
        if (intervals.Count == 0) return Empty;
        var sorted = intervals.Order().ToList();
        int maxAt = intervals.IndexOf(sorted[^1]);
        double maxWork = work is { Count: > 0 } ? work.Max() : 0;
        int maxWorkAt = work is { Count: > 0 } ? work.IndexOf(maxWork) : -1;
        double p50 = sorted[sorted.Count / 2], period = periodMs > 0 ? periodMs : p50;
        int late = intervals.Count(v => v > period * 1.5);
        return new FrameStats(sorted.Count + 1, p50, sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))], sorted[^1],
            maxAt, maxWork, maxWorkAt, late, wokeLate, overran);
    }

    public override string ToString() =>
        $"{Frames} 帧，间隔 P50 {P50Ms:F1}ms / P95 {P95Ms:F1}ms / max {MaxMs:F1}ms（第 {MaxAt} 帧后），回调最长 {MaxWorkMs:F1}ms（第 {MaxWorkAt} 帧），" +
        $"晚到 {Late} 帧（醒晚 {WokeLate} / 超时 {Overran}）";
}
