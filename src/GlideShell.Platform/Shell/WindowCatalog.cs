using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Shell;

/// <summary>枚举当前虚拟桌面上应进入总览的顶层窗口（按 Z 序，最上层在前）。</summary>
public static unsafe class WindowCatalog
{
    public const int MaxWindows = 40; // DWM 缩略图数量与布局可读性的上限

    private static readonly Guid VirtualDesktopManagerClsid = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");
    private static readonly Guid VirtualDesktopManagerIid = typeof(IVirtualDesktopManager).GUID;
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    [ThreadStatic] private static List<nint>? t_handles;

    /// <param name="ownWindows">GlideShell 自己的窗口（覆盖层等），一律排除。</param>
    /// <param name="rejections">传入列表时记录每个被排除窗口的原因（诊断用）。</param>
    /// <param name="allDesktops">
    /// 跨桌面枚举（切换桌面用）：也收录其他虚拟桌面上的窗口，并填写 <see cref="OverviewWindow.DesktopId"/>；
    /// 不属于任何桌面的窗口（工具窗等）和被隐藏的当前桌面窗口仍然排除。
    /// </param>
    public static List<OverviewWindow> Snapshot(IReadOnlyList<MonitorInfo> monitors, ICollection<nint>? ownWindows = null,
        List<(string Window, string Reason)>? rejections = null, bool allDesktops = false)
    {
        t_handles = [];
        User32.EnumWindows(&Collect, 0);
        var handles = t_handles;
        t_handles = null;

        nint foreground = User32.GetForegroundWindow();
        var processNames = new Dictionary<uint, string>();
        var result = new List<OverviewWindow>();

        foreach (nint hwnd in handles)
        {
            if (result.Count >= MaxWindows) break;

            // 第一轮：只用本进程内的廉价查询过滤（绝大多数窗口在这里被排除）
            bool visible = User32.IsWindowVisible(hwnd);
            if (!visible && rejections is null) continue;
            string cls = ClassName(hwnd);
            long ex = User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE);
            int cloaked = 0;
            Dwmapi.DwmGetWindowAttribute(hwnd, Dwmapi.DWMWA_CLOAKED, &cloaked, sizeof(int));
            bool minimized = User32.IsIconic(hwnd);
            MonitorInfo? monitor = minimized ? null : MonitorOf(User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST), monitors);
            var bounds = minimized ? RestoreBounds(hwnd, monitors, out monitor) : VisibleBounds(hwnd);
            var probe = new WindowProbe(
                Visible: visible,
                ToolWindow: (ex & User32.WS_EX_TOOLWINDOW) != 0,
                AppWindow: (ex & User32.WS_EX_APPWINDOW) != 0,
                NoActivate: (ex & User32.WS_EX_NOACTIVATE) != 0,
                HasOwner: User32.GetWindow(hwnd, User32.GW_OWNER) != 0,
                Cloaked: cloaked,
                ClassName: cls,
                TitleLength: User32.GetWindowTextLengthW(hwnd),
                Width: (int)bounds.Width,
                Height: (int)bounds.Height,
                OnCurrentDesktop: null,
                IsOwnWindow: ownWindows?.Contains(hwnd) == true);

            // 第二轮：只对候选窗口做跨进程的虚拟桌面查询（每次约几十微秒，逐个查 400 多个窗口要二十多毫秒）
            Guid? desktopId = null;
            if (WindowFilter.Reject(probe, allDesktops) is null && VirtualDesktops is { } vdm)
            {
                int hr = vdm.IsWindowOnCurrentVirtualDesktop(hwnd, out int on);
                if (hr == 0) probe = probe with { OnCurrentDesktop = on != 0 };
                else if (hr is RpcDisconnected or RpcServerUnavailable) s_vdmTried = false; // Explorer 重启过：下次重新连接
                if (allDesktops && vdm.GetWindowDesktopId(hwnd, out Guid id) == 0 && id != Guid.Empty) desktopId = id;
            }

            if (WindowFilter.Reject(probe, allDesktops) is { } reason)
            {
                rejections?.Add(($"{Title(hwnd)} [{cls}]", reason));
                continue;
            }
            if (allDesktops)
            {
                // 不属于任何桌面（GetWindowDesktopId 失败）的窗口不参与滑动；
                // 在当前桌面上却被外壳隐藏的窗口（例如挂起的应用框架）也不画
                if (desktopId is null || (probe.OnCurrentDesktop == true && (cloaked & 2) != 0))
                {
                    rejections?.Add(($"{Title(hwnd)} [{cls}]", "不属于任何虚拟桌面或已隐藏"));
                    continue;
                }
            }

