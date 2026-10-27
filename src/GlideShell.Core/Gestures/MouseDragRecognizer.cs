using GlideShell.Core.Configuration;
using GlideShell.Core.Motion;

namespace GlideShell.Core.Gestures;

/// <summary>
/// 鼠标按键手势（Mac Mouse Fix 的「按住拖动 → 调度中心与空间」）：按住一个鼠标键拖动，就像在触摸板上滑：
/// 左右切换虚拟桌面，上拉总览，下拉应用窗口视图，全程跟手、松手按速度决定。
/// 宿主报告按下、每次鼠标位移（像素，用不带加速的原始位移）与松开；这里把位移换算成毫米，产生与触摸板一样的
/// <see cref="GestureEvent"/>（<see cref="GestureEvent.Fingers"/> = <see cref="Fingers"/>），交给同一套路由与控制器。
/// 换算按 Mac Mouse Fix 的做法以屏幕尺寸为准（<see cref="Press"/> 的参数）：横向拖过一个屏宽正好切过一个桌面（桌面跟着指针走），
/// 竖向拖过半个屏高完全展开总览。拖过 <see cref="BeginPx"/> 才算手势；没拖开就松开是一次普通点击（<see cref="Release"/> 返回 false）。
/// </summary>
public sealed class MouseDragRecognizer(Action<GestureEvent> emit)
{
    /// <summary>鼠标手势的「指数」：路由表里用它区分触摸板手势。</summary>
    public const int Fingers = 0;

    /// <summary>拖过这么远开始预热（枚举窗口等，不可见）。</summary>
    public const double PreparePx = 3;

    /// <summary>拖过这么远锁定方向、开始跟手（Mac Mouse Fix 是 7）。</summary>
    public const double BeginPx = 7;

    /// <summary>竖向拖过屏高的多少完全展开总览。</summary>
    public const double VerticalFraction = 0.5;

    private readonly VelocityTracker _velocity = new();
    private bool _pressed, _prepared, _began;
    private double _x, _y; // 按下以来的累计位移（像素）
    private double _bx, _by; // 开始跟手那一刻的位移
    private double _kx, _ky; // 每像素多少毫米（横 / 竖）
    private GestureAxis _axis;
    private GestureDirection _direction;

    public bool IsPressed => _pressed;

    /// <summary>已经在跟手（松开会结束手势，而不是点击）。</summary>
    public bool IsDragging => _began;

    /// <summary>每像素多少毫米：横向一个屏宽（加桌面间隙）= 切一个桌面的行程，竖向半个屏高 = 总览的行程，再乘灵敏度。</summary>
    public static (double Horizontal, double Vertical) Scale(double screenWidthPx, double screenHeightPx, double desktopGapPx,
        double desktopTravelMm, double overviewTravelMm, double sensitivity)
        => (desktopTravelMm / Math.Max(1, screenWidthPx + desktopGapPx) * sensitivity,
            overviewTravelMm / Math.Max(1, screenHeightPx * VerticalFraction) * sensitivity);

    /// <param name="mmPerPxX">横向每像素多少毫米（见 <see cref="Scale"/>）。</param>
    /// <param name="mmPerPxY">竖向每像素多少毫米。</param>
    public void Press(long hostUs, double mmPerPxX, double mmPerPxY)
    {
        _pressed = true;
        _prepared = _began = false;
        _x = _y = 0;
        _kx = mmPerPxX;
        _ky = mmPerPxY;
        _velocity.Clear();
        _velocity.Add(hostUs, 0, 0);
    }

    /// <summary>鼠标移动了 (<paramref name="dxPx"/>, <paramref name="dyPx"/>)（X 向右、Y 向下为正）。</summary>
    public void Move(double dxPx, double dyPx, long hostUs)
    {
        if (!_pressed) return;
        _x += dxPx;
        _y += dyPx;
        _velocity.Add(hostUs, _x * _kx, _y * _ky);
        if (!_prepared && Math.Max(Math.Abs(_x), Math.Abs(_y)) >= PreparePx)
        {
            _prepared = true;
            emit(Event(GesturePhase.Possible, hostUs));
        }
        if (!_began)
        {
            if (Math.Max(Math.Abs(_x), Math.Abs(_y)) < BeginPx) return;
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
        double dx = _began ? (_x - _bx) * _kx : 0, dy = _began ? (_y - _by) * _ky : 0;
        return new GestureEvent(GestureKind.Swipe, phase, Fingers, _axis, _direction, dx, dy, vx, vy, hostUs, hostUs);
    }
}
