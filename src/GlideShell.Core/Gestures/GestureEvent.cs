namespace GlideShell.Core.Gestures;

public enum GestureKind
{
    Swipe,
    Tap,
}

public enum GesturePhase
{
    /// <summary>达到最少指数的手指已落下，尚未移动到滑动阈值。可据此提前预热（枚举窗口等）。</summary>
    Possible,

    /// <summary>移动超过滑动阈值，方向已锁定。位移从这一刻起算。</summary>
    Began,

    Changed,

    /// <summary>手指抬起（剩余按下的手指 ≤ 1）。只有这一刻才允许做完成/取消的决定。</summary>
    Ended,

    /// <summary>异常中止（设备移除、长时间无报文等），消费方应回到手势前的状态，不按速度判定。</summary>
    Cancelled,

    /// <summary>Possible 之后没有形成滑动也不是轻点（例如改成了两指滚动）。</summary>
    Failed,
}

public enum GestureAxis
{
    Horizontal,
    Vertical,
}

public enum GestureDirection
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>
/// 手势事件。位移与速度为毫米、毫米/秒，坐标方向与触摸板一致（X 向右、Y 向下为正）。
/// <see cref="DxMm"/>/<see cref="DyMm"/> 从 <see cref="GesturePhase.Began"/> 起累计；指数中途变化不会造成跳变。
/// </summary>
public readonly record struct GestureEvent(
    GestureKind Kind,
    GesturePhase Phase,
    int Fingers,
    GestureAxis Axis,
    GestureDirection Direction,
    double DxMm,
    double DyMm,
    double VxMmS,
    double VyMmS,
    long HostTimeUs,
    long DeviceTimeUs)
{
    /// <summary>沿锁定轴、朝初始方向的位移（正 = 继续往初始方向滑，负 = 往回滑）。</summary>
    public double AlongMm => Direction switch
    {
        GestureDirection.Up => -DyMm,
        GestureDirection.Down => DyMm,
        GestureDirection.Left => -DxMm,
        _ => DxMm,
    };

    /// <summary>沿锁定轴、朝初始方向的速度。</summary>
    public double AlongVelocityMmS => Direction switch
    {
        GestureDirection.Up => -VyMmS,
        GestureDirection.Down => VyMmS,
        GestureDirection.Left => -VxMmS,
        _ => VxMmS,
    };
}
