using System.Runtime.InteropServices;
using static GlideShell.Tools.PtpProbe.Native;

namespace GlideShell.Tools.PtpProbe;

internal sealed record AxisInfo(int Min, int Max, double? Mm);

/// <summary>一台精确式触摸板及其报告描述符里与手势相关的部分。持有 preparsed data，用完 Dispose。</summary>
internal sealed class PtpDevice : IDisposable
{
    public required IntPtr Handle { get; init; }
    public required string Path { get; init; }
    public required uint VendorId { get; init; }
    public required uint ProductId { get; init; }
    public required IntPtr Preparsed { get; init; }
    public required HIDP_CAPS Caps { get; init; }
    public required HIDP_VALUE_CAPS[] Values { get; init; }
    public required HIDP_BUTTON_CAPS[] Buttons { get; init; }
    public required HIDP_VALUE_CAPS[] Features { get; init; }

    /// <summary>携带触点的集合编号（L1..Ln），即每份报文的触点槽。</summary>
    public required ushort[] Slots { get; init; }
    public required AxisInfo X { get; init; }
    public required AxisInfo Y { get; init; }

    /// <summary>ScanTime 位宽（PTP 规范为 16 位、100µs）；未声明时为 0。</summary>
    public required int ScanBits { get; init; }

    public static List<PtpDevice> EnumerateAll()
    {
        var result = new List<PtpDevice>();
        foreach (var handle in EnumerateHidDevices())
        {
            if (!TryGetHidInfo(handle, out var info) || info.usUsagePage != 0x0D || info.usUsage != 0x05) continue;
            IntPtr preparsed = GetPreparsedData(handle);
            if (preparsed == IntPtr.Zero) continue;
            if (HidP_GetCaps(preparsed, out var caps) != HIDP_STATUS_SUCCESS)
            {
                Marshal.FreeHGlobal(preparsed);
                continue;
            }

            var values = GetValueCaps(preparsed, HidpReportType.Input, caps.NumberInputValueCaps);
            var slots = values.Where(v => v.UsagePage == 0x01 && v.UsageMin == 0x30 && v.LinkCollection >= 1)
                              .Select(v => v.LinkCollection).Distinct().Order().ToArray();
            var scan = values.FirstOrDefault(v => v.LinkCollection == 0 && v.UsagePage == 0x0D && v.UsageMin == 0x56);

            result.Add(new PtpDevice
            {
                Handle = handle,
                Path = GetDeviceName(handle),
                VendorId = info.dwVendorId,
                ProductId = info.dwProductId,
                Preparsed = preparsed,
                Caps = caps,
                Values = values,
                Buttons = GetButtonCaps(preparsed, HidpReportType.Input, caps.NumberInputButtonCaps),
                Features = GetValueCaps(preparsed, HidpReportType.Feature, caps.NumberFeatureValueCaps),
                Slots = slots,
                X = Axis(values, slots, 0x30),
                Y = Axis(values, slots, 0x31),
                ScanBits = scan.BitSize,
            });
        }
        return result;
    }

    public bool SlotHasButton(ushort slot, ushort usage)
        => Buttons.Any(b => b.LinkCollection == slot && b.UsagePage == 0x0D && Covers(b, usage));

    public void Dispose() => Marshal.FreeHGlobal(Preparsed);

    // 非 Range 的 button cap 只有单个 usage（存在 UsageMin 位置），UsageMax 位置是保留字段
    public static bool Covers(HIDP_BUTTON_CAPS b, ushort usage)
        => b.IsRange ? b.UsageMin <= usage && usage <= b.UsageMax : b.UsageMin == usage;

    public static int DecodeUnitExponent(uint raw)
    {
        int n = (int)(raw & 0xF);
        return n >= 8 ? n - 16 : n;
    }

    private static AxisInfo Axis(HIDP_VALUE_CAPS[] values, ushort[] slots, ushort usage)
    {
        if (slots.Length == 0) return new AxisInfo(0, 0, null);
        var cap = values.First(v => v.LinkCollection == slots[0] && v.UsagePage == 0x01 && v.UsageMin == usage);
        return new AxisInfo(cap.LogicalMin, cap.LogicalMax, PhysicalMm(cap));
    }

    // HID 物理范围 → 毫米。Units 低 4 位是单位制（1 = SI 线性：cm，3 = 英制线性：inch），
    // 次 4 位是长度的幂；UnitsExp 是 4 位补码的十进制指数。
    private static double? PhysicalMm(HIDP_VALUE_CAPS v)
    {
        uint system = v.Units & 0xF;
        uint lengthPower = (v.Units >> 4) & 0xF;
        if (lengthPower != 1 || v.PhysicalMax <= v.PhysicalMin) return null;
        double mmPerUnit = system switch { 1 => 10.0, 3 => 25.4, _ => double.NaN };
        if (double.IsNaN(mmPerUnit)) return null;
        return (v.PhysicalMax - v.PhysicalMin) * Math.Pow(10, DecodeUnitExponent(v.UnitsExp)) * mmPerUnit;
    }
}
