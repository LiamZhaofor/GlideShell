using GlideShell.Core.Configuration;

namespace GlideShell.Tests.Configuration;

public class HotkeyTests
{
    [Theory]
    [InlineData("Alt+Space", HotkeyModifiers.Alt, 0x20u)]
    [InlineData("ctrl + shift + k", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x4Bu)]
    [InlineData("Win+F2", HotkeyModifiers.Win, 0x71u)]
    [InlineData("Control+Alt+Num5", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x65u)]
    [InlineData("Ctrl+;", HotkeyModifiers.Ctrl, 0xBAu)]
    [InlineData("Alt+0xE8", HotkeyModifiers.Alt, 0xE8u)]
    [InlineData("F12", HotkeyModifiers.None, 0x7Bu)]
    public void ParsesModifiersAndKey(string text, HotkeyModifiers mods, uint key)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(new Hotkey(mods, key), h);
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+A+B")]
    [InlineData("A+Ctrl")]
    [InlineData("Alt+Nope")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Alt+F25")]
    public void RejectsMalformed(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyMeansNone(string? text)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.True(h.IsNone);
        Assert.Equal("", h.ToString());
    }

    [Theory]
    [InlineData("shift+win+alt+ctrl+delete", "Win+Ctrl+Alt+Shift+Delete")]
    [InlineData("alt+space", "Alt+Space")]
    [InlineData("ctrl+escape", "Ctrl+Esc")]
    [InlineData("Win+num7", "Win+Num7")]
    public void FormatsInCanonicalOrder(string text, string expected)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(expected, h.ToString());
        Assert.True(Hotkey.TryParse(h.ToString(), out var again));
        Assert.Equal(h, again);
    }

    [Theory]
    [InlineData("Alt+Space", true)]
    [InlineData("Win+K", true)]
    [InlineData("F8", true)]
    [InlineData("Shift+F8", true)]
    [InlineData("Space", false)] // 不带修饰键：挡住打字
    [InlineData("Shift+A", false)] // 只带 Shift：就是大写 A
    public void ValidityNeedsARealModifier(string text, bool valid)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(valid, h.IsValid);
    }

    [Fact]
    public void ModifierKeysAreNotKeys()
    {
        Assert.False(new Hotkey(HotkeyModifiers.Ctrl, 0xA4).IsValid); // Ctrl+左 Alt
        Assert.False(new Hotkey(HotkeyModifiers.Alt, 0x5B).IsValid); // Alt+左 Win
    }

    [Fact]
    public void ConfigFallsBackToDefaultForUnusableHotkey()
    {
        var bad = new GlideShellConfig { Overview = new OverviewConfig { SearchHotkey = "Shift+Q" } };
        var (fixedConfig, problems) = bad.Sanitize();
        Assert.Equal(OverviewConfig.DefaultSearchHotkey, fixedConfig.Overview.SearchHotkey);
        Assert.Single(problems, p => p.Contains("SearchHotkey"));

        var off = new GlideShellConfig { Overview = new OverviewConfig { SearchHotkey = "" } }.Sanitize();
        Assert.Equal("", off.Config.Overview.SearchHotkey);
        Assert.Empty(off.Problems);

        var normalized = new GlideShellConfig { Overview = new OverviewConfig { SearchHotkey = "alt + ctrl + space" } }.Sanitize();
        Assert.Equal("Ctrl+Alt+Space", normalized.Config.Overview.SearchHotkey);
    }

    [Fact]
    public void ExtraSearchAppsAreTrimmedAndDeduplicated()
    {
        var config = new GlideShellConfig
        {
            Overview = new OverviewConfig { SearchExtraApps = [@" C:\Tools\a.exe ", @"c:\tools\A.EXE", "", "   ", @"D:\b.lnk"] },
        }.Sanitize().Config;
        Assert.Equal([@"C:\Tools\a.exe", @"D:\b.lnk"], config.Overview.SearchExtraApps);
    }
}
