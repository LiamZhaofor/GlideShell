using GlideShell.Core.Gestures;
using GlideShell.Core.Input;

namespace GlideShell.Tests.Support;

/// <summary>
/// 手写触摸脚本：按时间顺序生成触点帧（毫米坐标，设备时间 = 主机时间），驱动识别器。
/// 坐标方向与触摸板一致：X 向右、Y 向下；「上滑」= Y 减小。
/// </summary>
internal sealed class TouchScript
{
    private readonly List<ContactFrame> _frames = [];
    private readonly Dictionary<int, (double X, double Y)> _pos = [];

    public long NowUs { get; private set; } = 1_000_000;

    public IReadOnlyList<ContactFrame> Frames => _frames;

    /// <summary>经过 dtMs 后，给出的手指都处于按下状态（未列出的手指不在报文里）。</summary>
    public TouchScript Down(double dtMs, params (int Id, double X, double Y)[] fingers)
    {
        Advance(dtMs);
        var contacts = new List<Contact>();
        foreach (var f in fingers)
        {
            _pos[f.Id] = (f.X, f.Y);
            contacts.Add(new Contact(f.Id, f.X, f.Y, Tip: true, Confidence: true));
        }
        _frames.Add(ContactFrame.Create(NowUs, NowUs, contacts.ToArray()));
        return this;
    }

    /// <summary>经过 dtMs 后，列出的手指发出 TipSwitch=0 抬起报告，其余仍按着的手指照常报告。</summary>
    public TouchScript Lift(double dtMs, int[] lifted, params (int Id, double X, double Y)[] stillDown)
    {
        Advance(dtMs);
        var contacts = new List<Contact>();
        foreach (var id in lifted)
        {
            var p = _pos[id];
            contacts.Add(new Contact(id, p.X, p.Y, Tip: false, Confidence: true));
            _pos.Remove(id);
        }
        foreach (var f in stillDown)
        {
            _pos[f.Id] = (f.X, f.Y);
            contacts.Add(new Contact(f.Id, f.X, f.Y, Tip: true, Confidence: true));
        }
        _frames.Add(ContactFrame.Create(NowUs, NowUs, contacts.ToArray()));
        return this;
    }

    public TouchScript Raw(double dtMs, params Contact[] contacts)
    {
        Advance(dtMs);
        _frames.Add(ContactFrame.Create(NowUs, NowUs, contacts));
        return this;
    }

    /// <summary>只推进时间、不产生报文（模拟手指静止时设备停发）。</summary>
    public TouchScript Silence(double ms)
    {
        Advance(ms);
        return this;
    }

    /// <summary>
    /// 一组手指以 (startX, startY) 为起点、按 (dirX, dirY) 方向匀速移动 distanceMm，每 intervalMs 一帧。
    /// 手指横向间隔 15mm。
    /// </summary>
    public TouchScript Move(int fingers, double startX, double startY, double dirX, double dirY,
        double distanceMm, double durationMs, double intervalMs = 4, int firstId = 0)
    {
        int steps = Math.Max(1, (int)Math.Round(durationMs / intervalMs));
        double len = Math.Sqrt(dirX * dirX + dirY * dirY);
        for (int k = 1; k <= steps; k++)
        {
            double d = distanceMm * k / steps;
            Down(intervalMs, Fingers(fingers, startX + dirX / len * d, startY + dirY / len * d, firstId));
        }
        return this;
    }

    public static (int Id, double X, double Y)[] Fingers(int count, double x, double y, int firstId = 0)
        => Enumerable.Range(0, count).Select(i => (firstId + i, x + 15 * i, y)).ToArray();

    public static int[] Ids(int count, int firstId = 0) => Enumerable.Range(firstId, count).ToArray();

    public List<GestureEvent> Run(GestureSettings? settings = null)
    {
        var events = new List<GestureEvent>();
        var recognizer = new GestureRecognizer(settings ?? new GestureSettings(), events.Add);
        foreach (var f in _frames) recognizer.Process(f);
        return events;
    }

    private void Advance(double ms) => NowUs += (long)Math.Round(ms * 1000);
}
