using System.Runtime.InteropServices;
using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Platform.Native;
using GlideShell.Platform.Ui.Settings;

namespace GlideShell.Platform.Ui;

/// <summary>设置窗口需要宿主提供的环境。</summary>
/// <param name="DesktopsAvailable">虚拟桌面接口自检是否通过（不通过时切换桌面不可用）。</param>
/// <param name="RunningApps">当前有窗口的程序（进程名），供「排除的程序」挑选。</param>
/// <param name="Save">保存：写配置并立即生效，返回问题列表（空 = 成功）。</param>
/// <param name="OpenConfigFile">打开配置文件（界面以外的高级参数）。</param>
/// <param name="Version">版本号（「关于」页显示）。</param>
/// <param name="OpenLogs">打开日志目录（没有时不显示这一项）。</param>
public sealed record SettingsEnvironment(
    bool DesktopsAvailable,
    Func<IReadOnlyList<string>> RunningApps,
    Func<SettingsModel, IReadOnlyList<string>> Save,
    Action OpenConfigFile,
    string Version = "",
    Action? OpenLogs = null);

/// <summary>
/// 设置窗口：Windows 11「设置」的样子——Mica 背景、左侧导航、右侧一页页圆角卡片（图标 + 标题 + 一句说明 + 右侧控件），
/// 开关、滑块、下拉、分段按 Fluent 控件自画（<see cref="UiCanvas"/> 用 Direct2D 画整个客户区），跟随系统浅色 / 深色与强调色。
/// 改动立即生效（稍等片刻写配置，不用「保存」）；设置有冲突时页顶出现提示条、先不写。
/// 键盘：Tab / Shift+Tab 移动焦点，空格 / 回车操作，←/→ 调滑块与分段，Esc 关闭。同一时间只开一个，只能在 UI 线程上使用。
/// </summary>
public sealed unsafe partial class SettingsWindow : NativeWindow
{
    private const int DefaultWidth = 1000, DefaultHeight = 720, MinWidth = 760, MinHeight = 520; // DIP
    private const double NavWidthDip = 280, ContentMaxDip = 960;
    private const uint Style = 0x00CF0000; // WS_OVERLAPPEDWINDOW
    private const int SaveDelayMs = 350;

    private static SettingsWindow? s_current;

    private readonly UiThread _ui;
    private readonly SettingsEnvironment _env;
    private SettingsModel _model;
    private FluentTheme _theme = FluentTheme.Current();
    private readonly UiCanvas? _canvas;
    private uint _dpi = 96;
    private bool _mica;
    private int _page;
    private double _scroll, _contentHeight;
    private readonly List<Item> _nav = [];
    private readonly List<Item> _items = [];
    private readonly HashSet<string> _expanded = [];
    private readonly Dictionary<string, double> _anim = []; // 开关滑块的位置（过渡动画）
    private string? _hover, _pressed, _focus;
    private bool _keyboardFocus, _tracking, _timer;
    private IReadOnlyList<string> _problems = [];
    private int _saveGeneration;
    private bool _savePending;
    private nint _edit, _editFont, _editBrush; // 「输入进程名」用系统的编辑框
    private RectD? _editRect;

    private SettingsWindow(UiThread ui, SettingsModel model, SettingsEnvironment env)
    {
        _ui = ui;
        _model = model;
        _env = env;
        _canvas = UiCanvas.Create();
        var area = TargetArea();
        Create("GlideShell.Settings", Style, 0, (int)area.Left + 40, (int)area.Top + 40, 100, 100);
        User32.SetWindowTextW(Handle, "GlideShell 设置");
        AllowShellMessages(Handle);
        _dpi = Math.Max(96, User32.GetDpiForWindow(Handle));
        ApplyTheme();
        PlaceCentered(area);
        User32.SendMessageW(Handle, WM_SETICON, ICON_SMALL, AppIcon.Load(16 * (int)_dpi / 96));
        User32.SendMessageW(Handle, WM_SETICON, ICON_BIG, AppIcon.Load(32 * (int)_dpi / 96));
        Relayout();
    }

    private double S => _dpi / 96.0;

