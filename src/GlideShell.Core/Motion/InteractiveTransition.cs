namespace GlideShell.Core.Motion;

public enum TransitionPhase
{
    /// <summary>静止在某个值上。</summary>
    Resting,

    /// <summary>手指在板上：值严格跟随手指（越界时带橡皮筋阻尼），不做任何自动推进。</summary>
    Tracking,

    /// <summary>松手后由弹簧驶向目标；随时可被新的手势打断。</summary>
    Animating,
}

public readonly record struct TransitionSample(double Value, double Velocity, TransitionPhase Phase, double Target, bool JustSettled);

/// <summary>
/// 一个由手势驱动的连续过渡（例如总览展开进度 0..1、窗口切换的位置 0..n−1）。
/// 体现 GlideShell 的核心约定：手指在板上时只跟随、不提交；松手后才按投影落点选目标，
/// 并以松手速度为初速度启动弹簧；动画中再次按下手指会从当前画面位置接管（可打断）。
/// </summary>
/// <remarks>
/// 线程安全：输入线程调用 Begin/Track/Release，渲染线程按帧调用 <see cref="Sample"/>，内部用一把锁保护（无竞争时开销约几十纳秒）。
/// </remarks>
public sealed class InteractiveTransition
{
    private readonly object _gate = new();
    private TransitionPhase _phase = TransitionPhase.Resting;
    private double _value;
    private double _trackOrigin;
    private double _rawValue;
    private SpringAnimation _animation;

    public InteractiveTransition(double min, double max, double initial)
    {
        Min = min;
        Max = max;
        _value = initial;
    }

    public double Min { get; private set; }
    public double Max { get; private set; }

    /// <summary>
    /// 改变取值范围（例如窗口切换的窗口数在手势开始后才确定）。当前值超出新范围时夹回，正在进行的动画目标同样夹回。
    /// </summary>
    public void SetRange(double min, double max, long hostUs)
    {
        lock (_gate)
        {
            Min = min;
            Max = Math.Max(min, max);
            if (_phase == TransitionPhase.Animating && (_animation.Target < Min || _animation.Target > Max))
            {
                var s = _animation.Sample(hostUs);
                _value = s.Value;
                StartAnimation(Math.Clamp(_animation.Target, Min, Max), s.Velocity, hostUs);
            }
            else if (_phase == TransitionPhase.Resting)
            {
                _value = Math.Clamp(_value, Min, Max);
            }
            else if (_phase == TransitionPhase.Tracking)
            {
                _value = RubberBand.Clamp(_rawValue, Min, Max, OverscrollBelow, OverscrollAbove);
            }
        }
    }

    /// <summary>越过下界 / 上界时橡皮筋的最大位移（与值同单位）。0 = 硬边界。</summary>
    public double OverscrollBelow { get; init; } = 0.08;
    public double OverscrollAbove { get; init; } = 0.12;

    public SpringParameters Spring { get; init; } = SpringParameters.Default;

    /// <summary>松手时选目标用的投影减速率（见 <see cref="Projection"/>）。</summary>
    public double DecelerationRate { get; init; } = Projection.NormalDecelerationRate;

    public TransitionPhase Phase
    {
        get { lock (_gate) return _phase; }
    }

    /// <summary>当前目标（只读、不推进状态）：动画中为动画目标，否则为当前值。</summary>
    public double Target
    {
        get { lock (_gate) return _phase == TransitionPhase.Animating ? _animation.Target : _value; }
    }

    /// <summary>某时刻的值（只读、不推进状态）。落定事件只由 <see cref="Sample"/> 发出，其他线程查询用这个。</summary>
    public double Peek(long hostUs)
    {
        lock (_gate) return _phase == TransitionPhase.Animating ? _animation.Sample(hostUs).Value : _value;
    }

    /// <summary>手指按下：从当前画面位置开始跟随（动画中则就地接管，速度归零）。</summary>
    public void BeginTracking(long hostUs)
    {
        lock (_gate)
        {
            if (_phase == TransitionPhase.Animating) _value = _animation.Sample(hostUs).Value;
            _trackOrigin = _value;
            _rawValue = _value;
            _phase = TransitionPhase.Tracking;
        }
    }

