using GlideShell.Core.Input;

namespace GlideShell.Tests.Input;

public class ScanClockTests
{
    [Fact]
    public void Unwraps_16_bit_scan_time_across_rollover()
    {
        var clock = new ScanClock();
        long t0 = clock.Advance(65530, hostUs: 1_000_000);
        long t1 = clock.Advance(10, hostUs: 1_001_600); // 65530 → 10 跨过回绕：16 个 tick = 1.6ms

        Assert.Equal(1_600, t1 - t0);
    }

    [Fact]
    public void Uses_host_time_to_count_laps_after_long_idle()
    {
        var clock = new ScanClock();
        long t0 = clock.Advance(1000, hostUs: 0);
        // 空闲 10 秒：ScanTime 已转过一圈多（周期 6.5536s），只看模运算会少算一圈
        long t1 = clock.Advance((1000 + 100_000) % 65536, hostUs: 10_000_000);

        Assert.InRange(t1 - t0, 9_900_000, 10_100_000);
    }

    [Fact]
    public void Falls_back_to_host_time_when_device_resets_scan_time()
    {
        var clock = new ScanClock();
        long t0 = clock.Advance(40_000, hostUs: 0);
        long t1 = clock.Advance(0, hostUs: 1_000_000); // 设备在新一轮触摸时清零

        Assert.Equal(1_000_000, t1 - t0);
    }
}

public class FrameAssemblerTests
{
    private static readonly PtpGeometry Geometry = new(new PtpAxis(0, 4010, 124.2), new PtpAxis(0, 2443, 75.7), ContactSlots: 5);

    private static RawContact C(int id, int x, int y, bool tip = true) => new(id, tip, true, x, y);

    [Fact]
    public void Parallel_mode_report_becomes_one_frame_in_millimetres()
    {
        var frames = new List<ContactFrame>();
        var asm = new FrameAssembler(Geometry, f => { var copy = new ContactFrame(); copy.CopyFrom(f); frames.Add(copy); });

        asm.Push(1000, 100, 3, 0, [C(0, 0, 0), C(1, 4010, 2443), C(2, 2005, 1221), C(0, 9, 9), C(0, 9, 9)]);

        var frame = Assert.Single(frames);
        Assert.Equal(3, frame.Count);
        Assert.False(frame.Partial);
        Assert.Equal(124.2, frame.Contacts[1].XMm, 6);
        Assert.Equal(75.7, frame.Contacts[1].YMm, 6);
        Assert.Equal(3, frame.TouchingCount);
    }

    [Fact]
    public void Hybrid_mode_reports_with_same_scan_time_are_merged()
    {
        var geometry = Geometry with { ContactSlots = 2 };
        var frames = new List<ContactFrame>();
        var asm = new FrameAssembler(geometry, f => { var copy = new ContactFrame(); copy.CopyFrom(f); frames.Add(copy); });

        asm.Push(1000, 500, 3, 0, [C(0, 10, 10), C(1, 20, 20)]); // 第一份声明 3 个触点，只带 2 个
        Assert.Empty(frames);
        asm.Push(1100, 500, 0, 0, [C(2, 30, 30), C(0, 0, 0)]); // 后续报文 cc=0、同一 ScanTime

        var frame = Assert.Single(frames);
        Assert.Equal(3, frame.Count);
        Assert.False(frame.Partial);
        Assert.Equal(new[] { 0, 1, 2 }, frame.Contacts.ToArray().Select(c => c.Id));
    }

    [Fact]
    public void Incomplete_hybrid_frame_is_flushed_as_partial_when_next_frame_starts()
    {
        var geometry = Geometry with { ContactSlots = 2 };
        var frames = new List<ContactFrame>();
        var asm = new FrameAssembler(geometry, f => { var copy = new ContactFrame(); copy.CopyFrom(f); frames.Add(copy); });

        asm.Push(1000, 500, 3, 0, [C(0, 10, 10), C(1, 20, 20)]);
        asm.Push(5000, 540, 2, 0, [C(0, 11, 11), C(1, 21, 21)]);

        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].Partial);
        Assert.Equal(2, frames[0].Count);
        Assert.False(frames[1].Partial);
    }

    [Fact]
    public void Zero_count_report_is_an_empty_frame()
    {
        var frames = new List<ContactFrame>();
        var asm = new FrameAssembler(Geometry, f => { var copy = new ContactFrame(); copy.CopyFrom(f); frames.Add(copy); });

        asm.Push(1000, 100, 0, 1, [C(0, 0, 0, tip: false)]);

        var frame = Assert.Single(frames);
        Assert.Equal(0, frame.Count);
        Assert.Equal(1, frame.Buttons);
    }

    [Fact]
    public void Device_time_comes_from_scan_time_not_arrival_time()
    {
        var times = new List<long>();
        var asm = new FrameAssembler(Geometry, f => times.Add(f.DeviceTimeUs));

        // 两份报文成批到达（到达时刻相同），但硬件采样相隔 4ms
        asm.Push(10_000, 100, 1, 0, [C(0, 0, 0)]);
        asm.Push(10_000, 140, 1, 0, [C(0, 0, 0)]);

        Assert.Equal(4_000, times[1] - times[0]);
    }
}
