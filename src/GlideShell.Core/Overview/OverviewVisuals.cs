using GlideShell.Core.Geometry;

namespace GlideShell.Core.Overview;

/// <summary>
/// 总览的「进度 → 画面」映射。进度 p：0 = 窗口在原位，1 = 平铺位；越界阻尼时 p 可略超出 [0,1]。
/// 所有窗口同时、同进度运动（不错峰）：错峰会让「停住」的那一刻各窗口不在同一相位，破坏跟手感。
/// </summary>
public static class OverviewVisuals
{
    /// <summary>窗口当前矩形：原位与平铺位之间线性插值。跟手阶段必须线性，缓动交给松手后的弹簧。</summary>
    public static RectD WindowRect(RectD source, RectD tile, double p) => RectD.Lerp(source, tile, p);

    /// <summary>
    /// 最小化窗口不在屏幕上，没有「原位」可飞：停在平铺位，随进度从略小放大到原大，同时按 <see cref="MinimizedOpacity"/> 淡入。
    /// </summary>
    public static RectD MinimizedRect(RectD tile, double p, double minScale = 0.85)
        => tile.ScaleAroundCenter(minScale + (1 - minScale) * Math.Clamp(p, 0, 1));

    public static double MinimizedOpacity(double p) => Math.Clamp(p, 0, 1);

    /// <summary>
    /// 场景里一个窗口在进度 p 下的矩形与不透明度。收起时被选中的最小化窗口会被还原，
    /// 所以它和普通窗口一样飞向还原位置，而不是原地淡出。
    /// </summary>
    public static (RectD Rect, double Opacity) SceneWindow(OverviewWindow window, RectD tile, double p, bool emphasized)
    {
        if (window.IsMinimized && !emphasized) return (MinimizedRect(tile, p), MinimizedOpacity(p));
        var rect = WindowRect(window.Source, tile, p);
        return (emphasized ? EmphasizeForDismiss(rect, p) : rect, 1);
    }

    /// <summary>背景遮罩不透明度：随进度线性加深，越界部分不再加深。</summary>
    public static double BackdropOpacity(double p, double max = 0.85) => Math.Clamp(p, 0, 1) * max;

    /// <summary>标题条只在接近展开完成时淡入（飞行途中显示文字会显得乱）。</summary>
    public static double LabelOpacity(double p) => SmoothStep(0.75, 1.0, p);

    /// <summary>
    /// 收起时被选中的窗口在飞回途中略微放大，暗示「它会成为前台」（macOS 行为）。
    /// 放大量在半程最大、两端为 0：落定那一刻必须正好是窗口的真实大小，撤掉覆盖层时才不会「先大一下再缩回」。
    /// </summary>
    public static RectD EmphasizeForDismiss(RectD rect, double p, double maxGrow = 0.06)
    {
        double t = Math.Clamp(p, 0, 1);
        return rect.ScaleAroundCenter(1 + maxGrow * 4 * t * (1 - t));
    }

    public static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
