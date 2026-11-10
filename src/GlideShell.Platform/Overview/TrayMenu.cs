using System.Runtime.InteropServices;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 在总览里右键后台程序栏弹出的托盘菜单（UI 线程创建与使用）。
/// <para>
/// 菜单是程序自己弹的窗口，在覆盖层下面。跟着 <c>EVENT_SYSTEM_MENUPOPUPSTART / END</c>（Win32 菜单）与
/// <c>EVENT_OBJECT_SHOW / HIDE</c>（自绘菜单只发这个，例如 WinForms：实测 G-Helper）找到它的各级菜单窗口：根菜单挪到右键处；覆盖层先画它的缩略图（淡入、阴影），淡入完在它所在的位置开一个洞，
/// 露出真实菜单、鼠标直接点到它（自绘菜单拿不到全局捕获，被覆盖层挡着就点不了）。
/// <c>EVENT_OBJECT_INVOKED</c> 说明选了菜单项。
/// </para>
/// </summary>
internal sealed unsafe partial class TrayMenu : IDisposable
{
    private const uint EVENT_SYSTEM_MENUPOPUPSTART = 0x0006, EVENT_SYSTEM_MENUPOPUPEND = 0x0007, EVENT_OBJECT_INVOKED = 0x8013;
    private const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003;

    private static TrayMenu? s_current; // 回调是静态的：同一时刻只跟踪一个菜单
    private nint _popupHook, _showHook, _invokeHook;
    private readonly RectD _area; // 菜单要放进的屏幕区域（后台程序栏所在屏的工作区）
    private readonly HashSet<uint> _threads = []; // 弹过菜单的线程（选中菜单项的事件按线程认）

    /// <summary>右键的程序。</summary>
    public TrayApp App { get; }

    /// <summary>它在栏里的下标。</summary>
    public int Index { get; }

    /// <summary>这个程序相关的进程（菜单窗口按进程认）。</summary>
    public HashSet<uint> Processes { get; }

    /// <summary>菜单要出现的位置（屏幕物理像素）。<see cref="Centered"/>：横向居中在它上面（键盘弹出，锚点是图标上沿）；否则左上角在这里（右键处）。</summary>
    public double AnchorX { get; }

    public double AnchorY { get; }
    public bool Centered { get; }

    /// <summary>此刻显示着的各级菜单窗口，打开顺序（根菜单在前）。</summary>
    public List<Popup> Popups { get; } = [];

    /// <summary>见过菜单弹出。</summary>
    public bool Opened { get; private set; }

    /// <summary>选了菜单项。</summary>
    public bool Invoked { get; private set; }

    /// <summary>根菜单已经挪到锚点。</summary>
    public bool Placed;

    public long StartedMs { get; } = Environment.TickCount64;

    /// <summary>右键时的前台窗口（覆盖层从不激活，是用户原来在用的程序）：关自绘菜单时把前台交回给它。</summary>
    public nint ForegroundBefore { get; } = User32.GetForegroundWindow();

    /// <summary>托盘右键完成的时刻（-1 = 还没完成）。</summary>
    public long ClickedMs = -1;

    /// <summary>菜单全部关掉的时刻（-1 = 还开着或还没开）。</summary>
    public long ClosedMs = -1;

    /// <summary>菜单列表有变化（UI 线程回调）。</summary>
    public Action? Changed;

    public sealed class Popup(nint window)
    {
        public nint Window { get; } = window;
        public long ShownUs { get; } = HostClock.NowUs();
        public RectD Rect;
    }

