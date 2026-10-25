using System.Runtime.InteropServices;

namespace GlideShell.Platform.Native;

// hid.dll 的报告描述符查询与报文取值。结构体布局照抄 hidpi.h（VALUE/BUTTON_CAPS 各 72 字节，CAPS 64 字节）。
internal static unsafe partial class Hid
{
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    public enum ReportType { Input = 0, Output = 1, Feature = 2 }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed ushort Reserved[17];
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
        public byte IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
        public ushort BitSize, ReportCount;
        public fixed ushort Reserved2[5];
        public uint UnitsExp, Units;
        public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        // 联合体：IsRange=0 时 UsageMin 位置就是 Usage
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute;
        public ushort ReportCount, Reserved2;
        public fixed uint Reserved[9];
        // 联合体：IsRange=0 时 UsageMin 位置就是 Usage，UsageMax 位置是保留字段
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;

        public readonly bool Covers(ushort usage) => IsRange != 0 ? UsageMin <= usage && usage <= UsageMax : UsageMin == usage;
    }

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetCaps(nint preparsed, HIDP_CAPS* caps);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetValueCaps(ReportType type, HIDP_VALUE_CAPS* caps, ushort* length, nint preparsed);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetButtonCaps(ReportType type, HIDP_BUTTON_CAPS* caps, ushort* length, nint preparsed);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetUsageValue(ReportType type, ushort usagePage, ushort linkCollection, ushort usage,
        uint* value, nint preparsed, byte* report, uint reportLength);

    /// <summary>取某集合内当前「按下」的按钮 usage（TipSwitch / Confidence 是按钮位）。</summary>
    [LibraryImport("hid.dll")]
    public static partial int HidP_GetUsages(ReportType type, ushort usagePage, ushort linkCollection,
        ushort* usageList, uint* usageLength, nint preparsed, byte* report, uint reportLength);
}
