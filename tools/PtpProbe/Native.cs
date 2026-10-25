using System.Runtime.InteropServices;

namespace GlideShell.Tools.PtpProbe;

// Raw Input 设备枚举 + hid.dll 描述符查询。结构体布局照抄 winuser.h / hidpi.h。
internal static class Native
{
    private const uint RIM_TYPEHID = 2;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDI_DEVICEINFO = 0x2000000b;
    private const uint Failed = unchecked((uint)-1);

    public enum HidpReportType { Input = 0, Output = 1, Feature = 2 }

    public static IEnumerable<IntPtr> EnumerateHidDevices()
    {
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, size) == Failed || count == 0) yield break;
        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, size) == Failed) yield break;
        foreach (var d in list)
            if (d.dwType == RIM_TYPEHID) yield return d.hDevice;
    }

    public static bool TryGetHidInfo(IntPtr device, out RID_DEVICE_INFO_HID hid)
    {
        var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
        uint size = info.cbSize;
        bool ok = GetRawInputDeviceInfo(device, RIDI_DEVICEINFO, ref info, ref size) != Failed;
        hid = info.hid;
        return ok;
    }

    public static string GetDeviceName(IntPtr device)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0) return "";
        IntPtr buffer = Marshal.AllocHGlobal((int)chars * sizeof(char));
        try
        {
            return GetRawInputDeviceInfo(device, RIDI_DEVICENAME, buffer, ref chars) == Failed
                ? ""
                : Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>返回的内存由调用方 <see cref="Marshal.FreeHGlobal"/>；失败返回 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr GetPreparsedData(IntPtr device)
    {
        uint size = 0;
        if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size) != 0 || size == 0) return IntPtr.Zero;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, buffer, ref size) != size)
        {
            Marshal.FreeHGlobal(buffer);
            return IntPtr.Zero;
        }
        return buffer;
    }

    public static HIDP_VALUE_CAPS[] GetValueCaps(IntPtr preparsed, HidpReportType type, ushort count)
    {
        var caps = new HIDP_VALUE_CAPS[count];
        if (count > 0) HidP_GetValueCaps(type, caps, ref count, preparsed);
        return caps[..count];
    }

    public static HIDP_BUTTON_CAPS[] GetButtonCaps(IntPtr preparsed, HidpReportType type, ushort count)
    {
        var caps = new HIDP_BUTTON_CAPS[count];
        if (count > 0) HidP_GetButtonCaps(type, caps, ref count, preparsed);
        return caps[..count];
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, ref RID_DEVICE_INFO data, ref uint size);

    [DllImport("hid.dll")]
    public static extern uint HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

    [DllImport("hid.dll")]
    private static extern uint HidP_GetValueCaps(HidpReportType type, [Out] HIDP_VALUE_CAPS[] caps, ref ushort length, IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern uint HidP_GetButtonCaps(HidpReportType type, [Out] HIDP_BUTTON_CAPS[] caps, ref ushort length, IntPtr preparsed);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    // RID_DEVICE_INFO 是 dwType + 联合体；联合体中最大的成员是 RID_DEVICE_INFO_KEYBOARD（24 字节），
    // 这里只用 HID 分支，用 Size 撑满整个结构，避免 cbSize 偏小导致调用失败。
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    public struct RID_DEVICE_INFO
    {
        public uint cbSize;
        public uint dwType;
        public RID_DEVICE_INFO_HID hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId;
        public uint dwProductId;
        public uint dwVersionNumber;
        public ushort usUsagePage;
        public ushort usUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        [MarshalAs(UnmanagedType.U1)] public bool IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull;
        public byte Reserved;
        public ushort BitSize, ReportCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public ushort[] Reserved2;
        public uint UnitsExp, Units;
        public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        // 联合体：IsRange=false 时 UsageMin 即 Usage
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        [MarshalAs(UnmanagedType.U1)] public bool IsRange, IsStringRange, IsDesignatorRange, IsAbsolute;
        public ushort ReportCount, Reserved2;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] public uint[] Reserved;
        // 联合体：IsRange=false 时 UsageMin 即 Usage、UsageMax 为保留位，这里统一按 Range 读
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }
}
