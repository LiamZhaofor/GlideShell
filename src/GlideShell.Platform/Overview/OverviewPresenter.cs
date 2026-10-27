using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Motion;
using GlideShell.Core.Overview;
using GlideShell.Platform.Desktops;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;
using GlideShell.Platform.VirtualDesktops;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 总览的 Windows 实现（<see cref="IOverviewHost"/>）：每块显示器一个覆盖层，窗口画面全部是 DWM 实时缩略图，
/// 背景是桌面壁纸缩略图（不透明度随进度降低 = 变暗）。
/// 线程：控制器从输入 / 渲染线程回调本类，本类把窗口操作都投递到 UI 线程；渲染线程只更新缩略图属性。
/// </summary>
public sealed class OverviewPresenter : IOverviewHost, IOverviewSelectionHost, IDisposable
{
    private readonly UiThread _ui;
    private readonly Func<OverviewConfig> _config;
    private readonly RenderLoop _render;
    private readonly KeyboardHook _keys;
    private readonly List<OverlayWindow> _overlays = [];
    private volatile OverlayWindow[] _renderOverlays = [];
    private OverviewController? _controller;
    private RectD[] _rects = new RectD[WindowCatalog.MaxWindows];
    private double[] _opacity = new double[WindowCatalog.MaxWindows];
    private double[] _active = new double[WindowCatalog.MaxWindows];
    private bool _prepared;
    private bool _shown;
    private volatile bool _labelsShown;
    private long? _hoveredId;
    private long _sessionStartUs;
    private volatile bool _applyErrorLogged;
    private long? _raisedId; // UI 线程：本次会话已提到最上层的窗口
    private long? _activatedId; // UI 线程：本次会话收起时已经切到前台的窗口
    private long? _renderEmphasized; // 渲染线程：上一帧强调的窗口
    private readonly SharedThumbnailSource _shared;
    private readonly VirtualDesktopService? _desktops;
    private DesktopState _barState = DesktopState.Empty; // UI 线程：本次会话桌面条上的桌面
    private List<OverviewWindow> _barWindows = []; // UI 线程：各桌面不是最小化的窗口（Z 序），放大进入后交焦点用
    private List<OverviewWindow> _allWindows = []; // UI 线程：各桌面的全部窗口（Z 序），排各桌面的页
    private Guid? _homeDesktop; // UI 线程：打开总览时所在的桌面（那一页带壁纸，本次会话不能删）
    private Guid? _pageTarget; // UI 线程：总览里翻页翻到的桌面（真实桌面已在切换）
    private Guid? _pageFrom;
    private volatile PageBuffers? _pageBuffers; // 其余各页每帧的矩形（UI 线程准备，渲染线程写）

    private sealed class PageBuffers(int pages, Func<int, int> size)
    {
        public RectD[][] Rects { get; } = Enumerable.Range(0, pages).Select(k => new RectD[size(k)]).ToArray();
        public double[][] Opacity { get; } = Enumerable.Range(0, pages).Select(k => new double[size(k)]).ToArray();
        public double[][] Active { get; } = Enumerable.Range(0, pages).Select(k => new double[size(k)]).ToArray();
    }
    private Guid? _switchTarget; // UI 线程：点了桌面条上的别的桌面，已经在下面切过去
    private Guid? _switchPrevious;
    private Guid? _removeOnSwitch; // UI 线程：删的是当前桌面——放大进入相邻桌面时由删除来切换（系统删当前桌面会自己切过去）
    private long _switchStartedMs;
    private DesktopBarHit _barHovered = DesktopBarHit.Nothing; // UI 线程
    private volatile bool _barShown; // 渲染线程决定、UI 线程重绘用
    private volatile bool _barExpanded;

    // 拖动窗口（UI 线程）
    private OverlayWindow? _press; // 按下时所在的覆盖层（null = 没按着）
    private double _pressX, _pressY;
    private long? _pressWindow; // 按在了哪个窗口上（按在桌面条上为 null）
    private bool _dragging;
    private long _dragWindow;
    private Task<Guid?>? _dropMove; // 已放到别的桌面：移动窗口（新建桌面时先建）的任务，结果是目标桌面
    private readonly HashSet<long> _moved = []; // 本次会话移到别的桌面的窗口
    private bool _renderDragging; // 渲染线程：上一帧有拖动 / 放下中的窗口

    // 显示桌面
    private volatile nint[] _desktopHidden = []; // 上次显示桌面最小化的窗口（Z 序，最上层在前）
    private Dictionary<nint, RectD> _desktopHiddenRects = []; // UI 线程：它们最小化前的真实位置（拉回时飞回这里）
    private bool _desktopPullBack; // UI 线程：本次会话是把窗口拉回来（从进度 1 开始）
    private nint[] _desktopRestoring = []; // UI 线程：收起时正在还原的窗口

    /// <param name="desktops">虚拟桌面服务；为 null 或自检不通过时没有桌面条。</param>
    public OverviewPresenter(UiThread ui, Func<OverviewConfig> config, SharedThumbnailSource shared, VirtualDesktopService? desktops = null)
    {
        _ui = ui;
        _config = config;
        _shared = shared;
        _desktops = desktops;
        _keys = new KeyboardHook(OnKey);
        _render = new RenderLoop(RenderFrame, stats => Log.Info($"总览渲染：{stats}，刷新周期 {_render?.RefreshPeriodMs:F2}ms"));
    }

    public OverviewController Controller => _controller ?? throw new InvalidOperationException("先调用 Attach");

    public void Attach(OverviewController controller) => _controller = controller;

    /// <summary>GlideShell 自己的窗口（窗口枚举时排除）。</summary>
    public IReadOnlyCollection<nint> OwnWindows => _renderOverlays.Select(o => o.Handle).ToHashSet();

    // ---------- IOverviewHost（输入线程 / 渲染线程调用，全部投递到 UI 线程） ----------

    public void Prewarm() => _ui.Post(PrepareScene);

    public void CancelPrewarm() => _ui.Post(() =>
    {
        if (!_shown) ReleaseScene();
    });

    public void Show() => _ui.Post(() =>
    {
        if (!_prepared) PrepareScene();
        if (Controller.Mode == OverviewMode.App) FocusApp();
        else if (Controller.Mode == OverviewMode.Desktop) FocusDesktop();
        // 先在 UI 线程把第一帧摆好再显示覆盖层：否则显示出来的第一帧是还没定位缩略图的黑底，看起来像闪屏
        ApplyFrame(Controller.ProgressAt(HostClock.NowUs()));
        foreach (var o in _overlays)
            if (o.Layer is not null) o.ShowOverlay();
        _shown = true;
        _applyErrorLogged = false;
        _sessionStartUs = HostClock.NowUs();
        Controller.OnShown(_sessionStartUs);
        _render.Wake();
    });

    public void Activate() => _ui.Post(() =>
    {
        if (Controller.Mode == OverviewMode.Desktop)
        {
            CommitDesktop();
            return;
        }
        _keys.Install();
        Log.Info($"{(Controller.Mode == OverviewMode.App ? "应用窗口视图" : "总览")}展开（{(HostClock.NowUs() - _sessionStartUs) / 1000.0:F0}ms），{Controller.Scene.PresentCount} 个窗口");
    });

