using GlideShell.Core.Input;
using GlideShell.Core.Motion;

namespace GlideShell.Core.Gestures;

/// <summary>
/// 多指手势识别：触点帧 → 滑动 / 轻点事件。
/// <list type="bullet">
/// <item>位移 = 相邻两帧中「都在按」的触点的平均位移累加，所以落指、抬指、加指都不会让位移跳变。</item>
/// <item>抬指只认 TipSwitch=0（或触点从报文中消失）；没有报文 = 状态不变（手指静止时设备可能停发）。</item>
/// <item>Confidence=0 的触点（手掌）整个生命周期都不参与识别。</item>
/// <item>速度按设备时间做最小二乘，停住再抬起时速度为 0。</item>
/// </list>
/// 只在输入线程上调用；事件通过构造时传入的回调同步发出。
/// </summary>
public sealed class GestureRecognizer
{
    private enum State { Idle, Possible, Tracking, Draining }

    private struct Tracked
    {
        public int Id;
        public double X, Y;
        public bool Palm;
        public bool Seen;
    }

    private readonly GestureSettings _settings;
    private readonly Action<GestureEvent> _sink;
    private readonly VelocityTracker _velocity;
    private readonly Tracked[] _tracked = new Tracked[ContactFrame.MaxContacts];
    private int _trackedCount;

    private State _state;
    private double _tx, _ty;          // 当前指数下的累计位移（指数变化时清零重计）
    private double _originX, _originY; // Began 时的位移，之后上报的位移以它为零点
    private double _maxMove;
    private int _lastTouching;
    private int _maxFingers;
    private int _fingers;
    private bool _possibleEmitted;
    private long _startDevUs;
    private long _countChangedDevUs;
    private long? _belowSinceDevUs;
    private long _lastFrameHostUs;
    private long _lastFrameDevUs;
    private GestureAxis _axis;
    private GestureDirection _direction;

    public GestureRecognizer(GestureSettings settings, Action<GestureEvent> sink)
    {
        _settings = settings;
        _sink = sink;
        _velocity = new VelocityTracker(settings.VelocityWindowMs);
    }

    public bool IsActive => _state is State.Possible or State.Tracking;

    public void Process(ContactFrame frame)
    {
        _lastFrameHostUs = frame.HostTimeUs;
        _lastFrameDevUs = frame.DeviceTimeUs;
        int touching = UpdateContacts(frame, out double dx, out double dy);
        _tx += dx;
        _ty += dy;
        long dev = frame.DeviceTimeUs;

        switch (_state)
        {
            case State.Idle:
                if (touching == 0) return;
                _state = State.Possible;
                _startDevUs = dev;
                _countChangedDevUs = dev;
                _lastTouching = touching;
                _maxFingers = touching;
                _possibleEmitted = false;
                _belowSinceDevUs = null;
                _maxMove = 0;
                ResetTranslation();
                HandlePossible(frame, touching);
                break;
            case State.Possible:
                HandlePossible(frame, touching);
                break;
            case State.Tracking:
                HandleTracking(frame, touching);
                break;
            case State.Draining:
                if (touching == 0) _state = State.Idle;
                break;
        }
    }

    /// <summary>
    /// 输入线程定期调用（几百毫秒一次即可）。只做异常恢复：手势进行中超过
    /// <see cref="GestureSettings.StaleTimeoutMs"/> 收不到报文才取消。
    /// </summary>
    public void Tick(long hostUs)
    {
        if (IsActive && hostUs - _lastFrameHostUs > _settings.StaleTimeoutMs * 1000) Cancel(hostUs);
    }

    /// <summary>立即中止（设备移除、会话锁定等）。</summary>
    public void Cancel(long hostUs)
    {
        if (_state == State.Tracking) Emit(GestureKind.Swipe, GesturePhase.Cancelled, hostUs, _lastFrameDevUs, 0, 0);
        else if (_state == State.Possible && _possibleEmitted) Emit(GestureKind.Swipe, GesturePhase.Failed, hostUs, _lastFrameDevUs, 0, 0);
        _state = State.Idle;
        _trackedCount = 0;
    }

