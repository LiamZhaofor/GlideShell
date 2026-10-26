using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 一块显示器的总览覆盖层：非分层（不用 WS_EX_LAYERED，避免整窗混合的开销）、置顶、永不激活
/// （WS_EX_NOACTIVATE + MA_NOACTIVATE：点击不抢焦点，键盘由低级钩子处理）。只覆盖工作区，任务栏保持可见。
/// 窗口自身只画黑底、标题与悬停框（偶尔重绘）；每帧变化的内容全部是 DWM 缩略图，由渲染线程更新。
/// </summary>
internal sealed unsafe class OverlayWindow : NativeWindow
{
    private readonly Action<OverlayWindow, double, double> _onMouseMove;
    private readonly Action<OverlayWindow, double, double> _onClick;
    private volatile IThumbnailLayer _layer;
    private OverlayComposition? _composition; // 共享缩略图视觉模式下才创建
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
            if (_composition is not null)
                next = SharedThumbnailLayer.Register(shared, _composition, Handle, Monitor.WorkArea, windows, _layer as SharedThumbnailLayer, out sharedFailure);
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

    /// <summary>UI 线程：把某个场景窗口的缩略图提到最上层（不是本屏的窗口时什么也不做）。</summary>
    public void RaiseWindow(int sceneIndex)
    {
        if (_members.Contains(sceneIndex)) _layer = _layer.BringToTop(Handle, sceneIndex);
    }

    /// <summary>会话结束：隐藏缩略图但保留注册，下次打开时复用。</summary>
    public void Release()
    {
        _layer.HideAll();
        _scene = OverviewScene.Empty;
        _members = [];
    }

    /// <summary>彻底注销（覆盖层销毁前）。</summary>
    public void ReleaseAll()
    {
        var old = _layer;
        _layer = ThumbnailLayer.Empty(Monitor.WorkArea);
        old.Unregister();
        _scene = OverviewScene.Empty;
        _members = [];
    }

    public override void Dispose()
    {
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
        _layer.HideAll();
        User32.ShowWindow(Handle, User32.SW_HIDE);
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

    private static User32.RECT Local(RectD r, RectD origin) => new()
    {
        Left = (int)Math.Round(r.Left - origin.Left), Top = (int)Math.Round(r.Top - origin.Top),
        Right = (int)Math.Round(r.Right - origin.Left), Bottom = (int)Math.Round(r.Bottom - origin.Top),
    };
}
