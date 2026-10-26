namespace GlideShell.Core.Overview;

/// <summary>
/// Win11 窗口阴影的近似（真机实测拟合，2026-10-26，150% 缩放下测得后换算成 96 DPI 逻辑像素）：
/// 一个向下偏移的模糊矩形，横竖模糊半径不同。缩略图不含阴影，总览自己按这个参数画，
/// 撤层那一刻缩略图与真实窗口才一致（否则阴影会一下子压到下面的窗口上）。
/// </summary>
/// <param name="Opacity">阴影最深处的不透明度。</param>
/// <param name="SigmaX">横向高斯模糊的标准差（逻辑像素）。</param>
/// <param name="SigmaY">纵向高斯模糊的标准差（逻辑像素）。</param>
/// <param name="OffsetY">整体向下偏移（逻辑像素）。</param>
public readonly record struct ShadowStyle(double Opacity, double SigmaX, double SigmaY, double OffsetY)
{
    /// <summary>前台（活动）窗口：A=0.68、σx=29、σy=43、下移 34（物理像素，150%）。</summary>
    public static ShadowStyle Active { get; } = new(0.68, 19.5, 28.5, 22.8);

    /// <summary>非活动窗口：A=0.23、σx=18、σy=36、下移 34（物理像素，150%）。</summary>
    public static ShadowStyle Inactive { get; } = new(0.23, 12.0, 24.0, 22.7);

    /// <summary>阴影图在窗口四周各留的边距（逻辑像素）：偏移 + 3σ 之外已经看不出来。</summary>
    public double Margin => OffsetY + 3 * Math.Max(SigmaX, SigmaY);
}

/// <summary>阴影图：黑色、预乘 alpha 的 BGRA，按 <paramref name="Downscale"/> 缩小存储（阴影本身很虚，放大回去看不出差别）。</summary>
/// <param name="MarginPx">图的左上角相对窗口左上角向外的距离（物理像素，原尺寸）。</param>
public sealed record ShadowImage(int Width, int Height, int Downscale, double MarginPx, byte[] Bgra);

public static class WindowShadow
{
    /// <summary>
    /// 画出某个大小窗口的阴影图（物理像素尺寸 <paramref name="widthPx"/>×<paramref name="heightPx"/>，DPI 缩放 <paramref name="dpiScale"/>）。
    /// 窗口本身覆盖的区域留空：缩略图盖在上面，Win11 圆角窗口的四个角露出的是后面的内容，不是阴影。
    /// </summary>
    public static ShadowImage Render(ShadowStyle style, double widthPx, double heightPx, double dpiScale, int downscale = 4)
    {
        double margin = Math.Ceiling(style.Margin * dpiScale);
        double sx = style.SigmaX * dpiScale, sy = style.SigmaY * dpiScale, dy = style.OffsetY * dpiScale;
        int w = Math.Max(1, (int)Math.Ceiling((widthPx + 2 * margin) / downscale));
        int h = Math.Max(1, (int)Math.Ceiling((heightPx + 2 * margin) / downscale));

        // 高斯模糊对横竖可分离：覆盖率 = 横向区间模糊 × 纵向区间模糊
        var gx = new double[w];
        var inX = new bool[w];
        for (int i = 0; i < w; i++)
        {
            double x = -margin + (i + 0.5) * downscale;
            gx[i] = Interval(x, 0, widthPx, sx);
            inX[i] = x > 0 && x < widthPx;
        }
        var gy = new double[h];
        var inY = new bool[h];
        for (int j = 0; j < h; j++)
        {
            double y = -margin + (j + 0.5) * downscale;
            gy[j] = Interval(y, dy, heightPx + dy, sy);
            inY[j] = y > 0 && y < heightPx;
        }

        var bgra = new byte[w * h * 4];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                if (inX[i] && inY[j]) continue; // 窗口本身覆盖的区域
                bgra[(j * w + i) * 4 + 3] = (byte)Math.Round(255 * Math.Clamp(style.Opacity * gx[i] * gy[j], 0, 1)); // 黑色预乘：只有 alpha
            }
        return new ShadowImage(w, h, downscale, margin, bgra);
    }

    /// <summary>区间 [lo, hi] 的指示函数经标准差 <paramref name="sigma"/> 的高斯模糊后在 <paramref name="t"/> 处的值。</summary>
    public static double Interval(double t, double lo, double hi, double sigma)
    {
        double k = Math.Sqrt(2) * Math.Max(sigma, 1e-6);
        return 0.5 * (Erf((t - lo) / k) - Erf((t - hi) / k));
    }

    /// <summary>误差函数（Abramowitz–Stegun 7.1.26，误差 &lt; 1.5e-7）。</summary>
    public static double Erf(double x)
    {
        double sign = Math.Sign(x);
        x = Math.Abs(x);
        double t = 1 / (1 + 0.3275911 * x);
        double y = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return sign * y;
    }

    /// <summary>
    /// 某个窗口在本帧用「活动」阴影的权重（其余用非活动阴影）：会话开始时原来的前台窗口是活动的；
    /// 收起时被选中、要成为前台的窗口随进度 p→0 逐渐变成活动，原来的前台窗口逐渐变成非活动——
    /// 落定时与撤层后的真实桌面一致。
    /// </summary>
    public static double ActiveWeight(bool isForeground, bool isFocusTarget, bool hasFocusTarget, double progress)
    {
        double p = Math.Clamp(progress, 0, 1);
        if (isFocusTarget) return isForeground ? 1 : 1 - p;
        if (isForeground) return hasFocusTarget ? p : 1;
        return 0;
    }
}
