using System.Runtime.InteropServices;

namespace M1.MissionFlow;

// PTP（Windows Precision Touchpad）原始报文解析。
// 解析路线改编自 ClementGre/ThreeFingerDragOnWindows（MIT）的 TouchpadHelper.cs，
// 其源头是 emoacht/RawInput.Touchpad（MIT）。
// 思路：RegisterRawInputDevices 订阅 Digitizer/TouchPad (0x0D/0x05) 的原始输入，
// WM_INPUT 到达后取 RAWHID 载荷，用 hid.dll 的 HidP_* 系列按 preparsed data
// 逐字段取值（Scan Time 0x0D/0x56、Contact Count 0x0D/0x54、
// Contact Id 0x0D/0x51、X 0x01/0x30、Y 0x01/0x31），不假设固定位布局，
// 因此兼容各 OEM 的报告描述符差异。

public sealed record TouchpadDevice(IntPtr Handle, string DevicePath, uint VendorId, uint ProductId);

public sealed record PtpContact(int Id, int X, int Y, double NX, double NY);

public sealed record PtpReport(uint ContactCount, uint ScanTime, IReadOnlyList<PtpContact> Contacts);

public static class PtpRawInput
{
    public const int WM_INPUT = 0x00FF;
    public const int WM_INPUT_DEVICE_CHANGE = 0x00FE;

    private const uint RIM_TYPEHID = 2;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDI_DEVICEINFO = 0x2000000b;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_DEVNOTIFY = 0x00002000;
    private const uint HIDP_STATUS_SUCCESS = 0x00110000;

    // 触摸板逻辑坐标范围（来自 X/Y 的 HID Logical Min/Max），用于归一化和画布纵横比
    public static bool TryGetLogicalBounds(IntPtr hDevice, out int xMin, out int xMax, out int yMin, out int yMax)
    {
        xMin = xMax = yMin = yMax = 0;
        if (!_contexts.TryGetValue(hDevice, out var ctx)) return false;
        xMin = ctx.XMin; xMax = ctx.XMax; yMin = ctx.YMin; yMax = ctx.YMax;
        return true;
    }

