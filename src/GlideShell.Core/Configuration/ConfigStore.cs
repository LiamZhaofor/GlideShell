using System.Text;
using System.Text.Json;
using GlideShell.Core.Serialization;

namespace GlideShell.Core.Configuration;

/// <summary>配置文件读写（源生成序列化）。读取永不抛出：文件缺失或损坏时用默认值，并把原因放进问题列表。</summary>
public static class ConfigStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlideShell", "config.json");

    public static (GlideShellConfig Config, IReadOnlyList<string> Problems) Parse(string json)
    {
        try
        {
            var config = JsonSerializer.Deserialize(json, CoreJson.Config) ?? new GlideShellConfig();
            var (sanitized, problems) = config.Sanitize();
            if (config.Version > GlideShellConfig.CurrentVersion)
                problems = [.. problems, $"配置版本 {config.Version} 比本程序支持的 {GlideShellConfig.CurrentVersion} 新，未知字段已忽略"];
            return (sanitized, problems);
        }
        catch (JsonException ex)
        {
            return (new GlideShellConfig(), [$"配置文件格式错误（第 {ex.LineNumber + 1} 行）：{ex.Message}，已使用默认配置"]);
        }
    }

    public static (GlideShellConfig Config, IReadOnlyList<string> Problems) Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return (new GlideShellConfig(), []);
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (IOException ex)
        {
            return (new GlideShellConfig(), [$"读取配置失败：{ex.Message}，已使用默认配置"]);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (new GlideShellConfig(), [$"读取配置失败：{ex.Message}，已使用默认配置"]);
        }
    }

    public static string Serialize(GlideShellConfig config) => JsonSerializer.Serialize(config, CoreJson.Config);

    /// <summary>原子写入：先写临时文件再替换，写到一半崩溃也不会留下半个配置。</summary>
    public static void Save(GlideShellConfig config, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(config), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
