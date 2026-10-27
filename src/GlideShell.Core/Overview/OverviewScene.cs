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

/// <summary>一块显示器的总览区域（工作区，不含任务栏）与 DPI 缩放；<paramref name="Bar"/> 非空时顶部有桌面条，窗口平铺在它下面。</summary>
public readonly record struct OverviewArea(RectD WorkArea, double DpiScale, DesktopBarGeometry? Bar = null);

/// <summary>
/// 一次总览会话的静态场景：参与的窗口（Z 序，最上层在前）与各自的平铺位。
/// 不可变，由宿主在预热时构建好后整体替换给控制器，渲染线程只读。
/// 会话中可以移走 / 加入窗口（拖到别的桌面）：<see cref="Without"/> / <see cref="With"/> 返回新场景，
/// 窗口下标不变（缩略图注册按下标，不用重建），移走的窗口留在原位但标成 <see cref="IsRemoved"/>；
/// 其余窗口重新排版，新场景记着旧平铺位，<see cref="Tile(int, double, double)"/> 按重排进度从旧位置过渡过去。
/// 应用窗口视图（<see cref="Focus"/>）也用同一套下标：其他程序的窗口标成 <see cref="IsExcluded"/>，不排版，留在原位随进度淡出。
/// </summary>
public sealed class OverviewScene
{
    public static OverviewScene Empty { get; } = new([], [], [], new Context([], false, 0, null, false, false), [], null, []);

    private readonly DesktopBarGeometry?[] _bars; // 每个窗口平铺所在那块屏的桌面条（没有为 null）
    private readonly Context _context; // 重新排版用
    private readonly bool[] _removed;
    private readonly RectD?[]? _from; // 重排前的平铺位（收起时的坐标）；新加入的窗口为 null
    private readonly bool[] _excluded; // 应用窗口视图里其他程序的窗口

    /// <param name="MinimizedShelf">最小化的窗口单独排在底部一排（应用窗口视图，macOS App Exposé 的样子）。</param>
    private sealed record Context(IReadOnlyList<OverviewArea> Areas, bool PrimaryOnly, int PrimaryIndex, LayoutOptions? Options, bool GroupByApp,
        bool MinimizedShelf = false);

    /// <summary>底部最小化窗口那一排占工作区高度的比例。</summary>
    public const double ShelfFraction = 0.2;

    private OverviewScene(IReadOnlyList<OverviewWindow> windows, LayoutSlot[] slots, DesktopBarGeometry?[] bars, Context context, bool[] removed, RectD?[]? from,
        bool[] excluded)
    {
        Windows = windows;
        Slots = slots;
        _bars = bars;
        _context = context;
        _removed = removed;
        _from = from;
        _excluded = excluded;
    }

    public IReadOnlyList<OverviewWindow> Windows { get; }

    /// <summary>与 <see cref="Windows"/> 一一对应。</summary>
    public IReadOnlyList<LayoutSlot> Slots { get; }

    public int Count => Windows.Count;

    /// <summary>窗口已被移到别的桌面（不再显示、不参与选择与命中）。</summary>
    public bool IsRemoved(int index) => _removed[index];

    /// <summary>应用窗口视图里其他程序的窗口：不排版、不参与选择与命中，留在原位随进度淡出。</summary>
    public bool IsExcluded(int index) => _excluded[index];

    /// <summary>平铺在总览里的窗口（没有移走、也没有被排除）。</summary>
    public bool IsPresent(int index) => !_removed[index] && !_excluded[index];

