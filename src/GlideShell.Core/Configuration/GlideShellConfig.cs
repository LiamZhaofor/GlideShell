using System.Text.Json.Serialization;
using GlideShell.Core.Gestures;
using GlideShell.Core.Motion;

namespace GlideShell.Core.Configuration;

public sealed record SpringConfig
{
    /// <summary>弹簧周期（秒），越小越快。</summary>
    public double Response { get; set; } = 0.35;

    /// <summary>阻尼比：1 = 不回弹，小于 1 有回弹。</summary>
    public double DampingRatio { get; set; } = 0.86;

    public SpringParameters ToParameters() => new(Response, DampingRatio);
}

public sealed record OverviewConfig
{
    /// <summary>沿锁定轴滑动多少毫米 = 完全展开。</summary>
    public double TravelMm { get; set; } = 30;

    public SpringConfig Spring { get; set; } = new();

    public bool PrimaryMonitorOnly { get; set; }

    /// <summary>展开后背景（桌面）变暗的程度：0 = 不变暗，1 = 全黑。</summary>
    public double BackdropDim { get; set; } = 0.6;
}

public sealed record SwitcherConfig
{
    /// <summary>横滑多少毫米切换一个窗口。</summary>
    public double MmPerWindow { get; set; } = 22;

    public SpringConfig Spring { get; set; } = new() { Response = 0.3, DampingRatio = 0.9 };
}

public sealed record DesktopConfig
{
    /// <summary>
    /// 是否由 GlideShell 接管四指横滑切桌面。Win11 原生四指切桌面本身已经跟手（S3），
    /// 关掉后四指横滑交还系统。虚拟桌面接口自检不通过时自动视为关闭。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>横滑多少毫米 = 整整切过一个桌面。</summary>
    public double TravelMm { get; set; } = 55;

    public SpringConfig Spring { get; set; } = new() { Response = 0.32, DampingRatio = 0.92 };
}

public sealed record DragConfig
{
    /// <summary>慢速时每毫米手指位移移动多少物理像素。</summary>
    public double PixelsPerMm { get; set; } = 14;

    /// <summary>速度加速系数：100mm/s 时增益乘以 (1 + 加速)，最多按 400mm/s 计。</summary>
    public double Acceleration { get; set; } = 0.6;
}

// 配置记录的属性用 set 而不是 init：System.Text.Json 源生成对 init 属性用对象初始化器一次性赋值，
// JSON 里缺失的字段会被设成 null / 0，覆盖掉默认值；set 属性则是构造后逐个赋值，缺失字段保留默认值。

