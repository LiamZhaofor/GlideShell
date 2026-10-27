using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Gestures;
using GlideShell.Core.Motion;

namespace GlideShell.Core.Overview;

/// <summary>
/// 总览的宿主（覆盖层窗口、窗口枚举、缩略图）。控制器从输入线程和渲染线程调用这些方法，
/// 实现必须立刻返回：真正的工作投递到 UI 线程去做。
/// </summary>
public interface IOverviewHost
{
    /// <summary>后台准备场景：枚举窗口、布局、预注册缩略图（不可见）。完成后调用 <see cref="OverviewController.SetScene"/>。</summary>
    void Prewarm();

    /// <summary>预热作废（手势没有变成总览）：释放预热资源。</summary>
    void CancelPrewarm();

    /// <summary>显示覆盖层（不抢焦点），渲染线程开始按帧采样。</summary>
    void Show();

    /// <summary>展开落定：覆盖层接管键盘与鼠标。</summary>
    void Activate();

    /// <summary>
    /// 收起已经确定（松手决定收起、或点击 / Enter）：宿主可以趁收起动画还在播，提前把 <paramref name="focusWindowId"/> 切到前台，
    /// 动画落定时真实桌面就已经是最终的样子，撤层不用再等。动画中途被新手势接住也没关系（窗口已经在前台）。
    /// </summary>
    void Closing(long? focusWindowId);

    /// <summary>收起落定：隐藏覆盖层。<paramref name="focusWindowId"/> 非空时把它置前（最小化的先还原），否则还原原来的前台窗口。</summary>
    void Hide(long? focusWindowId);

    /// <summary>
    /// 点了桌面条上的另一个桌面：立刻在覆盖层下面切换真实桌面。覆盖层随后播放「放大进入那个桌面」，
    /// 落定时照常调用 <see cref="Hide"/>（focusWindowId 为 null），宿主负责把焦点交给新桌面最上层的窗口。
    /// </summary>
    void SwitchDesktop(int desktopIndex);

    /// <summary>
    /// 总览里四指横滑决定换到第 <paramref name="desktopIndex"/> 个桌面那一页（松手就决定，翻页动画还在播）：
    /// 宿主立刻在覆盖层下面切换真实桌面，总览留在新桌面上（macOS 调度中心）。
    /// </summary>
    void PageChanged(int desktopIndex);
}

/// <summary>总览的两种形态。</summary>
public enum OverviewMode
{
    /// <summary>窗口总览：当前桌面的全部窗口（macOS 调度中心）。</summary>
    All,

    /// <summary>应用窗口视图：只平铺当前程序的窗口，其余窗口原地淡出（macOS App Exposé）。</summary>
    App,

    /// <summary>
    /// 显示桌面：窗口跟手滑出屏幕（进度 1 = 全部推出去），落定后宿主把它们真正最小化并撤层；
    /// 桌面已显示时从进度 1 开始往回拉，落到 0 时宿主把它们还原。背景不变暗，没有标题与桌面条。
    /// </summary>
    Desktop,
}

/// <summary>渲染线程每帧取到的总览状态。</summary>
/// <param name="BarExpansion">桌面条展开程度（0 收起 ~ 1 展开）。</param>
/// <param name="ZoomDesktop">点击后正在放大进入的桌面（-1 = 没有）。</param>
/// <param name="Zoom">放大进入的进度（0 ~ 1）：目标桌面缩略图从桌面条放大到全屏，其余内容随之淡出。</param>
/// <param name="PagePosition">翻页位置（桌面下标，可带小数）：第 k 页横向偏移 (k − 位置) × (屏宽 + 间隙)。</param>
/// <param name="Relayout">当前页重排进度（窗口移到别的桌面后其余窗口补位；1 = 没有在重排）。</param>
/// <param name="DragIndex">正在拖动 / 放下的窗口（当前页场景下标，-1 = 没有）。</param>
/// <param name="DragRect">它这一帧的矩形（<paramref name="DragReturn"/> ≥ 0 时是飞回的起点）。</param>
/// <param name="DragReturn">没放到别的桌面、正在飞回的进度（0 ~ 1）：从 <paramref name="DragRect"/> 插值到它本来的位置；-1 = 不是在飞回。</param>
public readonly record struct OverviewFrame(
    double Progress,
    double BackdropOpacity,
    double LabelOpacity,
    TransitionPhase Phase,
    bool Visible,
    long? EmphasizedWindow,
    double BarExpansion = 0,
    int ZoomDesktop = -1,
    double Zoom = 0,
    double PagePosition = 0,
    double Relayout = 1,
    int DragIndex = -1,
    RectD DragRect = default,
    double DragOpacity = 1,
    double DragReturn = -1)
{
    /// <summary>有窗口在拖动或放下途中（标题、悬停框不画）。</summary>
    public bool Dragging => DragIndex >= 0;
}

