using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Core.Switcher;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;
using GlideShell.Platform.Overview;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;

namespace GlideShell.Platform.Switcher;

/// <summary>
/// 窗口切换条的 Windows 实现（<see cref="ISwitcherHost"/>）：前台窗口所在显示器中部的一行窗口缩略图，随连续位置平移，选中项标题在下方。
/// 共享缩略图可用时与总览同一套画法：整块屏轻轻变暗，缩略图是带窗口阴影的共享缩略图视觉、越靠近屏幕两端越淡，
/// 标题、程序图标与选中框在合成层里逐帧跟手（<see cref="SwitcherDecor"/>）；
/// 否则退回经典 DWM 缩略图 + GDI 标题，画在一条圆角横带里（系统亚克力背景，切换条不激活时实际是深灰）。
/// </summary>
public sealed unsafe class SwitcherPresenter : ISwitcherHost, IDisposable
{
    private readonly UiThread _ui;
    private readonly SharedThumbnailSource? _shared;
    private readonly Func<double> _cornerRadius;
    private volatile float _radiusPx;
    private readonly RenderLoop _render;

    /// <summary>最近一次渲染会话的帧统计与刷新周期（诊断用）。</summary>
    public FrameStats LastRender => _render.LastSession;

    public double RefreshPeriodMs => _render.RefreshPeriodMs;
    private OverlayComposition? _composition; // 横带窗口的合成目标（共享缩略图模式）
    private OverlayComposition? _panelRoot, _content, _decorRoot; // 从下往上：变暗层、缩略图、文字与选中框
    private OverlayComposition? _launchRoot; // 最上面：飞回中的窗口（不裁剪，可以飞到别的屏上）
    private nint _lifted; // 挂到 _launchRoot 上的窗口分组（0 = 没有）
    private volatile SharedThumbnailLayer? _layer;
    private SwitcherDecor? _decor;
    private SharedThumbnailBackend? _backend;
    private RectD[] _rects = [];
    private double[] _opacity = [], _active = [];
    private SwitcherController? _controller;
    private SwitcherBand? _band;
    private volatile Thumbs _thumbs = Thumbs.Empty;
    private volatile bool _shown;
    private bool _prepared;
    private volatile int _labelFor = -1;

    /// <param name="cornerRadius">缩略图卡片的圆角（DIP，与总览同一个配置）。</param>
    public SwitcherPresenter(UiThread ui, SharedThumbnailSource? shared = null, Func<double>? cornerRadius = null)
    {
        _ui = ui;
        _shared = shared;
        _cornerRadius = cornerRadius ?? (() => 0);
        _render = new RenderLoop(RenderFrame, stats => Log.Info($"切换条渲染：{stats}，刷新周期 {_render?.RefreshPeriodMs:F2}ms"));
    }

    public SwitcherController Controller => _controller ?? throw new InvalidOperationException("先调用 Attach");

    public void Attach(SwitcherController controller) => _controller = controller;

    public IReadOnlyCollection<nint> OwnWindows => _band is { Handle: not 0 } b ? [b.Handle] : [];

    // ---------- ISwitcherHost ----------

    public void Prewarm() => _ui.Post(Prepare);

    public void CancelPrewarm() => _ui.Post(() =>
    {
        if (!_shown) Release();
    });

    public void Show() => _ui.Post(() =>
    {
        if (!_prepared) Prepare();
        _band?.SetClickThrough(!Controller.IsKeySession);
        StartPointer();
        _band?.ShowBand();
        _shown = true;
        _render.Wake();
    });

    /// <summary>
    /// 松手：目标窗口最小化着的话，现在就悄悄还原（不激活、不播系统动画）并压到最下层，让程序在飞回的 0.4 秒里重绘好；
    /// 落定时只需提到前台。否则落定时才还原，程序重绘前的空白 / 旧画面会闪一下。
    /// </summary>
    public void Launching(long windowId) => _ui.Post(() =>
    {
        // 缩略图一支裁在本屏里（两端滑出去的项不能露到隔壁屏上），飞回的窗口挂到不裁剪的最上一支，才能飞到别的屏上的原位
        var scene = Controller.Scene;
        int index = Enumerable.Range(0, scene.Count).FirstOrDefault(i => scene.Windows[i].Id == windowId, -1);
        if (_layer is { } layer && _launchRoot is not null && index >= 0 && _lifted == 0) _lifted = layer.Lift(index, _launchRoot);

        nint hwnd = (nint)windowId;
        if (!User32.IsWindow(hwnd) || !User32.IsIconic(hwnd)) return;
        WindowActivator.RestoreBehind(hwnd);
        _ui.PostDelayed(1500, () => WindowActivator.EnableTransitions(hwnd));
    });

