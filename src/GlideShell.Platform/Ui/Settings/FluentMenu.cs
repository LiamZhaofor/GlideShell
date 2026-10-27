using System.Runtime.InteropServices;
using GlideShell.Core.Geometry;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui.Settings;

/// <summary>菜单的一项：<see cref="Text"/> 为 null 是分隔线；有 <see cref="Children"/> 的是子菜单。</summary>
internal sealed record MenuEntry(string? Text, bool Checked = false, bool Enabled = true, IReadOnlyList<MenuEntry>? Children = null, object? Tag = null)
{
    public static MenuEntry Separator { get; } = new((string?)null);

    public bool IsSeparator => Text is null;
}

/// <summary>菜单的样子：下拉框的选项列表（选中项左边一条强调色）或右键菜单（勾选标记）。</summary>
internal enum MenuStyle
{
    Dropdown,
    Context,
}

/// <summary>
/// Windows 11 风格的弹出菜单（代替系统的 Win32 菜单）：圆角、亚克力背景（菜单是激活的窗口，系统亚克力能画出来）、
/// 跟随浅色 / 深色，悬停高亮是圆角块，支持子菜单与键盘（↑↓ 选、回车确认、→ 打开子菜单、← / Esc 返回）。
/// <see cref="Show"/> 像 TrackPopupMenu 一样是模态的：在内部跑消息循环，返回选中的项（取消为 null）。点菜单外面、切到别的窗口就关闭。
/// </summary>
internal sealed unsafe partial class FluentMenu : NativeWindow
{
    private const double ItemDip = 34, SeparatorDip = 9, PadDip = 4, MinWidthDip = 160;

    private readonly IReadOnlyList<MenuEntry> _items;
    private readonly MenuStyle _style;
    private readonly FluentMenu? _parent;
    private readonly FluentMenu _root;
    private readonly FluentTheme _theme = FluentTheme.Current();
    private readonly UiCanvas? _canvas;
    private readonly double _s;
    private readonly List<RectD> _rows = [];
    private FluentMenu? _child;
    private int _hover = -1;
    private bool _done; // 只在根菜单上用
    private MenuEntry? _result;
    private bool _tracking;
    private readonly int _minWidth;