/// <summary>
/// 窗口总览：手势 → 连续进度（0 = 收起，1 = 展开）→ 画面。
/// <list type="bullet">
/// <item>朝展开方向（上 / 右）开始的手势才从收起态接管；展开态下反向滑动跟手收起，同向滑动是橡皮筋越界。</item>
/// <item>手指在板上只跟随；松手才按投影落点决定展开或收起，并以松手速度驱动弹簧。动画中可随时被新手势接管。</item>
/// <item>收起时指针下的窗口逐渐放大，松手后它成为前台（macOS 行为）。</item>
/// <item>桌面条：指针移到顶部展开；点另一个桌面时真实桌面立刻切换，覆盖层把那个桌面的缩略图放大到全屏后撤掉（macOS 调度中心）。</item>
/// <item>总览里四指横滑（<see cref="DesktopSwipe"/>）：留在总览里，窗口区域 1:1 跟手横向滑到相邻桌面那一页（各桌面各排一页），
/// 松手按投影决定停在哪一页，首尾橡皮筋；换页时真实桌面立刻切换（macOS 调度中心）。</item>
/// <item>收起态下沿收起方向滑（三指下滑）：应用窗口视图（<see cref="OverviewMode.App"/>），只平铺当前程序的窗口；
/// 它的展开方向是下、收起是上（macOS App Exposé）。宿主在 <see cref="IOverviewHost.Show"/> 里按 <see cref="Mode"/> 准备场景。</item>
/// <item>拖动窗口（<see cref="BeginDrag"/>）：窗口跟着指针走，移到桌面条上缩成缩略图大小；放到别的桌面上就飞进那个缩略图，
/// 其余窗口补位（<see cref="OverviewScene.Without"/>）；放在别处就飞回原位（macOS 调度中心）。</item>
/// </list>
/// 线程：IGestureTarget 方法在输入线程；<see cref="Sample"/> / <see cref="WindowRects"/> 在渲染线程；
/// <see cref="SetScene"/>、<see cref="SetHovered"/>、<see cref="Toggle"/> 等在 UI 线程。宿主回调都在锁外发出。
/// </summary>
public sealed class OverviewController : IGestureTarget
{
    private const double SettleEpsilon = 1e-3;

    private readonly IOverviewHost _host;
    private readonly Func<OverviewConfig> _config;
    private readonly object _gate = new();
    private readonly InteractiveTransition _transition;
    private readonly InteractiveTransition _bar = new(0, 1, 0) { Spring = new(0.28, 1.0) };
    private readonly InteractiveTransition _zoom = new(0, 1, 0) { Spring = new(0.32, 1.0) };
    private volatile OverviewScene _scene = OverviewScene.Empty;
    private volatile DesktopBarState _desktops = DesktopBarState.Empty;
    private int _zoomDesktop = -1;
    private readonly InteractiveTransition _pager = new(0, 0, 0) { OverscrollBelow = 0.12, OverscrollAbove = 0.12 };
    private volatile IReadOnlyList<OverviewScene> _pages = []; // 每个桌面一页（下标 = 桌面下标）；没有时只有 _scene
    private volatile int _page = -1; // 当前页（= 当前桌面）
    private bool _pagerTracking;
    private bool _pagerIgnoring;
    private int _pageAtBegin;
    private double _pageTravelMm = 55;
    private readonly InteractiveTransition _relayout = new(0, 1, 1) { Spring = new(0.32, 1.0) };
    private readonly InteractiveTransition _dragShrink = new(0, 1, 0) { Spring = new(0.25, 1.0) };
    private readonly InteractiveTransition _drop = new(0, 1, 0) { Spring = new(0.3, 1.0) };
    private DragStage _drag;
    private int _dragIndex = -1;
    private double _dragX, _dragY; // 指针位置
    private double _grabX, _grabY; // 按下处在窗口里的相对位置（0 ~ 1）
    private double _dragW, _dragH, _smallW, _smallH; // 平铺位大小 / 在桌面条上缩到的大小
    private RectD _dropFrom;
    private RectD? _dropInto; // 放到别的桌面：飞到这里并淡出；null = 飞回原位

    private enum DragStage
    {
        None,
        Dragging,
        Dropping,
    }

    private volatile OverviewMode _mode;
    private bool _visible;
    private bool _prewarming;
    private bool _tracking;
    private bool _ignoring;
    private bool _closingGesture;
    private bool _openPending;
    private double _openTarget = 1;
    private double _travelMm = 30;
    private double _valueAtBegin;
    private long? _hovered;
    private long? _focusTarget;

    public OverviewController(IOverviewHost host, Func<OverviewConfig> config)
    {
        _host = host;
        _config = config;
        _transition = new InteractiveTransition(0, 1, 0) { Spring = config().Spring.ToParameters() };
        DesktopSwipe = new DesktopSwipeTarget(this);
        DesktopGesture = new DesktopGestureTarget(this);
    }