    public void Hide(long? activateWindowId) => _ui.Post(() => HideNow(activateWindowId));

    /// <summary>UI 线程：键盘会话开始前同步准备场景（按下 Alt+Tab 那一刻的窗口与前台）。</summary>
    public void PrepareNow() => Prepare();

    /// <summary>UI 线程：同步撤层并把 <paramref name="activateWindowId"/> 切到前台（连按 Alt+Tab 时提前结束上一次的飞回）。</summary>
    public void HideNow(long? activateWindowId)
    {
        // 合成层模式下选中的缩略图已经飞到窗口原位、盖在它上面：先在覆盖层下面把窗口切到前台（最小化的不播系统动画直接还原），
        // 等 DWM 合成一帧再撤层，画面无缝衔接
        bool flew = _layer is not null;
        bool activated = activateWindowId is not { } target || WindowActivator.Activate((nint)target, instant: true);
        if (!activated) Log.Warn($"无法把窗口 0x{activateWindowId:X} 置为前台");
        if (flew && activateWindowId is not null) Dwmapi.DwmFlush();
        _shown = false;
        _thumbs.HideAll();
        _decor?.HideAll(); // 装饰只改属性不提交，接着缩略图层的 HideAll 一起提交
        _layer?.HideAll();
        PutBack();
        _band?.SetClickThrough(true);
        _band?.HideBand();
        _closing.Clear();
        Release();
    }

    // ---------- Alt+Tab：鼠标与 Delete ----------

    private const double PointerSlopPx = 4;
    private User32.POINT _pointerStart;
    private bool _pointerMoved;
    private readonly HashSet<long> _closing = []; // 这次会话里按 Delete 关掉的窗口：重新准备场景时不再列出

    /// <summary>会话开始：记下指针位置。切换条出现在静止的指针下面时系统也会发一次移动，真的动了才算悬停。</summary>
    private void StartPointer()
    {
        User32.POINT p;
        User32.GetCursorPos(&p);
        _pointerStart = p;
        _pointerMoved = false;
    }

    /// <summary>UI 线程：切换条窗口上的鼠标（只在 Alt+Tab 时才收得到）。悬停选中、点卡片切过去、点空白处取消。</summary>
    private void OnPointer(bool click, double x, double y)
    {
        if (!Controller.IsKeySession) return;
        if (!click && !_pointerMoved)
        {
            if (Math.Abs(x - _pointerStart.X) < PointerSlopPx && Math.Abs(y - _pointerStart.Y) < PointerSlopPx) return;
            _pointerMoved = true;
        }
        int hit = CardAt(x, y);
        long now = HostClock.NowUs();
        if (!click)
        {
            if (hit >= 0) Controller.KeyHover(hit);
            return;
        }
        if (hit >= 0)
        {
            Controller.KeyHover(hit);
            Controller.KeyCommit(now);
        }
        else Controller.KeyCancel(now);
    }

    /// <summary>指针下的卡片（按切换条居中的那一项算位置；两端淡得看不清的不算），没有返回 -1。</summary>
    private int CardAt(double x, double y)
    {
        var scene = Controller.Scene;
        int strip = Controller.KeyStripIndex;
        if (strip < 0) return -1;
        for (int i = 0; i < scene.Count; i++)
        {
            var r = scene.Layout.ItemRect(i, strip);
            if (r.Contains(x, y) && scene.Layout.EdgeFade(r) > 0.3) return i;
        }
        return -1;
    }

