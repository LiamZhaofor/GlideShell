using System.Globalization;
using System.Text;

namespace GlideShell.Core.Search;

/// <summary>可以搜到的一个程序：<see cref="Key"/> 区分程序（启动、记使用次数都按它），<see cref="Name"/> 是开始菜单里的名字。</summary>
public sealed class SearchEntry(string key, string name)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public SearchName Searchable { get; } = SearchName.Of(name);
}

/// <summary>一条结果：<see cref="Highlights"/> 是名字里匹配上的字（界面标成蓝色）。</summary>
public sealed record SearchResult(SearchEntry Entry, int[] Highlights, double Score);

/// <summary>
/// 打字找程序：匹配（<see cref="SearchMatcher"/>）后按代价排序，再按用户打开它的次数和最近一次打开的时间加权，
/// 常用的程序排到前面。
/// </summary>
public static class AppSearch
{
    public const int MaxResults = 8;

    // 开始菜单里不是程序本身的项：说明书、网址、卸载程序等
    private static readonly string[] NoiseWords =
    [
        "uninstall", "卸载", "readme", "read me", "release notes", "license", "documentation", "manual", "website", "help",
        "帮助", "说明", "官网", "网站", "文档", "许可",
    ];

    private static readonly string[] NoiseExtensions = [".url", ".chm", ".pdf", ".txt", ".htm", ".html", ".rtf", ".md", ".hlp", ".xps"];

    /// <summary>开始菜单里这一项是不是值得搜到的程序（<paramref name="target"/>：快捷方式指向的文件，商店应用为 null）。</summary>
    public static bool IsProgram(string name, string? target)
    {
        string lower = name.ToLowerInvariant();
        foreach (string word in NoiseWords)
            if (lower.Contains(word, StringComparison.Ordinal)) return false;
        if (target is { Length: > 0 })
        {
            if (target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string ext in NoiseExtensions)
                if (target.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>按 <paramref name="query"/> 找程序，最好的在前，最多 <paramref name="max"/> 条。</summary>
    public static IReadOnlyList<SearchResult> Find(string query, IReadOnlyList<SearchEntry> entries, AppUsage usage, DateTime now, int max = MaxResults)
    {
        string q = SearchMatcher.Normalize(query);
        if (q.Length == 0) return [];
        // 允许的最大代价：查询越长可以越宽松，但跳来跳去的离谱匹配不要
        int limit = 6 + 3 * q.Length;
        var found = new List<SearchResult>();
        foreach (var entry in entries)
        {
            if (SearchMatcher.Match(q, entry.Searchable) is not { } match || match.Cost > limit) continue;
            // 和名字完全一样的永远排第一（打全了名字就是要它），其余按代价减去使用加权
            double score = match.Exact ? -100 : match.Cost - usage.Bonus(entry.Key, now);
            found.Add(new SearchResult(entry, match.Highlights, score));
        }
        return found
            .OrderBy(r => r.Score)
            .ThenBy(r => r.Entry.Name.Length)
            .ThenBy(r => r.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// 还没输入时列出的程序：从搜索里打开过的，最近打开的在最上面（用户：「最后搜索的放到上面，这才符合逻辑」）。
    /// 没打开过任何程序时为空。
    /// </summary>
    public static IReadOnlyList<SearchResult> Frequent(IReadOnlyList<SearchEntry> entries, AppUsage usage, int max = MaxResults) =>
        entries
            .Where(e => usage.TimesOpened(e.Key) > 0)
            .Select(e => new SearchResult(e, [], 0))
            .OrderByDescending(r => usage.LastOpened(r.Entry.Key))
            .ThenBy(r => r.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .ToList();
}

/// <summary>
/// 从搜索里打开各个程序的次数与最近一次的时间（存在本机配置目录，一行一个：次数、时间、程序）。
/// 加权：次数取对数（打开 1 次 +1.5，7 次 +4.5，封顶 6），三天内打开过再 +1.5——足够把常用的程序从首字母档提到前缀档之前，
/// 但压不过和名字完全一样的（那一档永远排第一）。
/// </summary>
public sealed class AppUsage
{
    private readonly Dictionary<string, (int Count, DateTime Last)> _items = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _items.Count;

    public void Record(string key, DateTime now)
    {
        _items.TryGetValue(key, out var old);
        _items[key] = (old.Count + 1, now);
    }

    public int TimesOpened(string key) => _items.TryGetValue(key, out var v) ? v.Count : 0;

    /// <summary>最近一次从搜索里打开的时间（UTC；没打开过为 <see cref="DateTime.MinValue"/>）。</summary>
    public DateTime LastOpened(string key) => _items.TryGetValue(key, out var v) ? v.Last : DateTime.MinValue;

    public double Bonus(string key, DateTime now)
    {
        if (!_items.TryGetValue(key, out var v)) return 0;
        double bonus = Math.Min(6, 1.5 * Math.Log2(1 + v.Count));
        if (now - v.Last < TimeSpan.FromDays(3)) bonus += 1.5;
        return bonus;
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        foreach (var (key, (count, last)) in _items.OrderByDescending(p => p.Value.Last))
            sb.Append(count.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(last.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\t')
              .Append(key).Append('\n');
        return sb.ToString();
    }

    /// <summary>读回 <see cref="Serialize"/> 的内容；格式不对的行跳过。</summary>
    public static AppUsage Parse(string text)
    {
        var usage = new AppUsage();
        foreach (string line in text.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t', 3);
            if (parts.Length != 3 || parts[2].Length == 0) continue;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0) continue;
            if (!DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)) continue;
            usage._items[parts[2]] = (count, last.ToUniversalTime());
        }
        return usage;
    }
}
