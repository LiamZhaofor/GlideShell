using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Gestures;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;
using GlideShell.Platform.Updates;
using GlideShell.Platform.Ui.Settings;
using Icons = GlideShell.Platform.Ui.Settings.FluentTheme.Icons;

namespace GlideShell.Platform.Ui;

/// <summary>设置窗口的各页内容：每页一个函数，用 <see cref="PageBuilder"/> 从上往下排。文字面向第一次用的人：说清楚做什么、在哪里用。</summary>
public sealed partial class SettingsWindow
{
    private const int PageCount = 7;

    private static string PageTitle(int page) => page switch
    {
        0 => "首页",
        1 => "触摸板手势",
        2 => "总览与桌面",
        3 => "鼠标与触发角",
        4 => "排除的程序",
        5 => "诊断",
        _ => "关于",
    };

    private static string PageIcon(int page) => page switch
    {
        0 => Icons.Home,
        1 => Icons.Touch,
        2 => Icons.TaskView,
        3 => Icons.Mouse,
        4 => Icons.Block,
        5 => Icons.Health,
        _ => Icons.Info,
    };

    private void BuildPage(PageBuilder b)
    {
        b.Title(PageTitle(_page));
        if (_problems.Count > 0) b.InfoBar("这些设置有冲突，暂时没有生效：" + string.Join("；", _problems));
        switch (_page)
        {
            case 0: HomePage(b); break;
            case 1: GesturesPage(b); break;
            case 2: OverviewPage(b); break;
            case 3: MousePage(b); break;
            case 4: ExcludedPage(b); break;
            case 5: DiagnosticsPage(b); break;
            default: AboutPage(b); break;
        }
    }

    // ---------- 首页 ----------

    private void HomePage(PageBuilder b)
    {
        b.Paragraph("GlideShell 让触摸板手势真正跟手：手指走多少，画面走多少；停住就停住，往回滑就倒退，松手才决定去留。下面是你现在的手势。");
        b.Section("你的手势");
        var tiles = new List<(int Fingers, bool Horizontal, string Title, string Sub)>();
        for (int i = 0; i < SettingsModel.Slots.Count; i++)
        {
            var slot = SettingsModel.Slots[i];
            tiles.Add((slot.Fingers, slot.Axis == GestureAxis.Horizontal, slot.Label, Describe(slot, _model.Actions[i])));
        }
        double s = b.S, gap = 8 * s, w = (b.Width - gap) / 2, h = 84 * s;
        for (int i = 0; i < tiles.Count; i++)
        {
            var (fingers, horizontal, title, sub) = tiles[i];
            double x = b.Left + (i % 2) * (w + gap), y = b.Y + (i / 2) * (h + gap);
            b.Items.Add(new Item(ItemKind.Card, $"tile{i}") { Rect = new RectD(x, y, w, h), Text = title, Sub = sub, Indent = 76 * s });
            b.Items.Add(new Item(ItemKind.Gesture, $"tileg{i}") { Rect = new RectD(x + 16 * s, y + (h - 48 * s) / 2, 64 * s, 48 * s), Fingers = fingers, Horizontal = horizontal });
        }
        b.Y += 2 * (h + gap);
        b.Card("homemouse", Icons.Mouse, "鼠标", _model.MouseButton == MouseDragButton.None
            ? "没有开启按键手势"
            : $"按住{ButtonName(_model.MouseButton)}拖动：左右切换桌面，上拉总览，下拉应用窗口",
            Button("goMouse", "设置", () => Page = 3), b.ButtonWidthDip("设置"));
        b.Gap(6);
        b.Paragraph("小提示：在窗口总览里点某个窗口下面的标题，可以只看这个程序的窗口；三指左右滑可以在窗口之间切换。", "tip");

        b.Section("常用");
        b.Card("autostart", Icons.Power, "开机时启动", "登录 Windows 后自动在后台运行",
            Toggle("autostartT", () => _model.Autostart, v => _model = _model with { Autostart = v }), 96);
        b.Card("fullscreen", Icons.Monitor, "全屏时暂停", "游戏、视频、演示全屏时不接管手势，交给 Windows",
            Toggle("fullscreenT", () => _model.PauseInFullscreen, v => _model = _model with { PauseInFullscreen = v }), 96);
    }

