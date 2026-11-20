using GlideShell.Core.Geometry;
using GlideShell.Core.Motion;

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

/// <summary>一次桌面拖动排序：从原来的下标移到目标下标。</summary>
public readonly record struct DesktopBarMove(int From, int To);

/// <summary>桌面拖动排序的纯计算：所有桌面仍以原下标存储，只在画面上临时换到目标槽位。</summary>
public static class DesktopBarReorder
{
    /// <summary>原下标 <paramref name="index"/> 的桌面在拖动预览里应占的槽位。</summary>
    public static int Slot(int index, int from, int to, int count)
    {
        if (count <= 0 || index < 0 || index >= count || from < 0 || from >= count || to < 0 || to >= count || from == to) return index;
        if (index == from) return to;
        if (from < to && index > from && index <= to) return index - 1;
        if (from > to && index >= to && index < from) return index + 1;
        return index;
    }

    /// <summary>被拖桌面的中心越过其他槽位中心时，预览顺序随之改变。</summary>
    public static int Target(DesktopBarGeometry bar, int from, double draggedCenterX)
    {
        if (from < 0 || from >= bar.Count) return -1;
        int target = from;
        if (draggedCenterX > bar.Thumbs[from].CenterX)
            for (int i = from + 1; i < bar.Count && draggedCenterX > bar.Thumbs[i].CenterX; i++) target = i;
        else
            for (int i = from - 1; i >= 0 && draggedCenterX < bar.Thumbs[i].CenterX; i--) target = i;
        return target;
    }

    /// <summary>
    /// 拖动排序这一帧第 <paramref name="index"/> 个桌面的缩略图、收起时的桌面名条与展开后的桌面名位置（屏幕坐标）：
    /// 其余桌面在槽位之间滑动让位；被拖的跟着指针，松手后从松手处飞到它的槽位，桌面名始终跟着它的缩略图。
    /// 合成层的缩略图与装饰（名字、框）都按它摆，两边不会错开。
    /// </summary>
    public static (RectD Thumb, RectD Pill, RectD Label) Layout(DesktopBarGeometry bar, in DesktopBarDragFrame drag, int index, double expansion)
    {
        double slot = drag.SlotOf(index, bar.Count);
        var thumb = bar.ThumbRectAt(slot, expansion);
        var pill = bar.PillAt(slot);
        var label = bar.LabelAt(slot);
        if (index != drag.Source) return (thumb, pill, label);
        var follow = new RectD(drag.X - drag.GrabX * thumb.Width, drag.Y - drag.GrabY * thumb.Height, thumb.Width, thumb.Height);
        var moved = drag.Fly < 0 ? follow : RectD.Lerp(follow, thumb, Math.Clamp(drag.Fly, 0, 1));
        return (moved, pill, label.Offset(moved.Left - thumb.Left, moved.Top - thumb.Top));
    }
}

/// <summary>
/// 桌面拖动排序的让位动画（不可变：UI 线程整体替换，渲染线程只取样）。每个桌面一个显示槽位（可带小数）的弹簧；
/// 预览顺序变化时，各桌面从此刻的位置与速度出发驶向新槽位，所以让位连续、可以随时改道。
/// </summary>
public sealed class DesktopReorderMotion
{
    /// <summary>其余桌面让位 / 回原位的弹簧（与桌面条展开同一手感）。</summary>
    public static readonly SpringParameters SlotSpring = new(0.28, 1.0);

    /// <summary>松手后被拖的缩略图飞到槽位的弹簧（与拖窗口飞回同一手感）。</summary>
    public static readonly SpringParameters FlySpring = new(0.3, 1.0);

    private readonly SpringAnimation[] _slots;

    private DesktopReorderMotion(SpringAnimation[] slots) => _slots = slots;

    public int Count => _slots.Length;

    /// <summary>所有桌面都在原位（刚开始拖）。</summary>
    public static DesktopReorderMotion Identity(int count, long hostUs)
    {
        var slots = new SpringAnimation[Math.Max(0, count)];
        for (int i = 0; i < slots.Length; i++) slots[i] = new SpringAnimation(SlotSpring, i, i, 0, hostUs);
        return new DesktopReorderMotion(slots);
    }

    /// <summary>
    /// 驶向「<paramref name="from"/> 移到 <paramref name="to"/>」的预览顺序（<paramref name="from"/> = <paramref name="to"/> 即原顺序）。
    /// <paramref name="snap"/> 那个桌面直接到位：它正被拖着，画面上跟着指针，松手时要从它的目标槽位算起。
    /// </summary>
    public DesktopReorderMotion Retarget(int from, int to, long hostUs, int snap = -1)
    {
        var next = new SpringAnimation[_slots.Length];
        for (int i = 0; i < next.Length; i++)
        {
            double target = DesktopBarReorder.Slot(i, from, to, next.Length);
            var current = _slots[i];
            if (i == snap) next[i] = new SpringAnimation(SlotSpring, target, target, 0, hostUs);
            else if (current.Target == target) next[i] = current;
            else
            {
                var (value, velocity, _) = current.Sample(hostUs);
                next[i] = new SpringAnimation(SlotSpring, value, target, velocity, hostUs);
            }
        }
        return new DesktopReorderMotion(next);
    }

