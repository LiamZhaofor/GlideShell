namespace GlideShell.Platform.Input;

/// <summary>HID 单位换算（纯函数）。</summary>
public static class HidUnits
{
    /// <summary>UnitsExp 是 4 位补码的十进制指数（0x0..0x7 = 0..7，0x8..0xF = −8..−1）。</summary>
    public static int DecodeExponent(uint raw)
    {
        int n = (int)(raw & 0xF);
        return n >= 8 ? n - 16 : n;
    }

    /// <summary>
    /// 物理范围 → 毫米。Units 低 4 位是单位制（1 = SI 线性：cm，3 = 英制线性：inch），次 4 位是长度的幂；
    /// 不是一次幂长度或范围无效时返回 null。
    /// </summary>
    public static double? LengthMm(int physicalMin, int physicalMax, uint units, uint unitsExp)
    {
        uint system = units & 0xF;
        uint lengthPower = (units >> 4) & 0xF;
        if (lengthPower != 1 || physicalMax <= physicalMin) return null;
        double mmPerUnit = system switch { 1 => 10.0, 3 => 25.4, _ => double.NaN };
        if (double.IsNaN(mmPerUnit)) return null;
        return (physicalMax - physicalMin) * Math.Pow(10, DecodeExponent(unitsExp)) * mmPerUnit;
    }
}
