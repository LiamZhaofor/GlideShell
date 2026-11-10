using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Native;
using Microsoft.Win32;

namespace GlideShell.Platform.Shell;

/// <summary>设置里「后台程序栏显示哪些程序」的一项：按 exe 区分（同一程序的几个托盘图标一起显示或隐藏）。</summary>
public readonly record struct TrayAppChoice(string Exe, string Name, bool Running = true);

/// <summary>
/// 列出此刻在托盘里的程序（总览底部的后台程序栏）。新版任务栏（XAML）不再提供枚举托盘图标的接口
/// （ITrayNotify 能连上但一个图标都不回调），做法是：
/// <list type="number">
/// <item>注册表 <c>HKCU\Control Panel\NotifyIconSettings</c> 里有每个出现过的图标的 exe 与标识（UID 或 GUID）；</item>
/// <item>对上正在运行的进程，拿它的窗口逐个试 <c>Shell_NotifyIconGetRect</c>——成功就说明图标此刻在托盘，同时知道它属于哪个窗口；</item>
/// <item>这个程序（含同一程序目录下的相关进程）有可见窗口的不算（它已经在总览的窗口里）；记下它藏起来的、见过的主窗口。</item>
/// </list>
/// 全程不碰界面。任意线程调用（不用 COM）。
/// </summary>
public static unsafe partial class TrayCatalog
{
    private const string SettingsKey = @"Control Panel\NotifyIconSettings";
    private static readonly ConcurrentDictionary<long, byte> s_seen = new();

    /// <summary>上一次枚举的统计（诊断用）。</summary>
    public readonly record struct Stats(int Registered, int Live, int Shown, double Ms, int Probes = 0, double ProbeMs = 0, double NameMs = 0, double ClassifyMs = 0);

    public static Stats LastStats { get; private set; }

    /// <summary>
    /// 记下总览里出现过的窗口：之后它藏起来了（关到托盘），就能确定它是这个程序的主窗口。
    /// 句柄失效后由下次清理去掉（数量超过上限时）。
    /// </summary>
    public static void MarkSeen(IEnumerable<long> windows)
    {
        foreach (long w in windows) s_seen[w] = 0;
        if (s_seen.Count <= 2000) return;
        foreach (long w in s_seen.Keys)
            if (!User32.IsWindow((nint)w)) s_seen.TryRemove(w, out _);
    }

    // 图标的宿主窗口（按图标标识缓存，程序不退出就不变：下次一次查询就命中）；此刻不在托盘的图标记住进程，30 秒内不再逐个窗口试
    private static readonly ConcurrentDictionary<string, nint> s_owners = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (uint Pid, long Until)> s_absent = new(StringComparer.OrdinalIgnoreCase);

