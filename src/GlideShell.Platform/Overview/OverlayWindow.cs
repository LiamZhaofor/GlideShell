using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;
using GlideShell.Platform.VirtualDesktops;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 一块显示器的总览覆盖层：非分层（不用 WS_EX_LAYERED，避免整窗混合的开销）、置顶、永不激活
/// （WS_EX_NOACTIVATE + MA_NOACTIVATE：点击不抢焦点，键盘由低级钩子处理）。只覆盖工作区，任务栏保持可见。
/// 窗口自身只画黑底、标题、悬停框与桌面条的桌面名（偶尔重绘）；每帧变化的内容全部是 DWM 缩略图，由渲染线程更新。
/// 共享缩略图模式下合成树分两支：下面是窗口（<see cref="Layer"/>），上面是桌面条（<see cref="DesktopBarView"/>），
/// 放大进入别的桌面时那个桌面的缩略图要盖住全部窗口。
/// 总览里可以翻页（每个桌面一页窗口）：打开时所在桌面的那一页就是 <see cref="Layer"/>（带壁纸，壁纸不动），
/// 其余桌面各一层不带壁纸的缩略图；翻页时各页窗口的矩形整体横向平移。
/// 拖动窗口时它的缩略图分组临时挂到最上面一支（在桌面条上面，拖到桌面条上时不被缩略图挡住），放下落定后放回原来的层次。
/// </summary>
internal sealed unsafe class OverlayWindow : NativeWindow
{
    private readonly Action<OverlayWindow, OverlayMouse, double, double> _onMouse;
    private bool _releasingCapture;
    private OverlayComposition? _dragRoot; // 最上面：拖动中的窗口
    private nint _lifted; // 挂在 _dragRoot 下的分组视觉
    private volatile IThumbnailLayer _layer;
    private OverlayComposition? _composition; // 共享缩略图视觉模式下才创建
    private OverlayComposition? _content; // 其下：窗口缩略图
    private OverlayComposition? _barRoot; // 其下：桌面条（在窗口上面）
    private SharedThumbnailBackend? _shared;
    private readonly DesktopBarView _bar = new();
    private DesktopState _desktops = DesktopState.Empty;
    private bool _barVisible;
    private bool _barExpanded;
    private DesktopBarHit _barHovered = DesktopBarHit.Nothing;
    private OverviewScene _scene = OverviewScene.Empty; // 当前页（标题、悬停框按它画）
    private IReadOnlyList<int> _members = [];
    private readonly Dictionary<Guid, SharedThumbnailLayer> _pageCache = []; // 非起始页的缩略图层，按桌面缓存、跨会话复用
    private volatile Pages? _pages; // 本次会话的各页（没有多页时为 null）

    /// <summary>本次会话的各页。下标 = 桌面下标；<see cref="Home"/> 那一页用 <see cref="Layer"/>，其余用 <see cref="Layers"/>。</summary>
    private sealed class Pages(int home, IThumbnailLayer?[] layers, OverviewScene[] scenes, IReadOnlyList<int>[] members, RectD[][] scratch)
    {
        public int Home { get; } = home;
        public IThumbnailLayer?[] Layers { get; } = layers;
        public OverviewScene[] Scenes { get; } = scenes;
        public IReadOnlyList<int>[] Members { get; } = members;
        public RectD[][] Scratch { get; } = scratch; // 渲染线程：平移后的矩形
        public volatile int Current = home;
    }
    private bool _labelsVisible;
    private int _hovered = -1;

    public OverlayWindow(MonitorInfo monitor, Action<OverlayWindow, OverlayMouse, double, double> onMouse)
    {
        Monitor = monitor;
        _onMouse = onMouse;
        _layer = ThumbnailLayer.Empty(monitor.WorkArea);
        var wa = monitor.WorkArea;
        Create("GlideShell.Overlay", User32.WS_POPUP,
            User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW_U | User32.WS_EX_NOACTIVATE_U,
            (int)wa.Left, (int)wa.Top, (int)wa.Width, (int)wa.Height);
    }

