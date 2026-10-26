using GlideShell.Core.Overview;

namespace GlideShell.Tests.Overview;

public class WindowShadowTests
{
    [Fact]
    public void Erf_matches_known_values()
    {
        Assert.Equal(0, WindowShadow.Erf(0), 6);
        Assert.Equal(0.8427007929, WindowShadow.Erf(1), 6);
        Assert.Equal(-0.9953222650, WindowShadow.Erf(-2), 6);
    }

    // 真机对照实测（PtpProbe thumbnails --shadow，150%，普通 Win32 窗口可见边界 582×391；与窗口大小无关）：
    // 到窗口可见边的距离（物理像素）→ 白底上的变暗比例
    [Theory]
    [InlineData("下", 1, 0.306)]
    [InlineData("下", 8, 0.278)]
    [InlineData("下", 25, 0.212)]
    [InlineData("下", 50, 0.118)]
    [InlineData("下", 95, 0.016)]
    [InlineData("右", 1, 0.165)]
    [InlineData("右", 8, 0.129)]
    [InlineData("右", 25, 0.059)]
    [InlineData("右", 50, 0.008)]
    [InlineData("上", 1, 0.110)]
    [InlineData("上", 8, 0.075)]
    [InlineData("上", 25, 0.020)]
    public void Active_shadow_matches_the_measured_profile(string side, double d, double measured) =>
        AssertProfile(ShadowStyle.Active, side, d, measured);

    [Theory]
    [InlineData("下", 1, 0.220)]
    [InlineData("下", 8, 0.184)]
    [InlineData("下", 35, 0.110)]
    [InlineData("下", 70, 0.043)]
    [InlineData("右", 1, 0.118)]
    [InlineData("右", 8, 0.075)]
    [InlineData("右", 25, 0.020)]
    [InlineData("上", 1, 0.067)]
    [InlineData("上", 8, 0.031)]
    public void Inactive_shadow_matches_the_measured_profile(string side, double d, double measured) =>
        AssertProfile(ShadowStyle.Inactive, side, d, measured);

    // 125% 下的同一组实测（可见边界 584×392）：系统阴影随 DPI 等比缩放，同一套逻辑像素参数照样吻合
    [Theory]
    [InlineData("下", 1, 0.306, true)]
    [InlineData("下", 25, 0.192, true)]
    [InlineData("下", 50, 0.086, true)]
    [InlineData("右", 1, 0.165, true)]
    [InlineData("右", 12, 0.098, true)]
    [InlineData("上", 8, 0.067, true)]
    [InlineData("下", 1, 0.216, false)]
    [InlineData("下", 35, 0.094, false)]
    [InlineData("右", 1, 0.114, false)]
    [InlineData("上", 4, 0.043, false)]
    public void Shadows_scale_with_dpi_at_125_percent(string side, double d, double measured, bool active) =>
        AssertProfile(active ? ShadowStyle.Active : ShadowStyle.Inactive, side, d, measured, 584, 392, 1.25);

    private static void AssertProfile(ShadowStyle style, string side, double d, double measured, double W = 582, double H = 391, double dpi = 1.5)
    {
        // 可见边界最外一圈像素算第 0 个，d=1 是紧贴着的第一个窗口外像素（像素中心）
        var (x, y) = side switch { "右" => (W - 0.5 + d, H / 2), "下" => (W / 2, H - 0.5 + d), _ => (W / 2, 0.5 - d) };
        Assert.InRange(WindowShadow.Coverage(style, x, y, W, H, dpi), measured - 0.025, measured + 0.025);
    }

    [Fact]
    public void Image_matches_the_model_and_is_not_hollowed_under_the_window()
    {
        var img = WindowShadow.Render(ShadowStyle.Active, 400, 300, 1.5, downscale: 4);

        Assert.True(img.Bgra.Length >= img.Width * img.Height * 4); // 池里借的缓冲可能更长
        byte Alpha(double xPx, double yPx)
        {
            int i = (int)((xPx + img.MarginPx) / 4), j = (int)((yPx + img.MarginPx) / 4);
            return img.Bgra[(j * img.Width + i) * 4 + 3];
        }
        // 窗口边内外不能有断层（挖空会在边外留浅边，撤层时闪一下）
        Assert.True(Math.Abs(Alpha(200, 296) - Alpha(200, 304)) < 30);
        Assert.True(Alpha(200, 150) > 0);
        Assert.InRange(Alpha(200, 320) / 255.0, WindowShadow.Coverage(ShadowStyle.Active, 202, 322, 400, 300, 1.5) - 0.03,
            WindowShadow.Coverage(ShadowStyle.Active, 202, 322, 400, 300, 1.5) + 0.03);
        Assert.True(Alpha(200, 320) > Alpha(200, -20)); // 下方比上方深
        Assert.Equal(0, img.Bgra[0] | img.Bgra[1] | img.Bgra[2]); // 黑色
        img.Return();
    }

    [Fact]
    public void Active_weight_hands_over_from_the_old_foreground_to_the_chosen_window()
    {
        // 打开 / 没有选中目标：原前台始终活动
        Assert.Equal(1, WindowShadow.ActiveWeight(isForeground: true, isFocusTarget: false, hasFocusTarget: false, 0.4));
        Assert.Equal(0, WindowShadow.ActiveWeight(false, false, false, 0.4));
        // 收起到别的窗口：p=1 时还是原前台活动，p=0 时完全交给选中的窗口
        Assert.Equal(1, WindowShadow.ActiveWeight(true, false, true, 1));
        Assert.Equal(0, WindowShadow.ActiveWeight(true, false, true, 0));
        Assert.Equal(0, WindowShadow.ActiveWeight(false, true, true, 1));
        Assert.Equal(1, WindowShadow.ActiveWeight(false, true, true, 0));
        // 选中的就是原前台
        Assert.Equal(1, WindowShadow.ActiveWeight(true, true, true, 0.5));
    }
}
