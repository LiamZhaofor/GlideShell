using GlideShell.Platform.Native;
using Microsoft.Win32;

namespace GlideShell.Platform.Ui.Settings;

/// <summary>
/// 设置窗口的配色：Windows 11（WinUI 3）的浅色 / 深色设计令牌，跟随系统「应用模式」，强调色取系统强调色。
/// 颜色都是非预乘的 (R, G, B, A)。
/// </summary>
internal sealed record FluentTheme(
    bool Dark,
    D2D.ColorF Accent,
    D2D.ColorF OnAccent,
    D2D.ColorF TextPrimary,
    D2D.ColorF TextSecondary,
    D2D.ColorF TextTertiary,
    D2D.ColorF Card,
    D2D.ColorF CardHover,
    D2D.ColorF CardStroke,
    D2D.ColorF Control,
    D2D.ColorF ControlHover,
    D2D.ColorF ControlPressed,
    D2D.ColorF ControlStroke,
    D2D.ColorF Subtle,
    D2D.ColorF SubtlePressed,
    D2D.ColorF StrongStroke,
    D2D.ColorF Rail,
    D2D.ColorF Background,
    D2D.ColorF Divider,
    D2D.ColorF Caution)
{
    public const string Font = "Segoe UI Variable Text";
    public const string DisplayFont = "Segoe UI Variable Display";

    /// <summary>图标字体：Windows 11 的 Segoe Fluent Icons（Windows 10 是 Segoe MDL2 Assets，同样的码位）。</summary>
    public static string IconFont { get; } = Environment.OSVersion.Version.Build >= 22000 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    private static D2D.ColorF Rgba(int r, int g, int b, double a = 1) => new(r / 255f, g / 255f, b / 255f, (float)a);

    private static D2D.ColorF Black(double a) => new(0, 0, 0, (float)a);

    private static D2D.ColorF White(double a) => new(1, 1, 1, (float)a);

    public static FluentTheme Current()
    {
        bool dark = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;
        var accent = ReadAccent(dark);
        return dark
            ? new FluentTheme(true, accent, Black(1),
                White(1), White(0.786), White(0.545),
                White(0.051), White(0.08), Black(0.1),
                White(0.061), White(0.084), White(0.033), White(0.07),
                White(0.061), White(0.04), White(0.545), White(0.545),
                Rgba(32, 32, 32), White(0.084), Rgba(252, 225, 0))
            : new FluentTheme(false, accent, White(1),
                Black(0.894), Black(0.62), Black(0.447),
                White(0.7), Rgba(246, 246, 246, 0.5), Black(0.059),
                White(0.7), Rgba(249, 249, 249, 0.5), Rgba(249, 249, 249, 0.3), Black(0.1),
                Black(0.037), Black(0.024), Black(0.62), Black(0.447),
                Rgba(243, 243, 243), Black(0.08), Rgba(157, 93, 0));
    }

    /// <summary>系统强调色（HKCU\…\DWM\AccentColor，ABGR）。深色模式下用亮一些的变体（WinUI 的 AccentLight2）。</summary>
    private static D2D.ColorF ReadAccent(bool dark)
    {
        uint abgr = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", 0xFFD77800);
        double r = abgr & 0xFF, g = (abgr >> 8) & 0xFF, b = (abgr >> 16) & 0xFF;
        if (dark)
        {
            r += (255 - r) * 0.45;
            g += (255 - g) * 0.45;
            b += (255 - b) * 0.45;
        }
        return Rgba((int)r, (int)g, (int)b);
    }

    private static uint ReadDword(string key, string name, uint fallback)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(name) is int v ? unchecked((uint)v) : fallback;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    /// <summary>Segoe Fluent Icons / MDL2 的码位。</summary>
    internal static class Icons
    {
        public const string Home = "", Touch = "", TaskView = "", Mouse = "", Block = "", Info = "";
        public const string ChevronDown = "", ChevronUp = "", ChevronRight = "", Add = "", Delete = "";
        public const string Speed = "", Move = "", Folder = "", Document = "", Reset = "", Power = "";
        public const string Monitor = "", Switch = "", ViewAll = "", Color = "", Keyboard = "", Warning = "";
        public const string Settings = "", Apps = "", Desktop = "";
        public const string Sync = "", Download = "";
        public const string Ok = "\uE73E", Unavailable = "\uE711", Health = "\uE95E";
    }
}