    public MonitorInfo Monitor { get; }

    public IThumbnailLayer Layer => _layer;

    public bool IsShown { get; private set; }

    /// <summary>
    /// UI 线程：为本屏的窗口注册缩略图（不可见），尽量复用上一次的注册。
    /// <paramref name="shared"/> 非空时用共享缩略图视觉；那条路失败就退回经典缩略图并返回失败的 HRESULT（0 = 正常）。
    /// </summary>
    public int Prepare(OverviewScene scene, IReadOnlyList<int> members, SharedThumbnailBackend? shared)
    {
        Unlift();
        _scene = scene;
        _members = members;
        _hovered = -1;
        _labelsVisible = false;
        var windows = members.Select(i => (i, (nint)scene.Windows[i].Id)).ToList();
        int sharedFailure = 0;
        IThumbnailLayer? next = null;
        if (shared is not null)
        {
            if (_layer is ThumbnailLayer classic)
            {
                classic.Unregister(); // 换后端：旧注册不能复用
                _layer = ThumbnailLayer.Empty(Monitor.WorkArea);
            }
            _composition ??= OverlayComposition.Create(shared, Handle, out sharedFailure);
            // 先建的分支在下面：窗口一支、桌面条一支
            if (_composition is not null) _content ??= _composition.CreateBranch(out sharedFailure);
            if (_content is not null) _barRoot ??= _composition!.CreateBranch(out sharedFailure);
            if (_barRoot is not null) _dragRoot ??= _composition!.CreateBranch(out sharedFailure);
            if (_dragRoot is not null)
                next = SharedThumbnailLayer.Register(shared, _content!, Handle, Monitor.WorkArea, Monitor.DpiScale, windows, _layer as SharedThumbnailLayer, out sharedFailure);
            _shared = shared;
            if (next is null && sharedFailure == 0) sharedFailure = unchecked((int)0x80004005);
        }
        if (next is null)
        {
            if (_layer is SharedThumbnailLayer old)
            {
                old.Unregister();
                _layer = ThumbnailLayer.Empty(Monitor.WorkArea);
            }
            next = ThumbnailLayer.Register(Handle, Monitor.WorkArea, windows, _layer as ThumbnailLayer);
        }
        _layer = next;
        User32.InvalidateRect(Handle, null, 1);
        return sharedFailure;
    }

    /// <summary>本次会话有没有多页（共享缩略图模式下才有）。</summary>
    public bool HasPages => _pages is not null;

    /// <summary>
    /// UI 线程：为其余桌面的页注册缩略图（不可见；当前桌面那一页已由 <see cref="Prepare"/> 注册）。
    /// <paramref name="members"/>[k] 是第 k 页里画在本屏的窗口（场景下标）。只在共享缩略图模式下可用；失败返回 HRESULT。
    /// </summary>
    public int PreparePages(IReadOnlyList<OverviewScene> scenes, int home, IReadOnlyList<IReadOnlyList<int>> members, IReadOnlyList<Guid> desktops)
    {
        Unlift();
        _pages = null;
        if (_layer is not SharedThumbnailLayer || _shared is null || _content is null) return 0;
        foreach (var id in _pageCache.Keys.Where(id => !desktops.Contains(id)).ToList())
        {
            _pageCache[id].Unregister();
            _pageCache.Remove(id);
        }
        var layers = new IThumbnailLayer?[scenes.Count];
        var scratch = new RectD[scenes.Count][];
        for (int k = 0; k < scenes.Count; k++)
        {
            scratch[k] = new RectD[scenes[k].Count];
            if (k == home) continue;
            var windows = members[k].Select(i => (i, (nint)scenes[k].Windows[i].Id)).ToList();
            _pageCache.TryGetValue(desktops[k], out var previous);
            var layer = SharedThumbnailLayer.Register(_shared, _content, Handle, Monitor.WorkArea, Monitor.DpiScale, windows, previous, out int hr, withWallpaper: false);
            if (layer is null)
            {
                _pageCache.Remove(desktops[k]);
                return hr == 0 ? unchecked((int)0x80004005) : hr;
            }
            _pageCache[desktops[k]] = layer;
            layers[k] = layer;
        }
        _pages = new Pages(home, layers, [.. scenes], [.. members], scratch);
        return 0;
    }