    /// <summary>收起已经确定（输入线程 / UI 线程）：趁收起动画还在播，在覆盖层下面把选中的窗口提到最上层并切到前台。</summary>
    public void Closing(long? focusWindowId) => _ui.Post(() =>
    {
        if (_shown && Controller.Mode == OverviewMode.Desktop)
        {
            if (_desktopPullBack) RestoreDesktopWindows();
            return;
        }
        if (focusWindowId is not { } id || !_shown) return;
        RaiseForDismiss(id);
        if (_activatedId == id) return;
        if (!WindowActivator.Activate((nint)id, instant: true)) Log.Warn($"无法把窗口 0x{id:X} 置为前台");
        _activatedId = id;
    });

    public void Hide(long? focusWindowId) => _ui.Post(() =>
    {
        _keys.Uninstall();
        EndPress();
        if (_overlays.Any(o => o.Handle == User32.GetCapture())) User32.ReleaseCapture();
        if (_desktopRestoring.Length > 0)
        {
            // 显示桌面后拉回：等窗口都还原到位再撤层（还原是异步的）
            var restoring = _desktopRestoring;
            _desktopRestoring = [];
            long until = Environment.TickCount64 + 600;
            while (Environment.TickCount64 < until && restoring.Any(h => User32.IsWindow(h) && User32.IsIconic(h))) Dwmapi.DwmFlush();
            foreach (var h in restoring) WindowActivator.EnableTransitions(h);
        }
        // 前台窗口被拖到了别的桌面：焦点交给本桌面剩下最上层的窗口（否则还留在那个看不见的窗口里）
        if (focusWindowId is null && _pageTarget is null && _switchTarget is null && _moved.Contains(User32.GetForegroundWindow()) &&
            _barState.Current is { } here)
            DesktopHandoff.ActivateTop(_allWindows.Where(w => !_moved.Contains(w.Id)).ToList(), here);
        if (_switchTarget is { } target)
        {
            HideAfterDesktopSwitch(target);
            return;
        }
        // 翻过页：等真实桌面切好；没有选中窗口就把焦点交给新桌面最上层的窗口（否则还留在旧桌面上看不见的窗口里）
        if (_pageTarget is { } page)
        {
            var remaining = _allWindows.Where(w => !_moved.Contains(w.Id)).ToList(); // 拖到别的桌面的窗口桌面号已过时
            long until = Environment.TickCount64 + 400;
            while (Environment.TickCount64 < until &&
                   (_desktops?.State.Current != page || !DesktopHandoff.CloakSettled(remaining, page, _pageFrom)))
                Dwmapi.DwmFlush();
            if (focusWindowId is null) DesktopHandoff.ActivateTop(remaining, page);
        }
        // 选中的窗口通常已经在收起开始时切到前台了（Closing）；没有的话现在切，并等它真正成为前台（激活由目标程序的线程处理）
        if (focusWindowId is { } id)
        {
            if (_activatedId != id && !WindowActivator.Activate((nint)id, instant: true)) Log.Warn($"无法把窗口 0x{id:X} 置为前台");
            long deadline = Environment.TickCount64 + 50;
            while (User32.GetForegroundWindow() != (nint)id && Environment.TickCount64 < deadline) Dwmapi.DwmFlush();
        }
        // 缩略图按「此刻」真实桌面的 Z 序重排：会话开始时拍下的 Z 序可能已经过时
        // （真机截图：上一次切换刚激活的窗口在快照里还没到上面，撤层时选中窗口下面那层就变了）
        var zOrder = WindowCatalog.ZOrder();
        foreach (var o in _overlays) o.Restack(zOrder);

        // 画了窗口阴影：最后一帧与真实桌面一致，直接撤层，不等不淡出（不卡）。
        // 经典缩略图画不了阴影，才用约 100ms 的淡出把阴影的出现过渡掉
        var shown = _overlays.Where(o => o.IsShown).ToList();
        if (shown.All(o => o.Layer.DrawsShadows))
        {
            Dwmapi.DwmFlush(); // 让重排后的层次先上屏
            foreach (var o in _overlays) o.HideOverlay();
        }
        else
        {
            OverlayFade.FadeOutThenHide(shown.Select(o => o.Handle).ToList(), () =>
            {
                foreach (var o in _overlays) o.HideOverlay();
            });
        }
        _shown = false;
        ReleaseScene();
    });

    public void PageChanged(int desktopIndex) => _ui.Post(() =>
    {
        if (!_shown || _desktops is null || desktopIndex < 0 || desktopIndex >= _barState.Count) return;
        var target = _barState.Desktops[desktopIndex];
        _pageFrom ??= _barState.Current;
        _pageTarget = target;
        _barState = _barState with { CurrentIndex = desktopIndex };
        foreach (var o in _overlays)
        {
            o.SetCurrentPage(desktopIndex);
            o.SetBarCurrent(desktopIndex);
        }
        _raisedId = null;
        _activatedId = null;
        Select(-1);
        _ = _desktops.SwitchToAsync(target).ContinueWith(t =>
        {
            if (t.Result < 0) Log.Warn($"总览翻页：切换桌面失败 0x{t.Result:X8}");
        }, TaskScheduler.Default);
        Log.Info($"总览翻页：到桌面 {desktopIndex + 1}「{_barState.Names[desktopIndex]}」");
    });

    public void SwitchDesktop(int desktopIndex) => _ui.Post(() =>
    {
        if (!_shown || _desktops is null || desktopIndex < 0 || desktopIndex >= _barState.Count) return;
        var target = _barState.Desktops[desktopIndex];
        _switchTarget = target;
        _switchPrevious = _barState.Current;
        _switchStartedMs = Environment.TickCount64;
        foreach (var o in _overlays) o.BeginZoom(desktopIndex);
        Select(-1);
        if (_removeOnSwitch is { } removed)
        {
            // 删当前桌面：它的窗口并入目标桌面、系统随即切过去。那些窗口的桌面号已过时，不按它们等 cloak
            _removeOnSwitch = null;
            _switchPrevious = null;
            _ = _desktops.RemoveDesktopAsync(removed, target).ContinueWith(t =>
            {
                if (t.Result < 0) Log.Warn($"总览桌面条：删除当前桌面失败 0x{t.Result:X8}");
            }, TaskScheduler.Default);
            Log.Info($"总览桌面条：删除当前桌面，并入桌面 {desktopIndex + 1}「{_barState.Names[desktopIndex]}」");
            return;
        }
        _ = _desktops.SwitchToAsync(target).ContinueWith(t =>
        {
            if (t.Result < 0) Log.Warn($"总览桌面条：切换桌面失败 0x{t.Result:X8}");
        }, TaskScheduler.Default);
        Log.Info($"总览桌面条：切到桌面 {desktopIndex + 1}「{_barState.Names[desktopIndex]}」");
    });

    /// <summary>
    /// 放大进入别的桌面落定：等真实桌面完全切好（一般早就切好了，动画约 0.3 秒），焦点交给它最上层的窗口，
    /// 等两帧再撤层——最后一帧就是那个桌面的样子（壁纸、窗口、阴影），撤层无跳变。
    /// </summary>
    private void HideAfterDesktopSwitch(Guid target)
    {
        var windows = _barWindows.Where(w => !_moved.Contains(w.Id)).ToList();
        long deadline = Environment.TickCount64 + 400;
        while (Environment.TickCount64 < deadline &&
               (_desktops?.State.Current != target || !DesktopHandoff.CloakSettled(windows, target, _switchPrevious)))
            Dwmapi.DwmFlush();
        string who = DesktopHandoff.ActivateTop(windows, target);
        Dwmapi.DwmFlush();
        Dwmapi.DwmFlush();
        foreach (var o in _overlays) o.HideOverlay();
        Log.Info($"总览桌面条：撤层（点击后 {Environment.TickCount64 - _switchStartedMs}ms），焦点交给 {who}");
        _switchTarget = null;
        _switchPrevious = null;
        _shown = false;
        ReleaseScene();
    }

