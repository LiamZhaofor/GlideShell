namespace GlideShell.Core.Input;

/// <summary>
/// 把 PTP 报文拼成完整的触点帧，兼容两种报告模式：
/// 并行模式一份报文就是一帧；混合模式一帧拆成多份报文，只有第一份的 ContactCount 有效（后续为 0），
/// 它们共享同一个 ScanTime。逐份调用 <see cref="Push"/>，凑齐一帧就回调。
/// </summary>
public sealed class FrameAssembler
{
    private readonly PtpGeometry _geometry;
    private readonly ScanClock _clock;
    private readonly Action<ContactFrame> _onFrame;
    private readonly ContactFrame _frame = new();
    private bool _pending;
    private int _expected;
    private int _pendingScan;

    public FrameAssembler(PtpGeometry geometry, Action<ContactFrame> onFrame)
    {
        _geometry = geometry;
        _clock = new ScanClock(geometry.ScanTimeBits);
        _onFrame = onFrame;
    }

    /// <param name="hostUs">报文到达的主机时间（µs）。</param>
    /// <param name="slots">报文里的全部触点槽（原样，包括 ContactCount 之外的槽）。</param>
    public void Push(long hostUs, int scanTime, int contactCount, int buttons, ReadOnlySpan<RawContact> slots)
    {
        if (contactCount > 0)
        {
            if (_pending) Flush(partial: true);
            Start(hostUs, scanTime, buttons, contactCount);
            Append(slots, Math.Min(contactCount, slots.Length));
        }
        else if (_pending && scanTime == _pendingScan)
        {
            // 混合模式的后续报文：接着填剩余触点
            Append(slots, Math.Min(_expected - _frame.Count, slots.Length));
        }
        else
        {
            // 真正的「无触点」报文（部分设备在全部抬起后会发一份）
            if (_pending) Flush(partial: true);
            Start(hostUs, scanTime, buttons, 0);
        }

        if (_pending && _frame.Count >= _expected) Flush(partial: false);
    }

    /// <summary>设备移除、会话切换等场合丢弃拼了一半的帧并重置时钟。</summary>
    public void Reset()
    {
        _pending = false;
        _clock.Reset();
    }

    private void Start(long hostUs, int scanTime, int buttons, int expected)
    {
        _frame.Begin(_clock.Advance(scanTime, hostUs), hostUs, buttons);
        _pending = true;
        _expected = Math.Min(expected, ContactFrame.MaxContacts);
        _pendingScan = scanTime;
    }

    private void Append(ReadOnlySpan<RawContact> slots, int take)
    {
        for (int i = 0; i < take; i++)
        {
            var s = slots[i];
            _frame.Add(new Contact(s.Id, _geometry.X.ToMm(s.X), _geometry.Y.ToMm(s.Y), s.Tip, s.Confidence));
        }
    }

    private void Flush(bool partial)
    {
        _pending = false;
        if (partial) _frame.MarkPartial();
        _onFrame(_frame);
    }
}
