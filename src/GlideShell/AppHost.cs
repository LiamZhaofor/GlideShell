using System.Diagnostics;
using GlideShell.Core.Configuration;
using GlideShell.Core.Gestures;
using GlideShell.Core.Overview;
using GlideShell.Platform;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Input;
using GlideShell.Platform.NativeGestures;
using GlideShell.Platform.Overview;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;

namespace GlideShell;

/// <summary>
/// 常驻宿主：把各部分接起来并管理生命周期。
/// <code>
/// 输入线程   PtpInputThread → GestureRecognizer → GestureRouter → OverviewController（只改进度状态）
/// 渲染线程   RenderLoop（合成器时钟）→ OverviewController.Sample → DWM 缩略图
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
    private FileSystemWatcher? _configWatcher;
    private long _lastConfigReloadUs;

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
        _activeMap = ActiveMap(_config);
        _router = new GestureRouter(() => _activeMap, Target, Suppressed);
        _recognizer = new GestureRecognizer(_config.Recognition, _router.OnGesture);

        string? inputProblem = StartInput();
        TakeOverNativeGestures();
        StartWatchdog();

        _tray = new TrayIcon(_ui, "GlideShell", Menu, onClick: () => _overview.Toggle(HostClock.NowUs()));
        _hotkeys = new HotKeys(_ui);
        if (!_hotkeys.Register(Native.MOD_CONTROL | Native.MOD_ALT, VkO, () => _overview.Toggle(HostClock.NowUs())))
            Log.Warn("热键 Ctrl+Alt+O 被其他程序占用");
        WatchConfig();
        _presenter.WarmUp();
        if (inputProblem is not null) _tray.ShowBalloon("GlideShell 收不到触摸板输入", inputProblem, warning: true);

        _ui.Run();
        Shutdown();
        return 0;
    }

    // ---------- 手势 ----------

    private IGestureTarget? Target(GestureAction action) => action switch
    {
        GestureAction.Overview => _overview,
        _ => null, // 切换窗口（P4）、切换桌面（P5）尚未实现：留给系统
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
        if (_overview.IsVisible) return false;
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

    private IReadOnlyList<TrayMenuItem> Menu() =>
    [
        new("打开总览\tCtrl+Alt+O", () => _overview.Toggle(HostClock.NowUs())),
        new("暂停（使用 Windows 原生手势）", () => SetPaused(!_paused), Checked: _paused),
        TrayMenuItem.Separator,
        new("开机启动", () => Autostart.Set(_exePath, !Autostart.IsEnabled(_exePath)), Checked: Autostart.IsEnabled(_exePath)),
        new("打开配置文件", () => OpenPath(ConfigStore.DefaultPath)),
        new("打开日志目录", () => OpenPath(Log.DefaultDirectory)),
        TrayMenuItem.Separator,
        new("退出", () => _ui.Quit()),
    ];

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
