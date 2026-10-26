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

    [Theory]
    // 150% 缩放下真机实测的活动窗口阴影（物理像素，到窗口可见边的距离 → 变暗比例）
    [InlineData("右", 9, 0.294)]
    [InlineData("右", 25, 0.137)]
    [InlineData("右", 49, 0.024)]
    [InlineData("下", 9, 0.545)]
    [InlineData("下", 41, 0.307)]
    [InlineData("下", 81, 0.078)]
    [InlineData("上", 9, 0.169)]
    [InlineData("上", 25, 0.059)]
    public void Active_shadow_matches_the_measured_profile(string side, double d, double measured)
    {
        double W = 1729, H = 922;
        var (x, y) = side switch { "右" => (W + d, H / 2), "下" => (W / 2, H + d), _ => (W / 2, -d) };
        Assert.InRange(WindowShadow.Coverage(ShadowStyle.Active, x, y, W, H, 1.5), measured - 0.015, measured + 0.015);
    }

    [Theory]
    // 非活动窗口（400×300 测试窗口，150%）
    [InlineData("右", 9, 0.063)]
    [InlineData("下", 9, 0.176)]
    [InlineData("下", 49, 0.078)]
    [InlineData("上", 9, 0.024)]
    public void Inactive_shadow_matches_the_measured_profile(string side, double d, double measured)
    {
        double W = 382, H = 291;
        var (x, y) = side switch { "右" => (W + d, H / 2), "下" => (W / 2, H + d), _ => (W / 2, -d) };
        Assert.InRange(WindowShadow.Coverage(ShadowStyle.Inactive, x, y, W, H, 1.5), measured - 0.015, measured + 0.015);
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
