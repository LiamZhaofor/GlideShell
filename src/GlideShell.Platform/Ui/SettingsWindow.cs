using GlideShell.Core.Configuration;
using GlideShell.Core.Gestures;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>设置窗口需要宿主提供的环境。</summary>
/// <param name="DesktopsAvailable">虚拟桌面接口自检是否通过（不通过时切换桌面不可用）。</param>
/// <param name="RunningApps">当前有窗口的程序（进程名），供「不使用手势的程序」挑选。</param>
/// <param name="Save">保存：写配置并立即生效，返回问题列表（空 = 成功，窗口关闭）。</param>
/// <param name="OpenConfigFile">打开配置文件（界面以外的高级参数）。</param>
public sealed record SettingsEnvironment(
    bool DesktopsAvailable,
    Func<IReadOnlyList<string>> RunningApps,
    Func<SettingsModel, IReadOnlyList<string>> Save,
    Action OpenConfigFile);

/// <summary>
/// 设置窗口：Win32 标准控件（跟随系统主题），Per-Monitor DPI，Tab / Enter / Esc 导航。
/// 只编辑 <see cref="SettingsModel"/>；「保存」时交给宿主写配置。同一时间只开一个，只能在 UI 线程上使用。
/// 布局以 96 DPI 下的逻辑像素写死（两栏），按窗口所在显示器的 DPI 缩放。
/// </summary>
public sealed unsafe class SettingsWindow : NativeWindow
{
    private const int ClientWidth = 760, ClientHeight = 566;
    private const uint Style = User32.WS_OVERLAPPED | User32.WS_CAPTION | User32.WS_SYSMENU | User32.WS_MINIMIZEBOX;
    private const int IdOk = 1, IdCancel = 2; // IsDialogMessage 把 Enter / Esc 翻译成这两个命令
    private const int IdGestureBase = 100, IdDrag = 110, IdFullscreen = 111, IdAutostart = 112;
    private const int IdPresetBase = 120, IdRemove = 130, IdAdd = 131, IdDefaults = 140, IdOpenConfig = 141;

    private static SettingsWindow? s_current;
    private static bool s_commonControls;

    private readonly UiThread _ui;
    private readonly SettingsEnvironment _env;
    private readonly List<(nint Hwnd, int X, int Y, int W, int H)> _layout = [];
    private readonly nint[] _gestureCombos = new nint[SettingsModel.Slots.Count];
    private readonly List<Slider> _sliders = [];
    private readonly HashSet<nint> _notes = [];
    private SettingsModel _model;
    private nint _hwnd;
    private nint _font;
    private uint _dpi = 96;
    private nint _drag, _fullscreen, _autostart, _presetLabel, _desktopNote, _excludedList, _addCombo;
    private IReadOnlyList<string> _runningApps = [];

    private sealed class Slider(string label, double min, double max, double step, Func<double, string> format,
        Func<SettingsModel, double> get, Func<SettingsModel, double, SettingsModel> set)
    {
        public string Label { get; } = label;
        public double Min { get; } = min;
        public double Step { get; } = step;
        public int Steps { get; } = (int)Math.Round((max - min) / step);
        public Func<double, string> Format { get; } = format;
        public Func<SettingsModel, double> Get { get; } = get;
        public Func<SettingsModel, double, SettingsModel> Set { get; } = set;
        public nint Track { get; set; }
        public nint Value { get; set; }
    }

