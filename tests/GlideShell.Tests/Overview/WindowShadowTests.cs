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

    [Fact]
    public void Shadow_reproduces_the_measured_profile()
    {
        // 150% 缩放下实测（物理像素）：活动窗口右侧 11px 处约 0.275、底部 11px 处约 0.53、顶部比底部浅得多
        var s = ShadowStyle.Active;
        double W = 1729, H = 922, dpi = 1.5;
        double At(double x, double y) => s.Opacity
            * WindowShadow.Interval(x, 0, W, s.SigmaX * dpi) * WindowShadow.Interval(y, s.OffsetY * dpi, H + s.OffsetY * dpi, s.SigmaY * dpi);
        Assert.InRange(At(W + 11, H / 2), 0.22, 0.30);
        Assert.InRange(At(W / 2, H + 11), 0.45, 0.56);
        Assert.InRange(At(W / 2, -11), 0.05, 0.16);
        Assert.True(At(W + 150, H / 2) < 0.005);
    }

    [Fact]
    public void Image_leaves_the_window_area_clear_and_covers_the_margin()
    {
        var img = WindowShadow.Render(ShadowStyle.Inactive, 400, 300, 1.5, downscale: 4);

        Assert.Equal(img.Width * img.Height * 4, img.Bgra.Length);
        byte Alpha(double xPx, double yPx)
        {
            int i = (int)((xPx + img.MarginPx) / 4), j = (int)((yPx + img.MarginPx) / 4);
            return img.Bgra[(j * img.Width + i) * 4 + 3];
        }
        Assert.Equal(0, Alpha(200, 150)); // 窗口中间：留空
        Assert.True(Alpha(200, 310) > Alpha(200, -10)); // 下方比上方深（向下偏移）
        Assert.True(Alpha(200, 310) > 20);
        Assert.Equal(0, img.Bgra[0] | img.Bgra[1] | img.Bgra[2]); // 黑色
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