    /// <summary>
    /// 显示桌面手势（默认四指上下）：下滑显示桌面；桌面已显示时上滑把窗口拉回来，否则上滑打开窗口总览。
    /// 总览已展开时与总览手势一样（跟手收起 / 越界）。
    /// </summary>
    public IGestureTarget DesktopGesture { get; }

    /// <summary>宿主提供：此刻桌面是否处于「显示桌面」状态（上次显示桌面最小化的窗口都还最小化着）。输入线程调用，必须很快。</summary>
    public Func<bool>? DesktopShown { get; set; }

    /// <summary>总览展开时，切桌面的手势（四指横滑）改由它处理：在总览里跟手预览并切到相邻桌面。</summary>
    public IGestureTarget DesktopSwipe { get; }

    /// <summary>当前页的场景（有多页时随翻页变化；收起、选窗口、命中测试都按它）。</summary>
    public OverviewScene Scene
    {
        get
        {
            var pages = _pages;
            int page = _page;
            return page >= 0 && page < pages.Count ? pages[page] : _scene;
        }
    }

    /// <summary>各桌面的页（下标 = 桌面下标；没有多页时为空）。</summary>
    public IReadOnlyList<OverviewScene> Pages => _pages;

    /// <summary>当前页（= 当前桌面下标，-1 = 没有多页）。</summary>
    public int Page => _page;

    /// <summary>本次（或上一次）展开的形态；只在收起态下随新的展开改变。</summary>
    public OverviewMode Mode => _mode;

    public bool IsVisible
    {
        get { lock (_gate) return _visible; }
    }

    /// <summary>某时刻的进度（只读，不推进动画状态；UI 线程做命中测试用）。</summary>
    public double ProgressAt(long hostUs) => _transition.Peek(hostUs);

    public DesktopBarState Desktops => _desktops;

    /// <summary>宿主预热完成后提供场景（整体替换）。</summary>
    public void SetScene(OverviewScene scene)
    {
        _scene = scene;
        _pages = [];
        _page = -1;
    }

    /// <summary>
    /// 宿主预热时提供每个桌面的页（<paramref name="pages"/>[<paramref name="current"/>] 就是当前桌面的场景）。
    /// 有两页以上时总览里四指横滑可以翻页。
    /// </summary>
    public void SetPages(IReadOnlyList<OverviewScene> pages, int current)
    {
        _scene = current >= 0 && current < pages.Count ? pages[current] : OverviewScene.Empty;
        _pager.SetRange(0, Math.Max(0, pages.Count - 1), 0);
        _pager.SnapTo(Math.Max(0, current));
        _pages = pages;
        _page = current;
    }

    /// <summary>宿主预热时提供桌面条内容（没有虚拟桌面服务时为 <see cref="DesktopBarState.Empty"/>）。</summary>
    public void SetDesktops(DesktopBarState desktops) => _desktops = desktops;

    /// <summary>某时刻桌面条的展开程度（只读，UI 线程命中测试用）。</summary>
    public double BarExpansionAt(long hostUs) => _bar.Peek(hostUs);

    /// <summary>指针进入 / 离开桌面条（UI 线程）。只在完全展开、没有在收起或放大进入桌面时展开；离开时总是收回。</summary>
    public void SetBarHovered(bool hovered, long hostUs)
    {
        lock (_gate)
        {
            if (hovered && (!_visible || _tracking || _zoomDesktop >= 0 || _transition.Target < 1 - SettleEpsilon || _desktops.Count == 0)) return;
            if (_bar.Target == (hovered ? 1 : 0)) return;
            _bar.AnimateTo(hovered ? 1 : 0, hostUs);
        }
    }

    /// <summary>
    /// 点了桌面条上的第 <paramref name="index"/> 个桌面（UI 线程）。当前桌面：照常收起；
    /// 别的桌面：宿主立刻切换真实桌面，覆盖层播放放大进入，落定后撤层。
    /// </summary>
    public void GoToDesktop(int index, long hostUs)
    {
        bool current;
        lock (_gate)
        {
            if (!_visible || _zoomDesktop >= 0 || index < 0 || index >= _desktops.Count) return;
            current = index == _desktops.Current;
            if (!current)
            {
                _zoomDesktop = index;
                _focusTarget = null;
                _zoom.AnimateTo(1, hostUs);
            }
        }
        if (current) Close(null, hostUs);
        else _host.SwitchDesktop(index);
    }

    // ---------- 拖动窗口（UI 线程） ----------

    /// <summary>正在拖动或放下途中。</summary>
    public bool IsDragging
    {
        get { lock (_gate) return _drag != DragStage.None; }
    }

