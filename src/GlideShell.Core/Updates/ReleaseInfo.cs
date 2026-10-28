using System.Text.Json;

namespace GlideShell.Core.Updates;

/// <summary>发布附件：名字、下载地址、大小，以及 GitHub 给出的 SHA-256（小写十六进制，没有为 null）。</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>GitHub 上的一个正式版本（<c>/releases/latest</c>，不含预发布与草稿）。</summary>
/// <param name="Version">版本号（主.次.修订）。</param>
/// <param name="Tag">原始标签，例如 <c>v1.6.1</c>。</param>
/// <param name="Url">版本页面（下载与更新说明）。</param>
public sealed record ReleaseInfo(Version Version, string Tag, string Url)
{
    /// <summary>一键升级只认这个仓库自己的发布附件（下载地址不在这下面的一律不用）。</summary>
    public const string DownloadPrefix = "https://github.com/LiamZhaofor/GlideShell/releases/download/";

    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    /// <summary>
    /// 一键升级用的发布包：<c>GlideShell-{版本}-win-x64.zip</c>，下载地址在本仓库的发布下面，并且带合法的 SHA-256。
    /// 任一条不满足就是 null（只能打开下载页面手动装）。
    /// </summary>
    public ReleaseAsset? Package => Assets.FirstOrDefault(a =>
        a.Name.Equals($"GlideShell-{Version.ToString(3)}-win-x64.zip", StringComparison.OrdinalIgnoreCase) &&
        a.Url.StartsWith(DownloadPrefix + Tag + "/", StringComparison.Ordinal) &&
        a.Sha256 is { Length: 64 } && a.Size > 0);

    /// <summary>
    /// 解析 GitHub <c>GET /repos/{owner}/{repo}/releases/latest</c> 的响应。标签不是版本号、或是预发布 / 草稿时返回 null。
    /// 用 <see cref="JsonDocument"/> 读，不用反射（NativeAOT 安全）。
    /// </summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Flag(root, "prerelease") || Flag(root, "draft")) return null;
            string tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            string url = root.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
            if (!TryParseVersion(tag, out var version)) return null;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "";
            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var a in list.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    string name = Str(a, "name"), download = Str(a, "browser_download_url"), digest = Str(a, "digest");
                    long size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out long n) ? n : 0;
                    string? sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && IsHex64(digest[7..]) ? digest[7..].ToLowerInvariant() : null;
                    if (name.Length > 0 && download.Length > 0) assets.Add(new ReleaseAsset(name, download, size, sha));
                }
            return new ReleaseInfo(version, tag, url) { Assets = assets };
        }
        catch (JsonException)
        {
            return null;
        }

        static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static bool IsHex64(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);
    }

    /// <summary>
    /// 从标签或程序版本里读出「主.次.修订」：接受 <c>v1.6.1</c>、<c>1.6.1</c>、<c>1.6.1.0</c>、<c>1.6.1+提交号</c>、<c>1.6</c>（修订记为 0）；
    /// 带预发布后缀（<c>1.7.0-beta</c>）的不算。
    /// </summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        if (s.Contains('-')) return false;
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var numbers = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)) return false;
            if (i < 3) numbers[i] = n;
        }
        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>是否比 <paramref name="current"/> 新（只比主.次.修订）。</summary>
    public bool IsNewerThan(Version current) => Version > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
}
