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
/// </summary>
public sealed unsafe class SwitcherPresenter : ISwitcherHost, IDisposable
{
    private readonly UiThread _ui;
    private readonly RenderLoop _render;
    private SwitcherController? _controller;
    private SwitcherBand? _band;
    private volatile Thumbs _thumbs = Thumbs.Empty;
    private volatile bool _shown;
    private bool _prepared;
    private volatile int _labelFor = -1;

    public SwitcherPresenter(UiThread ui)
    {
        _ui = ui;
        _render = new RenderLoop(RenderFrame, stats => Log.Info($"切换条渲染：{stats}"));
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
        _band?.HideBand();
        Release();
        if (activateWindowId is { } id && !WindowActivator.Activate((nint)id))
            Log.Warn($"无法把窗口 0x{id:X} 置为前台");
    });

    public void Dispose()
    {
        _render.Dispose();
        _thumbs.Unregister();
        _band?.Dispose();
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
            _band?.Dispose();
            _band = new SwitcherBand(monitor, bandRect);
        }
        var scene = new SwitcherScene(windows, layout);
        var old = _thumbs;
        _thumbs = Thumbs.Register(_band.Handle, bandRect, scene);
        old.Unregister();
        _band.SetScene(scene);
        _labelFor = -1;
        Controller.SetScene(scene, HostClock.NowUs());
        _prepared = true;
        Log.Info($"切换条预热：{windows.Count} 个窗口，耗时 {(HostClock.NowUs() - t0) / 1000.0:F1}ms");
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
        if (_shown) _thumbs.Apply(scene, frame);
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
                sources[i] = ThumbnailLayer.VisibleSource(hwnd);
                nint thumb;
                if (Dwmapi.DwmRegisterThumbnail(band, hwnd, &thumb) == 0) thumbs[i] = thumb;
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

    public void SetScene(SwitcherScene scene)
    {
        _scene = scene;
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
            if (_selected < 0 || _selected >= _scene.Count) return;

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
