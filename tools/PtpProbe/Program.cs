using System.Runtime.InteropServices;
using System.Text;
using static GlideShell.Tools.PtpProbe.Native;

// 只读探针：枚举精确式触摸板（PTP），打印 HID 报告描述符里与手势识别相关的部分：
// 每份报文的触点槽数（判断并行 / 混合报告模式）、每槽是否带 TipSwitch / Confidence、
// X/Y 逻辑范围与物理尺寸（mm）、ScanTime 单位。不打开设备、不读写任何设置。
Console.OutputEncoding = Encoding.UTF8;

int found = 0;
foreach (var device in EnumerateHidDevices())
{
    if (!TryGetHidInfo(device, out var info) || info.usUsagePage != 0x0D || info.usUsage != 0x05) continue;
    found++;
    Console.WriteLine($"== PTP  VID={info.dwVendorId:X4}  PID={info.dwProductId:X4}");
    Console.WriteLine($"   {GetDeviceName(device)}");

    IntPtr preparsed = GetPreparsedData(device);
    if (preparsed == IntPtr.Zero)
    {
        Console.WriteLine("   读取 preparsed data 失败");
        continue;
    }
    try
    {
        PrintDescriptor(preparsed);
    }
    finally
    {
        Marshal.FreeHGlobal(preparsed);
    }
    Console.WriteLine();
}

if (found == 0) Console.WriteLine("未找到精确式触摸板（UsagePage 0x0D / Usage 0x05）。");
return found == 0 ? 1 : 0;

static void PrintDescriptor(IntPtr preparsed)
{
    HidP_GetCaps(preparsed, out var caps);
    Console.WriteLine($"   InputReportLen={caps.InputReportByteLength}  LinkCollections={caps.NumberLinkCollectionNodes}  " +
                      $"ValueCaps={caps.NumberInputValueCaps}  ButtonCaps={caps.NumberInputButtonCaps}");

    var values = GetValueCaps(preparsed, HidpReportType.Input, caps.NumberInputValueCaps);
    var buttons = GetButtonCaps(preparsed, HidpReportType.Input, caps.NumberInputButtonCaps);
    var features = GetValueCaps(preparsed, HidpReportType.Feature, caps.NumberFeatureValueCaps);

    var slots = values.Where(v => v.UsagePage == 0x01 && v.UsageMin == 0x30 && v.LinkCollection >= 1)
                      .Select(v => v.LinkCollection).Distinct().Order().ToList();
    Console.WriteLine($"   触点槽数 = {slots.Count}（每份报文最多携带的触点数；小于设备最大触点数即为混合模式，一帧会拆成多份报文）");

    foreach (var slot in slots)
    {
        bool tip = buttons.Any(b => b.LinkCollection == slot && b.UsagePage == 0x0D && Covers(b, 0x42));
        bool confidence = buttons.Any(b => b.LinkCollection == slot && b.UsagePage == 0x0D && Covers(b, 0x47));
        var x = values.First(v => v.LinkCollection == slot && v.UsagePage == 0x01 && v.UsageMin == 0x30);
        var y = values.FirstOrDefault(v => v.LinkCollection == slot && v.UsagePage == 0x01 && v.UsageMin == 0x31);
        Console.WriteLine($"   槽 L{slot}: TipSwitch={(tip ? "有" : "无")}  Confidence={(confidence ? "有" : "无")}  " +
                          $"X[{x.LogicalMin},{x.LogicalMax}] {FormatMm(x)}  Y[{y.LogicalMin},{y.LogicalMax}] {FormatMm(y)}");
    }

    var scan = values.FirstOrDefault(v => v.LinkCollection == 0 && v.UsagePage == 0x0D && v.UsageMin == 0x56);
    Console.WriteLine(scan.BitSize > 0
        ? $"   ScanTime: {scan.BitSize} 位，单位指数 {DecodeUnitExponent(scan.UnitsExp)}（PTP 规范为 100µs，按 {1 << scan.BitSize} 回绕）"
        : "   ScanTime: 未声明");

    bool hasContactCount = values.Any(v => v.LinkCollection == 0 && v.UsagePage == 0x0D && v.UsageMin == 0x54);
    bool hasMaxFeature = features.Any(v => v.UsagePage == 0x0D && v.UsageMin == 0x55);
    Console.WriteLine($"   ContactCount(0D/54)={(hasContactCount ? "有" : "无")}  ContactCountMaximum 特性报告(0D/55)={(hasMaxFeature ? "有" : "无")}");

    Console.WriteLine("   -- 全部输入 value caps（page/usage @集合 : 逻辑范围, 位宽 x 个数）");
    foreach (var v in values)
        Console.WriteLine($"      {v.UsagePage:X2}/{v.UsageMin:X2} @L{v.LinkCollection} : [{v.LogicalMin},{v.LogicalMax}]  {v.BitSize}b x{v.ReportCount}  rid={v.ReportID}");
    Console.WriteLine("   -- 全部输入 button caps（page/usage 范围 @集合）");
    foreach (var b in buttons)
        Console.WriteLine($"      {b.UsagePage:X2}/{b.UsageMin:X2}-{(b.IsRange ? b.UsageMax : b.UsageMin):X2} @L{b.LinkCollection}  rid={b.ReportID}");
}

// 非 Range 的 button cap 只有单个 usage（存在 UsageMin 位置），UsageMax 位置是保留字段
static bool Covers(HIDP_BUTTON_CAPS b, ushort usage)
    => b.IsRange ? b.UsageMin <= usage && usage <= b.UsageMax : b.UsageMin == usage;

// HID 物理范围 → 毫米。Units 低 4 位是单位制（1 = SI 线性：cm，3 = 英制线性：inch），
// 次 4 位是长度的幂；UnitsExp 是 4 位补码的十进制指数。
static string FormatMm(HIDP_VALUE_CAPS v)
{
    uint system = v.Units & 0xF;
    uint lengthPower = (v.Units >> 4) & 0xF;
    if (lengthPower != 1 || v.PhysicalMax <= v.PhysicalMin) return "(物理尺寸未知)";
    double mmPerUnit = system switch { 1 => 10.0, 3 => 25.4, _ => double.NaN };
    if (double.IsNaN(mmPerUnit)) return "(物理尺寸未知)";
    double mm = (v.PhysicalMax - v.PhysicalMin) * Math.Pow(10, DecodeUnitExponent(v.UnitsExp)) * mmPerUnit;
    return $"≈{mm:F1}mm";
}

static int DecodeUnitExponent(uint raw)
{
    int n = (int)(raw & 0xF);
    return n >= 8 ? n - 16 : n;
}
