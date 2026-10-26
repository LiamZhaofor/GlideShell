using GlideShell.Core.Geometry;

namespace GlideShell.Core.Overview;

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

    /// <summary>某进度下鼠标点中的窗口（从最上层往下找），没有返回 -1。</summary>
    public int HitTest(double x, double y, double progress)
    {
        for (int i = 0; i < Windows.Count; i++)
            if (OverviewVisuals.WindowRect(Windows[i].Source, Slots[i].Tile, progress).Contains(x, y)) return i;
        return -1;
    }
}
