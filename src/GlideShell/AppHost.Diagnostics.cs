using GlideShell.Core.Configuration;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Input;
using GlideShell.Platform.Overview;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;
using GlideShell.Platform.Updates;
using static GlideShell.Core.Localization.L;

namespace GlideShell;

/// <summary>兼容自检、一键诊断报告、异常退出提示（2.0 第二轮「稳定与可诊断」，见 ROADMAP 9.3）。</summary>
internal sealed partial class AppHost
{
    private const int ReportLogLines = 300;

    private bool? _nativeVerified; // 最近一次接管系统手势是否已在 Explorer 生效（null = 还没接管过）

    private string Version => typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>当前各项自检结果（UI 线程）：设置「诊断」页逐项显示，诊断报告也列出。</summary>
    private IReadOnlyList<CompatCheck> CompatibilityChecks()
    {
        var checks = new List<CompatCheck>();
        var cfg = _config;

        var devices = _input?.Devices ?? [];
        if (devices.Count == 0)
            checks.Add(new(T("触摸板"), CheckLevel.Unavailable, T("没有找到精确式触摸板（PTP），三指 / 四指手势用不了")));
        foreach (var d in devices)
        {
            var g = d.Geometry;
            string mode = g.ContactSlots >= 5 ? T("并行报告（一份报文带全部手指）") : F("每份报文 {0} 个触点槽，可能是混合报告模式（这种模式只有单元测试，还没在真机上验证）", g.ContactSlots);
            checks.Add(new(T("触摸板"), g.ContactSlots >= 5 ? CheckLevel.Ok : CheckLevel.Warning,
                $"VID {d.VendorId:X4} / PID {d.ProductId:X4}，{g.X.SizeMm:F1} × {g.Y.SizeMm:F1} mm，{mode}"));
        }

        checks.Add(_paused
            ? new(T("系统手势接管"), CheckLevel.Info, T("已暂停：用的是 Windows 原生手势"))
            : _nativeVerified switch
            {
                true => new(T("系统手势接管"), CheckLevel.Ok, T("已生效：被 GlideShell 接管的手势，Windows 自己不再响应")),
                false => new(T("系统手势接管"), CheckLevel.Warning, T("设置已写入，但 Explorer 还没生效（可能两边都会响应）；重启资源管理器或注销后生效")),
                _ => new(T("系统手势接管"), CheckLevel.Info, T("还没接管")),
            });

        checks.Add(new(T("虚拟桌面接口"), _virtualDesktops.Available ? CheckLevel.Ok : CheckLevel.Unavailable,
            _virtualDesktops.Available ? _virtualDesktops.Status : F("{0}：四指切桌面与桌面条交给 Windows", _virtualDesktops.Status)));

        checks.Add(!cfg.Overview.SharedThumbnails
            ? new(T("共享缩略图"), CheckLevel.Info, T("设置里关掉了，用经典缩略图（没有窗口阴影）"))
            : _sharedThumbnails.Status is null
                ? new(T("共享缩略图"), CheckLevel.Info, T("还没用到（第一次打开总览时检测）"))
                : new(T("共享缩略图"), _sharedThumbnails.Unavailable ? CheckLevel.Warning : CheckLevel.Ok, _sharedThumbnails.Status));
        if (_sharedThumbnails.DecorStatus is { } decor)
            checks.Add(new(T("合成层文字与装饰"), _sharedThumbnails.DecorAvailable ? CheckLevel.Ok : CheckLevel.Warning, decor));

        bool uiAccess = SystemInfo.HasUIAccess();
        checks.Add(new("UIAccess", uiAccess ? CheckLevel.Ok : CheckLevel.Info,
            uiAccess ? T("有：覆盖层盖得住开始菜单，管理员程序也能切换") : T("普通权限版：覆盖层排在开始菜单下面，以管理员身份运行的程序切不过去、收不到 Alt+Tab")));

        checks.Add(new(T("键盘钩子"), KeyboardHook.Active ? CheckLevel.Ok : CheckLevel.Warning,
            KeyboardHook.Active ? T("已安装（Alt+Tab、Ctrl+Win+←/→）") : T("没装上：Alt+Tab、Ctrl+Win+←/→ 交给 Windows")));
        checks.Add(cfg.Mouse.Button == MouseDragButton.None
            ? new(T("鼠标钩子"), CheckLevel.Info, T("鼠标按键手势没开，不装钩子"))
            : new(T("鼠标钩子"), _mouseGestures?.Hooked == true ? CheckLevel.Ok : CheckLevel.Warning,
                _mouseGestures?.Hooked == true ? F("已安装（{0}）", SettingsModel.MouseButtonLabel(cfg.Mouse.Button)) : T("没装上：鼠标按键手势用不了")));

        // 三指轻点 = 中键：识别了几次、SendInput 失败几次；钩子装着时再看补发的中键有没有进系统的输入流
        int taps = Volatile.Read(ref _middleClicks), tapFailures = Volatile.Read(ref _middleClickFailures);
        if (!cfg.ThreeFingerTapMiddleClick)
            checks.Add(new(T("三指轻点 = 中键"), CheckLevel.Info, T("关掉了，三指轻点交给 Windows")));
        else
        {
            string detail = F("本次运行识别 {0} 次，发送失败 {1} 次", taps, tapFailures);
            if (_mouseGestures?.Hooked == true) detail += F("；鼠标钩子看到 {0} 次", MouseButtonGestures.MarkedMiddleSeen);
            checks.Add(new(T("三指轻点 = 中键"), tapFailures > 0 ? CheckLevel.Warning : taps > 0 ? CheckLevel.Ok : CheckLevel.Info, detail));
        }

        // 后台程序栏：上次总览列出了几个托盘程序、最近一次点击的结果
        if (!cfg.Overview.TrayApps)
            checks.Add(new(T("后台程序栏"), CheckLevel.Info, T("关掉了")));
        else
        {
            var tray = TrayCatalog.LastStats;
            string click = _presenter.LastTrayClick switch
            {
                null => "",
                "ok" => T("；上次点击正常"),
                var error => F("；上次点击失败：{0}", error),
            };
            string apps = _presenter.Apps.Apps.Count > 0 ? F("；开始菜单里 {0} 个程序", _presenter.Apps.Apps.Count) : "";
            checks.Add(tray.Registered == 0 && tray.Ms == 0
                ? new(T("后台程序栏"), CheckLevel.Info, T("还没打开过总览") + apps)
                : new(T("后台程序栏"), _presenter.LastTrayClick is null or "ok" ? CheckLevel.Ok : CheckLevel.Warning,
                    F("上次总览：托盘里 {0} 个图标，栏里 {1} 个，耗时 {2:F1}ms", tray.Live, tray.Shown, tray.Ms) + click + apps));
        }

        foreach (var (name, stats, period) in new[]
                 {
                     (T("渲染 · 总览"), _presenter.LastRender, _presenter.RefreshPeriodMs),
                     (T("渲染 · 切换条"), _switcherPresenter.LastRender, _switcherPresenter.RefreshPeriodMs),
                     (T("渲染 · 切桌面"), _desktopPresenter.LastRender, _desktopPresenter.RefreshPeriodMs),
                 })
        {
            if (stats.Frames < 2) continue;
            var level = stats.Late == 0 ? CheckLevel.Ok : stats.Late * 100 <= stats.Frames ? CheckLevel.Info : CheckLevel.Warning;
            checks.Add(new(name, level, F("最近一次 {0} 帧，晚到 {1} 帧，最长间隔 {2:F1}ms（刷新周期 {3:F2}ms）", stats.Frames, stats.Late, stats.MaxMs, period)));
        }

        var update = _updates.Status;
        checks.Add(update.State switch
        {
            UpdateState.Failed => new(T("升级检测"), CheckLevel.Warning, F("上次检查失败：{0}", update.Error)),
            UpdateState.Available => new(T("升级检测"), CheckLevel.Info, F("有新版本 {0}", update.Latest!.Version.ToString(3))),
            UpdateState.UpToDate => new(T("升级检测"), CheckLevel.Ok, F("已是最新（{0:HH:mm} 检查）", update.CheckedAt)),
            _ => new(T("升级检测"), CheckLevel.Info, cfg.Updates.Check ? T("还没检查过") : T("自动检查已关掉")),
        });
        return checks;
    }

