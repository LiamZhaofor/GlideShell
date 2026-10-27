using GlideShell.Core.Configuration;
using GlideShell.Core.Gestures;
using GlideShell.Core.Motion;

namespace GlideShell.Core.Desktops;

/// <summary>切换虚拟桌面的宿主（覆盖层 + 虚拟桌面服务）。方法必须立刻返回，真正的工作投递到别的线程。</summary>
public interface IDesktopSwitchHost
{
    /// <summary>准备：读当前桌面与左右相邻桌面、枚举各自的窗口、预注册缩略图。完成后调用 <see cref="DesktopSwitchController.SetNeighbors"/>。</summary>
    void Prewarm();

    void CancelPrewarm();

    void Show();

    /// <summary>松手已决定切到相邻桌面（-1 左 / +1 右）：立刻在覆盖层下面无动画切换，覆盖层继续把动画播完。</summary>
    void Commit(int offset);

    /// <summary>动画落定：撤掉覆盖层。<paramref name="committedOffset"/> ≠ 0 时等切换完成后再撤，并把焦点交给新桌面的顶层窗口。</summary>
    void Hide(int committedOffset);
}

public readonly record struct DesktopSwitchFrame(double Position, TransitionPhase Phase, bool Visible);

/// <summary>
/// 四指连续切换虚拟桌面：位置 p（0 = 当前桌面，+1 = 右边的桌面完全滑入，−1 = 左边）随手指 1:1 移动
/// （手指向左，整个桌面向左，右边的桌面滑入），没有起滑死区之外的额外延迟；松手按投影落点决定切换或弹回，
/// 以松手速度驱动弹簧——这两点是相对 Windows 原生四指切换（固定时长收尾）的改进（S3）。
/// 首尾桌面之外是橡皮筋。一旦决定切换，后续手势在动画落定前不接管（真实桌面已在覆盖层下面切换）。
/// </summary>
public sealed class DesktopSwitchController : IGestureTarget
{
    private readonly IDesktopSwitchHost _host;
    private readonly Func<DesktopConfig> _config;
    private readonly object _gate = new();
    private readonly InteractiveTransition _transition;

    private bool _visible;
    private bool _prewarming;
    private bool _tracking;
    private bool _ignoring;
    private int _committed;
    private double _travelMm = 55;
    private int _pendingStep; // 键盘切换：等预热读到相邻桌面后再开始
    private int _queuedStep; // 动画还没落定时又按了一次：落定后接着切

    public DesktopSwitchController(IDesktopSwitchHost host, Func<DesktopConfig> config)
    {
        _host = host;
        _config = config;
        _transition = new InteractiveTransition(0, 0, 0)
        {
            Spring = config().Spring.ToParameters(),
            OverscrollBelow = 0.12,
            OverscrollAbove = 0.12,
        };
    }

    /// <summary>某时刻的位置（不推进动画、无副作用），供呈现层在显示覆盖层之前先摆好第一帧。</summary>
    public double PositionAt(long hostUs) => _transition.Peek(hostUs);

    public bool IsVisible
    {
        get { lock (_gate) return _visible; }
    }

    /// <summary>宿主读到相邻桌面后调用：没有左 / 右邻居时那一侧只剩橡皮筋。</summary>
    public void SetNeighbors(bool hasLeft, bool hasRight, long hostUs)
    {
        _transition.SetRange(hasLeft ? -1 : 0, hasRight ? 1 : 0, hostUs);
        int step;
        lock (_gate)
        {
            step = _pendingStep;
            _pendingStep = 0;
            if (step == 0) return;
            if (step < 0 ? !hasLeft : !hasRight)
            {
                // 那一侧没有桌面：带一点初速度弹一下（往外冲一点再回来），落定后撤层
                _transition.AnimateTo(0, hostUs, velocity: step * 1.5);
                return;
            }
            _committed = step;
            _transition.AnimateTo(step, hostUs);
        }
        _host.Commit(step);
    }

    /// <summary>
    /// 不用手势切到相邻桌面（Ctrl+Win+←/→，任意线程）：<paramref name="direction"/> −1 左 / +1 右。
    /// 和手势切换同一套覆盖层与弹簧；动画还没落定时再按，落定后接着往同一方向切。
    /// </summary>
    public void Step(int direction, long hostUs)
    {
        direction = Math.Sign(direction);
        if (direction == 0) return;
        lock (_gate)
        {
            if (_visible)
            {
                if (_committed != 0 && !_tracking) _queuedStep = direction;
                return;
            }
            _visible = true;
            _prewarming = false;
            _pendingStep = direction;
        }
        _host.Prewarm();
        _host.Show();
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
        bool show = false;
        lock (_gate)
        {
            if (_committed != 0)
            {
                _ignoring = true; // 真实桌面已经切换，等这次动画落定
                return;
            }
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
        }
        if (show) _host.Show();
    }

    public void Update(in GestureEvent e)
    {
        lock (_gate)
        {
            if (!_tracking) return;
        }
        _transition.Track(-e.DxMm / _travelMm);
    }

    public void End(in GestureEvent e)
    {
        int commit = 0;
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
                _transition.CancelTracking(0, e.HostTimeUs);
                return;
            }
            // 目标只能在当前位置两侧的整数之间：从 0.7 处反向甩只会弹回 0，不会越过当前桌面跳到另一侧
            double target = _transition.Release(-e.VxMmS / _travelMm, e.HostTimeUs,
                (current, projected) => Math.Clamp(Math.Round(projected), Math.Max(-1, Math.Floor(current)), Math.Min(1, Math.Ceiling(current))));
            commit = (int)Math.Round(target);
            _committed = commit;
        }
        if (commit != 0) _host.Commit(commit);
    }

    // ---------- 渲染线程 ----------

    public DesktopSwitchFrame Sample(long hostUs)
    {
        var s = _transition.Sample(hostUs);
        bool hide = false; // 落定后的宿主回调在锁外发出；不用委托，渲染线程零分配
        int committed = 0, queued = 0;
        bool visible;
        lock (_gate)
        {
            if (s.JustSettled && _visible && !_tracking && _pendingStep == 0)
            {
                _visible = false;
                committed = _committed;
                _committed = 0;
                queued = _queuedStep;
                _queuedStep = 0;
                hide = true;
            }
            visible = _visible || hide;
        }
        if (hide)
        {
            _host.Hide(committed);
            _transition.SnapTo(0); // 下一次从（新的）当前桌面开始
            if (queued != 0) Step(queued, hostUs);
        }
        return new DesktopSwitchFrame(s.Value, s.Phase, visible);
    }

    /// <summary>位置 p 下，相对当前桌面偏移 <paramref name="desktopOffset"/> 的那一屏的横向位移（像素）。</summary>
    public static double SlideOffset(int desktopOffset, double position, double screenWidth, double gap)
        => (desktopOffset - position) * (screenWidth + gap);
}