    // ---------- 触摸板手势 ----------

    private void GesturesPage(PageBuilder b)
    {
        b.Paragraph("每个手势可以交给 GlideShell，也可以选「系统默认」留给 Windows 原来的功能。三指轻点（中键）始终由 Windows 处理。");
        if (!_env.DesktopsAvailable) b.InfoBar("这台电脑的系统版本不支持 GlideShell 切换虚拟桌面，四指左右滑仍由 Windows 处理。");

        b.Section("手势");
        for (int i = 0; i < SettingsModel.Slots.Count; i++)
        {
            var slot = SettingsModel.Slots[i];
            var choices = SettingsModel.Choices(slot, _model.ThreeFingerDrag);
            var labels = choices.Select(a => SettingsModel.ActionLabel(a) + (a == GestureAction.SwitchDesktops && !_env.DesktopsAvailable ? "（不可用）" : "")).ToList();
            int index = i;
            var dropdown = new Item(ItemKind.Dropdown, $"slot{i}")
            {
                Options = labels, Enabled = choices.Count > 1,
                Index = () => IndexOf(choices, _model.Actions[index]),
                SetIndex = k => _model = _model.WithAction(slot, choices[k]),
            };
            var card = b.Card($"slotc{i}", "", slot.Label, Describe(slot, _model.Actions[i]), dropdown, 240, indentDip: 76);
            b.Items.Add(new Item(ItemKind.Gesture, $"slotg{i}")
            {
                Rect = new RectD(card.Rect.Left + 16 * b.S, card.Rect.CenterY - 24 * b.S, 64 * b.S, 48 * b.S), Fingers = slot.Fingers,
                Horizontal = slot.Axis == GestureAxis.Horizontal,
            });
        }
        b.Card("drag", Icons.Move, "三指拖移", "三指滑动等于按住左键拖动（像 Mac 的三指拖移）。开启后三指只用来拖动，窗口总览改为四指上下滑。",
            Toggle("dragT", () => _model.ThreeFingerDrag, v => _model = _model.WithThreeFingerDrag(v)), 96);
        b.Card("tap", Icons.Mouse, "三指轻点 = 鼠标中键", "三指轻轻点一下等于按鼠标中键（在新标签页打开链接、关闭标签页）。开着时 Windows 自己的三指轻点暂停，不会点两次",
            Toggle("tapT", () => _model.ThreeFingerTapMiddleClick, v => _model = _model with { ThreeFingerTapMiddleClick = v }), 96);

        b.Section("灵敏度");
        var presets = SensitivityPreset.All;
        b.Card("preset", Icons.Speed, "手指要滑多远",
            _model.Preset is null ? "当前是自定义的数值（在下面「细调」里改过）。选一档可以恢复。" : "越灵敏，滑得越短就能完成动作。不确定就选「标准」。",
            new Item(ItemKind.Segmented, "presetS")
            {
                Options = presets.Select(p => p.Label).ToList(),
                Index = () => _model.Preset is { } p ? presets.ToList().IndexOf(p) : -1,
                SetIndex = k => _model = _model.WithPreset(presets[k]),
            }, 240);
        bool fine = _expanded.Contains("fine");
        b.Card("fine", Icons.Settings, "细调", "分别调整各个手势要滑多远，以及三指拖移的指针速度", expander: true, expanded: fine);
        if (fine)
        {
            SliderCard(b, "slop", "起步距离", "手指移动多远才开始跟（越小越灵敏，太小容易误触）", 1, 8, 0.5, v => $"{v:0.0} mm",
                () => _model.SlopMm, v => _model = _model with { SlopMm = v });
            SliderCard(b, "ovTravel", "总览：滑满", "滑多远窗口总览完全展开", 10, 60, 1, v => $"{v:0} mm",
                () => _model.OverviewTravelMm, v => _model = _model with { OverviewTravelMm = v });
            SliderCard(b, "perWindow", "切换一个窗口", "三指左右滑多远切过一个窗口", 10, 50, 1, v => $"{v:0} mm",
                () => _model.MmPerWindow, v => _model = _model with { MmPerWindow = v });
            SliderCard(b, "dtTravel", "切换桌面：滑满", "四指左右滑多远整整切过一个桌面", 20, 100, 1, v => $"{v:0} mm",
                () => _model.DesktopTravelMm, v => _model = _model with { DesktopTravelMm = v });
            SliderCard(b, "dragSpeed", "拖移：指针速度", "三指拖移时，手指每移动 1 毫米指针走多远", 4, 40, 1, v => $"{v:0} px/mm",
                () => _model.DragPixelsPerMm, v => _model = _model with { DragPixelsPerMm = v }, _model.ThreeFingerDrag);
            SliderCard(b, "dragAccel", "拖移：加速", "手指滑得越快，指针走得越远", 0, 2, 0.1, v => $"{v:0.0}",
                () => _model.DragAcceleration, v => _model = _model with { DragAcceleration = v }, _model.ThreeFingerDrag);
        }
    }