    /// <summary>
    /// UI 线程：Alt+Tab 里按 Delete，关闭选中的窗口（像点它自己的「×」一样发 SC_CLOSE）。之后每 50ms 看一次：
    /// 窗口没了就从切换条上移走、选中项落到下一个；弹出了确认框（例如「是否保存」）或 1.5 秒后还在，就切过去让用户看到它在问什么。
    /// </summary>
    public void CloseSelected()
    {
        if (Controller.KeySelectedWindow is not { } id || _closing.Contains(id)) return;
        User32.PostMessageW((nint)id, 0x0112 /* WM_SYSCOMMAND */, 0xF060 /* SC_CLOSE */, 0);
        Log.Info($"切换条：关闭窗口 0x{id:X}");
        WatchClose(id, Environment.TickCount64 + 1500);
    }

    private void WatchClose(long id, long deadline) => _ui.PostDelayed(50, () =>
    {
        if (!_shown || !Controller.IsKeySession) return;
        nint hwnd = (nint)id;
        if (!User32.IsWindow(hwnd) || !User32.IsWindowVisible(hwnd))
        {
            _closing.Add(id);
            Prepare(); // 按此刻的窗口重新准备（不含关掉的），缩略图注册尽量复用
            Controller.KeyAfterRemove(HostClock.NowUs());
            if (_shown && Controller.IsKeySession)
            {
                _band?.SetClickThrough(false); // 横带可能换了一个（铺的屏变了）
                _band?.ShowBand();
            }
            return;
        }
        nint popup = User32.GetWindow(hwnd, 6 /* GW_ENABLEDPOPUP */);
        if ((popup != 0 && popup != hwnd) || Environment.TickCount64 >= deadline)
        {
            Log.Info($"切换条：窗口 0x{id:X} 没有直接关掉（{(popup != 0 && popup != hwnd ? "弹出了对话框" : "超时")}），切过去");
            var scene = Controller.Scene;
            int index = Enumerable.Range(0, scene.Count).FirstOrDefault(i => scene.Windows[i].Id == id, -1);
            if (index >= 0 && Controller.KeyHover(index)) Controller.KeyCommit(HostClock.NowUs());
            return;
        }
        WatchClose(id, deadline);
    });

    /// <summary>飞回的窗口放回缩略图一支（下次会话复用这一层）。</summary>
    private void PutBack()
    {
        if (_lifted != 0 && _launchRoot is not null) _layer?.PutBack(_lifted, _launchRoot);
        _lifted = 0;
    }

    public void Dispose()
    {
        _render.Dispose();
        _thumbs.Unregister();
        DisposeComposition();
        _band?.Dispose();
    }

    private void DisposeComposition()
    {
        PutBack();
        _layer?.Unregister();
        _layer = null;
        _decor?.Dispose();
        _decor = null;
        _launchRoot?.Dispose();
        _panelRoot?.Dispose();
        _content?.Dispose();
        _decorRoot?.Dispose();
        _composition?.Dispose();
        _panelRoot = _content = _decorRoot = _launchRoot = _composition = null;
    }

    // ---------- UI 线程 ----------

