using GlideShell.Platform.Diagnostics;

namespace GlideShell.Tests.Platform;

public class DiagnosticReportTests
{
    [Fact]
    public void Report_lists_sections_and_checks_and_warns_before_sharing()
    {
        var text = new DiagnosticReport("1.7.0")
            .Section("系统", ["Windows 11 专业版 24H2（内部版本 26100.1）"])
            .Checks([new CompatCheck("触摸板", CheckLevel.Ok, "VID 2808 / PID 0218"), new CompatCheck("共享缩略图", CheckLevel.Warning, "改用经典缩略图")])
            .Section("最近一次异常退出", [])
            .ToString();

        Assert.StartsWith("GlideShell 诊断报告 · 版本 1.7.0", text);
        Assert.Contains("不会上传", text); // 文件头提醒：只在本机、贴出去前看一眼
        Assert.Contains("## 系统\r\nWindows 11", text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        Assert.Contains("[正常] 触摸板：VID 2808 / PID 0218", text);
        Assert.Contains("[注意] 共享缩略图：改用经典缩略图", text);
        Assert.Contains("## 最近一次异常退出", text);
        Assert.Contains("（无）", text); // 空节也写出来，免得以为漏了
    }

    [Theory]
    [InlineData(CheckLevel.Ok, "正常")]
    [InlineData(CheckLevel.Warning, "注意")]
    [InlineData(CheckLevel.Unavailable, "不可用")]
    [InlineData(CheckLevel.Info, "说明")]
    public void Check_levels_read_in_plain_words(CheckLevel level, string text)
        => Assert.Equal(text, new CompatCheck("x", level, "").LevelText);
}
