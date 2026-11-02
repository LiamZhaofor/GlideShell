using GlideShell.Core.Updates;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;
using GlideShell.Platform.Updates;
using static GlideShell.Core.Localization.L;

namespace GlideShell;

/// <summary>一键升级（2.0 分发）：设置「关于 → 更新」的「升级」、托盘菜单、新版本通知、GlideShell.exe --upgrade。</summary>
internal sealed partial class AppHost
{
    /// <summary>安装脚本启动后这么久本进程还在：脚本出错了，允许再试。</summary>
    private const int UpgradeStallMs = 30_000;

    private const string WinGetUpgradeCommand = "winget upgrade LiamZhaofor.GlideShell";

    private readonly InstallKind _installKind = InstallLocation.Classify(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    private Upgrader? _upgrader;
    private bool _upgradeAfterCheck; // --upgrade：查完有新版本就直接升级

    private Upgrader Upgrader => _upgrader ??= new Upgrader(_updates.Agent);

    /// <summary>这种装法能不能一键升级（能的话，新版本发布包可验证时设置里出现「升级」）。</summary>
    private bool UpgradesInPlace => InstallLocation.CanUpgradeInPlace(_installKind);

    private bool CanOneClickUpgrade => UpgradesInPlace && _updates.Status is { State: UpdateState.Available, Latest.Package: not null };

    /// <summary>不能一键升级时给用户的一句话（能的话为 null）。</summary>
    private string? UpgradeUnavailableReason => _installKind switch
    {
        InstallKind.WinGet => F("这是用 winget 装的：在终端里运行 {0} 升级", WinGetUpgradeCommand),
        InstallKind.ProgramFiles => T("这是装在 Program Files 的 UIAccess 版（从源码构建）：发布包是普通权限版，覆盖会丢掉 UIAccess，请按 README 重新构建安装"),
        _ => null,
    };

    /// <summary>托盘菜单最上面那一项（没有新版本时为 null）。</summary>
    private TrayMenuItem? UpgradeMenuItem()
    {
        if (_updates.Status is not { State: UpdateState.Available, Latest: { } latest }) return null;
        string v = latest.Version.ToString(3);
        if (Upgrader.Status.Busy) return new TrayMenuItem(F("正在升级到 {0}…", v), Enabled: false);
        if (CanOneClickUpgrade) return new TrayMenuItem(F("升级到 {0}…", v), () => StartUpgrade(confirm: true));
        if (_installKind == InstallKind.WinGet) return new TrayMenuItem(F("用 winget 升级到 {0}…", v), () => StartUpgrade(confirm: false));
        return new TrayMenuItem(F("下载新版本 {0}…", v), OpenUpdatePage);
    }

    /// <summary>
    /// 升级到新版本（UI 线程）。能一键升级就下载、校验、运行新版本的安装脚本（本进程随后被它正常关掉，再启动新版本）；
    /// winget 装的把升级命令放进剪贴板；其余打开下载页面。<paramref name="confirm"/>：先问一下（托盘菜单、通知）。
    /// </summary>
    private void StartUpgrade(bool confirm)
    {
        if (_updates.Status is not { State: UpdateState.Available, Latest: { } latest }) return;
        if (!CanOneClickUpgrade)
        {
            if (_installKind == InstallKind.WinGet)
            {
                bool copied = SystemInfo.CopyToClipboard(_ui.Handle, WinGetUpgradeCommand);
                _tray?.ShowBalloon(T("用 winget 升级"), copied ? F("升级命令已复制：{0}，粘贴到终端里运行即可。", WinGetUpgradeCommand) : F("在终端里运行：{0}", WinGetUpgradeCommand));
            }
            else OpenUpdatePage();
            return;
        }
        if (Upgrader.Status.Busy) return;
        string v = latest.Version.ToString(3);
        if (confirm && !Dialogs.Confirm(_ui.Handle, F("升级到 GlideShell {0}？\n\n会从 GitHub 下载新版本、校验后安装，GlideShell 会退出并自动重新启动，设置会保留。", v),
                T("升级 GlideShell"))) return;
        bool autostart = Autostart.IsEnabled(_exePath);
        string destination = AppContext.BaseDirectory.TrimEnd('\\');
        Log.Info($"升级：开始升级到 {v}（{_installKind}，装到 {destination}，开机启动 {(autostart ? "保留" : "不开")}）");
        Upgrader.Start(latest, destination, shortcut: _installKind == InstallKind.Standard, autostart, s => _ui.Post(() => OnUpgradeChanged(s)));
        SettingsWindow.RefreshIfOpen();
    }

    private void OnUpgradeChanged(UpgradeStatus status)
    {
        SettingsWindow.RefreshIfOpen();
        if (_tray is not null)
            _tray.Tooltip = status.Stage switch
            {
                UpgradeStage.Downloading => F("GlideShell：正在下载新版本 {0:0}%", status.Progress * 100),
                UpgradeStage.Verifying => T("GlideShell：正在校验新版本"),
                UpgradeStage.Installing => T("GlideShell：正在安装新版本"),
                _ => _paused ? T("GlideShell（已暂停，使用系统手势）") : "GlideShell",
            };
        switch (status.Stage)
        {
            case UpgradeStage.Failed:
                _tray?.ShowBalloon(T("升级失败"), F("{0}。可以在设置里再试一次，点这里打开下载页面手动安装。", status.Error), warning: true, onClick: OpenUpdatePage);
                break;
            case UpgradeStage.Installing:
                _ui.PostDelayed(UpgradeStallMs, () =>
                {
                    if (Upgrader.Status.Stage != UpgradeStage.Installing) return;
                    Upgrader.MarkStalled();
                    Log.Warn($"升级：安装脚本 {UpgradeStallMs / 1000} 秒后还没有关掉本进程，见 {Upgrader.LogPath}");
                    OnUpgradeChanged(Upgrader.Status);
                });
                break;
        }
    }

    /// <summary>GlideShell.exe --upgrade（UI 线程）：有新版本就直接升级，还没查过就先查。</summary>
    private void UpgradeFromCommandLine()
    {
        if (_updates.Status.State == UpdateState.Available) StartUpgrade(confirm: false);
        else
        {
            _upgradeAfterCheck = true;
            CheckUpdates(manual: true);
        }
    }
}