    /// <summary>UI 线程：翻到了第 <paramref name="page"/> 页（标题、悬停框、提层与重排都改按这一页）。</summary>
    public void SetCurrentPage(int page)
    {
        if (_pages is not { } pages || page < 0 || page >= pages.Scenes.Length) return;
        pages.Current = page;
        _scene = pages.Scenes[page];
        _members = pages.Members[page];
        _hovered = -1;
        User32.InvalidateRect(Handle, null, 1);
    }

    /// <summary>UI 线程：桌面条上的当前桌面变了（翻页后），重画高亮。</summary>
    public void SetBarCurrent(int index)
    {
        _desktops = _desktops with { CurrentIndex = index };
        User32.InvalidateRect(Handle, null, 1);
    }

    /// <summary>UI 线程：本次会话不分页（应用窗口视图）。</summary>
    public void ClearPages()
    {
        Unlift();
        HidePages();
        _pages = null;
    }

    /// <summary>UI 线程：第 <paramref name="page"/> 页（没有多页时为 -1）的场景换了（窗口移走后补位，下标不变）。</summary>
    public void ReplaceScene(int page, OverviewScene scene)
    {
        if (_pages is { } pages && page >= 0 && page < pages.Scenes.Length)
        {
            pages.Scenes[page] = scene;
            if (page == pages.Current) _scene = scene;
        }
        else if (page < 0) _scene = scene;
        User32.InvalidateRect(Handle, null, 1);
    }

    /// <summary>
    /// UI 线程：第 <paramref name="page"/> 页加入了一个窗口（拖到了那个桌面），<paramref name="scene"/> 是加入后的场景，
    /// 新窗口是最后一个。本屏画不画它按 <paramref name="mine"/>。失败返回 HRESULT（那一页就不显示它）。
    /// </summary>
    public int AddPageWindow(int page, OverviewScene scene, bool mine)
    {
        if (_pages is not { } pages || page < 0 || page >= pages.Scenes.Length) return 0;
        int index = scene.Count - 1;
        pages.Scenes[page] = scene;
        pages.Scratch[page] = new RectD[scene.Count];
        if (!mine) return 0;
        var layer = (page == pages.Home ? _layer : pages.Layers[page]) as SharedThumbnailLayer;
        var next = layer?.AddWindow(Handle, index, (nint)scene.Windows[index].Id, Monitor.DpiScale, out int hr);
        if (next is null) return layer is null ? 0 : unchecked((int)0x80004005);
        pages.Members[page] = [.. pages.Members[page], index];
        if (page == pages.Home) _layer = next;
        else
        {
            pages.Layers[page] = next;
            foreach (var (id, cached) in _pageCache.ToList())
                if (ReferenceEquals(cached, layer)) _pageCache[id] = next;
        }
        return 0;
    }

    /// <summary>UI 线程：开始拖动当前页的某个窗口，把它挂到最上面一支（不是本屏的窗口、或不是共享缩略图时什么也不做）。</summary>
    public void Lift(int sceneIndex)
    {
        Unlift();
        if (_dragRoot is null || !_members.Contains(sceneIndex) || CurrentLayer is not SharedThumbnailLayer layer) return;
        _lifted = layer.Lift(sceneIndex, _dragRoot);
    }