    /// <summary>所有桌面直接停到目标槽位（落定动画被别的输入打断）。</summary>
    public DesktopReorderMotion Snap(long hostUs)
    {
        var next = new SpringAnimation[_slots.Length];
        for (int i = 0; i < next.Length; i++) next[i] = new SpringAnimation(SlotSpring, _slots[i].Target, _slots[i].Target, 0, hostUs);
        return new DesktopReorderMotion(next);
    }

    /// <summary>第 <paramref name="index"/> 个桌面此刻的槽位（越界的按原位）。</summary>
    public double SlotAt(int index, long hostUs) => index >= 0 && index < _slots.Length ? _slots[index].Sample(hostUs).Value : index;

    /// <summary>此刻所有桌面都已停在目标槽位。</summary>
    public bool IsSettled(long hostUs)
    {
        foreach (var slot in _slots)
            if (!slot.Sample(hostUs).Settled) return false;
        return true;
    }
}

/// <summary>
/// 一块屏上桌面条的几何（物理像素）。macOS 调度中心的样子：平时顶部一排桌面名（收起），
/// 指针移到顶部时展开成一排桌面缩略图（壁纸 + 窗口），名字挪到缩略图下面；展开时下面的窗口区域整体往下缩，给它让位。
/// 缩略图与工作区同宽高比，「点击后放大进入那个桌面」时从缩略图位置等比放大到整个工作区。
/// </summary>
public sealed class DesktopBarGeometry
{
    private DesktopBarGeometry(RectD area, double collapsedHeight, double expandedHeight, RectD[] thumbs, RectD[] pills, RectD[] labels,
        RectD addTile, RectD addPill, double removeSize, double bottomInset)
    {
        Area = area;
        BottomInset = bottomInset;
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

    /// <summary>窗口区域底部给后台程序栏让出的高度（像素）。</summary>
    public double BottomInset { get; }

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
    public RectD ContentArea => RectD.FromEdges(Area.Left, Area.Top + CollapsedHeight, Area.Right, Area.Bottom - BottomInset);

    /// <summary>展开时窗口区域。</summary>
    public RectD ExpandedContentArea => RectD.FromEdges(Area.Left, Area.Top + ExpandedHeight, Area.Right, Area.Bottom - BottomInset);

    /// <param name="dpiScale">1.0 = 96 DPI。</param>
    /// <param name="bottomInset">窗口区域底部给后台程序栏让出的高度（像素）。</param>
    public static DesktopBarGeometry Compute(RectD area, int count, double dpiScale, double bottomInset = 0)
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
        return new DesktopBarGeometry(area, collapsed, expanded, thumbs, pills, labels, addTile, addPill, labelH, Math.Max(0, bottomInset));
    }

    /// <summary>展开程度 <paramref name="expansion"/>（0 收起 ~ 1 展开）下第 <paramref name="index"/> 个缩略图的位置：从桌面名的位置长出来。</summary>
    public RectD ThumbRect(int index, double expansion)
    {
        var full = Thumbs[index];
        var pill = Pills[index];
        var seed = new RectD(pill.CenterX - full.Width * 0.15, pill.CenterY - full.Height * 0.15, full.Width * 0.3, full.Height * 0.3);
        return RectD.Lerp(seed, full, Math.Clamp(expansion, 0, 1.05));
    }

    /// <summary>小数槽位 <paramref name="slot"/> 处（拖动排序让位途中）的缩略图：在相邻两个槽位之间插值（各槽位等宽等距）。</summary>
    public RectD ThumbRectAt(double slot, double expansion)
    {
        var (lo, hi, t) = Split(slot);
        return RectD.Lerp(ThumbRect(lo, expansion), ThumbRect(hi, expansion), t);
    }

    /// <summary>小数槽位处收起时的桌面名条。</summary>
    public RectD PillAt(double slot)
    {
        var (lo, hi, t) = Split(slot);
        return RectD.Lerp(Pills[lo], Pills[hi], t);
    }

    /// <summary>小数槽位处展开后的桌面名。</summary>
    public RectD LabelAt(double slot)
    {
        var (lo, hi, t) = Split(slot);
        return RectD.Lerp(Labels[lo], Labels[hi], t);
    }

    private (int Lo, int Hi, double T) Split(double slot)
    {
        double s = Math.Clamp(double.IsFinite(slot) ? slot : 0, 0, Math.Max(0, Count - 1));
        int lo = (int)s;
        return (lo, Math.Min(lo + 1, Math.Max(0, Count - 1)), s - lo);
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
