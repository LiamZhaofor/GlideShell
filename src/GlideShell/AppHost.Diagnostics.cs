using GlideShell.Core.Configuration;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Overview;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;
using GlideShell.Platform.Updates;

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
            checks.Add(new("触摸板", CheckLevel.Unavailable, "没有找到精确式触摸板（PTP），三指 / 四指手势用不了"));
        foreach (var d in devices)
        {
            var g = d.Geometry;
            string mode = g.ContactSlots >= 5 ? "并行报告（一份报文带全部手指）" : $"每份报文 {g.ContactSlots} 个触点槽，可能是混合报告模式（这种模式只有单元测试，还没在真机上验证）";
            checks.Add(new("触摸板", g.ContactSlots >= 5 ? CheckLevel.Ok : CheckLevel.Warning,
                $"VID {d.VendorId:X4} / PID {d.ProductId:X4}，{g.X.SizeMm:F1} × {g.Y.SizeMm:F1} mm，{mode}"));
        }

        checks.Add(_paused
            ? new("系统手势接管", CheckLevel.Info, "已暂停：用的是 Windows 原生手势")
            : _nativeVerified switch
            {
                true => new("系统手势接管", CheckLevel.Ok, "已生效：被 GlideShell 接管的手势，Windows 自己不再响应"),
                false => new("系统手势接管", CheckLevel.Warning, "设置已写入，但 Explorer 还没生效（可能两边都会响应）；重启资源管理器或注销后生效"),
                _ => new("系统手势接管", CheckLevel.Info, "还没接管"),
            });

        checks.Add(new("虚拟桌面接口", _virtualDesktops.Available ? CheckLevel.Ok : CheckLevel.Unavailable,
            _virtualDesktops.Available ? _virtualDesktops.Status : $"{_virtualDesktops.Status}：四指切桌面与桌面条交给 Windows"));

        checks.Add(!cfg.Overview.SharedThumbnails
            ? new("共享缩略图", CheckLevel.Info, "设置里关掉了，用经典缩略图（没有窗口阴影）")
            : _sharedThumbnails.Status is null
                ? new("共享缩略图", CheckLevel.Info, "还没用到（第一次打开总览时检测）")
                : new("共享缩略图", _sharedThumbnails.Unavailable ? CheckLevel.Warning : CheckLevel.Ok, _sharedThumbnails.Status));
        if (_sharedThumbnails.DecorStatus is { } decor)
            checks.Add(new("合成层文字与装饰", decor.Contains("可用", StringComparison.Ordinal) ? CheckLevel.Ok : CheckLevel.Warning, decor));

        bool uiAccess = SystemInfo.HasUIAccess();
        checks.Add(new("UIAccess", uiAccess ? CheckLevel.Ok : CheckLevel.Info,
            uiAccess ? "有：覆盖层盖得住开始菜单，管理员程序也能切换" : "普通权限版：覆盖层排在开始菜单下面，以管理员身份运行的程序切不过去、收不到 Alt+Tab"));

        checks.Add(new("键盘钩子", KeyboardHook.Active ? CheckLevel.Ok : CheckLevel.Warning,
            KeyboardHook.Active ? "已安装（Alt+Tab、Ctrl+Win+←/→）" : "没装上：Alt+Tab、Ctrl+Win+←/→ 交给 Windows"));
        checks.Add(cfg.Mouse.Button == MouseDragButton.None
            ? new("鼠标钩子", CheckLevel.Info, "鼠标按键手势没开，不装钩子")
            : new("鼠标钩子", _mouseGestures?.Hooked == true ? CheckLevel.Ok : CheckLevel.Warning,
                _mouseGestures?.Hooked == true ? $"已安装（{SettingsModel.MouseButtonLabel(cfg.Mouse.Button)}）" : "没装上：鼠标按键手势用不了"));

        foreach (var (name, stats, period) in new[]
                 {
                     ("渲染 · 总览", _presenter.LastRender, _presenter.RefreshPeriodMs),
                     ("渲染 · 切换条", _switcherPresenter.LastRender, _switcherPresenter.RefreshPeriodMs),
                     ("渲染 · 切桌面", _desktopPresenter.LastRender, _desktopPresenter.RefreshPeriodMs),
                 })
        {
            if (stats.Frames < 2) continue;
            var level = stats.Late == 0 ? CheckLevel.Ok : stats.Late * 100 <= stats.Frames ? CheckLevel.Info : CheckLevel.Warning;
            checks.Add(new(name, level, $"最近一次 {stats.Frames} 帧，晚到 {stats.Late} 帧，最长间隔 {stats.MaxMs:F1}ms（刷新周期 {period:F2}ms）"));
        }

        var update = _updates.Status;
        checks.Add(update.State switch
        {
            UpdateState.Failed => new("升级检测", CheckLevel.Warning, $"上次检查失败：{update.Error}"),
            UpdateState.Available => new("升级检测", CheckLevel.Info, $"有新版本 {update.Latest!.Version.ToString(3)}"),
            UpdateState.UpToDate => new("升级检测", CheckLevel.Ok, $"已是最新（{update.CheckedAt:HH:mm} 检查）"),
            _ => new("升级检测", CheckLevel.Info, cfg.Updates.Check ? "还没检查过" : "自动检查已关掉"),
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
            .Section("系统", [
                SystemInfo.WindowsVersion(),
                $"程序：{_exePath}（{(SystemInfo.HasUIAccess() ? "UIAccess 版" : "普通权限版")}）",
                $"运行状态：{(_paused ? "已暂停" : "接管中")}",
            ])
            .Section("显示器", Monitors.Enumerate().Select(m =>
                $"{(m.IsPrimary ? "主屏" : "副屏")} {m.Bounds.Width:F0}×{m.Bounds.Height:F0} 位于 ({m.Bounds.Left:F0}, {m.Bounds.Top:F0})，缩放 {m.DpiScale * 100:F0}%"))
            .Checks(CompatibilityChecks())
            .Section("最近一次异常退出", CrashRecord.Latest() is { } crash ? [crash] : [])
            .Section("配置文件", ReadConfigText())
            .Section($"最近 {ReportLogLines} 行日志", SystemInfo.LogTail(ReportLogLines));
        string text = report.ToString();
        string path;
        try
        {
            path = report.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"诊断报告保存失败：{ex.Message}");
            _tray?.ShowBalloon("诊断报告保存失败", ex.Message, warning: true);
            return;
        }
        bool copied = SystemInfo.CopyToClipboard(_ui.Handle, text);
        Log.Info($"诊断报告：{path}（{text.Length} 字{(copied ? "，已复制到剪贴板" : "")}）");
        OpenPath(path);
        _tray?.ShowBalloon("诊断报告已生成", copied
            ? "已复制到剪贴板，也已用记事本打开。看一眼没问题再贴到 GitHub Issue。"
            : "已用记事本打开。看一眼没问题再贴到 GitHub Issue。");
    }

    private static IEnumerable<string> ReadConfigText()
    {
        try
        {
            return File.Exists(ConfigStore.DefaultPath) ? File.ReadAllLines(ConfigStore.DefaultPath) : ["（用的是默认设置，没有配置文件）"];
        }
        catch (IOException ex)
        {
            return [$"（读不了：{ex.Message}）"];
        }
    }

    /// <summary>启动时（UI 线程，托盘已建好）：上次异常退出过就提示一次，点通知生成诊断报告。提示了返回 true。</summary>
    private bool NoticeLastCrash()
    {
        if (CrashRecord.TakePending() is not { } crash) return false;
        Log.Warn($"上次异常退出：{crash.Trim()}");
        _tray?.ShowBalloon("GlideShell 上次异常退出了", "系统手势已经自动还原。点这里生成诊断报告，方便反馈问题。", warning: true,
            onClick: CreateDiagnosticReport);
        return true;
    }
}