    /// <summary>UI 线程：拖动落定，放回原来的层次（找它所在的那一层；那一层已经不在了就只摘下来）。</summary>
    public void Unlift()
    {
        if (_lifted == 0 || _dragRoot is null) return;
        nint group = _lifted;
        _lifted = 0;
        IEnumerable<IThumbnailLayer?> layers = [_layer, .. _pages?.Layers ?? [], .. _pageCache.Values];
        foreach (var layer in layers)
            if (layer is SharedThumbnailLayer shared && shared.PutBack(group, _dragRoot)) return;
        _shared?.Detach(_dragRoot, group);
    }

    /// <summary>当前页的缩略图层（提层、重排用）。</summary>
    private IThumbnailLayer CurrentLayer => _pages is { } p && p.Current != p.Home ? p.Layers[p.Current] ?? _layer : _layer;

    private void ReplaceCurrentLayer(IThumbnailLayer layer)
    {
        if (_pages is { } p && p.Current != p.Home)
        {
            var old = p.Layers[p.Current];
            p.Layers[p.Current] = layer;
            // 缓存里也换成新层（旧层的视觉已经转交给它）
            if (layer is SharedThumbnailLayer replaced)
                foreach (var (id, cached) in _pageCache.ToList())
                    if (ReferenceEquals(cached, old)) _pageCache[id] = replaced;
            return;
        }
        _layer = layer;
    }

    /// <summary>本次会话的桌面条几何（没有桌面条时为 null）。</summary>
    public DesktopBarGeometry? Bar => _bar.Geometry;

    /// <summary>
    /// UI 线程：准备桌面条。<paramref name="geometry"/> 为 null 时不显示桌面条；共享缩略图不可用时只画桌面名。
    /// <paramref name="windows"/> 是各桌面在本屏的窗口（Z 序，最上层在前）。返回失败的 HRESULT（0 = 正常）。
    /// </summary>
    public int PrepareBar(DesktopBarGeometry? geometry, DesktopState desktops, IReadOnlyList<OverviewWindow> windows, nint foreground)
    {
        _desktops = desktops;
        _barHovered = DesktopBarHit.Nothing;
        User32.InvalidateRect(Handle, null, 1); // 桌面增删后名字要重画
        bool shared = _layer is SharedThumbnailLayer && _shared is not null && _barRoot is not null;
        return _bar.Prepare(shared ? _shared : null, shared ? _barRoot : null, Handle, Monitor, geometry, desktops, windows, foreground);
    }

    /// <summary>UI 线程：点了第 <paramref name="index"/> 个桌面，它要放大进入，挪到其余桌面上面。</summary>
    public void BeginZoom(int index) => _bar.BringToFront(index);

    /// <summary>
    /// 渲染线程：摆好本帧的窗口与桌面条（共享缩略图模式下一次 Commit，同一帧生效）。返回第一个失败的 HRESULT。
    /// <paramref name="rects"/> 等是当前页（按进度飞行）；有多页时其余各页取 <paramref name="pageRects"/> 等（已在平铺位），
    /// 每页按翻页位置整体横向平移。
    /// </summary>
    public int Render(ReadOnlySpan<RectD> rects, ReadOnlySpan<double> opacity, ReadOnlySpan<double> active, double wallpaper, in OverviewFrame frame,
        RectD[][]? pageRects = null, double[][]? pageOpacity = null, double[][]? pageActive = null)
    {
        if (_layer is SharedThumbnailLayer home && _shared is { } backend && (_bar.HasThumbnails || _pages is not null))
        {
            int hr = 0;
            if (_pages is { } pages && pageRects is not null && pageOpacity is not null && pageActive is not null)
            {
                double gap = 48 * Monitor.DpiScale, width = Monitor.WorkArea.Width;
                for (int k = 0; k < pages.Scenes.Length && k < pageRects.Length; k++)
                {
                    bool current = k == pages.Current;
                    var src = current ? rects : pageRects[k];
                    var op = current ? opacity : pageOpacity[k];
                    var ac = current ? active : pageActive[k];
                    var dst = pages.Scratch[k];
                    int n = Math.Min(dst.Length, Math.Min(src.Length, Math.Min(op.Length, ac.Length)));
                    double dx = OverviewController.PageOffset(k, frame.PagePosition, width, gap);
                    for (int i = 0; i < n; i++) dst[i] = src[i].Offset(dx, 0);
                    var layer = k == pages.Home ? home : pages.Layers[k] as SharedThumbnailLayer;
                    int r = layer?.Apply(dst.AsSpan(0, n), op[..n], ac[..n], k == pages.Home ? wallpaper : 0, commit: false) ?? 0;
                    if (hr == 0) hr = r;
                }
            }
            else hr = home.Apply(rects, opacity, active, wallpaper, commit: false);
            _bar.Apply(frame);
            int committed = backend.Commit();
            return hr != 0 ? hr : committed;
        }
        return _layer.Apply(rects, opacity, active, wallpaper);
    }

