using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Motion;
using GlideShell.Core.Overview;
using GlideShell.Core.Search;
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

    /// <summary>最近一次渲染会话的帧统计与刷新周期（诊断用）。</summary>
    public FrameStats LastRender => _render.LastSession;

    public double RefreshPeriodMs => _render.RefreshPeriodMs;
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
    private OverviewScene? _allScene; // UI 线程：应用窗口视图换进来之前的总览场景（点标题行回到总览用）
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

    // 后台程序栏（UI 线程）
    private readonly AppCatalog _apps = new();
    private readonly TrayInvoker _trayInvoker = new();
    private OverlayWindow? _dockOverlay; // 本次会话放后台程序栏的覆盖层（任务栏所在那块屏；不显示时为 null）
    private IReadOnlyList<TrayApp> _dockApps = [];
    private IReadOnlyList<TrayApp> _lastDockApps = []; // 上一次列出的托盘程序：下次总览一开始先按它摆（第一帧就有栏），新列表到了再换
    private int _dockHovered = -1; // 悬停或键盘选中的程序（-1 = 没有）
    private int _dockSession;
    private Task<string?>? _trayAction; // 正在替用户点托盘图标：撤层要等它点完（溢出区在覆盖层下面打开，用户看不到）
    private Launch? _launch; // 左键了栏里的程序，总览开着等它的窗口出来
    private TrayMenu? _menu; // 右键了栏里的程序：它的托盘菜单在总览里弹出、还没关
    // 打字搜索程序（UI 线程）
    private readonly AppLauncher _launcher;
    private ISearchHost? _searchOverlay; // 搜索面板所在的覆盖层或单独的搜索窗（null = 没在搜）
    private readonly Dictionary<nint, SearchWindow> _searchWindows = []; // 按显示器缓存的单独搜索窗
    private SearchWindow? _standalone; // 正在显示的单独搜索窗（快捷键打开、不进总览）
    private string _query = "";
    private IReadOnlyList<SearchResult> _results = [];
    private int _searchSelected = -1;
    private bool _searchPress; // 在面板里按下的（松开在面板外不算点了外面）
    private string? _pendingSearch; // 快捷键打开总览、面板还没出来时先记下打的字（null = 没有在等）
    private bool _pendingEnter; // 等面板时按了 Enter：面板出来就打开第一条
    private bool _searchFromHotkey; // 这次搜索是快捷键打开的：Esc 直接收起总览，删空了也留在搜索里

    /// <summary>
    /// 宿主设置：这个按键（加上此刻按着的修饰键）是不是「打开搜索」的快捷键。总览的按键钩子先收到按键，
    /// 是快捷键就放过去，交给宿主的全局钩子（再按一次收起）。
    /// </summary>
    public Func<uint, bool>? IsSearchHotkey { get; set; }

    private TrayApp? _watchApp; // 刚关了菜单的程序：一阵子里勤快地看它的托盘图标还在不在（选了「退出」就从栏里去掉）
    private long _watchUntilMs;
    private nint _launched; // 本次会话从栏里飞出来的窗口（撤层时确保它在前台）
    private long _flightStartedMs; // 它开始飞的时刻（日志用）

    /// <summary>左键了栏里的程序、正在等它的窗口出来（UI 线程）。</summary>
    private sealed class Launch(TrayApp app, int index, HashSet<uint> processes)
    {
        public TrayApp App { get; } = app;
        public int Index { get; } = index;
        public HashSet<uint> Processes { get; } = processes;
        public long StartedMs { get; } = Environment.TickCount64;
        public long ClickedMs = -1; // 托盘点击完成的时刻（-1 = 还没完成）
        public bool Failed; // 点击失败
        public bool Restored; // 已经替它直接显示过主窗口
        public bool FromMenu; // 选了托盘菜单里的一项（不替它显示主窗口：选的可能是「退出」）
        public bool Quiet; // 菜单没选就关了：悄悄等一会儿，有窗口出来照样飞进来，没有就留在总览里
    }

    private const long LaunchWaitMs = 900, LaunchMaxMs = 2000, RestoreAfterMs = 600;
    private const long MenuWaitMs = 1200; // 右键完成后等菜单弹出的时间
    private const long MenuHoleDelayUs = 250_000; // 菜单弹出多久后在覆盖层上开洞露出真实菜单（等它自己的淡入播完）

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
        _launcher = new AppLauncher(_apps);
        // 带上此刻的内存占用：内存吃紧时（换页）展开头几帧容易晚到，排查卡顿时好对照
        _render = new RenderLoop(RenderFrame, stats => Log.Info($"总览渲染：{stats}，刷新周期 {_render?.RefreshPeriodMs:F2}ms，内存占用 {SystemInfo.MemoryLoad()}%"));
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
        if (_standalone is not null) CloseStandalone(); // 单独搜索开着时三指上滑：收起搜索，进总览
        if (!_prepared) PrepareScene();
        if (Controller.Mode == OverviewMode.App) FocusApp();
        else if (Controller.Mode == OverviewMode.Desktop) FocusDesktop();
        // 先在 UI 线程把第一帧摆好再显示覆盖层：否则显示出来的第一帧是还没定位缩略图的黑底，看起来像闪屏
        ApplyFrame(Controller.ProgressAt(HostClock.NowUs()));
        foreach (var o in _overlays)
            if (o.Layer is not null) o.ShowOverlay();
        _shown = true;
        if (Controller.Mode == OverviewMode.Desktop && _desktopPullBack)
        {
            // 拉回：窗口此刻全在屏幕外，覆盖层盖上之后就在它下面还原它们。缩略图从「最小化时留下的画面」换成实时画面时
            // 会短暂空白、程序还要重绘（Chrome 先是一片底色），趁它们还在屏幕外换完，滑进来的就是真实画面，撤层无跳变
            Dwmapi.DwmFlush();
            RestoreDesktopWindows();
        }
        _applyErrorLogged = false;
        _sessionStartUs = HostClock.NowUs();
        Controller.OnShown(_sessionStartUs);
        _render.Wake();
        if (_pendingSearch is not null) StartPendingSearch();
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
        if (_shown && Controller.Mode == OverviewMode.Desktop) return; // 拉回会话一开始就已经在还原了（FocusDesktop）
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
        EndMenu(); // 菜单还开着：留在原处，撤层后露出来
        ResetSearch();
        if (_trayAction is { IsCompleted: false } clicking)
        {
            // 正在替用户点托盘图标（藏起来的图标要在覆盖层下面打开溢出区）：点完再撤层，用户看不到溢出区开合。
            // 收起后的最后一帧和桌面一样，多停一会儿看不出来
            long until = Environment.TickCount64 + 1200;
            while (!clicking.IsCompleted && Environment.TickCount64 < until) Dwmapi.DwmFlush();
        }
        // 从栏里飞出来的窗口比总览收起慢一些：总览收完了它可能还在飞，接着画完（落定时与真实窗口重合）再撤层
        foreach (var o in _overlays)
        {
            long flightUntil = Environment.TickCount64 + 1500;
            while (o.Flying && Environment.TickCount64 < flightUntil && !o.StepFlight(HostClock.NowUs())) Dwmapi.DwmFlush();
        }
        if (_launched != 0) Log.Info($"后台程序栏：窗口飞入 {Environment.TickCount64 - _flightStartedMs}ms 后撤层");
        if (_launched != 0 && User32.IsWindow(_launched) && User32.GetForegroundWindow() != _launched)
            WindowActivator.Activate(_launched, instant: true); // 从栏里飞出来的窗口：撤层后它在最上面、是前台
        if (_overlays.Any(o => o.Handle == User32.GetCapture())) User32.ReleaseCapture();
        if (_desktopRestoring.Length > 0)
        {
            // 显示桌面后拉回：等窗口都还原到位再撤层（还原是异步的）
            var restoring = _desktopRestoring;
            _desktopRestoring = [];
            long until = Environment.TickCount64 + 600;
            while (Environment.TickCount64 < until && restoring.Any(h => User32.IsWindow(h) && User32.IsIconic(h))) Dwmapi.DwmFlush();
            foreach (var h in restoring)
            {
                WindowActivator.EnableTransitions(h);
                ThumbnailLayer.IconicInsets.TryRemove(h, out _);
            }
            // 异步还原各自完成，谁在上面不确定：按推开前的层次重排，原来最上层的那个切回前台（随后缩略图按真实层次重排、撤层）
            WindowActivator.Stack(restoring);
            if (restoring.Length > 0) WindowActivator.Activate(restoring[0]);
            Dwmapi.DwmFlush();
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
        if (_standalone is { } search && hwnd != search.Handle)
        {
            // 单独的搜索窗开着时切到了别的窗口（Alt+Tab、点了任务栏）：收起
            Log.Info($"搜索窗：{WindowCatalog.ClassName(hwnd)} 成了前台，收起");
            CloseStandalone();
            return;
        }
        if (!_shown || Controller.Mode == OverviewMode.Desktop || _switchTarget is not null || !Controller.IsSettledOpen) return;
        if (_overlays.Any(o => o.Handle == hwnd)) return;
        if (_launch is not null || _menu is not null) return; // 正在打开栏里的程序 / 弹它的菜单：溢出区、程序的窗口先后成为前台，是我们自己点出来的
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
        if (_config().TrayApps || _config().Search) _apps.Refresh();
        if (_config().Search)
        {
            _launcher.SetExtraApps(_config().SearchExtraApps);
            _launcher.Prebuild();
        }
        if (_shown || _prepared) return;
        PrepareScene();
        ReleaseScene();
    });

    public void Dispose()
    {
        _render.Dispose();
        _keys.Dispose();
        _apps.Dispose();
        _trayInvoker.Dispose();
        _launcher.Dispose();
        foreach (var w in _searchWindows.Values) w.Dispose();
        _searchWindows.Clear();
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
        var shared = _shared.Get();
        var decor = _shared.Decor(_ui);
        // 后台程序栏：只在任务栏所在那块屏、合成层装饰可用时有（窗口区域底部给它让位）
        _dockOverlay = cfg.TrayApps && shared is not null && decor is not null ? DockOverlay() : null;
        if (cfg.Search) _launcher.SetExtraApps(cfg.SearchExtraApps);
        if (cfg.Search) _launcher.Prebuild(); // 程序索引超过 10 分钟就在后台重建（开始菜单里装了 / 删了程序），建好顺便拆字取拼音
        // 桌面条：有两个以上虚拟桌面才显示（每块屏一条，显示各桌面在这块屏上的样子）
        var desktops = _desktops is { Available: true } ? _desktops.State : DesktopState.Empty;
        bool bar = desktops.Count >= 2 && desktops.CurrentIndex >= 0;
        var bars = monitors.Select(m => bar ? DesktopBarGeometry.Compute(m.WorkArea, desktops.Count, m.DpiScale, DockInset(m)) : null).ToList();
        var areas = monitors.Select(m => new OverviewArea(m.WorkArea, m.DpiScale, bars[m.Index], DockInset(m))).ToList();
        var scene = OverviewScene.Build(windows, areas, cfg.PrimaryMonitorOnly, primary, groupByApp: cfg.GroupByApp);
        if (scene.Count > _rects.Length)
        {
            _rects = new RectD[scene.Count];
            _opacity = new double[scene.Count];
            _active = new double[scene.Count];
        }

        foreach (var o in _overlays)
        {
            // 平铺在本屏的窗口，加上此刻有一部分在本屏上的窗口：后者跨屏或要飞到别的屏上去，
            // 只画在一块屏上的话，展开 / 收起的那一刻它在本屏上的那部分会凭空消失 / 出现
            var members = Enumerable.Range(0, scene.Count)
                .Where(i => (cfg.PrimaryMonitorOnly ? primary : scene.Windows[i].MonitorIndex) == o.Monitor.Index ||
                            scene.Windows[i].Overlaps(o.Monitor.WorkArea))
                .ToList();
            o.CornerRadiusDip = cfg.CornerRadius;
            int failure = o.Prepare(scene, members, shared, decor);
            if (failure != 0) _shared.Disable(failure, "总览");
        }
        Controller.SetScene(scene);
        StartDock(windows);

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
        var focused = scene.Focus(w => app.Length > 0 ? w.ProcessName == app : w.Id == front.Id, areas, minimizedShelf: true);
        _allScene = _overlays.All(o => o.OnHomePage) ? scene : null;
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
        _dockOverlay?.PrepareDock(null, [], false); // 应用窗口视图没有后台程序栏（预热时已按上次的列表摆好了）
        Log.Info($"应用窗口视图：{Path.GetFileName(app)}，{focused.PresentCount} 个窗口");
    }

    /// <summary>
    /// 总览开着时直接换成第 <paramref name="index"/> 个窗口所属程序的应用窗口视图：桌面条与其余各页撤掉，
    /// 选中的窗口从现在的平铺位重排过去，其余窗口原地淡出。翻到了别的桌面那一页时不换（返回 false）。
    /// </summary>
    private bool SwitchToApp(OverviewScene scene, int index)
    {
        if (_overlays.Any(o => !o.OnHomePage)) return false; // 别的桌面那一页的缩略图在另一层上，撤掉分页就没了
        var w = scene.Windows[index];
        string app = w.ProcessName;
        var areas = _overlays.Select(o => new OverviewArea(o.Monitor.WorkArea, o.Monitor.DpiScale)).ToList();
        var focused = scene.Focus(x => app.Length > 0 ? x.ProcessName == app : x.Id == w.Id, areas, minimizedShelf: true, animate: true);
        if (!Controller.FocusApp(focused, HostClock.NowUs())) return false;
        _allScene = scene;
        _pageBuffers = null;
        _barState = DesktopState.Empty;
        _barHovered = DesktopBarHit.Nothing;
        _hoveredId = null;
        nint foreground = User32.GetForegroundWindow();
        foreach (var o in _overlays)
        {
            o.ClearPages();
            o.ReplaceScene(-1, focused);
            o.PrepareBar(null, DesktopState.Empty, [], foreground);
        }
        RetractDock();
        UpdateDecorations();
        Log.Info($"总览：换成应用窗口视图 {Path.GetFileName(app)}，{focused.PresentCount} 个窗口");
        return true;
    }

    /// <summary>
    /// 应用窗口视图里回到窗口总览：这个程序的窗口重排回总览里的位置，其他程序的窗口在各自的平铺位淡入；
    /// 桌面条与其余各页重新准备（各桌面的缩略图层有缓存，不用重建）。没有换进来之前的总览场景时返回 false。
    /// </summary>
    private bool SwitchToAll(OverviewScene app)
    {
        if (_allScene is not { } all || all.Count != app.Count) return false;
        var scene = all.ReturnFrom(app);
        if (!Controller.UnfocusApp(scene, HostClock.NowUs())) return false;
        _allScene = null;
        _hoveredId = null;
        foreach (var o in _overlays) o.ReplaceScene(-1, scene);
        if (RefreshBarThumbnails() && _shared.Get() is not null && _desktops is { } desktops)
        {
            var state = desktops.State;
            var cfg = _config();
            var monitors = _overlays.Select(o => o.Monitor).ToList();
            int primary = Math.Max(0, monitors.FindIndex(m => m.IsPrimary));
            var areas = monitors.Select(m => new OverviewArea(m.WorkArea, m.DpiScale, DesktopBarGeometry.Compute(m.WorkArea, state.Count, m.DpiScale, DockInset(m)), DockInset(m))).ToList();
            _allWindows = WindowCatalog.Snapshot(monitors, OwnWindows.ToHashSet(), allDesktops: true);
            PreparePages(state, state.CurrentIndex, new Dictionary<int, OverviewScene> { [state.CurrentIndex] = scene }, areas, cfg.PrimaryMonitorOnly, primary);
            _homeDesktop = state.Current;
            _barStateHomeIndex = state.CurrentIndex;
        }
        ShowDock(animate: true);
        UpdateDecorations();
        Log.Info($"总览：从应用窗口视图回到总览，{scene.PresentCount} 个窗口");
        return true;
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
        // 拉回：最小化的窗口在系统里的层次已经被打乱（按最小化的先后排），缩略图要按推开前记下的层次叠
        var stacking = _desktopPullBack ? _desktopHidden.Concat(WindowCatalog.ZOrder().Where(h => !hidden.Contains(h))).ToList() : null;
        foreach (var o in _overlays)
        {
            o.ClearPages();
            o.ReplaceScene(-1, desk);
            o.PrepareBar(null, DesktopState.Empty, [], foreground);
            if (stacking is not null) o.Restack(stacking);
        }
        _dockOverlay?.PrepareDock(null, [], false); // 显示桌面没有后台程序栏
        Log.Info($"显示桌面：{(_desktopPullBack ? "拉回" : "推开")} {desk.PresentCount} 个窗口");
    }

    /// <summary>
    /// 显示桌面落定（进度 1）：推开的会话把窗口真正最小化（不播系统动画），等它们都最小化了、焦点交给桌面，再撤层；
    /// 拉回的会话被弹回到 1（没拉过一半）：窗口本来就还最小化着，直接撤层。最后一帧就是壁纸 + 桌面图标，撤层无跳变。
    /// </summary>
    private void CommitDesktop()
    {
        // 落定到这里之间又开始了新手势（例如四指下滑后马上上滑）：先不动窗口，等那个手势落定再说
        if (!_shown || !Controller.IsSettledOpen) return;
        nint[] hide;
        if (_desktopPullBack)
        {
            // 拉了一半又松回去（桌面继续显示）：一开始就还原了的窗口重新最小化
            hide = _desktopRestoring;
        }
        else
        {
            var scene = Controller.Scene;
            hide = Enumerable.Range(0, scene.Count).Where(scene.IsPresent).Select(i => (nint)scene.Windows[i].Id).ToArray();
            _desktopHiddenRects = Enumerable.Range(0, scene.Count).Where(scene.IsPresent).ToDictionary(i => (nint)scene.Windows[i].Id, i => scene.Windows[i].Source);
            foreach (var h in hide) ThumbnailLayer.RememberInset(h); // 最小化后缩略图带着透明边框：拉回时按它裁掉
        }
        _desktopRestoring = [];
        foreach (var h in hide) WindowActivator.MinimizeQuietly(h);
        long until = Environment.TickCount64 + 600;
        while (Environment.TickCount64 < until && hide.Any(h => User32.IsWindow(h) && !User32.IsIconic(h))) Dwmapi.DwmFlush();
        foreach (var h in hide) WindowActivator.EnableTransitions(h);
        _desktopHidden = hide;
        if (!Controller.IsSettledOpen)
        {
            // 最小化途中用户已经开始往回拉：转成拉回会话（在覆盖层下面还原），覆盖层留着继续跟手
            _desktopPullBack = true;
            RestoreDesktopWindows();
            Log.Info("显示桌面：最小化途中开始拉回");
            return;
        }
        nint progman = User32.FindWindowW("Progman", null);
        if (progman != 0) WindowActivator.Activate(progman);
        foreach (var o in _overlays) o.HideOverlay();
        _shown = false;
        ReleaseScene();
        Controller.EndDesktop();
        Log.Info(_desktopPullBack ? "显示桌面：没有拉回，桌面仍显示" : $"显示桌面：已最小化 {hide.Length} 个窗口");
    }

    /// <summary>显示桌面后拉回（会话一开始）：从下往上依次还原，最上层的最后还原、成为前台；收起落定时等它们都还原到位再撤层。</summary>
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
        _dockApps = [];
        _dockHovered = -1;
        _dockOverlay = null;
        _launch = null;
        _launched = 0;
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
        if (_menu is { Opened: true } menu)
        {
            // 托盘菜单开着：点在菜单以外 = 关掉菜单（Win32 菜单有全局捕获，到不了这里；自绘菜单不知道点了外面），其余不响应
            if (kind == OverlayMouse.Down && !menu.Popups.Exists(p => p.Rect.Contains(x, y))) menu.Dismiss();
            return;
        }
        if (_searchOverlay is { } search)
        {
            SearchMouse(search, overlay, kind, x, y);
            return;
        }
        switch (kind)
        {
            case OverlayMouse.Down:
                _press = overlay;
                _pressX = x;
                _pressY = y;
                _pressWindow = PressedWindow(overlay, x, y);
                break;
            case OverlayMouse.Move:
                if (_dragging) DragMove(OverlayAt(overlay, x, y), x, y);
                else if (_press is not null && _pressWindow is { } id && Math.Max(Math.Abs(x - _pressX), Math.Abs(y - _pressY)) >= DragThreshold(overlay))
                {
                    if (!BeginDrag(overlay, id, x, y)) _pressWindow = null; // 不能拖：松开时照旧按点击处理
                    else DragMove(overlay, x, y);
                }
                else if (_press is null) OnMouseMove(overlay, x, y);
                break;
            case OverlayMouse.Up:
                if (_dragging) Drop(OverlayAt(overlay, x, y), x, y);
                else if (_press is { } pressed) OnClick(pressed, _pressX, _pressY);
                _press = null;
                break;
            case OverlayMouse.Lost:
                EndPress();
                break;
            case OverlayMouse.RightUp:
                if (!_dragging && overlay.Dock is { } dock && dock.Contains(x, y)) ActivateDock(dock.HitTest(x, y), menu: true, (x, y));
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

    /// <summary>指针所在那块屏的覆盖层：拖着窗口可以拖到别的屏上（按下的覆盖层捕获了鼠标）；不在任何屏的工作区里时用 <paramref name="fallback"/>。</summary>
    private OverlayWindow OverlayAt(OverlayWindow fallback, double x, double y)
        => _overlays.FirstOrDefault(o => o.IsShown && o.Monitor.WorkArea.Contains(x, y)) ?? fallback;

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
            var source = OnScreen(scene.Windows[index], overlay.Monitor.WorkArea);
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
    /// 窗口在 <paramref name="area"/> 这块屏上的位置：放到别的屏的桌面条上时，按它在自己那块屏里的相对位置换算过去，
    /// 缩进那块屏的桌面缩略图里位置才对（缩略图按所在屏的工作区缩小）。
    /// </summary>
    private RectD OnScreen(OverviewWindow window, RectD area)
    {
        var s = window.Source;
        if (area.Intersects(s) || _overlays.FirstOrDefault(o => o.Monitor.Index == window.MonitorIndex)?.Monitor.WorkArea is not { } own) return s;
        double kx = area.Width / Math.Max(1, own.Width), ky = area.Height / Math.Max(1, own.Height);
        return new RectD(area.Left + (s.Left - own.Left) * kx, area.Top + (s.Top - own.Top) * ky, s.Width * kx, s.Height * ky);
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
        // 后台程序栏：指针在栏上时不选窗口
        if (overlay.Dock is { } dock && dock.Contains(x, y))
        {
            int item = dock.HitTest(x, y);
            overlay.DockHovered = item >= 0;
            if (item != _dockHovered) SetDockHover(item);
            if (_hoveredId is not null) Select(-1);
            return;
        }
        overlay.DockHovered = false;
        if (_dockHovered >= 0) SetDockHover(-1);
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
        overlay.LabelHovered = index < 0 && Controller.IsSettledOpen && Controller.Mode is OverviewMode.All or OverviewMode.App && scene.LabelHitTest(x, y) >= 0;
        long? id = index >= 0 ? scene.Windows[index].Id : null;
        if (id == _hoveredId) return;
        Select(index);
    }

    private void OnClick(OverlayWindow overlay, double x, double y)
    {
        if (overlay.Dock is { } dock && dock.Contains(x, y))
        {
            ActivateDock(dock.HitTest(x, y), menu: false); // 点在栏的空白处：什么也不做
            return;
        }
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
        // 点窗口下面的标题行（程序图标 + 标题）：总览里换成只看这个程序窗口的应用窗口视图，应用窗口视图里换回总览（都不收起）
        if (scene.LabelHitTest(x, y) is >= 0 and var label &&
            (Controller.Mode == OverviewMode.All ? SwitchToApp(scene, label) : Controller.Mode == OverviewMode.App && SwitchToAll(scene))) return;
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
            var areas = monitors.Select(m => new OverviewArea(m.WorkArea, m.DpiScale, DesktopBarGeometry.Compute(m.WorkArea, state.Count, m.DpiScale, DockInset(m)), DockInset(m))).ToList();
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
            int failure = o.PrepareBar(DesktopBarGeometry.Compute(o.Monitor.WorkArea, state.Count, o.Monitor.DpiScale, DockInset(o.Monitor)), state, mine, foreground);
            if (failure != 0) _shared.Disable(failure, "总览桌面条");
        }
        Controller.SetDesktops(new DesktopBarState(state.Names, state.CurrentIndex));
        _barHovered = DesktopBarHit.Nothing;
        UpdateDecorations();
        return true;
    }

    private int _barStateHomeIndex = -1; // UI 线程：起始桌面在当前桌面列表里的下标

    private const uint VkEscape = 0x1B, VkReturn = 0x0D, VkTab = 0x09, VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28;
    private const uint VkApps = 0x5D, VkF10 = 0x79, VkBack = 0x08, VkSpace = 0x20;

    // ---------- 打字搜索程序 ----------

    private static bool KeyDown(int vk) => User32.GetAsyncKeyState(vk) < 0;

    /// <summary>
    /// 按键先给搜索（UI 线程，低级键盘钩子里）：没在搜时，字母、数字（或 Ctrl+V 粘贴）开始搜索；在搜时，
    /// 字母数字空格接着输入，退格删字（删空回到窗口），↑↓ / Tab 选结果，Enter 打开，Esc 回到窗口，其余按键吞掉（不漏给下面的程序）。
    /// 总览的按键在输入法之前就截下了，输入框里永远是字母：直接打拼音（wx、weixin），汉字要粘贴。返回 false 的交给总览原来的处理。
    /// </summary>
    private bool SearchKey(uint vk)
    {
        bool ctrl = KeyDown(0x11), shift = KeyDown(User32.VK_SHIFT);
        if (KeyDown(User32.VK_MENU) || KeyDown(0x5B) || KeyDown(0x5C)) return false; // Alt、Win 组合键照旧
        char ch = vk switch
        {
            >= 0x41 and <= 0x5A => (char)('a' + (vk - 0x41)),
            >= 0x30 and <= 0x39 when !shift => (char)('0' + (vk - 0x30)),
            >= 0x60 and <= 0x69 => (char)('0' + (vk - 0x60)),
            _ => '\0',
        };
        bool paste = ctrl && vk == 0x56;
        if (_searchOverlay is null && _pendingSearch is { } pending)
        {
            // 快捷键打开总览，面板还没出来：打的字先记下，面板出来时一起填进去；其余按键吞掉（不漏给下面的程序）
            switch (vk)
            {
                case VkEscape:
                    ResetSearch();
                    Controller.Close(null, HostClock.NowUs());
                    break;
                case VkBack:
                    _pendingSearch = ctrl || pending.Length == 0 ? "" : pending[..^1];
                    break;
                case VkReturn:
                    _pendingEnter = true;
                    break;
                case VkSpace when !ctrl:
                    _pendingSearch = pending + " ";
                    break;
                default:
                    if (paste) _pendingSearch = pending + PasteText();
                    else if (ch != '\0' && !ctrl) _pendingSearch = pending + ch;
                    break;
            }
            return true;
        }
        if (_searchOverlay is null)
        {
            if (!(paste || ch != '\0' && !ctrl) || !CanSearch()) return false;
            string first = paste ? PasteText() : ch.ToString();
            if (first.Length == 0) return false;
            BeginSearch(first);
            return true;
        }
        switch (vk)
        {
            case VkEscape:
                if (_searchFromHotkey)
                {
                    // 快捷键打开的：是来找程序的，Esc 直接收起总览
                    EndSearch();
                    Controller.Close(null, HostClock.NowUs());
                }
                else EndSearch();
                return true;
            case VkBack:
                _query = ctrl ? "" : _query.Length > 0 ? _query[..^(char.IsLowSurrogate(_query[^1]) && _query.Length > 1 ? 2 : 1)] : "";
                if (_query.Trim().Length == 0 && !_searchFromHotkey) EndSearch();
                else UpdateSearch();
                return true;
            case VkReturn:
                LaunchSearch(_searchSelected);
                return true;
            case VkUp:
                MoveSearch(-1);
                return true;
            case VkDown:
                MoveSearch(1);
                return true;
            case VkTab:
                MoveSearch(shift ? -1 : 1);
                return true;
            case VkSpace when !ctrl:
                _query += " ";
                UpdateSearch();
                return true;
        }
        if (paste) _query += PasteText();
        else if (ch != '\0' && !ctrl) _query += ch;
        else return true;
        UpdateSearch();
        return true;
    }

    /// <summary>测试用（UI 线程）：搜索框里换成 <paramref name="query"/>（与用户打这些字相同；空串 = 收起搜索）。</summary>
    public void TypeSearch(string query)
    {
        if (_searchOverlay is null && _pendingSearch is not null)
        {
            _pendingSearch = query;
            return;
        }
        if (query.Length == 0)
        {
            if (_searchFromHotkey && _searchOverlay is not null)
            {
                _query = "";
                UpdateSearch();
            }
            else EndSearch();
            return;
        }
        if (_searchOverlay is null)
        {
            if (!CanSearch()) return;
            BeginSearch(query);
            return;
        }
        _query = query;
        UpdateSearch();
    }

    /// <summary>
    /// UI 线程：按下了「打开搜索」的快捷键。总览没开：打开总览，覆盖层一显示就弹出搜索面板（不等展开动画播完），
    /// 这之间打的字先记下；总览开着、没在搜：直接弹出面板；正在搜（或在等面板）：收起总览。
    /// </summary>
    public void ToggleSearch()
    {
        var controller = Controller;
        long now = HostClock.NowUs();
        if (_standalone is not null)
        {
            Log.Info("搜索窗：快捷键收起");
            CloseStandalone();
            return;
        }
        if (!controller.IsVisible && OpenStandalone()) return;
        if (controller.IsVisible)
        {
            if (controller.Mode == OverviewMode.Desktop) return;
            if (_searchOverlay is not null || _pendingSearch is not null)
            {
                Log.Info("总览：快捷键收起搜索");
                EndSearch();
                controller.Close(null, now);
                return;
            }
            controller.Open(now, controller.Mode); // 正在收起：重新展开（已展开时不变）
            if (_shown && CanStartSearchEarly())
            {
                BeginSearch("", fromHotkey: true);
                return;
            }
        }
        _pendingSearch = "";
        _pendingEnter = false;
        _keys.Install(); // 不等展开完：现在就截下按键，免得前几个字漏给下面的程序
        if (!controller.IsVisible) controller.Open(now, OverviewMode.All);
        if (!controller.IsVisible)
        {
            _pendingSearch = null;
            _keys.Uninstall();
            return;
        }
        if (_shown) StartPendingSearch();
    }

    /// <summary>
    /// 单独弹出搜索面板（不进总览）：在指针所在的屏上显示透明的搜索窗，挂上按键钩子，直接进入搜索。
    /// 没有共享缩略图 / Direct2D 时画不了面板，返回 false（退回到打开总览里的搜索）。
    /// </summary>
    private unsafe bool OpenStandalone()
    {
        if (!_config().Search || _shared.Get() is not { } shared || _shared.Decor(_ui) is not { } decor) return false;
        User32.POINT p;
        double px = 0, py = 0;
        if (User32.GetCursorPos(&p))
        {
            px = p.X;
            py = p.Y;
        }
        var monitors = Monitors.Enumerate();
        if (monitors.Count == 0) return false;
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(px, py)) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        if (_searchWindows.TryGetValue(monitor.Handle, out var window) &&
            (window.Monitor.WorkArea != monitor.WorkArea || window.Monitor.DpiScale != monitor.DpiScale))
        {
            window.Dispose(); // 分辨率、缩放、任务栏位置变了：重建
            _searchWindows.Remove(monitor.Handle);
            window = null;
        }
        if (window is null)
        {
            window = new SearchWindow(_ui, monitor, (w, kind, x, y) =>
            {
                if (ReferenceEquals(w, _standalone) && _searchOverlay is { } search) SearchMouse(search, w, kind, x, y);
            });
            _searchWindows[monitor.Handle] = window;
        }
        if (!window.Prepare(shared, decor)) return false;
        _standalone = window;
        _keys.Install();
        window.ShowWindow();
        BeginSearch("", fromHotkey: true, host: window);
        return true;
    }

    /// <summary>收起单独的搜索窗（淡出，播完窗口自己藏起来），摘下按键钩子。</summary>
    private void CloseStandalone()
    {
        if (_standalone is not { } window) return;
        _standalone = null;
        window.HideSearch();
        ResetSearch();
        if (!Controller.IsVisible) _keys.Uninstall();
    }

    /// <summary>覆盖层已经显示：把等着的搜索开起来（面板与总览一起展开），补上等面板时打的字与 Enter。</summary>
    private void StartPendingSearch()
    {
        if (_pendingSearch is not { } text) return;
        bool enter = _pendingEnter;
        _pendingSearch = null;
        _pendingEnter = false;
        if (!CanStartSearchEarly())
        {
            Log.Info("总览：快捷键打开了总览，但现在不能搜索（显示桌面、拖动或没有能画面板的屏）");
            return;
        }
        BeginSearch(text, fromHotkey: true);
        if (enter) LaunchSearch(_searchSelected);
    }

    /// <summary>快捷键打开的搜索不等展开动画播完：覆盖层显示着、不是显示桌面、没在拖动 / 弹托盘菜单 / 等程序窗口就行。</summary>
    private bool CanStartSearchEarly() =>
        _config().Search && _shown && Controller.IsVisible && Controller.Mode != OverviewMode.Desktop && _menu is null && _launch is null &&
        !_dragging && _overlays.Any(o => o.IsShown && o.SearchAvailable);

    /// <summary>能开始搜索：设置里开着、总览已经展开好（不在显示桌面、拖窗口、弹托盘菜单、等程序窗口），有能画面板的屏。</summary>
    private bool CanSearch() =>
        _config().Search && _shown && Controller.IsSettledOpen && Controller.Mode != OverviewMode.Desktop && _menu is null && _launch is null &&
        !_dragging && _overlays.Any(o => o.IsShown && o.SearchAvailable);

    /// <summary>剪贴板里的文字：第一行，最多 40 个字。</summary>
    private static string PasteText()
    {
        string text = SystemInfo.ClipboardText() ?? "";
        int line = text.IndexOfAny(['\r', '\n']);
        if (line >= 0) text = text[..line];
        text = text.Trim();
        return text.Length > 40 ? text[..40] : text;
    }

    /// <summary>开始搜索：面板出现在指针所在的那块屏（窗口选中框、栏里的高亮先撤掉）。</summary>
    private unsafe void BeginSearch(string text, bool fromHotkey = false, ISearchHost? host = null)
    {
        _searchFromHotkey = fromHotkey;
        if (host is not null)
        {
            _searchOverlay = host;
            _query = text;
            Log.Info($"搜索窗：快捷键打开搜索（开始菜单里 {_launcher.Entries.Count} 个可搜的程序）");
            UpdateSearch();
            return;
        }
        User32.POINT p;
        double px = 0, py = 0;
        if (User32.GetCursorPos(&p))
        {
            px = p.X;
            py = p.Y;
        }
        _searchOverlay = _overlays.FirstOrDefault(o => o.IsShown && o.SearchAvailable && o.Monitor.Bounds.Contains(px, py))
                         ?? _overlays.First(o => o.IsShown && o.SearchAvailable);
        _query = text;
        SetDockHover(-1);
        Select(-1);
        Log.Info($"总览：开始搜索程序（开始菜单里 {_launcher.Entries.Count} 个可搜的程序）");
        UpdateSearch();
    }

    private void UpdateSearch()
    {
        if (_searchOverlay is not { } o) return;
        _launcher.SetExtraApps(_config().SearchExtraApps);
        long t0 = HostClock.NowUs();
        _results = _query.Trim().Length == 0
            ? AppSearch.Frequent(_launcher.Entries, _launcher.Usage) // 还没输入（快捷键打开的）：列出打开过的，最近的在上面
            : AppSearch.Find(_query, _launcher.Entries, _launcher.Usage, DateTime.UtcNow);
        _searchSelected = _results.Count > 0 ? 0 : -1;
        long t1 = HostClock.NowUs();
        o.ShowSearch(_query, _results, _searchSelected, _launcher.IconOf);
        if (t1 - t0 > 20_000) Log.Info($"总览：搜索「{_query}」匹配用了 {(t1 - t0) / 1000.0:F1}ms");
    }

    private void MoveSearch(int step)
    {
        if (_searchOverlay is not { } o || _results.Count == 0) return;
        _searchSelected = (_searchSelected + step + _results.Count) % _results.Count;
        o.SelectSearch(_searchSelected);
    }

    /// <summary>收起搜索面板（淡出），回到窗口。</summary>
    private void EndSearch()
    {
        if (_standalone is not null)
        {
            CloseStandalone(); // 单独的搜索窗：Esc、点外面、打开了程序，都是整个收起
            return;
        }
        _searchOverlay?.HideSearch();
        ResetSearch();
    }

    private void ResetSearch()
    {
        _pendingSearch = null;
        _pendingEnter = false;
        _searchFromHotkey = false;
        _searchOverlay = null;
        _query = "";
        _results = [];
        _searchSelected = -1;
        _searchPress = false;
    }

    /// <summary>打开第 <paramref name="index"/> 条结果：记一次使用、启动它，总览收起（新程序的窗口随后出现在最上面）。</summary>
    private void LaunchSearch(int index)
    {
        if (index < 0 || index >= _results.Count) return;
        var entry = _results[index].Entry;
        string query = _query;
        Log.Info($"总览：从搜索打开「{entry.Name}」（输入「{query}」，第 {index + 1} 条）");
        _ = _launcher.Launch(entry).ContinueWith(t =>
        {
            string? error = t.IsCompletedSuccessfully ? t.Result : t.Exception?.InnerException?.Message ?? "没有执行";
            if (error is not null) Log.Warn($"总览：打开「{entry.Name}」失败：{error}");
        }, TaskScheduler.Default);
        EndSearch();
        Controller.Close(null, HostClock.NowUs());
    }

    /// <summary>
    /// 在搜时的鼠标：指针移到哪一行就选中哪一行，点结果打开它；在面板外按下又松开 = 回到窗口。其余（拖窗口、点窗口）都不响应。
    /// </summary>
    private void SearchMouse(ISearchHost search, object overlay, OverlayMouse kind, double x, double y)
    {
        switch (kind)
        {
            case OverlayMouse.Move:
                if (!ReferenceEquals(overlay, search)) return;
                int hover = search.SearchHit(x, y);
                if (hover >= 0 && hover != _searchSelected)
                {
                    _searchSelected = hover;
                    search.SelectSearch(hover);
                }
                break;
            case OverlayMouse.Down:
                _searchPress = ReferenceEquals(overlay, search) && search.SearchContains(x, y);
                break;
            case OverlayMouse.Up:
                int hit = ReferenceEquals(overlay, search) ? search.SearchHit(x, y) : -1;
                if (hit >= 0) LaunchSearch(hit);
                else if (!_searchPress) EndSearch();
                _searchPress = false;
                break;
        }
    }

    private bool OnKey(uint vk)
    {
        if (!Controller.IsVisible && _standalone is null) return false; // 快捷键提前挂上了钩子、总览却没打开：按键一律放过
        if (IsSearchHotkey?.Invoke(vk) == true) return false;
        if (_standalone is not null) return SearchKey(vk); // 单独的搜索窗：按键只给搜索（Alt、Win 组合键照旧交给系统）
        if (_menu is { Opened: true } menu)
        {
            // 托盘菜单开着：按键给菜单（方向键选、Enter 选中）。自绘菜单收不到 Esc（见 TrayMenu.Dismiss），替它关
            if (vk != VkEscape || menu.Win32Menu) return false;
            menu.Dismiss();
            return true;
        }
        if (SearchKey(vk)) return true;
        if (_dockHovered >= 0 && _dockOverlay?.Dock is { } dock && DockKey(vk, dock)) return true;
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
                // 最下面一行再往下：进入后台程序栏
                if (direction == NavigationDirection.Down && EnterDock()) return true;
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
        if (index >= 0 && _dockHovered >= 0) SetDockHover(-1);
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

    // ---------- 后台程序栏（UI 线程） ----------

    /// <summary>放后台程序栏的覆盖层：任务栏（托盘）所在那块屏，找不到时用主屏。</summary>
    private unsafe OverlayWindow? DockOverlay()
    {
        nint tray = User32.FindWindowW("Shell_TrayWnd", null);
        User32.RECT r;
        if (tray != 0 && User32.GetWindowRect(tray, &r))
        {
            double cx = (r.Left + r.Right) / 2.0, cy = (r.Top + r.Bottom) / 2.0;
            if (_overlays.FirstOrDefault(o => o.Monitor.Bounds.Contains(cx, cy)) is { } found) return found;
        }
        return _overlays.FirstOrDefault(o => o.Monitor.IsPrimary) ?? _overlays.FirstOrDefault();
    }

    /// <summary>这块屏的窗口区域底部要给后台程序栏让出的高度（没有栏为 0）。</summary>
    private double DockInset(MonitorInfo m) =>
        _dockOverlay is { } o && o.Monitor.Index == m.Index ? TrayDockGeometry.LayoutInset(m.WorkArea, m.DpiScale) : 0;

    /// <summary>
    /// 预热时就先按上一次列出的托盘程序把栏摆好（跟着总览的展开进度从任务栏后面升起，第一帧就在），
    /// 同时在后台线程重新列一遍（毫秒级，但要逐个给资源管理器发消息，不放在 UI 线程上）；新列表有变化再换上。
    /// 先把这次总览里的窗口记成「见过」：之后它们关到托盘，就知道是它的主窗口。
    /// </summary>
    private void StartDock(IReadOnlyList<OverviewWindow> windows)
    {
        _dockApps = [];
        _dockHovered = -1;
        int session = ++_dockSession;
        if (_dockOverlay is null) return;
        TrayCatalog.MarkSeen(windows.Select(w => w.Id));
        _apps.Refresh();
        _dockApps = Visible(_lastDockApps);
        if (_dockApps.Count > 0) ShowDock(animate: false);
        _watchApp = null;
        _ui.PostDelayed(DockWatchMs, () => WatchDock(session));
        _ = Task.Run(() => TrayCatalog.Snapshot(_apps)).ContinueWith(t => _ui.Post(() =>
        {
            if (session != _dockSession || !_prepared || _dockOverlay is null) return;
            if (!t.IsCompletedSuccessfully)
            {
                Log.Warn($"后台程序栏：列出托盘程序失败：{t.Exception?.InnerException?.Message}");
                return;
            }
            _lastDockApps = t.Result;
            var fresh = Visible(t.Result);
            var stats = TrayCatalog.LastStats;
            Log.Info($"后台程序栏：托盘里 {stats.Live} 个图标，栏里 {fresh.Count} 个（{string.Join("、", fresh.Select(a => a.Name))}），耗时 {stats.Ms:F1}ms");
            bool same = fresh.Count == _dockApps.Count && fresh.Zip(_dockApps).All(p => p.First.Key == p.Second.Key && p.First.Name == p.Second.Name);
            _dockApps = fresh;
            if (same && fresh.Count > 0) return; // 已经按它摆好了：只换数据（宿主窗口等标识可能更新了）
            if (Controller.Mode == OverviewMode.All) ShowDock(animate: Controller.IsSettledOpen);
        }), TaskScheduler.Default);
    }

    private const int DockWatchMs = 400, DockWatchFastMs = 80;

    /// <summary>
    /// 总览开着时看栏里的程序还在不在（UI 线程，每 0.4 秒）：进程退出了就从栏里去掉（淡出、其余收拢）。
    /// 刚关了菜单的程序 5 秒内每 80 毫秒还看它的托盘图标——选了「退出」时图标先没、进程后退，看图标反应快；
    /// 只去掉图标不退出的程序也一样去掉。
    /// </summary>
    private void WatchDock(int session)
    {
        if (session != _dockSession || !_prepared) return;
        long now = Environment.TickCount64;
        bool fast = _watchApp is not null && now < _watchUntilMs;
        if (_shown && _menu is null && Controller.Mode == OverviewMode.All && _dockOverlay?.Dock is { } dock)
        {
            for (int i = 0; i < Math.Min(dock.Count, _dockApps.Count); i++)
            {
                var app = _dockApps[i];
                bool watched = fast && _watchApp!.Key == app.Key;
                if (TrayCatalog.IsRunning(app) && (!watched || TrayCatalog.TryGetRect(app, out _))) continue;
                RemoveDockApp(i);
                break;
            }
        }
        _ui.PostDelayed(fast ? DockWatchFastMs : DockWatchMs, () => WatchDock(session));
    }

    /// <summary>栏里第 <paramref name="index"/> 个程序没了：去掉它（动画见 <see cref="OverlayWindow.RemoveDockApp"/>）。</summary>
    private void RemoveDockApp(int index)
    {
        var app = _dockApps[index];
        _dockApps = _dockApps.Where((_, k) => k != index).ToList();
        _lastDockApps = _lastDockApps.Where(a => a.Key != app.Key).ToList();
        if (_watchApp?.Key == app.Key) _watchApp = null;
        Log.Info($"后台程序栏：「{app.Name}」{(TrayCatalog.IsRunning(app) ? "的托盘图标没了" : "退出了")}，从栏里去掉");
        // 菜单里选的是「退出」：不等窗口了，留在总览里看着它从栏里消失
        if (_launch is { FromMenu: true } l && l.App.Key == app.Key) EndLaunch();
        SetDockHover(-1);
        if (_dockOverlay is not { } o) return;
        if (_dockApps.Count == 0)
        {
            o.RetractDock();
            return;
        }
        o.RemoveDockApp(index, TrayDockGeometry.Compute(o.Monitor.WorkArea, o.Monitor.DpiScale, _dockApps.Count), _dockApps);
    }

    /// <summary>去掉用户在设置里选了不显示的程序。</summary>
    private IReadOnlyList<TrayApp> Visible(IReadOnlyList<TrayApp> apps)
    {
        var hidden = _config().HiddenTrayApps;
        return hidden.Length == 0 ? apps : apps.Where(a => !hidden.Contains(a.Exe, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// 按本次会话列出的托盘程序显示栏。<paramref name="animate"/>：总览已经展开好了（列表晚到、从应用窗口视图回到总览），
    /// 栏用弹簧从任务栏后面升起；否则直接跟总览的展开进度走。
    /// </summary>
    private void ShowDock(bool animate)
    {
        if (_dockOverlay is not { } o) return;
        SetDockHover(-1);
        if (_dockApps.Count == 0)
        {
            o.RetractDock();
            return;
        }
        var geometry = TrayDockGeometry.Compute(o.Monitor.WorkArea, o.Monitor.DpiScale, _dockApps.Count);
        o.PrepareDock(geometry, _dockApps, animate);
        if (geometry.Count < _dockApps.Count) Log.Info($"后台程序栏：放不下，只显示前 {geometry.Count} 个");
    }

    /// <summary>总览开着时撤走栏（换成应用窗口视图）：沉回任务栏后面。</summary>
    private void RetractDock()
    {
        SetDockHover(-1);
        _dockOverlay?.RetractDock();
    }

    private void SetDockHover(int index)
    {
        _dockHovered = index;
        _dockOverlay?.SetDockHover(index);
    }

    /// <summary>在窗口里按 ↓ 到了最下面一行（或总览里没有窗口）：选中栏里横向最近的程序。</summary>
    private bool EnterDock()
    {
        if (_dockOverlay?.Dock is not { } dock) return false;
        var scene = Controller.Scene;
        int target;
        if (scene.PresentCount == 0) target = 0;
        else
        {
            if (_hoveredId is null) return false; // 第一下先选中窗口
            int from = Selected(scene);
            if (scene.Navigate(from, NavigationDirection.Down) != from) return false;
            target = dock.Nearest(scene.Tile(from, 0).CenterX);
        }
        if (target < 0) return false;
        Select(-1);
        SetDockHover(target);
        return true;
    }

    /// <summary>栏里有选中项时的按键：←/→ 移动，↑ 回到窗口，Enter 点，菜单键 / Shift+F10 弹出托盘菜单。返回 false 的交给总览原来的处理。</summary>
    private bool DockKey(uint vk, TrayDockGeometry dock)
    {
        switch (vk)
        {
            case VkLeft:
                SetDockHover(Math.Max(0, _dockHovered - 1));
                return true;
            case VkRight:
                SetDockHover(Math.Min(dock.Count - 1, _dockHovered + 1));
                return true;
            case VkUp:
                LeaveDock(dock.Tiles[Math.Min(_dockHovered, dock.Count - 1)].CenterX);
                return true;
            case VkDown:
                return true;
            case VkReturn:
                ActivateDock(_dockHovered, menu: false);
                return true;
            case VkApps:
            case VkF10 when User32.GetAsyncKeyState(User32.VK_SHIFT) < 0:
                ActivateDock(_dockHovered, menu: true);
                return true;
            case VkTab:
                SetDockHover(-1);
                return false; // 照常在窗口之间循环
            default:
                return false;
        }
    }

    /// <summary>从栏里按 ↑：选中最下面一行里横向离 <paramref name="x"/> 最近的窗口。</summary>
    private void LeaveDock(double x)
    {
        var scene = Controller.Scene;
        int best = -1;
        double bestScore = double.MaxValue, bottom = double.MinValue;
        for (int i = 0; i < scene.Count; i++)
            if (scene.IsPresent(i)) bottom = Math.Max(bottom, scene.Tile(i, 0).Bottom);
        for (int i = 0; i < scene.Count; i++)
        {
            if (!scene.IsPresent(i)) continue;
            var t = scene.Tile(i, 0);
            double score = Math.Abs(t.CenterX - x) + 2 * (bottom - t.Bottom);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        SetDockHover(-1);
        if (best >= 0) Select(best);
    }

    /// <summary>
    /// 点了栏里的第 <paramref name="index"/> 个程序。点击在专门的线程上经过资源管理器完成。
    /// <list type="bullet">
    /// <item>右键：弹出它的托盘菜单，总览不收，菜单出现在右键处（<paramref name="at"/>；键盘弹出时在图标上面），见 <see cref="BeginMenu"/>。</item>
    /// <item>
    /// 左键：左键单击它的托盘图标。总览先不收，图标一下一下地弹跳；程序把窗口打开了（在覆盖层下面，看不到它突然出现），
    /// 总览再收起，那个窗口同时从图标飞到它的真实位置（见 <see cref="FlyIn"/>）。等不到窗口（程序弹了菜单、或什么也没做）就照常收起。
    /// 少数程序只认双击：左键 0.6 秒后还没出窗口、而它有见过的主窗口，直接把那个窗口显示出来。
    /// </item>
    /// </list>
    /// </summary>
    private void ActivateDock(int index, bool menu, (double X, double Y)? at = null)
    {
        if (index < 0 || index >= _dockApps.Count || !Controller.IsSettledOpen || _menu is not null || _trayAction is { IsCompleted: false }) return;
        if (_launch is { Quiet: true }) EndLaunch(); // 上一个菜单关掉后还在悄悄等窗口：用户又点了别的，不等了
        if (_launch is not null) return;
        var app = _dockApps[index];
        string what = menu ? "弹出托盘菜单" : "左键";
        Log.Info($"后台程序栏：{what}「{app.Name}」");
        SetDockHover(-1);
        Launch? launch = null;
        TrayMenu? tray = menu ? BeginMenu(app, index, at) : null;
        var click = _trayInvoker.Click(app, menu, tray?.AnchorX ?? 0, tray?.AnchorY ?? 0);
        _trayAction = click;
        if (!menu)
        {
            launch = new Launch(app, index, TrayCatalog.RelatedProcesses(app));
            _launch = launch;
            _dockOverlay?.SetDockLaunching(index);
            _ui.PostDelayed(16, PollLaunch);
        }
        _ = click.ContinueWith(t => _ui.Post(() =>
        {
            string? error = t.IsCompletedSuccessfully ? t.Result : t.Exception?.InnerException?.Message ?? "没有执行";
            if (error is not null) Log.Warn($"后台程序栏：{what}「{app.Name}」失败：{error}");
            else Log.Info($"后台程序栏：{what}「{app.Name}」：{_trayInvoker.LastTiming}");
            if (tray is not null)
            {
                tray.ClickedMs = Environment.TickCount64;
                if (error is not null && _menu == tray) EndMenu(); // 没弹出来：留在总览里
                return;
            }
            if (launch is null) return;
            launch.ClickedMs = Environment.TickCount64;
            launch.Failed = error is not null;
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// 测试用（UI 线程）：左键栏里的第 <paramref name="index"/> 个程序，与用户点它相同（总览要已经展开）；
    /// 负数 -(n+1) = 弹出第 n 个的托盘菜单（与键盘弹出相同）。
    /// </summary>
    public void ClickDock(int index)
    {
        if (index >= 0) ActivateDock(index, menu: false);
        else ActivateDock(-index - 1, menu: true);
    }

    /// <summary>左键之后每帧看一次程序的窗口出来没有（UI 线程）。</summary>
    private unsafe void PollLaunch()
    {
        if (_launch is not { } l) return;
        if (!_shown || !Controller.IsSettledOpen)
        {
            EndLaunch(); // 用户自己收起了总览（Esc、手势、点了别的窗口）：不等了
            return;
        }
        long now = Environment.TickCount64;
        nint window = TrayCatalog.ShownWindow(l.Processes);
        nint main = (nint)l.App.MainWindow;
        if (window == 0 && !l.Restored && !l.FromMenu && main != 0 && l.ClickedMs >= 0 && (l.Failed || now - l.ClickedMs >= RestoreAfterMs) &&
            User32.IsWindow(main) && !User32.IsWindowVisible(main))
        {
            // 点击失败，或程序只认双击：直接显示见过的主窗口（下一帧照常等它出来、飞进去）
            User32.ShowWindow(main, 5 /* SW_SHOW */);
            l.Restored = true;
            Log.Info($"后台程序栏：「{l.App.Name}」托盘左键后没有出窗口，直接显示它的主窗口 0x{main:X}");
        }
        if (window != 0)
        {
            FlyIn(l, window);
            return;
        }
        long deadline = l.ClickedMs < 0 ? l.StartedMs + LaunchMaxMs : Math.Min(l.StartedMs + LaunchMaxMs, l.ClickedMs + (l.Failed && !l.Restored ? 0 : LaunchWaitMs));
        if (now >= deadline)
        {
            if (l.Quiet)
            {
                EndLaunch(); // 关掉菜单后没出窗口：留在总览里
                return;
            }
            Log.Info($"后台程序栏：「{l.App.Name}」没有打开窗口（{now - l.StartedMs}ms），收起总览");
            EndLaunch();
            Controller.Close(null, HostClock.NowUs());
            return;
        }
        _ui.PostDelayed(16, PollLaunch);
    }

    /// <summary>
    /// 程序的窗口出来了：总览收起，这个窗口同时从栏里它的图标飞到真实位置——起点是图标处一个窗口宽高比的小矩形，
    /// 与其他窗口飞回原位、栏沉下去同时开始，用自己的弹簧（周期 0.42 秒、不回弹，与 macOS 打开窗口的速度相当），落定时缩略图与真实窗口重合再撤层。
    /// 窗口出在别的屏上（或共享缩略图不可用）时不飞，照常收起。
    /// </summary>
    private void FlyIn(Launch l, nint window)
    {
        EndLaunch();
        _launched = window;
        _flightStartedMs = Environment.TickCount64;
        var to = WindowCatalog.VisibleBounds(window);
        bool flying = false;
        if (_dockOverlay is { Dock: { } dock } o && l.Index < dock.Count &&
            _overlays.FirstOrDefault(x => x.Monitor.WorkArea.Contains(to.CenterX, to.CenterY)) == o)
        {
            var icon = dock.Icons[l.Index];
            double h = icon.Height * 1.3, w = h * to.Width / Math.Max(1, to.Height);
            if (w > icon.Width * 2.4)
            {
                w = icon.Width * 2.4;
                h = w * to.Height / Math.Max(1, to.Width);
            }
            var from = new RectD(icon.CenterX - w / 2, icon.CenterY - h / 2, w, h);
            // 注册缩略图要几毫秒（期间渲染线程等着）：飞行与收起的起点取注册完之后，第一帧不会一下跳出一截
            flying = o.BeginFlight(window, from, to, HostClock.NowUs());
        }
        long now = HostClock.NowUs();
        Log.Info($"后台程序栏：「{l.App.Name}」的窗口出来了（{Environment.TickCount64 - l.StartedMs}ms）{(flying ? "，从图标飞到原位" : "")}");
        Controller.Close(null, now);
    }

    /// <summary>
    /// 右键栏里的程序：等它弹出托盘菜单（在覆盖层下面），把菜单挪到 <paramref name="at"/>（右键处；键盘弹出时为 null，在图标上面居中），
    /// 覆盖层画它的缩略图淡入、再开洞露出真实菜单（见 <see cref="TrayMenu"/>、<see cref="OverlayWindow.ShowMenu"/>）。总览一直开着：
    /// <list type="bullet">
    /// <item>选了菜单项：和左键一样等窗口出来、飞进去，等不到就收起总览。</item>
    /// <item>没选就关了（Esc、点了外面）：留在总览里（有窗口出来照样飞进去）。</item>
    /// <item>等不到菜单（程序没反应、或弹的东西认不出来）：收起总览，让它露出来。</item>
    /// </list>
    /// </summary>
    private TrayMenu BeginMenu(TrayApp app, int index, (double X, double Y)? at)
    {
        double x, y;
        if (at is { } p) (x, y) = p;
        else
        {
            var tile = _dockOverlay?.Dock is { } dock && index < dock.Count ? dock.Tiles[index] : default;
            x = tile.CenterX;
            y = tile.Top - 6 * (_dockOverlay?.Monitor.DpiScale ?? 1);
        }
        var area = _dockOverlay?.Monitor.WorkArea ?? default;
        var menu = new TrayMenu(app, index, TrayCatalog.RelatedProcesses(app), x, y, centered: at is null, area);
        menu.Changed = () =>
        {
            if (_menu == menu) SyncMenu(menu);
        };
        _menu = menu;
        _ui.PostDelayed(16, PollMenu);
        return menu;
    }

    /// <summary>菜单开着时每帧看一次（UI 线程）：更新位置、开洞，看它关了没有。</summary>
    private void PollMenu()
    {
        if (_menu is not { } m) return;
        if (!_shown || !Controller.IsSettledOpen)
        {
            EndMenu(); // 用户收起了总览：菜单留在原处
            return;
        }
        long now = Environment.TickCount64;
        if (!m.Opened)
        {
            // 不发菜单事件的程序：前台成了它的一个没有标题栏的小弹窗
            nint fg = User32.GetForegroundWindow();
            if (m.LooksLikeMenu(fg) && m.Add(fg)) Log.Info($"后台程序栏：「{m.App.Name}」的菜单没有发事件，按前台弹窗 {WindowCatalog.ClassName(fg)} 处理");
            else if (m.ClickedMs >= 0 && now - m.ClickedMs > MenuWaitMs)
            {
                Log.Info($"后台程序栏：「{m.App.Name}」右键后 {now - m.StartedMs}ms 没有弹出菜单，收起总览");
                EndMenu();
                Controller.Close(null, HostClock.NowUs());
                return;
            }
        }
        m.Refresh();
        SyncMenu(m);
        if (m.ClosedMs >= 0 && now - m.ClosedMs >= 60) // 选中菜单项的事件可能比菜单关掉晚一点到
        {
            FinishMenu(m);
            return;
        }
        _ui.PostDelayed(16, PollMenu);
    }

    /// <summary>根菜单第一次出现时挪到锚点；把各级菜单交给覆盖层画、开洞。</summary>
    private void SyncMenu(TrayMenu m)
    {
        if (_dockOverlay is not { } o) return;
        if (!m.Placed && m.Popups.Count > 0)
        {
            m.PlaceRoot();
            m.Placed = true;
            var r = m.Popups[0].Rect;
            Log.Info($"后台程序栏：「{m.App.Name}」的菜单弹出来了（{Environment.TickCount64 - m.StartedMs}ms，{WindowCatalog.ClassName(m.Popups[0].Window)}），挪到 [{r.Left:F0},{r.Top:F0} {r.Width:F0}x{r.Height:F0}]");
        }
        long now = HostClock.NowUs();
        var popups = m.Popups.Select(p => (p.Window, p.Rect, p.ShownUs, now - p.ShownUs >= MenuHoleDelayUs)).ToList();
        o.ShowMenu(popups, m.CornerRadiusDip * o.Monitor.DpiScale);
    }

    /// <summary>菜单关了（UI 线程）：选了菜单项就像左键一样等窗口出来；没选就留在总览里（有窗口出来照样飞进去）。</summary>
    private void FinishMenu(TrayMenu m)
    {
        EndMenu();
        Log.Info($"后台程序栏：「{m.App.Name}」的菜单关了（{(m.Invoked ? "选了菜单项" : "没选")}）");
        _watchApp = m.App;
        _watchUntilMs = Environment.TickCount64 + 5000;
        _launch = new Launch(m.App, m.Index, m.Processes) { ClickedMs = Environment.TickCount64, FromMenu = true, Quiet = !m.Invoked };
        _ui.PostDelayed(16, PollLaunch);
    }

    private void EndMenu()
    {
        if (_menu is not { } m) return;
        _menu = null;
        m.Dispose();
        _dockOverlay?.ShowMenu([], 0);
    }

    private void EndLaunch()
    {
        _launch = null;
        _dockOverlay?.SetDockLaunching(-1);
    }

    /// <summary>
    /// 设置里「栏里显示哪些程序」的列表：此刻托盘里的全部程序（有可见窗口的也算），加上已经选了不显示、但此刻没在运行的程序。
    /// 任意线程调用（要给资源管理器发消息，几十毫秒）。
    /// </summary>
    public IReadOnlyList<TrayAppChoice> TrayAppChoices()
    {
        var list = TrayCatalog.Snapshot(_apps, includeVisible: true)
            .GroupBy(a => a.Exe, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TrayAppChoice(g.Key, g.First().Name))
            .ToList();
        foreach (string exe in _config().HiddenTrayApps)
            if (!list.Any(c => string.Equals(c.Exe, exe, StringComparison.OrdinalIgnoreCase)))
                list.Add(new TrayAppChoice(exe, Path.GetFileNameWithoutExtension(exe), Running: false));
        return list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>后台程序栏最近一次点击的结果（诊断用；null = 还没点过）。</summary>
    public string? LastTrayClick => _trayInvoker.LastResult;

    /// <summary>程序索引（诊断用）。</summary>
    public AppCatalog Apps => _apps;

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
        double labelOpacity = controller.Mode == OverviewMode.Desktop ? 0 : frame.LabelOpacity;
        double corner = controller.Mode == OverviewMode.Desktop ? -1 : frame.Progress; // 显示桌面：窗口原样滑出屏幕
        foreach (var o in _renderOverlays)
        {
            if (!o.IsShown) continue;
            int hr = paged
                ? o.Render(nowUs, rects.AsSpan(0, count), opacity.AsSpan(0, count), active.AsSpan(0, count), wallpaper, frame, labelOpacity, corner, buffers!.Rects, buffers.Opacity, buffers.Active)
                : o.Render(nowUs, rects.AsSpan(0, count), opacity.AsSpan(0, count), active.AsSpan(0, count), wallpaper, frame, labelOpacity, corner);
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