/// <summary>
/// 用户配置（%APPDATA%\GlideShell\config.json）。缺失的字段取默认值；越界的数值会被夹回合法范围并报告。
/// </summary>
public sealed record GlideShellConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>识别参数（灵敏度）。</summary>
    public GestureSettings Recognition { get; set; } = new();

    /// <summary>自定义映射；不填时按 <see cref="ThreeFingerDrag"/> 取默认映射表。</summary>
    public List<GestureBinding>? Bindings { get; set; }

    /// <summary>三指拖移（P6）。开启后三指整组让给拖移，总览等改为四指。</summary>
    public bool ThreeFingerDrag { get; set; }

    public OverviewConfig Overview { get; set; } = new();

    public SwitcherConfig Switcher { get; set; } = new();

    public DesktopConfig Desktops { get; set; } = new();

    public DragConfig Drag { get; set; } = new();

    /// <summary>前台是这些进程时不接管手势（进程名，不含 .exe，不区分大小写）。</summary>
    public List<string> ExcludedProcesses { get; set; } = [];

    /// <summary>前台是全屏应用（游戏、演示）时不接管手势。</summary>
    public bool PauseInFullscreen { get; set; } = true;

    [JsonIgnore] // 计算值，不写进配置文件
    public GestureMap EffectiveMap => Bindings is { Count: > 0 } b
        ? new GestureMap(b)
        : ThreeFingerDrag ? GestureMap.WithThreeFingerDrag : GestureMap.Default;

    /// <summary>把越界数值夹回合法范围，返回修正后的配置与问题列表（映射冲突时回退到默认映射）。</summary>
    public (GlideShellConfig Config, IReadOnlyList<string> Problems) Sanitize()
    {
        var problems = new List<string>();
        double Clamp(double v, double min, double max, string name)
        {
            if (double.IsFinite(v) && v >= min && v <= max) return v;
            double fixedValue = double.IsFinite(v) ? Math.Clamp(v, min, max) : min;
            problems.Add($"{name}={v} 超出范围 [{min}, {max}]，已改为 {fixedValue}");
            return fixedValue;
        }
        SpringConfig FixSpring(SpringConfig s, string name) => s with
        {
            Response = Clamp(s.Response, 0.05, 2, $"{name}.Response"),
            DampingRatio = Clamp(s.DampingRatio, 0.1, 3, $"{name}.DampingRatio"),
        };

        var r = Recognition;
        double slop = Clamp(r.SlopMm, 0.5, 20, "Recognition.SlopMm");
        var recognition = r with
        {
            MinFingers = (int)Clamp(r.MinFingers, 3, 5, "Recognition.MinFingers"),
            SlopMm = slop,
            FingerSettleMs = Clamp(r.FingerSettleMs, 0, 200, "Recognition.FingerSettleMs"),
            TapMaxDurationMs = Clamp(r.TapMaxDurationMs, 50, 1000, "Recognition.TapMaxDurationMs"),
            TapMaxMoveMm = Clamp(r.TapMaxMoveMm, 0, slop, "Recognition.TapMaxMoveMm"),
            AxisLockRatio = Clamp(r.AxisLockRatio, 0.25, 4, "Recognition.AxisLockRatio"),
            VelocityWindowMs = Clamp(r.VelocityWindowMs, 20, 300, "Recognition.VelocityWindowMs"),
            StaleTimeoutMs = Clamp(r.StaleTimeoutMs, 1000, 60_000, "Recognition.StaleTimeoutMs"),
        };

        var bindings = Bindings;
        if (bindings is { Count: > 0 })
        {
            var mapProblems = new GestureMap(bindings).Validate(ThreeFingerDrag);
            if (mapProblems.Count > 0)
            {
                problems.AddRange(mapProblems);
                problems.Add("手势映射有冲突，已改用默认映射");
                bindings = null;
            }
        }

        var config = this with
        {
            Version = CurrentVersion,
            Recognition = recognition,
            Bindings = bindings,
            Overview = Overview with
            {
                TravelMm = Clamp(Overview.TravelMm, 5, 100, "Overview.TravelMm"),
                BackdropDim = Clamp(Overview.BackdropDim, 0, 1, "Overview.BackdropDim"),
                Spring = FixSpring(Overview.Spring, "Overview.Spring"),
            },
            Switcher = Switcher with
            {
                MmPerWindow = Clamp(Switcher.MmPerWindow, 5, 100, "Switcher.MmPerWindow"),
                Spring = FixSpring(Switcher.Spring, "Switcher.Spring"),
            },
            Drag = Drag with
            {
                PixelsPerMm = Clamp(Drag.PixelsPerMm, 1, 80, "Drag.PixelsPerMm"),
                Acceleration = Clamp(Drag.Acceleration, 0, 4, "Drag.Acceleration"),
            },
            Desktops = Desktops with
            {
                TravelMm = Clamp(Desktops.TravelMm, 10, 150, "Desktops.TravelMm"),
                Spring = FixSpring(Desktops.Spring, "Desktops.Spring"),
            },
            ExcludedProcesses = ExcludedProcesses
                .Select(p => p.Trim())
                .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
        return (config, problems);
    }

    public bool IsExcluded(string processName)
        => ExcludedProcesses.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
}
