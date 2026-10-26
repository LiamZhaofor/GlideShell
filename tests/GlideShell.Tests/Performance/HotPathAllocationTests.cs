using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Gestures;
using GlideShell.Core.Input;
using GlideShell.Core.Overview;
using GlideShell.Core.Switcher;

namespace GlideShell.Tests.Performance;

/// <summary>
/// 守住 ROADMAP 3.2 的约定「热路径零分配」：手指在板上时每份报文走的
/// 组帧 → 识别 → 路由 → 控制器，以及渲染线程每帧的取样与窗口矩形，都不得分配托管对象。
/// 手势开始 / 结束时的少量分配（预热、选目标）不在约束内，所以只测中段的 Changed 报文。
/// </summary>
public class HotPathAllocationTests
{
    private sealed class NoopOverviewHost : IOverviewHost
    {
        public void Prewarm() { }
        public void CancelPrewarm() { }
        public void Show() { }
        public void Activate() { }
        public void Closing(long? focusWindowId) { }
        public void Hide(long? focusWindowId) { }
    }

    private sealed class NoopSwitcherHost : ISwitcherHost
    {
        public void Prewarm() { }
        public void CancelPrewarm() { }
        public void Show() { }
        public void Hide(long? activateWindowId) { }
    }

    private static readonly PtpGeometry Geometry = new(PtpAxis.Create(0, 4010, 124.2), PtpAxis.Create(0, 2443, 75.7), 5);

    /// <summary>三指沿某方向匀速滑动的原始报文（250Hz，设备逻辑坐标），事先全部生成好。</summary>
    private static (int Scan, RawContact[] Slots)[] Swipe(double dxMm, double dyMm, int frames)
    {
        int Lx(double mm) => (int)Math.Round(mm / 124.2 * 4010);
        int Ly(double mm) => (int)Math.Round(mm / 75.7 * 2443);
        var reports = new (int, RawContact[])[frames + 2];
        for (int k = 0; k <= frames + 1; k++)
        {
            bool tip = k <= frames;
            double t = Math.Min(k, frames) / (double)frames;
            double x = 30 + dxMm * t, y = 50 + dyMm * t;
            reports[k] = ((k * 40) % 65_536,
            [
                new(0, tip, true, Lx(x), Ly(y)), new(1, tip, true, Lx(x + 15), Ly(y)), new(2, tip, true, Lx(x + 30), Ly(y)),
                new(0, false, false, 0, 0), new(0, false, false, 0, 0),
            ]);
        }
        return reports;
    }

    private static long AllocatedDuring(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // 速度要让手势在前 30 份报文内就被确认（Began 时的分配不在约束内）：40mm / 260 份 ≈ 每份 0.15mm，约 20 份越过 3mm 阈值
    [Theory]
    [InlineData(0, -40)] // 三指上滑 → 总览
    [InlineData(40, 0)] // 三指横滑 → 切换窗口
    public void Input_path_does_not_allocate_per_report(double dxMm, double dyMm)
    {
        var overviewConfig = new OverviewConfig();
        var switcherConfig = new SwitcherConfig();
        var overview = new OverviewController(new NoopOverviewHost(), () => overviewConfig);
        var switcher = new SwitcherController(new NoopSwitcherHost(), () => switcherConfig);
        var map = GestureMap.Default;
        var router = new GestureRouter(() => map, a => a switch
        {
            GestureAction.Overview => overview,
            GestureAction.SwitchWindows => switcher,
            _ => null,
        }, () => false);
        var recognizer = new GestureRecognizer(new GestureSettings(), router.OnGesture);
        var assembler = new FrameAssembler(Geometry, recognizer.Process);

        var reports = Swipe(dxMm, dyMm, frames: 260);
        long t = 1_000_000;
        void Push(int k) => assembler.Push(t + k * 4_000L, reports[k].Scan, 3, 0, reports[k].Slots);

        // 先完整跑一遍（JIT、首次扩容），再跑第二遍并只测中段
        for (int k = 0; k < reports.Length; k++) Push(k);
        t += 10_000_000;
        for (int k = 0; k < 30; k++) Push(k); // 落指、确认手势（允许分配）
        long allocated = AllocatedDuring(() =>
        {
            for (int k = 30; k < 230; k++) Push(k);
        });
        for (int k = 230; k < reports.Length; k++) Push(k);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Render_sampling_does_not_allocate_per_frame()
    {
        var overviewConfig = new OverviewConfig();
        var overview = new OverviewController(new NoopOverviewHost(), () => overviewConfig);
        var windows = Enumerable.Range(0, 8)
            .Select(i => new OverviewWindow(i + 1, $"W{i}", new RectD(100 * i, 50 * i, 900, 700), i == 7, i == 0, 0, "p"))
            .ToList();
        var scene = OverviewScene.Build(windows, [new OverviewArea(new RectD(0, 0, 2560, 1528), 1.5)]);
        overview.SetScene(scene);
        overview.Toggle(0);
        overview.OnShown(0);
        var rects = new RectD[scene.Count];
        var opacity = new double[scene.Count];

        void Frame(long us)
        {
            var frame = overview.Sample(us);
            OverviewController.WindowRects(scene, frame, rects, opacity);
        }

        for (int i = 0; i < 10; i++) Frame(i * 6_000);
        long allocated = AllocatedDuring(() =>
        {
            for (int i = 10; i < 200; i++) Frame(i * 6_000); // 165Hz 下约 1.2 秒：弹簧动画全程
        });

        Assert.Equal(0, allocated);
    }
}
