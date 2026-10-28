using System.Text.Json;

namespace GlideShell.Core.Updates;

/// <summary>GitHub 上的一个正式版本（<c>/releases/latest</c>，不含预发布与草稿）。</summary>
/// <param name="Version">版本号（主.次.修订）。</param>
/// <param name="Tag">原始标签，例如 <c>v1.6.1</c>。</param>
/// <param name="Url">版本页面（下载与更新说明）。</param>
public sealed record ReleaseInfo(Version Version, string Tag, string Url)
{
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
            return new ReleaseInfo(version, tag, url);
        }
        catch (JsonException)
        {
            return null;
        }

        static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
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
