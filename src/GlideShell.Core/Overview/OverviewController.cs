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

    /// <summary>收起落定：隐藏覆盖层。<paramref name="focusWindowId"/> 非空时把它置前（最小化的先还原），否则还原原来的前台窗口。</summary>
    void Hide(long? focusWindowId);
}

/// <summary>渲染线程每帧取到的总览状态。</summary>
public readonly record struct OverviewFrame(
    double Progress,
    double BackdropOpacity,
    double LabelOpacity,
    TransitionPhase Phase,
    bool Visible,
    long? EmphasizedWindow);

/// <summary>
/// 窗口总览：手势 → 连续进度（0 = 收起，1 = 展开）→ 画面。
/// <list type="bullet">
/// <item>朝展开方向（上 / 右）开始的手势才从收起态接管；展开态下反向滑动跟手收起，同向滑动是橡皮筋越界。</item>
/// <item>手指在板上只跟随；松手才按投影落点决定展开或收起，并以松手速度驱动弹簧。动画中可随时被新手势接管。</item>
/// <item>收起时指针下的窗口逐渐放大，松手后它成为前台（macOS 行为）。</item>
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
    private volatile OverviewScene _scene = OverviewScene.Empty;

    private bool _visible;
    private bool _prewarming;
    private bool _tracking;
    private bool _ignoring;
    private bool _closingGesture;
    private double _travelMm = 30;
    private double _valueAtBegin;
    private long? _hovered;
    private long? _focusTarget;

    public OverviewController(IOverviewHost host, Func<OverviewConfig> config)
    {
        _host = host;
        _config = config;
        _transition = new InteractiveTransition(0, 1, 0) { Spring = config().Spring.ToParameters() };
    }

    public OverviewScene Scene => _scene;

    public bool IsVisible
    {
        get { lock (_gate) return _visible; }
    }

    /// <summary>宿主预热完成后提供场景（整体替换）。</summary>
    public void SetScene(OverviewScene scene) => _scene = scene;

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
                if (target <= SettleEpsilon) _focusTarget = _closingGesture ? _hovered : null;
            }
            _closingGesture = false;
        }
    }

    // ---------- 非手势（UI 线程：热键、点击、Esc） ----------

    /// <summary>热键 / 托盘：收起态展开，展开态（或正在展开）收起。</summary>
    public void Toggle(long hostUs)
    {
        bool open;
        lock (_gate) open = !_visible || _transition.Target <= SettleEpsilon;
        if (open) Open(hostUs);
        else Close(null, hostUs);
    }

    public void Open(long hostUs)
    {
        bool show = false;
        lock (_gate)
        {
            if (!_visible)
            {
                _visible = true;
                show = true;
            }
            _focusTarget = null;
            _transition.AnimateTo(1, hostUs);
        }
        if (show)
        {
            _host.Prewarm();
            _host.Show();
        }
    }

    /// <summary>收起；<paramref name="focusWindowId"/> 为收起后置前的窗口（点击选择的那个）。</summary>
    public void Close(long? focusWindowId, long hostUs)
    {
        lock (_gate)
        {
            if (!_visible) return;
            _focusTarget = focusWindowId;
            _transition.AnimateTo(0, hostUs);
        }
    }

    // ---------- 渲染线程 ----------

    public OverviewFrame Sample(long hostUs)
    {
        var s = _transition.Sample(hostUs);
        Action? settled = null;
        bool visible;
        long? emphasized;
        lock (_gate)
        {
            if (s.JustSettled && _visible && !_tracking)
            {
                if (s.Value <= SettleEpsilon)
                {
                    _visible = false;
                    long? focus = _focusTarget;
                    _focusTarget = null;
                    settled = () => _host.Hide(focus);
                }
                else if (s.Value >= 1 - SettleEpsilon)
                {
                    settled = _host.Activate;
                }
            }
            visible = _visible || settled is not null;
            emphasized = _focusTarget ?? (_closingGesture ? _hovered : null);
        }
        settled?.Invoke();
        return new OverviewFrame(s.Value, OverviewVisuals.BackdropOpacity(s.Value), OverviewVisuals.LabelOpacity(s.Value),
            s.Phase, visible, emphasized);
    }

    /// <summary>按帧计算每个窗口的当前矩形，写进 <paramref name="dest"/>（长度 ≥ 场景窗口数，零分配）。</summary>
    public static void WindowRects(OverviewScene scene, in OverviewFrame frame, Span<RectD> dest)
    {
        for (int i = 0; i < scene.Count; i++)
        {
            var rect = OverviewVisuals.WindowRect(scene.Windows[i].Source, scene.Slots[i].Tile, frame.Progress);
            if (frame.EmphasizedWindow == scene.Windows[i].Id) rect = OverviewVisuals.EmphasizeForDismiss(rect, frame.Progress);
            dest[i] = rect;
        }
    }

    // 展开方向：竖直轴向上、水平轴向右为正
    private static double OpenAxisMm(in GestureEvent e) => e.Axis == GestureAxis.Vertical ? -e.DyMm : e.DxMm;

    private static double OpenAxisVelocity(in GestureEvent e) => e.Axis == GestureAxis.Vertical ? -e.VyMmS : e.VxMmS;

    private static bool IsOpeningDirection(in GestureEvent e) => e.Direction is GestureDirection.Up or GestureDirection.Right;
}
