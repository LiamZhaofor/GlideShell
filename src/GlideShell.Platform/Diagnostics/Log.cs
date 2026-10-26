using System.Text;
using System.Threading.Channels;

namespace GlideShell.Platform.Diagnostics;

/// <summary>
/// 异步、有界的文件日志：调用方只做一次入队（不碰磁盘），后台任务批量写入。
/// 队列满时丢弃并计数，绝不阻塞输入线程或渲染线程。文件超过 4MB 滚动一次（保留一个旧文件）。
/// 默认位置 %LOCALAPPDATA%\GlideShell\logs\glideshell.log。
/// </summary>
public static class Log
{
    private const long RollBytes = 4 * 1024 * 1024;

    private static readonly Channel<string> s_queue = Channel.CreateBounded<string>(new BoundedChannelOptions(8192)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait, // 配合 TryWrite：满了直接返回 false，由我们计数
    });

    private static Task? s_writer;
    private static long s_dropped;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlideShell", "logs");

    public static string? FilePath { get; private set; }

    public static long Dropped => Interlocked.Read(ref s_dropped);

    /// <summary>不调用 Start 时日志只进队列、不落盘（测试与工具场景）。</summary>
    public static void Start(string? directory = null)
    {
        if (s_writer is not null) return;
        string dir = directory ?? DefaultDirectory;
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "glideshell.log");
        s_writer = Task.Run(WriteLoop);
    }

    public static void Info(string message) => Enqueue("INFO", message);

    public static void Warn(string message) => Enqueue("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Enqueue("ERROR", ex is null ? message : $"{message}\n{ex}");

    /// <summary>退出前调用：等待队列写完（最多 timeout）。</summary>
    public static void Flush(TimeSpan timeout)
    {
        s_queue.Writer.TryComplete();
        s_writer?.Wait(timeout);
    }

    private static void Enqueue(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [t{Environment.CurrentManagedThreadId}] {message}";
        if (!s_queue.Writer.TryWrite(line)) Interlocked.Increment(ref s_dropped);
    }

    private static async Task WriteLoop()
    {
        var reader = s_queue.Reader;
        var batch = new StringBuilder();
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Length < 64 * 1024 && reader.TryRead(out var line)) batch.Append(line).Append('\n');
            try
            {
                RollIfNeeded();
                await File.AppendAllTextAsync(FilePath!, batch.ToString(), Encoding.UTF8).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // 磁盘问题时丢弃这一批，不影响主流程
            }
        }
    }

    private static void RollIfNeeded()
    {
        var info = new FileInfo(FilePath!);
        if (!info.Exists || info.Length < RollBytes) return;
        string old = Path.ChangeExtension(FilePath!, ".1.log");
        File.Move(FilePath!, old, overwrite: true);
    }
}
