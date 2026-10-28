using GlideShell.Core.Updates;

namespace GlideShell.Tests.Updates;

public class ReleaseInfoTests
{
    [Theory]
    [InlineData("v1.6.1", 1, 6, 1)]
    [InlineData("1.6.1", 1, 6, 1)]
    [InlineData("V2.0", 2, 0, 0)]
    [InlineData("1.6.0.0", 1, 6, 0)]
    [InlineData("1.6.0+b07318713a2b", 1, 6, 0)]
    public void Versions_are_read_from_tags_and_build_strings(string text, int major, int minor, int build)
    {
        Assert.True(ReleaseInfo.TryParseVersion(text, out var v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("1.7.0-beta")]
    [InlineData("1.x.0")]
    public void Non_release_tags_are_rejected(string text) => Assert.False(ReleaseInfo.TryParseVersion(text, out _));

    private const string Latest = """
        {
          "url": "https://api.github.com/repos/LiamZhaofor/GlideShell/releases/1",
          "html_url": "https://github.com/LiamZhaofor/GlideShell/releases/tag/v1.6.1",
          "tag_name": "v1.6.1",
          "name": "GlideShell 1.6.1",
          "draft": false,
          "prerelease": false,
          "assets": [ {
            "name": "GlideShell-1.6.1-win-x64.zip", "size": 2639488,
            "digest": "sha256:CE1F2585A3CCF71CFC919EEA38B2E642153ADD714D7AB26E82014FAFE27F3A4B",
            "browser_download_url": "https://github.com/LiamZhaofor/GlideShell/releases/download/v1.6.1/GlideShell-1.6.1-win-x64.zip"
          } ]
        }
        """;

    [Fact]
    public void Latest_release_response_is_parsed()
    {
        var r = ReleaseInfo.Parse(Latest);
        Assert.NotNull(r);
        Assert.Equal(new Version(1, 6, 1), r.Version);
        Assert.Equal("v1.6.1", r.Tag);
        Assert.Equal("https://github.com/LiamZhaofor/GlideShell/releases/tag/v1.6.1", r.Url);
    }

    [Fact]
    public void Prereleases_drafts_and_garbage_are_ignored()
    {
        Assert.Null(ReleaseInfo.Parse(Latest.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.Null(ReleaseInfo.Parse(Latest.Replace("\"draft\": false", "\"draft\": true")));
        Assert.Null(ReleaseInfo.Parse("{\"message\": \"API rate limit exceeded\"}"));
        Assert.Null(ReleaseInfo.Parse("<html>"));
        Assert.Null(ReleaseInfo.Parse("[]"));
    }

    [Fact]
    public void Only_https_links_are_kept()
        => Assert.Equal("", ReleaseInfo.Parse(Latest.Replace("https://github.com/LiamZhaofor", "javascript:alert(1)//"))!.Url);

    [Theory]
    [InlineData(1, 6, 0, true)]
    [InlineData(1, 6, 1, false)]
    [InlineData(1, 7, 0, false)]
    public void Newer_is_decided_on_major_minor_patch(int major, int minor, int build, bool newer)
    {
        var r = ReleaseInfo.Parse(Latest)!;
        Assert.Equal(newer, r.IsNewerThan(new Version(major, minor, build, 0)));
    }

    [Fact]
    public void The_release_package_is_picked_with_its_sha256()
    {
        var pkg = ReleaseInfo.Parse(Latest)!.Package;
        Assert.NotNull(pkg);
        Assert.Equal(2639488, pkg.Size);
        Assert.Equal("ce1f2585a3ccf71cfc919eea38b2e642153add714d7ab26e82014fafe27f3a4b", pkg.Sha256); // 统一成小写
    }

    [Theory]
    [InlineData("https://github.com/LiamZhaofor/GlideShell/releases/download/v1.6.1/", "https://example.com/")] // 别处的地址
    [InlineData("download/v1.6.1/", "download/v1.6.0/")] // 挂在别的版本下
    [InlineData("sha256:CE1F", "sha256:XX1F")] // 摘要不是十六进制
    [InlineData("sha256:", "md5:")] // 不是 SHA-256
    [InlineData("\"GlideShell-1.6.1-win-x64.zip\"", "\"GlideShell-1.6.1-win-arm64.zip\"")] // 名字对不上
    public void Packages_that_cannot_be_verified_are_not_used_for_one_click_upgrade(string from, string to)
        => Assert.Null(ReleaseInfo.Parse(Latest.Replace(from, to))!.Package);

    private const string Local = @"C:\Users\a\AppData\Local", ProgramFiles = @"C:\Program Files";

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\GlideShell", InstallKind.Standard)]
    [InlineData(@"c:\users\A\appdata\local\programs\glideshell\", InstallKind.Standard)]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\WinGet\Packages\LiamZhaofor.GlideShell_Microsoft.Winget.Source_8wekyb3d8bbwe", InstallKind.WinGet)]
    [InlineData(@"C:\Program Files\GlideShell", InstallKind.ProgramFiles)]
    [InlineData(@"D:\Tools\GlideShell", InstallKind.Elsewhere)]
    [InlineData(@"C:\Program Files (x86)\GlideShell", InstallKind.Elsewhere)] // 不是 Program Files 本身
    public void Install_location_decides_how_to_upgrade(string dir, InstallKind kind)
    {
        Assert.Equal(kind, InstallLocation.Classify(dir, Local, ProgramFiles));
        Assert.Equal(kind is InstallKind.Standard or InstallKind.Elsewhere, InstallLocation.CanUpgradeInPlace(kind));
    }
}
