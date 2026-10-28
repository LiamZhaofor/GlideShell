namespace GlideShell.Platform.Diagnostics;

public enum CheckLevel
{
    /// <summary>正常。</summary>
    Ok,

    /// <summary>能用，但有需要知道的情况（例如退回了经典缩略图、系统手势还在等生效）。</summary>
    Warning,

    /// <summary>不可用：相关功能关掉或退回系统。</summary>
    Unavailable,

    /// <summary>只是说明（没开、还没用到）。</summary>
    Info,
}

/// <summary>兼容自检的一项：设置「诊断」页逐项显示，诊断报告里也列出。</summary>
public sealed record CompatCheck(string Name, CheckLevel Level, string Detail)
{
    public string LevelText => Level switch
    {
        CheckLevel.Ok => "正常",
        CheckLevel.Warning => "注意",
        CheckLevel.Unavailable => "不可用",
        _ => "说明",
    };
}