    private void Prepare()
    {
        long t0 = HostClock.NowUs();
        var monitors = Monitors.Enumerate();
        if (monitors.Count == 0) return;
        var windows = WindowCatalog.Snapshot(monitors, OwnWindows.ToHashSet());
        if (_closing.Count > 0) windows.RemoveAll(w => _closing.Contains(w.Id));
        // 第 0 项必须是当前前台窗口（置顶窗口在 Z 序里可能排在它前面）
        int fg = windows.FindIndex(w => w.IsForeground);
        if (fg > 0)
        {
            var w = windows[fg];
            windows.RemoveAt(fg);
            windows.Insert(0, w);
        }
        var monitor = windows.Count > 0
            ? monitors[Math.Clamp(windows[0].MonitorIndex, 0, monitors.Count - 1)]
            : monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        var layout = SwitcherLayout.Create(windows.Select(w => (w.Source.Width, w.Source.Height)).ToList(), monitor.WorkArea, monitor.DpiScale);
        var bandRect = layout.BandRect(monitor.DpiScale);
        // 合成层可用时横带窗口本身完全透明（没有 GDI 表面），合成层里整块屏轻轻变暗、缩略图浮在上面
        bool wantComposed = _shared?.Get() is not null && _shared.Decor(_ui) is not null;
        // 合成层模式下横带窗口铺满工作区；别的屏上也有窗口时连那几块屏一起铺上——选中的窗口松手后要飞回它在别的屏上的原位，
        // 只铺本屏的话飞到屏幕边上就被裁掉了（双屏真机）
        var span = monitors.Where(m => m == monitor || windows.Any(w => m.WorkArea.Intersects(w.Source)))
            .Aggregate(monitor.WorkArea, (r, m) => RectD.Union(r, m.WorkArea));
        if (_band is null || _band.Rect != bandRect || _band.Monitor.DpiScale != monitor.DpiScale || _band.Composed != wantComposed ||
            (wantComposed && _band.WindowRect != span))
            RecreateBand(monitor, bandRect, wantComposed ? span : bandRect);
        var scene = new SwitcherScene(windows, layout);
        var old = _thumbs;
        bool composed = wantComposed && PrepareComposition(scene, monitor, _band!.WindowRect, bandRect);
        if (!composed && _band!.Composed) RecreateBand(monitor, bandRect, bandRect); // 合成层失败：退回经典缩略图 + GDI
        _thumbs = composed ? Thumbs.Empty : Thumbs.Register(_band!.Handle, bandRect, scene);
        old.Unregister();
        _band!.SetScene(scene, drawTitle: !composed);
        _labelFor = -1;
        Controller.SetScene(scene, HostClock.NowUs());
        _prepared = true;
        Log.Info($"切换条预热：{windows.Count} 个窗口，耗时 {(HostClock.NowUs() - t0) / 1000.0:F1}ms");
    }

    /// <param name="windowRect">横带窗口的位置：等于 <paramref name="bandRect"/> 时是经典模式（窗口就是横带），否则是合成层模式（透明，铺满）。</param>
    private void RecreateBand(MonitorInfo monitor, RectD bandRect, RectD windowRect)
    {
        DisposeComposition();
        _band?.Dispose();
        _band = new SwitcherBand(monitor, bandRect, windowRect == bandRect ? null : windowRect) { OnPointer = OnPointer };
    }

    /// <summary>共享缩略图 + 合成层装饰；不可用或失败时返回 false（用经典缩略图）。</summary>
    /// <param name="windowRect">横带窗口在屏幕上的位置（合成层模式下是整个工作区或几块屏，缩略图才能飞出横带）。</param>
    private bool PrepareComposition(SwitcherScene scene, MonitorInfo monitor, RectD windowRect, RectD bandRect)
    {
        var backend = _shared?.Get();
        var res = _shared?.Decor(_ui);
        if (backend is null || res is null || _band is null)
        {
            DisposeComposition();
            return false;
        }
        int hr = 0;
        if (_backend != backend) DisposeComposition();
        _backend = backend;
        _composition ??= OverlayComposition.Create(backend, _band.Handle, out hr);
        if (_composition is not null) _panelRoot ??= _composition.CreateBranch(out hr);
        if (_panelRoot is not null) _content ??= _composition!.CreateBranch(out hr);
        if (_content is not null) _decorRoot ??= _composition!.CreateBranch(out hr);
        if (_decorRoot is not null) _launchRoot ??= _composition!.CreateBranch(out hr);
        PutBack(); // 上一次会话没走撤层就重新准备（不该发生）：飞回的窗口也先放回去
        // 窗口铺满几块屏时，缩略图只露在切换条所在的这块屏上（切换条两端滑出去的项不能出现在隔壁屏）
        _content?.SetClip(monitor.WorkArea.Left - windowRect.Left, monitor.WorkArea.Top - windowRect.Top,
            monitor.WorkArea.Right - windowRect.Left, monitor.WorkArea.Bottom - windowRect.Top);
        var layer = _launchRoot is null ? null : SharedThumbnailLayer.Register(backend, _content!, _band.Handle, windowRect, monitor.DpiScale,
            scene.Windows.Select((w, i) => (i, (nint)w.Id)).ToList(), _layer, out hr, withWallpaper: false);
        _layer = layer;
        if (layer is null)
        {
            _shared!.Disable(hr == 0 ? unchecked((int)0x80004005) : hr, "切换条");
            DisposeComposition();
            return false;
        }
        double radiusDip = _cornerRadius();
        _radiusPx = (float)(radiusDip * monitor.DpiScale);
        if (_decor is not null && _decor.CardRadiusDip != radiusDip)
        {
            _decor.Dispose();
            _decor = null;
        }
        _decor ??= new SwitcherDecor(res, _decorRoot!, _panelRoot!, monitor, windowRect, radiusDip);
        _decor.Prepare(scene);
        if (_rects.Length < scene.Count)
        {
            _rects = new RectD[scene.Count];
            _opacity = new double[scene.Count];
            _active = new double[scene.Count];
        }
        return true;
    }

