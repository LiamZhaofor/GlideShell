using System.Diagnostics;
using GlideShell.Core.Configuration;
using GlideShell.Core.Desktops;
using GlideShell.Core.Gestures;
using GlideShell.Core.Input;
using GlideShell.Core.Overview;
using GlideShell.Core.Pointer;
using GlideShell.Core.Switcher;
using GlideShell.Platform;
using GlideShell.Platform.Desktops;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Input;
using GlideShell.Platform.NativeGestures;
using GlideShell.Platform.Overview;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Switcher;
using GlideShell.Platform.Ui;
using GlideShell.Platform.VirtualDesktops;

namespace GlideShell;

/// <summary>
/// 常驻宿主：把各部分接起来并管理生命周期。
/// <code>
/// 输入线程   PtpInputThread → GestureRecognizer → GestureRouter → Overview / Switcher 控制器（只改进度状态）
/// 渲染线程   每个呈现层一个 RenderLoop（合成器时钟）→ 控制器.Sample → DWM 缩略图
/// UI 线程    托盘、热键、前台监视、覆盖层窗口、窗口枚举（主线程的消息循环）
/// 看门狗进程 GlideShell.exe --watchdog：主进程异常退出后还原系统手势
/// </code>
/// </summary>
internal sealed class AppHost
{
    private const uint VkO = 0x4F;

    private readonly string _exePath = Environment.ProcessPath ?? "GlideShell.exe";
    private readonly NativeGestureManager _native = new();
    private volatile GlideShellConfig _config = new();
    private volatile GestureMap _activeMap = new([]);
    private volatile GestureRecognizer? _recognizer;
    private volatile bool _paused;
    private UiThread _ui = null!;
    private TrayIcon? _tray;
    private HotKeys? _hotkeys;
    private ForegroundMonitor? _foreground;
    private PtpInputThread? _input;
    private GestureRouter _router = null!;
    private OverviewPresenter _presenter = null!;
    private OverviewController _overview = null!;
    private SwitcherPresenter _switcherPresenter = null!;
    private SwitcherController _switcher = null!;
    private VirtualDesktopService _virtualDesktops = null!;
    private DesktopSwitchPresenter _desktopPresenter = null!;
    private DesktopSwitchController _desktopSwitch = null!;
    private DragController _drag = null!;
    private FileSystemWatcher? _configWatcher;
    private long _lastConfigReloadUs;
    private readonly string? _simulate;

    /// <param name="simulate">开发用：不启动真实输入，按名称回放一段合成手势（见 <see cref="SyntheticTouch.Scripts"/>）。</param>
    public AppHost(string? simulate = null) => _simulate = simulate;

    public int Run()
    {
        Log.Start();
        Log.Info($"GlideShell {typeof(AppHost).Assembly.GetName().Version} 启动：{Environment.OSVersion}，{_exePath}");
        LoadConfig();

        _ui = new UiThread();
        _foreground = new ForegroundMonitor();
        _presenter = new OverviewPresenter(_ui, () => _config.Overview);
        _overview = new OverviewController(_presenter, () => _config.Overview);
        _presenter.Attach(_overview);
        _switcherPresenter = new SwitcherPresenter(_ui);
        _switcher = new SwitcherController(_switcherPresenter, () => _config.Switcher);
        _switcherPresenter.Attach(_switcher);
        _virtualDesktops = VirtualDesktopService.Start(Log.Info);
        _desktopPresenter = new DesktopSwitchPresenter(_ui, _virtualDesktops);
        _desktopSwitch = new DesktopSwitchController(_desktopPresenter, () => _config.Desktops);
        _desktopPresenter.Attach(_desktopSwitch);
        _drag = new DragController(new MouseInjector(), () => _config.Drag);
        _activeMap = ActiveMap(_config);
        _router = new GestureRouter(() => _activeMap, Target, Suppressed);
        _recognizer = new GestureRecognizer(_config.Recognition, _router.OnGesture);

        string? inputProblem = _simulate is null ? StartInput() : null;
        if (_simulate is null)
        {
            // 模拟模式只用于自动化验证，不碰用户的系统手势设置
            TakeOverNativeGestures();
            StartWatchdog();
        }

        _tray = new TrayIcon(_ui, "GlideShell", Menu, onClick: () => _overview.Toggle(HostClock.NowUs()));
        _hotkeys = new HotKeys(_ui);
        if (!_hotkeys.Register(Native.MOD_CONTROL | Native.MOD_ALT, VkO, () => _overview.Toggle(HostClock.NowUs())))
            Log.Warn("热键 Ctrl+Alt+O 被其他程序占用");
        WatchConfig();
        _presenter.WarmUp();
        if (_simulate is not null) StartSimulation(_simulate);
        if (inputProblem is not null) _tray.ShowBalloon("GlideShell 收不到触摸板输入", inputProblem, warning: true);

        _ui.Run();
        Shutdown();
        return 0;
    }

