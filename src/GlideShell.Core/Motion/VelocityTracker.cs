namespace GlideShell.Core.Motion;

/// <summary>
/// 二维速度估计：对最近一个时间窗内的采样做最小二乘直线拟合（与 Android VelocityTracker 的 LSQ 思路一致）。
/// 时间用设备时间（µs），所以消息成批到达不会把速度算歪。环形缓冲，热路径零分配。
/// </summary>
public sealed class VelocityTracker(double windowMs = 100)
{
    private const int Capacity = 64;

    private readonly long[] _t = new long[Capacity];
    private readonly double[] _x = new double[Capacity];
    private readonly double[] _y = new double[Capacity];
    private readonly long _windowUs = (long)(windowMs * 1000);
    private int _head; // 下一个写入位置
    private int _count;

    public void Clear() => _count = 0;

    public void Add(long timeUs, double x, double y)
    {
        if (_count > 0)
        {
            long last = _t[(_head - 1 + Capacity) % Capacity];
            if (timeUs < last) Clear(); // 时间倒退 = 新的时间基准，旧样本作废
            else if (timeUs == last)
            {
                // 同一时刻的重复采样只保留最新值
                int i = (_head - 1 + Capacity) % Capacity;
                _x[i] = x;
                _y[i] = y;
                return;
            }
        }
        _t[_head] = timeUs;
        _x[_head] = x;
        _y[_head] = y;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>
    /// 截至 <paramref name="nowUs"/> 的速度（单位/秒）。只用窗口内的样本：
    /// 手指停住后再抬起时，窗口里没有新样本，速度就是 0——这正是「停住再松手 = 不甩」。
    /// </summary>
    public (double Vx, double Vy) Velocity(long nowUs)
    {
        int n = 0;
        double st = 0, sx = 0, sy = 0;
        // 以最新样本为时间原点，避免大数相减丢精度
        for (int k = 0; k < _count; k++)
        {
            int i = (_head - 1 - k + Capacity * 2) % Capacity;
            long age = nowUs - _t[i];
            if (age > _windowUs) break;
            if (age < 0) continue;
            double t = -age / 1e6;
            st += t;
            sx += _x[i];
            sy += _y[i];
            n++;
        }
        if (n < 2) return (0, 0);

        double mt = st / n, mx = sx / n, my = sy / n;
        double stt = 0, stx = 0, sty = 0;
        for (int k = 0; k < n; k++)
        {
            int i = (_head - 1 - k + Capacity * 2) % Capacity;
            double t = -(nowUs - _t[i]) / 1e6 - mt;
            stt += t * t;
            stx += t * (_x[i] - mx);
            sty += t * (_y[i] - my);
        }
        return stt <= 1e-12 ? (0, 0) : (stx / stt, sty / stt);
    }
}