    /// <summary>
    /// 开始拖动当前页的窗口 <paramref name="windowId"/>，指针在 (<paramref name="x"/>, <paramref name="y"/>)。
    /// <paramref name="small"/> 是它移到桌面条上时缩到的大小（只用宽高）。只在总览完全展开、没有手势 / 翻页 / 放大进入时可以开始。
    /// </summary>
    public bool BeginDrag(long windowId, double x, double y, RectD small, long hostUs)
    {
        lock (_gate)
        {
            if (!_visible || _tracking || _pagerTracking || _zoomDesktop >= 0 || _drag != DragStage.None) return false;
            if (_transition.Target < 1 - SettleEpsilon || _transition.Peek(hostUs) < 1 - 0.02) return false;
            if (_page >= 0 && Math.Abs(_pager.Peek(hostUs) - _page) > 0.01) return false;
            var scene = Scene;
            int index = scene.IndexOf(windowId);
            if (index < 0 || scene.IsRemoved(index)) return false;
            var tile = scene.Tile(index, _bar.Peek(hostUs), _relayout.Peek(hostUs));
            _drag = DragStage.Dragging;
            _dragIndex = index;
            _dragX = x;
            _dragY = y;
            _grabX = Math.Clamp((x - tile.Left) / Math.Max(1, tile.Width), 0, 1);
            _grabY = Math.Clamp((y - tile.Top) / Math.Max(1, tile.Height), 0, 1);
            _dragW = tile.Width;
            _dragH = tile.Height;
            _smallW = Math.Min(small.Width, tile.Width);
            _smallH = Math.Min(small.Height, tile.Height);
            _dragShrink.SnapTo(0);
            _hovered = null;
            return true;
        }
    }

    /// <summary>拖动中指针移动；<paramref name="overBar"/>：指针在桌面条上（窗口缩小）。</summary>
    public void DragTo(double x, double y, bool overBar, long hostUs)
    {
        lock (_gate)
        {
            if (_drag != DragStage.Dragging) return;
            _dragX = x;
            _dragY = y;
            if (_dragShrink.Target != (overBar ? 1 : 0)) _dragShrink.AnimateTo(overBar ? 1 : 0, hostUs);
        }
    }

    /// <summary>
    /// 松开：<paramref name="into"/> 非空表示放到了别的桌面——窗口飞到那里（桌面条缩略图里它的位置）并淡出，
    /// 当前页移走它、其余窗口补位，返回移走后的当前页场景；为 null 时窗口飞回原位，返回 null。
    /// </summary>
    public OverviewScene? EndDrag(RectD? into, long hostUs)
    {
        lock (_gate)
        {
            if (_drag != DragStage.Dragging) return null;
            _dropFrom = DragRect(_dragShrink.Peek(hostUs));
            _dropInto = into;
            _drag = DragStage.Dropping;
            _drop.SnapTo(0);
            _drop.AnimateTo(1, hostUs);
            if (into is null) return null;
            var next = Scene.Without(_dragIndex, _relayout.Peek(hostUs));
            ReplaceScene(_page, next);
            _relayout.SnapTo(0);
            _relayout.AnimateTo(1, hostUs);
            return next;
        }
    }

    /// <summary>
    /// 窗口 <paramref name="windowId"/> 已经关掉（总览里点了关闭按钮）：它在原位缩小淡出，其余窗口补位。
    /// 返回移走后的当前页场景；不在当前页、或正在拖动时返回 null（拖动中的不加动画，下次准备场景时自然没有它）。
    /// </summary>
    public OverviewScene? RemoveWindow(long windowId, long hostUs)
    {
        lock (_gate)
        {
            var scene = Scene;
            int index = scene.IndexOf(windowId);
            if (!_visible || index < 0 || !scene.IsPresent(index) || _drag != DragStage.None) return null;
            var tile = scene.Tile(index, _bar.Peek(hostUs), _relayout.Peek(hostUs));
            _dragIndex = index;
            _dropFrom = tile;
            _dropInto = tile.ScaleAroundCenter(0.8);
            _drag = DragStage.Dropping;
            _drop.SnapTo(0);
            _drop.AnimateTo(1, hostUs);
            var next = scene.Without(index, _relayout.Peek(hostUs));
            ReplaceScene(_page, next);
            _relayout.SnapTo(0);
            _relayout.AnimateTo(1, hostUs);
            if (_hovered == windowId) _hovered = null;
            return next;
        }
    }

    /// <summary>
    /// 第 <paramref name="page"/> 页（桌面下标）加入一个窗口（从别的页拖到了那个桌面），返回它在那一页的场景下标；没有多页时返回 -1。
    /// 那一页不在屏幕上，直接排到新位置。
    /// </summary>
    public int AddToPage(int page, OverviewWindow window)
    {
        lock (_gate)
        {
            if (page < 0 || page >= _pages.Count || page == _page) return -1;
            var next = _pages[page].With(window);
            ReplaceScene(page, next);
            return next.Count - 1;
        }
    }

    private void ReplaceScene(int page, OverviewScene scene)
    {
        if (page >= 0 && page < _pages.Count)
        {
            var pages = _pages.ToArray();
            pages[page] = scene;
            _pages = pages;
            if (page == _page) _scene = scene;
        }
        else _scene = scene;
    }