    // ---------- 手势 ----------

    private IGestureTarget? Target(GestureAction action) => action switch
    {
        GestureAction.Overview => _overview,
        GestureAction.SwitchWindows => _switcher,
        // 虚拟桌面接口自检不通过、或用户关掉了它：四指横滑交还系统（原生切桌面本身已跟手）
        GestureAction.SwitchDesktops when _virtualDesktops.Available && _config.Desktops.Enabled => _desktopSwitch,
        GestureAction.Drag => _drag,
        _ => null,
    };

    /// <summary>
    /// 实际生效的映射 = 配置映射里「已经实现」的动作。系统手势只按它接管：
    /// 还没实现的动作（如切换窗口）绝不能先把对应的系统手势关掉。
    /// </summary>
    private GestureMap ActiveMap(GlideShellConfig cfg)
        => new(cfg.EffectiveMap.Bindings.Where(b => b.Action != GestureAction.None && Target(b.Action) is not null).ToList());

    /// <summary>只在手指落下与开始滑动时各调用一次（输入线程）。总览已打开时不拦截：必须能用手势收起。</summary>
    private bool Suppressed()
    {
        if (_overview.IsVisible || _switcher.IsVisible || _desktopSwitch.IsVisible || _drag.IsDragging) return false;
        if (_paused) return true;
        var cfg = _config;
        var fg = _foreground?.Current;
        if (fg is not null && cfg.IsExcluded(fg.ProcessName)) return true;
        return cfg.PauseInFullscreen && _foreground?.IsFullscreenNow() == true;
    }

    private string? StartInput()
    {
        _input = new PtpInputThread(
            frame => _recognizer?.Process(frame),
            hostUs => _recognizer?.Tick(hostUs),
            hostUs => _recognizer?.Cancel(hostUs),
            Log.Info);
        try
        {
            _input.Start();
            if (_input.Devices.Count == 0) return "没有找到精确式触摸板（PTP）。接入后会自动识别。";
            foreach (var d in _input.Devices)
                Log.Info($"触摸板 VID={d.VendorId:X4} PID={d.ProductId:X4}：{d.Geometry.X.SizeMm:F1}×{d.Geometry.Y.SizeMm:F1}mm，{d.Geometry.ContactSlots} 槽");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            Log.Error("输入线程启动失败", ex);
            return ex.Message;
        }
    }

    /// <summary>
    /// 在后台线程按真实时间回放合成手势（此模式不启动真实输入线程，识别器只有这一个调用方）。
    /// 回放结束 1.5 秒后退出，供自动化截屏验证。
    /// </summary>
    private void StartSimulation(string name)
    {
        if (!SyntheticTouch.Scripts.TryGetValue(name, out var script))
        {
            Log.Warn($"未知的模拟脚本 {name}，可用：{string.Join("、", SyntheticTouch.Scripts.Keys)}");
            return;
        }
        var thread = new Thread(() =>
        {
            Thread.Sleep(1500); // 等启动预热完成
            long start = HostClock.NowUs();
            foreach (var (atMs, contacts) in SyntheticTouch.Frames(script.Fingers, script.Steps))
            {
                long due = start + (long)(atMs * 1000);
                while (HostClock.NowUs() < due) Thread.Sleep(1);
                long now = HostClock.NowUs();
                _recognizer?.Process(ContactFrame.Create(now, now, contacts));
            }
            Log.Info($"模拟手势 {name} 回放完毕");
            Thread.Sleep(1500);
            _ui.Quit();
        }) { IsBackground = true, Name = "GlideShell.Simulate" };
        thread.Start();
    }