    private void Release()
    {
        var old = _thumbs;
        _thumbs = Thumbs.Empty;
        old.Unregister();
        _prepared = false;
    }

    // ---------- 渲染线程 ----------

    private bool RenderFrame(long nowUs)
    {
        var frame = Controller.Sample(nowUs);
        // 只画可见的帧：落定那一帧之后控制器已复位（位置回到 0、飞回进度清零），UI 线程撤层前若再按复位后的状态画一帧，
        // 整条切换条会重新出现一下（真机：切换之后闪一下切换条）
        if (!frame.Visible) return false;
        var scene = Controller.Scene;
        if (_shown)
        {
            if (_layer is { } layer && _backend is { } backend && _rects.Length >= scene.Count)
            {
                int n = scene.Count;
                double launch = frame.LaunchIndex >= 0 ? frame.Launch : 0;
                for (int i = 0; i < n; i++)
                {
                    _rects[i] = scene.Layout.ItemRect(i, frame.Position);
                    _opacity[i] = frame.Appear * scene.Layout.EdgeFade(_rects[i]); // 越靠近屏幕两端越淡
                    _active[i] = Math.Clamp(1 - Math.Abs(i - frame.Position), 0, 1); // 居中的那个用活动阴影
                    if (frame.LaunchIndex < 0) continue;
                    if (i == frame.LaunchIndex)
                    {
                        // 飞回：从切换条上的位置飞到窗口的真实位置与大小（落定时与窗口重合，撤层无跳变）
                        _rects[i] = RectD.Lerp(_rects[i], scene.Windows[i].Source, launch);
                        _opacity[i] = frame.Appear; // 飞向别的屏上的原位时不能按离屏幕中央的距离淡掉
                        _active[i] = 1;
                    }
                    else _opacity[i] *= 1 - OverviewVisuals.SmoothStep(0, 0.6, launch);
                }
                // 圆角随飞回从卡片圆角过渡回窗口自己的（落定时与真实窗口一致）
                layer.Apply(_rects.AsSpan(0, n), _opacity.AsSpan(0, n), _active.AsSpan(0, n), 0, commit: false, corner: 1 - launch, cardRadiusPx: _radiusPx);
                _decor?.Apply(frame);
                backend.Commit();
            }
            else _thumbs.Apply(scene, frame);
        }
        int selected = frame.Selected;
        if (selected != _labelFor)
        {
            _labelFor = selected; // 只在选中项变化时请 UI 线程重绘标题
            _ui.Post(() => _band?.SetSelected(selected));
        }
        return frame.Visible;
    }

    /// <summary>一次会话的缩略图（与场景窗口同序）。不可变，UI 线程整体替换。</summary>
    private sealed class Thumbs
    {
        public static Thumbs Empty { get; } = new([], [], default);

        private readonly nint[] _thumbs;
        private readonly User32.RECT[] _sources;
        private readonly RectD _origin;

        private Thumbs(nint[] thumbs, User32.RECT[] sources, RectD origin)
        {
            _thumbs = thumbs;
            _sources = sources;
            _origin = origin;
        }

        public static Thumbs Register(nint band, RectD bandRect, SwitcherScene scene)
        {
            int n = scene.Count;
            var thumbs = new nint[n];
            var sources = new User32.RECT[n];
            for (int i = 0; i < n; i++)
            {
                nint hwnd = (nint)scene.Windows[i].Id;
                nint thumb;
                if (Dwmapi.DwmRegisterThumbnail(band, hwnd, &thumb) != 0) continue;
                var (source, hasContent) = ThumbnailLayer.ThumbnailSource(hwnd, thumb);
                if (!hasContent)
                {
                    Dwmapi.DwmUnregisterThumbnail(thumb); // 没有画面的最小化窗口只留标题
                    continue;
                }
                thumbs[i] = thumb;
                sources[i] = source;
            }
            var t = new Thumbs(thumbs, sources, bandRect);
            t.HideAll();
            return t;
        }

