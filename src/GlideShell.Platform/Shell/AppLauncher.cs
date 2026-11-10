using System.Runtime.InteropServices;
using GlideShell.Core.Search;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Shell;

/// <summary>
/// 总览搜索的程序来源与启动：开始菜单「所有应用」（<see cref="AppCatalog"/>）里值得搜到的程序，按名字去重；
/// 启动用 <c>shell:AppsFolder\&lt;ParsingName&gt;</c>（和开始菜单点它一样，商店应用、桌面程序都行），在专门的 STA 线程上执行；
/// 记录从搜索里打开各程序的次数（本机配置目录 search-usage.txt），常用的排前面。
/// 另有用户在设置里手动添加的程序（文件路径，<see cref="SetExtraApps"/>）：标识是「file:」加路径，直接运行那个文件。
/// </summary>
public sealed unsafe partial class AppLauncher : IDisposable
{
    private readonly AppCatalog _catalog;
    private readonly ComWorker _worker = new("GlideShell.Launch", sta: true);
    private IReadOnlyList<InstalledApp>? _builtFrom;
    private IReadOnlyList<string> _extra = [];
    private IReadOnlyList<string>? _builtExtra;
    private const string FilePrefix = "file:";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_names = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SearchEntry> _entries = [];
    private Dictionary<string, InstalledApp> _byKey = [];
    private AppUsage? _usage;
    private readonly string _usagePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlideShell", "search-usage.txt");

    public AppLauncher(AppCatalog catalog) => _catalog = catalog;

    private readonly object _gate = new();

    /// <summary>
    /// 可搜的程序（任意线程；程序索引换过就重建）。名字拆字、取拼音约 0.1 秒（第一次还要读拼音表），
    /// 所以程序索引建好后先在后台调用一次（<see cref="Prebuild"/>），别让第一次按键等它。
    /// </summary>
    public IReadOnlyList<SearchEntry> Entries
    {
        get
        {
            lock (_gate) return Build();
        }
    }

    /// <summary>设置里手动添加的程序（文件完整路径）；换了就在下次取 <see cref="Entries"/> 时重建。</summary>
    public void SetExtraApps(IReadOnlyList<string> paths)
    {
        lock (_gate) _extra = paths;
    }

    /// <summary>程序索引建好后在后台把可搜的程序准备好。</summary>
    public void Prebuild() => _ = _catalog.Refresh().ContinueWith(_ => Entries, TaskScheduler.Default);

