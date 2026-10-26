using GlideShell.Core.Geometry;

namespace GlideShell.Core.Overview;

/// <summary>总览里的方向键导航方向。</summary>
public enum NavigationDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>一块显示器的总览区域（工作区，不含任务栏）与 DPI 缩放。</summary>
public readonly record struct OverviewArea(RectD WorkArea, double DpiScale);

/// <summary>
/// 一次总览会话的静态场景：参与的窗口（Z 序，最上层在前）与各自的平铺位。
/// 不可变，由宿主在预热时构建好后整体替换给控制器，渲染线程只读。
/// </summary>
public sealed class OverviewScene
{
    public static OverviewScene Empty { get; } = new([], []);

    private OverviewScene(IReadOnlyList<OverviewWindow> windows, LayoutSlot[] slots)
    {
        Windows = windows;
        Slots = slots;
    }

    public IReadOnlyList<OverviewWindow> Windows { get; }

    /// <summary>与 <see cref="Windows"/> 一一对应。</summary>
    public IReadOnlyList<LayoutSlot> Slots { get; }

    public int Count => Windows.Count;

    /// <summary>
    /// 按显示器分组布局：窗口在哪块屏，就平铺在哪块屏（各屏独立排版）。
    /// <paramref name="primaryOnly"/> 时全部排到 <paramref name="primaryIndex"/> 那块屏。
    /// </summary>
    public static OverviewScene Build(IReadOnlyList<OverviewWindow> windows, IReadOnlyList<OverviewArea> areas,
        bool primaryOnly = false, int primaryIndex = 0, LayoutOptions? options = null)
    {
        if (windows.Count == 0 || areas.Count == 0) return Empty;
        var slots = new LayoutSlot[windows.Count];
        var groups = windows.Select((w, i) => (w, i))
            .GroupBy(x => primaryOnly ? primaryIndex : Math.Clamp(x.w.MonitorIndex, 0, areas.Count - 1));
        foreach (var g in groups)
        {
            var area = areas[Math.Clamp(g.Key, 0, areas.Count - 1)];
            var members = g.ToList();
            var opts = options ?? LayoutOptions.Scaled(area.DpiScale);
            var laid = OverviewLayout.Compute(members.Select(m => m.w.Source).ToList(), area.WorkArea, opts);
            for (int k = 0; k < members.Count; k++) slots[members[k].i] = laid[k];
        }
        return new OverviewScene(windows, slots);
    }

    public int IndexOf(long windowId)
    {
        for (int i = 0; i < Windows.Count; i++)
            if (Windows[i].Id == windowId) return i;
        return -1;
    }

    /// <summary>
    /// 方向键导航：从 <paramref name="from"/> 出发，找该方向上最近的窗口（按平铺位中心）。
    /// 只考虑方向半平面内的窗口，评分 = 主方向距离 + 2 × 横向偏离（偏得越多越不像「那个方向」）；
    /// 那个方向没有窗口时原地不动。<paramref name="from"/> 无效时返回第 0 个（最上层）。
    /// </summary>
    public int Navigate(int from, NavigationDirection direction)
    {
        if (Count == 0) return -1;
        if (from < 0 || from >= Count) return 0;
        var c = Slots[from].Tile;
        int best = from;
        double bestScore = double.MaxValue;
        for (int i = 0; i < Count; i++)
        {
            if (i == from) continue;
            var t = Slots[i].Tile;
            double dx = t.CenterX - c.CenterX, dy = t.CenterY - c.CenterY;
            var (along, across) = direction switch
            {
                NavigationDirection.Left => (-dx, dy),
                NavigationDirection.Right => (dx, dy),
                NavigationDirection.Up => (-dy, dx),
                _ => (dy, dx),
            };
            if (along <= 1) continue;
            double score = along + 2 * Math.Abs(across);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Tab 导航：按阅读顺序（先上后下、同一行先左后右）循环到下一个 / 上一个窗口。
    /// 中心高度相差不到最矮平铺位一半的算同一行。
    /// </summary>
    public int Cycle(int from, bool backwards)
    {
        if (Count == 0) return -1;
        var order = ReadingOrder();
        int pos = Array.IndexOf(order, from);
        if (pos < 0) return order[0];
        return order[(pos + (backwards ? order.Length - 1 : 1)) % order.Length];
    }

    private int[] ReadingOrder()
    {
        double rowTolerance = Slots.Min(s => s.Tile.Height) / 2;
        var byY = Enumerable.Range(0, Count).OrderBy(i => Slots[i].Tile.CenterY).ToList();
        var order = new List<int>(Count);
        int start = 0;
        while (start < byY.Count)
        {
            double rowY = Slots[byY[start]].Tile.CenterY;
            int end = start;
            while (end < byY.Count && Slots[byY[end]].Tile.CenterY - rowY < rowTolerance) end++;
            order.AddRange(byY.GetRange(start, end - start).OrderBy(i => Slots[i].Tile.CenterX));
            start = end;
        }
        return [.. order];
    }

    /// <summary>某进度下鼠标点中的窗口（从最上层往下找），没有返回 -1。</summary>
    public int HitTest(double x, double y, double progress)
    {
        for (int i = 0; i < Windows.Count; i++)
            if (OverviewVisuals.SceneWindow(Windows[i], Slots[i].Tile, progress, emphasized: false).Rect.Contains(x, y)) return i;
        return -1;
    }
}
