using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GlideShell.Platform.Diagnostics;

/// <summary>诊断用的系统信息：Windows 版本、本进程是否带 UIAccess、写剪贴板。</summary>
public static unsafe partial class SystemInfo
{
    /// <summary>例如「Windows 11 专业版 24H2（内部版本 26100.2314）」。</summary>
    public static string WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
            string product = key?.GetValue("ProductName") as string ?? "Windows";
            // 注册表里的 ProductName 在 Windows 11 上仍写 Windows 10，按内部版本号改正
            if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            string display = key?.GetValue("DisplayVersion") as string ?? "";
            string ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";
            return $"{product} {display}（内部版本 {build}{ubr}）".Replace("  ", " ");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Environment.OSVersion.VersionString;
        }
    }

    /// <summary>本进程的令牌带不带 UIAccess（装在 Program Files、签名过的 UIAccess 版才有）。</summary>
    public static bool HasUIAccess()
    {
        nint token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /* TOKEN_QUERY */, &token)) return false;
        try
        {
            uint value = 0, size;
            return GetTokenInformation(token, 26 /* TokenUIAccess */, &value, sizeof(uint), &size) && value != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>把文字放进剪贴板（<paramref name="owner"/> 是本进程的一个窗口）；失败返回 false。</summary>
    public static bool CopyToClipboard(nint owner, string text)
    {
        if (!OpenClipboard(owner)) return false;
        try
        {
            EmptyClipboard();
            nuint bytes = (nuint)((text.Length + 1) * sizeof(char));
            nint memory = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, bytes);
            if (memory == 0) return false;
            void* p = GlobalLock(memory);
            if (p == null)
            {
                GlobalFree(memory);
                return false;
            }
            fixed (char* src = text) Buffer.MemoryCopy(src, p, (long)bytes, text.Length * sizeof(char));
            ((char*)p)[text.Length] = '\0';
            GlobalUnlock(memory);
            if (SetClipboardData(13 /* CF_UNICODETEXT */, memory) == 0)
            {
                GlobalFree(memory);
                return false;
            }
            return true; // 成功后内存归剪贴板所有
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>日志文件最后 <paramref name="lines"/> 行（日志还在写，按共享读打开）。</summary>
    public static IReadOnlyList<string> LogTail(int lines)
    {
        if (Log.FilePath is not { } path || !File.Exists(path)) return [];
        Log.Flush(TimeSpan.FromMilliseconds(500));
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int MaxBytes = 256 * 1024; // 只读文件尾部
            if (stream.Length > MaxBytes) stream.Seek(-MaxBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            if (stream.Length > MaxBytes && all.Count > 0) all.RemoveAt(0); // 第一行可能被截断
            return all.Skip(Math.Max(0, all.Count - lines)).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, nint* token);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, void* info, uint length, uint* returned);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial void* GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
}