    /// <summary>平铺在总览里的窗口数。</summary>
    public int PresentCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Count; i++)
                if (IsPresent(i)) n++;
            return n;
        }
    }

    /// <summary>
    /// 显示桌面：每个窗口的「平铺位」是把它沿离屏幕中心最近的方向整个推出所在屏的工作区（再多出 <paramref name="marginPx"/>），
    /// 进度 0 → 1 时窗口跟手滑出屏幕；最小化的窗口本来就不在桌面上，标成排除。窗口下标不变。
    /// </summary>
    public OverviewScene PushAside(IReadOnlyList<OverviewArea> areas, double marginPx = 40)
    {
        var slots = Slots.ToArray();
        var excluded = new bool[Count];
        for (int i = 0; i < Count; i++)
        {
            var w = Windows[i];
            if (w.IsMinimized || _removed[i])
            {
                excluded[i] = true;
                continue;
            }
            var area = areas.Count == 0 ? w.Source : areas[Math.Clamp(w.MonitorIndex, 0, areas.Count - 1)].WorkArea;
            var s = w.Source;
            // 往哪边推：窗口中心相对屏幕中心的方向，按屏幕宽高归一化后取较大的那个轴
            double dx = (s.CenterX - area.CenterX) / Math.Max(1, area.Width), dy = (s.CenterY - area.CenterY) / Math.Max(1, area.Height);
            RectD tile = Math.Abs(dx) >= Math.Abs(dy)
                ? s.Offset(dx >= 0 ? area.Right + marginPx - s.Left : area.Left - marginPx - s.Right, 0)
                : s.Offset(0, dy >= 0 ? area.Bottom + marginPx - s.Top : area.Top - marginPx - s.Bottom);
            slots[i] = new LayoutSlot(tile, tile);
        }
        return new OverviewScene(Windows, slots, new DesktopBarGeometry?[Count], _context with { Areas = areas }, (bool[])_removed.Clone(), null, excluded);
    }

    /// <summary>
    /// 应用窗口视图：只平铺 <paramref name="include"/> 选中的窗口，排在 <paramref name="areas"/> 里（通常没有桌面条）；
    /// 其余窗口标成排除。窗口下标不变，缩略图注册可以直接沿用。前台标记交给选中窗口里最上层的非最小化窗口。
    /// <paramref name="minimizedShelf"/>：同一块屏上既有普通窗口又有最小化窗口时，最小化的缩小排在底部一排（之后补位也按这个规则）。
    /// <paramref name="animate"/>：总览已经展开、从当前平铺直接换成应用窗口视图——选中的窗口从现在的平铺位重排过去，
    /// 其余窗口在现在的平铺位淡出（都按重排进度，见 <see cref="RelayoutFrom"/>）。
    /// </summary>
    public OverviewScene Focus(Func<OverviewWindow, bool> include, IReadOnlyList<OverviewArea> areas, bool minimizedShelf = false, bool animate = false)
    {
        var excluded = Windows.Select(w => !include(w)).ToArray();
        var windows = Windows.ToArray();
        int fg = Array.FindIndex(windows, w => w.IsForeground);
        if (fg < 0 || excluded[fg])
        {
            int next = Enumerable.Range(0, windows.Length).FirstOrDefault(i => !excluded[i] && !_removed[i] && !windows[i].IsMinimized, -1);
            if (next >= 0)
            {
                if (fg >= 0) windows[fg] = windows[fg] with { IsForeground = false };
                windows[next] = windows[next] with { IsForeground = true };
            }
        }
        var context = _context with { Areas = areas, MinimizedShelf = minimizedShelf };
        var hidden = _removed.Zip(excluded, (r, e) => r || e).ToArray();
        var from = animate ? Enumerable.Range(0, Count).Select(i => IsPresent(i) ? (RectD?)Tile(i, 0) : null).ToArray() : null;
        var (slots, bars) = Arrange(windows, hidden, context, [.. Slots], new DesktopBarGeometry?[Count]);
        return new OverviewScene(windows, slots, bars, context, (bool[])_removed.Clone(), from, excluded);
    }

    /// <summary>第 <paramref name="index"/> 个窗口在桌面条展开 <paramref name="expansion"/> 时的平铺位（窗口区域往下缩，给桌面条让位）。</summary>
    public RectD Tile(int index, double expansion) => Tile(index, expansion, 1);

    /// <summary>同上，重排进度 <paramref name="relayout"/>（0 = 重排前的位置，1 = 新位置）。</summary>
    public RectD Tile(int index, double expansion, double relayout)
    {
        var tile = RawTile(index, relayout);
        return _bars[index] is { } bar ? bar.MapContent(tile, expansion) : tile;
    }

    /// <summary>重排前的平铺位（没有在重排、或是新加入的窗口为 null）。</summary>
    public RectD? RelayoutFrom(int index) => _from?[index];

    /// <summary>
    /// 鼠标点在哪个窗口下方的标题行上（只算平铺在总览里的窗口；没有返回 -1）。总览里点标题行 = 只看这个程序的窗口。
    /// </summary>
    public int LabelHitTest(double x, double y)
    {
        for (int i = 0; i < Count; i++)
            if (IsPresent(i) && Slots[i].Label.Contains(x, y)) return i;
        return -1;
    }

    private RectD RawTile(int index, double relayout)
    {
        var tile = Slots[index].Tile;
        return relayout < 1 && _from?[index] is { } from ? RectD.Lerp(from, tile, Math.Max(0, relayout)) : tile;
    }

    /// <summary>
    /// 移走第 <paramref name="index"/> 个窗口（拖到了别的桌面）：其余窗口重新排版，从重排进度 <paramref name="relayoutAt"/>
    /// 时的位置过渡过去。移走的是前台窗口时，前台标记交给剩下最上层的非最小化窗口（活动阴影跟着它）。
    /// </summary>
    public OverviewScene Without(int index, double relayoutAt = 1)
    {
        if (index < 0 || index >= Count || _removed[index]) return this;
        var removed = (bool[])_removed.Clone();
        removed[index] = true;
        var windows = Windows.ToArray();
        if (windows[index].IsForeground)
        {
            windows[index] = windows[index] with { IsForeground = false };
            for (int i = 0; i < windows.Length; i++)
                if (!removed[i] && !_excluded[i] && !windows[i].IsMinimized)
                {
                    windows[i] = windows[i] with { IsForeground = true };
                    break;
                }
        }
        var from = Enumerable.Range(0, Count).Select(i => (RectD?)RawTile(i, relayoutAt)).ToArray();
        var (slots, bars) = Arrange(windows, removed.Zip(_excluded, (r, e) => r || e).ToArray(), _context, [.. Slots], [.. _bars]);
        return new OverviewScene(windows, slots, bars, _context, removed, from, _excluded);
    }

    /// <summary>加入一个窗口（从别的桌面拖过来），排在最后（下标 = 原来的 <see cref="Count"/>）；其余窗口重新排版。</summary>
    public OverviewScene With(OverviewWindow window, double relayoutAt = 1)
    {
        var windows = Windows.Append(window).ToArray();
        var removed = _removed.Append(false).ToArray();
        var excluded = _excluded.Append(false).ToArray();
        if (_context.Areas.Count == 0)
            return new OverviewScene(windows, new LayoutSlot[windows.Length], new DesktopBarGeometry?[windows.Length], _context, removed, null, excluded);
        var from = Enumerable.Range(0, Count).Select(i => (RectD?)RawTile(i, relayoutAt)).Append(null).ToArray();
        var (slots, bars) = Arrange(windows, removed.Zip(excluded, (r, e) => r || e).ToArray(), _context, [.. Slots, default], [.. _bars, null]);
        return new OverviewScene(windows, slots, bars, _context, removed, from, excluded);
    }

    /// <summary>
    /// 按显示器分组布局：窗口在哪块屏，就平铺在哪块屏（各屏独立排版）。
    /// <paramref name="primaryOnly"/> 时全部排到 <paramref name="primaryIndex"/> 那块屏。
    /// </summary>
    /// <param name="groupByApp">同一个程序（进程名）的窗口排在一起：排版时把它们当作都在这组窗口的中心附近。</param>
    public static OverviewScene Build(IReadOnlyList<OverviewWindow> windows, IReadOnlyList<OverviewArea> areas,
        bool primaryOnly = false, int primaryIndex = 0, LayoutOptions? options = null, bool groupByApp = false)
    {
        var context = new Context(areas, primaryOnly, primaryIndex, options, groupByApp);
        if (windows.Count == 0 || areas.Count == 0) return new OverviewScene([], [], [], context, [], null, []);
        var (slots, bars) = Arrange(windows, new bool[windows.Count], context, new LayoutSlot[windows.Count], new DesktopBarGeometry?[windows.Count]);
        return new OverviewScene(windows, slots, bars, context, new bool[windows.Count], null, new bool[windows.Count]);
    }

    /// <summary>按屏分组排版没有隐去（<paramref name="removed"/>）的窗口（就地写进 <paramref name="slots"/> / <paramref name="bars"/>；隐去的保留原来的值）。</summary>
    private static (LayoutSlot[] Slots, DesktopBarGeometry?[] Bars) Arrange(IReadOnlyList<OverviewWindow> windows, bool[] removed, Context context,
        LayoutSlot[] slots, DesktopBarGeometry?[] bars)
    {
        var areas = context.Areas;
        var groups = windows.Select((w, i) => (w, i))
            .Where(x => !removed[x.i])
            .GroupBy(x => context.PrimaryOnly ? context.PrimaryIndex : Math.Clamp(x.w.MonitorIndex, 0, areas.Count - 1));
        foreach (var g in groups)
        {
            var area = areas[Math.Clamp(g.Key, 0, areas.Count - 1)];
            var members = g.ToList();
            var opts = context.Options ?? LayoutOptions.Scaled(area.DpiScale);
            var sources = members.Select(m => m.w.Source).ToList();
            if (context.GroupByApp)
            {
                // 就近布局按中心分行、行内按中心排序：同一程序的窗口都挪到它们的共同中心，排出来就挨在一起，组与组之间仍按位置就近
                foreach (var app in members.Select((m, k) => (m.w.ProcessName, k)).GroupBy(x => x.ProcessName))
                {
                    double cx = app.Average(x => sources[x.k].CenterX), cy = app.Average(x => sources[x.k].CenterY);
                    foreach (var (_, k) in app)
                        sources[k] = sources[k].Offset(cx - sources[k].CenterX, cy - sources[k].CenterY);
                }
            }
            var content = area.Bar?.ContentArea ?? area.WorkArea;
            var shelf = members.Select(m => m.w.IsMinimized).ToArray();
            LayoutSlot[] laid;
            if (context.MinimizedShelf && shelf.Any(x => x) && shelf.Any(x => !x))
            {
                // 普通窗口排在上面，最小化的在底部一排（各自就近布局；底部那排缩得更小）
                double shelfH = Math.Round(content.Height * ShelfFraction);
                var top = RectD.FromEdges(content.Left, content.Top, content.Right, content.Bottom - shelfH);
                var bottom = RectD.FromEdges(content.Left, content.Bottom - shelfH - opts.Gap, content.Right, content.Bottom);
                laid = new LayoutSlot[members.Count];
                foreach (bool minimized in new[] { false, true })
                {
                    var idx = Enumerable.Range(0, members.Count).Where(k => shelf[k] == minimized).ToList();
                    var part = OverviewLayout.Compute(idx.Select(k => sources[k]).ToList(), minimized ? bottom : top,
                        minimized ? opts with { MarginFraction = 0.12, MaxScale = 0.25 } : opts);
                    for (int j = 0; j < idx.Count; j++) laid[idx[j]] = part[j];
                }
            }
            else laid = OverviewLayout.Compute(sources, content, opts);
            for (int k = 0; k < members.Count; k++)
            {
                slots[members[k].i] = laid[k];
                bars[members[k].i] = area.Bar;
            }
        }
        return (slots, bars);
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
        if (from < 0 || from >= Count || !IsPresent(from)) return FirstPresent();
        var c = Slots[from].Tile;
        int best = from;
        double bestScore = double.MaxValue;
        for (int i = 0; i < Count; i++)
        {
            if (i == from || !IsPresent(i)) continue;
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
        var order = ReadingOrder();
        if (order.Length == 0) return -1;
        int pos = Array.IndexOf(order, from);
        if (pos < 0) return order[0];
        return order[(pos + (backwards ? order.Length - 1 : 1)) % order.Length];
    }

    /// <summary>最上层的没有移走的窗口（-1 = 没有）。</summary>
    public int FirstPresent()
    {
        for (int i = 0; i < Count; i++)
            if (IsPresent(i)) return i;
        return -1;
    }

    private int[] ReadingOrder()
    {
        var present = Enumerable.Range(0, Count).Where(IsPresent).ToList();
        if (present.Count == 0) return [];
        double rowTolerance = present.Min(i => Slots[i].Tile.Height) / 2;
        var byY = present.OrderBy(i => Slots[i].Tile.CenterY).ToList();
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

    /// <summary>某进度（与桌面条展开程度）下鼠标点中的窗口（从最上层往下找），没有返回 -1。</summary>
    public int HitTest(double x, double y, double progress, double barExpansion = 0)
    {
        for (int i = 0; i < Windows.Count; i++)
            if (IsPresent(i) && OverviewVisuals.SceneWindow(Windows[i], Tile(i, barExpansion), progress, emphasized: false).Rect.Contains(x, y)) return i;
        return -1;
    }
}
