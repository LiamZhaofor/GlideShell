namespace M1.MissionFlow;

// M1 调试日志：写到 %TEMP%\MissionFlow.log，便于无手指环境下定位状态机走向
internal static class Log
{
    private static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MissionFlow.log");

    public static void W(string msg)
    {
        try { System.IO.File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} [tid{Environment.CurrentManagedThreadId}] {msg}\n"); }
        catch { }
    }
}

internal static class LogExt
{
    public static string TruncateLog(this string s, int max)
        => s.Length > max ? s[..max] + "…" : s;
}
