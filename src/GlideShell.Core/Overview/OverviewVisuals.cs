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

    /// <summary>背景遮罩不透明度：随进度线性加深，越界部分不再加深。</summary>
    public static double BackdropOpacity(double p, double max = 0.85) => Math.Clamp(p, 0, 1) * max;

    /// <summary>标题条只在接近展开完成时淡入（飞行途中显示文字会显得乱）。</summary>
    public static double LabelOpacity(double p) => SmoothStep(0.75, 1.0, p);

    /// <summary>
    /// 收起时指针下的窗口逐渐放大，暗示「松手后它会成为前台」（macOS 行为）。
    /// p 从 1 降到 0 时放大量从 0 增长到 <paramref name="maxGrow"/>。
    /// </summary>
    public static RectD EmphasizeForDismiss(RectD rect, double p, double maxGrow = 0.08)
        => rect.ScaleAroundCenter(1 + maxGrow * (1 - Math.Clamp(p, 0, 1)));

    public static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
