using System.Runtime.InteropServices;

namespace GlideShell.Platform.Diagnostics;

/// <summary>
/// 诊断用截屏：把整个虚拟屏幕存成 BMP（DWM 合成后的画面，含缩略图与覆盖层）。
/// 只在用户放了标记文件时使用（例如排查「撤层那一刻画面突变」），平时不会调用。
/// </summary>
public static unsafe partial class ScreenCapture
{
    /// <summary>标记文件存在时返回保存目录（日志目录下的 captures），否则 null。</summary>
    public static string? EnabledDirectory(string marker)
    {
        string root = Path.GetDirectoryName(Log.DefaultDirectory) ?? Log.DefaultDirectory;
        if (!File.Exists(Path.Combine(root, marker))) return null;
        string dir = Path.Combine(Log.DefaultDirectory, "captures");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Save(string path)
    {
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79); // SM_X/Y/CX/CYVIRTUALSCREEN
        nint screen = GetDC(0);
        nint mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = h, biPlanes = 1, biBitCount = 32 }; // 自下而上，与 BMP 文件一致
        void* bits;
        nint bmp = CreateDIBSection(screen, &bi, 0, &bits, 0, 0);
        nint old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
        using (var f = File.Create(path))
        {
            int size = w * h * 4;
            Span<byte> header = stackalloc byte[14];
            header[0] = (byte)'B';
            header[1] = (byte)'M';
            BitConverter.TryWriteBytes(header[2..], 14 + 40 + size);
            BitConverter.TryWriteBytes(header[10..], 14 + 40);
            f.Write(header);
            f.Write(new ReadOnlySpan<byte>(&bi, 40));
            f.Write(new ReadOnlySpan<byte>(bits, size));
        }
        SelectObject(mem, old);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(0, screen);
    }

    /// <summary>读回屏幕上一块区域（物理像素，自上而下逐行，每像素 0xAARRGGBB）。</summary>
    public static uint[] Grab(int x, int y, int w, int h)
    {
        var pixels = new uint[w * h];
        nint screen = GetDC(0);
        nint mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 }; // 负高度：自上而下
        void* bits;
        nint bmp = CreateDIBSection(screen, &bi, 0, &bits, 0, 0);
        nint old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000);
        new ReadOnlySpan<uint>(bits, w * h).CopyTo(pixels);
        SelectObject(mem, old);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(0, screen);
        return pixels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] private static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint hwnd, nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateDIBSection(nint dc, BITMAPINFOHEADER* bi, uint usage, void** bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
}
