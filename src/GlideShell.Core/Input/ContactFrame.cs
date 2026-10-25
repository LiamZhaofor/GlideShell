namespace GlideShell.Core.Input;

/// <summary>一帧里的一个触点（毫米坐标）。<see cref="Tip"/>=false 是该触点的抬起报告。</summary>
public readonly record struct Contact(int Id, double XMm, double YMm, bool Tip, bool Confidence);

/// <summary>
/// 触摸板一帧：同一次扫描的全部触点。由 <see cref="FrameAssembler"/> 复用同一个实例填充（热路径零分配），
/// 只在回调期间有效；需要保留时用 <see cref="CopyFrom"/> 拷贝。
/// </summary>
public sealed class ContactFrame
{
    public const int MaxContacts = 10;

    private readonly Contact[] _contacts = new Contact[MaxContacts];

    /// <summary>设备时间（µs，由 ScanTime 展开），用于速度计算。</summary>
    public long DeviceTimeUs { get; private set; }

    /// <summary>主机时间（µs，QPC），用于和渲染时间轴对齐。</summary>
    public long HostTimeUs { get; private set; }

    /// <summary>物理按键位（bit0 = 按键 1）。</summary>
    public int Buttons { get; private set; }

    /// <summary>混合模式下没凑齐声明的触点数就被下一帧打断。</summary>
    public bool Partial { get; private set; }

    public int Count { get; private set; }

    public ReadOnlySpan<Contact> Contacts => _contacts.AsSpan(0, Count);

    /// <summary>按下中的触点数（TipSwitch=1）。</summary>
    public int TouchingCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Count; i++)
                if (_contacts[i].Tip) n++;
            return n;
        }
    }

    public void CopyFrom(ContactFrame other)
    {
        Begin(other.DeviceTimeUs, other.HostTimeUs, other.Buttons);
        other.Contacts.CopyTo(_contacts);
        Count = other.Count;
        Partial = other.Partial;
    }

    /// <summary>测试与回放用：直接构造一帧。</summary>
    public static ContactFrame Create(long deviceTimeUs, long hostTimeUs, ReadOnlySpan<Contact> contacts, int buttons = 0)
    {
        var f = new ContactFrame();
        f.Begin(deviceTimeUs, hostTimeUs, buttons);
        foreach (var c in contacts) f.Add(c);
        return f;
    }

    internal void Begin(long deviceTimeUs, long hostTimeUs, int buttons)
    {
        DeviceTimeUs = deviceTimeUs;
        HostTimeUs = hostTimeUs;
        Buttons = buttons;
        Partial = false;
        Count = 0;
    }

    internal bool Add(in Contact c)
    {
        if (Count >= MaxContacts) return false;
        _contacts[Count++] = c;
        return true;
    }

    internal void MarkPartial() => Partial = true;
}