    public TrayMenu(TrayApp app, int index, HashSet<uint> processes, double anchorX, double anchorY, bool centered, RectD area)
    {
        _area = area;
        App = app;
        Index = index;
        Processes = processes;
        AnchorX = anchorX;
        AnchorY = anchorY;
        Centered = centered;
        s_current?.Dispose();
        s_current = this;
        _popupHook = User32.SetWinEventHook(EVENT_SYSTEM_MENUPOPUPSTART, EVENT_SYSTEM_MENUPOPUPEND, 0, &OnEvent, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);
        _showHook = User32.SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE, 0, &OnEvent, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);
        _invokeHook = User32.SetWinEventHook(EVENT_OBJECT_INVOKED, EVENT_OBJECT_INVOKED, 0, &OnEvent, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>窗口属于这个程序的进程。</summary>
    public bool Owns(nint hwnd, out uint thread)
    {
        uint pid = 0;
        thread = hwnd == 0 ? 0 : User32.GetWindowThreadProcessId(hwnd, &pid);
        return pid != 0 && Processes.Contains(pid);
    }

    /// <summary>
    /// 像自绘菜单：这个程序的、看得见的顶层窗口，不进任务栏（工具窗口、置顶或有所有者），比屏幕小得多（提示框、输入法窗口除外）。
    /// 样式各家不同，不看 WS_POPUP / 标题栏：实测微信的 Qt 菜单两样都没有，QQ 的 Chromium 菜单带着 WS_CAPTION。
    /// </summary>
    public bool LooksLikeMenu(nint hwnd)
    {
        if (hwnd == 0 || !User32.IsWindowVisible(hwnd) || !Owns(hwnd, out _)) return false;
        long style = User32.GetWindowLongPtrW(hwnd, User32.GWL_STYLE), ex = User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE);
        if ((style & 0x40000000L /* WS_CHILD */) != 0) return false;
        if ((ex & (User32.WS_EX_TOOLWINDOW | 0x8L /* WS_EX_TOPMOST */)) == 0 && User32.GetWindow(hwnd, User32.GW_OWNER) == 0) return false;
        if (WindowCatalog.ClassName(hwnd) is "tooltips_class32" or "IME" or "MSCTFIME UI") return false;
        var r = WindowCatalog.VisibleBounds(hwnd);
        return r.Width > 0 && r.Height > 0 && r.Width < _area.Width * 0.5 && r.Height < _area.Height * 0.95;
    }

    /// <summary>加入一个菜单窗口（事件，或者不发事件的程序：前台的小弹窗）。返回是否新加的。</summary>
    public bool Add(nint hwnd)
    {
        // 菜单关了以后再出来的窗口（选了菜单项打开的对话框等）不算菜单
        if (ClosedMs >= 0 || Popups.Exists(p => p.Window == hwnd) || !Owns(hwnd, out uint thread)) return false;
        _threads.Add(thread);
        Popups.Add(new Popup(hwnd) { Rect = WindowCatalog.VisibleBounds(hwnd) });
        Opened = true;
        ClosedMs = -1;
        return true;
    }

    /// <summary>去掉已经关掉的菜单窗口（结束事件漏发、或窗口直接销毁时），更新各自的位置。返回列表或位置有没有变化。</summary>
    public bool Refresh()
    {
        bool changed = Popups.RemoveAll(p => !User32.IsWindow(p.Window) || !User32.IsWindowVisible(p.Window)) > 0;
        foreach (var p in Popups)
        {
            var r = WindowCatalog.VisibleBounds(p.Window);
            if (r == p.Rect) continue;
            p.Rect = r;
            changed = true;
        }
        if (Opened && Popups.Count == 0 && ClosedMs < 0) ClosedMs = Environment.TickCount64;
        return changed;
    }