    /// <summary>UI 线程：桌面条显隐、展开与悬停的桌面变化时才重绘。</summary>
    public void SetBarDecorations(bool visible, bool expanded, DesktopBarHit hovered)
    {
        if (visible == _barVisible && expanded == _barExpanded && hovered == _barHovered) return;
        _barVisible = visible;
        _barExpanded = expanded;
        _barHovered = hovered;
        User32.InvalidateRect(Handle, null, 1);
    }

    /// <summary>UI 线程：把某个场景窗口的缩略图提到最上层（不是本屏的窗口时什么也不做）。</summary>
    public void RaiseWindow(int sceneIndex)
    {
        Unlift();
        if (_members.Contains(sceneIndex)) ReplaceCurrentLayer(CurrentLayer.BringToTop(Handle, sceneIndex));
    }

    /// <summary>UI 线程：按真实桌面当前的 Z 序重排本屏缩略图的层次（撤层前用）。</summary>
    public void Restack(IReadOnlyList<nint> topFirst)
    {
        Unlift();
        ReplaceCurrentLayer(CurrentLayer.Restack(Handle, topFirst));
    }

    /// <summary>会话结束：隐藏缩略图但保留注册，下次打开时复用。</summary>
    public void Release()
    {
        Unlift();
        _layer.HideAll();
        HidePages();
        _pages = null;
        _bar.HideAll();
        _barVisible = false;
        _scene = OverviewScene.Empty;
        _members = [];
    }

    /// <summary>彻底注销（覆盖层销毁前）。</summary>
    public void ReleaseAll()
    {
        Unlift();
        var old = _layer;
        _layer = ThumbnailLayer.Empty(Monitor.WorkArea);
        old.Unregister();
        _pages = null;
        foreach (var layer in _pageCache.Values) layer.Unregister();
        _pageCache.Clear();
        _bar.Dispose();
        _scene = OverviewScene.Empty;
        _members = [];
    }

    public override void Dispose()
    {
        _bar.Dispose();
        _content?.Dispose(); // 分支先于根释放
        _barRoot?.Dispose();
        _dragRoot?.Dispose();
        _content = _barRoot = _dragRoot = null;
        _composition?.Dispose();
        _composition = null;
        base.Dispose();
    }

    public void ShowOverlay()
    {
        var wa = Monitor.WorkArea;
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, (int)wa.Left, (int)wa.Top, (int)wa.Width, (int)wa.Height,
            User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
        IsShown = true;
    }

    public void HideOverlay()
    {
        // 先藏窗口再藏缩略图：反过来的话中间可能合成出一帧「覆盖层还在、缩略图没了」的黑底
        User32.ShowWindow(Handle, User32.SW_HIDE);
        Unlift();
        _layer.HideAll();
        HidePages();
        _bar.HideAll();
        IsShown = false;
    }

    private void HidePages()
    {
        if (_pages is not { } pages) return;
        foreach (var layer in pages.Layers) layer?.HideAll();
    }

