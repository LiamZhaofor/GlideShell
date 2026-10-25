using System.Runtime.InteropServices;
using GlideShell.Core.Input;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Input;

/// <summary>一台精确式触摸板：描述符信息 + 报文解析。解析用预分配缓冲，热路径零分配。</summary>
public sealed unsafe class PtpDevice : IDisposable
{
    private const ushort PageGeneric = 0x01, PageButton = 0x09, PageDigitizer = 0x0D;
    private const ushort UsageX = 0x30, UsageY = 0x31, UsageTipSwitch = 0x42, UsageConfidence = 0x47;
    private const ushort UsageContactId = 0x51, UsageContactCount = 0x54, UsageScanTime = 0x56;
    private const int MaxUsages = 32;

    private nint _preparsed;
    private readonly ushort[] _slotCollections;
    private readonly RawContact[] _slots;
    private readonly ushort* _usages;

    private PtpDevice(nint handle, string path, uint vendorId, uint productId, nint preparsed,
        ushort[] slotCollections, PtpGeometry geometry)
    {
        Handle = handle;
        Path = path;
        VendorId = vendorId;
        ProductId = productId;
        _preparsed = preparsed;
        _slotCollections = slotCollections;
        _slots = new RawContact[slotCollections.Length];
        _usages = (ushort*)NativeMemory.Alloc(MaxUsages, sizeof(ushort));
        Geometry = geometry;
    }

    public nint Handle { get; }
    public string Path { get; }
    public uint VendorId { get; }
    public uint ProductId { get; }
    public PtpGeometry Geometry { get; }

    // 最近一次 Parse 的结果
    public int ScanTime { get; private set; }
    public int ContactCount { get; private set; }
    public int Buttons { get; private set; }
    public ReadOnlySpan<RawContact> Slots => _slots;

    /// <summary>枚举当前所有精确式触摸板。</summary>
    public static List<PtpDevice> EnumerateAll()
    {
        var result = new List<PtpDevice>();
        uint count = 0;
        uint size = (uint)sizeof(User32.RAWINPUTDEVICELIST);
        if (User32.GetRawInputDeviceList(null, &count, size) == User32.Failed || count == 0) return result;
        var list = new User32.RAWINPUTDEVICELIST[count];
        fixed (User32.RAWINPUTDEVICELIST* p = list)
        {
            if (User32.GetRawInputDeviceList(p, &count, size) == User32.Failed) return result;
        }
        foreach (var d in list)
        {
            if (d.dwType == User32.RIM_TYPEHID && TryOpen(d.hDevice) is { } dev) result.Add(dev);
        }
        return result;
    }

    /// <summary>打开一个原始输入设备；不是 PTP 或描述符缺少触点集合时返回 null。</summary>
    public static PtpDevice? TryOpen(nint handle)
    {
        var info = new User32.RID_DEVICE_INFO { cbSize = (uint)sizeof(User32.RID_DEVICE_INFO) };
        uint size = info.cbSize;
        if (User32.GetRawInputDeviceInfo(handle, User32.RIDI_DEVICEINFO, &info, &size) == User32.Failed) return null;
        if (info.hid.usUsagePage != PageDigitizer || info.hid.usUsage != 0x05) return null;

        uint ppSize = 0;
        if (User32.GetRawInputDeviceInfo(handle, User32.RIDI_PREPARSEDDATA, null, &ppSize) != 0 || ppSize == 0) return null;
        nint preparsed = (nint)NativeMemory.Alloc(ppSize);
        bool keep = false;
        try
        {
            if (User32.GetRawInputDeviceInfo(handle, User32.RIDI_PREPARSEDDATA, (void*)preparsed, &ppSize) != ppSize) return null;
            Hid.HIDP_CAPS caps;
            if (Hid.HidP_GetCaps(preparsed, &caps) != Hid.HIDP_STATUS_SUCCESS) return null;

            ushort n = caps.NumberInputValueCaps;
            var values = new Hid.HIDP_VALUE_CAPS[n];
            fixed (Hid.HIDP_VALUE_CAPS* pv = values)
            {
                if (n > 0 && Hid.HidP_GetValueCaps(Hid.ReportType.Input, pv, &n, preparsed) != Hid.HIDP_STATUS_SUCCESS) return null;
            }

            var slotCollections = values.Take(n)
                .Where(v => v.UsagePage == PageGeneric && v.UsageMin == UsageX && v.LinkCollection >= 1)
                .Select(v => v.LinkCollection).Distinct().Order().ToArray();
            if (slotCollections.Length == 0) return null;

            var x = values.First(v => v.LinkCollection == slotCollections[0] && v.UsagePage == PageGeneric && v.UsageMin == UsageX);
            var y = values.First(v => v.LinkCollection == slotCollections[0] && v.UsagePage == PageGeneric && v.UsageMin == UsageY);
            var scan = values.Take(n).FirstOrDefault(v => v.LinkCollection == 0 && v.UsagePage == PageDigitizer && v.UsageMin == UsageScanTime);
            var geometry = new PtpGeometry(
                PtpAxis.Create(x.LogicalMin, x.LogicalMax, HidUnits.LengthMm(x.PhysicalMin, x.PhysicalMax, x.Units, x.UnitsExp)),
                PtpAxis.Create(y.LogicalMin, y.LogicalMax, HidUnits.LengthMm(y.PhysicalMin, y.PhysicalMax, y.Units, y.UnitsExp)),
                slotCollections.Length,
                scan.BitSize > 0 ? scan.BitSize : 16);

            keep = true;
            return new PtpDevice(handle, DeviceName(handle), info.hid.dwVendorId, info.hid.dwProductId, preparsed, slotCollections, geometry);
        }
        finally
        {
            if (!keep) NativeMemory.Free((void*)preparsed);
        }
    }