    /// <summary>
    /// 把根菜单挪到锚点：放得下就往右下开，否则翻到左边 / 上面（与系统在指针处弹菜单的规则一样），再夹进屏幕区域。
    /// </summary>
    public void PlaceRoot()
    {
        var area = _area;
        if (Popups.Count == 0) return;
        var root = Popups[0];
        var r = WindowCatalog.VisibleBounds(root.Window);
        double w = r.Width, h = r.Height, x, y;
        if (Centered)
        {
            x = AnchorX - w / 2;
            y = AnchorY - h;
        }
        else
        {
            x = AnchorX + w <= area.Right ? AnchorX : AnchorX - w;
            y = AnchorY + h <= area.Bottom ? AnchorY : AnchorY - h;
        }
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - w));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - h));
        int dx = (int)Math.Round(x - r.Left), dy = (int)Math.Round(y - r.Top);
        if (dx != 0 || dy != 0)
        {
            User32.RECT wr;
            User32.GetWindowRect(root.Window, &wr);
            User32.SetWindowPos(root.Window, 0, wr.Left + dx, wr.Top + dy, 0, 0, SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        root.Rect = WindowCatalog.VisibleBounds(root.Window);
    }

    /// <summary>根菜单是 Win32 菜单（#32768）：Windows 11 上有 8 DIP 的圆角；菜单模式有全局鼠标捕获、自己处理 Esc。</summary>
    public bool Win32Menu => Popups.Count > 0 && WindowCatalog.ClassName(Popups[0].Window) == "#32768";

    /// <summary>
    /// 菜单的圆角（DIP）：Win32 菜单 8；自绘菜单按它向 DWM 要的圆角（DWMWA_WINDOW_CORNER_PREFERENCE：圆 8、小圆 4，实测 G-Helper 是小圆），没要就是直角。
    /// </summary>
    public double CornerRadiusDip
    {
        get
        {
            if (Popups.Count == 0) return 0;
            if (Win32Menu) return 8;
            int preference = 0;
            if (Dwmapi.DwmGetWindowAttribute(Popups[0].Window, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, &preference, sizeof(int)) != 0) return 0;
            return preference switch { 2 => 8, 3 => 4, _ => 0 };
        }
    }

    /// <summary>
    /// 关掉菜单（点了覆盖层上菜单以外的地方、按了 Esc）：给各级菜单发 Esc（Win32 菜单、Qt 菜单都认）；
    /// 自绘菜单再把前台交回右键前的窗口——它们只在自己线程里看鼠标和按键，覆盖层又从不激活，不知道点了外面，
    /// 程序失去前台时才会自己收起（实测 WinForms 对发过去的 Esc 没反应）。
    /// </summary>
    public void Dismiss()
    {
        for (int i = Popups.Count - 1; i >= 0; i--)
        {
            User32.PostMessageW(Popups[i].Window, 0x0100 /* WM_KEYDOWN */, 0x1B, 0x00010001);
            User32.PostMessageW(Popups[i].Window, 0x0101 /* WM_KEYUP */, 0x1B, unchecked((nint)0xC0010001));
        }
        if (Win32Menu) return;
        nint back = ForegroundBefore;
        if (back == 0 || !User32.IsWindow(back) || Owns(back, out _)) back = User32.FindWindowW("Shell_TrayWnd", null);
        WindowActivator.Activate(back);
    }

    private const uint SWP_NOSIZE = 0x0001, SWP_NOOWNERZORDER = 0x0200;

    private void OnEvent(uint eventType, nint hwnd, int idObject, uint thread)
    {
        switch (eventType)
        {
            case EVENT_SYSTEM_MENUPOPUPSTART:
                if (Add(hwnd)) Changed?.Invoke();
                break;
            case EVENT_OBJECT_SHOW when idObject == 0 && LooksLikeMenu(hwnd):
                if (Add(hwnd)) Changed?.Invoke();
                break;
            case EVENT_SYSTEM_MENUPOPUPEND:
            case EVENT_OBJECT_HIDE when idObject == 0:
                if (Popups.RemoveAll(p => p.Window == hwnd) > 0)
                {
                    if (Popups.Count == 0) ClosedMs = Environment.TickCount64;
                    Changed?.Invoke();
                }
                break;
            case EVENT_OBJECT_INVOKED:
                if (Opened && _threads.Contains(thread)) Invoked = true;
                break;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            s_current?.OnEvent(eventType, hwnd, idObject, thread);
        }
        catch
        {
            // 非托管回调里不能抛出
        }
    }

    public void Dispose()
    {
        if (_popupHook != 0) User32.UnhookWinEvent(_popupHook);
        if (_showHook != 0) User32.UnhookWinEvent(_showHook);
        if (_invokeHook != 0) User32.UnhookWinEvent(_invokeHook);
        _popupHook = _showHook = _invokeHook = 0;
        if (s_current == this) s_current = null;
    }
}
