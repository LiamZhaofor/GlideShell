using System.Text.Json;
using System.Text.Json.Serialization;
using GlideShell.Core.Gestures;
using GlideShell.Core.NativeGestures;
using Microsoft.Win32;

namespace GlideShell.Platform.NativeGestures;

/// <summary>一次接管 / 还原的结果。</summary>
public sealed record NativeGestureResult(bool Applied, bool Verified, IReadOnlyList<string> Messages);

/// <summary>
/// 系统原生触摸板手势的接管与还原（S2 机制）：写注册表 → Explorer 设置缓存 Refresh → GetValue 读回校验。
/// <list type="bullet">
/// <item>接管记录持久化在 <see cref="RecordPath"/>：主进程正常退出、托盘「退回原生」、看门狗发现崩溃，都按它还原。</item>
/// <item>只改 GlideShell 绑定的（指数, 轴）对应的方向；轻点（中键等）永不触碰。</item>
/// <item>还原时用户在接管期间自己改过的项不覆盖。</item>
/// <item>设置缓存不可用（未来系统版本接口变化、Explorer 未运行）时降级：注册表照写，提示重启 Explorer 后生效。</item>
/// </list>
/// </summary>
public sealed class NativeGestureManager(string? recordPath = null)
{

    public string RecordPath { get; } = recordPath ?? DefaultRecordPath;

    public static string DefaultRecordPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlideShell", "native-gestures.json");

    /// <summary>磁盘上有未还原的接管记录（例如上次崩溃）。</summary>
    public bool HasPendingTakeover => LoadRecord() is { IsEmpty: false };

    /// <summary>
    /// 接管映射表需要的系统手势。先还原旧记录（崩溃残留、映射表变更），再从用户的原生设置出发重新规划，
    /// 保证记录里的「原值」永远是用户自己的设置。
    /// </summary>
    public NativeGestureResult TakeOver(GestureMap map)
    {
        var messages = new List<string>();
        if (LoadRecord() is { IsEmpty: false })
        {
            var restored = Restore();
            messages.AddRange(restored.Messages);
        }

        var (changes, warnings) = TakeoverPlanner.Plan(map, ReadDword);
        messages.AddRange(warnings);
        if (changes.Count == 0)
        {
            messages.Add("系统手势已经是所需状态，无需改动");
            return new NativeGestureResult(true, VerifyAll(TouchpadSettings.All, messages), messages);
        }

        // 先落盘记录再写注册表：写到一半崩溃时，看门狗也能按记录还原
        var record = new TakeoverRecord(DateTimeOffset.Now, Environment.OSVersion.VersionString, changes);
        SaveRecord(record);
        using (var key = Registry.CurrentUser.CreateSubKey(TouchpadSettings.KeyPath))
            foreach (var c in changes) key.SetValue(c.Name, c.Written, RegistryValueKind.DWord);

        bool verified = RefreshAndVerify(changes.Select(c => (c.Id, c.Name, (int?)c.Written)), messages);
        messages.Add($"已接管 {changes.Count} 项系统手势：{string.Join("、", changes.Select(c => $"{c.Name}={c.Written}"))}");
        return new NativeGestureResult(true, verified, messages);
    }

