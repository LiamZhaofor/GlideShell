namespace M1.MissionFlow;

// 把 PTP 报文流提炼成手势语义：三指基线、累计位移（上滑为正）、释放瞬间速度。
// DeltaYUp / ReleaseVelocityUp 在「三指抬起的那次 Feed 之后」仍然保留可读，
// 供状态机做松手判定；下次三指按下时才会被覆盖。
internal sealed class GestureEngine
{
    private double? _baseNX, _baseNY;
    private double _smoothNY;
    private readonly Queue<(double t, double y)> _history = new();

    public int ContactCount { get; private set; } = -1;
    public bool IsThreeFinger => ContactCount == 3;

    /// <summary>三指累计上滑位移（归一化，上滑为正）。</summary>
    public double DeltaYUp { get; private set; }

    /// <summary>三指累计横向位移（归一化，向右为正）。</summary>
    public double DeltaX { get; private set; }

    /// <summary>最近 ~120ms 的竖直速度（归一化单位/秒，上滑为正），在三指期间持续更新。</summary>
    public double ReleaseVelocityUp { get; private set; }

    public void Feed(PtpReport r, double nowMs)
    {
        int prev = ContactCount;
        ContactCount = (int)r.ContactCount;
        _lastAnyReportMs = nowMs; // 任意报文都刷新：有效性判断必须基于"新鲜的报文"而非陈旧的触点数

        // 实证（17:51 日志）：报文 cc=3 但解析出的触点常不足 3 个（分包/固件怪癖），
        // 导致位移分支整段跳过、ΔY 冻结 0——上滑"失明"400ms 的真凶。
        // 对策：cc==3 时用"当前可解析出的触点"的平均位置继续追踪（1-2 个触点也是近似真值）。
        if (ContactCount == 3 && r.Contacts.Count >= 1)
        {
            double avgNY = r.Contacts.Average(c => c.NY);
            double avgNX = r.Contacts.Average(c => c.NX);

            // 触点数抖动宽限：500ms 内回到三指 = 同一手势的延续，基线保持不动
            // （稀疏报文模式下间隔可达 100-200ms，80ms 会让每份报文都重置基线，位移永远为 0）
            bool continuing = nowMs - _lastThreeMs < 500;
            _lastThreeMs = nowMs;
            _lastAvgNX = avgNX;
            _lastAvgNY = avgNY;

            double baseNY = !continuing || _baseNY is null ? avgNY : _baseNY.Value;
            double baseNX = !continuing || _baseNX is null ? avgNX : _baseNX.Value;
            _baseNY = baseNY;
            _baseNX = baseNX;

            if (!continuing)
            {
                // 真正的新手势：重置平滑与速度历史
                _smoothNY = avgNY;
                _history.Clear();
            }
            else
            {
                _smoothNY = _smoothNY * 0.5 + avgNY * 0.5; // 轻量去抖，不伤跟手感
            }

            _history.Enqueue((nowMs, _smoothNY));
            while (_history.Count > 2 && nowMs - _history.Peek().t > 120) _history.Dequeue();

            DeltaYUp = baseNY - _smoothNY;
            DeltaX = avgNX - baseNX;

            var first = _history.Peek();
            var last = _history.Last();
            double dt = (last.t - first.t) / 1000.0;
            ReleaseVelocityUp = dt > 0.016 ? (first.y - last.y) / dt : 0; // NY 减小 = 上滑
        }
        // 触点数 != 3：不清基线——宽限期内视为仍按住。
        // 完全松手（cc=0）立即终止宽限：当份报文即可触发松手判定，
        // 否则松开后不再有报文、宽限永不到期（卡在中途的回归就是这么来的）。
        if (r.ContactCount == 0) _lastThreeMs = double.NegativeInfinity;
    }

    /// <summary>
    /// 是否应视为"三指在手"。必须以报文新鲜度为前提：本机触摸板按住静止时会停发报文，
    /// 陈旧的 cc=3 永远不能当作"还按着"（曾导致滑到顶停住后冻结 1.4 秒）。
    /// </summary>
    public bool IsThreeFingerEffective(double nowMs)
        => nowMs - _lastThreeMs < 80
           || (ContactCount == 3 && nowMs - _lastAnyReportMs < 80);

    /// <summary>把基线移到最近一次三指触点的位置：新一轮三指按下时调用，
    /// 否则陈旧基线（如原上滑起点）会让随后的下滑位移全程为正、无法触发收起。</summary>
    public void Rebase()
    {
        _baseNY = _lastAvgNY;
        _baseNX = _lastAvgNX;
        _smoothNY = _lastAvgNY;
        _history.Clear();
        DeltaYUp = 0;
        DeltaX = 0;
        ReleaseVelocityUp = 0;
    }

    public void Reset()
    {
        ContactCount = -1;
        _baseNY = _baseNX = null;
        _history.Clear();
        DeltaYUp = DeltaX = ReleaseVelocityUp = 0;
        _lastThreeMs = double.NegativeInfinity;
    }

    private double _lastThreeMs = double.NegativeInfinity;
    private double _lastAvgNX, _lastAvgNY;
    private double _lastAnyReportMs = double.NegativeInfinity;
}
