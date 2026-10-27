using GlideShell.Core.Geometry;

namespace GlideShell.Core.Overview;

/// <summary>总览桌面条的内容：所有虚拟桌面的显示名（任务视图的顺序）与当前桌面。没有虚拟桌面服务时为 <see cref="Empty"/>（不显示桌面条）。</summary>
public sealed record DesktopBarState(IReadOnlyList<string> Names, int Current)
{
    public static DesktopBarState Empty { get; } = new([], -1);

    public int Count => Names.Count;
}

/// <summary>桌面条上指针下的东西。</summary>
public enum DesktopBarPart
{
    None,

    /// <summary>某个桌面（名字或缩略图）：点击切过去。</summary>
    Desktop,

    /// <summary>某个桌面名字右边的「×」：删除这个桌面。</summary>
    Remove,

    /// <summary>末尾的「+」：新建桌面。</summary>
    Add,
}

/// <param name="Index">桌面下标（<see cref="DesktopBarPart.Desktop"/> / <see cref="DesktopBarPart.Remove"/>），其余为 -1。</param>
public readonly record struct DesktopBarHit(DesktopBarPart Part, int Index)
{
    public static DesktopBarHit Nothing => new(DesktopBarPart.None, -1);
}

/// <summary>
/// 一块屏上桌面条的几何（物理像素）。macOS 调度中心的样子：平时顶部一排桌面名（收起），
/// 指针移到顶部时展开成一排桌面缩略图（壁纸 + 窗口），名字挪到缩略图下面；展开时下面的窗口区域整体往下缩，给它让位。
/// 缩略图与工作区同宽高比，「点击后放大进入那个桌面」时从缩略图位置等比放大到整个工作区。
/// </summary>
public sealed class DesktopBarGeometry
{
    private DesktopBarGeometry(RectD area, double collapsedHeight, double expandedHeight, RectD[] thumbs, RectD[] pills, RectD[] labels,
        RectD addTile, RectD addPill, double removeSize)
    {
        Area = area;
        CollapsedHeight = collapsedHeight;
        ExpandedHeight = expandedHeight;
        Thumbs = thumbs;
        Pills = pills;
        Labels = labels;
        AddTile = addTile;
        AddPill = addPill;
        _removeSize = removeSize;
    }

    private readonly double _removeSize;

    /// <summary>这块屏的工作区（覆盖层范围）。</summary>
    public RectD Area { get; }

    /// <summary>收起时顶部桌面名那一条的高度（窗口区域从它下面开始）。</summary>
    public double CollapsedHeight { get; }

    public double ExpandedHeight { get; }

    /// <summary>展开后每个桌面缩略图的位置。</summary>
    public IReadOnlyList<RectD> Thumbs { get; }

    /// <summary>收起时每个桌面名的位置（与缩略图同一列）。</summary>
    public IReadOnlyList<RectD> Pills { get; }

    /// <summary>展开后缩略图下面的桌面名位置。</summary>
    public IReadOnlyList<RectD> Labels { get; }

    public int Count => Thumbs.Count;

    /// <summary>展开后末尾「+」的位置（与缩略图同高、窄一些）。</summary>
    public RectD AddTile { get; }

    /// <summary>收起时末尾「+」的位置。</summary>
    public RectD AddPill { get; }

    /// <summary>展开后第 <paramref name="index"/> 个桌面名字右端的「×」（悬停在这个桌面上时才显示）。</summary>
    public RectD RemoveButton(int index)
    {
        var label = Labels[index];
        return new RectD(label.Right - _removeSize, label.Top + (label.Height - _removeSize) / 2, _removeSize, _removeSize);
    }

    /// <summary>收起时窗口区域（总览平铺用）。</summary>
    public RectD ContentArea => RectD.FromEdges(Area.Left, Area.Top + CollapsedHeight, Area.Right, Area.Bottom);

    /// <summary>展开时窗口区域。</summary>
    public RectD ExpandedContentArea => RectD.FromEdges(Area.Left, Area.Top + ExpandedHeight, Area.Right, Area.Bottom);

    /// <param name="dpiScale">1.0 = 96 DPI。</param>
    public static DesktopBarGeometry Compute(RectD area, int count, double dpiScale)
    {
        double dip = dpiScale;
        double collapsed = 44 * dip;
        double top = 14 * dip, labelGap = 6 * dip, labelH = 22 * dip, bottom = 12 * dip, gap = 22 * dip, side = 40 * dip;
        double aspect = area.Width / Math.Max(1, area.Height);
        double thumbH = Math.Clamp(area.Height * 0.11, 64 * dip, 140 * dip);
        double thumbW = thumbH * aspect;
        double addW = thumbH * 0.6; // 末尾的「+」：与缩略图同高、窄一些
        // 桌面多了放不下：整体缩小
        double needW = count * thumbW + addW + count * gap;
        if (needW > area.Width - 2 * side)
        {
            double k = (area.Width - 2 * side - count * gap) / (count * thumbW + addW);
            thumbW *= k;
            thumbH *= k;
            addW *= k;
            needW = count * thumbW + addW + count * gap;
        }
        double expanded = top + thumbH + labelGap + labelH + bottom;

        var thumbs = new RectD[count];
        var pills = new RectD[count];
        var labels = new RectD[count];
        double x = area.Left + (area.Width - needW) / 2;
        double pillH = 28 * dip;
        for (int i = 0; i < count; i++)
        {
            thumbs[i] = new RectD(x, area.Top + top, thumbW, thumbH);
            pills[i] = new RectD(x, area.Top + (collapsed - pillH) / 2, thumbW, pillH);
            labels[i] = new RectD(x, thumbs[i].Bottom + labelGap, thumbW, labelH);
            x += thumbW + gap;
        }
        var addTile = new RectD(x, area.Top + top, addW, thumbH);
        var addPill = new RectD(addTile.CenterX - pillH / 2, area.Top + (collapsed - pillH) / 2, pillH, pillH);
        return new DesktopBarGeometry(area, collapsed, expanded, thumbs, pills, labels, addTile, addPill, labelH);
    }

