using System.Runtime.InteropServices;
using GlideShell.Platform.Diagnostics;

namespace GlideShell.Platform.Shell;

/// <summary>
/// 开始菜单「所有应用」里的一个程序。<see cref="ParsingName"/> 是它在 <c>shell:AppsFolder</c> 里的标识（启动用
/// <c>shell:AppsFolder\&lt;ParsingName&gt;</c>）；<see cref="Target"/> 是快捷方式指向的 exe（商店应用没有）；
/// <see cref="PackagePath"/> 是商店应用的安装目录。
/// </summary>
public sealed record InstalledApp(string Name, string ParsingName, string? Target, string? AppUserModelId, string? PackagePath);

/// <summary>
/// 「所有应用」的索引（外壳的 <c>shell:AppsFolder</c>，与开始菜单同一来源，名字已本地化）。在专门的 STA 线程上枚举，
/// 结果整体替换；按需刷新（距上次超过一定时间才重建）。后台程序栏用它给程序取开始菜单里的名字（「微信」而不是 Weixin.exe）。
/// </summary>
public sealed unsafe partial class AppCatalog : IDisposable
{
    private sealed record Index(
        IReadOnlyList<InstalledApp> Apps,
        Dictionary<string, InstalledApp> ByTarget,
        Dictionary<string, InstalledApp> ByAumid,
        List<InstalledApp> Packaged);

