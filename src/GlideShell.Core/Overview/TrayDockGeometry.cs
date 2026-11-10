using GlideShell.Core.Geometry;

namespace GlideShell.Core.Overview;

/// <summary>
/// 总览底部后台程序栏的几何（物理像素）：工作区底边居中的一条圆角栏，每个程序一个「图标 + 名字」的方块。
/// 程序太多放不下时整体缩小（最小 0.6 倍），还放不下就从末尾截掉。
/// </summary>
public sealed class TrayDockGeometry
{
    // 以下单位都是 DIP
    public const double TileW = 70, TileH = 62, Icon = 32, IconTop = 7, LabelGap = 4, LabelH = 16;
    public const double PadX = 8, PadY = 6, Gap = 2, CornerRadius = 16;
    public const double BottomMargin = 14, TopGap = 10, SideMargin = 48, MinScale = 0.6;

    /// <summary>栏本身的高度（DIP）。</summary>
    public const double DockHeight = TileH + 2 * PadY;

    private TrayDockGeometry(RectD area, RectD dock, RectD[] tiles, RectD[] icons, RectD[] labels, double scale, double dpiScale)
    {
        Area = area;
        Dock = dock;
        Tiles = tiles;
        Icons = icons;
        Labels = labels;
        Scale = scale;
        DpiScale = dpiScale;
    }

    /// <summary>这块屏的工作区（覆盖层范围）。</summary>
    public RectD Area { get; }

    /// <summary>栏的外框（没有程序时为空矩形）。</summary>
    public RectD Dock { get; }

    /// <summary>每个程序的方块（悬停高亮的范围、点击范围）。</summary>
    public IReadOnlyList<RectD> Tiles { get; }

    public IReadOnlyList<RectD> Icons { get; }

    /// <summary>每个程序的名字位置。</summary>
    public IReadOnlyList<RectD> Labels { get; }

    /// <summary>DIP → 像素的总缩放（DPI × 放不下时的缩小）。</summary>
    public double Scale { get; }

    public double DpiScale { get; }

    /// <summary>显示出来的程序数（放不下时少于传入的数目，下标与传入的前若干个对应）。</summary>
    public int Count => Tiles.Count;

    public double Radius => CornerRadius * Scale;

    /// <summary>
    /// 栏完全收进屏幕底边以下要往下移多少（总览收起时栏沉到任务栏后面：覆盖层只盖工作区，底边以下的部分看不见）。
    /// </summary>
    public double HiddenOffset => Count == 0 ? 0 : Area.Bottom - Dock.Top + 2 * DpiScale;

    /// <summary>窗口区域要给栏留出的高度（栏 + 底边距 + 与窗口之间的空隙）。</summary>
    public static double ReservedHeight(double dpiScale) => (DockHeight + BottomMargin + TopGap) * dpiScale;

    /// <summary>
    /// 总览排窗口时底部要让出的高度：<see cref="ReservedHeight"/> 减去布局自己留的底边距的一半
    /// （布局四周本来就留了约短边 6% 的空白，栏可以占掉其中一半，窗口不会白白缩得太小）。
    /// </summary>
    public static double LayoutInset(RectD area, double dpiScale)
    {
        double layoutMargin = Math.Min(area.Width, area.Height) * new LayoutOptions().MarginFraction;
        return Math.Max(0, Math.Round(ReservedHeight(dpiScale) - layoutMargin / 2));
    }

    public static TrayDockGeometry Compute(RectD area, double dpiScale, int count)
    {
        int n = Math.Max(0, count);
        double maxW = Math.Max(0, area.Width - 2 * SideMargin * dpiScale);
        double s = 1;
        if (NaturalWidth(n) * dpiScale > maxW)
        {
            // 放不下：先整体缩小，缩到最小还放不下就从末尾截掉
            s = Math.Max(MinScale, maxW / (NaturalWidth(n) * dpiScale));
            while (n > 0 && NaturalWidth(n) * dpiScale * s > maxW + 0.5) n--;
        }
        double k = dpiScale * s;
        if (n == 0) return new TrayDockGeometry(area, default, [], [], [], k, dpiScale);

        double dockW = NaturalWidth(n) * k, dockH = DockHeight * k;
        double left = Math.Round(area.CenterX - dockW / 2), bottom = Math.Round(area.Bottom - BottomMargin * dpiScale);
        var dock = new RectD(left, bottom - dockH, Math.Round(dockW), Math.Round(dockH));
        var tiles = new RectD[n];
        var icons = new RectD[n];
        var labels = new RectD[n];
        double x = dock.Left + PadX * k, top = dock.Top + PadY * k, iw = Icon * k;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) x += Gap * k;
            var tile = new RectD(x, top, TileW * k, TileH * k);
            tiles[i] = tile;
            icons[i] = new RectD(Math.Round(tile.CenterX - iw / 2), Math.Round(tile.Top + IconTop * k), Math.Round(iw), Math.Round(iw));
            labels[i] = new RectD(tile.Left + 2 * k, icons[i].Bottom + LabelGap * k, tile.Width - 4 * k, LabelH * k);
            x += tile.Width;
        }
        return new TrayDockGeometry(area, dock, tiles, icons, labels, k, dpiScale);
    }

    /// <summary><paramref name="n"/> 个程序排成一条时的宽度（DIP）。</summary>
    private static double NaturalWidth(int n) => 2 * PadX + n * TileW + Math.Max(0, n - 1) * Gap;

    public bool Contains(double x, double y) => Count > 0 && Dock.Contains(x, y);

    /// <summary>指针下的程序（-1 = 没有，包括栏里的空白处）。</summary>
    public int HitTest(double x, double y)
    {
        if (!Contains(x, y)) return -1;
        for (int i = 0; i < Count; i++)
            if (Tiles[i].Contains(x, y)) return i;
        return -1;
    }

    /// <summary>横向离 <paramref name="x"/> 最近的程序（键盘从窗口往下进入栏时用）。</summary>
    public int Nearest(double x)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < Count; i++)
        {
            double d = Math.Abs(Tiles[i].CenterX - x);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }
}