    private IReadOnlyList<SearchEntry> Build()
    {
        {
            var apps = _catalog.Apps;
            if (ReferenceEquals(apps, _builtFrom) && ReferenceEquals(_extra, _builtExtra)) return _entries;
            var entries = new List<SearchEntry>(apps.Count);
            var byKey = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var app in apps)
            {
                if (app.Name.Length == 0 || app.ParsingName.Length == 0 || !AppSearch.IsProgram(app.Name, app.Target)) continue;
                if (!names.Add(app.Name) || !byKey.TryAdd(app.ParsingName, app)) continue; // 同名的只留一个
                entries.Add(new SearchEntry(app.ParsingName, app.Name));
            }
            // 手动添加的：文件还在、开始菜单里没有同一个 exe 的才加（名字可以与开始菜单里的重名）
            var targets = new HashSet<string>(apps.Where(a => !string.IsNullOrEmpty(a.Target)).Select(a => a.Target!), StringComparer.OrdinalIgnoreCase);
            foreach (string path in _extra)
            {
                if (!File.Exists(path) || targets.Contains(path)) continue;
                entries.Add(new SearchEntry(FilePrefix + path, DisplayName(path)));
            }
            _builtFrom = apps;
            _builtExtra = _extra;
            _entries = entries;
            _byKey = byKey;
            return entries;
        }
    }

    /// <summary>UI 线程：从搜索里打开各程序的记录（第一次用时从文件读）。</summary>
    public AppUsage Usage
    {
        get
        {
            if (_usage is not null) return _usage;
            try
            {
                _usage = File.Exists(_usagePath) ? AppUsage.Parse(File.ReadAllText(_usagePath)) : new AppUsage();
            }
            catch (Exception ex)
            {
                Log.Warn($"读取搜索记录失败：{ex.Message}");
                _usage = new AppUsage();
            }
            return _usage;
        }
    }

    /// <summary>结果里这一项的程序（取图标、启动用）。</summary>
    public InstalledApp? App(SearchEntry entry)
    {
        lock (_gate) return _byKey.GetValueOrDefault(entry.Key);
    }

    /// <summary>程序图标的标识（与总览里窗口的图标同一个缓存：按 AppsFolder 里的标识取外壳给的图标）。</summary>
    public static AppId IconId(InstalledApp app) => new("aumid:" + app.ParsingName, app.Target ?? "");

    /// <summary>结果里这一项的图标标识：开始菜单的程序按 AppsFolder 标识，手动添加的按文件路径（外壳给的文件图标）。</summary>
    public AppId? IconOf(SearchEntry entry) =>
        entry.Key.StartsWith(FilePrefix, StringComparison.Ordinal) ? new AppId(entry.Key[FilePrefix.Length..], entry.Key[FilePrefix.Length..])
        : App(entry) is { } app ? IconId(app) : null;

    /// <summary>
    /// 手动添加的程序显示的名字：exe 的文件说明（太长或没有就不用），否则文件名（不带扩展名）。读一次缓存。
    /// </summary>
    public static string DisplayName(string path) => s_names.GetOrAdd(path, static p =>
    {
        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string? desc = System.Diagnostics.FileVersionInfo.GetVersionInfo(p).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(desc) && desc.Length <= 40) return desc;
            }
            catch (Exception)
            {
                // 读不了版本信息：用文件名
            }
        }
        return Path.GetFileNameWithoutExtension(p);
    });

    /// <summary>
    /// UI 线程：打开程序并记一次使用。结果在任务里：null = 成功，否则是失败原因。
    /// 启动前允许任何进程把窗口切到前台（总览覆盖层从不激活，新程序的窗口要能自己到最前面）。
    /// </summary>
    public Task<string?> Launch(SearchEntry entry)
    {
        var usage = Usage;
        usage.Record(entry.Key, DateTime.UtcNow);
        string text = usage.Serialize();
        string path = _usagePath;
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            catch (Exception ex)
            {
                Log.Warn($"保存搜索记录失败：{ex.Message}");
            }
        });
        // 手动添加的直接运行那个文件（工作目录是它所在的文件夹，和双击它一样），开始菜单的按 AppsFolder 标识打开
        bool isFile = entry.Key.StartsWith(FilePrefix, StringComparison.Ordinal);
        string target = isFile ? entry.Key[FilePrefix.Length..] : @"shell:AppsFolder\" + entry.Key;
        string directory = isFile ? Path.GetDirectoryName(target) ?? "" : "";
        return _worker.Run(() =>
        {
            User32.AllowSetForegroundWindow(unchecked((uint)-1) /* ASFW_ANY */);
            fixed (char* file = target)
            fixed (char* verb = "open")
            fixed (char* dir = directory)
            {
                var info = new ShellExecuteInfo
                {
                    Size = (uint)sizeof(ShellExecuteInfo),
                    Mask = 0x00000400 /* SEE_MASK_FLAG_NO_UI */ | 0x00000100 /* SEE_MASK_NOASYNC */,
                    Verb = verb,
                    File = file,
                    Directory = directory.Length > 0 ? dir : null,
                    Show = 1, // SW_SHOWNORMAL
                };
                if (ShellExecuteExW(&info)) return (string?)null;
                return $"0x{Marshal.GetLastPInvokeError():X}";
            }
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellExecuteInfo
    {
        public uint Size, Mask;
        public nint Window;
        public char* Verb, File, Parameters, Directory;
        public int Show;
        public nint InstApp, IdList;
        public char* Class;
        public nint ClassKey;
        public uint HotKey;
        public nint IconOrMonitor, Process;
    }

    [LibraryImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellExecuteExW(ShellExecuteInfo* info);

    public void Dispose() => _worker.Dispose();
}
