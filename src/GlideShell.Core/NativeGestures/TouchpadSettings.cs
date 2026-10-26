namespace GlideShell.Core.NativeGestures;

/// <summary>
/// Windows 触摸板手势设置（HKCU\...\PrecisionTouchPad 下的 DWORD 值）。
/// 编号（SettingId）与缺省值来自 S2 对 twinui.dll / SettingsHandlers_nt.dll 的静态分析（26100 系列），
/// Explorer 设置缓存的 Refresh / GetValue 按编号工作。详见 docs/spikes/S2-native-gestures.md。
/// </summary>
public static class TouchpadSettings
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";

    /// <summary>*SlideEnabled = 0xFFFF：按方向自定义（读 *Up/Down/Left/Right）。</summary>
    public const int CustomMode = 0xFFFF;

    /// <summary>方向动作：无。</summary>
    public const int ActionNothing = 0;

    public sealed record Setting(string Name, int Id, int Default);

    public static readonly Setting ThreeFingerTap = new("ThreeFingerTapEnabled", 0x0F, 1);
    public static readonly Setting FourFingerTap = new("FourFingerTapEnabled", 0x10, 2);
    public static readonly Setting ThreeFingerSlide = new("ThreeFingerSlideEnabled", 0x11, 1);
    public static readonly Setting FourFingerSlide = new("FourFingerSlideEnabled", 0x12, 2);
    public static readonly Setting ThreeFingerLeft = new("ThreeFingerLeft", 0x14, 1);
    public static readonly Setting ThreeFingerUp = new("ThreeFingerUp", 0x16, 2);
    public static readonly Setting ThreeFingerRight = new("ThreeFingerRight", 0x18, 1);
    public static readonly Setting ThreeFingerDown = new("ThreeFingerDown", 0x1A, 3);
    public static readonly Setting FourFingerLeft = new("FourFingerLeft", 0x1C, 4);
    public static readonly Setting FourFingerRight = new("FourFingerRight", 0x1E, 4);
    public static readonly Setting FourFingerUp = new("FourFingerUp", 0x20, 2);
    public static readonly Setting FourFingerDown = new("FourFingerDown", 0x22, 3);

    public static readonly IReadOnlyList<Setting> All =
    [
        ThreeFingerTap, FourFingerTap, ThreeFingerSlide, FourFingerSlide,
        ThreeFingerLeft, ThreeFingerUp, ThreeFingerRight, ThreeFingerDown,
        FourFingerLeft, FourFingerRight, FourFingerUp, FourFingerDown,
        new("CustomThreeFingerTap", 0x24, 1), new("CustomFourFingerTap", 0x26, 2),
    ];

    public static Setting? Find(string name)
        => All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>某指数的「总开关 + 四个方向」。</summary>
    public static (Setting Slide, Setting Left, Setting Up, Setting Right, Setting Down) Group(int fingers) => fingers switch
    {
        3 => (ThreeFingerSlide, ThreeFingerLeft, ThreeFingerUp, ThreeFingerRight, ThreeFingerDown),
        4 => (FourFingerSlide, FourFingerLeft, FourFingerUp, FourFingerRight, FourFingerDown),
        _ => throw new ArgumentOutOfRangeException(nameof(fingers), "系统只有三指 / 四指手势设置"),
    };

    /// <summary>
    /// 预设模式展开成「左、上、右、下」的方向动作。1/2 与 twinui 缺省方向值一致（S2 验证）；
    /// 3（音频和音量）的展开是推断（上下 = 音量、左右 = 曲目），未经真机验证，调用方应提示。
    /// </summary>
    public static (int Left, int Up, int Right, int Down)? ExpandPreset(int mode) => mode switch
    {
        0 => (0, 0, 0, 0),
        1 => (1, 2, 1, 3),   // 切换应用 / 任务视图 / 切换应用 / 显示桌面
        2 => (4, 2, 4, 3),   // 切换桌面 / 任务视图 / 切换桌面 / 显示桌面
        3 => (15, 16, 14, 17), // 上一曲 / 音量+ / 下一曲 / 音量-（推断）
        _ => null,
    };
}
