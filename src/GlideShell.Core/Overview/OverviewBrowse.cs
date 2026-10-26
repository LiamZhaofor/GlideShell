using GlideShell.Core.Configuration;
using GlideShell.Core.Gestures;

namespace GlideShell.Core.Overview;

/// <summary>总览展开时移动选中项（与鼠标悬停、方向键共用同一个高亮）。</summary>
public interface IOverviewSelectionHost
{
    /// <summary>按阅读顺序移动 <paramref name="steps"/> 格（负数向前）。任意线程调用，实现方自行投递到 UI 线程。</summary>
    void MoveSelection(int steps);
}

/// <summary>
/// 总览已展开时的三指横滑：在窗口之间移动选中项（每滑过 <see cref="SwitcherConfig.MmPerWindow"/> 一格，手指向右 = 往后），
/// 之后三指下滑或 Enter 就切到选中的窗口。只改选中项，不碰总览的展开进度。
/// </summary>
public sealed class OverviewBrowse(IOverviewSelectionHost host, Func<SwitcherConfig> config) : IGestureTarget
{
    private double _mmPerStep = 22;
    private int _steps;

    public void Prepare(long hostUs)
    {
    }

    public void Unprepare(long hostUs)
    {
    }

    public void Begin(in GestureEvent e)
    {
        _mmPerStep = Math.Max(5, config().MmPerWindow);
        _steps = 0;
        Update(e);
    }

    public void Update(in GestureEvent e)
    {
        // 起步就算一格（越过滑动阈值时手指已经在动），之后每 mmPerStep 一格；回滑会退回
        int steps = (int)Math.Truncate(e.DxMm / _mmPerStep) + Math.Sign(e.DxMm);
        if (steps == _steps) return;
        host.MoveSelection(steps - _steps);
        _steps = steps;
    }

    public void End(in GestureEvent e)
    {
    }
}