    /// <summary>展开程度 <paramref name="expansion"/>（0 收起 ~ 1 展开）下第 <paramref name="index"/> 个缩略图的位置：从桌面名的位置长出来。</summary>
    public RectD ThumbRect(int index, double expansion)
    {
        var full = Thumbs[index];
        var pill = Pills[index];
        var seed = new RectD(pill.CenterX - full.Width * 0.15, pill.CenterY - full.Height * 0.15, full.Width * 0.3, full.Height * 0.3);
        return RectD.Lerp(seed, full, Math.Clamp(expansion, 0, 1.05));
    }

    /// <summary>屏幕上的矩形 <paramref name="source"/>（例如窗口原位）画在第 <paramref name="index"/> 个桌面缩略图里的位置。</summary>
    public RectD InThumb(int index, RectD source, double expansion = 1)
    {
        var thumb = ThumbRect(index, expansion);
        double k = thumb.Width / Math.Max(1, Area.Width);
        return new RectD(thumb.Left + (source.Left - Area.Left) * k, thumb.Top + (source.Top - Area.Top) * k, source.Width * k, source.Height * k);
    }

    /// <summary>点击某个桌面后：缩略图随 <paramref name="zoom"/>（0 ~ 1）从桌面条上的位置放大到整个工作区。</summary>
    public RectD ZoomRect(int index, double expansion, double zoom)
        => RectD.Lerp(ThumbRect(index, expansion), Area, Math.Clamp(zoom, 0, 1));

    /// <summary>
    /// 窗口区域随展开程度往下缩：把收起时的平铺位映射到展开时的位置（以窗口区域的底边中点为基准等比缩放，
    /// 保持平铺的相对位置不变）。
    /// </summary>
    public RectD MapContent(RectD tile, double expansion)
    {
        double e = Math.Clamp(expansion, 0, 1);
        if (e <= 0) return tile;
        var a0 = ContentArea;
        double newTop = a0.Top + (ExpandedHeight - CollapsedHeight) * e;
        double k = (a0.Bottom - newTop) / Math.Max(1, a0.Height);
        double cx = a0.CenterX, bottom = a0.Bottom;
        return RectD.FromEdges(cx + (tile.Left - cx) * k, bottom - (bottom - tile.Top) * k,
            cx + (tile.Right - cx) * k, bottom - (bottom - tile.Bottom) * k);
    }

    /// <summary>指针是否在桌面条范围内（收起时是桌面名那一条，展开时是整条）：进入就展开，离开就收起。</summary>
    public bool Contains(double x, double y, double expansion)
        => Area.Contains(x, y) && y < Area.Top + CollapsedHeight + (ExpandedHeight - CollapsedHeight) * Math.Clamp(expansion, 0, 1);

    /// <summary>指针下的桌面（-1 = 没有）：展开过半按缩略图与名字算，否则按桌面名算。</summary>
    public int HitTest(double x, double y, double expansion)
    {
        var hit = Hit(x, y, expansion, removable: false);
        return hit.Part == DesktopBarPart.Desktop ? hit.Index : -1;
    }

    /// <summary>
    /// 指针下的东西：桌面、末尾的「+」，以及（<paramref name="removable"/> 时）展开后桌面名右端的「×」。
    /// 「×」只在展开后、有两个以上桌面时有效（最后一个桌面不能删）。
    /// </summary>
    public DesktopBarHit Hit(double x, double y, double expansion, bool removable = true)
    {
        bool expanded = expansion >= 0.5;
        if ((expanded ? AddTile : AddPill).Contains(x, y)) return new DesktopBarHit(DesktopBarPart.Add, -1);
        for (int i = 0; i < Count; i++)
        {
            if (expanded)
            {
                if (removable && Count >= 2 && RemoveButton(i).Contains(x, y)) return new DesktopBarHit(DesktopBarPart.Remove, i);
                if (Thumbs[i].Contains(x, y) || Labels[i].Contains(x, y)) return new DesktopBarHit(DesktopBarPart.Desktop, i);
            }
            else if (Pills[i].Contains(x, y)) return new DesktopBarHit(DesktopBarPart.Desktop, i);
        }
        return DesktopBarHit.Nothing;
    }
}
