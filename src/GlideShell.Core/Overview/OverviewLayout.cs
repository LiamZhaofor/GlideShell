using GlideShell.Core.Geometry;

namespace GlideShell.Core.Overview;

/// <summary>总览布局参数。长度单位为像素，调用方按显示器 DPI 缩放后传入。</summary>
public sealed record LayoutOptions
{
    /// <summary>四周留白占工作区短边的比例。</summary>
    public double MarginFraction { get; init; } = 0.06;

    public double Gap { get; init; } = 20;

    /// <summary>每个窗口下方标题条的高度。</summary>
    public double LabelHeight { get; init; } = 36;

    /// <summary>缩放上限：哪怕只有一个窗口，也要有明显的「收缩」。</summary>
    public double MaxScale { get; init; } = 0.65;

    /// <summary>小窗口最多放大的比例（按窗口高度相对工作区高度线性给出），保证小窗可读。</summary>
    public double SmallWindowBoost { get; init; } = 0.25;

    /// <summary>评分里「横向空间利用率」的权重：越大越倾向于铺满宽度。</summary>
    public double UtilizationWeight { get; init; } = 0.55;

    public static LayoutOptions Scaled(double dpiScale) => new()
    {
        Gap = 20 * dpiScale,
        LabelHeight = 36 * dpiScale,
    };
}

/// <summary>一个窗口在总览里的位置：缩略图区域 + 下方标题条。</summary>
public readonly record struct LayoutSlot(RectD Tile, RectD Label);

/// <summary>
/// 就近布局（思路来自 GNOME Shell workspace.js，旧原型实测过手感）：
/// 按原位的竖直中心分行、行内按水平中心排序，让平铺位落在原位附近、飞行距离最短；
/// 所有窗口同一缩放系数（保留相对大小），小窗口适度放大；尝试 1..n 行，按「缩放 + 空间利用」评分取最优，评分变差即停。
/// </summary>
public static class OverviewLayout
{
    public static LayoutSlot[] Compute(IReadOnlyList<RectD> sources, RectD area, LayoutOptions? options = null)
    {
        var o = options ?? new LayoutOptions();
        int n = sources.Count;
        var slots = new LayoutSlot[n];
        if (n == 0 || area.IsEmpty) return slots;

        double margin = Math.Min(area.Width, area.Height) * o.MarginFraction;
        double availW = area.Width - 2 * margin;
        double availH = area.Height - 2 * margin;

        // 有效尺寸 = 原尺寸 × 小窗放大系数；打包和缩放都按有效尺寸算，保证放大后也不越界
        var ew = new double[n];
        var eh = new double[n];
        for (int i = 0; i < n; i++)
        {
            var s = sources[i];
            double boost = 1 + o.SmallWindowBoost * Math.Clamp(1 - s.Height / area.Height, 0, 1);
            ew[i] = Math.Max(1, s.Width) * boost;
            eh[i] = Math.Max(1, s.Height) * boost;
        }

        var byRow = Enumerable.Range(0, n).OrderBy(i => sources[i].CenterY).ThenBy(i => sources[i].CenterX).ToArray();
        double totalW = ew.Sum();

        List<List<int>>? bestRows = null;
        double bestScore = double.MinValue, bestScale = 0;
        int lastRowCount = -1;
        for (int target = 1; target <= n; target++)
        {
            var rows = PackRows(byRow, ew, totalW / target);
            if (rows.Count == lastRowCount) continue; // 贪心分出来的行数和上一轮一样，评分也一样
            lastRowCount = rows.Count;

            double scaleW = double.MaxValue, sumRowH = 0;
            foreach (var row in rows)
            {
                scaleW = Math.Min(scaleW, (availW - o.Gap * (row.Count - 1)) / row.Sum(i => ew[i]));
                sumRowH += row.Max(i => eh[i]);
            }
            double scaleH = (availH - o.Gap * (rows.Count - 1) - o.LabelHeight * rows.Count) / sumRowH;
            double scale = Math.Min(Math.Min(scaleW, scaleH), o.MaxScale);
            if (scale <= 0) break;

            double usedW = rows.Max(r => r.Sum(i => ew[i]) * scale + o.Gap * (r.Count - 1));
            double score = scale + o.UtilizationWeight * Math.Min(1, usedW / availW);
            if (bestRows is not null && score <= bestScore + 0.002) break;
            bestScore = score;
            bestRows = rows;
            bestScale = scale;
        }
        bestRows ??= [byRow.ToList()];
        if (bestScale <= 0) bestScale = 0.01;

        foreach (var row in bestRows) row.Sort((a, b) => sources[a].CenterX.CompareTo(sources[b].CenterX));

        var rowHeights = bestRows.Select(r => r.Max(i => eh[i]) * bestScale + o.LabelHeight).ToList();
        double y = area.Top + margin + Math.Max(0, (availH - rowHeights.Sum() - o.Gap * (bestRows.Count - 1)) / 2);
        for (int r = 0; r < bestRows.Count; r++)
        {
            var row = bestRows[r];
            double rowW = row.Sum(i => ew[i]) * bestScale + o.Gap * (row.Count - 1);
            double x = area.Left + margin + Math.Max(0, (availW - rowW) / 2);
            foreach (int i in row)
            {
                double w = ew[i] * bestScale, h = eh[i] * bestScale;
                var tile = new RectD(x, y + (rowHeights[r] - o.LabelHeight - h) / 2, w, h);
                slots[i] = new LayoutSlot(tile, new RectD(x, tile.Bottom + 4, w, o.LabelHeight - 4));
                x += w + o.Gap;
            }
            y += rowHeights[r] + o.Gap;
        }
        return slots;
    }

    // 贪心分行：当前行加上这个窗口离理想行宽更远时就换行，行宽尽量接近理想值
    private static List<List<int>> PackRows(int[] order, double[] widths, double idealRowW)
    {
        var rows = new List<List<int>> { new() };
        double rowW = 0;
        foreach (int i in order)
        {
            var row = rows[^1];
            if (row.Count > 0 && Math.Abs(rowW + widths[i] - idealRowW) > Math.Abs(rowW - idealRowW))
            {
                rows.Add(row = []);
                rowW = 0;
            }
            row.Add(i);
            rowW += widths[i];
        }
        return rows;
    }
}