    // ---------- 总览与桌面 ----------

    private void OverviewPage(PageBuilder b)
    {
        b.Paragraph("窗口总览把所有窗口平铺开（像 Mac 的调度中心）：顶部是各个虚拟桌面，点窗口切过去，拖窗口到上面的桌面就移过去。");
        b.Section("窗口总览");
        b.Card("appWindows", Icons.Apps, "只看当前程序的窗口", "没打开总览时三指下滑：只平铺当前程序的窗口（像 Mac 的 App Exposé）",
            Toggle("appWindowsT", () => _model.AppWindows, v => _model = _model with { AppWindows = v }), 96);
        b.Card("groupByApp", Icons.ViewAll, "同一程序的窗口排在一起", "默认按窗口原来的位置就近排",
            Toggle("groupByAppT", () => _model.GroupByApp, v => _model = _model with { GroupByApp = v }), 96);
        SliderCard(b, "dim", "背景变暗", "总览展开后桌面背景暗多少", 0, 1, 0.05, v => $"{v * 100:0}%",
            () => _model.BackdropDim, v => _model = _model with { BackdropDim = v }, icon: Icons.Color);
        SliderCard(b, "corner", "窗口圆角", "窗口在总览里缩小成卡片时的圆角（跟着窗口一起缩小）。0 = 保持窗口原样", 0, 32, 1, v => v == 0 ? "原样" : $"{v:0}",
            () => _model.CornerRadius, v => _model = _model with { CornerRadius = v }, icon: Icons.Desktop);

        b.Section("键盘");
        b.Card("altTab", Icons.Keyboard, "Alt+Tab 用切换条", "按住 Alt、每按一次 Tab 选下一个窗口，松开 Alt 切过去；快速按一下直接回到上一个窗口",
            Toggle("altTabT", () => _model.TakeOverAltTab, v => _model = _model with { TakeOverAltTab = v }), 96);
        b.Card("desktopKeys", Icons.Keyboard, "键盘切换桌面也跟手", "Ctrl+Win+←/→ 用 GlideShell 的切换动画；总览开着时改为在总览里翻页",
            Toggle("desktopKeysT", () => _model.TakeOverDesktopHotkeys, v => _model = _model with { TakeOverDesktopHotkeys = v }, _env.DesktopsAvailable), 96,
            enabled: _env.DesktopsAvailable);

        bool adv = _expanded.Contains("ovAdv");
        b.Card("ovAdv", Icons.Settings, "高级", "出现画面问题时才需要改", expander: true, expanded: adv);
        if (adv)
            b.Card("shared", "", "共享缩略图", "高刷新率屏幕上更流畅、能画出与系统一致的窗口阴影。总览或切换时画面异常可以关掉试试",
                Toggle("sharedT", () => _model.SharedThumbnails, v => _model = _model with { SharedThumbnails = v }), 96);
    }

    // ---------- 鼠标与触发角 ----------

