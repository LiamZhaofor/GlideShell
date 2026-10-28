using System.ComponentModel;
using System.Text;
using GlideShell.Core.Updates;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Updates;

public enum UpdateState
{
    NotChecked,
    Checking,
    UpToDate,
    Available,
    Failed,
}

/// <summary>最近一次检查的结果。</summary>
/// <param name="Latest">GitHub 上最新的正式版本（查到时）。</param>
/// <param name="CheckedAt">完成的时间（本地时间，没查过为 default）。</param>
/// <param name="Error">失败原因（给人看的一句话）。</param>
public sealed record UpdateStatus(UpdateState State, ReleaseInfo? Latest = null, DateTime CheckedAt = default, string? Error = null);

/// <summary>
/// 升级检测：用系统的 WinHTTP 向 GitHub 查询最新的正式版本（<c>/releases/latest</c>），和当前版本比较。
/// 请求里只有版本查询本身（User-Agent 带当前版本号，GitHub API 要求有 User-Agent），不发送任何其他数据；不下载、不自动安装。
/// 走系统代理设置。同一时间只跑一个查询，在线程池线程上完成。
/// </summary>
public sealed unsafe class UpdateChecker
{
    public const string Repository = "LiamZhaofor/GlideShell";

    /// <summary>查不到具体版本页面时打开的地址。</summary>
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";

    private const string Host = "api.github.com";
    private const int MaxBytes = 1 << 20; // 正常的响应只有几 KB

    private readonly string _notifiedPath;
    private volatile UpdateStatus _status = new(UpdateState.NotChecked);
    private int _running;

    /// <param name="current">当前版本。</param>
    /// <param name="stateDirectory">记录「哪个版本已经通知过」的目录（%LOCALAPPDATA%\GlideShell）。</param>
    public UpdateChecker(Version current, string stateDirectory)
    {
        Current = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        _notifiedPath = Path.Combine(stateDirectory, "update-notified.txt");
    }

    public Version Current { get; }

    public UpdateStatus Status => _status;

    /// <summary>新版本的页面（下载与更新说明）。</summary>
    public string PageUrl => _status.Latest is { Url.Length: > 0 } r ? r.Url : ReleasesPage;

    /// <summary>后台查询一次；已经在查时返回 false。完成后在线程池线程上回调 <paramref name="onDone"/>。</summary>
    public bool CheckAsync(Action<UpdateStatus> onDone)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return false;
        _status = _status with { State = UpdateState.Checking, Error = null };
        Task.Run(() =>
        {
            var result = Check();
            _status = result;
            Volatile.Write(ref _running, 0);
            onDone(result);
        });
        return true;
    }

    /// <summary>
    /// 这个版本第一次发现：记下来并返回 true（托盘只为每个新版本通知一次，重启后也记得）；通知过返回 false。
    /// </summary>
    public bool MarkNotified(Version version)
    {
        string text = version.ToString(3);
        try
        {
            if (File.Exists(_notifiedPath) && File.ReadAllText(_notifiedPath).Trim() == text) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(_notifiedPath)!);
            File.WriteAllText(_notifiedPath, text);
        }
        catch (IOException)
        {
            // 记不下来：最多是下次启动再通知一次
        }
        catch (UnauthorizedAccessException)
        {
        }
        return true;
    }

    private UpdateStatus Check()
    {
        var now = DateTime.Now;
        try
        {
            var (code, body) = Get($"/repos/{Repository}/releases/latest");
            if (code == 404) return new UpdateStatus(UpdateState.UpToDate, null, now); // 还没有任何正式版本
            if (code is 403 or 429) return new UpdateStatus(UpdateState.Failed, null, now, "GitHub 暂时限制了查询次数，稍后再试");
            if (code != 200) return new UpdateStatus(UpdateState.Failed, null, now, $"GitHub 返回 HTTP {code}");
            if (ReleaseInfo.Parse(body) is not { } latest) return new UpdateStatus(UpdateState.Failed, null, now, "看不懂 GitHub 的回复");
            return new UpdateStatus(latest.IsNewerThan(Current) ? UpdateState.Available : UpdateState.UpToDate, latest, now);
        }
        catch (Win32Exception ex)
        {
            return new UpdateStatus(UpdateState.Failed, null, now, Describe(ex.NativeErrorCode));
        }
        catch (IOException ex)
        {
            return new UpdateStatus(UpdateState.Failed, null, now, ex.Message);
        }
    }

    private (int Status, string Body) Get(string path)
    {
        const string headers = "Accept: application/vnd.github+json\r\nX-GitHub-Api-Version: 2022-11-28\r\n";
        using var body = new MemoryStream();
        int status = WinHttpClient.Get($"https://{Host}{path}", Agent, headers, body, MaxBytes);
        return (status, Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length));
    }

    /// <summary>User-Agent（GitHub API 要求有）：只带程序名与当前版本号。</summary>
    public string Agent => $"GlideShell/{Current.ToString(3)}";

    private static string Describe(int code) => WinHttpClient.Describe(code);
}
