using GlideShell.Core.Geometry;

namespace GlideShell.Core.Overview;

/// <summary>
/// 总览里悬停窗口左上角的关闭按钮（macOS 调度中心的样子）：深色圆底 + 白色「×」，圆心在平铺位左上角往里一点。
/// </summary>
public static class CloseButton
{
    /// <summary>按钮直径（DIP）。</summary>
    public const double DiameterDip = 26;

    /// <summary>平铺位 <paramref name="tile"/> 上关闭按钮的外接正方形（物理像素）。</summary>
    public static RectD Rect(RectD tile, double dpiScale)
    {
        double d = DiameterDip * dpiScale;
        double cx = tile.Left + d * 0.3, cy = tile.Top + d * 0.3;
        return new RectD(cx - d / 2, cy - d / 2, d, d);
    }

    /// <summary>点 (x, y) 是否落在按钮的圆里。</summary>
    public static bool Hit(RectD button, double x, double y)
    {
        double dx = x - button.CenterX, dy = y - button.CenterY, r = button.Width / 2;
        return dx * dx + dy * dy <= r * r;
    }

    /// <summary>画出 <paramref name="size"/>×<paramref name="size"/> 的按钮图（预乘 BGRA，4×4 超采样抗锯齿）。</summary>
    public static byte[] Render(int size)
    {
        const int Samples = 4;
        const double DiscAlpha = 0.92;
        const byte DiscR = 0x3A, DiscG = 0x3A, DiscB = 0x3E;
        var bgra = new byte[size * size * 4];
        double r = size / 2.0, halfStroke = Math.Max(0.75, size * 0.042), halfArm = size * 0.19;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int disc = 0, cross = 0;
                for (int sy = 0; sy < Samples; sy++)
                    for (int sx = 0; sx < Samples; sx++)
                    {
                        double px = x + (sx + 0.5) / Samples - r, py = y + (sy + 0.5) / Samples - r;
                        if (px * px + py * py > r * r) continue;
                        disc++;
                        // 「×」：两条对角线段，u / v 是沿两条对角线的坐标
                        double u = (px + py) / Math.Sqrt(2), v = (px - py) / Math.Sqrt(2);
                        if ((Math.Abs(v) <= halfStroke && Math.Abs(u) <= halfArm) || (Math.Abs(u) <= halfStroke && Math.Abs(v) <= halfArm)) cross++;
                    }
                double white = cross / (double)(Samples * Samples), dark = (disc - cross) / (double)(Samples * Samples) * DiscAlpha;
                int i = (y * size + x) * 4;
                bgra[i] = (byte)Math.Round(white * 255 + dark * DiscB);
                bgra[i + 1] = (byte)Math.Round(white * 255 + dark * DiscG);
                bgra[i + 2] = (byte)Math.Round(white * 255 + dark * DiscR);
                bgra[i + 3] = (byte)Math.Round((white + dark) * 255);
            }
        return bgra;
    }
}
