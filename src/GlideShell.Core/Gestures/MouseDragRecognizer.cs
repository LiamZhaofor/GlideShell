using GlideShell.Core.Configuration;
using GlideShell.Core.Motion;

namespace GlideShell.Core.Gestures;

/// <summary>
/// 鼠标按键手势（Mac Mouse Fix 的「按住拖动 → 调度中心与空间」）：按住一个鼠标键拖动，就像在触摸板上滑：
/// 左右切换虚拟桌面，上拉总览，下拉应用窗口视图，全程跟手、松手按速度决定。
/// 宿主报告按下、每次指针位移（DIP）与松开；这里把位移换算成毫米，产生与触摸板一样的 <see cref="GestureEvent"/>
/// （<see cref="GestureEvent.Fingers"/> = <see cref="Fingers"/>），交给同一套路由与控制器。
/// 拖过 <see cref="BeginDip"/> 才算手势；没拖开就松开是一次普通点击（<see cref="Release"/> 返回 false，宿主补发这次点击）。
/// </summary>
public sealed class MouseDragRecognizer(Func<MouseGesturesConfig> config, Action<GestureEvent> emit)
{
    /// <summary>鼠标手势的「指数」：路由表里用它区分触摸板手势。</summary>
    public const int Fingers = 0;

    /// <summary>拖过这么远开始预热（枚举窗口等，不可见）。</summary>
    public const double PrepareDip = 3;

    /// <summary>拖过这么远锁定方向、开始跟手。</summary>
    public const double BeginDip = 10;

    private readonly VelocityTracker _velocity = new();
    private bool _pressed, _prepared, _began;
    private double _x, _y; // 按下以来的累计位移（DIP）
    private double _bx, _by; // 开始跟手那一刻的位移
    private double _mmPerDip;
    private GestureAxis _axis;
    private GestureDirection _direction;

    public bool IsPressed => _pressed;

    /// <summary>已经在跟手（松开会结束手势，而不是点击）。</summary>
    public bool IsDragging => _began;

    public void Press(long hostUs)
    {
        _pressed = true;
        _prepared = _began = false;
        _x = _y = 0;
        _mmPerDip = config().MmPerDip;
        _velocity.Clear();
        _velocity.Add(hostUs, 0, 0);
    }

    /// <summary>指针移动了 (<paramref name="dxDip"/>, <paramref name="dyDip"/>)（X 向右、Y 向下为正）。</summary>
    public void Move(double dxDip, double dyDip, long hostUs)
    {
        if (!_pressed) return;
        _x += dxDip;
        _y += dyDip;
        _velocity.Add(hostUs, _x * _mmPerDip, _y * _mmPerDip);
        if (!_prepared && Math.Max(Math.Abs(_x), Math.Abs(_y)) >= PrepareDip)
        {
            _prepared = true;
            emit(Event(GesturePhase.Possible, hostUs));
        }
        if (!_began)
        {
            if (Math.Sqrt(_x * _x + _y * _y) < BeginDip) return;
            _began = true;
            _axis = Math.Abs(_x) >= Math.Abs(_y) ? GestureAxis.Horizontal : GestureAxis.Vertical;
            _direction = _axis == GestureAxis.Horizontal ? (_x > 0 ? GestureDirection.Right : GestureDirection.Left) : (_y > 0 ? GestureDirection.Down : GestureDirection.Up);
            _bx = _x;
            _by = _y;
            emit(Event(GesturePhase.Began, hostUs));
            return;
        }
        emit(Event(GesturePhase.Changed, hostUs));
    }

    /// <summary>松开。返回是否是一次拖动（false = 没拖开，宿主应当照常执行这次点击）。</summary>
    public bool Release(long hostUs)
    {
        if (!_pressed) return false;
        _pressed = false;
        if (_began) emit(Event(GesturePhase.Ended, hostUs));
        else if (_prepared) emit(Event(GesturePhase.Failed, hostUs));
        bool dragged = _began;
        _began = _prepared = false;
        return dragged;
    }

    /// <summary>中止（钩子失效、设置改了等）：正在跟手的动作回到手势前的状态。</summary>
    public void Cancel(long hostUs)
    {
        if (!_pressed) return;
        _pressed = false;
        if (_began) emit(Event(GesturePhase.Cancelled, hostUs));
        else if (_prepared) emit(Event(GesturePhase.Failed, hostUs));
        _began = _prepared = false;
    }

    private GestureEvent Event(GesturePhase phase, long hostUs)
    {
        var (vx, vy) = _velocity.Velocity(hostUs);
        double dx = _began ? (_x - _bx) * _mmPerDip : 0, dy = _began ? (_y - _by) * _mmPerDip : 0;
        return new GestureEvent(GestureKind.Swipe, phase, Fingers, _axis, _direction, dx, dy, vx, vy, hostUs, hostUs);
    }
}
