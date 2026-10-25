using GlideShell.Platform.Input;

namespace GlideShell.Tests.Platform;

public class HidUnitsTests
{
    [Theory]
    [InlineData(0x0u, 0)]
    [InlineData(0x7u, 7)]
    [InlineData(0x8u, -8)]
    [InlineData(0xDu, -3)]
    [InlineData(0xCu, -4)]
    public void Decodes_four_bit_twos_complement_exponent(uint raw, int expected)
        => Assert.Equal(expected, HidUnits.DecodeExponent(raw));

    [Fact]
    public void Converts_centimetre_physical_range_to_millimetres()
    {
        // 本机 ASUS 触摸板的 X 轴：物理 0..12420，单位 0x11（SI 线性 cm），指数 0xD（10^-3）→ 124.2mm
        Assert.Equal(124.2, HidUnits.LengthMm(0, 12420, 0x11, 0xD)!.Value, 9);
    }

    [Fact]
    public void Converts_inch_physical_range_to_millimetres()
        => Assert.Equal(25.4, HidUnits.LengthMm(0, 100, 0x13, 0xE)!.Value, 9); // 100 × 10^-2 inch

    [Fact]
    public void Rejects_non_length_units_and_empty_ranges()
    {
        Assert.Null(HidUnits.LengthMm(0, 100, 0x1001, 0xC)); // 时间单位
        Assert.Null(HidUnits.LengthMm(5, 5, 0x11, 0xD));
    }
}

/// <summary>依赖本机硬件的冒烟测试：没有精确式触摸板时跳过。</summary>
public class PtpHardwareTests
{
    [Fact]
    public void Enumerated_touchpads_have_a_sane_geometry()
    {
        var devices = PtpDevice.EnumerateAll();
        try
        {
            if (devices.Count == 0) Assert.Skip("本机没有精确式触摸板");
            foreach (var d in devices)
            {
                Assert.InRange(d.Geometry.ContactSlots, 1, 10);
                Assert.InRange(d.Geometry.X.SizeMm, 40, 300);
                Assert.InRange(d.Geometry.Y.SizeMm, 25, 200);
                Assert.True(d.Geometry.X.LogicalMax > d.Geometry.X.LogicalMin);
            }
        }
        finally
        {
            foreach (var d in devices) d.Dispose();
        }
    }

    [Fact]
    public void Input_thread_can_start_and_stop_repeatedly()
    {
        for (int i = 0; i < 2; i++)
        {
            using var input = new PtpInputThread(_ => { });
            input.Start();
        }
    }
}
