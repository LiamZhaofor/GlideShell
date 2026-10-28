using System.Runtime.InteropServices;

namespace GlideShell.Platform.Native;

// 升级检测用的 WinHTTP：系统自带，走系统代理设置，TLS 由系统处理；比 HttpClient 小得多（NativeAOT 下 HttpClient 要多出好几 MB）。
internal static unsafe partial class WinHttp
{
    public const uint WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY = 4; // Windows 8.1+：系统与当前用户的代理设置（含 IE / 系统代理）
    public const uint WINHTTP_FLAG_SECURE = 0x00800000;
    public const uint WINHTTP_QUERY_STATUS_CODE = 19;
    public const uint WINHTTP_QUERY_FLAG_NUMBER = 0x20000000;
    public const ushort INTERNET_DEFAULT_HTTPS_PORT = 443;

    [LibraryImport("winhttp.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint WinHttpOpen(string agent, uint accessType, string? proxy, string? proxyBypass, uint flags);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpSetTimeouts(nint handle, int resolve, int connect, int send, int receive);

    [LibraryImport("winhttp.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint WinHttpConnect(nint session, string server, ushort port, uint reserved);

    [LibraryImport("winhttp.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint WinHttpOpenRequest(nint connect, string verb, string path, string? version, string? referrer, nint acceptTypes, uint flags);

    [LibraryImport("winhttp.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpSendRequest(nint request, string headers, uint headersLength, nint optional, uint optionalLength, uint totalLength, nint context);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpReceiveResponse(nint request, nint reserved);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpQueryHeaders(nint request, uint infoLevel, nint name, void* buffer, uint* bufferLength, uint* index);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpReadData(nint request, void* buffer, uint toRead, uint* read);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpCloseHandle(nint handle);
}
