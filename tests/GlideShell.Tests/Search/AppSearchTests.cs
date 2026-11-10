using GlideShell.Core.Search;

namespace GlideShell.Tests.Search;

public class AppSearchTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private static readonly SearchEntry[] Apps =
    [
        new("wechat", "微信"),
        new("wecom", "企业微信"),
        new("wps", "WPS Office"),
        new("vscode", "Visual Studio Code"),
        new("vs", "Visual Studio 2022"),
        new("netease", "网易云音乐"),
        new("qq", "QQ"),
        new("qqmusic", "QQ音乐"),
        new("powertoys", "PowerToys"),
        new("explorer", "文件资源管理器"),
        new("edge", "Microsoft Edge"),
        new("bank", "银行助手"),
        new("chrome", "Google Chrome"),
        new("terminal", "终端"),
        new("calc", "计算器"),
    ];

    private static string Top(string query, AppUsage? usage = null) =>
        AppSearch.Find(query, Apps, usage ?? new AppUsage(), Now).FirstOrDefault()?.Entry.Name ?? "";

    private static List<string> Names(string query, AppUsage? usage = null) =>
        AppSearch.Find(query, Apps, usage ?? new AppUsage(), Now).Select(r => r.Entry.Name).ToList();

    [Fact]
    public void Pinyin_table_has_common_and_polyphonic_readings()
    {
        Assert.Equal(["wei"], Pinyin.Readings('微'));
        Assert.Contains("yue", Pinyin.Readings('乐'));
        Assert.Contains("le", Pinyin.Readings('乐'));
        Assert.Equal("le", Pinyin.Readings('乐')[0]); // 最常用的排第一
        Assert.Contains("hang", Pinyin.Readings('行'));
        Assert.Equal(["lv"], Pinyin.Readings('绿').Where(r => r == "lv")); // ü 写作 v
        Assert.Empty(Pinyin.Readings('A'));
    }

    [Theory]
    [InlineData("weixin")]
    [InlineData("weix")]
    [InlineData("wxin")]
    [InlineData("wx")]
    [InlineData("微信")]
    [InlineData("WEIXIN")]
    public void Wechat_is_found_by_full_partial_and_initial_pinyin(string query) => Assert.Equal("微信", Top(query));

    [Fact]
    public void English_word_initials_match()
    {
        Assert.Equal("Visual Studio Code", Top("vsc"));
        Assert.Equal("Visual Studio Code", Top("code"));
        Assert.Equal("PowerToys", Top("pt"));
        Assert.Equal("Google Chrome", Top("chrome"));
    }

    [Fact]
    public void Polyphonic_characters_match_either_reading()
    {
        Assert.Equal("网易云音乐", Top("wyyyy"));
        Assert.Equal("网易云音乐", Top("yunyinyue"));
        Assert.Equal(["QQ音乐", "网易云音乐"], Names("yinyue")); // 都从词首匹配：名字短的在前
        Assert.Equal("银行助手", Top("yinhang"));
        Assert.Equal("QQ音乐", Top("qqyy"));
    }

    [Fact]
    public void Exact_beats_prefix_beats_initials_beats_substring()
    {
        Assert.Equal("QQ", Top("qq"));
        Assert.Equal(["QQ", "QQ音乐"], Names("qq"));
        // 「微信」从头匹配，「企业微信」从中间的字匹配
        var wx = Names("weixin");
        Assert.True(wx.IndexOf("微信") < wx.IndexOf("企业微信"));
        Assert.Contains("文件资源管理器", Names("glq")); // 中间的字首字母（子串）也能找到
    }

    [Fact]
    public void Matched_characters_are_reported_for_highlighting()
    {
        var r = AppSearch.Find("vsc", Apps, new AppUsage(), Now)[0];
        Assert.Equal([0, 7, 14], r.Highlights); // V、S、C
        var w = AppSearch.Find("wx", Apps, new AppUsage(), Now)[0];
        Assert.Equal([0, 1], w.Highlights);
    }

    [Fact]
    public void Nonsense_does_not_match_and_empty_query_returns_nothing()
    {
        Assert.Empty(Names("zzzz"));
        Assert.Empty(Names(""));
        Assert.Empty(Names("  "));
    }

    [Fact]
    public void Frequently_opened_apps_rank_higher()
    {
        // 不加权时 Visual Studio 2022 和 Visual Studio Code 对 vs 一样好（一样长，按字母序）
        Assert.Equal("Visual Studio 2022", Top("vs"));
        var usage = new AppUsage();
        for (int i = 0; i < 5; i++) usage.Record("vscode", Now.AddHours(-1));
        Assert.Equal("Visual Studio Code", Top("vs", usage));
        // 但压不过完全相同的名字
        var qq = new AppUsage();
        for (int i = 0; i < 20; i++) qq.Record("qqmusic", Now);
        Assert.Equal("QQ", Top("qq", qq));
    }

    [Fact]
    public void Usage_round_trips_and_skips_bad_lines()
    {
        var usage = new AppUsage();
        usage.Record("a", Now);
        usage.Record("a", Now);
        usage.Record(@"shell\b c", Now.AddDays(-5));
        var back = AppUsage.Parse(usage.Serialize() + "garbage\n\t\t\nx\tnot-a-date\tk\n");
        Assert.Equal(2, back.Count);
        Assert.Equal(2, back.TimesOpened("a"));
        Assert.Equal(1, back.TimesOpened(@"shell\b c"));
        Assert.True(back.Bonus("a", Now) > back.Bonus(@"shell\b c", Now));
    }

    [Theory]
    [InlineData("卸载 微信", null, false)]
    [InlineData("Uninstall Foo", null, false)]
    [InlineData("Foo Website", null, false)]
    [InlineData("Foo 帮助", null, false)]
    [InlineData("Readme", @"C:\foo\readme.txt", false)]
    [InlineData("Foo", @"C:\foo\foo.chm", false)]
    [InlineData("Foo Home", "https://foo.example", false)]
    [InlineData("微信", @"C:\Program Files\Tencent\Weixin\Weixin.exe", true)]
    [InlineData("计算器", null, true)]
    public void Start_menu_noise_is_filtered(string name, string? target, bool expected) => Assert.Equal(expected, AppSearch.IsProgram(name, target));

    [Fact]
    public void FrequentListsOpenedAppsMostRecentFirst()
    {
        var usage = new AppUsage();
        Assert.Empty(AppSearch.Frequent(Apps, usage));
        for (int i = 0; i < 6; i++) usage.Record("qq", Now.AddDays(-10));
        usage.Record("wps", Now.AddDays(-20));
        usage.Record("wechat", Now.AddHours(-2));
        var names = AppSearch.Frequent(Apps, usage).Select(r => r.Entry.Name).ToArray();
        Assert.Equal(["微信", "QQ", "WPS Office"], names); // 按最近一次打开：次数多的 QQ 也排在刚打开的微信后面
        Assert.All(AppSearch.Frequent(Apps, usage), r => Assert.Empty(r.Highlights));
    }
}
