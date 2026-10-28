namespace GlideShell.Platform.Diagnostics;

/// <summary>
/// 异常退出的记录（%LOCALAPPDATA%\GlideShell\last-crash.txt）：主进程遇到未处理的异常时写入异常与堆栈，
/// 看门狗发现主进程退出码不为 0 时补上退出码（暂停状态下崩溃、被结束也能发现）。下次启动时提示一次，
/// 之后改名为 last-crash-shown.txt 留给诊断报告。纯文本，只在本机。
/// </summary>
public static class CrashRecord
{
    private static string Directory => Path.GetDirectoryName(Log.DefaultDirectory)!;

    public static string PendingPath => Path.Combine(Directory, "last-crash.txt");

    public static string ShownPath => Path.Combine(Directory, "last-crash-shown.txt");

    /// <summary>追加一段（异常处理器、看门狗都可能写；失败就算了，崩溃时不能再出错）。</summary>
    public static void Append(string text)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(PendingPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text.TrimEnd()}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>有没提示过的记录就取出来（并转为已提示），没有返回 null。</summary>
    public static string? TakePending()
    {
        try
        {
            if (!File.Exists(PendingPath)) return null;
            string text = File.ReadAllText(PendingPath);
            File.Move(PendingPath, ShownPath, overwrite: true);
            return text;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>最近一次异常退出（已提示过的也算），诊断报告用；没有返回 null。</summary>
    public static string? Latest()
    {
        try
        {
            foreach (var path in new[] { PendingPath, ShownPath })
                if (File.Exists(path)) return $"{File.ReadAllText(path).TrimEnd()}（记录时间 {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}）";
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }
}
