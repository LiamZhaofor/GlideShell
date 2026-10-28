using System.ComponentModel;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Updates;

/// <summary>
/// 最小的 HTTPS GET（系统 WinHTTP：走系统代理设置，TLS 由系统处理，自动跟随重定向——GitHub 的发布附件会跳到下载服务器）。
/// 同步执行，调用方放在后台线程上。失败抛 <see cref="Win32Exception"/>（WinHTTP 错误码）或 <see cref="IOException"/>。
/// </summary>
internal static unsafe class WinHttpClient
{
    private const int TimeoutMs = 15_000;

    /// <param name="url">https 地址。</param>
    /// <param name="headers">附加请求头（每行以 \r\n 结尾）。</param>
    /// <param name="output">响应体写到这里。</param>
    /// <param name="maxBytes">响应体超过这么大就放弃。</param>
    /// <param name="progress">已收到的字节数（每收到一块调用一次）。</param>
    /// <returns>HTTP 状态码（跟随重定向之后的）。</returns>
    public static int Get(string url, string agent, string headers, Stream output, long maxBytes, Action<long>? progress = null)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps) throw new IOException("只支持 https");
        nint session = 0, connect = 0, request = 0;
        try
        {
            session = WinHttp.WinHttpOpen(agent, WinHttp.WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, null, null, 0);
            if (session == 0) throw new Win32Exception();
            WinHttp.WinHttpSetTimeouts(session, TimeoutMs, TimeoutMs, TimeoutMs, TimeoutMs);
            connect = WinHttp.WinHttpConnect(session, uri.Host, (ushort)uri.Port, 0);
            if (connect == 0) throw new Win32Exception();
            request = WinHttp.WinHttpOpenRequest(connect, "GET", uri.PathAndQuery, null, null, 0, WinHttp.WINHTTP_FLAG_SECURE);
            if (request == 0) throw new Win32Exception();
            if (!WinHttp.WinHttpSendRequest(request, headers, uint.MaxValue /* -1：以 null 结尾 */, 0, 0, 0, 0)) throw new Win32Exception();
            if (!WinHttp.WinHttpReceiveResponse(request, 0)) throw new Win32Exception();
            uint status = 0, size = sizeof(uint);
            if (!WinHttp.WinHttpQueryHeaders(request, WinHttp.WINHTTP_QUERY_STATUS_CODE | WinHttp.WINHTTP_QUERY_FLAG_NUMBER, 0, &status, &size, null))
                throw new Win32Exception();

            var chunk = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                uint read;
                fixed (byte* p = chunk)
                    if (!WinHttp.WinHttpReadData(request, p, (uint)chunk.Length, &read)) throw new Win32Exception();
                if (read == 0) break;
                total += read;
                if (total > maxBytes) throw new IOException("回复太大");
                output.Write(chunk, 0, (int)read);
                progress?.Invoke(total);
            }
            return (int)status;
        }
        finally
        {
            if (request != 0) WinHttp.WinHttpCloseHandle(request);
            if (connect != 0) WinHttp.WinHttpCloseHandle(connect);
            if (session != 0) WinHttp.WinHttpCloseHandle(session);
        }
    }

    /// <summary>常见的 WinHTTP 错误码换成一句话（系统的错误文字对 WinHTTP 的码大多是「未知错误」）。</summary>
    public static string Describe(int code) => code switch
    {
        12002 => "连接超时",
        12007 => "找不到服务器（没有联网？）",
        12029 => "连不上 GitHub",
        12030 => "连接被中断",
        12038 or 12044 or 12045 or 12057 or 12175 => "安全连接失败",
        _ => $"网络错误 {code}",
    };
}