    private RectD DragRect(double shrink)
    {
        double w = _dragW + (_smallW - _dragW) * shrink, h = _dragH + (_smallH - _dragH) * shrink;
        return new RectD(_dragX - _grabX * w, _dragY - _grabY * h, w, h);
    }

    /// <summary>指针下的窗口（UI 线程根据鼠标位置更新）。</summary>
    public void SetHovered(long? windowId)
    {
        lock (_gate) _hovered = windowId;
    }

    // ---------- 手势（输入线程） ----------

    public void Prepare(long hostUs)
    {
        bool prewarm;
        lock (_gate)
        {
            prewarm = !_visible && !_prewarming;
            if (prewarm) _prewarming = true;
        }
        if (prewarm) _host.Prewarm();
    }

    public void Unprepare(long hostUs)
    {
        bool cancel;
        lock (_gate)
        {
            cancel = !_visible && _prewarming;
            if (cancel) _prewarming = false;
        }
        if (cancel) _host.CancelPrewarm();
    }

    public void Begin(in GestureEvent e) => Begin(e, desktopGesture: false);

    /// <param name="desktopGesture">显示桌面手势：收起态下下滑 = 显示桌面，上滑 = 拉回窗口（桌面已显示时）或打开总览。</param>
    private void Begin(in GestureEvent e, bool desktopGesture)
    {
        bool show = false, cancelPrewarm = false;
        bool restore = desktopGesture && !_visible && e.Direction == GestureDirection.Up && DesktopShown?.Invoke() == true;
        lock (_gate)
        {
            if (_zoomDesktop >= 0 || _pagerTracking || _drag == DragStage.Dragging)
            {
                _ignoring = true; // 正在放大进入别的桌面 / 正在翻页 / 鼠标正拖着窗口，等它落定
                return;
            }
            OverviewMode? open = null; // 收起态下这次手势要展开的形态（null = 不接管）
            if (!_visible)
            {
                if (desktopGesture)
                    open = e.Axis != GestureAxis.Vertical ? null
                        : e.Direction == GestureDirection.Down || restore ? OverviewMode.Desktop
                        : OverviewMode.All;
                else if (IsOpeningDirection(e)) open = OverviewMode.All;
                else if (e.Axis == GestureAxis.Vertical && _config().AppWindows) open = OverviewMode.App;
            }
            if (!_visible && open is null)
            {
                // 收起态下朝「收起」方向滑、又没开应用窗口视图：不是总览手势，放弃预热
                _ignoring = true;
                cancelPrewarm = _prewarming;
                _prewarming = false;
            }
            else
            {
                _ignoring = false;
                _openPending = false;
                if (!_visible)
                {
                    _mode = open!.Value;
                    _visible = true;
                    _prewarming = false;
                    show = true;
                    if (restore) _transition.SnapTo(1); // 窗口此刻都在屏幕外：从「完全展开」往回拉
                }
                _tracking = true;
                _travelMm = Math.Max(1, _config().TravelMm);
                _transition.BeginTracking(e.HostTimeUs);
                _valueAtBegin = _transition.Peek(e.HostTimeUs);
                _closingGesture = _valueAtBegin > 0.5;
                _focusTarget = null;
                if (_bar.Target != 0) _bar.AnimateTo(0, e.HostTimeUs); // 手势接管：桌面条先收回
                if (_page >= 0) _pager.SnapTo(_page); // 翻页动画还没播完就开始收起：先停到当前页
            }
        }
        if (cancelPrewarm) _host.CancelPrewarm();
        if (show) _host.Show();
    }

    public void Update(in GestureEvent e)
    {
        lock (_gate)
        {
            if (!_tracking) return;
        }
        _transition.Track(OpenAxisMm(e) / _travelMm);
    }

    public void End(in GestureEvent e)
    {
        bool closing = false;
        long? focus = null;
        lock (_gate)
        {
            if (_ignoring)
            {
                _ignoring = false;
                return;
            }
            if (!_tracking) return;
            _tracking = false;

            if (e.Phase == GesturePhase.Cancelled)
            {
                // 异常中止：回到手势开始前的端点，不按速度判断
                _transition.CancelTracking(_valueAtBegin >= 0.5 ? 1 : 0, e.HostTimeUs);
            }
            else
            {
                double target = _transition.Release(OpenAxisVelocity(e) / _travelMm, e.HostTimeUs);
                if (target <= SettleEpsilon)
                {
                    _focusTarget = _closingGesture ? _hovered : null;
                    closing = true;
                    focus = _focusTarget;
                }
            }
            _closingGesture = false;
        }
        if (closing) _host.Closing(focus);
    }

    // ---------- 非手势（UI 线程：热键、点击、Esc） ----------

    /// <summary>热键 / 托盘：收起态展开，展开态（或正在展开）收起。</summary>
    public void Toggle(long hostUs)
    {
        bool open;
        lock (_gate)
        {
            if (_zoomDesktop >= 0) return;
            open = !_visible || _transition.Target <= SettleEpsilon;
        }
        if (open) Open(hostUs);
        else Close(null, hostUs);
    }

