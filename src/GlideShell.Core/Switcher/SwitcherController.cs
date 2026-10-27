using GlideShell.Core.Configuration;
using GlideShell.Core.Gestures;
using GlideShell.Core.Motion;
using GlideShell.Core.Overview;

namespace GlideShell.Core.Switcher;

/// <summary>窗口切换的宿主（切换条覆盖层）。方法必须立刻返回，真正的工作投递到 UI 线程。</summary>
public interface ISwitcherHost
{
    /// <summary>后台准备：按最近使用顺序枚举窗口、布局、预注册缩略图。完成后调用 <see cref="SwitcherController.SetScene"/>。</summary>
    void Prewarm();

    void CancelPrewarm();

    void Show();

    /// <summary>落定：隐藏切换条；<paramref name="activateWindowId"/> 非空时把它切到前台。</summary>
    void Hide(long? activateWindowId);
}

/// <summary>一次切换会话的窗口（第 0 个是当前前台窗口，之后按最近使用顺序）与切换条布局。</summary>
public sealed record SwitcherScene(IReadOnlyList<OverviewWindow> Windows, SwitcherLayout Layout)
{
    public static SwitcherScene Empty { get; } = new([], SwitcherLayout.Create([], default));

    public int Count => Windows.Count;
}

/// <summary>渲染线程每帧取到的切换状态。</summary>
/// <param name="Launch">
/// 松手后的「飞回」进度（0 ~ 1）：选中的缩略图从切换条上的位置飞到窗口的真实位置与大小，其余内容淡出；
/// 落定时宿主把窗口切到前台并撤层。没有在飞回时为 0。
/// </param>
/// <param name="LaunchIndex">正在飞回的窗口（-1 = 没有）。</param>
public readonly record struct SwitcherFrame(double Position, int Selected, double Appear, TransitionPhase Phase, bool Visible, double Launch = 0,
    int LaunchIndex = -1);

/// <summary>
/// 横滑跟手切换窗口：连续位置 p（0 = 当前窗口）随手指 1:1 移动（手指向左，内容向左，下一个窗口进入中央），
/// 松手按投影落点吸附到最近的窗口，同时选中的缩略图飞回窗口的真实位置（<see cref="SwitcherFrame.Launch"/>），
/// 落定时切换前台、撤层，画面与真实桌面无缝衔接。两端有橡皮筋。动画中（包括飞回途中）再次滑动从当前位置接管。
/// 线程约定与 <see cref="OverviewController"/> 相同。
/// </summary>
public sealed class SwitcherController : IGestureTarget
{
    private const double AppearMs = 120;

    private readonly ISwitcherHost _host;
    private readonly Func<SwitcherConfig> _config;
    private readonly object _gate = new();
    private readonly InteractiveTransition _transition;
    private readonly InteractiveTransition _launch;
    private int _launchIndex = -1; // 正在飞回的窗口（-1 = 没有）
    private volatile SwitcherScene _scene = SwitcherScene.Empty;

    private bool _visible;
    private bool _prewarming;
    private bool _tracking;
    private double _mmPerWindow = 22;
    private long _shownAtUs;

    public SwitcherController(ISwitcherHost host, Func<SwitcherConfig> config)
    {
        _host = host;
        _config = config;
        _transition = new InteractiveTransition(0, 0, 0)
        {
            Spring = config().Spring.ToParameters(),
            OverscrollBelow = 0.35,
            OverscrollAbove = 0.35,
        };
        // 飞回：不越界、略带阻尼（落定时正好在窗口的真实位置，撤层才没有跳变）
        _launch = new InteractiveTransition(0, 1, 0) { Spring = new SpringParameters(0.3, 1.0) };
    }

    public SwitcherScene Scene => _scene;

    public bool IsVisible
    {
        get { lock (_gate) return _visible; }
    }

    /// <summary>宿主准备好场景后调用（UI 线程）；窗口数在这时才确定，同步更新取值范围。</summary>
    public void SetScene(SwitcherScene scene, long hostUs)
    {
        _scene = scene;
        _transition.SetRange(0, Math.Max(0, scene.Count - 1), hostUs);
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
            if (!_visible)
            {
                _visible = true;
                _prewarming = false;
                _shownAtUs = e.HostTimeUs;
                show = true;
            }
            _tracking = true;
            if (_launchIndex >= 0)
            {
                // 飞回途中又滑起来：停止飞回，从当前位置接着跟手
                _launchIndex = -1;
                _launch.SnapTo(0);
            }
            _mmPerWindow = Math.Max(1, _config().MmPerWindow);
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
        _transition.Track(-e.DxMm / _mmPerWindow);
    }

    public void End(in GestureEvent e)
    {
        lock (_gate)
        {
            if (!_tracking) return;
            _tracking = false;
            double target;
            if (e.Phase == GesturePhase.Cancelled)
            {
                _transition.CancelTracking(0, e.HostTimeUs); // 异常中止：回到当前窗口，不切换
                target = 0;
            }
            else
            {
                int last = Math.Max(0, _scene.Count - 1);
                target = _transition.Release(-e.VxMmS / _mmPerWindow, e.HostTimeUs, (_, projected) => Math.Clamp(Math.Round(projected), 0, last));
            }
            // 松手就开始飞回（与吸附同时进行，不等吸附落定）：起点是选中项此刻在切换条上的位置，随吸附移动
            if (_scene.Count > 0)
            {
                _launchIndex = (int)Math.Clamp(Math.Round(target), 0, _scene.Count - 1);
                _launch.SnapTo(0);
                _launch.AnimateTo(1, e.HostTimeUs);
            }
        }
    }

    // ---------- 渲染线程 ----------

    public SwitcherFrame Sample(long hostUs)
    {
        var s = _transition.Sample(hostUs);
        var l = _launch.Sample(hostUs);
        var scene = _scene;
        bool hide = false; // 落定后的宿主回调在锁外发出；不用委托，渲染线程零分配
        long? activate = null;
        bool visible;
        double appear, launch;
        int launchIndex;
        double position = s.Value;
        lock (_gate)
        {
            if (_launchIndex < 0 && s.JustSettled && _visible && !_tracking)
            {
                // 兜底（没有松手事件就落定了，例如场景为空）：选中的缩略图从这里飞回窗口原位
                _launchIndex = scene.Count == 0 ? -1 : (int)Math.Clamp(Math.Round(s.Value), 0, scene.Count - 1);
                if (_launchIndex < 0) hide = true;
                else
                {
                    _launch.SnapTo(0);
                    _launch.AnimateTo(1, hostUs);
                    l = _launch.Sample(hostUs);
                }
            }
            if (_launchIndex >= 0 && l.JustSettled && _visible && !_tracking)
            {
                activate = _launchIndex > 0 ? scene.Windows[_launchIndex].Id : null;
                hide = true;
            }
            if (hide)
            {
                _visible = false; // 这一帧仍按落点画（与撤层后的桌面一致）
                _transition.SnapTo(0); // 下一次切换从当前（新的前台）窗口开始
            }
            visible = _visible || hide;
            appear = Math.Clamp((hostUs - _shownAtUs) / 1000.0 / AppearMs, 0, 1);
            launch = _launchIndex >= 0 ? Math.Clamp(l.Value, 0, 1) : 0;
            launchIndex = _launchIndex;
            if (hide)
            {
                _launchIndex = -1;
                _launch.SnapTo(0);
            }
        }
        if (hide) _host.Hide(activate);
        int nearest = scene.Layout.Nearest(position);
        return new SwitcherFrame(position, nearest, appear, s.Phase, visible, launch, launchIndex);
    }
}
