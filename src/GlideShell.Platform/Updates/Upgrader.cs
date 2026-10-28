using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using GlideShell.Core.Updates;
using GlideShell.Platform.Diagnostics;

namespace GlideShell.Platform.Updates;

public enum UpgradeStage
{
    Idle,
    Downloading,
    Verifying,
    Installing,
    Failed,
}

/// <param name="Progress">下载进度 0 ~ 1。</param>
public sealed record UpgradeStatus(UpgradeStage Stage, double Progress = 0, string? Error = null)
{
    public bool Busy => Stage is UpgradeStage.Downloading or UpgradeStage.Verifying or UpgradeStage.Installing;
}

/// <summary>
/// 一键升级：下载新版本的发布包到 %LOCALAPPDATA%\GlideShell\updates，核对大小与 GitHub 给出的 SHA-256，解压，
/// 在隐藏窗口里运行包里的 install.ps1 装回当前目录（脚本会用 --quit 让本进程正常退出、还原系统手势，复制后重新启动新版本）。
/// 包里的安装脚本是新版本自己的，所以以后改安装步骤不用动这里。安装脚本的输出写到 logs\upgrade.log。
/// 发布包没有签名：可信来自「只用本仓库发布下的附件」与 SHA-256 一致（地址与摘要都来自 HTTPS 的 GitHub API）。
/// </summary>
public sealed class Upgrader
{
    private const long MaxPackageBytes = 64L * 1024 * 1024;

    private readonly string _agent;
    private volatile UpgradeStatus _status = new(UpgradeStage.Idle);
    private int _running;

    public Upgrader(string agent) => _agent = agent;

    public UpgradeStatus Status => _status;

    private static string UpdatesDirectory => Path.Combine(Path.GetDirectoryName(Log.DefaultDirectory)!, "updates");

    public static string LogPath => Path.Combine(Log.DefaultDirectory, "upgrade.log");

    /// <summary>
    /// 后台开始升级（同一时间只有一次）；每到一步在线程池线程上回调 <paramref name="onChange"/>。
    /// 安装脚本启动后本进程很快就会被它关掉；没被关掉（脚本出错）由调用方超时处理。
    /// </summary>
    /// <param name="destination">装回哪个目录（当前 exe 所在目录）。</param>
    /// <param name="shortcut">重建开始菜单快捷方式（标准位置才要）。</param>
    /// <param name="autostart">保留开机启动（原来开着才要）。</param>
    public bool Start(ReleaseInfo release, string destination, bool shortcut, bool autostart, Action<UpgradeStatus> onChange)
    {
        if (release.Package is not { } package) return false;
        if (Interlocked.Exchange(ref _running, 1) == 1) return false;
        Task.Run(() =>
        {
            void Report(UpgradeStatus s)
            {
                _status = s;
                onChange(s);
            }

            try
            {
                Report(new(UpgradeStage.Downloading));
                string dir = Path.Combine(UpdatesDirectory, release.Version.ToString(3));
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                Directory.CreateDirectory(dir);
                string zip = Path.Combine(dir, package.Name);
                long lastReported = 0;
                using (var file = new FileStream(zip, FileMode.Create, FileAccess.Write))
                {
                    int code = WinHttpClient.Get(package.Url, _agent, "Accept: application/octet-stream\r\n", file, MaxPackageBytes, received =>
                    {
                        if (received - lastReported < 128 * 1024 && received < package.Size) return;
                        lastReported = received;
                        Report(new(UpgradeStage.Downloading, Math.Clamp((double)received / package.Size, 0, 1)));
                    });
                    if (code != 200) throw new IOException($"下载失败：GitHub 返回 HTTP {code}");
                }

                Report(new(UpgradeStage.Verifying, 1));
                var info = new FileInfo(zip);
                if (info.Length != package.Size) throw new IOException($"下载的大小不对（{info.Length} / {package.Size} 字节）");
                string sha;
                using (var stream = File.OpenRead(zip)) sha = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (sha != package.Sha256) throw new IOException("下载的文件校验不通过（SHA-256 与 GitHub 给出的不一致），没有安装");
                string unpacked = Path.Combine(dir, "package");
                ZipFile.ExtractToDirectory(zip, unpacked);
                string script = Path.Combine(unpacked, "install.ps1");
                if (!File.Exists(script) || !File.Exists(Path.Combine(unpacked, "GlideShell.exe"))) throw new IOException("发布包里缺少安装脚本或程序");

                Report(new(UpgradeStage.Installing, 1));
                Log.Info($"升级：{package.Name} 已下载并校验（SHA-256 {sha[..12]}…），运行安装脚本装到 {destination}");
                Log.Flush(TimeSpan.FromSeconds(1));
                LaunchInstaller(script, destination, shortcut, autostart);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception)
            {
                string message = ex is Win32Exception w && w.NativeErrorCode >= 12000 ? WinHttpClient.Describe(w.NativeErrorCode) : ex.Message;
                Log.Warn($"升级失败：{message}");
                Volatile.Write(ref _running, 0);
                Report(new(UpgradeStage.Failed, 0, message));
            }
        });
        return true;
    }

    /// <summary>安装脚本超时没有关掉本进程：当作失败，允许再试。</summary>
    public void MarkStalled()
    {
        if (_status.Stage != UpgradeStage.Installing) return;
        Volatile.Write(ref _running, 0);
        _status = new(UpgradeStage.Failed, 0, "安装脚本没有完成，详见日志目录里的 upgrade.log");
    }

    /// <summary>Windows PowerShell（系统自带）运行新版本的 install.ps1，输出写到 upgrade.log；隐藏窗口，不等它结束。</summary>
    private static void LaunchInstaller(string script, string destination, bool shortcut, bool autostart)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        string command = $"& {Quote(script)} -Destination {Quote(destination)}" +
                         (shortcut ? "" : " -NoShortcut") + (autostart ? "" : " -NoAutostart") +
                         $" *> {Quote(LogPath)}";
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-Command", command })
            psi.ArgumentList.Add(arg);
        Process.Start(psi)?.Dispose();
    }
}
