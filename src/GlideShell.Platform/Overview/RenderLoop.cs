using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 渲染线程：按合成器时钟（显示器刷新）逐帧回调，回调里取最新的手势 / 动画状态并更新缩略图。
/// Windows 11 用 DCompositionWaitForCompositorClock；不可用时退回 DwmFlush。
/// 合成器时钟下用 <see cref="FramePacer"/> 晚取样（下一帧起点前留出余量再取样），取样到上屏约 1 帧。
/// 回调返回 false（覆盖层已隐藏）后线程休眠，直到 <see cref="Wake"/>。
/// </summary>
public sealed unsafe class RenderLoop : IDisposable
{
    private readonly Func<long, bool> _renderFrame;
    private readonly Action<FrameStats>? _onSessionEnd;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _running = true;
    private bool _useCompositorClock = true;
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
        while (_running)
        {
            _wake.WaitOne();
            intervals.Clear();
            long last = 0;
            while (_running)
            {
                bool onClock = WaitForNextFrame();
                long tick = HostClock.NowUs();
                if (last != 0) intervals.Add((tick - last) / 1000.0);
                last = tick;
                if (onClock) _pacer.WaitForSamplePoint(tick);
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
                if (!keepGoing) break;
            }
            LastSession = FrameStats.From(intervals);
            _onSessionEnd?.Invoke(LastSession);
        }
    }

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

public readonly record struct FrameStats(int Frames, double P50Ms, double P95Ms, double MaxMs)
{
    public static FrameStats Empty => new(0, 0, 0, 0);

    public static FrameStats From(List<double> intervals)
    {
        if (intervals.Count == 0) return Empty;
        var sorted = intervals.Order().ToList();
        return new FrameStats(sorted.Count + 1, sorted[sorted.Count / 2], sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))], sorted[^1]);
    }

    public override string ToString() => $"{Frames} 帧，间隔 P50 {P50Ms:F1}ms / P95 {P95Ms:F1}ms / max {MaxMs:F1}ms";
}
