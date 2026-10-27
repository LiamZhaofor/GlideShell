using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using GlideShell.Core.Overview;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Shell;

/// <summary>
/// 窗口所属程序的图标（总览里窗口标题前、最小化占位卡片上）。程序按「应用标识」区分：窗口带 AppUserModelID 的用它
/// （商店应用都在同一个 ApplicationFrameHost 进程里，只能这样分开），否则用进程的 exe 路径。
/// 图标由外壳（IShellItemImageFactory，和开始菜单、任务栏同一来源）按需要的像素大小给出，在专门的 STA 线程上取，
/// 结果（预乘 BGRA）按（程序，大小）缓存到进程退出。取完在 UI 线程上回调。
/// </summary>
public sealed unsafe partial class AppIcons : IDisposable
{
    private readonly Ui.UiThread _ui;
    private readonly ConcurrentDictionary<(string App, int Size), ShadowImage?> _cache = new();
    private readonly Dictionary<(string App, int Size), List<Action<ShadowImage?>>> _waiting = []; // UI 线程
    private readonly BlockingCollection<(string App, string Parsing, string Fallback, int Size)> _queue = new();
    private readonly Thread _thread;

    public AppIcons(Ui.UiThread ui)
    {
        _ui = ui;
        _thread = new Thread(Run) { IsBackground = true, Name = "GlideShell.Icons" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// UI 线程：窗口的应用标识（<see cref="AppId.Key"/>：AppUserModelID 或 exe 完整路径；都取不到为空串）。
    /// </summary>
    public static AppId AppOf(nint hwnd)
    {
        string exe = ExePath(hwnd);
        return new AppId(AppUserModelId(hwnd) is { Length: > 0 } id ? "aumid:" + id : exe, exe);
    }

    /// <summary>
    /// UI 线程：取 <paramref name="app"/> 的 <paramref name="sizePx"/> 像素图标。
    /// 已缓存时直接返回（可能是 null：取不到）；否则返回 null、<paramref name="pending"/> 为 true，取到后在 UI 线程上调用 <paramref name="ready"/>。
    /// </summary>
    public ShadowImage? Get(AppId app, int sizePx, Action<ShadowImage?> ready, out bool pending)
    {
        pending = false;
        if (app.Key.Length == 0) return null;
        var key = (app.Key, sizePx);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        pending = true;
        if (_waiting.TryGetValue(key, out var list))
        {
            list.Add(ready);
            return null;
        }
        _waiting[key] = [ready];
        bool aumid = app.Key.StartsWith("aumid:", StringComparison.Ordinal);
        _queue.Add((app.Key, aumid ? @"shell:AppsFolder\" + app.Key[6..] : app.Key, aumid ? app.Exe : "", sizePx));
        return null;
    }

    private void Run()
    {
        Ole32.CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
        foreach (var (app, parsing, fallback, size) in _queue.GetConsumingEnumerable())
        {
            ShadowImage? image = null;
            try
            {
                image = Load(parsing, size);
                // AppUserModelID 在「所有应用」里找不到（程序自己设的、没有开始菜单快捷方式）：退回 exe 的图标
                if (image is null && fallback.Length > 0) image = Load(fallback, size);
            }
            catch (Exception ex)
            {
                Log.Warn($"取程序图标失败（{parsing}）：{ex.Message}");
            }
            var key = (app, size);
            _cache[key] = image;
            _ui.Post(() =>
            {
                if (!_waiting.Remove(key, out var callbacks)) return;
                foreach (var cb in callbacks) cb(image);
            });
        }
    }

    private static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    /// <summary>STA 线程：外壳给出的图标（32 位带 alpha 的 HBITMAP）转成预乘 BGRA。</summary>
    private static ShadowImage? Load(string parsing, int size)
    {
        Guid iid = IID_IShellItemImageFactory;
        void* factory;
        fixed (char* p = parsing)
            if (SHCreateItemFromParsingName(p, 0, &iid, &factory) < 0 || factory == null) return null;
        nint hbm = 0;
        int hr;
        try
        {
            // GetImage(SIZE, SIIGBF, HBITMAP*)：只要图标（不要文件内容缩略图）
            hr = ((delegate* unmanaged[Stdcall]<void*, D2D.SizeU, int, nint*, int>)(*(void***)factory)[3])(factory, new D2D.SizeU((uint)size, (uint)size), 0x4 /* SIIGBF_ICONONLY */, &hbm);
        }
        finally
        {
            D2D.Release(factory);
        }
        if (hr < 0 || hbm == 0) return null;
        try
        {
            var bgra = new byte[size * size * 4];
            var info = Gdi32.TopDown32(size, size);
            nint dc = Gdi32.CreateCompatibleDC(0);
            int lines;
            fixed (byte* bits = bgra) lines = Gdi32.GetDIBits(dc, hbm, 0, (uint)size, bits, &info, 0);
            Gdi32.DeleteDC(dc);
            if (lines <= 0) return null;
            Premultiply(bgra);
            return new ShadowImage(size, size, 1, 0, bgra);
        }
        finally
        {
            Gdi32.DeleteObject(hbm);
        }
    }

    /// <summary>
    /// 外壳给的位图有时已经预乘、有时没有：有任何一个颜色分量大于 alpha 就肯定没预乘，补乘一遍；
    /// alpha 全是 0（不带透明度的老图标）就当整张不透明。
    /// </summary>
    private static void Premultiply(byte[] bgra)
    {
        bool straight = false, anyAlpha = false;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            if (a != 0) anyAlpha = true;
            if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a) straight = true;
        }
        if (!anyAlpha)
        {
            for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
            return;
        }
        if (!straight) return;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            bgra[i] = (byte)((bgra[i] * a + 127) / 255);
            bgra[i + 1] = (byte)((bgra[i + 1] * a + 127) / 255);
            bgra[i + 2] = (byte)((bgra[i + 2] * a + 127) / 255);
        }
    }

    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    /// <summary>窗口的 AppUserModelID（System.AppUserModel.ID 属性；没有为 null）。</summary>
    internal static string? AppUserModelId(nint hwnd)
    {
        Guid iid = IID_IPropertyStore;
        void* store;
        if (SHGetPropertyStoreForWindow(hwnd, &iid, &store) < 0 || store == null) return null;
        try
        {
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            byte* pv = stackalloc byte[24]; // PROPVARIANT
            new Span<byte>(pv, 24).Clear();
            int hr = ((delegate* unmanaged[Stdcall]<void*, PropertyKey*, void*, int>)(*(void***)store)[5])(store, &key, pv);
            if (hr < 0) return null;
            string? id = *(ushort*)pv == 31 /* VT_LPWSTR */ && *(char**)(pv + 8) != null ? new string(*(char**)(pv + 8)) : null;
            PropVariantClear(pv);
            return id;
        }
        finally
        {
            D2D.Release(store);
        }
    }

    private static string ExePath(nint hwnd)
    {
        uint pid = 0;
        User32.GetWindowThreadProcessId(hwnd, &pid);
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if (h == 0) return "";
        try
        {
            char* buffer = stackalloc char[1024];
            uint size = 1024;
            return Kernel32.QueryFullProcessImageNameW(h, 0, buffer, &size) ? new string(buffer, 0, (int)size) : "";
        }
        finally
        {
            Kernel32.CloseHandle(h);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHCreateItemFromParsingName(char* path, nint bindContext, Guid* iid, void** item);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(nint hwnd, Guid* iid, void** store);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(void* pv);

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>窗口所属的程序：<see cref="Key"/> 用来区分程序（AppUserModelID 带「aumid:」前缀，或 exe 路径），<see cref="Exe"/> 是进程的 exe 路径。</summary>
public readonly record struct AppId(string Key, string Exe);