    /// <summary>打开设置窗口；已经开着就把它带到前台。</summary>
    public static SettingsWindow ShowOrActivate(UiThread ui, SettingsModel model, SettingsEnvironment env)
    {
        if (s_current is { Handle: not 0 } open)
        {
            User32.ShowWindow(open.Handle, User32.SW_RESTORE);
            User32.SetForegroundWindow(open.Handle);
            return open;
        }
        var window = CreateHidden(ui, model, env);
        User32.ShowWindow(window.Handle, User32.SW_SHOWNORMAL);
        User32.SetForegroundWindow(window.Handle);
        return window;
    }

    /// <summary>创建但不显示（<see cref="ShowOrActivate"/> 内部使用；也供离屏截图检查布局）。</summary>
    public static SettingsWindow CreateHidden(UiThread ui, SettingsModel model, SettingsEnvironment env)
    {
        var window = new SettingsWindow(ui, model, env);
        s_current = window;
        return window;
    }

    /// <summary>当前编辑中的模型（测试与诊断用）。</summary>
    public SettingsModel Model => _model;

    /// <summary>当前页（测试与截图用）。</summary>
    public int Page
    {
        get => _page;
        set
        {
            _page = Math.Clamp(value, 0, PageCount - 1);
            _scroll = 0;
            Relayout();
        }
    }

    // ---------- 外观 ----------

    /// <summary>Mica 背景（Windows 11），深色标题栏跟随主题；深色时弹出菜单也用深色。不支持 Mica 时画纯色背景。</summary>
    private void ApplyTheme()
    {
        _theme = FluentTheme.Current();
        int dark = _theme.Dark ? 1 : 0, backdrop = 2 /* DWMSBT_MAINWINDOW */;
        Dwmapi.DwmSetWindowAttribute(Handle, Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(int));
        var margins = new Dwmapi.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _mica = Environment.OSVersion.Version.Build >= 22000 &&
                Dwmapi.DwmExtendFrameIntoClientArea(Handle, &margins) == 0 &&
                Dwmapi.DwmSetWindowAttribute(Handle, Dwmapi.DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(int)) == 0;
        DarkMenus(_theme.Dark);
        if (_editBrush != 0) Gdi32.DeleteObject(_editBrush);
        _editBrush = Gdi32.CreateSolidBrush(_theme.Dark ? Gdi32.Rgb(45, 45, 45) : Gdi32.Rgb(255, 255, 255));
        if (_edit != 0) User32.InvalidateRect(_edit, null, 1);
    }

    /// <summary>未公开的 uxtheme#135 SetPreferredAppMode：弹出菜单跟随深色。没有就算了。</summary>
    private static void DarkMenus(bool dark)
    {
        nint ux = LoadLibraryW("uxtheme.dll");
        if (ux == 0) return;
        var setMode = (delegate* unmanaged[Stdcall]<int, int>)GetProcAddress(ux, 135);
        var flush = (delegate* unmanaged[Stdcall]<void>)GetProcAddress(ux, 136);
        if (setMode != null) setMode(dark ? 2 /* ForceDark */ : 3 /* ForceLight */);
        if (flush != null) flush();
    }

    // ---------- 布局 ----------

