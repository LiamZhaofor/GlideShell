using GlideShell.Core.Desktops;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;
using GlideShell.Platform.Overview;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;
using GlideShell.Platform.VirtualDesktops;

namespace GlideShell.Platform.Desktops;

/// <summary>
/// 四指切桌面的 Windows 实现（<see cref="IDesktopSwitchHost"/>，S3 推荐路线）：
/// 每块屏一个置顶工具窗口覆盖层（工具窗口不属于任何虚拟桌面，切换全程可见），
/// 把左 / 中 / 右三个桌面各自的壁纸与窗口（DWM 缩略图，其他桌面上的窗口同样是实时的）拼成一条，随位置平移。
/// 默认用共享缩略图视觉并画窗口阴影（与总览共用后端），撤层前后画面一致；不可用时退回经典缩略图（没有阴影）。
/// 松手决定切换后立刻在覆盖层下面无动画切换真实桌面；动画落定、收到切换通知、再多等 3 帧后撤层
/// （盖住 explorer 逐个解除 cloak 的中间帧），最后把焦点交给新桌面最上层的窗口。
/// </summary>
public sealed class DesktopSwitchPresenter : IDesktopSwitchHost, IDisposable
{
    private const int HideWaitTimeoutMs = 400;

    private readonly UiThread _ui;
    private readonly VirtualDesktopService _desktops;
    private readonly SharedThumbnailSource _shared;
    private readonly RenderLoop _render;
    private readonly List<DesktopOverlay> _overlays = [];
    private volatile DesktopOverlay[] _renderOverlays = [];
    private DesktopSwitchController? _controller;
    private IReadOnlyList<OverviewWindow> _windows = [];
    private DesktopState _sessionState = DesktopState.Empty;
    private bool _prepared;
    private volatile bool _shown;
    private Guid? _committedTarget;

    public DesktopSwitchPresenter(UiThread ui, VirtualDesktopService desktops, SharedThumbnailSource shared)
    {
        _ui = ui;
        _desktops = desktops;
        _shared = shared;
        _render = new RenderLoop(RenderFrame, stats => Log.Info($"切桌面渲染：{stats}，刷新周期 {_render?.RefreshPeriodMs:F2}ms"));
    }

    public DesktopSwitchController Controller => _controller ?? throw new InvalidOperationException("先调用 Attach");

    public void Attach(DesktopSwitchController controller) => _controller = controller;

    private IReadOnlyCollection<nint> OwnWindows => _overlays.Select(o => o.Handle).ToHashSet();

    // ---------- IDesktopSwitchHost ----------

    public void Prewarm() => _ui.Post(Prepare);

    public void CancelPrewarm() => _ui.Post(() =>
    {
        if (!_shown) Release();
    });

    public void Show() => _ui.Post(() =>
    {
        if (!_prepared) Prepare();
        // 先把第一帧摆好再显示：否则显示出来的第一帧是还没定位缩略图的黑底，看起来就是一次闪屏
        double position = Controller.PositionAt(HostClock.NowUs());
        foreach (var o in _overlays) o.Layer.Apply(position);
        foreach (var o in _overlays) o.ShowOverlay();
        _shown = true;
        _render.Wake();
    });

    public void Commit(int offset)
    {
        // 输入线程上调用：桌面切换在虚拟桌面服务的工作线程上执行（5–13ms），不阻塞输入
        var target = _sessionState.Neighbor(offset);
        if (target is not { } id) return;
        _committedTarget = id;
        _ = _desktops.SwitchToAsync(id).ContinueWith(t =>
        {
            if (t.Result < 0) Log.Warn($"切换桌面失败 0x{t.Result:X8}");
        }, TaskScheduler.Default);
    }

    public void Hide(int committedOffset) => _ui.Post(() =>
    {
        var target = committedOffset != 0 ? _committedTarget : null;
        _committedTarget = null;
        if (target is { } id) WaitForSwitchThenHide(id, _sessionState.Current, Environment.TickCount64, -1);
        else HideNow();
    });

    public void Dispose()
    {
        _render.Dispose();
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
    }

    // ---------- UI 线程 ----------

