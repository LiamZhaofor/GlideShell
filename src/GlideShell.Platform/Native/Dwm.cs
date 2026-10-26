using System.Runtime.InteropServices;

namespace GlideShell.Platform.Native;

// DWM 缩略图、合成时钟，以及覆盖层用到的窗口 / GDI / 键盘钩子 API。
internal static unsafe partial class Dwmapi
{
    public const uint DWM_TNP_RECTDESTINATION = 0x01;
    public const uint DWM_TNP_RECTSOURCE = 0x02;
    public const uint DWM_TNP_OPACITY = 0x04;
    public const uint DWM_TNP_VISIBLE = 0x08;
    public const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public User32.RECT rcDestination;
        public User32.RECT rcSource;
        public byte opacity;
        public int fVisible;
        public int fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx, cy;
    }

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmRegisterThumbnail(nint destination, nint source, nint* thumbnail);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmUnregisterThumbnail(nint thumbnail);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmUpdateThumbnailProperties(nint thumbnail, DWM_THUMBNAIL_PROPERTIES* properties);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmQueryThumbnailSourceSize(nint thumbnail, SIZE* size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();

    public const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const uint DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWCP_ROUND = 2;
    public const int DWMSBT_TRANSIENTWINDOW = 3; // 亚克力

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmExtendFrameIntoClientArea(nint hwnd, MARGINS* margins);
}

internal static unsafe partial class Dcomp
{
    public const uint WAIT_TIMEOUT = 258;

    /// <summary>Windows 11：阻塞到下一次合成器时钟（即显示器刷新）为止。</summary>
    [LibraryImport("dcomp.dll")]
    public static partial uint DCompositionWaitForCompositorClock(uint count, nint* handles, uint timeoutMs);
}

internal static unsafe partial class User32
{
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_NOACTIVATE_U = 0x08000000;
    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_RESTORE = 9;
    public static readonly nint HWND_TOPMOST = -1;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_SETCURSOR = 0x0020;
    public const nint MA_NOACTIVATE = 3;
    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_MENU = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    // INPUT 联合体的最大成员是 MOUSEINPUT（x64 下 32 字节），键盘输入要补齐到同样大小
    [StructLayout(LayoutKind.Sequential, Size = 40)]
    public struct INPUT
    {
        public uint type;
        private uint _pad;
        public KEYBDINPUT ki;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowW(string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint hwnd, RECT* rect, int erase);

    [LibraryImport("user32.dll")]
    public static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint hwnd, PAINTSTRUCT* ps);

    [LibraryImport("user32.dll")]
    public static partial int FillRect(nint hdc, RECT* rect, nint brush);

    [LibraryImport("user32.dll")]
    public static partial int FrameRect(nint hdc, RECT* rect, nint brush);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawTextW(nint hdc, string text, int count, RECT* rect, uint format);

    [LibraryImport("user32.dll")]
    public static partial nint SetWindowsHookExW(int idHook, delegate* unmanaged<int, nint, nint, nint> proc, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial uint SendInput(uint count, INPUT* inputs, int size);

    [LibraryImport("user32.dll")]
    public static partial nint SetCursor(nint cursor);

    [LibraryImport("user32.dll")]
    public static partial nint LoadCursorW(nint instance, nint name);

    public const nint IDC_ARROW = 32512;
    public const nint IDC_HAND = 32649;

    public const uint DT_CENTER = 0x01, DT_VCENTER = 0x04, DT_SINGLELINE = 0x20, DT_END_ELLIPSIS = 0x8000, DT_NOPREFIX = 0x800;
}

internal static unsafe partial class Gdi32
{
    public const int TRANSPARENT = 1;
    public const int FW_NORMAL = 400, FW_SEMIBOLD = 600;
    public const int NULL_PEN = 8;

    [LibraryImport("gdi32.dll")]
    public static partial nint GetStockObject(int index);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RoundRect(nint hdc, int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll")]
    public static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    public static uint Rgb(byte r, byte g, byte b) => r | ((uint)g << 8) | ((uint)b << 16);
}