        public void Apply(SwitcherScene scene, in SwitcherFrame frame)
        {
            var props = new Dwmapi.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Dwmapi.DWM_TNP_RECTDESTINATION | Dwmapi.DWM_TNP_RECTSOURCE | Dwmapi.DWM_TNP_OPACITY | Dwmapi.DWM_TNP_VISIBLE,
                fVisible = 1,
                opacity = (byte)Math.Round(255 * frame.Appear),
            };
            int n = Math.Min(scene.Count, _thumbs.Length);
            for (int i = 0; i < n; i++)
            {
                if (_thumbs[i] == 0) continue;
                var r = scene.Layout.ItemRect(i, frame.Position);
                props.rcSource = _sources[i];
                props.rcDestination = new User32.RECT
                {
                    Left = (int)Math.Round(r.Left - _origin.Left), Top = (int)Math.Round(r.Top - _origin.Top),
                    Right = (int)Math.Round(r.Right - _origin.Left), Bottom = (int)Math.Round(r.Bottom - _origin.Top),
                };
                Dwmapi.DwmUpdateThumbnailProperties(_thumbs[i], &props);
            }
        }

        public void HideAll()
        {
            var props = new Dwmapi.DWM_THUMBNAIL_PROPERTIES { dwFlags = Dwmapi.DWM_TNP_VISIBLE, fVisible = 0 };
            foreach (var t in _thumbs) if (t != 0) Dwmapi.DwmUpdateThumbnailProperties(t, &props);
        }

        public void Unregister()
        {
            foreach (var t in _thumbs) if (t != 0) Dwmapi.DwmUnregisterThumbnail(t);
        }
    }
}

/// <summary>
/// 切换条横带：不激活、鼠标穿透。
/// 合成层模式（<see cref="Composed"/>）下窗口本身完全透明（WS_EX_NOREDIRECTIONBITMAP），背景、缩略图、标题都在合成层里；
/// 否则是圆角窗口 + 系统亚克力背景（只在窗口激活时才画，切换条从不激活，实际是深灰），GDI 画选中项标题。
/// </summary>
internal sealed unsafe class SwitcherBand : NativeWindow
{
    private SwitcherScene _scene = SwitcherScene.Empty;
    private int _selected = -1;
    private bool _drawTitle = true;
    private readonly bool _acrylic;

