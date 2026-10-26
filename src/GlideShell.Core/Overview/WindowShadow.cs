namespace GlideShell.Core.Overview;

/// <summary>阴影的一层：一个向下偏移、横竖模糊半径不同的模糊矩形（96 DPI 逻辑像素）。</summary>
/// <param name="Opacity">这一层最深处的不透明度。</param>
/// <param name="SigmaX">横向高斯模糊的标准差。</param>
/// <param name="SigmaY">纵向高斯模糊的标准差。</param>
/// <param name="OffsetY">整体向下偏移。</param>
public readonly record struct ShadowLayer(double Opacity, double SigmaX, double SigmaY, double OffsetY);

/// <summary>
/// Win11 窗口阴影的近似（真机实测拟合，2026-10-26，150% 缩放下测得后换算成 96 DPI 逻辑像素）。
/// 缩略图不含阴影，总览自己按这个参数画，撤层那一刻缩略图与真实窗口才一致（否则阴影会一下子压到下面的窗口上）。
/// 多层按「1 − ∏(1 − 各层)」叠加。
/// </summary>
public sealed record ShadowStyle(IReadOnlyList<ShadowLayer> Layers)
{
    /// <summary>
    /// 前台（活动）窗口：贴边的深层 + 偏下的宽层（物理像素，150%：A=0.41 σ=17/21 不偏移；A=0.55 σ=29/34 下移 45），
    /// 四条边逐点误差 &lt; 0.01（RMS 0.0033）。
    /// </summary>
    public static ShadowStyle Active { get; } = new([new(0.41, 11.6, 14.3, 0), new(0.55, 19.3, 22.3, 29.9)]);

    /// <summary>非活动窗口：单层 A=0.23、σx=18、σy=36、下移 34（物理像素，150%，RMS 0.0045）。</summary>
    public static ShadowStyle Inactive { get; } = new([new(0.23, 12.0, 24.0, 22.7)]);

    /// <summary>阴影图在窗口四周各留的边距（逻辑像素）：偏移 + 3σ 之外已经看不出来。</summary>
    public double Margin => Layers.Max(l => l.OffsetY + 3 * Math.Max(l.SigmaX, l.SigmaY));
}

/// <summary>阴影图：黑色、预乘 alpha 的 BGRA，按 <paramref name="Downscale"/> 缩小存储（阴影本身很虚，放大回去看不出差别）。</summary>
/// <param name="MarginPx">图的左上角相对窗口左上角向外的距离（物理像素，原尺寸）。</param>
/// <param name="Bgra">从 <see cref="System.Buffers.ArrayPool{T}.Shared"/> 借来的缓冲（可能比 Width×Height×4 长），用完调用 <see cref="Return"/>。</param>
public sealed record ShadowImage(int Width, int Height, int Downscale, double MarginPx, byte[] Bgra)
{
    /// <summary>上传完就还回去：每张约 1MB，反复新分配会落在大对象堆里迟迟不回收。</summary>
    public void Return() => System.Buffers.ArrayPool<byte>.Shared.Return(Bgra);
}

public static class WindowShadow
{
    /// <summary>
    /// 画出某个大小窗口的阴影图（物理像素尺寸 <paramref name="widthPx"/>×<paramref name="heightPx"/>，DPI 缩放 <paramref name="dpiScale"/>）。
    /// 窗口下面也照常画：缩略图不透明、盖在上面；真实阴影本来也延伸到窗口下面，圆角露出来的正是它。
    /// （早先把窗口区域挖空，放大插值时在窗口边外留下 2–4px 的浅边，撤层时这一圈「闪一下」。）
    /// </summary>
    public static ShadowImage Render(ShadowStyle style, double widthPx, double heightPx, double dpiScale, int downscale = 4)
    {
        double margin = Math.Ceiling(style.Margin * dpiScale);
        int w = Math.Max(1, (int)Math.Ceiling((widthPx + 2 * margin) / downscale));
        int h = Math.Max(1, (int)Math.Ceiling((heightPx + 2 * margin) / downscale));

        // 每层的高斯模糊横竖可分离：该层覆盖率 = 横向区间模糊 × 纵向区间模糊
        int n = style.Layers.Count;
        var gx = new double[n][];
        var gy = new double[n][];
        for (int k = 0; k < n; k++)
        {
            var l = style.Layers[k];
            gx[k] = new double[w];
            for (int i = 0; i < w; i++)
                gx[k][i] = l.Opacity * Interval(-margin + (i + 0.5) * downscale, 0, widthPx, l.SigmaX * dpiScale);
            gy[k] = new double[h];
            double dy = l.OffsetY * dpiScale;
            for (int j = 0; j < h; j++)
                gy[k][j] = Interval(-margin + (j + 0.5) * downscale, dy, heightPx + dy, l.SigmaY * dpiScale);
        }

        int size = w * h * 4;
        var bgra = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
        Array.Clear(bgra, 0, size);
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                double clear = 1;
                for (int k = 0; k < n; k++) clear *= 1 - gx[k][i] * gy[k][j];
                bgra[(j * w + i) * 4 + 3] = (byte)Math.Round(255 * Math.Clamp(1 - clear, 0, 1)); // 黑色预乘：只有 alpha
            }
        return new ShadowImage(w, h, downscale, margin, bgra);
    }

    /// <summary>窗口外某点（相对窗口左上角，物理像素）的阴影不透明度，与 <see cref="Render"/> 同一个模型（测试与诊断用）。</summary>
    public static double Coverage(ShadowStyle style, double x, double y, double widthPx, double heightPx, double dpiScale)
    {
        double clear = 1;
        foreach (var l in style.Layers)
        {
            double dy = l.OffsetY * dpiScale;
            clear *= 1 - l.Opacity * Interval(x, 0, widthPx, l.SigmaX * dpiScale) * Interval(y, dy, heightPx + dy, l.SigmaY * dpiScale);
        }
        return 1 - clear;
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
