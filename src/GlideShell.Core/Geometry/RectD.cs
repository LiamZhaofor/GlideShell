namespace GlideShell.Core.Geometry;

/// <summary>双精度矩形（屏幕物理像素）。布局与插值都在这里算，最后才取整交给合成器。</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectD FromEdges(double left, double top, double right, double bottom)
        => new(left, top, right - left, bottom - top);

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>两个矩形有没有面积大于 0 的交集（只碰到边不算）。</summary>
    public bool Intersects(RectD other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom && !IsEmpty && !other.IsEmpty;

    public RectD Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>四周各向外扩 <paramref name="dx"/> / <paramref name="dy"/>（负数为内缩）。</summary>
    public RectD Inflate(double dx, double dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

    /// <summary>以中心为基准缩放。</summary>
    public RectD ScaleAroundCenter(double factor)
    {
        double w = Width * factor, h = Height * factor;
        return new RectD(CenterX - w / 2, CenterY - h / 2, w, h);
    }

    /// <summary>线性插值（t 可超出 [0,1]：越界阻尼时窗口继续略微收缩 / 放大）。</summary>
    public static RectD Lerp(RectD a, RectD b, double t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Width + (b.Width - a.Width) * t, a.Height + (b.Height - a.Height) * t);

    /// <summary>保持纵横比放进 <paramref name="box"/> 并居中（快照与实时缩略图同样观感）。</summary>
    public static RectD FitInside(double contentWidth, double contentHeight, RectD box)
    {
        if (contentWidth <= 0 || contentHeight <= 0) return box;
        double s = Math.Min(box.Width / contentWidth, box.Height / contentHeight);
        double w = contentWidth * s, h = contentHeight * s;
        return new RectD(box.CenterX - w / 2, box.CenterY - h / 2, w, h);
    }

    public static RectD Union(RectD a, RectD b)
        => FromEdges(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    public override string ToString() => $"({X:0.#},{Y:0.#} {Width:0.#}×{Height:0.#})";
}
