namespace GlideShell.Core.Search;

/// <summary>
/// 一个名字拆成可匹配的单元：每个汉字一个（可以用它的任一读音、读音的开头几个字母、或这个字本身匹配），
/// 每个字母 / 数字一个；空格、标点只用来分词。词首：名字开头、空格标点之后、汉字、小写到大写（PowerToys 的 T）、
/// 连续大写里最后一个接小写的（ZCode 的 C、PDFReader 的 R）、字母数字交界。
/// </summary>
public sealed class SearchName
{
    internal readonly record struct Unit(int Index, string[] Spellings, bool WordStart, bool Han);

    private SearchName(string text, Unit[] units)
    {
        Text = text;
        Units = units;
    }

    public string Text { get; }

    internal Unit[] Units { get; }

    public static SearchName Of(string text)
    {
        var units = new List<Unit>(text.Length);
        char prev = ' ';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : ' ';
            var readings = Pinyin.Readings(c);
            if (readings.Count > 0)
            {
                units.Add(new Unit(i, [.. readings, c.ToString()], true, true));
            }
            else if (char.IsLetterOrDigit(c))
            {
                char lower = char.ToLowerInvariant(Fold(c));
                bool start = units.Count == 0 || !char.IsLetterOrDigit(prev) || Pinyin.IsHan(prev) ||
                             char.IsLower(prev) && char.IsUpper(c) || char.IsUpper(prev) && char.IsUpper(c) && char.IsLower(next) ||
                             char.IsDigit(prev) != char.IsDigit(c);
                units.Add(new Unit(i, [lower.ToString()], start, false));
            }
            prev = c;
        }
        return new SearchName(text, [.. units]);
    }

    /// <summary>全角字母数字折成半角。</summary>
    internal static char Fold(char c) => c is >= '！' and <= '～' ? (char)(c - 0xFEE0) : c;
}

/// <summary>一次匹配：<see cref="Cost"/> 越小越好（0 = 和名字完全一样），<see cref="Highlights"/> 是名字里匹配上的字的下标（升序）。</summary>
public readonly record struct SearchMatch(int Cost, int[] Highlights, bool Exact);

/// <summary>
/// 打字找程序的匹配：全拼、首字母、混拼（weix、wxin）、多音字、英文词首字母（vsc → Visual Studio Code）、子串、跳字都能匹配。
/// 查询（小写字母、数字、汉字）按顺序被名字里的单元一段段吃掉：汉字吃掉它某个读音的开头若干个字母（或这个字本身），
/// 字母数字吃掉一个同样的字符；单元可以跳过。按「怎么吃的」计代价，取代价最小的吃法：
/// <list type="bullet">
/// <item>从名字开头起 0；从中间某个词的开头起 3；从词中间起 4（连续的子串 code → ZCode 仍比跳着匹配的词首好）。</item>
/// <item>汉字只吃了读音的前几个字母（首字母、混拼）+1；整个读音或字本身 0。</item>
/// <item>中间跳过了单元、下一个吃的是词首（vsc、首字母跳字）+2；不是词首 +6。</item>
/// </list>
/// 于是：完全相同 0 &lt; 前缀（weix 1）&lt; 首字母（wx 2）&lt; 英文词首（vsc 4）&lt; 子串 &lt; 跳字。
/// </summary>
public static class SearchMatcher
{
    public const int StartWord = 3, StartMid = 4, Partial = 1, JumpToWord = 2, JumpMid = 6;
    private const int Inf = int.MaxValue / 4;

    /// <summary>查询规范化：小写、全角折半角，只留字母、数字、汉字（空格标点去掉）。</summary>
    public static string Normalize(string query)
    {
        var chars = new List<char>(query.Length);
        foreach (char raw in query)
        {
            char c = SearchName.Fold(raw);
            if (char.IsLetterOrDigit(c)) chars.Add(char.ToLowerInvariant(c));
        }
        return new string([.. chars]);
    }

    /// <summary><paramref name="query"/>（已 <see cref="Normalize"/>）匹配 <paramref name="name"/>；匹配不上返回 null。</summary>
    public static SearchMatch? Match(string query, SearchName name)
    {
        var units = name.Units;
        int n = units.Length, m = query.Length;
        if (m == 0 || n == 0) return null;
        // memo[i, j, state]：从单元 i、查询位置 j 起把剩下的查询吃完的最小代价。state：0 = 还没开始，1 = 紧接着上一个吃掉的单元，2 = 中间跳过了
        var memo = new int[(n + 1) * (m + 1) * 3];
        Array.Fill(memo, -1);
        int best = Solve(0, 0, 0);
        if (best >= Inf) return null;

        // 按最优解走一遍，记下吃掉的单元
        var highlights = new List<int>();
        int i = 0, j = 0, state = 0;
        bool all = true;
        while (j < m)
        {
            int target = Solve(i, j, state);
            // 代价相同时先吃（高亮靠前的字）
            if (TakeCost(i, j, state, out int len) != target)
            {
                all = false;
                state = state == 0 ? 0 : 2;
                i++;
                continue;
            }
            highlights.Add(units[i].Index);
            j += len;
            state = 1;
            i++;
        }
        if (i < n) all = false;
        return new SearchMatch(best, [.. highlights], best == 0 && all);

        int Solve(int i, int j, int state)
        {
            if (j == m) return 0;
            if (i == n) return Inf;
            int key = ((i * (m + 1)) + j) * 3 + state;
            if (memo[key] >= 0) return memo[key];
            int result = Math.Min(Skip(i, j, state), TakeCost(i, j, state, out _));
            memo[key] = result;
            return result;
        }

        int Skip(int i, int j, int state) => i >= n ? Inf : Solve(i + 1, j, state == 0 ? 0 : 2);

        int EnterCost(int i, int state)
        {
            var u = units[i];
            return state switch
            {
                0 => i == 0 ? 0 : u.WordStart ? StartWord : StartMid,
                2 => u.WordStart ? JumpToWord : JumpMid,
                _ => 0,
            };
        }

        // 在单元 i 吃掉查询的若干个字符（试每个读音的每个可能长度），返回最小总代价
        int TakeCost(int i, int j, int state, out int bestLen)
        {
            bestLen = 0;
            int result = Inf;
            int enter = EnterCost(i, state);
            foreach (var spelling in units[i].Spellings)
            {
                int max = Math.Min(spelling.Length, m - j);
                for (int len = 1; len <= max; len++)
                {
                    if (query[j + len - 1] != spelling[len - 1]) break;
                    int rest = Solve(i + 1, j + len, 1);
                    if (rest >= Inf) continue;
                    int cost = enter + (len < spelling.Length ? Partial : 0) + rest;
                    if (cost < result)
                    {
                        result = cost;
                        bestLen = len;
                    }
                }
            }
            return result;
        }

    }
}