    private void HandlePossible(ContactFrame frame, int touching)
    {
        long dev = frame.DeviceTimeUs;
        if (touching != _lastTouching)
        {
            // 指数变化：位移基准重置（新增手指的落点不算位移），稳定计时重新开始
            _countChangedDevUs = dev;
            _lastTouching = touching;
            _maxFingers = Math.Max(_maxFingers, touching);
            ResetTranslation();
        }
        _maxMove = Math.Max(_maxMove, Math.Sqrt(_tx * _tx + _ty * _ty));

        if (touching == 0)
        {
            double durationMs = (dev - _startDevUs) / 1000.0;
            if (_maxFingers >= _settings.MinFingers && durationMs <= _settings.TapMaxDurationMs && _maxMove <= _settings.TapMaxMoveMm)
            {
                _fingers = _maxFingers;
                Emit(GestureKind.Tap, GesturePhase.Ended, frame.HostTimeUs, dev, 0, 0);
            }
            else if (_possibleEmitted)
            {
                Emit(GestureKind.Swipe, GesturePhase.Failed, frame.HostTimeUs, dev, 0, 0);
            }
            _state = State.Idle;
            return;
        }

        if (touching < _settings.MinFingers)
        {
            // 曾达到最少指数后又少于它：可能是轻点时手指先后抬起，等一个稳定窗口再判失败
            if (_maxFingers >= _settings.MinFingers)
            {
                _belowSinceDevUs ??= dev;
                if (dev - _belowSinceDevUs.Value > _settings.FingerSettleMs * 1000)
                {
                    if (_possibleEmitted) Emit(GestureKind.Swipe, GesturePhase.Failed, frame.HostTimeUs, dev, 0, 0);
                    _state = State.Draining;
                }
            }
            return;
        }
        _belowSinceDevUs = null;

        if (!_possibleEmitted)
        {
            _possibleEmitted = true;
            _fingers = touching;
            Emit(GestureKind.Swipe, GesturePhase.Possible, frame.HostTimeUs, dev, 0, 0);
        }

        _velocity.Add(dev, _tx, _ty);
        double dist = Math.Sqrt(_tx * _tx + _ty * _ty);
        bool settled = dev - _countChangedDevUs >= _settings.FingerSettleMs * 1000;
        if (dist >= _settings.SlopMm && (settled || dist >= 2 * _settings.SlopMm)) Begin(frame, touching);
    }

    private void Begin(ContactFrame frame, int touching)
    {
        _state = State.Tracking;
        _fingers = touching;
        _axis = Math.Abs(_tx) >= Math.Abs(_ty) * _settings.AxisLockRatio ? GestureAxis.Horizontal : GestureAxis.Vertical;
        _direction = _axis == GestureAxis.Horizontal
            ? (_tx >= 0 ? GestureDirection.Right : GestureDirection.Left)
            : (_ty < 0 ? GestureDirection.Up : GestureDirection.Down);
        _originX = _tx;
        _originY = _ty;
        var (vx, vy) = _velocity.Velocity(frame.DeviceTimeUs);
        Emit(GestureKind.Swipe, GesturePhase.Began, frame.HostTimeUs, frame.DeviceTimeUs, vx, vy);
    }

    private void HandleTracking(ContactFrame frame, int touching)
    {
        long dev = frame.DeviceTimeUs;
        if (touching >= 2) _velocity.Add(dev, _tx, _ty);
        var (vx, vy) = _velocity.Velocity(dev);
        if (touching <= 1)
        {
            Emit(GestureKind.Swipe, GesturePhase.Ended, frame.HostTimeUs, dev, vx, vy);
            _state = touching == 0 ? State.Idle : State.Draining;
            return;
        }
        Emit(GestureKind.Swipe, GesturePhase.Changed, frame.HostTimeUs, dev, vx, vy);
    }

    private void ResetTranslation()
    {
        _tx = _ty = 0;
        _velocity.Clear();
    }

    private void Emit(GestureKind kind, GesturePhase phase, long hostUs, long devUs, double vx, double vy)
    {
        bool reportsMotion = phase is GesturePhase.Changed or GesturePhase.Ended;
        _sink(new GestureEvent(kind, phase, _fingers, _axis, _direction,
            reportsMotion ? _tx - _originX : 0, reportsMotion ? _ty - _originY : 0,
            vx, vy, hostUs, devUs));
    }

    /// <summary>
    /// 更新触点表，返回按下中的有效触点数（不含手掌），并输出「前后两帧都在按」的触点平均位移。
    /// </summary>
    private int UpdateContacts(ContactFrame frame, out double dx, out double dy)
    {
        for (int i = 0; i < _trackedCount; i++) _tracked[i].Seen = false;
        double sx = 0, sy = 0;
        int common = 0, touching = 0;

        foreach (var c in frame.Contacts)
        {
            int idx = Find(c.Id);
            if (!c.Tip)
            {
                if (idx >= 0) RemoveAt(idx); // TipSwitch=0：该触点抬起
                continue;
            }
            if (idx < 0)
            {
                if (_trackedCount >= _tracked.Length) continue;
                idx = _trackedCount++;
                _tracked[idx] = new Tracked { Id = c.Id, X = c.XMm, Y = c.YMm, Palm = !c.Confidence, Seen = true };
            }
            else
            {
                ref var t = ref _tracked[idx];
                t.Palm |= !c.Confidence;
                if (!t.Palm)
                {
                    sx += c.XMm - t.X;
                    sy += c.YMm - t.Y;
                    common++;
                }
                t.X = c.XMm;
                t.Y = c.YMm;
                t.Seen = true;
            }
            if (!_tracked[idx].Palm) touching++;
        }

        // 没有抬起报告就从帧里消失的触点：同样视为结束
        for (int i = _trackedCount - 1; i >= 0; i--)
            if (!_tracked[i].Seen) RemoveAt(i);

        dx = common > 0 ? sx / common : 0;
        dy = common > 0 ? sy / common : 0;
        return touching;
    }

    private int Find(int id)
    {
        for (int i = 0; i < _trackedCount; i++)
            if (_tracked[i].Id == id) return i;
        return -1;
    }

    private void RemoveAt(int i) => _tracked[i] = _tracked[--_trackedCount];
}