    private void MousePage(PageBuilder b)
    {
        b.Section("按住鼠标键拖动");
        b.Paragraph("像 Mac Mouse Fix 一样：按住一个鼠标键拖动，左右切换桌面（桌面跟着指针走），上拉打开总览，下拉只看当前程序的窗口。只点一下仍是这个键原来的功能。");
        var buttons = SettingsModel.MouseButtonChoices;
        b.Card("mouseButton", Icons.Mouse, "按住的键", "侧键一般在鼠标左侧拇指的位置。开着时移动鼠标会占一点 CPU（回报率越高越多），不用就选「不用」",
            new Item(ItemKind.Dropdown, "mouseButtonD")
            {
                Options = buttons.Select(SettingsModel.MouseButtonLabel).ToList(),
                Index = () => IndexOf(buttons, _model.MouseButton),
                SetIndex = k => _model = _model with { MouseButton = buttons[k] },
            }, 240);
        SliderCard(b, "mouseSens", "灵敏度", "1.0× 时拖过一个屏幕宽切过一个桌面", 0.5, 2, 0.1, v => $"{v:0.0}×",
            () => _model.MouseSensitivity, v => _model = _model with { MouseSensitivity = v }, _model.MouseButton != MouseDragButton.None, Icons.Speed);

        b.Section("触发角");
        b.Paragraph("指针移到屏幕的某个角上停一下就触发。点下面屏幕四个角上的按钮来设置。暂停、全屏或排除的程序在前台时不触发。");
        double s = b.S, w = Math.Min(b.Width, 520 * s), h = 300 * s;
        var screen = new RectD(b.Left + (b.Width - w) / 2, b.Y, w, h);
        b.Items.Add(new Item(ItemKind.Screen, "screen") { Rect = screen });
        var display = ItemPainter.ScreenArea(screen, s);
        var inner = new RectD(display.Left, display.Top, display.Width, display.Height - 14 * s); // 任务栏上面
        var actions = SettingsModel.HotCornerChoices;
        var labels = actions.Select(SettingsModel.HotCornerLabel).ToList();
        double cw = Math.Min(170 * s, inner.Width / 2 - 16 * s), ch = 30 * s, m = 10 * s;
        for (int c = 0; c < 4; c++)
        {
            int corner = c;
            double x = c % 2 == 0 ? inner.Left + m : inner.Right - m - cw, y = c < 2 ? inner.Top + m : inner.Bottom - m - ch;
            b.Items.Add(new Item(ItemKind.Corner, $"corner{c}")
            {
                Rect = new RectD(x, y, cw, ch), Text = SettingsModel.HotCornerNames[c], Options = labels,
                Index = () => IndexOf(actions, _model.HotCorners[corner]),
                SetIndex = k => _model = _model.WithHotCorner(corner, actions[k]),
            });
        }
        b.Y += h + 8 * s;
    }

    // ---------- 排除的程序 ----------

    private void ExcludedPage(PageBuilder b)
    {
        b.Paragraph("这些程序在前台时，GlideShell 不接管手势，交给 Windows 处理。适合远程桌面、虚拟机、自己处理手势的游戏或绘图软件。");
        b.Card("fullscreen2", Icons.Monitor, "全屏时暂停", "游戏、视频、演示全屏时不接管手势",
            Toggle("fullscreen2T", () => _model.PauseInFullscreen, v => _model = _model with { PauseInFullscreen = v }), 96);

        b.Section("排除的程序");
        if (_model.ExcludedProcesses.Count == 0) b.Paragraph("还没有排除任何程序。", "empty");
        foreach (string app in _model.ExcludedProcesses)
        {
            string name = app;
            b.Card($"ex:{app}", Icons.Apps, app, "", Button($"exRemove:{app}", "移除", () =>
            {
                _model = _model with { ExcludedProcesses = _model.ExcludedProcesses.Where(a => !a.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList() };
                Changed();
            }, Icons.Delete), b.ButtonWidthDip("移除", icon: true));
        }

        b.Section("添加");
        b.Card("addRunning", Icons.Add, "从正在运行的程序里选", "列出现在开着窗口的程序", Button("addRunningB", "选择…", PickRunning), b.ButtonWidthDip("选择…"));
        // 手动输入：系统的编辑框 + 「添加」按钮
        double s = b.S, editW = 220, buttonW = b.ButtonWidthDip("添加");
        var box = new Item(ItemKind.EditBox, "editBox");
        var card = b.Card("addManual", Icons.Document, "输入进程名", "不带 .exe，例如远程桌面是 mstsc", box, editW + 8 + buttonW);
        var area = box.Rect;
        box.Rect = new RectD(area.Left, area.Top, editW * s, area.Height);
        _editRect = box.Rect;
        b.Items.Add(new Item(ItemKind.AccentButton, "addManualB")
        {
            Rect = new RectD(area.Right - buttonW * s, area.Top, buttonW * s, area.Height), Text = "添加", Click = AddTyped,
        });
        _ = card;
    }

    private void PickRunning()
    {
        IReadOnlyList<string> running;
        try
        {
            running = _env.RunningApps();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            running = [];
        }
        var candidates = running.Where(a => !_model.ExcludedProcesses.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) candidates = ["（没有可添加的程序）"];
        var anchor = Find("addRunningB") is { } btn ? OnScreen(btn) : new RectD(0, 0, 0, 0);
        int k = ShowMenu(candidates, -1, anchor.Left, anchor.Bottom + 4 * S);
        if (k < 0 || running.Count == 0 || !running.Contains(candidates[k])) return;
        AddExcluded(candidates[k]);
    }

    private void AddTyped()
    {
        string name = EditText().Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0) return;
        ClearEdit();
        AddExcluded(name);
    }