    /// <param name="composedRect">非空 = 合成层模式，窗口铺在这里（透明）：选中的缩略图松手后才能飞出横带、飞回窗口原位。</param>
    public SwitcherBand(MonitorInfo monitor, RectD rect, RectD? composedRect = null)
    {
        Monitor = monitor;
        Rect = rect;
        bool composed = Composed = composedRect is not null;
        WindowRect = composedRect ?? rect;
        var w = WindowRect;
        Create("GlideShell.Switcher", User32.WS_POPUP,
            User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW_U | User32.WS_EX_NOACTIVATE_U | 0x20 /* WS_EX_TRANSPARENT：鼠标穿透 */ |
            (composed ? 0x00200000u /* WS_EX_NOREDIRECTIONBITMAP */ : 0),
            (int)w.Left, (int)w.Top, (int)w.Width, (int)w.Height);
        if (composed) return;

        int round = Dwmapi.DWMWCP_ROUND, dark = 1;
        Dwmapi.DwmSetWindowAttribute(Handle, Dwmapi.DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int));
        Dwmapi.DwmSetWindowAttribute(Handle, Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(int));
        // 亚克力：整个客户区延伸为「玻璃」，系统在其后绘制模糊背景；黑色像素在玻璃区域里是透明的
        int backdrop = Dwmapi.DWMSBT_TRANSIENTWINDOW;
        var margins = new Dwmapi.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _acrylic = Dwmapi.DwmExtendFrameIntoClientArea(Handle, &margins) == 0 &&
                   Dwmapi.DwmSetWindowAttribute(Handle, Dwmapi.DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(int)) == 0;
    }

    /// <summary>合成层模式：窗口本身透明，背景与文字都在合成层里。</summary>
    public bool Composed { get; }

    public MonitorInfo Monitor { get; }

    /// <summary>横带（面板）在屏幕上的位置。</summary>
    public RectD Rect { get; }

    /// <summary>窗口在屏幕上的位置：合成层模式下是整个工作区（别的屏上有窗口时连那几块屏一起），否则就是横带。</summary>
    public RectD WindowRect { get; }

    /// <param name="drawTitle">GDI 画选中项标题（合成层画标题时为 false）。</param>
    public void SetScene(SwitcherScene scene, bool drawTitle = true)
    {
        _scene = scene;
        _drawTitle = drawTitle;
        _selected = -1;
        User32.InvalidateRect(Handle, null, 1);
    }

    public void SetSelected(int index)
    {
        if (index == _selected) return;
        _selected = index;
        User32.InvalidateRect(Handle, null, 1);
    }

    public void ShowBand() => User32.SetWindowPos(Handle, User32.HWND_TOPMOST, (int)WindowRect.Left, (int)WindowRect.Top, (int)WindowRect.Width,
        (int)WindowRect.Height, User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);

    public void HideBand() => User32.ShowWindow(Handle, User32.SW_HIDE);

    /// <summary>鼠标点击（false 为移动），屏幕坐标。只在不穿透时（Alt+Tab）收得到。</summary>
    public Action<bool, double, double>? OnPointer { get; init; }

    /// <summary>鼠标穿透（三指滑动时）还是接收鼠标（Alt+Tab 时：悬停选中、点击切换）。</summary>
    public void SetClickThrough(bool through)
    {
        const nint Transparent = 0x20; // WS_EX_TRANSPARENT
        nint style = User32.GetWindowLongPtrW(Handle, User32.GWL_EXSTYLE);
        nint next = through ? style | Transparent : style & ~Transparent;
        if (next != style) User32.SetWindowLongPtrW(Handle, User32.GWL_EXSTYLE, next);
    }

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case User32.WM_MOUSEACTIVATE:
                return User32.MA_NOACTIVATE;
            case User32.WM_MOUSEMOVE:
            case User32.WM_LBUTTONDOWN:
                OnPointer?.Invoke(msg == User32.WM_LBUTTONDOWN, WindowRect.Left + (short)(lParam & 0xFFFF), WindowRect.Top + (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case User32.WM_ERASEBKGND:
                return 1;
            case User32.WM_PAINT:
                Paint();
                return 0;
        }
        return null;
    }

    private void Paint()
    {
        User32.PAINTSTRUCT ps;
        nint hdc = User32.BeginPaint(Handle, &ps);
        try
        {
            nint bg = Gdi32.CreateSolidBrush(_acrylic ? 0 : Gdi32.Rgb(32, 32, 36));
            User32.FillRect(hdc, &ps.rcPaint, bg);
            Gdi32.DeleteObject(bg);
            if (!_drawTitle || _selected < 0 || _selected >= _scene.Count) return;

            var label = _scene.Layout.LabelRect(Monitor.DpiScale);
            var r = new User32.RECT
            {
                Left = (int)(label.Left - WindowRect.Left), Top = (int)(label.Top - WindowRect.Top),
                Right = (int)(label.Right - WindowRect.Left), Bottom = (int)(label.Bottom - WindowRect.Top),
            };
            nint font = Gdi32.CreateFontW(-(int)Math.Round(16 * Monitor.DpiScale), 0, 0, 0, Gdi32.FW_NORMAL, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            nint oldFont = Gdi32.SelectObject(hdc, font);
            Gdi32.SetBkMode(hdc, Gdi32.TRANSPARENT);
            Gdi32.SetTextColor(hdc, Gdi32.Rgb(240, 240, 245));
            User32.DrawTextW(hdc, _scene.Windows[_selected].Title, -1, &r,
                User32.DT_CENTER | User32.DT_VCENTER | User32.DT_SINGLELINE | User32.DT_END_ELLIPSIS | User32.DT_NOPREFIX);
            Gdi32.SelectObject(hdc, oldFont);
            Gdi32.DeleteObject(font);
        }
        finally
        {
            User32.EndPaint(Handle, &ps);
        }
    }
}
