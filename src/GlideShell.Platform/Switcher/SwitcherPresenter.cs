using GlideShell.Core.Geometry;
using GlideShell.Core.Switcher;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;
using GlideShell.Platform.Overview;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;

namespace GlideShell.Platform.Switcher;

/// <summary>
/// 窗口切换条的 Windows 实现（<see cref="ISwitcherHost"/>）：前台窗口所在显示器中部的一条横带
/// （Windows 11 系统亚克力背景、圆角），窗口缩略图在其中随连续位置平移，选中项标题在下方。
/// 横带之外不遮挡——背后是实时的桌面，和 Alt+Tab 一样。
/// 共享缩略图可用时与总览同一套画法：缩略图是带窗口阴影的共享缩略图视觉，标题、程序图标与选中框在合成层里逐帧跟手
/// （<see cref="SwitcherDecor"/>）；否则退回经典 DWM 缩略图 + GDI 标题。
/// </summary>
public sealed unsafe class SwitcherPresenter : ISwitcherHost, IDisposable
{
    private readonly UiThread _ui;
    private readonly SharedThumbnailSource? _shared;
    private readonly Func<double> _cornerRadius;
    private volatile float _radiusPx;
    private readonly RenderLoop _render;
    private OverlayComposition? _composition; // 横带窗口的合成目标（共享缩略图模式）
    private OverlayComposition? _content, _decorRoot;
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
        _band?.ShowBand();
        _shown = true;
        _render.Wake();
    });

    public void Hide(long? activateWindowId) => _ui.Post(() =>
    {
        _shown = false;
        _thumbs.HideAll();
        _layer?.HideAll();
        _decor?.HideAll();
        _band?.HideBand();
        Release();
        if (activateWindowId is { } id && !WindowActivator.Activate((nint)id))
            Log.Warn($"无法把窗口 0x{id:X} 置为前台");
    });

    public void Dispose()
    {
        _render.Dispose();
        _thumbs.Unregister();
        DisposeComposition();
        _band?.Dispose();
    }

    private void DisposeComposition()
    {
        _layer?.Unregister();
        _layer = null;
        _decor?.Dispose();
        _decor = null;
        _content?.Dispose();
        _decorRoot?.Dispose();
        _composition?.Dispose();
        _content = _decorRoot = _composition = null;
    }

    // ---------- UI 线程 ----------

    private void Prepare()
    {
        long t0 = HostClock.NowUs();
        var monitors = Monitors.Enumerate();
        if (monitors.Count == 0) return;
        var windows = WindowCatalog.Snapshot(monitors, OwnWindows.ToHashSet());
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
        if (_band is null || _band.Rect != bandRect || _band.Monitor.DpiScale != monitor.DpiScale)
        {
            DisposeComposition();
            _band?.Dispose();
            _band = new SwitcherBand(monitor, bandRect);
        }
        var scene = new SwitcherScene(windows, layout);
        var old = _thumbs;
        bool composed = PrepareComposition(scene, monitor, bandRect);
        _thumbs = composed ? Thumbs.Empty : Thumbs.Register(_band.Handle, bandRect, scene);
        old.Unregister();
        _band.SetScene(scene, drawTitle: !composed);
        _labelFor = -1;
        Controller.SetScene(scene, HostClock.NowUs());
        _prepared = true;
        Log.Info($"切换条预热：{windows.Count} 个窗口，耗时 {(HostClock.NowUs() - t0) / 1000.0:F1}ms");
    }

    /// <summary>共享缩略图 + 合成层装饰；不可用或失败时返回 false（用经典缩略图）。</summary>
    private bool PrepareComposition(SwitcherScene scene, MonitorInfo monitor, RectD bandRect)
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
        if (_composition is not null) _content ??= _composition.CreateBranch(out hr);
        if (_content is not null) _decorRoot ??= _composition!.CreateBranch(out hr);
        var layer = _decorRoot is null ? null : SharedThumbnailLayer.Register(backend, _content!, _band.Handle, bandRect, monitor.DpiScale,
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
        _decor ??= new SwitcherDecor(res, _decorRoot!, monitor, bandRect, radiusDip);
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
        var scene = Controller.Scene;
        if (_shown)
        {
            if (_layer is { } layer && _backend is { } backend && _rects.Length >= scene.Count)
            {
                int n = scene.Count;
                for (int i = 0; i < n; i++)
                {
                    _rects[i] = scene.Layout.ItemRect(i, frame.Position);
                    _opacity[i] = frame.Appear;
                    _active[i] = Math.Clamp(1 - Math.Abs(i - frame.Position), 0, 1); // 居中的那个用活动阴影
                }
                layer.Apply(_rects.AsSpan(0, n), _opacity.AsSpan(0, n), _active.AsSpan(0, n), 0, commit: false, corner: 1, cardRadiusPx: _radiusPx);
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
/// 切换条横带：圆角，Windows 11 系统亚克力背景（不支持时为深灰），不激活、鼠标穿透。
/// 只画选中项标题（选中项变化时重绘）；缩略图由渲染线程逐帧更新。
/// </summary>
internal sealed unsafe class SwitcherBand : NativeWindow
{
    private SwitcherScene _scene = SwitcherScene.Empty;
    private int _selected = -1;
    private bool _drawTitle = true;
    private readonly bool _acrylic;

    public SwitcherBand(MonitorInfo monitor, RectD rect)
    {
        Monitor = monitor;
        Rect = rect;
        Create("GlideShell.Switcher", User32.WS_POPUP,
            User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW_U | User32.WS_EX_NOACTIVATE_U | 0x20 /* WS_EX_TRANSPARENT：鼠标穿透 */,
            (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height);

        int round = Dwmapi.DWMWCP_ROUND, dark = 1, backdrop = Dwmapi.DWMSBT_TRANSIENTWINDOW;
        Dwmapi.DwmSetWindowAttribute(Handle, Dwmapi.DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int));
        Dwmapi.DwmSetWindowAttribute(Handle, Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(int));
        // 亚克力：整个客户区延伸为「玻璃」，系统在其后绘制模糊背景；黑色像素在玻璃区域里是透明的
        var margins = new Dwmapi.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _acrylic = Dwmapi.DwmExtendFrameIntoClientArea(Handle, &margins) == 0 &&
                   Dwmapi.DwmSetWindowAttribute(Handle, Dwmapi.DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(int)) == 0;
    }

    public MonitorInfo Monitor { get; }

    public RectD Rect { get; }

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

    public void ShowBand() => User32.SetWindowPos(Handle, User32.HWND_TOPMOST, (int)Rect.Left, (int)Rect.Top, (int)Rect.Width, (int)Rect.Height,
        User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);

    public void HideBand() => User32.ShowWindow(Handle, User32.SW_HIDE);

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case User32.WM_MOUSEACTIVATE:
                return User32.MA_NOACTIVATE;
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
                Left = (int)(label.Left - Rect.Left), Top = (int)(label.Top - Rect.Top),
                Right = (int)(label.Right - Rect.Left), Bottom = (int)(label.Bottom - Rect.Top),
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
