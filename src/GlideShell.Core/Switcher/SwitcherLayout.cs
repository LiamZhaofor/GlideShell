using GlideShell.Core.Geometry;

namespace GlideShell.Core.Switcher;

/// <summary>
/// 窗口切换条的几何：一行等高的缩略图（按窗口纵横比定宽），连续位置 p（0 = 第一个窗口居中）决定整行平移，
/// 离中心越近的项放得越大。p 可以是小数，也可以略超出两端（橡皮筋），整行都随之连续移动。
/// </summary>
public sealed class SwitcherLayout
{
    private readonly double[] _centers; // 各项中心相对第 0 项中心的横向偏移
    private readonly double[] _widths;

    private SwitcherLayout(RectD area, double itemHeight, double[] centers, double[] widths)
    {
        Area = area;
        ItemHeight = itemHeight;
        _centers = centers;
        _widths = widths;
    }

    public RectD Area { get; }
    public double ItemHeight { get; }
    public int Count => _widths.Length;

    /// <summary>选中项的放大倍数（正好居中时）。</summary>
    public const double FocusScale = 1.12;

    /// <param name="sizes">各窗口（宽, 高），按切换顺序。</param>
    /// <param name="area">所在显示器的工作区。</param>
    public static SwitcherLayout Create(IReadOnlyList<(double Width, double Height)> sizes, RectD area, double dpiScale = 1)
    {
        double itemHeight = Math.Round(area.Height * 0.2);
        double maxWidth = area.Width * 0.3;
        double gap = 28 * dpiScale;
        var widths = new double[sizes.Count];
        var centers = new double[sizes.Count];
        double x = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            var (w, h) = sizes[i];
            widths[i] = Math.Min(maxWidth, w > 0 && h > 0 ? itemHeight * w / h : itemHeight * 16 / 9);
            if (i > 0) x += widths[i - 1] / 2 + gap + widths[i] / 2;
            centers[i] = x;
        }
        return new SwitcherLayout(area, itemHeight, centers, widths);
    }

    /// <summary>位置 p 下第 i 项的矩形（屏幕坐标）。</summary>
    public RectD ItemRect(int i, double p)
    {
        double offset = _centers[i] - CenterAt(p);
        // 放大的项（最多两个：p 两侧的整数位）把左右的项各推开自己增宽的一半，间距保持不变
        int f = (int)Math.Floor(p);
        for (int k = f; k <= f + 1; k++)
        {
            if (k < 0 || k >= Count || k == i) continue;
            double extra = _widths[k] * (Emphasis(k, p) - 1);
            offset += k < i ? extra / 2 : -extra / 2;
        }
        double e = Emphasis(i, p);
        double w = _widths[i] * e, h = ItemHeight * e;
        return new RectD(Area.CenterX + offset - w / 2, Area.CenterY - h / 2, w, h);
    }

    private static double Emphasis(int i, double p) => 1 + (FocusScale - 1) * Math.Max(0, 1 - Math.Abs(i - p));

    /// <summary>选中项（离中心最近的一项）。</summary>
    public int Nearest(double p) => Count == 0 ? -1 : (int)Math.Clamp(Math.Round(p), 0, Count - 1);

    /// <summary>选中项标题条的位置。</summary>
    public RectD LabelRect(double dpiScale = 1)
    {
        double h = 40 * dpiScale;
        double top = Area.CenterY + ItemHeight * FocusScale / 2 + 16 * dpiScale;
        return new RectD(Area.Left + Area.Width * 0.2, top, Area.Width * 0.6, h);
    }

    /// <summary>切换条所在的横带（缩略图在其中滚动，超出部分被裁掉；横带之外是实时的桌面）。</summary>
    public RectD BandRect(double dpiScale = 1)
    {
        double pad = 24 * dpiScale;
        double top = Area.CenterY - ItemHeight * FocusScale / 2 - pad;
        double bottom = LabelRect(dpiScale).Bottom + pad / 2;
        double margin = Area.Width * 0.04;
        return RectD.FromEdges(Area.Left + margin, top, Area.Right - margin, bottom);
    }

    // 连续位置 → 行内横向坐标：整数位置正好是那一项的中心，之间线性插值，两端外按首末间距外推
    private double CenterAt(double p)
    {
        if (Count == 0) return 0;
        if (Count == 1) return p * (_widths[0] + ItemHeight * 0.3);
        if (p <= 0) return p * (_centers[1] - _centers[0]);
        if (p >= Count - 1) return _centers[^1] + (p - (Count - 1)) * (_centers[^1] - _centers[^2]);
        int i = (int)Math.Floor(p);
        double t = p - i;
        return _centers[i] + (_centers[i + 1] - _centers[i]) * t;
    }
}
