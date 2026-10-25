using System.Text.Json;
using System.Text.Json.Serialization;

namespace M1.MissionFlow;

// M2：手感参数全部进配置文件（%APPDATA%\MissionFlow\config.json），改完下一次手势即生效，无需重编译。
// 文件不存在时自动写入默认值；缺失字段用默认值兜底。
internal sealed class MissionConfig
{
    // 手感
    public double EnterThreshold = 0.30;   // 三指上滑多少（触摸板高度归一化）= 进度 1.0
    public double EnterCommit = 0.55;      // 松手时进度达到即进入
    public double CommitVelocity = 1.2;    // 或离手速度达到（单位/秒）即进入
    public double DragCommitVelocity = 2.5; // 滑动中即时提交的速度阈值（快甩不等松手）
    public double DragCommitMinP = 0.35;   // 即时提交的最低进度（防误触）
    public double ExitThreshold = 0.22;    // 交互态三指下滑关闭行程
    public double ExitCommit = 0.50;
    public double StaggerStep = 0.015;     // 起飞错峰步进
    public double StaggerMax = 0.20;

    // 动画时长（ms）
    public int EnterAnimMs = 200;
    public int DismissAnimMs = 230;
    public int CancelAnimMs = 180;

    // 行为
    public bool PrimaryMonitorOnly = false; // 只在主屏平铺
    public bool PauseOnFullscreen = true;   // 前台是全屏应用时暂停手势

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MissionFlow", "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // 与 JSON 交互的 DTO：全部可空，缺失即回落默认值
    private sealed class Dto
    {
        public double? EnterThreshold { get; set; }
        public double? EnterCommit { get; set; }
        public double? CommitVelocity { get; set; }
        public double? DragCommitVelocity { get; set; }
        public double? DragCommitMinP { get; set; }
        public double? ExitThreshold { get; set; }
        public double? ExitCommit { get; set; }
        public double? StaggerStep { get; set; }
        public double? StaggerMax { get; set; }
        public int? EnterAnimMs { get; set; }
        public int? DismissAnimMs { get; set; }
        public int? CancelAnimMs { get; set; }
        public bool? PrimaryMonitorOnly { get; set; }
        public bool? PauseOnFullscreen { get; set; }
    }

    private DateTime _lastWrite = DateTime.MinValue;

    /// <summary>加载配置；文件变化时自动重载（每次手势开始时调用，开销是一次 mtime 检查）。</summary>
    public static MissionConfig LoadOrReload(MissionConfig? current)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            if (!File.Exists(ConfigPath))
            {
                var def = new MissionConfig();
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(ToDto(def), JsonOpts));
                def._lastWrite = File.GetLastWriteTimeUtc(ConfigPath);
                Log.W($"Config: 已生成默认配置 {ConfigPath}");
                return def;
            }

            var mtime = File.GetLastWriteTimeUtc(ConfigPath);
            if (current is not null && mtime == current._lastWrite) return current;

            var cfg = new MissionConfig();
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(ConfigPath)) ?? new Dto();
            if (dto.EnterThreshold is { } v1) cfg.EnterThreshold = v1;
            if (dto.EnterCommit is { } v2) cfg.EnterCommit = v2;
            if (dto.CommitVelocity is { } v3) cfg.CommitVelocity = v3;
            if (dto.DragCommitVelocity is { } dv) cfg.DragCommitVelocity = dv;
            if (dto.DragCommitMinP is { } dp) cfg.DragCommitMinP = dp;
            if (dto.ExitThreshold is { } v4) cfg.ExitThreshold = v4;
            if (dto.ExitCommit is { } v5) cfg.ExitCommit = v5;
            if (dto.StaggerStep is { } v6) cfg.StaggerStep = v6;
            if (dto.StaggerMax is { } v7) cfg.StaggerMax = v7;
            if (dto.EnterAnimMs is { } v8) cfg.EnterAnimMs = v8;
            if (dto.DismissAnimMs is { } v9) cfg.DismissAnimMs = v9;
            if (dto.CancelAnimMs is { } v10) cfg.CancelAnimMs = v10;
            if (dto.PrimaryMonitorOnly is { } v11) cfg.PrimaryMonitorOnly = v11;
            if (dto.PauseOnFullscreen is { } v12) cfg.PauseOnFullscreen = v12;
            cfg._lastWrite = mtime;
            if (current is not null) Log.W("Config: 检测到配置更新，已重载");
            return cfg;
        }
        catch (Exception ex)
        {
            Log.W($"Config: 读取失败，用默认值 ({ex.Message})");
            return current ?? new MissionConfig();
        }
    }

    private static Dto ToDto(MissionConfig c) => new()
    {
        EnterThreshold = c.EnterThreshold,
        EnterCommit = c.EnterCommit,
        CommitVelocity = c.CommitVelocity,
        DragCommitVelocity = c.DragCommitVelocity,
        DragCommitMinP = c.DragCommitMinP,
        ExitThreshold = c.ExitThreshold,
        ExitCommit = c.ExitCommit,
        StaggerStep = c.StaggerStep,
        StaggerMax = c.StaggerMax,
        EnterAnimMs = c.EnterAnimMs,
        DismissAnimMs = c.DismissAnimMs,
        CancelAnimMs = c.CancelAnimMs,
        PrimaryMonitorOnly = c.PrimaryMonitorOnly,
        PauseOnFullscreen = c.PauseOnFullscreen,
    };
}
