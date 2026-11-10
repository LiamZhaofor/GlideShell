using GlideShell.Core.Search;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;

namespace GlideShell.Platform.Overview;

/// <summary>能画搜索面板的地方：总览覆盖层（在总览里打字），或单独弹出的 <see cref="SearchWindow"/>（快捷键）。坐标都是屏幕坐标。</summary>
internal interface ISearchHost
{
    MonitorInfo Monitor { get; }
    void ShowSearch(string query, IReadOnlyList<SearchResult> results, int selected, Func<SearchEntry, AppId?> iconOf);
    void SelectSearch(int index);
    void HideSearch();
    int SearchHit(double x, double y);
    bool SearchContains(double x, double y);
}

internal sealed partial class OverlayWindow : ISearchHost;

/// <summary>
/// 快捷键单独弹出的搜索面板（不进总览）：盖住一块屏工作区的透明置顶窗口，永不激活（键盘由低级钩子处理），
/// 只画搜索面板（与总览里同一个 <see cref="SearchDecor"/>，不压暗背景），面板外是透明的，下面的程序照常看得见；
/// 点面板外面由宿主收起。没有重定向表面（WS_EX_NOREDIRECTIONBITMAP），只显示合成层的内容。
/// 自己一个渲染循环：显示期间逐帧画（光标要闪），收起的淡出播完就藏起窗口、停下。
/// </summary>
internal sealed unsafe class SearchWindow : NativeWindow, ISearchHost
{
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    private readonly UiThread _ui;
    private readonly Action<SearchWindow, OverlayMouse, double, double> _onMouse;
    private readonly RenderLoop _render;
    private SharedThumbnailBackend? _shared;
    private OverlayComposition? _composition, _glassRoot, _root;
    private volatile SearchDecor? _decor;
    private volatile bool _closing;
    private bool _releasingCapture;

    public SearchWindow(UiThread ui, MonitorInfo monitor, Action<SearchWindow, OverlayMouse, double, double> onMouse)
    {
        _ui = ui;
        Monitor = monitor;
        _onMouse = onMouse;
        var wa = monitor.WorkArea;
        Create("GlideShell.Search", User32.WS_POPUP,
            User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW_U | User32.WS_EX_NOACTIVATE_U | WS_EX_NOREDIRECTIONBITMAP,
            (int)wa.Left, (int)wa.Top, (int)wa.Width, (int)wa.Height);
        _render = new RenderLoop(RenderFrame);
    }

    public MonitorInfo Monitor { get; }

    public bool IsShown { get; private set; }

    /// <summary>UI 线程：第一次用时建合成层与面板。建不出来（没有共享缩略图 / Direct2D）返回 false。</summary>
    public bool Prepare(SharedThumbnailBackend shared, DecorResources decor)
    {
        if (_decor is not null) return true;
        int hr = 0;
        _composition = OverlayComposition.Create(shared, Handle, out hr);
        if (_composition is not null) _glassRoot = _composition.CreateBranch(out hr);
        if (_glassRoot is not null) _root = _composition!.CreateBranch(out hr);
        if (_root is null)
        {
            Log.Warn($"搜索窗：建合成层失败 0x{hr:X8}");
            return false;
        }
        _shared = shared;
        _decor = new SearchDecor(decor, _root, _glassRoot!, Handle, Monitor, dim: 0);
        return true;
    }

    /// <summary>UI 线程：显示窗口（面板随后由 <see cref="ShowSearch"/> 填内容、弹簧淡入）。</summary>
    public void ShowWindow()
    {
        _closing = false;
        var wa = Monitor.WorkArea;
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, (int)wa.Left, (int)wa.Top, (int)wa.Width, (int)wa.Height,
            User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
        IsShown = true;
        _render.Wake();
    }

    public void ShowSearch(string query, IReadOnlyList<SearchResult> results, int selected, Func<SearchEntry, AppId?> iconOf)
    {
        _decor?.Show(query, results, selected, iconOf);
        _render.Wake();
    }

    public void SelectSearch(int index) => _decor?.Select(index);

    /// <summary>UI 线程：收起面板（淡出播完后藏起窗口）。</summary>
    public void HideSearch()
    {
        _closing = true;
        _decor?.Hide();
        _render.Wake();
    }

    public int SearchHit(double x, double y) => _decor?.HitTest(x, y) ?? -1;

    public bool SearchContains(double x, double y) => _decor?.Contains(x, y) ?? false;

    /// <summary>渲染线程：画一帧并提交。淡出播完就交给 UI 线程藏起窗口，停下渲染。</summary>
    private bool RenderFrame(long nowUs)
    {
        if (_decor is not { } decor || _shared is not { } shared) return false;
        decor.Apply(nowUs, 1);
        shared.Commit();
        if (_closing && decor.IsGone)
        {
            _ui.Post(HideWindow);
            return false;
        }
        return true;
    }

    private void HideWindow()
    {
        if (!_closing || !IsShown) return; // 淡出途中又打开了
        User32.ShowWindow(Handle, User32.SW_HIDE);
        IsShown = false;
    }

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case User32.WM_MOUSEACTIVATE:
                return User32.MA_NOACTIVATE;
            case User32.WM_SETCURSOR:
                User32.SetCursor(User32.LoadCursorW(0, User32.IDC_ARROW));
                return 1;
            case User32.WM_MOUSEMOVE:
                Mouse(OverlayMouse.Move, lParam);
                return 0;
            case User32.WM_LBUTTONDOWN:
                User32.SetCapture(Handle);
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
            case 0x0204 /* WM_RBUTTONDOWN */:
                return 0;
            case 0x0205 /* WM_RBUTTONUP */:
                Mouse(OverlayMouse.Up, lParam); // 右键点面板外面也收起（与左键一样）
                return 0;
            case User32.WM_CAPTURECHANGED:
                if (!_releasingCapture) _onMouse(this, OverlayMouse.Lost, 0, 0);
                return 0;
            case User32.WM_ERASEBKGND:
                return 1;
        }
        return null;
    }

    private void Mouse(OverlayMouse kind, nint lParam)
        => _onMouse(this, kind, Monitor.WorkArea.Left + (short)(lParam & 0xFFFF), Monitor.WorkArea.Top + (short)((lParam >> 16) & 0xFFFF));

    public override void Dispose()
    {
        _render.Dispose();
        _decor?.Dispose();
        _decor = null;
        _root?.Dispose();
        _glassRoot?.Dispose();
        _composition?.Dispose();
        base.Dispose();
    }
}