    public void Open(long hostUs) => Open(hostUs, OverviewMode.All);

    /// <summary>不用手势展开（热键、托盘、触发角）：<paramref name="mode"/> 为展开的形态。</summary>
    public void Open(long hostUs, OverviewMode mode)
    {
        bool show = false;
        lock (_gate)
        {
            _focusTarget = null;
            if (!_visible)
            {
                // 动画等覆盖层真正显示出来（OnShown）再开始，否则预热的那几十毫秒会吃掉动画开头
                _mode = mode;
                _visible = true;
                _openPending = true;
                _openTarget = 1;
                show = true;
            }
            else
            {
                _transition.AnimateTo(1, hostUs);
            }
        }
        if (show)
        {
            _host.Prewarm();
            _host.Show();
        }
    }

    /// <summary>
    /// 触发角「显示桌面」：桌面已显示时把窗口拉回来（从进度 1 播到 0），否则显示桌面。总览开着时什么也不做。
    /// </summary>
    public void ToggleDesktop(long hostUs)
    {
        bool restore = DesktopShown?.Invoke() == true;
        lock (_gate)
        {
            if (_visible) return;
            _mode = OverviewMode.Desktop;
            _visible = true;
            _openPending = true;
            _focusTarget = null;
            _openTarget = restore ? 0 : 1;
            if (restore) _transition.SnapTo(1);
        }
        _host.Prewarm();
        _host.Show();
    }

    /// <summary>宿主在覆盖层真正显示出来后调用（UI 线程）。热键展开的动画从这一刻开始。</summary>
    public void OnShown(long hostUs)
    {
        bool closing;
        lock (_gate)
        {
            if (!_openPending) return;
            _openPending = false;
            _transition.AnimateTo(_openTarget, hostUs);
            closing = _openTarget == 0;
        }
        if (closing) _host.Closing(null); // 不用手势拉回窗口：宿主现在就开始还原
    }

    /// <summary>
    /// 宿主：显示桌面落定、窗口已真正最小化、覆盖层已撤（UI 线程）。复位到收起态（不再走收起动画）。
    /// </summary>
    public void EndDesktop()
    {
        lock (_gate)
        {
            _visible = false;
            _tracking = false;
            _focusTarget = null;
            _transition.SnapTo(0);
            _bar.SnapTo(0);
        }
    }

    /// <summary>收起；<paramref name="focusWindowId"/> 为收起后置前的窗口（点击选择的那个）。</summary>
    public void Close(long? focusWindowId, long hostUs)
    {
        lock (_gate)
        {
            if (!_visible || _zoomDesktop >= 0 || _pagerTracking) return;
            if (_drag == DragStage.Dragging) // 拖动中按 Esc：窗口随收起飞回原位
            {
                _dropFrom = DragRect(_dragShrink.Peek(hostUs));
                _dropInto = null;
                _drag = DragStage.Dropping;
                _drop.SnapTo(0);
                _drop.AnimateTo(1, hostUs);
            }
            _focusTarget = focusWindowId;
            _transition.AnimateTo(0, hostUs);
            if (_page >= 0) _pager.SnapTo(_page);
            if (_bar.Target != 0) _bar.AnimateTo(0, hostUs);
        }
        _host.Closing(focusWindowId);
    }

    // ---------- 渲染线程 ----------

