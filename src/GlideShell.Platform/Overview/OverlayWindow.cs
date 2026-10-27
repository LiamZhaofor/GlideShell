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
/// </summary>
internal sealed unsafe class OverlayWindow : NativeWindow
{
    private readonly Action<OverlayWindow, double, double> _onMouseMove;
    private readonly Action<OverlayWindow, double, double> _onClick;
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
    private OverviewScene _scene = OverviewScene.Empty;
    private IReadOnlyList<int> _members = [];
    private bool _labelsVisible;
    private int _hovered = -1;

    public OverlayWindow(MonitorInfo monitor, Action<OverlayWindow, double, double> onMouseMove, Action<OverlayWindow, double, double> onClick)
    {
        Monitor = monitor;
        _onMouseMove = onMouseMove;
        _onClick = onClick;
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
            if (_barRoot is not null)
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

    /// <summary>渲染线程：摆好本帧的窗口与桌面条（共享缩略图模式下两者一次 Commit，同一帧生效）。返回第一个失败的 HRESULT。</summary>
    public int Render(ReadOnlySpan<RectD> rects, ReadOnlySpan<double> opacity, ReadOnlySpan<double> active, double wallpaper, in OverviewFrame frame)
    {
        if (_layer is SharedThumbnailLayer shared && _bar.HasThumbnails && _shared is { } backend)
        {
            int hr = shared.Apply(rects, opacity, active, wallpaper, commit: false);
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
        if (_members.Contains(sceneIndex)) _layer = _layer.BringToTop(Handle, sceneIndex);
    }

    /// <summary>UI 线程：按真实桌面当前的 Z 序重排本屏缩略图的层次（撤层前用）。</summary>
    public void Restack(IReadOnlyList<nint> topFirst) => _layer = _layer.Restack(Handle, topFirst);

    /// <summary>会话结束：隐藏缩略图但保留注册，下次打开时复用。</summary>
    public void Release()
    {
        _layer.HideAll();
        _bar.HideAll();
        _barVisible = false;
        _scene = OverviewScene.Empty;
        _members = [];
    }

    /// <summary>彻底注销（覆盖层销毁前）。</summary>
    public void ReleaseAll()
    {
        var old = _layer;
        _layer = ThumbnailLayer.Empty(Monitor.WorkArea);
        old.Unregister();
        _bar.Dispose();
        _scene = OverviewScene.Empty;
        _members = [];
    }

    public override void Dispose()
    {
        _bar.Dispose();
        _content?.Dispose(); // 分支先于根释放
        _barRoot?.Dispose();
        _content = _barRoot = null;
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
        _layer.HideAll();
        _bar.HideAll();
        IsShown = false;
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
                _onMouseMove(this, Monitor.WorkArea.Left + (short)(lParam & 0xFFFF), Monitor.WorkArea.Top + (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case User32.WM_LBUTTONDOWN:
                _onClick(this, Monitor.WorkArea.Left + (short)(lParam & 0xFFFF), Monitor.WorkArea.Top + (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case User32.WM_ERASEBKGND:
                return 1; // WM_PAINT 里整体画
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
