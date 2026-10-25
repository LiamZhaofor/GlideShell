using System.Runtime.InteropServices;

namespace GlideShell.Tools.PtpProbe;

// Raw Input / hid.dll / 窗口消息循环的互操作。结构体布局照抄 winuser.h / hidpi.h。
internal static class Native
{
    public const uint RIM_TYPEMOUSE = 0;
    public const uint RIM_TYPEHID = 2;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDI_DEVICEINFO = 0x2000000b;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDEV_REMOVE = 0x00000001;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint Failed = unchecked((uint)-1);

    public const uint HIDP_STATUS_SUCCESS = 0x00110000;

    public const uint WM_INPUT = 0x00FF;
    public const uint WM_APP = 0x8000;
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    public enum HidpReportType { Input = 0, Output = 1, Feature = 2 }

    // ---------- 设备枚举与描述符 ----------

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

    // ---------- 报文解析 ----------

    [DllImport("hid.dll")]
    public static extern uint HidP_GetUsageValue(HidpReportType type, ushort usagePage, ushort linkCollection, ushort usage,
        out uint value, IntPtr preparsed, IntPtr report, uint reportLength);

    /// <summary>取某集合内当前「按下」的按钮 usage 列表（TipSwitch / Confidence 就是按钮位）。</summary>
    [DllImport("hid.dll")]
    public static extern uint HidP_GetUsages(HidpReportType type, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsed, IntPtr report, uint reportLength);

    // ---------- Raw Input 注册与读取 ----------

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    // ---------- 消息窗口 ----------

    public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? moduleName);

    // ---------- 自检：注入一次鼠标移动 ----------

    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, [In] INPUT[] inputs, int size);

    // ---------- 结构体 ----------

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
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    /// <summary>RAWINPUT 联合体里的 RAWHID 头部；HID 报文字节紧随其后（dwSizeHid × dwCount）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWHID
    {
        public uint dwSizeHid;
        public uint dwCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    // INPUT = type + 联合体；联合体最大成员 MOUSEINPUT，含指针字段，所以 mi 按 8 字节对齐，x64 下总长 40
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
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
