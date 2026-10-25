namespace GlideShell.Tools.PtpProbe;

/// <summary>S1 录制场景。每个场景都对应 docs/ROADMAP.md 第 5 节 S1 要回答的问题，也是 P2 回放测试的夹具来源。</summary>
internal sealed record Scenario(string Slug, string Title, string Instructions, int Seconds, string Purpose);

internal static class Scenarios
{
    public static readonly Scenario[] All =
    [
        new("warmup", "单指自检",
            "单指在触摸板上随便滑几下，看状态行里的报文数在涨。", 8,
            "确认录制链路正常"),
        new("three-up-release", "三指上滑后抬起",
            "三指一起放下，匀速上滑约半个板高，然后抬起。做 3 次，每次之间停 1 秒。", 15,
            "抬指信号：最后一份报文是否 TipSwitch=0、之后是否还有 cc=0 报文"),
        new("three-up-hold", "三指上滑后停住",
            "三指上滑到一半停住，保持完全不动约 2 秒，再抬起。做 3 次。", 22,
            "按住不动时设备是否停发报文（旧原型的「静默」问题）"),
        new("three-flick-up", "三指快甩",
            "三指快速向上甩出并立刻抬起。做 3 次。", 12,
            "快甩的速度量级、抬指前最后几份报文的间隔"),
        new("three-reverse", "三指中途反向",
            "三指慢慢上滑 → 不抬指往回下滑 → 再上滑，然后抬起。做 2 次。", 15,
            "连续可逆跟手所需的轨迹"),
        new("three-down", "三指下滑",
            "三指从板子上半部向下滑，然后抬起。做 3 次。", 12,
            "下滑方向的轨迹（交互态收起）"),
        new("three-horizontal", "三指横滑",
            "三指向左滑后抬起，再向右滑后抬起，各做 2 次。系统会弹出「切换应用」，属正常现象。", 15,
            "横滑轨迹与方向锁定（P4）"),
        new("three-tap", "三指轻点",
            "三指轻点 5 次（系统会产生中键点击，属正常现象）。", 12,
            "轻点的持续时间和抖动幅度：区分轻点与滑动的阈值"),
        new("finger-change", "三指 ↔ 四指切换",
            "三指按下并滑动，途中加上第四根手指继续滑，再抬起一根继续滑，最后全部抬起。做 2 次。", 18,
            "触点数变化时的报文表现（指数稳定策略）"),
        new("staggered-landing", "手指依次落下",
            "一根一根地放下三指（每根间隔约半秒），然后上滑并抬起。做 2 次。", 15,
            "落指错开时间：识别前要等多久才确定指数"),
        new("four-up", "四指上滑",
            "四指上滑后抬起，做 2 次（系统可能打开任务视图，属正常现象）。", 12,
            "四指轨迹（P5/P6）"),
        new("four-horizontal", "四指横滑",
            "四指先向左滑一次，再向右滑一次（系统可能切换桌面，第二次会切回来）。", 12,
            "四指横滑轨迹（P5 切桌面）"),
        new("two-scroll", "两指滚动",
            "两指上下滚动几下。", 8,
            "反例：两指滚动不能误触发 GlideShell"),
        new("palm", "手掌误触",
            "在键盘上摆出打字姿势，让手掌根部轻轻搭在触摸板边缘几次。", 12,
            "Confidence 位的表现：误触过滤"),
    ];
}