    private FluentMenu(IReadOnlyList<MenuEntry> items, MenuStyle style, FluentMenu? parent, int x, int y, bool preferAbove, double alignRight = double.NaN,
        int minWidth = 0)
    {
        _minWidth = minWidth;
        _items = items;
        _style = style;
        _parent = parent;
        _root = parent?._root ?? this;
        _canvas = UiCanvas.Create();
        nint monitor = MonitorFromPoint(new User32.POINT { X = x, Y = y }, 2);
        uint dpiX = 96, dpiY;
        Shcore.GetDpiForMonitor(monitor, Shcore.MDT_EFFECTIVE_DPI, &dpiX, &dpiY);
        _s = Math.Max(96u, dpiX) / 96.0;
        var info = new User32.MONITORINFO { cbSize = (uint)sizeof(User32.MONITORINFO) };
        User32.GetMonitorInfo(monitor, &info);
        var work = info.rcWork;

        var (w, h) = Measure();
        // 放不下就往上 / 往左翻（子菜单往左翻时贴着父菜单的左边）
        int left = x, top = preferAbove ? y - h : y;
        if (left + w > work.Right) left = double.IsNaN(alignRight) ? work.Right - w : (int)alignRight - w;
        if (top + h > work.Bottom) top = Math.Max(work.Top, y - h);
        if (top < work.Top) top = work.Top;
        left = Math.Max(work.Left, left);

        Create("GlideShell.Menu", 0x80000000 /* WS_POPUP */, 0x00000008 /* WS_EX_TOPMOST */ | 0x00000080 /* WS_EX_TOOLWINDOW */, left, top, w, h,
            parent?.Handle ?? 0, classStyle: 0x00020000 /* CS_DROPSHADOW */);
        int round = 2 /* DWMWCP_ROUND */, dark = _theme.Dark ? 1 : 0, backdrop = 3 /* DWMSBT_TRANSIENTWINDOW：亚克力 */;
        Dwmapi.DwmSetWindowAttribute(Handle, Dwmapi.DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int));
        Dwmapi.DwmSetWindowAttribute(Handle, Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(int));
        var margins = new Dwmapi.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Acrylic = Environment.OSVersion.Version.Build >= 22000 && Dwmapi.DwmExtendFrameIntoClientArea(Handle, &margins) == 0 &&
                  Dwmapi.DwmSetWindowAttribute(Handle, Dwmapi.DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(int)) == 0;
        if (_style == MenuStyle.Dropdown) _hover = Enumerable.Range(0, _items.Count).FirstOrDefault(i => _items[i].Checked, -1);
    }

    private bool Acrylic { get; }

    /// <summary>
    /// 在屏幕坐标 (<paramref name="x"/>, <paramref name="y"/>) 弹出菜单（<paramref name="preferAbove"/> 时菜单底边在这里，例如托盘菜单），
    /// 等用户选完，返回选中的项（取消为 null）。只能在 UI 线程上调用。
    /// </summary>
    /// <param name="minWidth">菜单至少多宽（物理像素；下拉框的选项列表和下拉框一样宽）。</param>
    public static MenuEntry? Show(IReadOnlyList<MenuEntry> items, int x, int y, MenuStyle style = MenuStyle.Context, bool preferAbove = false, int minWidth = 0)
    {
        var menu = new FluentMenu(items, style, null, x, y, preferAbove, minWidth: minWidth);
        menu.ShowAndActivate();
        User32.MSG msg;
        while (!menu._done)
        {
            if (User32.GetMessageW(&msg, 0, 0, 0) <= 0)
            {
                User32.PostQuitMessage((int)msg.wParam); // 程序要退出：把退出消息留给外层循环
                break;
            }
            User32.TranslateMessage(&msg);
            User32.DispatchMessageW(&msg);
        }
        menu.CloseChild();
        if (menu.Handle != 0) User32.DestroyWindow(menu.Handle);
        return menu._result;
    }

    private void ShowAndActivate()
    {
        User32.ShowWindow(Handle, 5 /* SW_SHOW */);
        User32.SetForegroundWindow(Handle);
        User32.SetFocus(Handle);
    }

    // ---------- 布局与画 ----------

    private bool HasChecks => _style == MenuStyle.Context && _items.Any(i => i.Checked);

    private bool HasChildren => _items.Any(i => i.Children is { Count: > 0 });

    private UiFont Font => new(FluentTheme.Font, 14 * _s);

    private (int W, int H) Measure()
    {
        double s = _s, y = PadDip * s, textW = 0;
        _rows.Clear();
        foreach (var it in _items)
        {
            double h = (it.IsSeparator ? SeparatorDip : ItemDip) * s;
            _rows.Add(new RectD(0, y, 0, h));
            y += h;
            if (!it.IsSeparator && _canvas is not null)
            {
                // 「文字\t快捷键」：快捷键靠右，两者之间至少空 32 DIP
                var (label, shortcut) = Split(it.Text!);
                double tw = _canvas.Measure(label, Font, 2000 * s).Width;
                if (shortcut.Length > 0) tw += 32 * s + _canvas.Measure(shortcut, Font, 2000 * s).Width;
                textW = Math.Max(textW, tw);
            }
        }
        double w = Math.Max(Math.Max(MinWidthDip * s, _minWidth), TextLeft + textW + (HasChildren ? 40 : 20) * s + 8 * s);
        for (int i = 0; i < _rows.Count; i++) _rows[i] = _rows[i] with { Width = w };
        return ((int)Math.Ceiling(w), (int)Math.Ceiling(y + PadDip * s));
    }

    private double TextLeft => (HasChecks || _style == MenuStyle.Dropdown ? 40 : 16) * _s;

    private void Paint()
    {
        User32.PAINTSTRUCT ps;
        nint hdc = User32.BeginPaint(Handle, &ps);
        try
        {
            if (_canvas is null) return;
            User32.RECT rc;
            User32.GetClientRect(Handle, &rc);
            if (!_canvas.Begin(rc.Right, rc.Bottom)) return;
            var t = _theme;
            double s = _s;
            // 亚克力上叠一层半透明底色（WinUI 菜单的「亚克力填充」）；没有亚克力时是不透明底色
            var fill = t.Dark ? new D2D.ColorF(0.17f, 0.17f, 0.17f, Acrylic ? 0.82f : 1) : new D2D.ColorF(0.988f, 0.988f, 0.988f, Acrylic ? 0.8f : 1);
            _canvas.Fill(new RectD(0, 0, rc.Right, rc.Bottom), 0, fill);
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                var row = _rows[i];
                if (it.IsSeparator)
                {
                    _canvas.Fill(new RectD(0, row.CenterY - 0.5 * s, rc.Right, Math.Max(1, s)), 0, t.Divider);
                    continue;
                }
                var cell = new RectD(4 * s, row.Top + 2 * s, rc.Right - 8 * s, row.Height - 4 * s);
                bool selected = _style == MenuStyle.Dropdown && it.Checked;
                if ((i == _hover && it.Enabled) || selected || _child is not null && _child == ChildOf(i))
                    _canvas.Fill(cell, 4 * s, i == _hover ? HoverFill(t) : t.Subtle);
                if (selected) _canvas.Fill(new RectD(cell.Left, cell.CenterY - 8 * s, 3 * s, 16 * s), 1.5 * s, t.Accent);
                if (_style == MenuStyle.Context && it.Checked)
                    _canvas.Text("", new RectD(cell.Left + 8 * s, cell.Top, 20 * s, cell.Height), new UiFont(FluentTheme.IconFont, 12 * s), t.TextPrimary, UiAlign.Center);
                var color = it.Enabled ? t.TextPrimary : t.TextTertiary;
                var (label, shortcut) = Split(it.Text!);
                var textBox = new RectD(TextLeft, cell.Top, cell.Right - TextLeft - (it.Children is { Count: > 0 } ? 28 * s : 12 * s), cell.Height);
                _canvas.Text(label, textBox, Font, color);
                if (shortcut.Length > 0) _canvas.Text(shortcut, textBox, new UiFont(FluentTheme.Font, 12 * s), t.TextSecondary, UiAlign.Right);
                if (it.Children is { Count: > 0 })
                    _canvas.Text(FluentTheme.Icons.ChevronRight, new RectD(cell.Right - 26 * s, cell.Top, 20 * s, cell.Height), new UiFont(FluentTheme.IconFont, 10 * s),
                        t.TextSecondary, UiAlign.Center);
            }
            _canvas.Present(hdc);
        }
        finally
        {
            User32.EndPaint(Handle, &ps);
        }
    }

    private static (string Label, string Shortcut) Split(string text)
    {
        int tab = text.IndexOf('\t');
        return tab < 0 ? (text, "") : (text[..tab], text[(tab + 1)..]);
    }

    private static D2D.ColorF HoverFill(FluentTheme t) => t.Dark ? new D2D.ColorF(1, 1, 1, 0.08f) : new D2D.ColorF(0, 0, 0, 0.05f);

    private FluentMenu? ChildOf(int index) => _child is { } c && c._items == _items[index].Children ? c : null;

    // ---------- 交互 ----------

    private int RowAt(double y)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (y >= _rows[i].Top && y < _rows[i].Bottom) return _items[i].IsSeparator ? -1 : i;
        return -1;
    }

    private void SetHover(int index, bool openChild)
    {
        if (index == _hover && (!openChild || ChildOf(Math.Max(0, index)) is not null || index < 0)) return;
        _hover = index;
        User32.InvalidateRect(Handle, null, 0);
        if (!openChild) return;
        if (index >= 0 && _items[index] is { Enabled: true, Children.Count: > 0 }) OpenChild(index, activate: false);
        else CloseChild();
    }

    private void OpenChild(int index, bool activate)
    {
        if (ChildOf(index) is { } existing)
        {
            if (activate) existing.ShowAndActivate();
            return;
        }
        CloseChild();
        User32.RECT wr;
        User32.GetWindowRect(Handle, &wr);
        var row = _rows[index];
        int x = wr.Right - (int)(4 * _s), y = wr.Top + (int)row.Top - (int)(PadDip * _s);
        _child = new FluentMenu(_items[index].Children!, _style, this, x, y, false, alignRight: wr.Left + 4 * _s);
        if (activate)
        {
            _child._hover = _child._items.ToList().FindIndex(e => !e.IsSeparator && e.Enabled);
            _child.ShowAndActivate();
        }
        else
        {
            User32.ShowWindow(_child.Handle, 4 /* SW_SHOWNOACTIVATE */);
        }
        User32.InvalidateRect(Handle, null, 0);
    }

    private void CloseChild()
    {
        if (_child is null) return;
        _child.CloseChild();
        if (_child.Handle != 0) User32.DestroyWindow(_child.Handle);
        _child = null;
        User32.InvalidateRect(Handle, null, 0);
    }

    private void Choose(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var it = _items[index];
        if (it.IsSeparator || !it.Enabled) return;
        if (it.Children is { Count: > 0 })
        {
            OpenChild(index, activate: true);
            return;
        }
        _root._result = it;
        _root._done = true;
    }

    private void Move(int step)
    {
        int n = _items.Count, i = _hover;
        for (int k = 0; k < n; k++)
        {
            i = ((i < 0 ? (step > 0 ? -1 : 0) : i) + step + n) % n;
            if (!_items[i].IsSeparator && _items[i].Enabled) break;
        }
        SetHover(i, openChild: false);
    }

    /// <summary>本菜单链里的窗口（根菜单与各级子菜单）。</summary>
    private bool InChain(nint hwnd)
    {
        for (var m = _root; m is not null; m = m._child)
            if (m.Handle == hwnd) return true;
        return false;
    }

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case 0x000F /* WM_PAINT */:
                Paint();
                return 0;
            case 0x0014 /* WM_ERASEBKGND */:
                return 1;
            case 0x0021 /* WM_MOUSEACTIVATE */:
                return 3; // MA_NOACTIVATE：点菜单不改变激活（子菜单点下去不让父菜单失去激活）
            case 0x0006 /* WM_ACTIVATE */ when (wParam & 0xFFFF) == 0:
                // 激活跑到菜单以外的窗口（点了外面、切了程序）：整串关闭
                if (!InChain(lParam)) _root._done = true;
                return 0;
            case 0x0200 /* WM_MOUSEMOVE */:
                if (!_tracking)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = Handle };
                    TrackMouseEvent(&tme);
                    _tracking = true;
                }
                SetHover(RowAt((short)((lParam >> 16) & 0xFFFF)), openChild: true);
                return 0;
            case 0x02A3 /* WM_MOUSELEAVE */:
                _tracking = false;
                if (_child is null) SetHover(_style == MenuStyle.Dropdown ? _hover : -1, openChild: false);
                return 0;
            case 0x0202 /* WM_LBUTTONUP */:
                Choose(RowAt((short)((lParam >> 16) & 0xFFFF)));
                return 0;
            case 0x0100 /* WM_KEYDOWN */:
                switch ((uint)wParam)
                {
                    case 0x26: Move(-1); break; // ↑
                    case 0x28: Move(1); break; // ↓
                    case 0x0D or 0x20: Choose(_hover); break; // 回车、空格
                    case 0x27 when _hover >= 0 && _items[_hover].Children is { Count: > 0 }: OpenChild(_hover, activate: true); break; // →
                    case 0x25 or 0x1B when _parent is not null: // ← / Esc：回到上一级
                        _parent.CloseChild();
                        _parent.ShowAndActivate();
                        break;
                    case 0x1B: _root._done = true; break;
                }
                return 0;
        }
        return null;
    }

    protected override void OnDestroyed() => _canvas?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize, dwFlags;
        public nint hwndTrack;
        public uint dwHoverTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackMouseEvent(TRACKMOUSEEVENT* tme);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(User32.POINT point, uint flags);
}
