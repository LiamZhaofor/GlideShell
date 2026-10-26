using GlideShell.Platform.Native;

namespace GlideShell.Platform.NativeGestures;

/// <summary>
/// Explorer（twinui.dll）里的设置缓存服务（未公开）。触摸板手势的生效值以它为准：注册表只是持久化层，
/// Explorer 不监听注册表变化，写完必须 <see cref="Refresh"/> 才立即生效（与「设置」应用的做法相同）。
/// 获取：CoCreateInstance(CLSID_ImmersiveShell, IServiceProvider) → QueryService(SID, IID)。
/// 方法槽（由 OneCoreUAPCommonProxyStub.dll 的 MIDL 格式串确认）：[3] Refresh(id)，[5] GetValue(id, out int)。
/// 接口来自 26100 系列的分析，未来版本可能变化：调用方必须用 <see cref="GetValue"/> 读回校验，失败时降级。
/// </summary>
internal sealed unsafe class ShellSettingsCache : IDisposable
{
    private static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid IidServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid SidSettingsCache = new("53660488-8855-460B-A9AB-5CFC6B5012CA");
    private static readonly Guid IidSettingsCache = new("4214F6FA-EB36-4E2F-9CA2-23FDC1832DF7");
    private const uint ClsctxLocalServer = 0x4;

    private nint _cache;

    private ShellSettingsCache(nint cache) => _cache = cache;

    /// <summary>连接 Explorer 的设置缓存；Explorer 未运行或接口不存在时返回 null。</summary>
    public static ShellSettingsCache? TryConnect(out int hr)
    {
        Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED); // 已初始化时返回非 0，忽略
        Guid clsid = ClsidImmersiveShell, iidSp = IidServiceProvider;
        void* sp;
        hr = Ole32.CoCreateInstance(&clsid, 0, ClsctxLocalServer, &iidSp, &sp);
        if (hr < 0 || sp is null) return null;
        try
        {
            Guid sid = SidSettingsCache, iid = IidSettingsCache;
            nint cache;
            var queryService = (delegate* unmanaged<nint, Guid*, Guid*, nint*, int>)Slot((nint)sp, 3);
            hr = queryService((nint)sp, &sid, &iid, &cache);
            return hr < 0 || cache == 0 ? null : new ShellSettingsCache(cache);
        }
        finally
        {
            Release((nint)sp);
        }
    }

    /// <summary>从注册表重读这一项；值变化时 Explorer 广播并通知订阅者。</summary>
    public int Refresh(int settingId) => ((delegate* unmanaged<nint, int, int>)Slot(_cache, 3))(_cache, settingId);

    /// <summary>读 Explorer 当前的生效值（注册表没有该值时为缺省值）。</summary>
    public int GetValue(int settingId, out int value)
    {
        int v = 0;
        int hr = ((delegate* unmanaged<nint, int, int*, int>)Slot(_cache, 5))(_cache, settingId, &v);
        value = v;
        return hr;
    }

    public void Dispose()
    {
        if (_cache == 0) return;
        Release(_cache);
        _cache = 0;
    }

    private static nint Slot(nint comObject, int index) => (*(nint**)comObject)[index];

    private static void Release(nint comObject) => ((delegate* unmanaged<nint, uint>)Slot(comObject, 2))(comObject);
}
