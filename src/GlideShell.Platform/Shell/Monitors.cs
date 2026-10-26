using System.Runtime.InteropServices;
using GlideShell.Core.Geometry;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Shell;

/// <summary>一块显示器：整屏、工作区（不含任务栏）、DPI 缩放（1.0 = 96 DPI）。坐标均为物理像素。</summary>
public sealed record MonitorInfo(nint Handle, int Index, RectD Bounds, RectD WorkArea, double DpiScale, bool IsPrimary);

public static unsafe class Monitors
{
    [ThreadStatic] private static List<nint>? t_handles;

    /// <summary>枚举当前所有显示器（主屏不一定是第一个；<see cref="MonitorInfo.Index"/> 就是列表下标）。</summary>
    public static List<MonitorInfo> Enumerate()
    {
        t_handles = [];
        User32.EnumDisplayMonitors(0, null, &Collect, 0);
        var result = new List<MonitorInfo>();
        foreach (var h in t_handles)
        {
            var mi = new User32.MONITORINFO { cbSize = (uint)sizeof(User32.MONITORINFO) };
            if (!User32.GetMonitorInfo(h, &mi)) continue;
            uint dx = 96, dy = 96;
            Shcore.GetDpiForMonitor(h, Shcore.MDT_EFFECTIVE_DPI, &dx, &dy);
            result.Add(new MonitorInfo(h, result.Count, ToRect(mi.rcMonitor), ToRect(mi.rcWork), dx / 96.0,
                (mi.dwFlags & User32.MONITORINFOF_PRIMARY) != 0));
        }
        t_handles = null;
        return result;
    }

    internal static RectD ToRect(User32.RECT r) => RectD.FromEdges(r.Left, r.Top, r.Right, r.Bottom);

    [UnmanagedCallersOnly]
    private static int Collect(nint monitor, nint hdc, User32.RECT* rect, nint lParam)
    {
        t_handles?.Add(monitor);
        return 1;
    }
}