    // ---------- 系统手势 ----------

    private void TakeOverNativeGestures()
    {
        var result = _native.TakeOver(_activeMap);
        foreach (var m in result.Messages) Log.Info($"系统手势：{m}");
        if (!result.Verified) _tray?.ShowBalloon("系统手势未能立即切换", "已写入设置，重启资源管理器后生效。详见日志。", warning: true);
    }

    private void RestoreNativeGestures()
    {
        var result = _native.Restore();
        foreach (var m in result.Messages) Log.Info($"系统手势：{m}");
    }

    private void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        // 暂停 = 一键退回 Windows 原生手势；恢复 = 重新接管
        if (paused) RestoreNativeGestures();
        else TakeOverNativeGestures();
        if (_tray is not null) _tray.Tooltip = paused ? "GlideShell（已暂停，使用系统手势）" : "GlideShell";
        Log.Info(paused ? "已暂停：系统手势已还原" : "已恢复接管");
    }

    private void StartWatchdog()
    {
        try
        {
            var psi = new ProcessStartInfo(_exePath, $"--watchdog {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"看门狗启动失败：{ex.Message}（崩溃后需要手动运行 GlideShell.exe --restore-native）");
        }
    }

    // ---------- 配置 ----------

    private void LoadConfig()
    {
        string path = ConfigStore.DefaultPath;
        var (config, problems) = ConfigStore.Load(path);
        foreach (var p in problems) Log.Warn($"配置：{p}");
        if (!File.Exists(path))
        {
            try
            {
                ConfigStore.Save(config, path);
                Log.Info($"已生成默认配置 {path}");
            }
            catch (IOException ex)
            {
                Log.Warn($"无法写入默认配置：{ex.Message}");
            }
        }
        _config = config;
    }

    private void WatchConfig()
    {
        string path = ConfigStore.DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _configWatcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => _ui.Post(ReloadConfig);
        _configWatcher.Changed += onChange;
        _configWatcher.Created += onChange;
        _configWatcher.Renamed += (_, _) => _ui.Post(ReloadConfig);
        _configWatcher.EnableRaisingEvents = true;
    }

    private void ReloadConfig()
    {
        long now = HostClock.NowUs();
        if (now - _lastConfigReloadUs < 300_000) return; // 编辑器保存时会连发几次事件
        _lastConfigReloadUs = now;
        Thread.Sleep(50); // 等编辑器写完

        var old = _config;
        LoadConfig();
        var cfg = _config;
        if (cfg.Recognition != old.Recognition) _recognizer = new GestureRecognizer(cfg.Recognition, _router.OnGesture);
        var map = ActiveMap(cfg);
        bool mapChanged = !map.Bindings.SequenceEqual(_activeMap.Bindings);
        _activeMap = map;
        if (!_paused && mapChanged) TakeOverNativeGestures();
        Log.Info("配置已重载");
    }

    // ---------- 托盘 ----------

    private IReadOnlyList<TrayMenuItem> Menu()
    {
        var cfg = _config;
        string? lastApp = _foreground?.Current.ProcessName is { Length: > 0 } name && !name.Equals("explorer", StringComparison.OrdinalIgnoreCase) ? name : null;
        var items = new List<TrayMenuItem>
        {
            new("打开总览\tCtrl+Alt+O", () => _overview.Toggle(HostClock.NowUs())),
            new("暂停（使用 Windows 原生手势）", () => SetPaused(!_paused), Checked: _paused),
            TrayMenuItem.Separator,
            new("三指拖移（总览、切桌面改为四指）", () => UpdateConfig(c => c with { ThreeFingerDrag = !c.ThreeFingerDrag }), Checked: cfg.ThreeFingerDrag),
            new("四指横滑切换虚拟桌面", () => UpdateConfig(c => c with { Desktops = c.Desktops with { Enabled = !c.Desktops.Enabled } }),
                Checked: cfg.Desktops.Enabled, Enabled: _virtualDesktops.Available),
        };
        foreach (var (label, preset) in SensitivityPresets)
            items.Add(new($"灵敏度：{label}", () => UpdateConfig(preset), Checked: preset(cfg) == cfg));
        items.Add(TrayMenuItem.Separator);
        if (lastApp is not null && !cfg.IsExcluded(lastApp))
            items.Add(new($"在 {lastApp} 中不使用手势", () => UpdateConfig(c => c with { ExcludedProcesses = [.. c.ExcludedProcesses, lastApp] })));
        if (cfg.ExcludedProcesses.Count > 0)
            items.Add(new($"恢复在全部程序中使用手势（已排除 {cfg.ExcludedProcesses.Count} 个）", () => UpdateConfig(c => c with { ExcludedProcesses = [] })));
        items.AddRange(
        [
            new("开机启动", () => Autostart.Set(_exePath, !Autostart.IsEnabled(_exePath)), Checked: Autostart.IsEnabled(_exePath)),
            new("打开配置文件", () => OpenPath(ConfigStore.DefaultPath)),
            new("打开日志目录", () => OpenPath(Log.DefaultDirectory)),
            TrayMenuItem.Separator,
            new("退出", () => _ui.Quit()),
        ]);
        return items;
    }

    /// <summary>灵敏度预设：同时调整滑动阈值和各动作的行程（越灵敏，手指走得越短）。</summary>
    private static readonly (string Label, Func<GlideShellConfig, GlideShellConfig> Apply)[] SensitivityPresets =
    [
        ("高", c => WithSensitivity(c, slop: 2.5, overview: 24, perWindow: 18, desktop: 45)),
        ("标准", c => WithSensitivity(c, slop: 3.0, overview: 30, perWindow: 22, desktop: 55)),
        ("低", c => WithSensitivity(c, slop: 4.0, overview: 38, perWindow: 28, desktop: 68)),
    ];

    private static GlideShellConfig WithSensitivity(GlideShellConfig c, double slop, double overview, double perWindow, double desktop) => c with
    {
        Recognition = c.Recognition with { SlopMm = slop, TapMaxMoveMm = Math.Min(c.Recognition.TapMaxMoveMm, slop) },
        Overview = c.Overview with { TravelMm = overview },
        Switcher = c.Switcher with { MmPerWindow = perWindow },
        Desktops = c.Desktops with { TravelMm = desktop },
    };

    /// <summary>托盘里改设置：写回配置文件（文件监视器随后重载，这里也立即生效）。</summary>
    private void UpdateConfig(Func<GlideShellConfig, GlideShellConfig> change)
    {
        var (updated, problems) = change(_config).Sanitize();
        foreach (var p in problems) Log.Warn($"配置：{p}");
        try
        {
            ConfigStore.Save(updated, ConfigStore.DefaultPath);
        }
        catch (IOException ex)
        {
            Log.Warn($"保存配置失败：{ex.Message}");
        }
        _lastConfigReloadUs = 0;
        ReloadConfig();
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warn($"打开 {path} 失败：{ex.Message}");
        }
    }

    private void Shutdown()
    {
        Log.Info("正在退出");
        _configWatcher?.Dispose();
        _input?.Dispose();
        _presenter.Dispose();
        _switcherPresenter.Dispose();
        _desktopPresenter.Dispose();
        _virtualDesktops.Dispose();
        RestoreNativeGestures();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _foreground?.Dispose();
        _ui.Dispose();
        Log.Flush(TimeSpan.FromSeconds(2));
    }

    // 托盘菜单与热键需要的少量常量（避免 GlideShell 直接依赖 Platform 的内部互操作类）
    private static class Native
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
    }
}
