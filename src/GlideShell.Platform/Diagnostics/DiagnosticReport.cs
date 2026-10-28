using System.Text;

namespace GlideShell.Platform.Diagnostics;

/// <summary>
/// 一键诊断报告：一份纯文本，方便直接贴到 GitHub Issue。宿主按节填内容（版本与系统、触摸板、显示器、兼容自检、
/// 最近一次异常退出、配置、最近的日志）。只写到本机文件、放进剪贴板，不上传；文件头提醒用户贴出去前看一眼。
/// </summary>
public sealed class DiagnosticReport
{
    private readonly StringBuilder _text = new();

    public DiagnosticReport(string version)
    {
        _text.AppendLine($"GlideShell 诊断报告 · 版本 {version} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _text.AppendLine("这份报告只保存在本机，GlideShell 不会上传。日志里可能有程序名、窗口标题、虚拟桌面名，贴到 Issue 之前请看一眼。");
    }

    public DiagnosticReport Section(string title, IEnumerable<string> lines)
    {
        _text.AppendLine();
        _text.AppendLine($"## {title}");
        bool any = false;
        foreach (var line in lines)
        {
            _text.AppendLine(line);
            any = true;
        }
        if (!any) _text.AppendLine("（无）");
        return this;
    }

    public DiagnosticReport Checks(IEnumerable<CompatCheck> checks)
        => Section("兼容自检", checks.Select(c => $"[{c.LevelText}] {c.Name}：{c.Detail}"));

    public override string ToString() => _text.ToString();

    /// <summary>报告存放目录（%LOCALAPPDATA%\GlideShell\diagnostics）。</summary>
    public static string DirectoryPath => Path.Combine(Path.GetDirectoryName(Log.DefaultDirectory)!, "diagnostics");

    /// <summary>写到诊断目录，返回文件路径（只留最近 10 份）。</summary>
    public string Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath, $"GlideShell-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)); // 带 BOM：记事本一定按 UTF-8 打开
        foreach (var old in Directory.GetFiles(DirectoryPath, "GlideShell-诊断-*.txt").Order(StringComparer.Ordinal).SkipLast(10))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
            }
        }
        return path;
    }
}
