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

    /// <summary>
    /// 松手、选中的缩略图开始飞回 <paramref name="windowId"/>（不是当前窗口时才调用）。最小化的窗口可以趁飞回途中
    /// 悄悄还原到最下层、让程序重绘好，落定时只需提到前台，不会闪一下。
    /// </summary>
    void Launching(long windowId) { }
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
/// <param name="RingIndex">选中框画在哪一项上（-1 = 跟着连续位置走）：Alt+Tab 时鼠标悬停选中，切换条不滚动，只有选中框移过去。</param>
public readonly record struct SwitcherFrame(double Position, int Selected, double Appear, TransitionPhase Phase, bool Visible, double Launch = 0,
    int LaunchIndex = -1, int RingIndex = -1);

/// <summary>
/// 横滑跟手切换窗口：连续位置 p（0 = 当前窗口）随手指 1:1 移动（手指向左，内容向左，下一个窗口进入中央），
/// 松手按投影落点吸附到最近的窗口，同时选中的缩略图飞回窗口的真实位置（<see cref="SwitcherFrame.Launch"/>），
/// 落定时切换前台、撤层，画面与真实桌面无缝衔接。两端有橡皮筋。动画中（包括飞回途中）再次滑动从当前位置接管。
/// 也可以用键盘驱动（Alt+Tab，见 <see cref="KeyStep"/>）：每按一次 Tab 选中项弹簧移动一格，松开 Alt 时同样飞回。
/// 线程约定与 <see cref="OverviewController"/> 相同。
/// </summary>
public sealed class SwitcherController : IGestureTarget
{
    private const double AppearMs = 120;

    /// <summary>键盘切换时切换条延迟多久才淡入：快速按一下 Alt+Tab 直接切到上一个窗口，不闪出切换条（与系统的 Alt+Tab 一致）。</summary>
    public const long KeyRevealDelayUs = 100_000;

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
    private bool _keySession; // 键盘（Alt+Tab）会话中：Alt 还按着
    private int _keyIndex; // 选中项
    private int _stripIndex; // 切换条居中的那一项（鼠标悬停改选中项时不动）

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