    private void Prepare()
    {
        long t0 = HostClock.NowUs();
        var state = _desktops.State;
        if (state.Current is not { } current) return;
        var monitors = Monitors.Enumerate();
        EnsureOverlays(monitors);
        var windows = WindowCatalog.Snapshot(monitors, OwnWindows.ToHashSet(), allDesktops: true)
            .Where(w => !w.IsMinimized) // 最小化窗口不在桌面画面里
            .ToList();
        Guid? left = state.Neighbor(-1), right = state.Neighbor(1);
        nint foreground = User32.GetForegroundWindow();
        var shared = _shared.Get();
        foreach (var o in _overlays)
        {
            // 本屏的窗口，加上有一部分在本屏上的跨屏窗口（只画在一块屏上的话，显示 / 撤层的那一刻会缺一块）
            var mine = windows.Where(w => w.MonitorIndex == o.Monitor.Index || w.Overlaps(o.Monitor.WorkArea)).ToList();
            int failure = o.Prepare(mine, current, left, right, foreground, shared);
            if (failure != 0) _shared.Disable(failure, "切桌面");
        }
        _windows = windows;
        _sessionState = state;
        Controller.SetNeighbors(left is not null, right is not null, HostClock.NowUs());
        _prepared = true;
        Log.Info($"切桌面预热：桌面 {state.CurrentIndex + 1}/{state.Count}，" +
                 $"本桌面 {windows.Count(w => w.DesktopId == current)} 个窗口，相邻桌面 {windows.Count(w => w.DesktopId == left || w.DesktopId == right)} 个，" +
                 $"耗时 {(HostClock.NowUs() - t0) / 1000.0:F1}ms{(shared is null ? "（经典缩略图）" : "")}");
    }

    private void Release()
    {
        foreach (var o in _overlays) o.ReleaseLayer();
        _prepared = false;
    }


    /// <summary>
    /// 撤层前要等真实桌面完全切好：先等切换通知，再等 explorer 把窗口的外壳 cloak 换过来——
    /// 新桌面的窗口都解除了 cloak、旧桌面的窗口都 cloak 上了。只等通知的话，有窗口的桌面（真机反馈：桌面 1 ↔ 2 闪、
    /// 只有壁纸的 2 ↔ 3 不闪）会在撤层后才看到旧窗口消失 / 新窗口出现，就是一次闪烁。
    /// 然后在覆盖层还盖着时把焦点交出去（标题栏的激活变化也藏在下面），等两帧再撤层。
    /// </summary>
    private void WaitForSwitchThenHide(Guid target, Guid? previous, long startedMs, long switchedMs)
    {
        long now = Environment.TickCount64;
        bool switched = _desktops.State.Current == target;
        if (switched && switchedMs < 0) switchedMs = now;
        bool settled = switched && DesktopHandoff.CloakSettled(_windows, target, previous);
        if (!settled && now - startedMs < HideWaitTimeoutMs)
        {
            _ui.PostDelayed(5, () => WaitForSwitchThenHide(target, previous, startedMs, switchedMs));
            return;
        }
        if (!switched) Log.Warn("等待桌面切换通知超时，直接撤层");
        else if (!settled) Log.Warn("等待窗口 cloak 状态切换超时，直接撤层");
        Log.Info($"切桌面撤层：切换通知 {(switchedMs < 0 ? -1 : switchedMs - startedMs)}ms，窗口到位 {now - startedMs}ms");

        DesktopHandoff.ActivateTop(_windows, target);
        Dwmapi.DwmFlush();
        Dwmapi.DwmFlush();
        HideNow();
    }

    private void HideNow()
    {
        _shown = false;
        foreach (var o in _overlays) o.HideOverlay(); // 不淡出：淡出期间画面停在最后一帧，用户感觉是「卡一下」
        Release();
    }

    private void EnsureOverlays(List<MonitorInfo> monitors)
    {
        bool same = _overlays.Count == monitors.Count &&
                    _overlays.Zip(monitors).All(p => p.First.Monitor.WorkArea == p.Second.WorkArea && p.First.Monitor.DpiScale == p.Second.DpiScale);
        if (same) return;
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
        foreach (var m in monitors) _overlays.Add(new DesktopOverlay(m));
        _renderOverlays = _overlays.ToArray();
    }

    // ---------- 渲染线程 ----------

    private bool RenderFrame(long nowUs)
    {
        var frame = Controller.Sample(nowUs);
        // 只画可见的帧：落定那一帧之后控制器已把位置归零（为下一次手势准备），而 UI 线程撤层还要等切换完成、
        // 交出焦点再等两帧；这期间若按归零后的位置再画一帧，原来那个桌面会被摆回屏幕中央——
        // 真机反馈「空桌面切到有窗口的桌面闪一下空桌面，反之闪一下前一个桌面的窗口」
        if (_shown && frame.Visible)
            foreach (var o in _renderOverlays) o.Layer.Apply(frame.Position);
        return frame.Visible;
    }
}