            uint pid = 0;
            User32.GetWindowThreadProcessId(hwnd, &pid);
            if (!processNames.TryGetValue(pid, out string? process)) processNames[pid] = process = ProcessName(pid);
            result.Add(new OverviewWindow(hwnd, Title(hwnd), bounds, minimized, hwnd == foreground, monitor?.Index ?? 0, process, desktopId));
        }
        return result;
    }

    public static string Title(nint hwnd)
    {
        int len = User32.GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        var buffer = new char[len + 1];
        fixed (char* p = buffer)
        {
            int n = User32.GetWindowTextW(hwnd, p, buffer.Length);
            return new string(p, 0, Math.Max(0, n));
        }
    }

    public static string ClassName(nint hwnd)
    {
        char* buffer = stackalloc char[256];
        int n = User32.GetClassNameW(hwnd, buffer, 256);
        return new string(buffer, 0, Math.Max(0, n));
    }

    public static string ProcessName(uint pid)
    {
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if (h == 0) return "";
        try
        {
            char* buffer = stackalloc char[1024];
            uint size = 1024;
            if (!Kernel32.QueryFullProcessImageNameW(h, 0, buffer, &size)) return "";
            return Path.GetFileNameWithoutExtension(new string(buffer, 0, (int)size));
        }
        finally
        {
            Kernel32.CloseHandle(h);
        }
    }

    /// <summary>可见边界（去掉 Win10/11 的隐形缩放边框），比 GetWindowRect 更贴近用户看到的窗口。</summary>
    public static RectD VisibleBounds(nint hwnd)
    {
        User32.RECT r;
        if (Dwmapi.DwmGetWindowAttribute(hwnd, Dwmapi.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(User32.RECT)) == 0 && r.Width > 0)
            return Monitors.ToRect(r);
        User32.GetWindowRect(hwnd, &r);
        return Monitors.ToRect(r);
    }

    /// <summary>
    /// 最小化窗口的还原位置。Win11 最小化窗口的 GetWindowRect 只有 237×40，必须用 WINDOWPLACEMENT；
    /// 而 rcNormalPosition 是相对所在显示器工作区的坐标，要加上工作区相对整屏的偏移（任务栏在上 / 左时才有差别）。
    /// </summary>
    private static RectD RestoreBounds(nint hwnd, IReadOnlyList<MonitorInfo> monitors, out MonitorInfo? monitor)
    {
        var wp = new User32.WINDOWPLACEMENT { length = (uint)sizeof(User32.WINDOWPLACEMENT) };
        monitor = null;
        if (!User32.GetWindowPlacement(hwnd, &wp)) return default;
        var n = wp.rcNormalPosition;
        nint hmon = User32.MonitorFromRect(&n, User32.MONITOR_DEFAULTTONEAREST);
        monitor = MonitorOf(hmon, monitors);
        double dx = 0, dy = 0;
        if (monitor is not null)
        {
            dx = monitor.WorkArea.Left - monitor.Bounds.Left;
            dy = monitor.WorkArea.Top - monitor.Bounds.Top;
        }
        return Monitors.ToRect(n).Offset(dx, dy);
    }

    private static MonitorInfo? MonitorOf(nint hmon, IReadOnlyList<MonitorInfo> monitors)
    {
        foreach (var m in monitors)
            if (m.Handle == hmon) return m;
        return monitors.Count > 0 ? monitors[0] : null;
    }

    // 已文档化的虚拟桌面查询接口：创建一次、复用（只在 UI 线程上使用）
    private const int RpcDisconnected = unchecked((int)0x80010108);
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);
    private static IVirtualDesktopManager? s_vdm;
    private static bool s_vdmTried;

    /// <summary>已文档化的虚拟桌面查询接口是否可用（诊断用）。</summary>
    public static bool VirtualDesktopQueryAvailable => VirtualDesktops is not null;

    private static IVirtualDesktopManager? VirtualDesktops
    {
        get
        {
            if (!s_vdmTried)
            {
                s_vdmTried = true;
                s_vdm = TryCreateVirtualDesktopManager();
            }
            return s_vdm;
        }
    }

    private static IVirtualDesktopManager? TryCreateVirtualDesktopManager()
    {
        Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED); // 已初始化（含 STA）时返回非 0，忽略即可
        Guid clsid = VirtualDesktopManagerClsid, iid = VirtualDesktopManagerIid;
        void* unknown;
        if (Ole32.CoCreateInstance(&clsid, 0, Ole32.CLSCTX_ALL, &iid, &unknown) != 0 || unknown is null) return null;
        try
        {
            return (IVirtualDesktopManager)ComWrappers.GetOrCreateObjectForComInstance((nint)unknown, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release((nint)unknown);
        }
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint lParam)
    {
        t_handles?.Add(hwnd);
        return 1;
    }
}
