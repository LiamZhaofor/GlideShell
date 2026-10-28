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
          "assets": [ { "name": "GlideShell-1.6.1-win-x64.zip" } ]
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
}
