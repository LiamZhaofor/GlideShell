namespace GlideShell.Core.Input;

/// <summary>触摸板一个轴：逻辑范围 + 物理长度。Core 内一律用毫米，和设备分辨率无关。</summary>
public sealed record PtpAxis(int LogicalMin, int LogicalMax, double SizeMm)
{
    // PTP 规范要求声明物理尺寸；万一缺失，按常见精确式触摸板的分辨率（约 30 单位/mm）估算
    private const double FallbackUnitsPerMm = 30;

    public static PtpAxis Create(int logicalMin, int logicalMax, double? sizeMm)
        => new(logicalMin, logicalMax, sizeMm is > 0 ? sizeMm.Value : Math.Max(1, logicalMax - logicalMin) / FallbackUnitsPerMm);

    public double ToMm(int logical) => (logical - LogicalMin) * SizeMm / Math.Max(1, LogicalMax - LogicalMin);
}

/// <summary>
/// 触摸板几何与报文格式。坐标原点在左上角，Y 向下增大（与 HID 一致）；「上滑」是 Y 减小。
/// </summary>
/// <param name="ContactSlots">每份报文最多携带的触点数；小于设备最大触点数时为混合模式。</param>
/// <param name="ScanTimeBits">ScanTime 位宽（PTP 规范：16 位，单位 100µs）。</param>
public sealed record PtpGeometry(PtpAxis X, PtpAxis Y, int ContactSlots, int ScanTimeBits = 16);