    /// <summary>按接管记录还原。无记录时什么也不做。</summary>
    /// <param name="refreshShell">
    /// false = 只写注册表、不调用 Explorer 的设置缓存（注销 / 关机时 Explorer 也在退出，跨进程调用可能卡住；
    /// 注册表是下次登录时 Explorer 读取的值，写回就够了）。
    /// </param>
    public NativeGestureResult Restore(bool refreshShell = true)
    {
        var messages = new List<string>();
        var record = LoadRecord();
        if (record is null || record.IsEmpty) return new NativeGestureResult(false, true, ["没有需要还原的接管记录"]);

        var plan = TakeoverPlanner.PlanRestore(record, ReadDword);
        using (var key = Registry.CurrentUser.CreateSubKey(TouchpadSettings.KeyPath))
        {
            foreach (var (c, action) in plan)
            {
                switch (action)
                {
                    case RestoreAction.WriteOriginal:
                        key.SetValue(c.Name, c.Original!.Value, RegistryValueKind.DWord);
                        break;
                    case RestoreAction.Delete:
                        key.DeleteValue(c.Name, throwOnMissingValue: false);
                        break;
                    case RestoreAction.KeepUserChange:
                        messages.Add($"{c.Name} 在接管期间被改成了 {ReadDword(c.Name)}，保留用户的设置");
                        break;
                }
            }
        }

        // 期望值：写回原值的项 = 原值；删除的项 = 缺省值（由 GetValue 自己给出，这里不强求具体数值）
        bool verified = !refreshShell || RefreshAndVerify(
            plan.Where(p => p.Action is RestoreAction.WriteOriginal or RestoreAction.Delete)
                .Select(p => (p.Change.Id, p.Change.Name, p.Action == RestoreAction.WriteOriginal ? p.Change.Original : null)),
            messages);
        File.Delete(RecordPath);
        int restored = plan.Count(p => p.Action is RestoreAction.WriteOriginal or RestoreAction.Delete);
        messages.Add(refreshShell ? $"已还原系统手势（{restored} 项）" : $"已还原系统手势的注册表（{restored} 项，下次登录时生效）");
        return new NativeGestureResult(true, verified, messages);
    }

    /// <summary>
    /// 对接管记录里的项重新 Refresh 并读回校验（注册表已写好，只差 Explorer 生效）。
    /// 登录早期开机启动时，Explorer 的设置缓存可能还没就绪，用它隔几秒重试。
    /// </summary>
    public NativeGestureResult Reverify()
    {
        var messages = new List<string>();
        var record = LoadRecord();
        if (record is null || record.IsEmpty) return new NativeGestureResult(false, true, messages);
        bool verified = RefreshAndVerify(record.Changes.Select(c => (c.Id, c.Name, (int?)c.Written)), messages);
        return new NativeGestureResult(true, verified, messages);
    }

    /// <summary>注册表值与 Explorer 生效值逐项对照（诊断用）。</summary>
    public IReadOnlyList<(string Name, int? Registry, int? Effective)> Describe()
    {
        using var cache = ShellSettingsCache.TryConnect(out _);
        return TouchpadSettings.All.Select(s =>
        {
            int? effective = cache is not null && cache.GetValue(s.Id, out int v) >= 0 ? v : null;
            return (s.Name, ReadDword(s.Name), effective);
        }).ToList();
    }

    public TakeoverRecord? LoadRecord()
    {
        try
        {
            return File.Exists(RecordPath) ? JsonSerializer.Deserialize(File.ReadAllText(RecordPath), NativeGesturesJson.Default.TakeoverRecord) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void SaveRecord(TakeoverRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
        string temp = RecordPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, NativeGesturesJson.Default.TakeoverRecord));
        File.Move(temp, RecordPath, overwrite: true);
    }

    private static int? ReadDword(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(TouchpadSettings.KeyPath);
        return key?.GetValue(name) is int v ? v : null;
    }

    private static bool RefreshAndVerify(IEnumerable<(int Id, string Name, int? Expected)> items, List<string> messages)
    {
        using var cache = ShellSettingsCache.TryConnect(out int hr);
        if (cache is null)
        {
            messages.Add($"连接 Explorer 设置缓存失败（0x{hr:X8}）：注册表已写入，Explorer 重启后生效");
            return false;
        }
        bool ok = true;
        foreach (var (id, name, expected) in items)
        {
            int refresh = cache.Refresh(id);
            if (refresh < 0)
            {
                messages.Add($"Refresh({name}) 失败 0x{refresh:X8}");
                ok = false;
                continue;
            }
            if (expected is { } want && (cache.GetValue(id, out int got) < 0 || got != want))
            {
                messages.Add($"{name} 读回不一致：期望 {want}，Explorer 生效值 {got}");
                ok = false;
            }
        }
        return ok;
    }

    private static bool VerifyAll(IEnumerable<TouchpadSettings.Setting> settings, List<string> messages)
        => RefreshAndVerify(settings.Select(s => (s.Id, s.Name, ReadDword(s.Name))), messages);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TakeoverRecord))]
internal sealed partial class NativeGesturesJson : JsonSerializerContext;
