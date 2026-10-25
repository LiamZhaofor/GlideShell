namespace GlideShell.Core.Input;

/// <summary>
/// 把 ScanTime（16 位、100µs、会回绕）展开成单调递增的设备时间（µs）。
/// 速度一律按设备时间算：它是硬件采样时刻，不受消息排队、成批投递影响。
/// </summary>
/// <remarks>
/// 只靠 ScanTime 分不清空闲期间转了几圈，也分不清设备在新一轮触摸时把 ScanTime 清零；
/// 这两种情况都用主机时间间隔校正（只影响跨空闲的那一个间隔，不影响触摸期间的精度）。
/// </remarks>
public sealed class ScanClock(int scanTimeBits = 16)
{
    private const long TickUs = 100;
    private const long ResetToleranceUs = 500_000;

    private readonly long _mod = 1L << Math.Clamp(scanTimeBits, 1, 31);
    private bool _started;
    private long _lastScan;
    private long _lastHostUs;
    private long _deviceUs;

    /// <summary>推进一份报文，返回它的设备时间（µs）。首份报文以主机时间为起点，两条时间轴大致对齐。</summary>
    public long Advance(int scanTime, long hostUs)
    {
        if (!_started)
        {
            _started = true;
            _deviceUs = hostUs;
        }
        else
        {
            long hostDelta = Math.Max(0, hostUs - _lastHostUs);
            long periodUs = _mod * TickUs;
            long scanDelta = ((scanTime - _lastScan) % _mod + _mod) % _mod * TickUs;
            if (hostDelta > periodUs / 2)
            {
                // 可能已转过不止一圈：取与主机间隔最接近的圈数
                long laps = Math.Max(0, (long)Math.Round((hostDelta - scanDelta) / (double)periodUs));
                scanDelta += laps * periodUs;
            }
            else if (scanDelta > hostDelta + ResetToleranceUs)
            {
                scanDelta = hostDelta; // 设备时间比主机快出很多 = 设备把 ScanTime 清零了
            }
            _deviceUs += scanDelta;
        }
        _lastScan = scanTime;
        _lastHostUs = hostUs;
        return _deviceUs;
    }

    public void Reset() => _started = false;
}
