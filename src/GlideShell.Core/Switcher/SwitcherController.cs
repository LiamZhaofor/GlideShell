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
public readonly record struct SwitcherFrame(double Position, int Selected, double Appear, TransitionPhase Phase, bool Visible);

/// <summary>
/// 横滑跟手切换窗口：连续位置 p（0 = 当前窗口）随手指 1:1 移动（手指向左，内容向左，下一个窗口进入中央），
/// 松手按投影落点吸附到最近的窗口并切换；两端有橡皮筋。动画中再次滑动从当前位置接管。
/// 线程约定与 <see cref="OverviewController"/> 相同。
/// </summary>
public sealed class SwitcherController : IGestureTarget
{
    private const double AppearMs = 120;

    private readonly ISwitcherHost _host;
    private readonly Func<SwitcherConfig> _config;
    private readonly object _gate = new();
    private readonly InteractiveTransition _transition;
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
            if (e.Phase == GesturePhase.Cancelled)
            {
                _transition.CancelTracking(0, e.HostTimeUs); // 异常中止：回到当前窗口，不切换
                return;
            }
            int last = Math.Max(0, _scene.Count - 1);
            _transition.Release(-e.VxMmS / _mmPerWindow, e.HostTimeUs, (_, projected) => Math.Clamp(Math.Round(projected), 0, last));
        }
    }

    // ---------- 渲染线程 ----------

    public SwitcherFrame Sample(long hostUs)
    {
        var s = _transition.Sample(hostUs);
        var scene = _scene;
        bool hide = false; // 落定后的宿主回调在锁外发出；不用委托，渲染线程零分配
        long? activate = null;
        bool visible;
        double appear;
        lock (_gate)
        {
            if (s.JustSettled && _visible && !_tracking)
            {
                _visible = false;
                int selected = scene.Count == 0 ? 0 : (int)Math.Clamp(Math.Round(s.Value), 0, scene.Count - 1);
                activate = selected > 0 ? scene.Windows[selected].Id : null;
                hide = true;
                _transition.SnapTo(0); // 下一次切换从当前（新的前台）窗口开始
            }
            visible = _visible || hide;
            appear = Math.Clamp((hostUs - _shownAtUs) / 1000.0 / AppearMs, 0, 1);
        }
        if (hide) _host.Hide(activate);
        int nearest = scene.Layout.Nearest(s.Value);
        return new SwitcherFrame(s.Value, nearest, appear, s.Phase, visible);
    }
}