    /// <summary>手指移动：<paramref name="delta"/> 是自按下以来的累计位移（已换算成值的单位）。</summary>
    public void Track(double delta)
    {
        lock (_gate)
        {
            if (_phase != TransitionPhase.Tracking) return;
            _rawValue = _trackOrigin + delta;
            _value = RubberBand.Clamp(_rawValue, Min, Max, OverscrollBelow, OverscrollAbove);
        }
    }

    /// <summary>
    /// 松手：按投影落点选目标，以当前速度启动弹簧，返回选中的目标。
    /// <paramref name="chooseTarget"/> 接收 (当前值, 投影落点)，缺省为「投影过半就到另一端」。
    /// </summary>
    public double Release(double velocity, long hostUs, Func<double, double, double>? chooseTarget = null)
    {
        lock (_gate)
        {
            if (_phase != TransitionPhase.Tracking) return _phase == TransitionPhase.Animating ? _animation.Target : _value;

            // 越界区里画面速度 = 手指速度 × 橡皮筋斜率，保证松手瞬间速度连续
            double visualVelocity = velocity * BandSlope(_rawValue);
            double projected = Projection.Project(_rawValue, velocity, DecelerationRate);
            double target = Math.Clamp((chooseTarget ?? NearestEnd)(_value, projected), Min, Max);
            StartAnimation(target, visualVelocity, hostUs);
            return target;
        }
    }

    /// <summary>手势被取消（异常恢复路径）：回到按下前最近的端点，不按速度判断。</summary>
    public void CancelTracking(double restoreTo, long hostUs)
    {
        lock (_gate)
        {
            if (_phase != TransitionPhase.Tracking) return;
            StartAnimation(Math.Clamp(restoreTo, Min, Max), 0, hostUs);
        }
    }

    /// <summary>非手势触发（热键、点击）：从当前画面位置动画到目标。</summary>
    public void AnimateTo(double target, long hostUs, double velocity = 0)
    {
        lock (_gate)
        {
            double from = _value, v = velocity;
            if (_phase == TransitionPhase.Animating)
            {
                var s = _animation.Sample(hostUs);
                from = s.Value;
                v = velocity == 0 ? s.Velocity : velocity;
            }
            _value = from;
            StartAnimation(Math.Clamp(target, Min, Max), v, hostUs);
        }
    }

    /// <summary>立即跳到某值并静止（初始化、强制复位）。</summary>
    public void SnapTo(double value)
    {
        lock (_gate)
        {
            _value = Math.Clamp(value, Min, Max);
            _phase = TransitionPhase.Resting;
        }
    }

    /// <summary>渲染线程每帧调用：返回该时刻的值；动画落定的那一帧 <see cref="TransitionSample.JustSettled"/> 为 true。</summary>
    public TransitionSample Sample(long hostUs)
    {
        lock (_gate)
        {
            switch (_phase)
            {
                case TransitionPhase.Animating:
                    var (value, velocity, settled) = _animation.Sample(hostUs);
                    _value = value;
                    if (!settled) return new TransitionSample(value, velocity, _phase, _animation.Target, false);
                    _phase = TransitionPhase.Resting;
                    return new TransitionSample(value, 0, _phase, value, true);
                case TransitionPhase.Tracking:
                    return new TransitionSample(_value, 0, _phase, _value, false);
                default:
                    return new TransitionSample(_value, 0, _phase, _value, false);
            }
        }
    }

    private double NearestEnd(double current, double projected)
        => projected >= (Min + Max) / 2 ? Max : Min;

    private double BandSlope(double raw)
    {
        if (raw < Min) return RubberBand.Slope(Min - raw, OverscrollBelow);
        if (raw > Max) return RubberBand.Slope(raw - Max, OverscrollAbove);
        return 1;
    }

    private void StartAnimation(double target, double velocity, long hostUs)
    {
        _animation = new SpringAnimation(Spring, _value, target, velocity, hostUs);
        _phase = TransitionPhase.Animating;
    }
}
