using System.Runtime.InteropServices;

namespace GlideShell.Platform.Native;

// 设置窗口用到的标准控件（按钮、复选框、组合框、列表框、滑块）与对话框导航、字体、DPI。
internal static unsafe partial class User32
{
    public const uint WS_OVERLAPPED = 0x00000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000, WS_GROUP = 0x00020000, WS_VSCROLL = 0x00200000;
    public const uint WS_EX_CLIENTEDGE = 0x00000200, WS_EX_CONTROLPARENT = 0x00010000;

    public const uint BS_PUSHBUTTON = 0x0, BS_DEFPUSHBUTTON = 0x1, BS_AUTOCHECKBOX = 0x3, BS_GROUPBOX = 0x7;
    public const uint CBS_DROPDOWN = 0x2, CBS_DROPDOWNLIST = 0x3, CBS_AUTOHSCROLL = 0x40;
    public const uint LBS_NOTIFY = 0x1, LBS_NOINTEGRALHEIGHT = 0x100;
    public const uint SS_LEFT = 0x0, SS_RIGHT = 0x2;
    public const uint TBS_HORZ = 0x0, TBS_NOTICKS = 0x10;

    public const uint WM_SETFONT = 0x0030, WM_HSCROLL = 0x0114, WM_DPICHANGED = 0x02E0;
    public const uint WM_CTLCOLORBTN = 0x0135, WM_CTLCOLORSTATIC = 0x0138;
    public const uint BM_GETCHECK = 0x00F0, BM_SETCHECK = 0x00F1;
    public const uint CB_ADDSTRING = 0x0143, CB_GETCURSEL = 0x0147, CB_RESETCONTENT = 0x014B, CB_SETCURSEL = 0x014E;
    public const uint LB_ADDSTRING = 0x0180, LB_DELETESTRING = 0x0182, LB_RESETCONTENT = 0x0184, LB_GETCURSEL = 0x0188;
    public const uint TBM_GETPOS = 0x0400, TBM_SETPOS = 0x0405, TBM_SETRANGE = 0x0406;
    public const int BN_CLICKED = 0, CBN_SELCHANGE = 1;
    public const int COLOR_WINDOW = 5, COLOR_GRAYTEXT = 17;
    public const int SW_SHOWNORMAL = 1;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint MB_OK = 0x0, MB_ICONWARNING = 0x30;
    public const uint SPI_GETNONCLIENTMETRICS = 0x0029;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOGFONTW
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        public fixed char lfFaceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NONCLIENTMETRICSW
    {
        public uint cbSize;
        public int iBorderWidth, iScrollWidth, iScrollHeight, iCaptionWidth, iCaptionHeight;
        public LOGFONTW lfCaptionFont;
        public int iSmCaptionWidth, iSmCaptionHeight;
        public LOGFONTW lfSmCaptionFont;
        public int iMenuWidth, iMenuHeight;
        public LOGFONTW lfMenuFont, lfStatusFont, lfMessageFont;
        public int iPaddedBorderWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INITCOMMONCONTROLSEX
    {
        public uint dwSize, dwICC;
    }

    public const uint ICC_BAR_CLASSES = 0x4, ICC_STANDARD_CLASSES = 0x4000;

    [LibraryImport("user32.dll")]
    public static partial nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint SendMessageW(nint hwnd, uint msg, nint wParam, char* lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnableWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowTextW(nint hwnd, string text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsDialogMessageW(nint dialog, MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoForDpi(uint action, uint param, void* data, uint winIni, uint dpi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustWindowRectExForDpi(RECT* rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);

    [LibraryImport("user32.dll")]
    public static partial nint GetSysColorBrush(int index);

    [LibraryImport("user32.dll")]
    public static partial uint GetSysColor(int index);

    [LibraryImport("user32.dll")]
    public static partial nint SetFocus(nint hwnd);

    public const int VK_SHIFT = 0x10;

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, RECT* rect);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBoxW(nint owner, string text, string caption, uint type);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitCommonControlsEx(INITCOMMONCONTROLSEX* init);
}

internal static unsafe partial class Gdi32
{
    [LibraryImport("gdi32.dll")]
    public static partial nint CreateFontIndirectW(User32.LOGFONTW* font);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetBkColor(nint hdc, uint color);
}