    private void AddExcluded(string name)
    {
        if (_model.ExcludedProcesses.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        _model = _model with { ExcludedProcesses = [.. _model.ExcludedProcesses, name] };
        Changed();
    }

    // ---------- 关于 ----------

    private void AboutPage(PageBuilder b)
    {
        b.Card("app", Icons.Touch, "GlideShell", (_env.Version.Length > 0 ? $"版本 {_env.Version} · " : "") + "让 Windows 触摸板手势真正跟手");
        b.Card("autostart2", Icons.Power, "开机时启动", "登录 Windows 后自动在后台运行",
            Toggle("autostart2T", () => _model.Autostart, v => _model = _model with { Autostart = v }), 96);

        b.Section("更新");
        b.Card("checkUpdates", Icons.Sync, "自动检查更新", "每天向 GitHub 查一次有没有新版本，有的话在托盘通知。只查版本号，不发送其他数据；关掉后完全不联网",
            Toggle("checkUpdatesT", () => _model.CheckUpdates, v => _model = _model with { CheckUpdates = v }), 96);
        if (_env.UpdateStatus is { } updateStatus && _env.CheckUpdates is { } check)
        {
            var st = updateStatus();
            bool available = st.State == UpdateState.Available && st.Latest is not null;
            string label = available ? "下载" : st.State == UpdateState.Checking ? "正在检查" : "检查";
            var button = new Item(available ? ItemKind.AccentButton : ItemKind.Button, "updateB")
            {
                Text = label, Click = available ? _env.OpenUpdatePage ?? check : check, Enabled = st.State != UpdateState.Checking,
            };
            b.Card("updateNow", available ? Icons.Download : Icons.Info, available ? $"有新版本 {st.Latest!.Version.ToString(3)}" : "检查更新",
                UpdateText(st), button, b.ButtonWidthDip(label));
        }

        b.Section("更多");
        b.Card("config", Icons.Document, "配置文件", "弹簧参数等不常用的选项在配置文件里，保存后立即生效",
            Button("configB", "打开", _env.OpenConfigFile), b.ButtonWidthDip("打开"));
        if (_env.OpenLogs is { } logs)
            b.Card("logs", Icons.Folder, "日志", "遇到问题时可以把日志发给开发者", Button("logsB", "打开", logs), b.ButtonWidthDip("打开"));
        b.Card("defaults", Icons.Reset, "恢复默认设置", "所有设置回到刚安装时的样子（排除的程序和开机启动保留）",
            Button("defaultsB", "恢复默认", ResetDefaults), b.ButtonWidthDip("恢复默认"));
    }

    // ---------- 诊断 ----------

    private void DiagnosticsPage(PageBuilder b)
    {
        b.Paragraph("GlideShell 依赖几项系统能力（触摸板原始输入、虚拟桌面接口、共享缩略图等），这里逐项列出它们在你的电脑上是否正常。" +
                    "遇到问题时生成一份诊断报告贴到 GitHub Issue，就能看到你的机器上发生了什么。");
        if (_env.CreateDiagnosticReport is { } report)
            b.Card("report", Icons.Document, "生成诊断报告",
                "版本、系统、触摸板、显示器、下面的自检结果、配置和最近的日志。只保存在本机并复制到剪贴板，不会上传；贴出去之前可以先看一眼",
                Button("reportB", "生成", report, accent: true), b.ButtonWidthDip("生成"));
        if (_env.OpenLogs is { } logs)
            b.Card("logs2", Icons.Folder, "日志", "每次启动、手势、渲染统计都记在这里", Button("logs2B", "打开", logs), b.ButtonWidthDip("打开"));

        b.Section("兼容自检");
        var checks = _env.Compatibility?.Invoke() ?? [];
        for (int i = 0; i < checks.Count; i++)
        {
            var c = checks[i];
            string icon = c.Level switch
            {
                CheckLevel.Ok => Icons.Ok,
                CheckLevel.Warning => Icons.Warning,
                CheckLevel.Unavailable => Icons.Unavailable,
                _ => Icons.Info,
            };
            b.Card($"chk{i}", icon, c.Name, $"{c.LevelText} · {c.Detail}");
        }
    }

    private string UpdateText(UpdateStatus st)
    {
        string current = _env.Version.Length > 0 ? $"当前版本 {_env.Version}" : "当前版本";
        return st.State switch
        {
            UpdateState.Checking => "正在向 GitHub 查询…",
            UpdateState.UpToDate => $"{current}，已经是最新的（{st.CheckedAt:HH:mm} 检查）",
            UpdateState.Available => $"{current}。点「下载」打开下载页面，下载后双击「安装.cmd」即可升级，设置会保留",
            UpdateState.Failed => $"检查失败：{st.Error}",
            _ => $"{current}，还没有检查过",
        };
    }

    private void ResetDefaults()
    {
        int r = User32.MessageBoxW(Handle, "所有设置都会回到刚安装时的样子（排除的程序和开机启动保留）。继续吗？", "恢复默认设置",
            0x00000004 /* MB_YESNO */ | 0x00000030 /* MB_ICONWARNING */);
        if (r != 6 /* IDYES */) return;
        _model = _model.Defaults();
        _anim.Clear();
        Changed();
    }

    // ---------- 小工具 ----------

    private void SliderCard(PageBuilder b, string key, string title, string sub, double min, double max, double step, Func<double, string> format,
        Func<double> get, Action<double> set, bool enabled = true, string icon = "")
        => b.Card(key, icon, title, sub, new Item(ItemKind.Slider, key + "S")
        {
            Min = min, Max = max, Step = step, Format = format, Value = get, SetValue = set, Enabled = enabled,
        }, 300, enabled: enabled);

    private static Item Toggle(string key, Func<bool> get, Action<bool> set, bool enabled = true)
        => new(ItemKind.Toggle, key) { On = get, SetOn = set, Enabled = enabled };

    private static Item Button(string key, string text, Action click, string icon = "", bool accent = false)
        => new(accent ? ItemKind.AccentButton : ItemKind.Button, key) { Text = text, Click = click, Icon = icon };

    private static string ButtonName(MouseDragButton b) => b switch
    {
        MouseDragButton.Back => "侧键「后退」",
        MouseDragButton.Forward => "侧键「前进」",
        MouseDragButton.Middle => "中键",
        _ => "",
    };

    /// <summary>某个手势位置绑定的动作做什么（一句话，给第一次用的人看）。</summary>
    private string Describe(GestureSlot slot, GestureAction action) => action switch
    {
        GestureAction.Overview when slot.Axis == GestureAxis.Vertical => _model.AppWindows
            ? "上滑：窗口总览；下滑：只看当前程序的窗口"
            : "上滑：窗口总览",
        GestureAction.Overview => "左右滑：窗口总览",
        GestureAction.SwitchWindows => "左右滑：在窗口之间切换，松手切过去",
        GestureAction.SwitchDesktops => _env.DesktopsAvailable ? "左右滑：切换虚拟桌面，桌面跟着手指走" : "切换虚拟桌面（这台电脑不支持，仍由 Windows 处理）",
        GestureAction.ShowDesktop => "下滑：显示桌面；上滑：把窗口拉回来（桌面没显示时打开总览）",
        GestureAction.Drag => "滑动：按住左键拖动",
        _ => "交给 Windows 原来的手势",
    };

    private static int IndexOf<T>(IReadOnlyList<T> list, T a) where T : struct, Enum
    {
        for (int i = 0; i < list.Count; i++)
            if (EqualityComparer<T>.Default.Equals(list[i], a)) return i;
        return -1;
    }
}