    private void Relayout()
    {
        User32.RECT rc;
        User32.GetClientRect(Handle, &rc);
        double s = S, w = rc.Right, h = rc.Bottom;
        _nav.Clear();
        double y = 72 * s;
        for (int i = 0; i < PageCount; i++)
        {
            int page = i;
            _nav.Add(new Item(ItemKind.Nav, $"nav{i}")
            {
                Rect = new RectD(8 * s, y, NavWidthDip * s - 16 * s, 36 * s), Text = PageTitle(i), Icon = PageIcon(i), Selected = i == _page,
                Click = () => Page = page,
            });
            y += 40 * s;
        }

        _items.Clear();
        _editRect = null;
        if (_canvas is not null)
        {
            double left = NavWidthDip * s + 28 * s;
            double width = Math.Min(ContentMaxDip * s, w - left - 36 * s);
            var b = new PageBuilder(_canvas, s, left, Math.Max(200 * s, width), 32 * s, _items);
            BuildPage(b);
            _contentHeight = b.Y + 40 * s;
        }
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentHeight - h));
        PlaceEdit();
        User32.InvalidateRect(Handle, null, 0);
    }

    /// <summary>内容区（导航右边），滚动只作用在这里。</summary>
    private RectD ContentArea
    {
        get
        {
            User32.RECT rc;
            User32.GetClientRect(Handle, &rc);
            double x = NavWidthDip * S;
            return new RectD(x, 0, Math.Max(0, rc.Right - x), rc.Bottom);
        }
    }

    private RectD OnScreen(Item it) => it.Kind == ItemKind.Nav ? it.Rect : it.Rect.Offset(0, -_scroll);

    // ---------- 画 ----------

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
            double s = S;
            if (!_mica) _canvas.Fill(new RectD(0, 0, rc.Right, rc.Bottom), 0, t.Background);

            // 左上角：程序名
            _canvas.Text("GlideShell", new RectD(24 * s, 18 * s, NavWidthDip * s - 40 * s, 22 * s), new UiFont(FluentTheme.Font, 14 * s, 600), t.TextPrimary, UiAlign.Left, false);
            _canvas.Text("触摸板手势设置", new RectD(24 * s, 40 * s, NavWidthDip * s - 40 * s, 18 * s), new UiFont(FluentTheme.Font, 12 * s), t.TextSecondary, UiAlign.Left, false);
            foreach (var it in _nav) PaintItem(it);

            var content = ContentArea;
            _canvas.PushClip(content);
            foreach (var it in _items)
            {
                var r = OnScreen(it);
                if (r.Bottom < content.Top - 4 * s || r.Top > content.Bottom + 4 * s) continue;
                PaintItem(it);
            }
            // 滚动条（内容超出时，细细一条）
            if (_contentHeight > content.Height + 1)
            {
                double track = content.Height - 8 * s, k = content.Height / _contentHeight;
                double barH = Math.Max(32 * s, track * k), barY = 4 * s + (track - barH) * (_scroll / (_contentHeight - content.Height));
                _canvas.Fill(new RectD(content.Right - 7 * s, barY, 3 * s, barH), 1.5 * s, t.TextTertiary);
            }
            _canvas.PopClip();
            _canvas.Present(hdc);
        }
        finally
        {
            User32.EndPaint(Handle, &ps);
        }
    }

    private void PaintItem(Item it)
    {
        double anim = 0;
        if (it.Kind == ItemKind.Toggle)
        {
            double target = it.On?.Invoke() == true ? 1 : 0;
            if (!_anim.TryGetValue(it.Key, out anim)) _anim[it.Key] = anim = target;
        }
        ItemPainter.Paint(_canvas!, _theme, S, it, OnScreen(it), it.Key == _hover, it.Key == _pressed, _keyboardFocus && it.Key == _focus, anim);
    }

    // ---------- 交互 ----------

    private IEnumerable<Item> Interactive => _nav.Concat(_items).Where(i => i.Interactive);

    private Item? HitTest(double x, double y)
    {
        var content = ContentArea;
        foreach (var it in Interactive.Reverse())
        {
            if (it.Kind != ItemKind.Nav && !content.Contains(x, y)) continue;
            if (OnScreen(it).Contains(x, y)) return it;
        }
        return null;
    }

    private Item? Find(string? key) => key is null ? null : _nav.Concat(_items).FirstOrDefault(i => i.Key == key);

    private void OnMouseMove(double x, double y)
    {
        if (!_tracking)
        {
            var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = 2 /* TME_LEAVE */, hwndTrack = Handle };
            TrackMouseEvent(&tme);
            _tracking = true;
        }
        if (_pressed is not null && Find(_pressed) is { Kind: ItemKind.Slider } slider)
        {
            SetSlider(slider, x);
            return;
        }
        string? hover = HitTest(x, y)?.Key;
        if (hover != _hover)
        {
            _hover = hover;
            User32.InvalidateRect(Handle, null, 0);
        }
    }

    private void OnMouseDown(double x, double y)
    {
        var it = HitTest(x, y);
        _pressed = it?.Key;
        _keyboardFocus = false;
        User32.SetFocus(Handle);
        if (it is null) return;
        _focus = it.Key;
        User32.SetCapture(Handle);
        if (it.Kind == ItemKind.Slider) SetSlider(it, x);
        User32.InvalidateRect(Handle, null, 0);
    }

    private void OnMouseUp(double x, double y)
    {
        string? pressed = _pressed;
        _pressed = null;
        User32.ReleaseCapture();
        var it = HitTest(x, y);
        if (it is not null && it.Key == pressed && it.Kind != ItemKind.Slider) Activate(it, x);
        User32.InvalidateRect(Handle, null, 0);
    }

    /// <summary>点击 / 空格 / 回车：按元素种类操作。<paramref name="x"/> 用于分段控件选哪一段（键盘时为 NaN）。</summary>
    private void Activate(Item it, double x = double.NaN)
    {
        if (!it.Enabled) return;
        switch (it.Kind)
        {
            case ItemKind.Toggle:
                it.SetOn?.Invoke(it.On?.Invoke() != true);
                StartAnimation();
                Changed();
                break;
            case ItemKind.Dropdown:
            case ItemKind.Corner:
            {
                var r = OnScreen(it);
                int chosen = ShowMenu(it.Options, it.Index?.Invoke() ?? -1, r.Left, r.Bottom + 4 * S, r.Width);
                if (chosen >= 0 && chosen != (it.Index?.Invoke() ?? -1))
                {
                    it.SetIndex?.Invoke(chosen);
                    Changed();
                }
                break;
            }
            case ItemKind.Segmented:
            {
                int n = Math.Max(1, it.Options.Count);
                int i = double.IsNaN(x) ? ((it.Index?.Invoke() ?? 0) + 1) % n : (int)((x - it.Rect.Left) / (it.Rect.Width / n));
                it.SetIndex?.Invoke(Math.Clamp(i, 0, n - 1));
                Changed();
                break;
            }
            case ItemKind.Expander:
                if (!_expanded.Remove(it.Key)) _expanded.Add(it.Key);
                Relayout();
                break;
            default:
                it.Click?.Invoke();
                break;
        }
    }

    private void SetSlider(Item it, double x)
    {
        var track = ItemPainter.SliderTrack(OnScreen(it), S);
        double f = Math.Clamp((x - track.Left) / Math.Max(1, track.Width), 0, 1);
        SetSliderValue(it, it.Min + f * (it.Max - it.Min));
    }

    private void SetSliderValue(Item it, double v)
    {
        double snapped = Math.Round(Math.Round((v - it.Min) / it.Step) * it.Step + it.Min, 4);
        snapped = Math.Clamp(snapped, it.Min, it.Max);
        if (Math.Abs(snapped - (it.Value?.Invoke() ?? double.NaN)) < 1e-9) return;
        it.SetValue?.Invoke(snapped);
        Changed();
    }

    private void OnWheel(int delta)
    {
        double before = _scroll;
        _scroll = Math.Clamp(_scroll - delta / 120.0 * 64 * S, 0, Math.Max(0, _contentHeight - ContentArea.Height));
        if (_scroll == before) return;
        PlaceEdit();
        User32.InvalidateRect(Handle, null, 0);
    }

    private bool OnKey(uint vk)
    {
        var focused = Find(_focus);
        switch (vk)
        {
            case 0x09: // Tab
            {
                bool back = GetKeyState(0x10) < 0;
                var list = Interactive.ToList();
                if (list.Count == 0) return true;
                int i = focused is null ? -1 : list.FindIndex(x => x.Key == focused.Key);
                i = back ? (i <= 0 ? list.Count - 1 : i - 1) : (i + 1) % list.Count;
                _focus = list[i].Key;
                _keyboardFocus = true;
                ScrollIntoView(list[i]);
                User32.InvalidateRect(Handle, null, 0);
                return true;
            }
            case 0x20 or 0x0D when focused is not null: // 空格、回车
                _keyboardFocus = true;
                Activate(focused);
                User32.InvalidateRect(Handle, null, 0);
                return true;
            case 0x25 or 0x27 when focused is { Kind: ItemKind.Slider }: // ← →
                SetSliderValue(focused, (focused.Value?.Invoke() ?? 0) + (vk == 0x27 ? focused.Step : -focused.Step));
                return true;
            case 0x25 or 0x27 when focused is { Kind: ItemKind.Segmented }:
            {
                int i = Math.Clamp((focused.Index?.Invoke() ?? 0) + (vk == 0x27 ? 1 : -1), 0, focused.Options.Count - 1);
                focused.SetIndex?.Invoke(i);
                Changed();
                return true;
            }
            case 0x26 or 0x28 when focused is { Kind: ItemKind.Nav }: // ↑ ↓ 在导航里换页
            {
                int next = Math.Clamp(_page + (vk == 0x28 ? 1 : -1), 0, PageCount - 1);
                Page = next;
                _focus = $"nav{next}";
                _keyboardFocus = true;
                return true;
            }
            case 0x1B: // Esc
                User32.PostMessageW(Handle, 0x0010 /* WM_CLOSE */, 0, 0);
                return true;
        }
        return false;
    }

    private void ScrollIntoView(Item it)
    {
        if (it.Kind == ItemKind.Nav) return;
        var content = ContentArea;
        double top = it.Rect.Top - 16 * S, bottom = it.Rect.Bottom + 16 * S;
        if (top < _scroll) _scroll = Math.Max(0, top);
        else if (bottom > _scroll + content.Height) _scroll = bottom - content.Height;
        PlaceEdit();
    }

    /// <summary>
    /// 弹出选项列表（Fluent 风格的菜单：圆角、亚克力、选中项左边一条强调色），列表至少和控件一样宽，返回选中的下标，取消返回 -1。
    /// </summary>
    private int ShowMenu(IReadOnlyList<string> options, int current, double x, double y, double width = 0)
    {
        var entries = options.Select((o, i) => new MenuEntry(o, Checked: i == current, Tag: i)).ToList();
        var pt = new User32.POINT { X = (int)x, Y = (int)y };
        ClientToScreen(Handle, &pt);
        var chosen = FluentMenu.Show(entries, pt.X, pt.Y, MenuStyle.Dropdown, minWidth: (int)width);
        return chosen?.Tag is int i ? i : -1;
    }

    // ---------- 开关动画 ----------

    private void StartAnimation()
    {
        if (_timer) return;
        _timer = true;
        SetTimer(Handle, 1, 15, 0);
    }

    private void OnTimer()
    {
        bool moving = false;
        foreach (var it in _items.Where(i => i.Kind == ItemKind.Toggle))
        {
            double target = it.On?.Invoke() == true ? 1 : 0;
            double cur = _anim.GetValueOrDefault(it.Key, target);
            double next = cur + Math.Sign(target - cur) * Math.Min(Math.Abs(target - cur), 15 / 140.0);
            _anim[it.Key] = next;
            if (Math.Abs(next - target) > 1e-6) moving = true;
        }
        User32.InvalidateRect(Handle, null, 0);
        if (moving) return;
        KillTimer(Handle, 1);
        _timer = false;
    }

    // ---------- 即时生效 ----------

    /// <summary>模型改了：重新布局（说明文字可能跟着变），稍等片刻写配置（拖滑块时不会每一步都写）。</summary>
    private void Changed()
    {
        _problems = _model.Validate();
        Relayout();
        if (_problems.Count > 0) return; // 有冲突：先不写，页顶提示
        int generation = ++_saveGeneration;
        _savePending = true;
        _ui.PostDelayed(SaveDelayMs, () =>
        {
            if (generation == _saveGeneration && Handle != 0) SaveNow();
        });
    }

    private void SaveNow()
    {
        if (!_savePending) return;
        _savePending = false;
        var problems = _env.Save(_model);
        if (problems.Count > 0 && Handle != 0)
        {
            _problems = problems;
            Relayout();
        }
    }

    // ---------- 「输入进程名」编辑框 ----------

    private void PlaceEdit()
    {
        if (_editRect is not { } r)
        {
            if (_edit != 0) User32.ShowWindow(_edit, User32.SW_HIDE);
            return;
        }
        if (_edit == 0)
        {
            fixed (char* cls = "EDIT")
                _edit = User32.CreateWindowExW(0, cls, null, User32.WS_CHILD | User32.WS_TABSTOP | 0x0080 /* ES_AUTOHSCROLL */, 0, 0, 10, 10, Handle, 900,
                    Kernel32.GetModuleHandleW(null), 0);
            fixed (char* hint = "例如 mstsc") User32.SendMessageW(_edit, 0x1501 /* EM_SETCUEBANNER */, 1, (nint)hint);
        }
        if (_editFont != 0) Gdi32.DeleteObject(_editFont);
        _editFont = Gdi32.CreateFontW(-(int)Math.Round(14 * S), 0, 0, 0, Gdi32.FW_NORMAL, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        User32.SendMessageW(_edit, User32.WM_SETFONT, _editFont, 1);
        var screen = r.Offset(0, -_scroll).Inflate(-10 * S, -7 * S);
        var content = ContentArea;
        bool visible = screen.Top >= content.Top && screen.Bottom <= content.Bottom;
        User32.SetWindowPos(_edit, 0, (int)screen.Left, (int)screen.Top, (int)screen.Width, (int)screen.Height,
            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | (visible ? User32.SWP_SHOWWINDOW : 0x0080u /* SWP_HIDEWINDOW */));
    }

    private string EditText()
    {
        if (_edit == 0) return "";
        int len = User32.GetWindowTextLengthW(_edit);
        if (len <= 0) return "";
        var buffer = new char[len + 1];
        fixed (char* p = buffer) return new string(p, 0, User32.GetWindowTextW(_edit, p, buffer.Length));
    }

    private void ClearEdit()
    {
        if (_edit != 0) User32.SetWindowTextW(_edit, "");
    }

    // ---------- 窗口消息 ----------

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case 0x0006 /* WM_ACTIVATE */ when (wParam & 0xFFFF) != 0:
                // UIAccess 版：任务栏（普通权限）能激活这个窗口，但把它提到最上层的那一步被消息隔离拦掉了——
                // 窗口成了前台却压在别的窗口下面，再点任务栏按钮就被当成「最小化活动窗口」。自己提到最上层
                User32.SetWindowPos(Handle, 0 /* HWND_TOP */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE | NOMOVE | NOACTIVATE */);
                return null;
            case 0x000F /* WM_PAINT */:
                Paint();
                return 0;
            case 0x0014 /* WM_ERASEBKGND */:
                return 1;
            case 0x0005 /* WM_SIZE */:
                Relayout();
                return 0;
            case 0x0024 /* WM_GETMINMAXINFO */:
            {
                var min = (User32.POINT*)(lParam + 24); // MINMAXINFO.ptMinTrackSize
                min->X = (int)(MinWidth * S);
                min->Y = (int)(MinHeight * S);
                return 0;
            }
            case 0x0200 /* WM_MOUSEMOVE */:
                OnMouseMove((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case 0x02A3 /* WM_MOUSELEAVE */:
                _tracking = false;
                if (_hover is not null)
                {
                    _hover = null;
                    User32.InvalidateRect(Handle, null, 0);
                }
                return 0;
            case 0x0201 /* WM_LBUTTONDOWN */:
                OnMouseDown((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case 0x0202 /* WM_LBUTTONUP */:
                OnMouseUp((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case 0x020A /* WM_MOUSEWHEEL */:
                OnWheel((short)((wParam >> 16) & 0xFFFF));
                return 0;
            case 0x0100 /* WM_KEYDOWN */:
                return OnKey((uint)wParam) ? 0 : null;
            case 0x0113 /* WM_TIMER */:
                OnTimer();
                return 0;
            case 0x0020 /* WM_SETCURSOR */ when wParam == Handle && (lParam & 0xFFFF) == 1 /* HTCLIENT */:
                User32.SetCursor(User32.LoadCursorW(0, User32.IDC_ARROW));
                return 1;
            case 0x0133 /* WM_CTLCOLOREDIT */:
                Gdi32.SetTextColor(wParam, _theme.Dark ? Gdi32.Rgb(255, 255, 255) : Gdi32.Rgb(26, 26, 26));
                Gdi32.SetBkColor(wParam, _theme.Dark ? Gdi32.Rgb(45, 45, 45) : Gdi32.Rgb(255, 255, 255));
                return _editBrush;
            case 0x001A /* WM_SETTINGCHANGE */:
            case 0x031A /* WM_THEMECHANGED */:
            case 0x0320 /* WM_DWMCOLORIZATIONCOLORCHANGED */:
                // 系统切换浅色 / 深色、换强调色
                ApplyTheme();
                _anim.Clear();
                Relayout();
                return null;
            case 0x02E0 /* WM_DPICHANGED */:
            {
                _dpi = (uint)(wParam & 0xFFFF);
                var r = (User32.RECT*)lParam;
                User32.SetWindowPos(Handle, 0, r->Left, r->Top, r->Width, r->Height, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                Relayout();
                return 0;
            }
        }
        return null;
    }

    protected override void OnDestroyed()
    {
        SaveNow(); // 还没写的改动现在写
        if (ReferenceEquals(s_current, this)) s_current = null;
        if (_editFont != 0) Gdi32.DeleteObject(_editFont);
        if (_editBrush != 0) Gdi32.DeleteObject(_editBrush);
        _editFont = _editBrush = 0;
        _canvas?.Dispose();
    }

    /// <summary>
    /// UIAccess 版：系统的消息隔离（UIPI）会拦掉普通权限进程发来的大部分消息，任务栏（资源管理器）点按钮时发的
    /// 还原 / 关闭（WM_SYSCOMMAND）、右键菜单（WM_POPUPSYSTEMMENU）都到不了，点任务栏按钮窗口不会到前台。放行这几条。
    /// </summary>
    private static void AllowShellMessages(nint hwnd)
    {
        foreach (uint msg in (uint[])[0x0112 /* WM_SYSCOMMAND */, 0x0313 /* WM_POPUPSYSTEMMENU */, 0x0006 /* WM_ACTIVATE */, 0x0086 /* WM_NCACTIVATE */])
            ChangeWindowMessageFilterEx(hwnd, msg, 1 /* MSGFLT_ALLOW */, 0);
    }

    /// <summary>鼠标所在显示器的工作区（没有显示器信息时给一个保底区域）。</summary>
    private static RectD TargetArea()
    {
        User32.POINT p;
        User32.GetCursorPos(&p);
        int x = p.X, y = p.Y;
        var monitors = Shell.Monitors.Enumerate();
        return (monitors.FirstOrDefault(m => m.Bounds.Contains(x, y)) ?? monitors.FirstOrDefault())?.WorkArea ?? new RectD(0, 0, 1280, 800);
    }

    /// <summary>按 DPI 算出窗口大小（不超过工作区），放在工作区中央。</summary>
    private void PlaceCentered(RectD area)
    {
        int cw = (int)Math.Min(DefaultWidth * S, area.Width - 40), ch = (int)Math.Min(DefaultHeight * S, area.Height - 40);
        var r = new User32.RECT { Right = cw, Bottom = ch };
        User32.AdjustWindowRectExForDpi(&r, Style, false, 0, _dpi);
        int w = Math.Min(r.Width, (int)area.Width), h = Math.Min(r.Height, (int)area.Height);
        int x = (int)(area.Left + (area.Width - w) / 2), y = (int)(area.Top + Math.Max(0, (area.Height - h) / 2));
        User32.SetWindowPos(Handle, 0, x, y, w, h, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
    }

    private const uint WM_SETICON = 0x0080;
    private const nint ICON_SMALL = 0, ICON_BIG = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize, dwFlags;
        public nint hwndTrack;
        public uint dwHoverTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeFilterStruct);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackMouseEvent(TRACKMOUSEEVENT* tme);

    [LibraryImport("user32.dll")]
    private static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KillTimer(nint hwnd, nuint id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hwnd, User32.POINT* point);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int vk);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryW(string name);

    [LibraryImport("kernel32.dll")]
    private static partial void* GetProcAddress(nint module, nint ordinal);
}
