using System.Text.RegularExpressions;
using GlideShell.Core.Localization;

namespace GlideShell.Tests.Localization;

/// <summary>扫源码里 T("…") / F("…") / N("…") 的中文原文，与 <see cref="English"/> 逐条对照。</summary>
[Collection(nameof(LocalizationTests))] // 会改全局的界面语言：不与其他测试并行
public partial class LocalizationTests
{
    [GeneratedRegex("""\b[TFN]\("((?:[^"\\]|\\.)*)"[,)]""")]
    private static partial Regex Call();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "GlideShell.slnx"))) return Path.Combine(dir.FullName, "src");
        throw new DirectoryNotFoundException("找不到仓库根目录（GlideShell.slnx）");
    }

    /// <summary>源码里所有要翻译的原文 → 第一次出现的位置。</summary>
    private static Dictionary<string, string> SourceKeys()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match m in Call().Matches(lines[i]))
                    keys.TryAdd(Regex.Unescape(m.Groups[1].Value), $"{Path.GetFileName(file)}:{i + 1}");
            }
        }
        return keys;
    }

    private static HashSet<string> Placeholders(string s)
        => Placeholder().Matches(s).Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void Every_source_string_has_an_english_translation()
    {
        var missing = SourceKeys().Where(k => !English.Strings.ContainsKey(k.Key)).Select(k => $"{k.Value}  {k.Key}").ToList();
        Assert.True(missing.Count == 0, "没有英文译文：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_translation_is_used()
    {
        var keys = SourceKeys();
        var unused = English.Strings.Keys.Where(k => !keys.ContainsKey(k)).ToList();
        Assert.True(unused.Count == 0, "译文表里有源码没用到的条目：\n" + string.Join("\n", unused));
    }

    [Fact]
    public void Translations_keep_the_placeholders()
    {
        var bad = English.Strings
            .Where(kv => !Placeholders(kv.Key).SetEquals(Placeholders(kv.Value)))
            .Select(kv => $"{kv.Key}  →  {kv.Value}")
            .ToList();
        Assert.True(bad.Count == 0, "占位符不一致：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Translations_contain_no_chinese()
    {
        var bad = English.Strings.Where(kv => kv.Value.Any(c => c is >= '\u4e00' and <= '\u9fff')).Select(kv => kv.Value).ToList();
        Assert.True(bad.Count == 0, "译文里还有中文：\n" + string.Join("\n", bad));
    }

    [Theory]
    [InlineData("auto", "zh-CN", UiLanguage.Chinese)]
    [InlineData("auto", "zh-TW", UiLanguage.Chinese)]
    [InlineData("auto", "en-US", UiLanguage.English)]
    [InlineData("auto", "ja-JP", UiLanguage.English)]
    [InlineData("en", "zh-CN", UiLanguage.English)]
    [InlineData("zh-CN", "en-US", UiLanguage.Chinese)]
    [InlineData(null, "de-DE", UiLanguage.English)]
    public void Language_resolves_from_setting_and_system(string? setting, string system, UiLanguage expected)
        => Assert.Equal(expected, L.Resolve(setting, system));

    [Fact]
    public void Untranslated_text_falls_back_to_chinese()
    {
        var before = L.Language;
        try
        {
            L.Language = UiLanguage.English;
            Assert.Equal("这句没有译文", L.T("这句没有译文"));
            Assert.Equal("Home", L.T("首页"));
            L.Language = UiLanguage.Chinese;
            Assert.Equal("首页", L.T("首页"));
        }
        finally
        {
            L.Language = before;
        }
    }
}

[CollectionDefinition(nameof(LocalizationTests), DisableParallelization = true)]
public sealed class LocalizationCollection;