    private static readonly HashSet<string> ShellPopupClasses =
    [
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "ControlCenterWindow",
    ];

    /// <summary>
    /// 前台变了（UI 线程，前台监视器回调）。覆盖层盖在工作区上（UIAccess 版还排在系统弹出层之上），
    /// 任务栏露在外面：总览展开时用户点了任务栏——托盘溢出区、开始菜单、通知中心这些外壳弹出层成了前台，
    /// 或者指针在覆盖层外（任务栏上）时别的窗口成了前台（点了任务栏按钮）——就收起总览，让它们露出来
    /// （点的是总览里的窗口就收起到它）。翻页切桌面引起的前台变化指针在覆盖层上，不受影响。
    /// </summary>
    public unsafe void OnForegroundChanged(nint hwnd)
    {
        if (!_shown || Controller.Mode == OverviewMode.Desktop || _switchTarget is not null || !Controller.IsSettledOpen) return;
        if (_overlays.Any(o => o.Handle == hwnd)) return;
        bool shell = ShellPopupClasses.Contains(WindowCatalog.ClassName(hwnd));
        User32.POINT p;
        bool got = User32.GetCursorPos(&p);
        double px = p.X, py = p.Y;
        bool outside = got && !_overlays.Any(o => o.Monitor.WorkArea.Contains(px, py));
        if (!shell && !outside) return;
        long? focus = !shell && Controller.Scene.IndexOf(hwnd) >= 0 ? hwnd : null;
        Log.Info($"总览：任务栏上的操作让 {WindowCatalog.ClassName(hwnd)} 成了前台，收起");
        Controller.Close(focus, HostClock.NowUs());
    }

    /// <summary>总览展开时的三指横滑（任意线程）：按阅读顺序移动选中项。</summary>
    public void MoveSelection(int steps) => _ui.Post(() =>
    {
        if (!_shown || steps == 0) return;
        // 还没有选中项时第一格先选中原来的前台窗口
        if (_hoveredId is null)
        {
            MoveSelection(Selected);
            steps -= Math.Sign(steps);
        }
        for (int i = 0; i < Math.Abs(steps); i++)
            MoveSelection(scene => scene.Cycle(Selected(scene), backwards: steps < 0));
    });

    /// <summary>
    /// 启动后在 UI 线程静默预热一次（不显示）：JIT、覆盖层窗口创建、首次缩略图注册都提前完成，
    /// 第一次手势的预热从约 80ms 降到几毫秒。
    /// </summary>
    public void WarmUp() => _ui.Post(() =>
    {
        if (_shown || _prepared) return;
        PrepareScene();
        ReleaseScene();
    });

    public void Dispose()
    {
        _render.Dispose();
        _keys.Dispose();
        foreach (var o in _overlays)
        {
            o.ReleaseAll();
            o.Dispose();
        }
        _overlays.Clear();
    }

    // ---------- UI 线程 ----------

    private void PrepareScene()
    {
        long t0 = HostClock.NowUs();
        var monitors = Monitors.Enumerate();
        EnsureOverlays(monitors);
        var cfg = _config();
        long t1 = HostClock.NowUs();
        var windows = WindowCatalog.Snapshot(monitors, OwnWindows.ToHashSet());
        long t2 = HostClock.NowUs();
        int primary = Math.Max(0, monitors.FindIndex(m => m.IsPrimary));
        // 桌面条：有两个以上虚拟桌面才显示（每块屏一条，显示各桌面在这块屏上的样子）
        var desktops = _desktops is { Available: true } ? _desktops.State : DesktopState.Empty;
        bool bar = desktops.Count >= 2 && desktops.CurrentIndex >= 0;
        var bars = monitors.Select(m => bar ? DesktopBarGeometry.Compute(m.WorkArea, desktops.Count, m.DpiScale) : null).ToList();
        var areas = monitors.Select(m => new OverviewArea(m.WorkArea, m.DpiScale, bars[m.Index])).ToList();
        var scene = OverviewScene.Build(windows, areas, cfg.PrimaryMonitorOnly, primary, groupByApp: cfg.GroupByApp);
        if (scene.Count > _rects.Length)
        {
            _rects = new RectD[scene.Count];
            _opacity = new double[scene.Count];
            _active = new double[scene.Count];
        }

        var shared = _shared.Get();
        foreach (var o in _overlays)
        {
            // 平铺在本屏的窗口，加上此刻有一部分在本屏上的窗口：后者跨屏或要飞到别的屏上去，
            // 只画在一块屏上的话，展开 / 收起的那一刻它在本屏上的那部分会凭空消失 / 出现
            var members = Enumerable.Range(0, scene.Count)
                .Where(i => (cfg.PrimaryMonitorOnly ? primary : scene.Windows[i].MonitorIndex) == o.Monitor.Index ||
                            scene.Windows[i].Overlaps(o.Monitor.WorkArea))
                .ToList();
            int failure = o.Prepare(scene, members, shared);
            if (failure != 0) _shared.Disable(failure, "总览");
        }
        Controller.SetScene(scene);

        // 桌面条里各桌面的窗口：跨桌面再枚举一次（最小化的不在桌面画面里）
        long t3 = HostClock.NowUs();
        _barState = bar ? desktops : DesktopState.Empty;
        _allWindows = bar ? WindowCatalog.Snapshot(monitors, OwnWindows.ToHashSet(), allDesktops: true) : [];
        _barWindows = _allWindows.Where(w => !w.IsMinimized).ToList();
        nint foreground = User32.GetForegroundWindow();
        foreach (var o in _overlays)
        {
            var mine = _barWindows.Where(w => w.MonitorIndex == o.Monitor.Index || w.Overlaps(o.Monitor.WorkArea)).ToList();
            int failure = o.PrepareBar(bars[o.Monitor.Index], _barState, mine, foreground);
            if (failure != 0) _shared.Disable(failure, "总览桌面条");
        }
        Controller.SetDesktops(bar ? new DesktopBarState(desktops.Names, desktops.CurrentIndex) : DesktopBarState.Empty);
        _homeDesktop = bar ? desktops.Current : null;
        _barStateHomeIndex = bar ? desktops.CurrentIndex : -1;
        if (bar && shared is not null)
            PreparePages(desktops, desktops.CurrentIndex, new Dictionary<int, OverviewScene> { [desktops.CurrentIndex] = scene }, areas, cfg.PrimaryMonitorOnly, primary);
        _prepared = true;
        long t4 = HostClock.NowUs();
        Log.Info($"总览预热：{scene.Count} 个窗口，{monitors.Count} 块屏，耗时 {(t4 - t0) / 1000.0:F1}ms" +
                 $"（显示器 {(t1 - t0) / 1000.0:F1} / 枚举 {(t2 - t1) / 1000.0:F1} / 布局+缩略图 {(t3 - t2) / 1000.0:F1}" +
                 (bar ? $" / 桌面条 {desktops.Count} 个桌面 {_barWindows.Count} 个窗口 {(t4 - t3) / 1000.0:F1}）" : "）"));
    }

