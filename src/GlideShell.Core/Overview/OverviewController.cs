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

    /// <summary>总览里四指横滑开始预览相邻桌面（还没决定切换）：宿主把那个桌面的缩略图挪到最上层，随手指放大。</summary>
    void PreviewDesktop(int desktopIndex);
}

/// <summary>渲染线程每帧取到的总览状态。</summary>
/// <param name="BarExpansion">桌面条展开程度（0 收起 ~ 1 展开）。</param>
/// <param name="ZoomDesktop">点击后正在放大进入的桌面（-1 = 没有）。</param>
/// <param name="Zoom">放大进入的进度（0 ~ 1）：目标桌面缩略图从桌面条放大到全屏，其余内容随之淡出。</param>
public readonly record struct OverviewFrame(
    double Progress,
    double BackdropOpacity,
    double LabelOpacity,
    TransitionPhase Phase,
    bool Visible,
    long? EmphasizedWindow,
    double BarExpansion = 0,
    int ZoomDesktop = -1,
    double Zoom = 0);

/// <summary>
/// 窗口总览：手势 → 连续进度（0 = 收起，1 = 展开）→ 画面。
/// <list type="bullet">
/// <item>朝展开方向（上 / 右）开始的手势才从收起态接管；展开态下反向滑动跟手收起，同向滑动是橡皮筋越界。</item>
/// <item>手指在板上只跟随；松手才按投影落点决定展开或收起，并以松手速度驱动弹簧。动画中可随时被新手势接管。</item>
/// <item>收起时指针下的窗口逐渐放大，松手后它成为前台（macOS 行为）。</item>
/// <item>桌面条：指针移到顶部展开；点另一个桌面时真实桌面立刻切换，覆盖层把那个桌面的缩略图放大到全屏后撤掉（macOS 调度中心）。</item>
/// <item>总览里四指横滑（<see cref="DesktopSwipe"/>）：相邻桌面的缩略图随手指放大、其余内容淡出；松手按投影决定切过去还是缩回。</item>
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
    private bool _zoomTracking; // 四指横滑正在跟手预览 _zoomDesktop
    private bool _zoomCommitted; // 已决定切过去（真实桌面已在切换）：放大落定时撤层
    private int _swipeDirection; // +1 = 右边的桌面（手指向左），-1 = 左边
    private double _swipeTravelMm = 45;
    private bool _swipeIgnoring;

    private bool _visible;
    private bool _prewarming;
    private bool _tracking;
    private bool _ignoring;
    private bool _closingGesture;
    private bool _openPending;
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
    }

    /// <summary>总览展开时，切桌面的手势（四指横滑）改由它处理：在总览里跟手预览并切到相邻桌面。</summary>
    public IGestureTarget DesktopSwipe { get; }

    public OverviewScene Scene => _scene;

    public bool IsVisible
    {
        get { lock (_gate) return _visible; }
    }

    /// <summary>某时刻的进度（只读，不推进动画状态；UI 线程做命中测试用）。</summary>
    public double ProgressAt(long hostUs) => _transition.Peek(hostUs);

    public DesktopBarState Desktops => _desktops;

    /// <summary>宿主预热完成后提供场景（整体替换）。</summary>
    public void SetScene(OverviewScene scene) => _scene = scene;

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
                _zoomCommitted = true;
                _focusTarget = null;
                _zoom.AnimateTo(1, hostUs);
            }
        }
        if (current) Close(null, hostUs);
        else _host.SwitchDesktop(index);
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

    public void Begin(in GestureEvent e)
    {
        bool show = false, cancelPrewarm = false;
        lock (_gate)
        {
            if (_zoomDesktop >= 0)
            {
                _ignoring = true; // 正在放大进入别的桌面（真实桌面已经切过去了），等它落定
                return;
            }
            if (!_visible && !IsOpeningDirection(e))
            {
                // 收起态下朝「收起」方向滑：不是总览手势（留作将来的应用窗口视图），放弃预热
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
                    _visible = true;
                    _prewarming = false;
                    show = true;
                }
                _tracking = true;
                _travelMm = Math.Max(1, _config().TravelMm);
                _transition.BeginTracking(e.HostTimeUs);
                _valueAtBegin = _transition.Peek(e.HostTimeUs);
                _closingGesture = _valueAtBegin > 0.5;
                _focusTarget = null;
                if (_bar.Target != 0) _bar.AnimateTo(0, e.HostTimeUs); // 手势接管：桌面条先收回
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

    public void Open(long hostUs)
    {
        bool show = false;
        lock (_gate)
        {
            _focusTarget = null;
            if (!_visible)
            {
                // 动画等覆盖层真正显示出来（OnShown）再开始，否则预热的那几十毫秒会吃掉动画开头
                _visible = true;
                _openPending = true;
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

    /// <summary>宿主在覆盖层真正显示出来后调用（UI 线程）。热键展开的动画从这一刻开始。</summary>
    public void OnShown(long hostUs)
    {
        lock (_gate)
        {
            if (!_openPending) return;
            _openPending = false;
            _transition.AnimateTo(1, hostUs);
        }
    }

    /// <summary>收起；<paramref name="focusWindowId"/> 为收起后置前的窗口（点击选择的那个）。</summary>
    public void Close(long? focusWindowId, long hostUs)
    {
        lock (_gate)
        {
            if (!_visible || _zoomDesktop >= 0) return;
            _focusTarget = focusWindowId;
            _transition.AnimateTo(0, hostUs);
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
        bool hide = false, activate = false; // 落定后的宿主回调在锁外发出；不用委托，渲染线程零分配
        long? focus = null;
        bool visible;
        long? emphasized;
        int zoomDesktop;
        lock (_gate)
        {
            zoomDesktop = _zoomDesktop;
            if (_zoomDesktop >= 0)
            {
                if (zoom.JustSettled && !_zoomTracking && _zoomCommitted && zoom.Value >= 1 - SettleEpsilon && _visible)
                {
                    // 放大进入别的桌面：落定时撤层（真实桌面早已切过去），各状态复位，下次从收起态开始
                    _visible = false;
                    hide = true;
                    _zoomDesktop = -1;
                    _zoomCommitted = false;
                    _transition.SnapTo(0);
                    _bar.SnapTo(0);
                    _zoom.SnapTo(0);
                }
                else if (zoom.JustSettled && !_zoomTracking && !_zoomCommitted && zoom.Value <= SettleEpsilon)
                {
                    _zoomDesktop = -1; // 四指横滑没切过去：缩回原位，回到普通总览
                    zoomDesktop = -1;
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
            s.Phase, visible, emphasized, bar.Value, zoomDesktop, zoomDesktop >= 0 ? zoom.Value : 0);
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
    /// 桌面条展开时平铺区域往下缩；放大进入别的桌面时整体淡出。
    /// </summary>
    public static void WindowRects(OverviewScene scene, in OverviewFrame frame, Span<RectD> dest, Span<double> opacity)
    {
        double fade = 1 - Math.Clamp(frame.Zoom, 0, 1);
        for (int i = 0; i < scene.Count; i++)
        {
            var w = scene.Windows[i];
            (dest[i], opacity[i]) = OverviewVisuals.SceneWindow(w, scene.Tile(i, frame.BarExpansion), frame.Progress, frame.EmphasizedWindow == w.Id);
            opacity[i] *= fade;
        }
    }

    // ---------- 总览里四指横滑切桌面（输入线程） ----------

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
        int index;
        lock (_gate)
        {
            // 手指向左 = 内容向左 = 右边的桌面过来（与四指切桌面一致）
            _swipeDirection = e.Direction == GestureDirection.Left ? 1 : e.Direction == GestureDirection.Right ? -1 : 0;
            index = _desktops.Current + _swipeDirection;
            _swipeIgnoring = !_visible || _tracking || _zoomDesktop >= 0 || _swipeDirection == 0 ||
                             _transition.Target < 1 - SettleEpsilon || index < 0 || index >= _desktops.Count;
            if (_swipeIgnoring) return; // 首尾桌面之外、或总览还没展开好：不接管
            _zoomDesktop = index;
            _zoomTracking = true;
            _zoomCommitted = false;
            _swipeTravelMm = Math.Max(1, _config().TravelMm * 1.5);
            _zoom.BeginTracking(e.HostTimeUs);
        }
        _host.PreviewDesktop(index);
    }

    private void UpdateDesktopSwipe(in GestureEvent e)
    {
        lock (_gate)
        {
            if (_swipeIgnoring || !_zoomTracking) return;
        }
        _zoom.Track(_swipeDirection * -e.DxMm / _swipeTravelMm);
    }

    private void EndDesktopSwipe(in GestureEvent e)
    {
        int commit = -1;
        lock (_gate)
        {
            if (_swipeIgnoring || !_zoomTracking) return;
            _zoomTracking = false;
            if (e.Phase == GesturePhase.Cancelled)
            {
                _zoom.CancelTracking(0, e.HostTimeUs);
                return;
            }
            double target = _zoom.Release(_swipeDirection * -e.VxMmS / _swipeTravelMm, e.HostTimeUs);
            if (target >= 1 - SettleEpsilon)
            {
                _zoomCommitted = true;
                commit = _zoomDesktop;
            }
        }
        if (commit >= 0) _host.SwitchDesktop(commit); // 松手就切（不等动画），覆盖层继续把放大播完
    }

    // 展开方向：竖直轴向上、水平轴向右为正
    private static double OpenAxisMm(in GestureEvent e) => e.Axis == GestureAxis.Vertical ? -e.DyMm : e.DxMm;

    private static double OpenAxisVelocity(in GestureEvent e) => e.Axis == GestureAxis.Vertical ? -e.VyMmS : e.VxMmS;

    private static bool IsOpeningDirection(in GestureEvent e) => e.Direction is GestureDirection.Up or GestureDirection.Right;
}