    private readonly ComWorker _worker = new("GlideShell.Apps", sta: true);
    private volatile Index _index = new([], new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), []);
    private long _builtAt = long.MinValue / 2; // Environment.TickCount64
    private Task? _building;
    private readonly object _gate = new();

    public IReadOnlyList<InstalledApp> Apps => _index.Apps;

    /// <summary>上一次建索引的耗时（毫秒，诊断用；还没建过为 -1）。</summary>
    public double LastBuildMs { get; private set; } = -1;

    /// <summary>后台重建索引（距上次不到 <paramref name="maxAgeMs"/> 或正在建时什么也不做）。返回正在进行的任务。</summary>
    public Task Refresh(long maxAgeMs = 10 * 60 * 1000)
    {
        lock (_gate)
        {
            if (_building is { IsCompleted: false } running) return running;
            if (Environment.TickCount64 - _builtAt < maxAgeMs) return Task.CompletedTask;
            _building = _worker.Run(() =>
            {
                long t0 = Environment.TickCount64;
                var apps = Enumerate();
                var byTarget = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
                var byAumid = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
                var packaged = new List<InstalledApp>();
                // 同一个 exe 有几个快捷方式时取名字最短的（「Python 3.12」而不是「Python 3.12 Module Docs」）
                foreach (var app in apps.OrderBy(a => a.Name.Length))
                {
                    if (app.Target is { Length: > 0 } target) byTarget.TryAdd(target, app);
                    if (app.AppUserModelId is { Length: > 0 } aumid) byAumid.TryAdd(aumid, app);
                    if (app.PackagePath is { Length: > 0 }) packaged.Add(app);
                }
                _index = new Index(apps, byTarget, byAumid, packaged);
                _builtAt = Environment.TickCount64;
                LastBuildMs = _builtAt - t0;
                Log.Info($"程序索引：{apps.Count} 个程序，耗时 {LastBuildMs}ms");
                return true;
            });
            return _building;
        }
    }

    /// <summary>
    /// 某个进程在开始菜单里的名字：先按应用标识（商店应用的进程有），再按快捷方式指向的 exe，再按商店应用的安装目录；都找不到返回 null。
    /// </summary>
    public string? NameFor(string exe, string? appUserModelId)
    {
        var index = _index;
        if (appUserModelId is { Length: > 0 } && index.ByAumid.TryGetValue(appUserModelId, out var byId)) return byId.Name;
        if (index.ByTarget.TryGetValue(exe, out var byTarget)) return byTarget.Name;
        foreach (var app in index.Packaged)
            if (exe.StartsWith(app.PackagePath!.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return app.Name;
        return null;
    }

    // ---------- STA 线程 ----------

    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IID_IShellItem2 = new("7e9fb0d3-919f-4307-ab2e-9b1860310c93");
    private static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private const uint SIGDN_NORMALDISPLAY = 0, SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid FormatId = fmtid;
        public uint PropertyId = pid;
    }

    private static readonly PropertyKey PKEY_Link_TargetParsingPath = new(new("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);
    private static readonly PropertyKey PKEY_AppUserModel_ID = new(new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private static readonly PropertyKey PKEY_AppUserModel_PackageInstallPath = new(new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 15);

    private static List<InstalledApp> Enumerate()
    {
        var list = new List<InstalledApp>();
        Guid iidItem = IID_IShellItem;
        void* folder;
        fixed (char* p = "shell:AppsFolder")
            if (SHCreateItemFromParsingName(p, 0, &iidItem, &folder) < 0 || folder == null) return list;
        try
        {
            Guid bhid = BHID_EnumItems, iidEnum = IID_IEnumShellItems;
            void* items;
            // IShellItem::BindToHandler(IBindCtx*, REFGUID, REFIID, void**)
            if (((delegate* unmanaged[Stdcall]<void*, nint, Guid*, Guid*, void**, int>)(*(void***)folder)[3])(folder, 0, &bhid, &iidEnum, &items) < 0 || items == null)
                return list;
            try
            {
                while (true)
                {
                    void* item;
                    uint fetched = 0;
                    // IEnumShellItems::Next(ULONG, IShellItem**, ULONG*)
                    int hr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, uint*, int>)(*(void***)items)[3])(items, 1, &item, &fetched);
                    if (hr != 0 || fetched == 0) break;
                    try
                    {
                        if (Read(item) is { } app) list.Add(app);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"程序索引：读一个程序失败：{ex.Message}");
                    }
                    finally
                    {
                        Com.Release(item);
                    }
                }
            }
            finally
            {
                Com.Release(items);
            }
        }
        finally
        {
            Com.Release(folder);
        }
        return list;
    }

    private static InstalledApp? Read(void* item)
    {
        string? name = DisplayName(item, SIGDN_NORMALDISPLAY), parsing = DisplayName(item, SIGDN_PARENTRELATIVEPARSING);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(parsing)) return null;
        string? target = null, aumid = null, package = null;
        Guid iid2 = IID_IShellItem2;
        void* item2;
        if (Com.QueryInterface(item, &iid2, &item2) >= 0 && item2 != null)
        {
            try
            {
                target = GetString(item2, PKEY_Link_TargetParsingPath);
                aumid = GetString(item2, PKEY_AppUserModel_ID);
                package = GetString(item2, PKEY_AppUserModel_PackageInstallPath);
            }
            finally
            {
                Com.Release(item2);
            }
        }
        // 没有应用标识的普通快捷方式：标识本身就是「已知文件夹 GUID + 相对路径」
        if (string.IsNullOrEmpty(target) && parsing.Contains('\\')) target = KnownFolders.Resolve(parsing);
        if (target is { Length: > 0 }) target = KnownFolders.Resolve(target);
        return new InstalledApp(name.Trim(), parsing, string.IsNullOrEmpty(target) ? null : target, string.IsNullOrEmpty(aumid) ? null : aumid,
            string.IsNullOrEmpty(package) ? null : package);
    }

    private static string? DisplayName(void* item, uint sigdn)
    {
        char* s;
        // IShellItem::GetDisplayName(SIGDN, LPWSTR*)
        if (((delegate* unmanaged[Stdcall]<void*, uint, char**, int>)(*(void***)item)[5])(item, sigdn, &s) < 0) return null;
        return Com.TakeString(s);
    }

    private static string? GetString(void* item2, PropertyKey key)
    {
        char* s;
        // IShellItem2::GetString(REFPROPERTYKEY, LPWSTR*)
        if (((delegate* unmanaged[Stdcall]<void*, PropertyKey*, char**, int>)(*(void***)item2)[17])(item2, &key, &s) < 0) return null;
        return Com.TakeString(s);
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHCreateItemFromParsingName(char* path, nint bindContext, Guid* iid, void** item);

    public void Dispose() => _worker.Dispose();
}
