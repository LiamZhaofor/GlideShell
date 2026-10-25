namespace GlideShell.Core.Motion;

/// <summary>
/// 越界阻尼（iOS 橡皮筋公式）：f(x) = (1 − 1 / (x·c / d + 1)) · d。
/// 起始斜率为 c（越界一开始就有阻力），越拉越难，最多逼近 d。
/// </summary>
public static class RubberBand
{
    public const double DefaultCoefficient = 0.55;

    public static double Apply(double overflow, double extent, double coefficient = DefaultCoefficient)
    {
        if (overflow <= 0 || extent <= 0) return 0;
        return (1 - 1 / (overflow * coefficient / extent + 1)) * extent;
    }

    /// <summary>f'(x)（x ≥ 0）：把手指速度换算成越界区里的画面速度，保证松手瞬间速度连续。区间内（x &lt; 0）为 1。</summary>
    public static double Slope(double overflow, double extent, double coefficient = DefaultCoefficient)
    {
        if (overflow < 0) return 1;
        if (extent <= 0) return 0;
        double k = overflow * coefficient / extent + 1;
        return coefficient / (k * k);
    }

    /// <summary>对 [min, max] 两端施加橡皮筋，区间内原样返回。</summary>
    public static double Clamp(double value, double min, double max, double extentBelow, double extentAbove,
        double coefficient = DefaultCoefficient)
    {
        if (value < min) return min - Apply(min - value, extentBelow, coefficient);
        if (value > max) return max + Apply(value - max, extentAbove, coefficient);
        return value;
    }
}

/// <summary>
/// 松手落点投影（Apple「Designing Fluid Interfaces」）：按减速率 r（每毫秒保留的速度比例）
/// 积分到停止，滑行距离 = v · r / (1 − r) / 1000。默认 0.998 对应 iOS 滚动视图的「正常」减速。
/// </summary>
public static class Projection
{
    public const double NormalDecelerationRate = 0.998;

    public static double Project(double position, double velocityPerSecond, double decelerationRate = NormalDecelerationRate)
        => position + velocityPerSecond / 1000 * decelerationRate / (1 - decelerationRate);
}