    private SettingsWindow(UiThread ui, SettingsModel model, SettingsEnvironment env)
    {
        _ui = ui;
        _model = model;
        _env = env;
        // 直接建在目标显示器上（鼠标所在屏），这样一开始拿到的就是那块屏的 DPI，不会显示后再缩放一次
        var area = TargetArea();
        Create("GlideShell.Settings", Style, 0, (int)area.Left + 40, (int)area.Top + 40, 100, 100);
        _hwnd = Handle;
        User32.SetWindowTextW(Handle, "GlideShell 设置");
        _dpi = Math.Max(96, User32.GetDpiForWindow(Handle));
        _font = CreateFont(_dpi);
        BuildControls();
        Layout();
        RefreshAll();
        PlaceCentered(area);
        User32.SendMessageW(Handle, WM_SETICON, ICON_SMALL, AppIcon.Load(16 * (int)_dpi / 96));
        User32.SendMessageW(Handle, WM_SETICON, ICON_BIG, AppIcon.Load(32 * (int)_dpi / 96));
    }

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
        if (!s_commonControls)
        {
            var icc = new User32.INITCOMMONCONTROLSEX { dwSize = (uint)sizeof(User32.INITCOMMONCONTROLSEX), dwICC = User32.ICC_BAR_CLASSES | User32.ICC_STANDARD_CLASSES };
            User32.InitCommonControlsEx(&icc);
            s_commonControls = true;
        }
        var window = new SettingsWindow(ui, model, env);
        s_current = window;
        ui.DialogWindow = window.Handle;
        return window;
    }

    /// <summary>当前编辑中的模型（测试与诊断用）。</summary>
    public SettingsModel Model => _model;

    // ---------- 控件 ----------

    private void BuildControls()
    {
        // 左栏：手势
        Group("手势", 16, 12, 360, 270);
        for (int i = 0; i < SettingsModel.Slots.Count; i++)
        {
            int y = 40 + 34 * i;
            Label(SettingsModel.Slots[i].Label, 32, y + 4, 104, 20);
            _gestureCombos[i] = Child("COMBOBOX", null, User32.CBS_DROPDOWNLIST | User32.WS_VSCROLL | User32.WS_TABSTOP, IdGestureBase + i, 140, y, 220, 240);
        }
        _drag = Child("BUTTON", "三指拖移（三指滑动 = 按住左键拖动）", User32.BS_AUTOCHECKBOX | User32.WS_TABSTOP, IdDrag, 32, 178, 330, 22);
        _fullscreen = Child("BUTTON", "全屏程序在前台时暂停手势", User32.BS_AUTOCHECKBOX | User32.WS_TABSTOP, IdFullscreen, 32, 204, 330, 22);
        _desktopNote = Note("", 32, 232, 330, 40);

        // 左栏：不使用手势的程序
        Group("不使用手势的程序", 16, 292, 360, 214);
        _excludedList = Child("LISTBOX", null, User32.LBS_NOTIFY | User32.LBS_NOINTEGRALHEIGHT | User32.WS_VSCROLL | User32.WS_TABSTOP, 0,
            32, 318, 240, 112, User32.WS_EX_CLIENTEDGE);
        Child("BUTTON", "移除", User32.BS_PUSHBUTTON | User32.WS_TABSTOP, IdRemove, 282, 318, 78, 28);
        _addCombo = Child("COMBOBOX", null, User32.CBS_DROPDOWN | User32.CBS_AUTOHSCROLL | User32.WS_VSCROLL | User32.WS_TABSTOP, 0, 32, 440, 240, 240);
        Child("BUTTON", "添加", User32.BS_PUSHBUTTON | User32.WS_TABSTOP, IdAdd, 282, 439, 78, 28);
        Note("前台是这些程序时，GlideShell 不接管手势。可从下拉框选择正在运行的程序，或输入进程名。", 32, 472, 330, 30);

        // 右栏：灵敏度
        Group("灵敏度", 392, 12, 352, 202);
        Label("预设", 408, 40, 56, 20);
        for (int i = 0; i < SensitivityPreset.All.Count; i++)
            Child("BUTTON", SensitivityPreset.All[i].Label, User32.BS_PUSHBUTTON | User32.WS_TABSTOP, IdPresetBase + i, 468 + 64 * i, 36, 58, 26);
        _presetLabel = Note("", 664, 40, 72, 20);
        AddSlider(new Slider("起步距离", 1, 8, 0.5, v => $"{v:0.0} mm", m => m.SlopMm, (m, v) => m with { SlopMm = v }), 72);
        AddSlider(new Slider("总览：滑满", 10, 60, 1, v => $"{v:0} mm", m => m.OverviewTravelMm, (m, v) => m with { OverviewTravelMm = v }), 106);
        AddSlider(new Slider("切换一个窗口", 10, 50, 1, v => $"{v:0} mm", m => m.MmPerWindow, (m, v) => m with { MmPerWindow = v }), 140);
        AddSlider(new Slider("切换桌面：滑满", 20, 100, 1, v => $"{v:0} mm", m => m.DesktopTravelMm, (m, v) => m with { DesktopTravelMm = v }), 174);

        Group("外观", 392, 224, 352, 62);
        AddSlider(new Slider("总览背景变暗", 0, 1, 0.05, v => $"{v * 100:0}%", m => m.BackdropDim, (m, v) => m with { BackdropDim = v }), 250);

        Group("三指拖移", 392, 296, 352, 96);
        AddSlider(new Slider("指针速度", 4, 40, 1, v => $"{v:0} px/mm", m => m.DragPixelsPerMm, (m, v) => m with { DragPixelsPerMm = v }), 322);
        AddSlider(new Slider("加速", 0, 2, 0.1, v => $"{v:0.0}", m => m.DragAcceleration, (m, v) => m with { DragAcceleration = v }), 356);

        Group("启动", 392, 402, 352, 56);
        _autostart = Child("BUTTON", "登录 Windows 时自动启动 GlideShell", User32.BS_AUTOCHECKBOX | User32.WS_TABSTOP, IdAutostart, 408, 426, 320, 22);

        // 底部按钮
        Child("BUTTON", "恢复默认", User32.BS_PUSHBUTTON | User32.WS_TABSTOP, IdDefaults, 16, 522, 96, 28);
        Child("BUTTON", "打开配置文件…", User32.BS_PUSHBUTTON | User32.WS_TABSTOP, IdOpenConfig, 120, 522, 124, 28);
        Child("BUTTON", "取消", User32.BS_PUSHBUTTON | User32.WS_TABSTOP, IdCancel, 560, 522, 88, 28);
        Child("BUTTON", "保存", User32.BS_DEFPUSHBUTTON | User32.WS_TABSTOP, IdOk, 656, 522, 88, 28);
    }

    private void AddSlider(Slider s, int y)
    {
        Label(s.Label, 408, y + 4, 100, 20);
        s.Track = Child("msctls_trackbar32", null, User32.TBS_HORZ | User32.TBS_NOTICKS | User32.WS_TABSTOP, 0, 510, y, 160, 26);
        s.Value = Label("", 674, y + 4, 64, 20);
        User32.SendMessageW(s.Track, User32.TBM_SETRANGE, 1, s.Steps << 16);
        _sliders.Add(s);
    }

    private void Group(string text, int x, int y, int w, int h) => Child("BUTTON", text, User32.BS_GROUPBOX, 0, x, y, w, h);

    private nint Label(string text, int x, int y, int w, int h) => Child("STATIC", text, User32.SS_LEFT, 0, x, y, w, h);

    private nint Note(string text, int x, int y, int w, int h)
    {
        nint hwnd = Label(text, x, y, w, h);
        _notes.Add(hwnd);
        return hwnd;
    }

    private nint Child(string cls, string? text, uint style, int id, int x, int y, int w, int h, uint exStyle = 0)
    {
        nint hwnd;
        fixed (char* c = cls)
        fixed (char* t = text)
            hwnd = User32.CreateWindowExW(exStyle, c, t, User32.WS_CHILD | User32.WS_VISIBLE | style, 0, 0, 0, 0,
                Handle, id, Kernel32.GetModuleHandleW(null), 0);
        User32.SendMessageW(hwnd, User32.WM_SETFONT, _font, 0);
        _layout.Add((hwnd, x, y, w, h));
        return hwnd;
    }

    // ---------- 模型 → 控件 ----------

    private void RefreshAll()
    {
        RefreshGestures();
        RefreshSliders();
        RefreshExcluded();
        Check(_autostart, _model.Autostart);
    }

    private void RefreshGestures()
    {
        for (int i = 0; i < SettingsModel.Slots.Count; i++)
        {
            var slot = SettingsModel.Slots[i];
            var choices = SettingsModel.Choices(slot, _model.ThreeFingerDrag);
            nint combo = _gestureCombos[i];
            User32.SendMessageW(combo, User32.CB_RESETCONTENT, 0, 0);
            foreach (var a in choices)
            {
                string text = SettingsModel.ActionLabel(a);
                if (a == GestureAction.SwitchDesktops && !_env.DesktopsAvailable) text += "（不可用）";
                AddString(combo, User32.CB_ADDSTRING, text);
            }
            int selected = Math.Max(0, IndexOf(choices, _model.Actions[i]));
            User32.SendMessageW(combo, User32.CB_SETCURSEL, selected, 0);
            User32.EnableWindow(combo, choices.Count > 1);
        }
        Check(_drag, _model.ThreeFingerDrag);
        Check(_fullscreen, _model.PauseInFullscreen);
        User32.SetWindowTextW(_desktopNote, _env.DesktopsAvailable
            ? "三指轻点（中键）以及选择「系统默认」的手势始终由 Windows 处理。"
            : "虚拟桌面接口自检未通过（系统版本不受支持），切换虚拟桌面不可用，四指左右滑仍归 Windows。");
    }

    private void RefreshSliders()
    {
        foreach (var s in _sliders)
        {
            double v = s.Get(_model);
            User32.SendMessageW(s.Track, User32.TBM_SETPOS, 1, (int)Math.Clamp(Math.Round((v - s.Min) / s.Step), 0, s.Steps));
            User32.SetWindowTextW(s.Value, s.Format(v));
        }
        User32.SetWindowTextW(_presetLabel, _model.Preset is { } p ? $"当前：{p.Label}" : "当前：自定义");
    }

    private void RefreshExcluded()
    {
        User32.SendMessageW(_excludedList, User32.LB_RESETCONTENT, 0, 0);
        foreach (string app in _model.ExcludedProcesses) AddString(_excludedList, User32.LB_ADDSTRING, app);

        _runningApps = SafeRunningApps();
        User32.SendMessageW(_addCombo, User32.CB_RESETCONTENT, 0, 0);
        foreach (string app in _runningApps.Where(a => !_model.ExcludedProcesses.Contains(a, StringComparer.OrdinalIgnoreCase)))
            AddString(_addCombo, User32.CB_ADDSTRING, app);
    }

    private IReadOnlyList<string> SafeRunningApps()
    {
        try
        {
            return _env.RunningApps();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    // ---------- 控件 → 模型 ----------

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case User32.WM_COMMAND:
                OnCommand((int)(wParam & 0xFFFF), (int)((wParam >> 16) & 0xFFFF));
                return 0;
            case User32.WM_HSCROLL:
                OnSlider(lParam);
                return 0;
            case User32.WM_ERASEBKGND:
            {
                User32.RECT r;
                User32.GetClientRect(Handle, &r);
                User32.FillRect(wParam, &r, User32.GetSysColorBrush(User32.COLOR_WINDOW));
                return 1;
            }
            case User32.WM_CTLCOLORSTATIC:
            case User32.WM_CTLCOLORBTN:
                Gdi32.SetBkColor(wParam, User32.GetSysColor(User32.COLOR_WINDOW));
                if (_notes.Contains(lParam)) Gdi32.SetTextColor(wParam, User32.GetSysColor(User32.COLOR_GRAYTEXT));
                return User32.GetSysColorBrush(User32.COLOR_WINDOW);
            case User32.WM_DPICHANGED:
                OnDpiChanged((uint)(wParam & 0xFFFF), (User32.RECT*)lParam);
                return 0;
            case User32.WM_CLOSE:
                User32.DestroyWindow(Handle);
                return 0;
        }
        return null;
    }

    private void OnCommand(int id, int code)
    {
        if (id >= IdGestureBase && id < IdGestureBase + SettingsModel.Slots.Count)
        {
            if (code != User32.CBN_SELCHANGE) return;
            var slot = SettingsModel.Slots[id - IdGestureBase];
            var choices = SettingsModel.Choices(slot, _model.ThreeFingerDrag);
            int sel = (int)User32.SendMessageW(_gestureCombos[id - IdGestureBase], User32.CB_GETCURSEL, 0, 0);
            if (sel >= 0 && sel < choices.Count) _model = _model.WithAction(slot, choices[sel]);
            return;
        }
        if (id >= IdPresetBase && id < IdPresetBase + SensitivityPreset.All.Count)
        {
            _model = _model.WithPreset(SensitivityPreset.All[id - IdPresetBase]);
            RefreshSliders();
            return;
        }
        switch (id)
        {
            case IdDrag:
                _model = _model.WithThreeFingerDrag(IsChecked(_drag));
                RefreshGestures();
                break;
            case IdFullscreen:
                _model = _model with { PauseInFullscreen = IsChecked(_fullscreen) };
                break;
            case IdAutostart:
                _model = _model with { Autostart = IsChecked(_autostart) };
                break;
            case IdRemove:
                int sel = (int)User32.SendMessageW(_excludedList, User32.LB_GETCURSEL, 0, 0);
                if (sel >= 0 && sel < _model.ExcludedProcesses.Count)
                {
                    _model = _model with { ExcludedProcesses = _model.ExcludedProcesses.Where((_, i) => i != sel).ToList() };
                    RefreshExcluded();
                }
                break;
            case IdAdd:
                AddExcluded();
                break;
            case IdDefaults:
                _model = _model.Defaults();
                RefreshAll();
                break;
            case IdOpenConfig:
                _env.OpenConfigFile();
                break;
            case IdCancel:
                User32.DestroyWindow(Handle);
                break;
            case IdOk:
                Save();
                break;
        }
    }

    private void AddExcluded()
    {
        string name = WindowText(_addCombo).Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0) return;
        if (!_model.ExcludedProcesses.Contains(name, StringComparer.OrdinalIgnoreCase))
            _model = _model with { ExcludedProcesses = [.. _model.ExcludedProcesses, name] };
        User32.SetWindowTextW(_addCombo, "");
        RefreshExcluded();
    }

    private void OnSlider(nint track)
    {
        var s = _sliders.FirstOrDefault(s => s.Track == track);
        if (s is null) return;
        int pos = (int)User32.SendMessageW(track, User32.TBM_GETPOS, 0, 0);
        double v = Math.Round(s.Min + pos * s.Step, 4);
        _model = s.Set(_model, v);
        User32.SetWindowTextW(s.Value, s.Format(v));
        User32.SetWindowTextW(_presetLabel, _model.Preset is { } p ? $"当前：{p.Label}" : "当前：自定义");
    }

    private void Save()
    {
        var problems = _model.Validate();
        if (problems.Count == 0) problems = _env.Save(_model);
        if (problems.Count > 0)
        {
            User32.MessageBoxW(Handle, string.Join("\n", problems), "无法保存设置", User32.MB_OK | User32.MB_ICONWARNING);
            return;
        }
        User32.DestroyWindow(Handle);
    }

    protected override void OnDestroyed()
    {
        if (ReferenceEquals(s_current, this)) s_current = null;
        if (_ui.DialogWindow == _hwnd) _ui.DialogWindow = 0;
        if (_font != 0) Gdi32.DeleteObject(_font);
        _font = 0;
    }

    // ---------- DPI 与布局 ----------

    private void OnDpiChanged(uint dpi, User32.RECT* suggested)
    {
        _dpi = dpi;
        nint old = _font;
        _font = CreateFont(dpi);
        foreach (var (hwnd, _, _, _, _) in _layout) User32.SendMessageW(hwnd, User32.WM_SETFONT, _font, 0);
        if (old != 0) Gdi32.DeleteObject(old);
        User32.SetWindowPos(Handle, 0, suggested->Left, suggested->Top, suggested->Width, suggested->Height, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
        Layout();
    }

    private void Layout()
    {
        foreach (var (hwnd, x, y, w, h) in _layout)
            User32.SetWindowPos(hwnd, 0, Scale(x), Scale(y), Scale(w), Scale(h), User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
        User32.InvalidateRect(Handle, null, 1);
    }

    /// <summary>鼠标所在显示器的工作区（没有显示器信息时给一个保底区域）。</summary>
    private static Core.Geometry.RectD TargetArea()
    {
        User32.POINT p;
        User32.GetCursorPos(&p);
        int x = p.X, y = p.Y;
        var monitors = Shell.Monitors.Enumerate();
        return (monitors.FirstOrDefault(m => m.Bounds.Contains(x, y)) ?? monitors.FirstOrDefault())?.WorkArea
               ?? new Core.Geometry.RectD(0, 0, 1280, 800);
    }

    /// <summary>窗口外框按 DPI 算出尺寸，放在工作区中央。</summary>
    private void PlaceCentered(Core.Geometry.RectD area)
    {
        var r = new User32.RECT { Right = Scale(ClientWidth), Bottom = Scale(ClientHeight) };
        User32.AdjustWindowRectExForDpi(&r, Style, false, 0, _dpi);
        int w = r.Width, h = r.Height;
        int x = (int)(area.Left + (area.Width - w) / 2), y = (int)(area.Top + Math.Max(0, (area.Height - h) / 2));
        User32.SetWindowPos(Handle, 0, x, y, w, h, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
    }

    private int Scale(int v) => (int)Math.Round(v * _dpi / 96.0);

    private static nint CreateFont(uint dpi)
    {
        var ncm = new User32.NONCLIENTMETRICSW { cbSize = (uint)sizeof(User32.NONCLIENTMETRICSW) };
        if (User32.SystemParametersInfoForDpi(User32.SPI_GETNONCLIENTMETRICS, ncm.cbSize, &ncm, 0, dpi))
            return Gdi32.CreateFontIndirectW(&ncm.lfMessageFont);
        return Gdi32.CreateFontW(-(int)Math.Round(12 * dpi / 96.0), 0, 0, 0, Gdi32.FW_NORMAL, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
    }

    // ---------- 小工具 ----------

    private const uint WM_SETICON = 0x0080;
    private const nint ICON_SMALL = 0, ICON_BIG = 1;

    private static void AddString(nint hwnd, uint msg, string text)
    {
        fixed (char* p = text) User32.SendMessageW(hwnd, msg, 0, p);
    }

    private static void Check(nint hwnd, bool on) => User32.SendMessageW(hwnd, User32.BM_SETCHECK, on ? 1 : 0, 0);

    private static bool IsChecked(nint hwnd) => User32.SendMessageW(hwnd, User32.BM_GETCHECK, 0, 0) == 1;

    private static string WindowText(nint hwnd)
    {
        int len = User32.GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        var buffer = new char[len + 1];
        fixed (char* p = buffer) return new string(p, 0, User32.GetWindowTextW(hwnd, p, buffer.Length));
    }

    private static int IndexOf(IReadOnlyList<GestureAction> list, GestureAction a)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == a) return i;
        return -1;
    }
}