    /// <summary>
    /// 生成诊断报告（UI 线程）：写到 %LOCALAPPDATA%\GlideShell\diagnostics，放进剪贴板，用默认的文本编辑器打开给用户看。
    /// 不上传任何东西。
    /// </summary>
    private void CreateDiagnosticReport()
    {
        var cfg = _config;
        var report = new DiagnosticReport(Version)
            .Section(T("系统"), [
                SystemInfo.WindowsVersion(),
                F("程序：{0}（{1}）", _exePath, (SystemInfo.HasUIAccess() ? T("UIAccess 版") : T("普通权限版"))),
                F("运行状态：{0}", (_paused ? T("已暂停") : T("接管中"))),
            ])
            .Section(T("显示器"), Monitors.Enumerate().Select(m =>
                F("{0} {1:F0}×{2:F0} 位于 ({3:F0}, {4:F0})，缩放 {5:F0}%", (m.IsPrimary ? T("主屏") : T("副屏")), m.Bounds.Width, m.Bounds.Height, m.Bounds.Left, m.Bounds.Top, m.DpiScale * 100)))
            .Checks(CompatibilityChecks())
            .Section(T("最近一次异常退出"), CrashRecord.Latest() is { } crash ? [crash] : [])
            .Section(T("配置文件"), ReadConfigText())
            .Section(F("最近 {0} 行日志", ReportLogLines), SystemInfo.LogTail(ReportLogLines));
        string text = report.ToString();
        string path;
        try
        {
            path = report.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"诊断报告保存失败：{ex.Message}");
            _tray?.ShowBalloon(T("诊断报告保存失败"), ex.Message, warning: true);
            return;
        }
        bool copied = SystemInfo.CopyToClipboard(_ui.Handle, text);
        Log.Info($"诊断报告：{path}（{text.Length} 字{(copied ? "，已复制到剪贴板" : "")}）");
        OpenPath(path);
        _tray?.ShowBalloon(T("诊断报告已生成"), copied
            ? T("已复制到剪贴板，也已用记事本打开。看一眼没问题再贴到 GitHub Issue。")
            : T("已用记事本打开。看一眼没问题再贴到 GitHub Issue。"));
    }

    private static IEnumerable<string> ReadConfigText()
    {
        try
        {
            return File.Exists(ConfigStore.DefaultPath) ? File.ReadAllLines(ConfigStore.DefaultPath) : [T("（用的是默认设置，没有配置文件）")];
        }
        catch (IOException ex)
        {
            return [F("（读不了：{0}）", ex.Message)];
        }
    }

    /// <summary>启动时（UI 线程，托盘已建好）：上次异常退出过就提示一次，点通知生成诊断报告。提示了返回 true。</summary>
    private bool NoticeLastCrash()
    {
        if (CrashRecord.TakePending() is not { } crash) return false;
        Log.Warn($"上次异常退出：{crash.Trim()}");
        _tray?.ShowBalloon(T("GlideShell 上次异常退出了"), T("系统手势已经自动还原。点这里生成诊断报告，方便反馈问题。"), warning: true,
            onClick: CreateDiagnosticReport);
        return true;
    }
}