    public OverviewFrame Sample(long hostUs)
    {
        var s = _transition.Sample(hostUs);
        var bar = _bar.Sample(hostUs);
        var zoom = _zoom.Sample(hostUs);
        var pager = _pager.Sample(hostUs);
        var relayout = _relayout.Sample(hostUs);
        var shrink = _dragShrink.Sample(hostUs);
        var drop = _drop.Sample(hostUs);
        bool hide = false, activate = false; // 落定后的宿主回调在锁外发出；不用委托，渲染线程零分配
        long? focus = null;
        bool visible;
        long? emphasized;
        int zoomDesktop;
        int dragIndex = -1;
        RectD dragRect = default;
        double dragOpacity = 1, dragReturn = -1;
        lock (_gate)
        {
            zoomDesktop = _zoomDesktop;
            if (_drag == DragStage.Dragging)
            {
                dragIndex = _dragIndex;
                dragRect = DragRect(shrink.Value);
            }
            else if (_drag == DragStage.Dropping)
            {
                dragIndex = _dragIndex;
                double t = Math.Clamp(drop.Value, 0, 1);
                if (_dropInto is { } into)
                {
                    dragRect = RectD.Lerp(_dropFrom, into, t);
                    dragOpacity = 1 - OverviewVisuals.SmoothStep(0.55, 1, t);
                }
                else
                {
                    dragRect = _dropFrom;
                    dragReturn = t;
                }
                if (drop.JustSettled) _drag = DragStage.None; // 这一帧还按落点画（与之后的画面一致）
            }
            if (_zoomDesktop >= 0)
            {
                if (zoom.JustSettled && zoom.Value >= 1 - SettleEpsilon && _visible)
                {
                    // 放大进入别的桌面：落定时撤层（真实桌面早已切过去），各状态复位，下次从收起态开始
                    _visible = false;
                    hide = true;
                    _zoomDesktop = -1;
                    _transition.SnapTo(0);
                    _bar.SnapTo(0);
                    _zoom.SnapTo(0);
                    _drag = DragStage.None;
                    _relayout.SnapTo(1);
                }
            }
            else if (s.JustSettled && _visible && !_tracking)
            {
                if (s.Value <= SettleEpsilon)
                {
                    _visible = false;
                    focus = _focusTarget;
                    _focusTarget = null;
                    hide = true;
                    _bar.SnapTo(0);
                    _drag = DragStage.None;
                    _relayout.SnapTo(1);
                }
                else if (s.Value >= 1 - SettleEpsilon)
                {
                    activate = true;
                }
            }
            visible = _visible || hide || activate;
            emphasized = _focusTarget ?? (_closingGesture ? _hovered : null);
        }
        if (hide) _host.Hide(focus);
        else if (activate) _host.Activate();
        return new OverviewFrame(s.Value, OverviewVisuals.BackdropOpacity(s.Value), OverviewVisuals.LabelOpacity(s.Value),
            s.Phase, visible, emphasized, bar.Value, zoomDesktop, zoomDesktop >= 0 ? zoom.Value : 0, _page >= 0 ? pager.Value : 0,
            relayout.Value, dragIndex, dragRect, dragOpacity, dragReturn);
    }

    /// <summary>
    /// 按帧计算每个窗口用「活动阴影」的权重（零分配）：原前台窗口是活动的；收起时被强调（要成为前台）的窗口随进度逐渐接过来。
    /// </summary>
    public static void ActiveShadowWeights(OverviewScene scene, in OverviewFrame frame, Span<double> dest)
    {
        long? target = frame.EmphasizedWindow;
        for (int i = 0; i < scene.Count; i++)
        {
            var w = scene.Windows[i];
            dest[i] = WindowShadow.ActiveWeight(w.IsForeground, target == w.Id, target is not null, frame.Progress);
        }
    }

    /// <summary>
    /// 按帧计算每个窗口的当前矩形与不透明度，写进 <paramref name="dest"/>（长度 ≥ 场景窗口数，零分配）。
    /// 桌面条展开时平铺区域往下缩；放大进入别的桌面时整体淡出；移到别的桌面的窗口不画，拖动中的窗口跟着指针。
    /// </summary>
    public static void WindowRects(OverviewScene scene, in OverviewFrame frame, Span<RectD> dest, Span<double> opacity)
    {
        double fade = 1 - Math.Clamp(frame.Zoom, 0, 1);
        for (int i = 0; i < scene.Count; i++)
        {
            var w = scene.Windows[i];
            (dest[i], opacity[i]) = OverviewVisuals.SceneWindow(w, scene.Tile(i, frame.BarExpansion, frame.Relayout), frame.Progress, frame.EmphasizedWindow == w.Id);
            if (i == frame.DragIndex)
            {
                if (frame.DragReturn >= 0) dest[i] = RectD.Lerp(frame.DragRect, dest[i], frame.DragReturn);
                else (dest[i], opacity[i]) = (frame.DragRect, frame.DragOpacity);
            }
            else if (scene.IsRemoved(i)) opacity[i] = 0;
            else if (scene.IsExcluded(i)) // 应用窗口视图里其他程序的窗口：留在原位，随进度淡出
                (dest[i], opacity[i]) = (w.Source, w.IsMinimized ? 0 : 1 - Math.Clamp(frame.Progress, 0, 1));
            opacity[i] *= fade;
        }
    }

    // ---------- 总览里四指横滑切桌面（输入线程） ----------

    private sealed class DesktopGestureTarget(OverviewController owner) : IGestureTarget
    {
        public void Prepare(long hostUs) => owner.Prepare(hostUs);

        public void Unprepare(long hostUs) => owner.Unprepare(hostUs);

        public void Begin(in GestureEvent e) => owner.Begin(e, desktopGesture: true);

        public void Update(in GestureEvent e) => owner.Update(e);

        public void End(in GestureEvent e) => owner.End(e);
    }

    /// <summary>
    /// 不用手势翻一页（Ctrl+Win+←/→，UI 线程）：总览展开、有多页、没在翻页 / 拖动 / 放大进入时，
    /// 翻到相邻的一页并切换真实桌面。返回是否翻了（到头了也算处理过，返回 true）。
    /// </summary>
    public bool StepPage(int direction, long hostUs)
    {
        int changed = -1;
        lock (_gate)
        {
            if (!_visible || _pages.Count < 2 || _page < 0 || _pagerTracking || _zoomDesktop >= 0 || _drag != DragStage.None) return false;
            int target = Math.Clamp(_page + Math.Sign(direction), 0, _pages.Count - 1);
            if (target == _page) return true;
            _page = target;
            _desktops = _desktops with { Current = target };
            _hovered = null;
            _pager.AnimateTo(target, hostUs);
            changed = target;
        }
        _host.PageChanged(changed);
        return true;
    }

