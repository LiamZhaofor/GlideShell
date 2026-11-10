using System.Globalization;

namespace GlideShell.Core.Configuration;

/// <summary>快捷键里的修饰键。</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// 一个全局快捷键：修饰键 + 一个键（Windows 虚拟键码）。配置里写成文字，例如 <c>Alt+Space</c>、<c>Ctrl+Shift+K</c>、<c>Win+F2</c>，
/// 空串 = 不用。键名不分大小写；不在键名表里的键写成 <c>0x</c> 加十六进制键码。
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint Key)
{
    public static readonly Hotkey None = default;

    public bool IsNone => Key == 0;

    /// <summary>
    /// 能用作全局快捷键：键不是修饰键，且带 Ctrl、Alt 或 Win 之一（只带 Shift 或不带修饰键会挡住正常打字）；F1–F24 可以单独用。
    /// </summary>
    public bool IsValid =>
        Key is > 0 and <= 0xFE && !IsModifierKey(Key) &&
        ((Modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0 || Key is >= 0x70 and <= 0x87);

    /// <summary>修饰键本身（Shift、Ctrl、Alt 及左右各一个，Win 左右）。</summary>
    public static bool IsModifierKey(uint vk) => vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;

    private static readonly (uint Vk, string Name)[] Names =
    [
        (0x20, "Space"), (0x0D, "Enter"), (0x09, "Tab"), (0x08, "Backspace"), (0x1B, "Esc"),
        (0x2D, "Insert"), (0x2E, "Delete"), (0x24, "Home"), (0x23, "End"), (0x21, "PageUp"), (0x22, "PageDown"),
        (0x25, "Left"), (0x26, "Up"), (0x27, "Right"), (0x28, "Down"), (0x2C, "PrintScreen"), (0x13, "Pause"),
        (0x6A, "NumMultiply"), (0x6B, "NumAdd"), (0x6D, "NumSubtract"), (0x6E, "NumDecimal"), (0x6F, "NumDivide"),
        (0xBA, ";"), (0xBB, "="), (0xBC, ","), (0xBD, "-"), (0xBE, "."), (0xBF, "/"), (0xC0, "`"),
        (0xDB, "["), (0xDC, "\\"), (0xDD, "]"), (0xDE, "'"),
    ];

    /// <summary>键的名字（显示与写配置用）。</summary>
    public static string KeyName(uint vk)
    {
        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F).ToString(CultureInfo.InvariantCulture);
        if (vk is >= 0x60 and <= 0x69) return "Num" + (vk - 0x60).ToString(CultureInfo.InvariantCulture);
        foreach (var (code, name) in Names)
            if (code == vk)
                return name;
        return "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }

    private static uint KeyCode(string name)
    {
        if (name.Length == 1 && char.ToUpperInvariant(name[0]) is var c && (c is >= 'A' and <= 'Z' or >= '0' and <= '9')) return c;
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) &&
            f is >= 1 and <= 24)
            return (uint)(0x6F + f);
        if (name.Length == 4 && name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && name[3] is >= '0' and <= '9') return (uint)(0x60 + name[3] - '0');
        foreach (var (code, n) in Names)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                return code;
        if (name.Equals("Escape", StringComparison.OrdinalIgnoreCase)) return 0x1B;
        if (name.Equals("Del", StringComparison.OrdinalIgnoreCase)) return 0x2E;
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint vk) && vk is > 0 and <= 0xFE)
            return vk;
        return 0;
    }

    /// <summary>解析配置里的文字；空串或空白 = <see cref="None"/>（返回 true）。认不出来返回 false。不检查 <see cref="IsValid"/>。</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = None;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var mods = HotkeyModifiers.None;
        uint key = 0;
        // 「Ctrl++」之类：键名本身没有「+」（小键盘加号写作 NumAdd），所以直接按「+」切
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            if (part.Length == 0) return false;
            var mod = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (mod != HotkeyModifiers.None)
            {
                if (key != 0) return false; // 修饰键要写在键前面
                mods |= mod;
                continue;
            }
            if (key != 0) return false; // 只能有一个键
            key = KeyCode(part);
            if (key == 0) return false;
        }
        if (key == 0) return false;
        hotkey = new Hotkey(mods, key);
        return true;
    }

    /// <summary>写配置、显示用的文字（修饰键按 Win、Ctrl、Alt、Shift 的顺序）；<see cref="None"/> 为空串。</summary>
    public override string ToString()
    {
        if (IsNone) return "";
        var parts = new List<string>(5);
        if ((Modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");
        if ((Modifiers & HotkeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
        if ((Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }
}
