namespace GlideShell.Core.Overview;

/// <summary>
/// 此刻在托盘里、没有可见窗口的一个程序（总览底部后台程序栏里的一项，平台无关的数据）。
/// <see cref="OwnerWindow"/> + <see cref="Uid"/>（或 <see cref="IconGuid"/>）标识它的托盘图标（Shell_NotifyIcon 的标识）。
/// 栏里所有程序一视同仁：左键 = 左键单击它的托盘图标，右键 = 弹出它的托盘菜单。
/// <see cref="MainWindow"/>：GlideShell 见过它显示出来、现在藏着的主窗口（没有为 0）——左键之后程序没把窗口打开时
/// （少数程序只认双击），直接还原它兜底。
/// </summary>
public sealed record TrayApp(
    string Key,
    string Name,
    string Exe,
    long OwnerWindow,
    uint Uid,
    Guid? IconGuid,
    long MainWindow = 0,
    int ProcessId = 0);

/// <summary>托盘程序（及同一程序的相关进程）的一个顶层窗口的原始属性，由平台层从 Win32 读取。</summary>
public readonly record struct TrayWindowInfo(
    long Handle,
    string Title,
    bool Visible,
    bool Minimized,
    bool ToolWindow,
    bool AppWindow,
    bool HasOwner,
    int Width,
    int Height,
    bool Seen);

/// <summary>托盘程序的判断（纯函数，可测）：有用户看得见的窗口的不进后台程序栏（它已经在总览的窗口里）。</summary>
public static class TrayClassifier
{
    public const int MinVisibleWidth = 160, MinVisibleHeight = 120; // 与总览收录窗口的尺寸下限一致

    /// <summary>
    /// 用户看得见的窗口：会出现在总览或任务栏里（包括最小化的、在别的虚拟桌面上的）。程序有这样的窗口就不进后台程序栏。
    /// </summary>
    public static bool IsVisibleAppWindow(in TrayWindowInfo w) =>
        w.Visible && (!w.ToolWindow || w.AppWindow) && (!w.HasOwner || w.AppWindow) && w.Title.Length > 0 &&
        (w.Minimized || (w.Width >= MinVisibleWidth && w.Height >= MinVisibleHeight));

    /// <summary>
    /// 有用户看得见的窗口 → null（不进栏）；否则返回见过它显示出来、现在藏着的主窗口（取面积最大的；没有为 0）。
    /// </summary>
    public static long? HiddenMainWindow(IReadOnlyList<TrayWindowInfo> windows)
    {
        foreach (var w in windows)
            if (IsVisibleAppWindow(w)) return null;
        long best = 0, bestArea = -1;
        foreach (var w in windows)
        {
            if (!w.Seen || w.Visible || w.ToolWindow || w.Title.Length == 0) continue;
            long area = (long)w.Width * w.Height;
            if (area <= bestArea) continue;
            best = w.Handle;
            bestArea = area;
        }
        return best;
    }

    /// <summary>栏里的顺序：按名字排（按当前语言的规则；位置固定，不随枚举顺序跳动）。</summary>
    public static List<TrayApp> Order(IEnumerable<TrayApp> apps) =>
        apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Key, StringComparer.Ordinal).ToList();
}

/// <summary>按 exe 路径判断进程之间的关系（纯函数，可测）。</summary>
public static class TrayPaths
{
    // 只是装程序的容器目录：两个 exe 只因为都在它下面，不算同一个程序
    private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Files", "Program Files (x86)", "WindowsApps", "Programs", "AppData", "Local", "Roaming", "LocalLow",
        "Downloads", "Desktop", "Documents", "Temp", "Users", "ProgramData",
    };

    /// <summary>
    /// 两个 exe 是不是同一个程序的进程：同一个文件；或者一个所在的目录是另一个的上级目录（含同一目录）——
    /// Steam 的窗口在 bin\cef\…\steamwebhelper.exe 里，托盘图标在上级目录的 steam.exe。
    /// 上级目录必须是程序自己的目录：至少三层（盘符、Program Files、程序名），而且不是 Program Files、下载这类容器目录；
    /// Windows 目录下只认同一个文件（System32 里的程序互不相干）。
    /// </summary>
    public static bool Related(string exeA, string exeB, string windowsDir)
    {
        if (exeA.Length == 0 || exeB.Length == 0) return false;
        if (string.Equals(exeA, exeB, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsUnder(exeA, windowsDir) || IsUnder(exeB, windowsDir)) return false;
        string? da = Path.GetDirectoryName(exeA), db = Path.GetDirectoryName(exeB);
        if (da is null || db is null) return false;
        string shallow = da.Length <= db.Length ? da : db, deep = da.Length <= db.Length ? db : da;
        if (!IsUnderOrSame(deep, shallow)) return false;
        var parts = shallow.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && !Containers.Contains(parts[^1]);
    }

    private static bool IsUnder(string path, string dir) =>
        dir.Length > 0 && path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderOrSame(string dir, string ancestor) =>
        string.Equals(dir.TrimEnd('\\'), ancestor.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || IsUnder(dir, ancestor);
}