    // 托盘进程不是开始菜单里那个程序时，按应用标识去找名字（Windows 安全中心的托盘图标属于 SecurityHealthSystray.exe）
    private static readonly Dictionary<string, string> AliasIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SecurityHealthSystray.exe"] = "Microsoft.SecHealthUI_8wekyb3d8bbwe!SecHealthUI",
    };

    private readonly record struct Entry(string Exe, uint? Uid, Guid? IconGuid, string Tooltip);

    private sealed class Proc(uint pid, string exe)
    {
        public uint Pid { get; } = pid;
        public string Exe { get; } = exe;
        public List<nint> TopLevel { get; } = [];
        public List<nint> MessageOnly { get; } = [];
    }

    [ThreadStatic] private static List<nint>? t_handles;

    /// <summary>
    /// 此刻在托盘里、没有可见窗口的程序（按栏里的顺序）。GlideShell 自己与资源管理器的系统图标不算。
    /// <paramref name="names"/> 给出开始菜单里的名字（没有就用 exe 的文件说明）。
    /// <paramref name="includeVisible"/>：有可见窗口的也列出来（设置里选「栏里显示哪些程序」用）。
    /// </summary>
    public static List<TrayApp> Snapshot(AppCatalog? names = null, bool includeVisible = false)
    {
        long t0 = Stopwatch.GetTimestamp();
        var entries = ReadSettings();
        var procs = Processes();
        var byExe = new Dictionary<string, List<Proc>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in procs.Values)
        {
            if (!byExe.TryGetValue(p.Exe, out var list)) byExe[p.Exe] = list = [];
            list.Add(p);
        }
        uint self = (uint)Environment.ProcessId;
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var apps = new List<TrayApp>();
        int live = 0;
        s_probes = 0;
        s_probeTicks = 0;
        long nameTicks = 0, classifyTicks = 0;
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (!byExe.TryGetValue(e.Exe, out var owners)) continue; // 没在运行
            nint owner = 0;
            Proc? proc = null;
            if (e.IconGuid is { } guid)
            {
                if (!GetRect(0, 0, guid, out _)) continue;
                proc = owners[0];
            }
            else if (e.Uid is { } uid)
            {
                string id = $"{e.Exe}|{uid}";
                if (s_owners.TryGetValue(id, out nint cached) && owners.FirstOrDefault(p => p.TopLevel.Contains(cached) || p.MessageOnly.Contains(cached)) is { } cachedProc &&
                    GetRect(cached, uid, null, out _))
                {
                    owner = cached;
                    proc = cachedProc;
                }
                else if (s_absent.TryGetValue(id, out var absent) && Environment.TickCount64 < absent.Until && owners.Any(p => p.Pid == absent.Pid))
                {
                    continue; // 刚试过，图标不在
                }
                else
                {
                    foreach (var p in owners)
                    {
                        foreach (nint h in Candidates(p))
                            if (GetRect(h, uid, null, out _))
                            {
                                owner = h;
                                proc = p;
                                break;
                            }
                        if (proc is not null) break;
                    }
                    if (proc is null)
                    {
                        s_absent[id] = (owners[0].Pid, Environment.TickCount64 + 30_000);
                        continue;
                    }
                    s_owners[id] = owner;
                    s_absent.TryRemove(id, out _);
                }
            }
            else continue;
            live++;
            if (proc.Pid == self || Path.GetFileName(e.Exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
            string key = $"{e.Exe}|{e.IconGuid?.ToString() ?? e.Uid?.ToString() ?? ""}";
            if (!seenKeys.Add(key)) continue;

            // 同一程序的全部顶层窗口（含同一程序目录下的其他进程：Steam 的窗口在 steamwebhelper.exe 里）
            long c0 = Stopwatch.GetTimestamp();
            var infos = new List<TrayWindowInfo>();
            foreach (var p in procs.Values)
                if (p.Pid == proc.Pid || TrayPaths.Related(p.Exe, e.Exe, windowsDir))
                    foreach (nint h in p.TopLevel)
                        infos.Add(Describe(h));
            long? main = TrayClassifier.HiddenMainWindow(infos);
            classifyTicks += Stopwatch.GetTimestamp() - c0;
            if (main is null && !includeVisible) continue; // 有可见窗口：已经在总览里
            long n0 = Stopwatch.GetTimestamp();
            string name = Name(e, proc.Pid, names);
            nameTicks += Stopwatch.GetTimestamp() - n0;
            apps.Add(new TrayApp(key, name, e.Exe, owner, e.Uid ?? 0, e.IconGuid, main ?? 0, (int)proc.Pid));
        }
        var ordered = TrayClassifier.Order(apps);
        if (!includeVisible)
            LastStats = new Stats(entries.Count, live, ordered.Count, Stopwatch.GetElapsedTime(t0).TotalMilliseconds, s_probes, s_probeTicks * 1000.0 / Stopwatch.Frequency,
            nameTicks * 1000.0 / Stopwatch.Frequency, classifyTicks * 1000.0 / Stopwatch.Frequency);
        return ordered;
    }

    /// <summary>与 <paramref name="app"/> 同一个程序的进程（它自己，加上同一程序目录下的其他进程：Steam 的窗口在 steamwebhelper.exe 里）。</summary>
    public static HashSet<uint> RelatedProcesses(TrayApp app)
    {
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pids = new HashSet<uint> { (uint)app.ProcessId };
        foreach (var p in Processes().Values)
            if (TrayPaths.Related(p.Exe, app.Exe, windowsDir)) pids.Add(p.Pid);
        return pids;
    }

    /// <summary>
    /// 这些进程此刻显示出来的窗口（用户看得见、没有最小化；按 Z 序取最上面的，前台窗口优先）。没有返回 0。
    /// 左键托盘图标之后用它等程序把窗口打开。
    /// </summary>
    public static nint ShownWindow(IReadOnlySet<uint> pids)
    {
        nint fg = User32.GetForegroundWindow();
        if (IsShown(fg, pids)) return fg;
        t_handles = [];
        User32.EnumWindows(&Collect, 0);
        var handles = t_handles;
        t_handles = null;
        foreach (nint h in handles)
            if (IsShown(h, pids)) return h;
        return 0;
    }

    private static bool IsShown(nint h, IReadOnlySet<uint> pids)
    {
        if (h == 0 || !User32.IsWindowVisible(h) || User32.IsIconic(h)) return false;
        uint pid = 0;
        User32.GetWindowThreadProcessId(h, &pid);
        return pids.Contains(pid) && TrayClassifier.IsVisibleAppWindow(Describe(h));
    }

    /// <summary>程序（托盘图标宿主窗口的进程）还在运行。不知道进程号、或打不开进程时按还在算。</summary>
    public static bool IsRunning(TrayApp app)
    {
        if (app.ProcessId == 0) return true;
        nint h = Kernel32.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, 0, (uint)app.ProcessId);
        if (h == 0) return Marshal.GetLastPInvokeError() != 87; // ERROR_INVALID_PARAMETER = 没有这个进程了
        try
        {
            uint code;
            return !GetExitCodeProcess(h, &code) || code == 259 /* STILL_ACTIVE */;
        }
        finally
        {
            Kernel32.CloseHandle(h);
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, uint* code);

    /// <summary>托盘图标此刻在屏幕上的位置（在「^」溢出区里藏着时是「^」按钮的位置）。图标已经没了返回 false。</summary>
    public static bool TryGetRect(TrayApp app, out RectD rect) => GetRect((nint)app.OwnerWindow, app.Uid, app.IconGuid, out rect);

    // ---------- 注册表 ----------

    private static List<Entry> ReadSettings()
    {
        var list = new List<Entry>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (root is null) return list;
            foreach (string sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("ExecutablePath") is not string path || path.Length == 0) continue;
                uint? uid = k.GetValue("UID") is int u ? unchecked((uint)u) : null;
                Guid? guid = k.GetValue("IconGuid") is string g && Guid.TryParse(g, out var parsed) && parsed != Guid.Empty ? parsed : null;
                if (uid is null && guid is null) continue;
                list.Add(new Entry(KnownFolders.Resolve(path), uid, guid, k.GetValue("InitialTooltip") as string ?? ""));
            }
        }
        catch (Exception)
        {
            // 读不了就当没有
        }
        return list;
    }

    // ---------- 窗口与进程 ----------

    private static Dictionary<uint, Proc> Processes()
    {
        var procs = new Dictionary<uint, Proc>();
        var paths = new Dictionary<uint, string?>();
        void Add(nint h, bool messageOnly)
        {
            uint pid = 0;
            User32.GetWindowThreadProcessId(h, &pid);
            if (pid == 0) return;
            if (!procs.TryGetValue(pid, out var p))
            {
                if (!paths.TryGetValue(pid, out string? exe)) paths[pid] = exe = ExePath(pid);
                if (exe is null) return;
                procs[pid] = p = new Proc(pid, exe);
            }
            (messageOnly ? p.MessageOnly : p.TopLevel).Add(h);
        }
        t_handles = [];
        User32.EnumWindows(&Collect, 0);
        var handles = t_handles;
        t_handles = null;
        foreach (nint h in handles) Add(h, false);
        nint m = 0;
        int guard = 0;
        while ((m = FindWindowExW(-3 /* HWND_MESSAGE */, m, null, null)) != 0 && ++guard < 5000) Add(m, true);
        return procs;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint lParam)
    {
        t_handles!.Add(hwnd);
        return 1;
    }

    /// <summary>可能是托盘图标宿主的窗口：类名像托盘宿主的先试（多数程序一两次就命中），再试其余隐藏窗口与仅消息窗口。</summary>
    private static IEnumerable<nint> Candidates(Proc p)
    {
        var all = p.TopLevel.Concat(p.MessageOnly).ToList();
        foreach (nint h in all)
            if (LooksLikeTrayHost(WindowCatalog.ClassName(h))) yield return h;
        foreach (nint h in all)
            if (!LooksLikeTrayHost(WindowCatalog.ClassName(h))) yield return h;
    }

    private static bool LooksLikeTrayHost(string cls) =>
        cls.Contains("Tray", StringComparison.OrdinalIgnoreCase) || cls.Contains("Notify", StringComparison.OrdinalIgnoreCase);

    private static TrayWindowInfo Describe(nint h)
    {
        long ex = User32.GetWindowLongPtrW(h, User32.GWL_EXSTYLE);
        User32.RECT r;
        User32.GetWindowRect(h, &r);
        return new TrayWindowInfo(h, Title(h), User32.IsWindowVisible(h), User32.IsIconic(h),
            ToolWindow: (ex & User32.WS_EX_TOOLWINDOW) != 0, AppWindow: (ex & User32.WS_EX_APPWINDOW) != 0,
            HasOwner: User32.GetWindow(h, User32.GW_OWNER) != 0, Width: r.Right - r.Left, Height: r.Bottom - r.Top, Seen: s_seen.ContainsKey(h));
    }

    /// <summary>窗口标题（不给窗口发消息：藏起来的窗口所在的程序可能没响应，GetWindowText 会卡住）。</summary>
    private static string Title(nint h)
    {
        char* buffer = stackalloc char[256];
        int n = InternalGetWindowText(h, buffer, 256);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    private static string? ExePath(uint pid)
    {
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if (h == 0) return null;
        try
        {
            char* buffer = stackalloc char[1024];
            uint size = 1024;
            return Kernel32.QueryFullProcessImageNameW(h, 0, buffer, &size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            Kernel32.CloseHandle(h);
        }
    }

    /// <summary>商店应用进程的应用标识（普通程序没有，返回 null）。</summary>
    private static string? AppUserModelId(uint pid)
    {
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if (h == 0) return null;
        try
        {
            char* buffer = stackalloc char[256];
            uint length = 256;
            return GetApplicationUserModelId(h, &length, buffer) == 0 && length > 1 ? new string(buffer, 0, (int)length - 1) : null;
        }
        finally
        {
            Kernel32.CloseHandle(h);
        }
    }

    /// <summary>
    /// 栏里显示的名字：开始菜单里的名字（「微信」）；没有就用图标注册时的提示文字，但只取像名字的（短、一行、不带版本号与状态）；
    /// 再没有用 exe 的文件说明，最后用文件名。
    /// </summary>
    private static string Name(Entry e, uint pid, AppCatalog? names)
    {
        string? aumid = AliasIds.TryGetValue(Path.GetFileName(e.Exe), out var alias) ? alias : AppUserModelId(pid);
        if (names?.NameFor(e.Exe, aumid) is { } listed) return listed;
        string tip = e.Tooltip.Trim();
        if (tip.Length is > 0 and <= 24 && LooksLikeName(tip)) return tip;
        return FileName(e.Exe);
    }

    private static bool LooksLikeName(string tip)
    {
        if (tip.IndexOfAny(['\n', '\r', ':', '：', '(', '（', '|', '·']) >= 0 || tip.Contains(" - ") || tip.Contains("...")) return false;
        // 带版本号的（「PowerToys v0.101」「Foo 1.2.3」）
        for (int i = 0; i + 2 < tip.Length; i++)
            if (char.IsDigit(tip[i]) && tip[i + 1] == '.' && char.IsDigit(tip[i + 2])) return false;
        return true;
    }

    /// <summary>exe 的文件说明（去掉「.Runner」这类后缀），没有就用文件名。</summary>
    private static string FileName(string exe)
    {
        try
        {
            string? desc = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(desc) && desc.Length <= 40) return desc;
        }
        catch (Exception)
        {
            // 读不了版本信息
        }
        return Path.GetFileNameWithoutExtension(exe);
    }

    // ---------- Shell_NotifyIconGetRect ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [ThreadStatic] private static int s_probes;
    [ThreadStatic] private static long s_probeTicks;

    private static bool GetRect(nint owner, uint uid, Guid? guid, out RectD rect)
    {
        s_probes++;
        long t = Stopwatch.GetTimestamp();
        try
        {
            return GetRectCore(owner, uid, guid, out rect);
        }
        finally
        {
            s_probeTicks += Stopwatch.GetTimestamp() - t;
        }
    }

    private static bool GetRectCore(nint owner, uint uid, Guid? guid, out RectD rect)
    {
        var id = new NotifyIconIdentifier { cbSize = (uint)sizeof(NotifyIconIdentifier), hWnd = owner, uID = uid, guidItem = guid ?? Guid.Empty };
        User32.RECT r;
        if (Shell_NotifyIconGetRect(&id, &r) != 0)
        {
            rect = default;
            return false;
        }
        rect = new RectD(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return true;
    }

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconGetRect(NotifyIconIdentifier* identifier, User32.RECT* rect);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string? className, string? title);

    [LibraryImport("user32.dll")]
    private static partial int InternalGetWindowText(nint hwnd, char* buffer, int maxCount);

    [LibraryImport("kernel32.dll")]
    private static partial int GetApplicationUserModelId(nint process, uint* length, char* buffer);
}