    public static bool RegisterInput(IntPtr hwndTarget)
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = 0x000D,
            usUsage = 0x0005,
            dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, // 后台/前台都能收到
            hwndTarget = hwndTarget
        };
        return RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    public static List<TouchpadDevice> EnumerateTouchpads()
    {
        var result = new List<TouchpadDevice>();
        uint count = 0;
        var listSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, listSize) != 0) return result;
        if (count == 0) return result;

        var devices = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(devices, ref count, listSize) != count) return result;

        foreach (var dev in devices)
        {
            if (dev.dwType != RIM_TYPEHID) continue;
            uint infoSize = 0;
            if (GetRawInputDeviceInfo(dev.hDevice, RIDI_DEVICEINFO, IntPtr.Zero, ref infoSize) != 0) continue;
            var info = new RID_DEVICE_INFO { cbSize = infoSize };
            if (GetRawInputDeviceInfo(dev.hDevice, RIDI_DEVICEINFO, ref info, ref infoSize) == unchecked((uint)-1)) continue;
            if (info.hid.usUsagePage != 0x000D || info.hid.usUsage != 0x0005) continue;

            string path = "";
            uint nameSize = 0;
            if (GetRawInputDeviceInfo(dev.hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref nameSize) != unchecked((uint)-1) && nameSize > 0)
            {
                var ptr = Marshal.AllocHGlobal((int)nameSize);
                try
                {
                    if (GetRawInputDeviceInfo(dev.hDevice, RIDI_DEVICENAME, ptr, ref nameSize) != unchecked((uint)-1))
                        path = Marshal.PtrToStringAnsi(ptr) ?? "";
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }

            result.Add(new TouchpadDevice(dev.hDevice, path, info.hid.dwVendorId, info.hid.dwProductId));
        }
        return result;
    }

    /// <summary>解析一条 WM_INPUT 的 lParam。失败返回 null（并递增 FailCount 便于观察 quirk 设备）。</summary>
    public static PtpReport? TryParse(IntPtr lParam)
    {
        uint rawInputSize = 0;
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref rawInputSize, headerSize) != 0)
        {
            FailCount++;
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)rawInputSize);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref rawInputSize, headerSize) != rawInputSize)
            {
                FailCount++;
                return null;
            }

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEHID)
            {
                FailCount++;
                return null;
            }
            LastDevice = header.hDevice;

            // RAWHID 紧跟在头部之后；dwSizeHid*dwCount 为 HID 报文总长
            var rawHid = Marshal.PtrToStructure<RAWHID>(buffer + (int)headerSize);
            int hidLen = (int)(rawHid.dwSizeHid * rawHid.dwCount);
            if (hidLen <= 0 || rawHid.dwSizeHid == 0)
            {
                FailCount++;
                return null;
            }
            int hidOffset = (int)rawInputSize - hidLen;

            var ctx = GetOrCreateContext(header.hDevice);
            if (ctx is null)
            {
                FailCount++;
                return null;
            }

            // dwCount>1 表示一次 WM_INPUT 打包了多份报告，逐片解析取最后一份
            PtpReport? last = null;
            var reportBytes = new byte[hidLen];
            Marshal.Copy(buffer + hidOffset, reportBytes, 0, hidLen);
            var reportPtr = Marshal.AllocHGlobal(hidLen);
            try
            {
                Marshal.Copy(reportBytes, 0, reportPtr, hidLen);
                for (int i = 0; i < rawHid.dwCount; i++)
                {
                    var r = ParseOneReport(ctx, reportPtr + (int)(rawHid.dwSizeHid * i), (int)rawHid.dwSizeHid);
                    if (r is not null) last = r;
                }
            }
            finally { Marshal.FreeHGlobal(reportPtr); }

            if (last is null) FailCount++;
            return last;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static uint FailCount { get; private set; }

    /// <summary>最近一次成功解析对应的设备句柄（用于查询逻辑范围等）。</summary>
    public static IntPtr LastDevice { get; private set; }

    // ---------- 内部：设备解析上下文（preparsed data 只取一次并缓存） ----------

    private static readonly Dictionary<IntPtr, DeviceContext> _contexts = new();

    private sealed class DeviceContext
    {
        public IntPtr PreparsedData;
        public List<ValueCapInfo> Caps = new();
        public int XMin, XMax, YMin, YMax;
        public bool HasXYBounds;
    }

    private sealed record ValueCapInfo(ushort UsagePage, ushort Usage, ushort LinkCollection, int LogicalMin, int LogicalMax);

    private static DeviceContext? GetOrCreateContext(IntPtr hDevice)
    {
        if (_contexts.TryGetValue(hDevice, out var cached)) return cached;

        uint preparsedSize = 0;
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref preparsedSize) != 0) return null;
        var preparsed = Marshal.AllocHGlobal((int)preparsedSize);
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, preparsed, ref preparsedSize) != preparsedSize)
        {
            Marshal.FreeHGlobal(preparsed);
            return null;
        }

        if (HidP_GetCaps(preparsed, out var caps) != HIDP_STATUS_SUCCESS)
        {
            Marshal.FreeHGlobal(preparsed);
            return null;
        }
        // 双保险：确认这台设备就是 PTP
        if (caps.UsagePage != 0x000D || caps.Usage != 0x0005)
        {
            Marshal.FreeHGlobal(preparsed);
            return null;
        }

        ushort capsLen = caps.NumberInputValueCaps;
        var valueCaps = new HIDP_VALUE_CAPS[capsLen];
        if (HidP_GetValueCaps(HIDP_REPORT_TYPE.HidP_Input, valueCaps, ref capsLen, preparsed) != HIDP_STATUS_SUCCESS)
        {
            Marshal.FreeHGlobal(preparsed);
            return null;
        }

        var ctx = new DeviceContext { PreparsedData = preparsed };
        foreach (var cap in valueCaps)
        {
            var info = new ValueCapInfo(cap.UsagePage, cap.Usage, cap.LinkCollection, cap.LogicalMin, cap.LogicalMax);
            ctx.Caps.Add(info);

            if (cap.UsagePage == 0x01 && cap.Usage == 0x30 && cap.LinkCollection >= 1)
            {
                ctx.XMin = cap.LogicalMin; ctx.XMax = cap.LogicalMax; ctx.HasXYBounds = true;
            }
            if (cap.UsagePage == 0x01 && cap.Usage == 0x31 && cap.LinkCollection >= 1)
            {
                ctx.YMin = cap.LogicalMin; ctx.YMax = cap.LogicalMax; ctx.HasXYBounds = true;
            }
        }

        _contexts[hDevice] = ctx;
        return ctx;
    }

    private static PtpReport? ParseOneReport(DeviceContext ctx, IntPtr report, int reportLen)
    {
        uint contactCount = 0;
        uint scanTime = 0;

        const int maxContacts = 10;
        var ids = new int[maxContacts];
        var xs = new int[maxContacts];
        var ys = new int[maxContacts];
        var has = new bool[maxContacts];
        int maxIdx = -1;

        foreach (var cap in ctx.Caps)
        {
            if (cap.LinkCollection == 0)
            {
                if (HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input, cap.UsagePage, 0, cap.Usage,
                        out var v, ctx.PreparsedData, report, (uint)reportLen) != HIDP_STATUS_SUCCESS)
                    continue;
                if (cap.UsagePage == 0x0D && cap.Usage == 0x56) scanTime = v;
                else if (cap.UsagePage == 0x0D && cap.Usage == 0x54) contactCount = v;
            }
            else
            {
                int idx = cap.LinkCollection - 1;
                if (idx < 0 || idx >= maxContacts) continue;
                if (HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input, cap.UsagePage, cap.LinkCollection, cap.Usage,
                        out var value, ctx.PreparsedData, report, (uint)reportLen) != HIDP_STATUS_SUCCESS)
                    continue;

                if (idx > maxIdx) maxIdx = idx;
                if (cap.UsagePage == 0x0D && cap.Usage == 0x51) ids[idx] = (int)value;
                else if (cap.UsagePage == 0x01 && cap.Usage == 0x30) xs[idx] = (int)value;
                else if (cap.UsagePage == 0x01 && cap.Usage == 0x31) ys[idx] = (int)value;
                has[idx] = true;
            }
        }

        var contacts = new List<PtpContact>((int)Math.Min(contactCount, maxIdx + 1));
        int take = (int)Math.Min(contactCount, (uint)(maxIdx + 1));
        for (int i = 0; i < take; i++)
        {
            if (!has[i]) continue;
            double nx = ctx.HasXYBounds && ctx.XMax > ctx.XMin ? (xs[i] - ctx.XMin) / (double)(ctx.XMax - ctx.XMin) : xs[i];
            double ny = ctx.HasXYBounds && ctx.YMax > ctx.YMin ? (ys[i] - ctx.YMin) / (double)(ctx.YMax - ctx.YMin) : ys[i];
            contacts.Add(new PtpContact(ids[i], xs[i], ys[i], nx, ny));
        }

        if (contactCount == 0 && contacts.Count == 0 && scanTime == 0) return null;
        return new PtpReport(contactCount, scanTime, contacts);
    }

    // ---------- Win32 interop ----------

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("User32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWHID
    {
        public uint dwSizeHid;
        public uint dwCount;
        public IntPtr bRawData; // 仅用于占位，实际数据在 RAWINPUT 缓冲区尾部
    }

    [DllImport("User32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [DllImport("User32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, ref RID_DEVICE_INFO pData, ref uint pcbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO
    {
        public uint cbSize;
        public uint dwType;
        public RID_DEVICE_INFO_HID hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId;
        public uint dwProductId;
        public uint dwVersionNumber;
        public ushort usUsagePage;
        public ushort usUsage;
    }

    [DllImport("Hid.dll", SetLastError = true)]
    private static extern uint HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

    private enum HIDP_REPORT_TYPE { HidP_Input, HidP_Output, HidP_Feature }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("Hid.dll", CharSet = CharSet.Auto)]
    private static extern uint HidP_GetValueCaps(HIDP_REPORT_TYPE ReportType, [Out] HIDP_VALUE_CAPS[] ValueCaps, ref ushort ValueCapsLength, IntPtr PreparsedData);

    [DllImport("Hid.dll", CharSet = CharSet.Auto)]
    private static extern uint HidP_GetUsageValue(HIDP_REPORT_TYPE ReportType, ushort UsagePage, ushort LinkCollection, ushort Usage, out uint UsageValue, IntPtr PreparsedData, IntPtr Report, uint ReportLength);

    // 布局照抄 hidpi.h（ThreeFingerDragOnWindows 验证过的封送版本）
    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;

        [MarshalAs(UnmanagedType.U1)] public bool IsAlias;

        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;

        [MarshalAs(UnmanagedType.U1)] public bool IsRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;
        [MarshalAs(UnmanagedType.U1)] public bool HasNull;

        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public ushort[] Reserved2;

        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;

        // Range
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;

        // NotRange（单值情形下 usage 存在 UsageMin 里）
        public readonly ushort Usage => UsageMin;
    }
}
