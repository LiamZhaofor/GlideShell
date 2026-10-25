namespace GlideShell.Core.Motion;

/// <summary>
/// 弹簧参数，采用与 SwiftUI 相同的直觉化参数：
/// <see cref="Response"/> 是无阻尼振荡周期（秒，越小越快），<see cref="DampingRatio"/> 1 = 临界阻尼（不回弹），小于 1 有回弹。
/// </summary>
public readonly record struct SpringParameters(double Response, double DampingRatio)
{
    /// <summary>默认：轻微回弹，约 0.35 秒落定。</summary>
    public static SpringParameters Default => new(0.35, 0.86);

    /// <summary>临界阻尼：不回弹，用于取消/收起。</summary>
    public static SpringParameters Critical => new(0.3, 1.0);

    public double AngularFrequency => 2 * Math.PI / Math.Max(1e-3, Response);
}

/// <summary>
/// 阻尼谐振子的解析解（质量 1）：给定相对目标的初始位移 x0 与初速度 v0，求 t 秒后的位移与速度。
/// 解析解与帧率无关，任意时刻采样都精确，可打断、可重定向（以当前位移/速度为新初值即可）。
/// </summary>
public static class Spring
{
    public static (double X, double V) Evaluate(SpringParameters p, double x0, double v0, double t)
    {
        if (t <= 0) return (x0, v0);
        double w = p.AngularFrequency;
        double z = Math.Max(0, p.DampingRatio);

        if (Math.Abs(z - 1) < 1e-6)
        {
            // 临界阻尼：x = (A + B t) e^{-wt}
            double a = x0, b = v0 + w * x0, e = Math.Exp(-w * t);
            return ((a + b * t) * e, (b - w * (a + b * t)) * e);
        }
        if (z < 1)
        {
            // 欠阻尼：x = e^{-zwt}(A cos wd t + B sin wd t)
            double wd = w * Math.Sqrt(1 - z * z);
            double a = x0, b = (v0 + z * w * x0) / wd;
            double e = Math.Exp(-z * w * t), c = Math.Cos(wd * t), s = Math.Sin(wd * t);
            double x = e * (a * c + b * s);
            double v = e * ((b * wd - z * w * a) * c - (a * wd + z * w * b) * s);
            return (x, v);
        }
        {
            // 过阻尼：两个实根
            double sq = Math.Sqrt(z * z - 1);
            double r1 = -w * (z - sq), r2 = -w * (z + sq);
            double c1 = (v0 - r2 * x0) / (r1 - r2), c2 = x0 - c1;
            double e1 = Math.Exp(r1 * t), e2 = Math.Exp(r2 * t);
            return (c1 * e1 + c2 * e2, c1 * r1 * e1 + c2 * r2 * e2);
        }
    }
}

/// <summary>一段弹簧动画：从某时刻的值与速度出发，驶向目标。</summary>
public readonly record struct SpringAnimation(SpringParameters Parameters, double From, double Target, double InitialVelocity, long StartUs)
{
    /// <summary>位移与速度都小于阈值即视为落定（阈值按进度单位）。</summary>
    public const double SettleTolerance = 1e-3;

    public (double Value, double Velocity, bool Settled) Sample(long nowUs)
    {
        double t = (nowUs - StartUs) / 1e6;
        var (x, v) = Spring.Evaluate(Parameters, From - Target, InitialVelocity, t);
        bool settled = t > 0 && Math.Abs(x) < SettleTolerance && Math.Abs(v) < SettleTolerance * 10;
        return settled ? (Target, 0, true) : (Target + x, v, false);
    }
}
