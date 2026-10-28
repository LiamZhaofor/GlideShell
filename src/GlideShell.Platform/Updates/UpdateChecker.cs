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
    private const int TimeoutMs = 15_000;
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
        nint session = 0, connect = 0, request = 0;
        try
        {
            session = WinHttp.WinHttpOpen($"GlideShell/{Current.ToString(3)}", WinHttp.WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, null, null, 0);
            if (session == 0) throw new Win32Exception();
            WinHttp.WinHttpSetTimeouts(session, TimeoutMs, TimeoutMs, TimeoutMs, TimeoutMs);
            connect = WinHttp.WinHttpConnect(session, Host, WinHttp.INTERNET_DEFAULT_HTTPS_PORT, 0);
            if (connect == 0) throw new Win32Exception();
            request = WinHttp.WinHttpOpenRequest(connect, "GET", path, null, null, 0, WinHttp.WINHTTP_FLAG_SECURE);
            if (request == 0) throw new Win32Exception();
            const string headers = "Accept: application/vnd.github+json\r\nX-GitHub-Api-Version: 2022-11-28\r\n";
            if (!WinHttp.WinHttpSendRequest(request, headers, uint.MaxValue /* -1：以 null 结尾 */, 0, 0, 0, 0)) throw new Win32Exception();
            if (!WinHttp.WinHttpReceiveResponse(request, 0)) throw new Win32Exception();
            uint status = 0, size = sizeof(uint);
            if (!WinHttp.WinHttpQueryHeaders(request, WinHttp.WINHTTP_QUERY_STATUS_CODE | WinHttp.WINHTTP_QUERY_FLAG_NUMBER, 0, &status, &size, null))
                throw new Win32Exception();

            using var body = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                uint read;
                fixed (byte* p = chunk)
                    if (!WinHttp.WinHttpReadData(request, p, (uint)chunk.Length, &read)) throw new Win32Exception();
                if (read == 0) break;
                body.Write(chunk, 0, (int)read);
                if (body.Length > MaxBytes) throw new IOException("GitHub 的回复太大");
            }
            return ((int)status, Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length));
        }
        finally
        {
            if (request != 0) WinHttp.WinHttpCloseHandle(request);
            if (connect != 0) WinHttp.WinHttpCloseHandle(connect);
            if (session != 0) WinHttp.WinHttpCloseHandle(session);
        }
    }

    /// <summary>常见的 WinHTTP 错误码换成一句话（系统的错误文字对 WinHTTP 的码大多是「未知错误」）。</summary>
    private static string Describe(int code) => code switch
    {
        12002 => "连接超时",
        12007 => "找不到服务器（没有联网？）",
        12029 => "连不上 GitHub",
        12030 => "连接被中断",
        12038 or 12044 or 12045 or 12057 or 12175 => "安全连接失败",
        _ => $"网络错误 {code}",
    };
}