    /// <summary>
    /// 应用窗口视图：在预热好的场景上只平铺前台程序的窗口（按进程名；前台不是场景里的窗口时取最上层的窗口），
    /// 其他程序的窗口原地淡出。下标不变，缩略图注册直接沿用；没有桌面条，也不分页。
    /// </summary>
    private void FocusApp()
    {
        var scene = Controller.Scene;
        var front = scene.Windows.FirstOrDefault(w => w.IsForeground) ?? scene.Windows.FirstOrDefault(w => !w.IsMinimized);
        if (front is null) return;
        string app = front.ProcessName;
        var areas = _overlays.Select(o => new OverviewArea(o.Monitor.WorkArea, o.Monitor.DpiScale)).ToList();
        var focused = scene.Focus(w => app.Length > 0 ? w.ProcessName == app : w.Id == front.Id, areas);
        Controller.SetScene(focused);
        Controller.SetDesktops(DesktopBarState.Empty);
        _pageBuffers = null;
        _barState = DesktopState.Empty;
        nint foreground = User32.GetForegroundWindow();
        foreach (var o in _overlays)
        {
            o.ClearPages();
            o.ReplaceScene(-1, focused);
            o.PrepareBar(null, DesktopState.Empty, [], foreground);
        }
        Log.Info($"应用窗口视图：{Path.GetFileName(app)}，{focused.PresentCount} 个窗口");
    }

    /// <summary>
    /// 显示桌面：此刻桌面是否处于「显示桌面」状态——上次最小化的窗口都还最小化着，而且前台是桌面（用户没有又打开别的窗口）。
    /// 输入线程调用（四指上滑开始时），只读几个窗口状态。
    /// </summary>
    public bool DesktopShownNow()
    {
        var hidden = _desktopHidden;
        if (hidden.Length == 0) return false;
        bool any = false;
        foreach (var h in hidden)
        {
            if (!User32.IsWindow(h)) continue;
            if (!User32.IsIconic(h)) return false;
            any = true;
        }
        return any && IsDesktopForeground();
    }

