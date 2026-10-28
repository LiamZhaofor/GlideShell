namespace GlideShell.Core.Gestures;

/// <summary>
/// 手势识别参数，全部用物理单位（毫米、毫秒），与触摸板分辨率无关。
/// 默认值按 S1 真机轨迹校准（2026-10-26，ASUS PTP，14 个场景全部通过，见 docs/spikes/S1-input-semantics.md）。
/// </summary>
public sealed record GestureSettings
{
    /// <summary>参与识别的最少指数；1–2 指留给系统（指针、滚动、缩放）。</summary>
    public int MinFingers { get; set; } = 3;

    /// <summary>质心移动超过它才确认是滑动。太小会把轻点的抖动当成滑动，太大会让起步发黏。</summary>
    public double SlopMm { get; set; } = 2.0;

    /// <summary>指数变化后要稳定这么久才锁定指数（手指很难完全同时落下）。移动很快时不等待。</summary>
    public double FingerSettleMs { get; set; } = 60;

    /// <summary>轻点：按下到全部抬起不超过这个时长。</summary>
    public double TapMaxDurationMs { get; set; } = 200;

    /// <summary>轻点：期间质心移动不超过这个距离（应不大于 <see cref="SlopMm"/>）。</summary>
    public double TapMaxMoveMm { get; set; } = 1.0;

    /// <summary>
    /// 新落下但触摸板还不确定（Confidence=0）的触点，离最近的手指不超过这个距离、且已有至少 2 根手指时，直接当手指算
    /// （真机日常轨迹：先报不确定的第 3 / 4 根手指离邻指 16–22mm；手掌不会落在指尖旁边）。0 = 一律等它变确定。
    /// </summary>
    public double AdoptNearFingerMm { get; set; } = 30;

    /// <summary>方向锁定：|dx| ≥ |dy| × 比例 判为横向，否则纵向。1 = 以 45° 为界。</summary>
    public double AxisLockRatio { get; set; } = 1.0;

    /// <summary>速度估计的时间窗。</summary>
    public double VelocityWindowMs { get; set; } = 100;

    /// <summary>
    /// 异常恢复：手势进行中超过这么久收不到任何报文才取消。这不是松手判定（松手只看 TipSwitch），
    /// 只防设备移除、驱动异常等情况把状态卡死，所以取得很长。
    /// </summary>
    public double StaleTimeoutMs { get; set; } = 10_000;
}