    /// <summary>键盘会话中（Alt+Tab 按下、Alt 还没松开）。</summary>
    public bool IsKeySession
    {
        get { lock (_gate) return _keySession; }
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
            _keySession = false; // 按着 Alt 又用手势滑：交给手势
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
        long? launching = null;
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
                if (_launchIndex > 0) launching = _scene.Windows[_launchIndex].Id;
            }
        }
        if (launching is { } id) _host.Launching(id);
    }

    // ---------- 键盘（Alt+Tab，UI 线程） ----------

    /// <summary>
    /// Alt+Tab：第一次调用开始一次键盘会话（宿主须已准备好场景，第 0 个是当前窗口），<paramref name="direction"/> = +1 选中下一个窗口、
    /// −1 选中最后一个（Alt+Shift+Tab）；之后每次移动一格，两端循环。选中项弹簧移到中央；切换条延迟 <see cref="KeyRevealDelayUs"/> 才淡入。
    /// 正在跟手滑动、正在飞回或没有窗口时返回 false（不开始）。
    /// </summary>
    public bool KeyStep(int direction, long hostUs)
    {
        bool show = false;
        lock (_gate)
        {
            int n = _scene.Count;
            if (!_keySession)
            {
                if (_visible || n == 0) return false;
                _keySession = true;
                _visible = true;
                _prewarming = false;
                _shownAtUs = hostUs + KeyRevealDelayUs;
                _keyIndex = _stripIndex = 0;
                _transition.SnapTo(0);
                show = true;
            }
            // 从选中项（可能是鼠标悬停选的）往下走一格，切换条把它滚到中间
            _keyIndex = _stripIndex = ((_keyIndex + Math.Sign(direction)) % n + n) % n;
            _transition.AnimateTo(_keyIndex, hostUs);
        }
        if (show) _host.Show();
        return true;
    }

    /// <summary>键盘会话中选中的窗口（Delete 关它；不在会话中为 null）。</summary>
    public long? KeySelectedWindow
    {
        get
        {
            lock (_gate) return _keySession && _keyIndex < _scene.Count ? _scene.Windows[_keyIndex].Id : null;
        }
    }

    /// <summary>切换条此刻居中的那一项（键盘会话中，鼠标命中测试按它算卡片位置；不在会话中为 -1）。</summary>
    public int KeyStripIndex
    {
        get { lock (_gate) return _keySession ? _stripIndex : -1; }
    }

    /// <summary>
    /// Alt+Tab 时鼠标悬停在第 <paramref name="index"/> 项上：选中它，但切换条不滚动（滚过去的话指针下换成了别的卡片，会一直滚下去），
    /// 只有选中框移过去。不在键盘会话中、下标越界返回 false。
    /// </summary>
    public bool KeyHover(int index)
    {
        lock (_gate)
        {
            if (!_keySession || index < 0 || index >= _scene.Count) return false;
            _keyIndex = index;
            return true;
        }
    }

    /// <summary>
    /// 键盘会话中场景换了（Alt+Tab 里按 Delete 关掉了选中的窗口，宿主已重新准备场景并调用过 <see cref="SetScene"/>）：
    /// 选中项留在原来的位置（现在是下一个窗口），切换条随之回到它。窗口都没了就收起。
    /// </summary>
    public void KeyAfterRemove(long hostUs)
    {
        bool hide = false;
        lock (_gate)
        {
            if (!_keySession) return;
            int n = _scene.Count;
            if (n == 0)
            {
                _keySession = false;
                _visible = false;
                _transition.SnapTo(0);
                hide = true;
            }
            else
            {
                _keyIndex = _stripIndex = Math.Min(_keyIndex, n - 1);
                _transition.AnimateTo(_keyIndex, hostUs);
            }
        }
        if (hide) _host.Hide(null);
    }

    /// <summary>
    /// 松开 Alt（或按回车）：切换到选中的窗口。切换条还没淡入（快速按一下）就直接切过去、立刻撤层；选中的是当前窗口也立刻撤层；
    /// 否则选中的缩略图从切换条飞回窗口原位，落定时切换。
    /// </summary>
    public void KeyCommit(long hostUs) => EndKeySession(commit: true, hostUs);

    /// <summary>Esc：不切换，直接撤层。</summary>
    public void KeyCancel(long hostUs) => EndKeySession(commit: false, hostUs);

    private void EndKeySession(bool commit, long hostUs)
    {
        bool hideNow = false;
        long? activate = null, launching = null;
        lock (_gate)
        {
            if (!_keySession) return;
            _keySession = false;
            int index = commit ? _keyIndex : 0;
            // 还没淡入（快速按一下），或者选回了当前窗口 / 取消了：直接撤层。当前窗口本来就在原位，
            // 再让它从切换条飞回去像是又打开了一次（三指手势滑回原处时卡片跟手缩回是连续的倒退，那边照旧）
            if (hostUs < _shownAtUs || index == 0)
            {
                hideNow = true;
                activate = index > 0 ? _scene.Windows[index].Id : null;
                _visible = false;
                _transition.SnapTo(0);
            }
            else
            {
                _launchIndex = index;
                _launch.SnapTo(0);
                _launch.AnimateTo(1, hostUs);
                if (index > 0) launching = _scene.Windows[index].Id;
            }
        }
        if (hideNow) _host.Hide(activate);
        if (launching is { } id) _host.Launching(id);
    }

    /// <summary>
    /// 飞回还没落定时立刻结束它（连按 Alt+Tab：下一次要从新的前台窗口算起）：返回 true 并给出要切到前台的窗口，
    /// 撤层由调用方同步完成（不再调用 <see cref="ISwitcherHost.Hide"/>）。没有在飞回时返回 false。
    /// </summary>
    public bool FinishLaunch(out long? activate)
    {
        lock (_gate)
        {
            activate = null;
            if (_launchIndex < 0 || _tracking || _keySession || !_visible) return false;
            activate = _launchIndex > 0 ? _scene.Windows[_launchIndex].Id : null;
            _launchIndex = -1;
            _launch.SnapTo(0);
            _visible = false;
            _transition.SnapTo(0);
            return true;
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
        int keyIndex, ringIndex;
        lock (_gate)
        {
            if (_launchIndex < 0 && s.JustSettled && _visible && !_tracking && !_keySession)
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
            keyIndex = _keySession ? _keyIndex : -1;
            ringIndex = _keySession && _keyIndex != _stripIndex ? _keyIndex : -1;
            if (hide)
            {
                _launchIndex = -1;
                _launch.SnapTo(0);
            }
        }
        if (hide) _host.Hide(activate);
        // 键盘切换时标题直接是按到的那一项（弹簧经过中间各项时不让标题跟着闪）
        int selected = keyIndex >= 0 ? keyIndex : scene.Layout.Nearest(position);
        return new SwitcherFrame(position, selected, appear, s.Phase, visible, launch, launchIndex, ringIndex);
    }
}