    /// <summary>解析一份 HID 报文（含 report id）。不是触点报文时返回 false。</summary>
    public bool Parse(byte* report, uint length)
    {
        uint cc, scan;
        bool hasCount = Hid.HidP_GetUsageValue(Hid.ReportType.Input, PageDigitizer, 0, UsageContactCount, &cc, _preparsed, report, length) == Hid.HIDP_STATUS_SUCCESS;
        bool hasScan = Hid.HidP_GetUsageValue(Hid.ReportType.Input, PageDigitizer, 0, UsageScanTime, &scan, _preparsed, report, length) == Hid.HIDP_STATUS_SUCCESS;
        if (!hasCount && !hasScan) return false;
        ContactCount = hasCount ? (int)cc : 0;
        ScanTime = hasScan ? (int)scan : 0;

        int buttons = 0;
        uint nu = MaxUsages;
        if (Hid.HidP_GetUsages(Hid.ReportType.Input, PageButton, 0, _usages, &nu, _preparsed, report, length) == Hid.HIDP_STATUS_SUCCESS)
        {
            for (int i = 0; i < nu; i++)
                if (_usages[i] is >= 1 and <= 31) buttons |= 1 << (_usages[i] - 1);
        }
        Buttons = buttons;

        for (int s = 0; s < _slotCollections.Length; s++)
        {
            ushort lc = _slotCollections[s];
            uint id = 0, x = 0, y = 0;
            Hid.HidP_GetUsageValue(Hid.ReportType.Input, PageDigitizer, lc, UsageContactId, &id, _preparsed, report, length);
            Hid.HidP_GetUsageValue(Hid.ReportType.Input, PageGeneric, lc, UsageX, &x, _preparsed, report, length);
            Hid.HidP_GetUsageValue(Hid.ReportType.Input, PageGeneric, lc, UsageY, &y, _preparsed, report, length);
            bool tip = false, confidence = false;
            nu = MaxUsages;
            if (Hid.HidP_GetUsages(Hid.ReportType.Input, PageDigitizer, lc, _usages, &nu, _preparsed, report, length) == Hid.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < nu; i++)
                {
                    if (_usages[i] == UsageTipSwitch) tip = true;
                    else if (_usages[i] == UsageConfidence) confidence = true;
                }
            }
            _slots[s] = new RawContact((int)id, tip, confidence, (int)x, (int)y);
        }
        return true;
    }

    public void Dispose()
    {
        if (_preparsed != 0)
        {
            NativeMemory.Free((void*)_preparsed);
            NativeMemory.Free(_usages);
            _preparsed = 0;
        }
    }

    private static string DeviceName(nint handle)
    {
        uint chars = 0;
        User32.GetRawInputDeviceInfo(handle, User32.RIDI_DEVICENAME, null, &chars);
        if (chars == 0) return "";
        var buffer = new char[chars];
        fixed (char* p = buffer)
        {
            return User32.GetRawInputDeviceInfo(handle, User32.RIDI_DEVICENAME, p, &chars) == User32.Failed
                ? ""
                : new string(p);
        }
    }
}
