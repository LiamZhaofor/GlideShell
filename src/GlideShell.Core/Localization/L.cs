using System.Globalization;

namespace GlideShell.Core.Localization;

/// <summary>界面语言。</summary>
public enum UiLanguage
{
    Chinese,
    English,
}

/// <summary>
/// 界面文字的本地化（gettext 的做法）：源码里直接写中文原文，<see cref="T"/> / <see cref="F"/> 按当前语言查 <see cref="English"/> 里的译文，
/// 查不到就用原文。调用处 <c>using static GlideShell.Core.Localization.L;</c> 后写 <c>T("首页")</c>、<c>F("版本 {0}", v)</c>。
/// <list type="bullet">
/// <item>参数里只能放普通字符串字面量（测试会扫源码，核对每一条都有译文、占位符个数一致）。</item>
/// <item>日志、异常、开发工具的输出不翻译，保持中文。</item>
/// </list>
/// </summary>
public static class L
{
    /// <summary>当前界面语言。UI 线程在读配置时设置；别的线程只读。</summary>
    public static UiLanguage Language { get; set; }

    /// <summary>配置里的语言选项：跟随系统。</summary>
    public const string Auto = "auto";

    public static string T(string zh)
        => Language == UiLanguage.English && English.Strings.TryGetValue(zh, out string? en) ? en : zh;

    /// <summary>
    /// 只做标记、不翻译（gettext 的 N_）：静态数据里的原文（启动时只算一次，不能在那时就定下语言），显示时再 <see cref="T"/>。
    /// 测试按它找出这些原文，核对译文。
    /// </summary>
    public static string N(string zh) => zh;

    /// <summary>带占位符（<c>{0}</c>、<c>{1:0.0}</c>）的文字：先查译文再格式化。</summary>
    public static string F(string zhFormat, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, T(zhFormat), args);

    /// <summary>
    /// 配置的语言选项 <paramref name="setting"/>（"auto" / "zh-CN" / "en"）落到具体语言：
    /// 跟随系统时，系统界面语言是中文（zh-*）就用中文，其余一律英文。
    /// </summary>
    public static UiLanguage Resolve(string? setting, string systemUiCulture) => setting?.ToLowerInvariant() switch
    {
        "zh-cn" or "zh" => UiLanguage.Chinese,
        "en" or "en-us" => UiLanguage.English,
        _ => systemUiCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? UiLanguage.Chinese : UiLanguage.English,
    };
}
