using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace GlideShell.Platform.Native;

// 窗口枚举、显示器、前台事件、进程信息与外壳状态查询。
internal static unsafe partial class User32
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_APPWINDOW = 0x00040000;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const uint GW_OWNER = 4;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    public const uint WPF_RESTORETOMAXIMIZED = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        /// <summary>还原态位置，注意是「工作区坐标」（相对所在显示器的工作区），不是屏幕坐标。</summary>
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const uint MONITORINFOF_PRIMARY = 1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowTextLengthW(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowTextW(nint hwnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    public static partial int GetClassNameW(nint hwnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hwnd, uint cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowPlacement(nint hwnd, WINDOWPLACEMENT* placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, RECT* rect);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, uint* processId);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromRect(RECT* rect, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, MONITORINFO* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, RECT* clip, delegate* unmanaged<nint, nint, RECT*, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);
}

internal static unsafe partial class Dwmapi
{
    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint DWMWA_CLOAKED = 14;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);
}

internal static unsafe partial class Kernel32
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("kernel32.dll")]
    public static partial nint OpenProcess(uint access, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* buffer, uint* size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);
}

internal static unsafe partial class Shell32
{
    // QUERY_USER_NOTIFICATION_STATE
    public const int QUNS_BUSY = 2;                    // 全屏应用在运行，或开启了演示设置
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3; // 独占全屏 D3D 应用
    public const int QUNS_PRESENTATION_MODE = 4;

    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(int* state);
}

internal static unsafe partial class Shcore
{
    public const int MDT_EFFECTIVE_DPI = 0;

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint monitor, int dpiType, uint* dpiX, uint* dpiY);
}

internal static unsafe partial class Ole32
{
    public const uint COINIT_MULTITHREADED = 0x0;
    public const uint CLSCTX_ALL = 0x17;

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, void** instance);
}

/// <summary>已文档化的虚拟桌面查询接口（只读部分即可满足窗口过滤）。</summary>
[GeneratedComInterface]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")] // 旧原型误写成 a5cd82ff（S3 发现），导致查询一直失败
internal partial interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(nint topLevelWindow, out int onCurrentDesktop);

    [PreserveSig]
    int GetWindowDesktopId(nint topLevelWindow, out Guid desktopId);

    [PreserveSig]
    int MoveWindowToDesktop(nint topLevelWindow, in Guid desktopId);
}