    /// <summary>UI 线程：标题显隐 / 悬停窗口变化时才重绘（不是每帧）。</summary>
    public void SetDecorations(bool labelsVisible, int hoveredSceneIndex)
    {
        if (labelsVisible == _labelsVisible && hoveredSceneIndex == _hovered) return;
        _labelsVisible = labelsVisible;
        _hovered = hoveredSceneIndex;
        User32.InvalidateRect(Handle, null, 1);
    }

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case User32.WM_MOUSEACTIVATE:
                return User32.MA_NOACTIVATE;
            case User32.WM_SETCURSOR:
                User32.SetCursor(User32.LoadCursorW(0, _hovered >= 0 ? User32.IDC_HAND : User32.IDC_ARROW));
                return 1;
            case User32.WM_MOUSEMOVE:
                Mouse(OverlayMouse.Move, lParam);
                return 0;
            case User32.WM_LBUTTONDOWN:
                User32.SetCapture(Handle); // 拖出覆盖层（或拖过别的屏）也收得到移动与松开
                Mouse(OverlayMouse.Down, lParam);
                return 0;
            case User32.WM_LBUTTONUP:
                if (User32.GetCapture() == Handle)
                {
                    _releasingCapture = true;
                    User32.ReleaseCapture();
                    _releasingCapture = false;
                }
                Mouse(OverlayMouse.Up, lParam);
                return 0;
            case User32.WM_CAPTURECHANGED:
                if (!_releasingCapture) _onMouse(this, OverlayMouse.Lost, 0, 0); // 被别人抢走（例如系统弹窗）：按取消处理
                return 0;
            case User32.WM_ERASEBKGND:
                return 1; // WM_PAINT 里整体画
            case User32.WM_PAINT:
                Paint();
                return 0;
        }
        return null;
    }

    private void Mouse(OverlayMouse kind, nint lParam)
        => _onMouse(this, kind, Monitor.WorkArea.Left + (short)(lParam & 0xFFFF), Monitor.WorkArea.Top + (short)((lParam >> 16) & 0xFFFF));

    private void Paint()
    {
        User32.PAINTSTRUCT ps;
        nint hdc = User32.BeginPaint(Handle, &ps);
        try
        {
            nint black = Gdi32.CreateSolidBrush(0);
            User32.FillRect(hdc, &ps.rcPaint, black);
            Gdi32.DeleteObject(black);
            PaintBar(hdc);
            if (!_labelsVisible) return;

            var origin = Monitor.WorkArea;
            nint font = Gdi32.CreateFontW(-(int)Math.Round(14 * Monitor.DpiScale), 0, 0, 0, Gdi32.FW_NORMAL, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            nint oldFont = Gdi32.SelectObject(hdc, font);
            Gdi32.SetBkMode(hdc, Gdi32.TRANSPARENT);
            nint card = Gdi32.CreateSolidBrush(Gdi32.Rgb(40, 40, 46));
            nint hover = Gdi32.CreateSolidBrush(Gdi32.Rgb(160, 200, 255));
            foreach (int i in _members)
            {
                if (!_scene.IsPresent(i)) continue; // 已移到别的桌面，或应用窗口视图里其他程序的窗口
                var w = _scene.Windows[i];
                var slot = _scene.Slots[i];
                var tile = Local(slot.Tile, origin);
                if (w.IsMinimized && !_layer.ShowsContent(i))
                {
                    // DWM 没有留下这个最小化窗口的画面（例如启动即最小化）：画一张卡片占位
                    User32.FillRect(hdc, &tile, card);
                    Gdi32.SetTextColor(hdc, Gdi32.Rgb(150, 150, 158));
                    User32.DrawTextW(hdc, "已最小化", -1, &tile, User32.DT_CENTER | User32.DT_VCENTER | User32.DT_SINGLELINE | User32.DT_NOPREFIX);
                }
                if (i == _hovered)
                {
                    var frame = tile;
                    for (int k = 0; k < 3; k++)
                    {
                        frame.Left -= 1; frame.Top -= 1; frame.Right += 1; frame.Bottom += 1;
                        User32.FrameRect(hdc, &frame, hover);
                    }
                }
                var label = Local(slot.Label, origin);
                Gdi32.SetTextColor(hdc, Gdi32.Rgb(225, 225, 230));
                User32.DrawTextW(hdc, w.Title, -1, &label,
                    User32.DT_CENTER | User32.DT_VCENTER | User32.DT_SINGLELINE | User32.DT_END_ELLIPSIS | User32.DT_NOPREFIX);
            }
            Gdi32.DeleteObject(card);
            Gdi32.DeleteObject(hover);
            Gdi32.SelectObject(hdc, oldFont);
            Gdi32.DeleteObject(font);
        }
        finally
        {
            User32.EndPaint(Handle, &ps);
        }
    }

    /// <summary>
    /// 桌面条的桌面名（GDI，画在半透明的壁纸缩略图下面，跟窗口标题一样）。收起时是一排名字，当前桌面带底色；
    /// 展开时名字在缩略图下面，当前桌面的缩略图带白框，悬停的带蓝框。
    /// </summary>
    private void PaintBar(nint hdc)
    {
        if (!_barVisible || _bar.Geometry is not { } g || g.Count != _desktops.Count) return;
        var origin = Monitor.WorkArea;
        double dpi = Monitor.DpiScale;
        nint font = Gdi32.CreateFontW(-(int)Math.Round(13 * dpi), 0, 0, 0, Gdi32.FW_SEMIBOLD, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        nint oldFont = Gdi32.SelectObject(hdc, font);
        Gdi32.SetBkMode(hdc, Gdi32.TRANSPARENT);
        nint pill = Gdi32.CreateSolidBrush(Gdi32.Rgb(78, 78, 86));
        nint hoverPill = Gdi32.CreateSolidBrush(Gdi32.Rgb(52, 52, 60));
        nint current = Gdi32.CreateSolidBrush(Gdi32.Rgb(235, 235, 240));
        nint hover = Gdi32.CreateSolidBrush(Gdi32.Rgb(160, 200, 255));
        nint oldPen = Gdi32.SelectObject(hdc, Gdi32.GetStockObject(Gdi32.NULL_PEN));
        int radius = (int)Math.Round(14 * dpi);
        nint symbolFont = Gdi32.CreateFontW(-(int)Math.Round(16 * dpi), 0, 0, 0, Gdi32.FW_SEMIBOLD, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        const uint Flags = User32.DT_CENTER | User32.DT_VCENTER | User32.DT_SINGLELINE | User32.DT_END_ELLIPSIS | User32.DT_NOPREFIX;
        for (int i = 0; i < g.Count; i++)
        {
            bool isCurrent = i == _desktops.CurrentIndex;
            bool isHovered = _barHovered.Index == i && _barHovered.Part is DesktopBarPart.Desktop or DesktopBarPart.Remove;
            if (!_barExpanded)
            {
                var r = Local(g.Pills[i], origin);
                if (isCurrent || isHovered)
                {
                    nint old = Gdi32.SelectObject(hdc, isCurrent ? pill : hoverPill);
                    Gdi32.RoundRect(hdc, r.Left, r.Top, r.Right + 1, r.Bottom + 1, radius, radius);
                    Gdi32.SelectObject(hdc, old);
                }
                Gdi32.SetTextColor(hdc, isCurrent || isHovered ? Gdi32.Rgb(240, 240, 245) : Gdi32.Rgb(185, 185, 195));
                User32.DrawTextW(hdc, _desktops.Names[i], -1, &r, Flags);
            }
            else
            {
                if (isCurrent || isHovered)
                {
                    var frame = Local(g.Thumbs[i], origin);
                    for (int k = 0; k < (isHovered ? 4 : 2); k++) // 画在半透明壁纸下面会被压暗：悬停框加粗才看得清
                    {
                        frame.Left -= 1; frame.Top -= 1; frame.Right += 1; frame.Bottom += 1;
                        User32.FrameRect(hdc, &frame, isHovered ? hover : current);
                    }
                }
                var label = Local(g.Labels[i], origin);
                // 悬停的非当前桌面：名字右端出现「×」（删除，窗口并入相邻桌面）；名字给它让出位置
                bool removable = isHovered && !isCurrent && g.Count >= 2;
                var x = Local(g.RemoveButton(i), origin);
                if (removable) label.Right = x.Left - 2;
                Gdi32.SetTextColor(hdc, isCurrent || isHovered ? Gdi32.Rgb(240, 240, 245) : Gdi32.Rgb(185, 185, 195));
                User32.DrawTextW(hdc, _desktops.Names[i], -1, &label, Flags);
                if (removable)
                {
                    if (_barHovered.Part == DesktopBarPart.Remove)
                    {
                        nint old = Gdi32.SelectObject(hdc, pill);
                        Gdi32.RoundRect(hdc, x.Left, x.Top, x.Right + 1, x.Bottom + 1, x.Right - x.Left, x.Bottom - x.Top);
                        Gdi32.SelectObject(hdc, old);
                    }
                    Gdi32.SetTextColor(hdc, _barHovered.Part == DesktopBarPart.Remove ? Gdi32.Rgb(255, 255, 255) : Gdi32.Rgb(225, 225, 232));
                    Gdi32.SelectObject(hdc, symbolFont); // 正文字号的「×」画在半透明壁纸下太小太淡
                    User32.DrawTextW(hdc, "×", -1, &x, Flags);
                    Gdi32.SelectObject(hdc, font);
                }
            }
        }

        // 末尾的「+」：新建桌面
        bool addHovered = _barHovered.Part == DesktopBarPart.Add;
        var add = Local(_barExpanded ? g.AddTile : g.AddPill, origin);
        if (addHovered || _barExpanded)
        {
            nint old = Gdi32.SelectObject(hdc, addHovered ? pill : hoverPill);
            Gdi32.RoundRect(hdc, add.Left, add.Top, add.Right + 1, add.Bottom + 1, radius, radius);
            Gdi32.SelectObject(hdc, old);
        }
        nint plusFont = Gdi32.CreateFontW(-(int)Math.Round((_barExpanded ? 26 : 16) * dpi), 0, 0, 0, Gdi32.FW_NORMAL, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        Gdi32.SelectObject(hdc, plusFont);
        Gdi32.SetTextColor(hdc, addHovered ? Gdi32.Rgb(255, 255, 255) : Gdi32.Rgb(185, 185, 195));
        User32.DrawTextW(hdc, "+", -1, &add, Flags);
        Gdi32.SelectObject(hdc, font);
        Gdi32.DeleteObject(plusFont);
        Gdi32.SelectObject(hdc, oldPen);
        Gdi32.DeleteObject(symbolFont);
        Gdi32.DeleteObject(pill);
        Gdi32.DeleteObject(hoverPill);
        Gdi32.DeleteObject(current);
        Gdi32.DeleteObject(hover);
        Gdi32.SelectObject(hdc, oldFont);
        Gdi32.DeleteObject(font);
    }

    private static User32.RECT Local(RectD r, RectD origin) => new()
    {
        Left = (int)Math.Round(r.Left - origin.Left), Top = (int)Math.Round(r.Top - origin.Top),
        Right = (int)Math.Round(r.Right - origin.Left), Bottom = (int)Math.Round(r.Bottom - origin.Top),
    };
}

/// <summary>覆盖层上的鼠标事件（坐标为屏幕物理像素）。</summary>
internal enum OverlayMouse
{
    Move,
    Down,
    Up,

    /// <summary>鼠标捕获被别人抢走：正在进行的按下 / 拖动按取消处理。</summary>
    Lost,
}