    private static bool IsDesktopForeground()
    {
        nint fg = User32.GetForegroundWindow();
        return fg == 0 || WindowCatalog.ClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>
    /// 显示桌面：在预热好的场景上把每个窗口的「平铺位」设成推出屏幕的位置（<see cref="OverviewScene.PushAside"/>），
    /// 没有桌面条、不分页、背景不变暗。拉回时上次最小化的那些窗口按没最小化处理（从屏幕外滑回原位）。
    /// </summary>
    private void FocusDesktop()
    {
        _desktopPullBack = Controller.ProgressAt(HostClock.NowUs()) > 0.5;
        var hidden = _desktopPullBack ? _desktopHidden.Select(h => (long)h).ToHashSet() : [];
        var scene = Controller.Scene;
        // 最小化窗口的「原位」是它的还原位置；最大化过的窗口还原后是最大化的，所以用最小化前记下的真实位置
        var windows = scene.Windows.Select(w => hidden.Contains(w.Id)
            ? w with { IsMinimized = false, Source = _desktopHiddenRects.GetValueOrDefault((nint)w.Id, w.Source) }
            : w).ToList();
        var cfg = _config();
        var monitors = _overlays.Select(o => o.Monitor).ToList();
        int primary = Math.Max(0, monitors.FindIndex(m => m.IsPrimary));
        var areas = monitors.Select(m => new OverviewArea(m.WorkArea, m.DpiScale)).ToList();
        var desk = OverviewScene.Build(windows, areas, cfg.PrimaryMonitorOnly, primary).PushAside(areas);
        Controller.SetScene(desk);
        Controller.SetDesktops(DesktopBarState.Empty);
        _pageBuffers = null;
        _barState = DesktopState.Empty;
        nint foreground = User32.GetForegroundWindow();
        foreach (var o in _overlays)
        {
            o.ClearPages();
            o.ReplaceScene(-1, desk);
            o.PrepareBar(null, DesktopState.Empty, [], foreground);
        }
        Log.Info($"显示桌面：{(_desktopPullBack ? "拉回" : "推开")} {desk.PresentCount} 个窗口");
    }

    /// <summary>
    /// 显示桌面落定（进度 1）：推开的会话把窗口真正最小化（不播系统动画），等它们都最小化了、焦点交给桌面，再撤层；
    /// 拉回的会话被弹回到 1（没拉过一半）：窗口本来就还最小化着，直接撤层。最后一帧就是壁纸 + 桌面图标，撤层无跳变。
    /// </summary>
    private void CommitDesktop()
    {
        if (!_shown) return;
        if (!_desktopPullBack)
        {
            var scene = Controller.Scene;
            var hide = Enumerable.Range(0, scene.Count).Where(scene.IsPresent).Select(i => (nint)scene.Windows[i].Id).ToArray();
            _desktopHiddenRects = Enumerable.Range(0, scene.Count).Where(scene.IsPresent).ToDictionary(i => (nint)scene.Windows[i].Id, i => scene.Windows[i].Source);
            foreach (var h in hide) WindowActivator.MinimizeQuietly(h);
            long until = Environment.TickCount64 + 600;
            while (Environment.TickCount64 < until && hide.Any(h => User32.IsWindow(h) && !User32.IsIconic(h))) Dwmapi.DwmFlush();
            foreach (var h in hide) WindowActivator.EnableTransitions(h);
            nint progman = User32.FindWindowW("Progman", null);
            if (progman != 0) WindowActivator.Activate(progman);
            _desktopHidden = hide;
        }
        foreach (var o in _overlays) o.HideOverlay();
        _shown = false;
        ReleaseScene();
        Controller.EndDesktop();
        Log.Info(_desktopPullBack ? "显示桌面：没有拉回，桌面仍显示" : $"显示桌面：已最小化 {_desktopHidden.Length} 个窗口");
    }

    /// <summary>显示桌面后拉回（松手决定收起、或触发角）：趁动画还在播，从下往上依次还原，最上层的最后还原、成为前台。</summary>
    private void RestoreDesktopWindows()
    {
        var hidden = _desktopHidden;
        _desktopHidden = [];
        for (int i = hidden.Length - 1; i >= 0; i--) WindowActivator.RestoreQuietly(hidden[i]);
        _desktopRestoring = hidden;
        Log.Info($"显示桌面：还原 {hidden.Length} 个窗口");
    }

    /// <summary>
    /// 每个桌面排一页（总览里四指横滑翻页用）：<paramref name="home"/> 那一页就是 <paramref name="homeScene"/>（已注册、带壁纸），
    /// 其余桌面用跨桌面枚举到的窗口排版。各桌面最上层的窗口标成前台：换到那一页再收起时它会被激活，活动阴影要画在它身上。
    /// 任一块屏准备失败就不分页（四指横滑不接管）。
    /// </summary>
    private void PreparePages(DesktopState state, int home, IReadOnlyDictionary<int, OverviewScene> keep, IReadOnlyList<OverviewArea> areas, bool primaryOnly, int primary)
    {
        var scenes = new OverviewScene[state.Count];
        for (int d = 0; d < state.Count; d++)
        {
            if (keep.TryGetValue(d, out var kept))
            {
                scenes[d] = kept;
                continue;
            }
            var mine = _allWindows.Where(w => w.DesktopId == state.Desktops[d]).ToList();
            int top = mine.FindIndex(w => !w.IsMinimized);
            for (int i = 0; i < mine.Count; i++)
                if (mine[i].IsForeground != (i == top)) mine[i] = mine[i] with { IsForeground = i == top };
            scenes[d] = OverviewScene.Build(mine, areas, primaryOnly, primary, groupByApp: _config().GroupByApp);
        }
        foreach (var o in _overlays)
        {
            var members = scenes.Select(sc => (IReadOnlyList<int>)Enumerable.Range(0, sc.Count)
                .Where(i => (primaryOnly ? primary : sc.Windows[i].MonitorIndex) == o.Monitor.Index || sc.Windows[i].Overlaps(o.Monitor.WorkArea))
                .ToList()).ToList();
            int failure = o.PreparePages(scenes, home, members, state.Desktops);
            if (failure != 0)
            {
                _shared.Disable(failure, "总览翻页");
                return;
            }
        }
        if (!_overlays.All(o => o.HasPages)) return;
        _pageBuffers = new PageBuffers(scenes.Length, k => scenes[k].Count);
        Controller.SetPages(scenes, home);
    }

    /// <summary>UI 线程：按某个进度把全部缩略图摆好（显示覆盖层之前用；用局部数组，不碰渲染线程的缓冲）。</summary>
    private void ApplyFrame(double progress)
    {
        var scene = Controller.Scene;
        var rects = new RectD[scene.Count];
        var opacity = new double[scene.Count];
        var frame = new OverviewFrame(progress, 0, 0, TransitionPhase.Tracking, true, null);
        var active = new double[scene.Count];
        OverviewController.WindowRects(scene, frame, rects, opacity);
        OverviewController.ActiveShadowWeights(scene, frame, active);
        double wallpaper = 1 - BackdropDim * Math.Clamp(progress, 0, 1);
        foreach (var o in _overlays) o.Layer.Apply(rects, opacity, active, wallpaper);
    }

    private void ReleaseScene()
    {
        foreach (var o in _overlays) o.Release();
        Controller.SetScene(OverviewScene.Empty);
        _prepared = false;
        _hoveredId = null;
        _raisedId = null;
        _activatedId = null;
        _barHovered = DesktopBarHit.Nothing;
        _pageTarget = null;
        _pageFrom = null;
        _homeDesktop = null;
        _pageBuffers = null;
        _moved.Clear();
        _dropMove = null;
        _removeOnSwitch = null;
        _dragging = false;
        _press = null;
        Controller.SetHovered(null);
    }

    // 显示器布局变了（接 / 拔显示器、改分辨率或缩放）就重建覆盖层
    private void EnsureOverlays(List<MonitorInfo> monitors)
    {
        bool same = _overlays.Count == monitors.Count &&
                    _overlays.Zip(monitors).All(p => p.First.Monitor.WorkArea == p.Second.WorkArea && p.First.Monitor.DpiScale == p.Second.DpiScale);
        if (same) return;
        foreach (var o in _overlays)
        {
            o.ReleaseAll();
            o.Dispose();
        }
        _overlays.Clear();
        foreach (var m in monitors) _overlays.Add(new OverlayWindow(m, OnMouse));
        _renderOverlays = _overlays.ToArray();
    }

    /// <summary>
    /// 鼠标：按下记下位置，没拖动就松开 = 点击（按下时的位置）；按在窗口上拖过系统拖动阈值就开始拖动窗口
    /// （只在能移动窗口、有桌面条时；否则照旧是点击）。
    /// </summary>
    private void OnMouse(OverlayWindow overlay, OverlayMouse kind, double x, double y)
    {
        if (Controller.Mode == OverviewMode.Desktop) return; // 显示桌面的动画途中不响应鼠标
        switch (kind)
        {
            case OverlayMouse.Down:
                _press = overlay;
                _pressX = x;
                _pressY = y;
                _pressWindow = PressedWindow(overlay, x, y);
                break;
            case OverlayMouse.Move:
                if (_dragging) DragMove(overlay, x, y);
                else if (_press is not null && _pressWindow is { } id && Math.Max(Math.Abs(x - _pressX), Math.Abs(y - _pressY)) >= DragThreshold(overlay))
                {
                    if (!BeginDrag(overlay, id, x, y)) _pressWindow = null; // 不能拖：松开时照旧按点击处理
                    else DragMove(overlay, x, y);
                }
                else if (_press is null) OnMouseMove(overlay, x, y);
                break;
            case OverlayMouse.Up:
                if (_dragging) Drop(overlay, x, y);
                else if (_press is { } pressed) OnClick(pressed, _pressX, _pressY);
                _press = null;
                break;
            case OverlayMouse.Lost:
                EndPress();
                break;
        }
    }

    /// <summary>取消按下 / 拖动（窗口飞回原位）。</summary>
    private void EndPress()
    {
        if (_dragging)
        {
            _dragging = false;
            Controller.EndDrag(null, HostClock.NowUs());
        }
        _press = null;
    }

    private static double DragThreshold(OverlayWindow overlay) => Math.Max(User32.GetSystemMetrics(User32.SM_CXDRAG), 6 * overlay.Monitor.DpiScale);

    /// <summary>按下处的窗口（只有能把窗口移到别的桌面时才算，按在桌面条上不算）。</summary>
    private long? PressedWindow(OverlayWindow overlay, double x, double y)
    {
        if (_desktops is not { CanMoveWindows: true } || overlay.Bar is not { } bar || _barState.Count < 2) return null;
        if (overlay.CloseButtonRect is { } close && CloseButton.Hit(close, x, y)) return null; // 按在关闭按钮上：不拖
        long now = HostClock.NowUs();
        if (bar.Contains(x, y, Controller.BarExpansionAt(now))) return null;
        var scene = Controller.Scene;
        int index = scene.HitTest(x, y, Controller.ProgressAt(now), Controller.BarExpansionAt(now));
        return index >= 0 ? scene.Windows[index].Id : null;
    }

    private bool BeginDrag(OverlayWindow overlay, long id, double x, double y)
    {
        var scene = Controller.Scene;
        int index = scene.IndexOf(id);
        if (index < 0 || overlay.Bar is not { } bar) return false;
        // 移到桌面条上时缩到它在桌面缩略图里的大小的 1.6 倍（太小了看不清拖的是哪个）
        var inThumb = bar.InThumb(0, scene.Windows[index].Source);
        var small = new RectD(0, 0, inThumb.Width * 1.6, inThumb.Height * 1.6);
        if (!Controller.BeginDrag(id, x, y, small, HostClock.NowUs())) return false;
        _dragging = true;
        _dragWindow = id;
        Select(-1);
        foreach (var o in _overlays) o.Lift(index);
        Log.Info($"总览：开始拖动窗口 0x{id:X}");
        return true;
    }

    /// <summary>
    /// 拖动中：指针进入桌面条展开后的范围就展开（不用先碰到收起时那一条，拖着窗口往上走自然就展开），
    /// 指针下的桌面高亮，窗口在桌面条上缩小。
    /// </summary>
    private void DragMove(OverlayWindow overlay, double x, double y)
    {
        long now = HostClock.NowUs();
        bool inBar = false;
        var hit = DesktopBarHit.Nothing;
        if (overlay.Bar is { } bar)
        {
            double e = Controller.BarExpansionAt(now);
            inBar = bar.Contains(x, y, 1);
            Controller.SetBarHovered(inBar, now);
            if (inBar) hit = bar.Hit(x, y, e, removable: false);
        }
        if (hit != _barHovered)
        {
            _barHovered = hit;
            UpdateDecorations();
        }
        Controller.DragTo(x, y, inBar, now);
    }

    /// <summary>
    /// 松开：放在别的桌面上 → 窗口飞进那个缩略图、真实窗口立刻移过去；放在「+」上 → 新建桌面再移过去；
    /// 放在别处（或当前桌面上）→ 飞回原位。
    /// </summary>
    private void Drop(OverlayWindow overlay, double x, double y)
    {
        _dragging = false;
        long now = HostClock.NowUs();
        var scene = Controller.Scene;
        int index = scene.IndexOf(_dragWindow);
        RectD? into = null;
        Guid? target = null; // null 且 into 非空 = 放在了「+」上：新建桌面
        if (overlay.Bar is { } bar && index >= 0 && _desktops is { CanMoveWindows: true })
        {
            double e = Controller.BarExpansionAt(now);
            var hit = bar.Contains(x, y, 1) ? bar.Hit(x, y, e, removable: false) : DesktopBarHit.Nothing;
            var source = scene.Windows[index].Source;
            if (hit.Part == DesktopBarPart.Desktop && hit.Index != _barState.CurrentIndex && hit.Index < _barState.Count)
            {
                target = _barState.Desktops[hit.Index];
                into = bar.InThumb(hit.Index, source, e);
            }
            else if (hit.Part == DesktopBarPart.Add)
            {
                var add = e >= 0.5 ? bar.AddTile : bar.AddPill;
                var shrunk = bar.InThumb(0, source, e);
                into = new RectD(add.CenterX - shrunk.Width / 4, add.CenterY - shrunk.Height / 4, shrunk.Width / 2, shrunk.Height / 2);
            }
        }
        var after = Controller.EndDrag(into, now);
        if (after is null || _desktops is not { } desktops) return;

        foreach (var o in _overlays) o.ReplaceScene(Controller.Page, after);
        nint hwnd = (nint)_dragWindow;
        _moved.Add(_dragWindow);
        string name = target is { } t ? _barState.Names[_barState.Desktops.ToList().IndexOf(t)] : "新桌面";
        _dropMove = Task.Run(async () =>
        {
            Guid? to = target ?? await desktops.CreateDesktopAsync();
            if (to is not { } id) return null;
            int hr = await desktops.MoveWindowAsync(hwnd, id);
            if (hr < 0)
            {
                Log.Warn($"总览：把窗口 0x{hwnd:X} 移到「{name}」失败 0x{hr:X8}");
                return null;
            }
            Log.Info($"总览：窗口 0x{hwnd:X} 移到「{name}」");
            return to;
        });
        if (_barHovered != DesktopBarHit.Nothing)
        {
            _barHovered = DesktopBarHit.Nothing;
            UpdateDecorations();
        }
    }

    /// <summary>
    /// 拖动落定（渲染线程发现放下的动画播完）：缩略图放回原来的层次；放到了别的桌面的话，等真实窗口移好，
    /// 再把它加进那个桌面的页、更新桌面条（新建的桌面整条重建）。
    /// </summary>
    private void DragSettled()
    {
        if (!_dragging) // 投递过来之前可能已经开始了下一次拖动（Lift 会先放回上一个）
            foreach (var o in _overlays) o.Unlift();
        if (_dropMove is not { } move) return;
        _dropMove = null;
        nint hwnd = (nint)_dragWindow;
        move.ContinueWith(t => _ui.Post(() => FinishDrop(t.Result, hwnd)), TaskScheduler.Default);
    }

    private void FinishDrop(Guid? desktop, nint hwnd)
    {
        if (!_shown || _desktops is null || _switchTarget is not null) return;
        if (desktop is not { } id) return;
        int page = _barState.Desktops.ToList().IndexOf(id);
        if (page < 0)
        {
            RefreshBar(); // 新建的桌面：整条重建（各页也按新列表重排）
            return;
        }
        // 加进那个桌面的页（它不在屏幕上，直接排到新位置）
        if (Controller.Pages.Count > page && _allWindows.FirstOrDefault(w => w.Id == hwnd) is { } window)
        {
            var moved = window with { DesktopId = id, IsForeground = false };
            if (Controller.AddToPage(page, moved) >= 0)
            {
                var scene = Controller.Pages[page];
                foreach (var o in _overlays)
                {
                    bool mine = (_config().PrimaryMonitorOnly ? o.Monitor.IsPrimary : moved.MonitorIndex == o.Monitor.Index) || moved.Overlaps(o.Monitor.WorkArea);
                    int failure = o.AddPageWindow(page, scene, mine);
                    if (failure != 0) Log.Warn($"总览：把窗口加到第 {page + 1} 页失败 0x{failure:X8}");
                }
                var pages = Controller.Pages;
                _pageBuffers = new PageBuffers(pages.Count, k => pages[k].Count);
            }
        }
        RefreshBarThumbnails();
    }

    private void OnMouseMove(OverlayWindow overlay, double x, double y)
    {
        // 桌面条：指针进入就展开，离开就收回；在条上时不选窗口
        if (overlay.Bar is { } bar)
        {
            long now = HostClock.NowUs();
            double e = Controller.BarExpansionAt(now);
            bool inBar = bar.Contains(x, y, e);
            Controller.SetBarHovered(inBar, now);
            var hit = inBar ? BarHit(bar, x, y, e) : DesktopBarHit.Nothing;
            if (hit != _barHovered)
            {
                _barHovered = hit;
                UpdateDecorations();
            }
            if (inBar)
            {
                if (_hoveredId is not null) Select(-1);
                return;
            }
        }
        var scene = Controller.Scene;
        long hitUs = HostClock.NowUs();
        int index = scene.HitTest(x, y, Controller.ProgressAt(hitUs), Controller.BarExpansionAt(hitUs));
        long? id = index >= 0 ? scene.Windows[index].Id : null;
        if (id == _hoveredId) return;
        Select(index);
    }

    private void OnClick(OverlayWindow overlay, double x, double y)
    {
        if (overlay.Bar is { } bar)
        {
            long now = HostClock.NowUs();
            double e = Controller.BarExpansionAt(now);
            if (bar.Contains(x, y, e))
            {
                var hit = BarHit(bar, x, y, e);
                switch (hit.Part)
                {
                    case DesktopBarPart.Desktop:
                        Controller.GoToDesktop(hit.Index, now);
                        break;
                    case DesktopBarPart.Add:
                        AddDesktop();
                        break;
                    case DesktopBarPart.Remove:
                        RemoveDesktop(hit.Index);
                        break;
                }
                return; // 点在桌面条的空白处：什么也不做
            }
        }
        if (overlay.CloseButtonRect is { } close && CloseButton.Hit(close, x, y) && _hoveredId is { } hovered)
        {
            CloseWindow(hovered);
            return;
        }
        var scene = Controller.Scene;
        long hitUs = HostClock.NowUs();
        int index = scene.HitTest(x, y, Controller.ProgressAt(hitUs), Controller.BarExpansionAt(hitUs));
        long? id = index >= 0 ? scene.Windows[index].Id : null;
        if (id is { } chosen) RaiseForDismiss(chosen);
        Controller.Close(id, HostClock.NowUs());
    }

    /// <summary>
    /// 关闭按钮：像点窗口自己的「×」一样发 SC_CLOSE，然后每 50ms 看一次：窗口没了（或隐藏了）就在总览里移走它、其余窗口补位；
    /// 弹出了确认框（例如「是否保存」），或 1.5 秒后还在，就收起总览并把它切到前台，让用户看到它在问什么。
    /// </summary>
    private void CloseWindow(long id)
    {
        nint hwnd = (nint)id;
        User32.PostMessageW(hwnd, 0x0112 /* WM_SYSCOMMAND */, 0xF060 /* SC_CLOSE */, 0);
        Log.Info($"总览：关闭窗口 0x{id:X}");
        Select(-1);
        WatchClose(id, Environment.TickCount64 + 1500);
    }

    private void WatchClose(long id, long deadline) => _ui.PostDelayed(50, () =>
    {
        if (!_shown) return;
        nint hwnd = (nint)id;
        if (!User32.IsWindow(hwnd) || !User32.IsWindowVisible(hwnd))
        {
            if (Controller.RemoveWindow(id, HostClock.NowUs()) is { } after)
                foreach (var o in _overlays) o.ReplaceScene(Controller.Page, after);
            if (Controller.Mode == OverviewMode.All && _barState.Count >= 2) RefreshBarThumbnails();
            return;
        }
        nint popup = User32.GetWindow(hwnd, 6 /* GW_ENABLEDPOPUP */);
        if ((popup != 0 && popup != hwnd) || Environment.TickCount64 >= deadline)
        {
            Log.Info($"总览：窗口 0x{id:X} 没有直接关掉（{(popup != 0 && popup != hwnd ? "弹出了对话框" : "超时")}），收起并切到它");
            RaiseForDismiss(id);
            Controller.Close(id, HostClock.NowUs());
            return;
        }
        WatchClose(id, deadline);
    });

    private static DesktopBarHit BarHit(DesktopBarGeometry bar, double x, double y, double expansion) => bar.Hit(x, y, expansion);

    /// <summary>桌面条末尾的「+」：新建桌面（加在最后、不切过去），桌面条随之多一个缩略图。</summary>
    private void AddDesktop()
    {
        if (_desktops is null || !_shown) return;
        _ = _desktops.CreateDesktopAsync().ContinueWith(t =>
        {
            Log.Info(t.Result is { } id ? "总览桌面条：新建桌面" : "总览桌面条：新建桌面失败");
            _ui.Post(RefreshBar);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// 桌面条上的「×」：删除这个桌面，它的窗口并入左边的桌面（第一个桌面并入右边，与任务视图一致）。
    /// 删的是当前桌面：放大进入并入的那个桌面后收起（系统删当前桌面会切过去）；
    /// 删的是打开总览时所在的桌面（翻页之后，那一页带着背景壁纸）：删完总览收起到当前桌面。
    /// </summary>
    private void RemoveDesktop(int index)
    {
        if (_desktops is null || !_shown || index < 0 || index >= _barState.Count || _barState.Count < 2 || _switchTarget is not null) return;
        int fallbackIndex = index > 0 ? index - 1 : index + 1;
        Guid target = _barState.Desktops[index], fallback = _barState.Desktops[fallbackIndex];
        string name = _barState.Names[index];
        if (index == _barState.CurrentIndex)
        {
            _removeOnSwitch = target;
            Controller.GoToDesktop(fallbackIndex, HostClock.NowUs());
            return;
        }
        if (target == _homeDesktop)
        {
            Controller.Close(null, HostClock.NowUs());
            _ = _desktops.RemoveDesktopAsync(target, fallback).ContinueWith(t =>
                Log.Info(t.Result < 0 ? $"总览桌面条：删除「{name}」失败 0x{t.Result:X8}" : $"总览桌面条：删除「{name}」（打开总览时所在的桌面，总览收起）"),
                TaskScheduler.Default);
            return;
        }
        _ = _desktops.RemoveDesktopAsync(target, fallback).ContinueWith(t =>
        {
            if (t.Result < 0) Log.Warn($"总览桌面条：删除「{name}」失败 0x{t.Result:X8}");
            else Log.Info($"总览桌面条：删除「{name}」");
            _ui.Post(RefreshBar);
        }, TaskScheduler.Default);
    }

    /// <summary>桌面增删后：按最新的桌面列表重新准备桌面条与各页（窗口区域不动）。</summary>
    private void RefreshBar()
    {
        if (!_shown || _desktops is null || _switchTarget is not null) return;
        var state = _desktops.State;
        if (state.Count < 2 || state.CurrentIndex < 0) return;
        var old = _barState;
        var oldPages = Controller.Pages;
        if (!RefreshBarThumbnails()) return;
        // 桌面增删后页的下标都变了：按新列表重排各页。起始页（带壁纸）与当前页（可能刚移走过窗口、正在补位）的场景与注册不变
        int home = _homeDesktop is { } h ? state.Desktops.ToList().IndexOf(h) : -1;
        var keep = new Dictionary<int, OverviewScene>();
        foreach (var id in new[] { _homeDesktop, old.Current })
        {
            int before = id is { } d ? old.Desktops.ToList().IndexOf(d) : -1, after = id is { } e ? state.Desktops.ToList().IndexOf(e) : -1;
            if (before >= 0 && before < oldPages.Count && after >= 0) keep[after] = oldPages[before];
        }
        var monitors = _overlays.Select(o => o.Monitor).ToList();
        _allWindows = WindowCatalog.Snapshot(monitors, OwnWindows.ToHashSet(), allDesktops: true);
        if (keep.ContainsKey(home) && _shared.Get() is not null)
        {
            var cfg = _config();
            int primary = Math.Max(0, monitors.FindIndex(m => m.IsPrimary));
            var areas = monitors.Select(m => new OverviewArea(m.WorkArea, m.DpiScale, DesktopBarGeometry.Compute(m.WorkArea, state.Count, m.DpiScale))).ToList();
            PreparePages(state, home, keep, areas, cfg.PrimaryMonitorOnly, primary);
            foreach (var o in _overlays) o.SetCurrentPage(state.CurrentIndex);
            Controller.SetPages(Controller.Pages, state.CurrentIndex);
        }
        _barStateHomeIndex = home;
    }

    /// <summary>按最新的桌面列表与各桌面的窗口重新准备桌面条（缩略图里的窗口跟着变）。桌面不到两个时返回 false。</summary>
    private bool RefreshBarThumbnails()
    {
        if (!_shown || _desktops is null || Controller.Mode != OverviewMode.All) return false;
        var state = _desktops.State;
        if (state.Count < 2 || state.CurrentIndex < 0) return false;
        var monitors = _overlays.Select(o => o.Monitor).ToList();
        _barState = state;
        _barWindows = WindowCatalog.Snapshot(monitors, OwnWindows.ToHashSet(), allDesktops: true).Where(w => !w.IsMinimized).ToList();
        nint foreground = User32.GetForegroundWindow();
        foreach (var o in _overlays)
        {
            var mine = _barWindows.Where(w => w.MonitorIndex == o.Monitor.Index || w.Overlaps(o.Monitor.WorkArea)).ToList();
            int failure = o.PrepareBar(DesktopBarGeometry.Compute(o.Monitor.WorkArea, state.Count, o.Monitor.DpiScale), state, mine, foreground);
            if (failure != 0) _shared.Disable(failure, "总览桌面条");
        }
        Controller.SetDesktops(new DesktopBarState(state.Names, state.CurrentIndex));
        _barHovered = DesktopBarHit.Nothing;
        UpdateDecorations();
        return true;
    }

    private int _barStateHomeIndex = -1; // UI 线程：起始桌面在当前桌面列表里的下标

    private bool OnKey(uint vk)
    {
        const uint VkEscape = 0x1B, VkReturn = 0x0D, VkTab = 0x09, VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28;
        switch (vk)
        {
            case VkEscape:
                EndPress();
                Controller.Close(null, HostClock.NowUs());
                return true;
            case VkReturn:
                if (_hoveredId is { } chosen) RaiseForDismiss(chosen);
                Controller.Close(_hoveredId, HostClock.NowUs());
                return true;
            case VkTab:
                MoveSelection(scene => scene.Cycle(Selected(scene), backwards: User32.GetAsyncKeyState(User32.VK_SHIFT) < 0));
                return true;
            case VkLeft or VkUp or VkRight or VkDown:
                var direction = vk switch
                {
                    VkLeft => NavigationDirection.Left,
                    VkUp => NavigationDirection.Up,
                    VkRight => NavigationDirection.Right,
                    _ => NavigationDirection.Down,
                };
                // 还没有选中项时，第一下先选中当前前台窗口（而不是直接跳走）
                MoveSelection(scene => _hoveredId is null ? Selected(scene) : scene.Navigate(Selected(scene), direction));
                return true;
            default:
                return false;
        }
    }

    /// <summary>键盘选中项：鼠标悬停的窗口；没有时是原来的前台窗口；再没有就是最上层窗口。</summary>
    private int Selected(OverviewScene scene)
    {
        int index = _hoveredId is { } id ? scene.IndexOf(id) : -1;
        if (index < 0)
            for (int i = 0; i < scene.Count && index < 0; i++)
                if (scene.Windows[i].IsForeground) index = i;
        return Math.Max(index, 0);
    }

    /// <summary>键盘移动选中项（与鼠标悬停共用同一个高亮；之后动鼠标会接着按鼠标位置更新）。</summary>
    private void MoveSelection(Func<OverviewScene, int> next)
    {
        var scene = Controller.Scene;
        if (scene.Count == 0) return;
        int index = next(scene);
        if (index < 0 || index >= scene.Count) return;
        Select(index);
    }

    /// <summary>选中（悬停 / 键盘 / 三指横滑）某个窗口，-1 = 不选。只改高亮，不改层次。</summary>
    private void Select(int index)
    {
        var scene = Controller.Scene;
        _hoveredId = index >= 0 ? scene.Windows[index].Id : null;
        Controller.SetHovered(_hoveredId);
        UpdateDecorations();
    }

    /// <summary>
    /// 收起时要成为前台的窗口提到缩略图最上层，飞回原位途中才不会被别的窗口挡住、到最后「凭空出现」。
    /// 只在真正开始收起时提这一个（点击、Enter、或三指下滑开始强调它时）：悬停时就提的话，
    /// 鼠标扫过的每个窗口都会被提一次，其余窗口的层次被打乱，撤层瞬间「选中窗口下面那层」会突变（真机截图确认）。
    /// </summary>
    private void RaiseForDismiss(long windowId)
    {
        if (_raisedId == windowId) return;
        int index = Controller.Scene.IndexOf(windowId);
        if (index < 0) return;
        foreach (var o in _overlays) o.RaiseWindow(index);
        _raisedId = windowId;
    }

    private void UpdateDecorations()
    {
        var scene = Controller.Scene;
        int hovered = _hoveredId is { } id ? scene.IndexOf(id) : -1;
        // 关闭按钮跟着标题一起显示（展开落定、桌面条收起、没有在翻页 / 拖动 / 补位），画在悬停窗口的左上角
        RectD? closeTile = _labelsShown && hovered >= 0 && scene.IsPresent(hovered) ? scene.Tile(hovered, 0) : null;
        foreach (var o in _overlays)
        {
            o.SetDecorations(_labelsShown, hovered);
            o.SetBarDecorations(_barShown, _barExpanded, _barHovered);
            o.SetCloseButton(closeTile is { } t && o.Monitor.WorkArea.Intersects(t) ? t : null);
        }
    }

    /// <summary>背景变暗的程度：显示桌面时不变暗（要露出的就是桌面本身）。</summary>
    private double BackdropDim => Controller.Mode == OverviewMode.Desktop ? 0 : _config().BackdropDim;

    // ---------- 渲染线程 ----------

    private bool RenderFrame(long nowUs)
    {
        var controller = Controller;
        var frame = controller.Sample(nowUs);
        // 只画可见的帧：撤层那一帧之后控制器已复位（进度、放大归零），UI 线程撤层前若再按复位后的状态画一帧，
        // 原来桌面的窗口会回到原位闪一下（真机：点桌面条切桌面时闪一下前一个桌面）
        if (!frame.Visible) return false;
        if (frame.EmphasizedWindow != _renderEmphasized)
        {
            // 三指下滑收起开始强调某个窗口：交给 UI 线程把它提到最上层（只在强调对象变化时投递一次）
            _renderEmphasized = frame.EmphasizedWindow;
            if (frame.EmphasizedWindow is { } emphasized) _ui.Post(() => RaiseForDismiss(emphasized));
        }
        var scene = controller.Scene;
        var rects = _rects;
        var opacity = _opacity;
        var active = _active;
        int count = Math.Min(scene.Count, Math.Min(rects.Length, Math.Min(opacity.Length, active.Length)));
        OverviewController.WindowRects(scene, frame, rects.AsSpan(0, count), opacity.AsSpan(0, count));
        OverviewController.ActiveShadowWeights(scene, frame, active.AsSpan(0, count));

        // 其余各页：已在平铺位，随翻页整体平移（覆盖层里按页偏移）
        var pages = controller.Pages;
        var buffers = _pageBuffers;
        int currentPage = controller.Page;
        bool paged = buffers is not null && pages.Count == buffers.Rects.Length && pages.Count > 1;
        if (paged)
            for (int k = 0; k < pages.Count; k++)
                if (k != currentPage && pages[k].Count <= buffers!.Rects[k].Length)
                    OverviewController.PageRects(pages[k], frame, buffers.Rects[k], buffers.Opacity[k], buffers.Active[k]);

        double wallpaper = (1 - BackdropDim * Math.Clamp(frame.Progress, 0, 1)) * (1 - Math.Clamp(frame.Zoom, 0, 1));
        foreach (var o in _renderOverlays)
        {
            if (!o.IsShown) continue;
            int hr = paged
                ? o.Render(rects.AsSpan(0, count), opacity.AsSpan(0, count), active.AsSpan(0, count), wallpaper, frame, buffers!.Rects, buffers.Opacity, buffers.Active)
                : o.Render(rects.AsSpan(0, count), opacity.AsSpan(0, count), active.AsSpan(0, count), wallpaper, frame);
            if (hr != 0 && !_applyErrorLogged)
            {
                _applyErrorLogged = true; // 每次会话只记一次
                Log.Warn($"更新缩略图失败 0x{hr:X8}（壁纸缩略图：{o.Layer.HasWallpaper}）");
            }
        }

        // 窗口标题与悬停框：展开完成才显示；桌面条展开、翻页（窗口区域在动）或放大进入别的桌面时不显示（GDI 文字跟不上逐帧变化）
        bool steady = frame.ZoomDesktop < 0;
        bool pageSettled = currentPage < 0 || Math.Abs(frame.PagePosition - currentPage) < 0.01; // 翻页途中窗口在横移
        bool labels = frame.LabelOpacity >= 0.5 && frame.BarExpansion < 0.02 && steady && pageSettled && !frame.Dragging && frame.Relayout >= 0.999 &&
                      controller.Mode != OverviewMode.Desktop;
        if (frame.Dragging != _renderDragging)
        {
            _renderDragging = frame.Dragging;
            if (!frame.Dragging) _ui.Post(DragSettled);
        }
        bool barShown = frame.LabelOpacity >= 0.5 && steady;
        bool barExpanded = frame.BarExpansion >= 0.5;
        if (labels != _labelsShown || barShown != _barShown || barExpanded != _barExpanded)
        {
            _labelsShown = labels;
            _barShown = barShown;
            _barExpanded = barExpanded;
            _ui.Post(UpdateDecorations);
        }
        return frame.Visible;
    }
}