    private sealed class DesktopSwipeTarget(OverviewController owner) : IGestureTarget
    {
        public void Prepare(long hostUs)
        {
        }

        public void Unprepare(long hostUs)
        {
        }

        public void Begin(in GestureEvent e) => owner.BeginDesktopSwipe(e);

        public void Update(in GestureEvent e) => owner.UpdateDesktopSwipe(e);

        public void End(in GestureEvent e) => owner.EndDesktopSwipe(e);
    }

    private void BeginDesktopSwipe(in GestureEvent e)
    {
        lock (_gate)
        {
            _pagerIgnoring = !_visible || _tracking || _zoomDesktop >= 0 || _pages.Count < 2 || _page < 0 ||
                             _transition.Target < 1 - SettleEpsilon || _drag != DragStage.None;
            if (_pagerIgnoring) return; // 总览还没展开好、或只有一个桌面：不接管
            _pagerTracking = true;
            _pageAtBegin = _page;
            _pageTravelMm = Math.Max(1, _config().TravelMm * 1.8);
            if (_bar.Target != 0) _bar.AnimateTo(0, e.HostTimeUs);
            _pager.BeginTracking(e.HostTimeUs);
        }
    }

    private void UpdateDesktopSwipe(in GestureEvent e)
    {
        lock (_gate)
        {
            if (_pagerIgnoring || !_pagerTracking) return;
        }
        _pager.Track(-e.DxMm / _pageTravelMm); // 手指向左 = 内容向左 = 右边的桌面过来
    }

    private void EndDesktopSwipe(in GestureEvent e)
    {
        int changed = -1;
        lock (_gate)
        {
            if (_pagerIgnoring || !_pagerTracking) return;
            _pagerTracking = false;
            if (e.Phase == GesturePhase.Cancelled)
            {
                _pager.CancelTracking(_pageAtBegin, e.HostTimeUs);
                return;
            }
            // 目标只能是开始那一页或它两侧相邻的一页，且在当前位置两侧的整数之间（反向甩只会弹回）
            int start = _pageAtBegin;
            double target = _pager.Release(-e.VxMmS / _pageTravelMm, e.HostTimeUs,
                (current, projected) => Math.Clamp(Math.Round(projected), Math.Max(start - 1, Math.Floor(current)), Math.Min(start + 1, Math.Ceiling(current))));
            int page = (int)Math.Round(target);
            if (page != _page && page >= 0 && page < _pages.Count)
            {
                _page = page;
                _desktops = _desktops with { Current = page };
                _hovered = null;
                changed = page;
            }
        }
        if (changed >= 0) _host.PageChanged(changed); // 松手就切真实桌面（不等动画），总览留在新桌面上
    }

    /// <summary>非当前页的窗口（渲染线程，零分配）：总是在平铺位、不透明（翻页时它们已经展开好了），随放大进入别的桌面淡出。</summary>
    public static void PageRects(OverviewScene scene, in OverviewFrame frame, Span<RectD> dest, Span<double> opacity, Span<double> active)
    {
        double fade = 1 - Math.Clamp(frame.Zoom, 0, 1);
        for (int i = 0; i < scene.Count; i++)
        {
            var w = scene.Windows[i];
            (dest[i], opacity[i]) = OverviewVisuals.SceneWindow(w, scene.Tile(i, frame.BarExpansion), 1, false);
            opacity[i] *= scene.IsRemoved(i) ? 0 : fade;
            active[i] = w.IsForeground ? 1 : 0; // 宿主把各桌面最上层的窗口标成前台：换到那一页再收起时它会被激活
        }
    }

    /// <summary>第 <paramref name="page"/> 页在翻页位置 <paramref name="position"/> 下的横向偏移（像素）。</summary>
    public static double PageOffset(int page, double position, double width, double gap) => (page - position) * (width + gap);

    // 展开方向：竖直轴向上、水平轴向右为正；应用窗口视图与显示桌面竖直轴反过来（向下展开）
    private bool OpensDownward => _mode is OverviewMode.App or OverviewMode.Desktop;

    private double OpenAxisMm(in GestureEvent e) => e.Axis == GestureAxis.Vertical ? (OpensDownward ? e.DyMm : -e.DyMm) : e.DxMm;

    private double OpenAxisVelocity(in GestureEvent e) => e.Axis == GestureAxis.Vertical ? (OpensDownward ? e.VyMmS : -e.VyMmS) : e.VxMmS;

    private static bool IsOpeningDirection(in GestureEvent e) => e.Direction is GestureDirection.Up or GestureDirection.Right;
}
