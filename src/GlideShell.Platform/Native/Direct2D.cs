using System.Runtime.InteropServices;

namespace GlideShell.Platform.Native;

// Direct2D（DC 渲染目标，软件光栅化）+ DirectWrite：总览里的文字、图标与装饰先画成预乘 BGRA 图，再上传成 DComp 表面。
// COM 调用用原始 vtable（同 Composition.cs）。头文件里的内联重载不占槽；槽位按 d2d1.h / dwrite.h 的声明顺序：
// ID2D1RenderTarget 从 4（CreateBitmap）到 56（IsSupported），ID2D1DCRenderTarget::BindDC = 57；
// IDWriteFactory::CreateTextFormat = 15、CreateTextLayout = 18、CreateEllipsisTrimmingSign = 20；IDWriteTextLayout::GetMetrics = 60。
internal static unsafe partial class D2D
{
    public static readonly Guid IID_ID2D1Factory = new("06152247-6F50-465A-9245-118BFD3B6007");
    public static readonly Guid IID_IDWriteFactory = new("B859EE5A-D838-4B5B-A2E8-1ADC7D93DB48");

    public const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const int ALPHA_MODE_PREMULTIPLIED = 1;
    public const int RENDER_TARGET_TYPE_SOFTWARE = 1;
    public const int TEXT_ANTIALIAS_MODE_GRAYSCALE = 2;
    public const int DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT = 4;
    public const int BITMAP_INTERPOLATION_MODE_LINEAR = 1;

    public const int DWRITE_FONT_STYLE_NORMAL = 0, DWRITE_FONT_STRETCH_NORMAL = 5;
    public const int DWRITE_WORD_WRAPPING_WRAP = 0, DWRITE_WORD_WRAPPING_NO_WRAP = 1;
    public const int DWRITE_TEXT_ALIGNMENT_LEADING = 0, DWRITE_TEXT_ALIGNMENT_TRAILING = 1, DWRITE_TEXT_ALIGNMENT_CENTER = 2;
    public const int DWRITE_PARAGRAPH_ALIGNMENT_NEAR = 0, DWRITE_PARAGRAPH_ALIGNMENT_CENTER = 2;
    public const int TEXT_ANTIALIAS_MODE_CLEARTYPE = 1;
    public const int DWRITE_TRIMMING_GRANULARITY_CHARACTER = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderTargetProperties
    {
        public int Type, Format, AlphaMode;
        public float DpiX, DpiY;
        public int Usage, MinLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapProperties
    {
        public int Format, AlphaMode;
        public float DpiX, DpiY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ColorF(float r, float g, float b, float a)
    {
        public float R = r, G = g, B = b, A = a;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point2F(float x, float y)
    {
        public float X = x, Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SizeU(uint width, uint height)
    {
        public uint Width = width, Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectF(float left, float top, float right, float bottom)
    {
        public float Left = left, Top = top, Right = right, Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RoundedRect(RectF rect, float rx, float ry)
    {
        public RectF Rect = rect;
        public float RadiusX = rx, RadiusY = ry;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Ellipse(Point2F center, float rx, float ry)
    {
        public Point2F Center = center;
        public float RadiusX = rx, RadiusY = ry;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TextMetrics
    {
        public float Left, Top, Width, WidthIncludingTrailingWhitespace, Height, LayoutWidth, LayoutHeight;
        public uint MaxBidiReorderingDepth, LineCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Trimming
    {
        public int Granularity;
        public uint Delimiter, DelimiterCount;
    }

    [LibraryImport("d2d1.dll")]
    public static partial int D2D1CreateFactory(int factoryType, Guid* iid, void* options, void** factory);

    [LibraryImport("dwrite.dll")]
    public static partial int DWriteCreateFactory(int factoryType, Guid* iid, void** factory);

    // ---- ID2D1Factory ----
    public static int CreateDCRenderTarget(void* factory, RenderTargetProperties* props, void** target)
        => ((delegate* unmanaged[Stdcall]<void*, RenderTargetProperties*, void**, int>)Vtbl(factory)[16])(factory, props, target);

    // ---- ID2D1RenderTarget / ID2D1DCRenderTarget ----
    public static int CreateBitmap(void* rt, SizeU size, void* data, uint pitch, BitmapProperties* props, void** bitmap)
        => ((delegate* unmanaged[Stdcall]<void*, SizeU, void*, uint, BitmapProperties*, void**, int>)Vtbl(rt)[4])(rt, size, data, pitch, props, bitmap);
    public static int CreateSolidColorBrush(void* rt, ColorF* color, void** brush)
        => ((delegate* unmanaged[Stdcall]<void*, ColorF*, void*, void**, int>)Vtbl(rt)[8])(rt, color, null, brush);
    public static void DrawLine(void* rt, Point2F p0, Point2F p1, void* brush, float width)
        => ((delegate* unmanaged[Stdcall]<void*, Point2F, Point2F, void*, float, void*, void>)Vtbl(rt)[15])(rt, p0, p1, brush, width, null);
    public static void DrawRoundedRectangle(void* rt, RoundedRect* rect, void* brush, float width)
        => ((delegate* unmanaged[Stdcall]<void*, RoundedRect*, void*, float, void*, void>)Vtbl(rt)[18])(rt, rect, brush, width, null);
    public static void FillRoundedRectangle(void* rt, RoundedRect* rect, void* brush)
        => ((delegate* unmanaged[Stdcall]<void*, RoundedRect*, void*, void>)Vtbl(rt)[19])(rt, rect, brush);
    public static void FillEllipse(void* rt, Ellipse* ellipse, void* brush)
        => ((delegate* unmanaged[Stdcall]<void*, Ellipse*, void*, void>)Vtbl(rt)[21])(rt, ellipse, brush);
    public static void DrawBitmap(void* rt, void* bitmap, RectF* dest, float opacity)
        => ((delegate* unmanaged[Stdcall]<void*, void*, RectF*, float, int, RectF*, void>)Vtbl(rt)[26])(rt, bitmap, dest, opacity, BITMAP_INTERPOLATION_MODE_LINEAR, null);
    public static void DrawTextLayout(void* rt, Point2F origin, void* layout, void* brush, int options)
        => ((delegate* unmanaged[Stdcall]<void*, Point2F, void*, void*, int, void>)Vtbl(rt)[28])(rt, origin, layout, brush, options);
    public static void SetTextAntialiasMode(void* rt, int mode) => ((delegate* unmanaged[Stdcall]<void*, int, void>)Vtbl(rt)[34])(rt, mode);
    public static void Clear(void* rt, ColorF* color) => ((delegate* unmanaged[Stdcall]<void*, ColorF*, void>)Vtbl(rt)[47])(rt, color);
    public static void BeginDraw(void* rt) => ((delegate* unmanaged[Stdcall]<void*, void>)Vtbl(rt)[48])(rt);
    public static int EndDraw(void* rt) => ((delegate* unmanaged[Stdcall]<void*, void*, void*, int>)Vtbl(rt)[49])(rt, null, null);
    public static int BindDC(void* rt, nint hdc, User32.RECT* rect) => ((delegate* unmanaged[Stdcall]<void*, nint, User32.RECT*, int>)Vtbl(rt)[57])(rt, hdc, rect);

    // ---- ID2D1SolidColorBrush ----
    public static void SetColor(void* brush, ColorF* color) => ((delegate* unmanaged[Stdcall]<void*, ColorF*, void>)Vtbl(brush)[8])(brush, color);

    // ---- IDWriteFactory ----
    public static int CreateTextFormat(void* factory, char* family, int weight, float size, char* locale, void** format)
        => ((delegate* unmanaged[Stdcall]<void*, char*, void*, int, int, int, float, char*, void**, int>)Vtbl(factory)[15])(
            factory, family, null, weight, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size, locale, format);
    public static int CreateTextLayout(void* factory, char* text, uint length, void* format, float maxWidth, float maxHeight, void** layout)
        => ((delegate* unmanaged[Stdcall]<void*, char*, uint, void*, float, float, void**, int>)Vtbl(factory)[18])(factory, text, length, format, maxWidth, maxHeight, layout);
    public static int CreateEllipsisTrimmingSign(void* factory, void* format, void** sign)
        => ((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Vtbl(factory)[20])(factory, format, sign);

    // ---- IDWriteTextFormat（IDWriteTextLayout 继承它）----
    public static int SetTextAlignment(void* format, int alignment) => ((delegate* unmanaged[Stdcall]<void*, int, int>)Vtbl(format)[3])(format, alignment);
    public static int SetParagraphAlignment(void* format, int alignment) => ((delegate* unmanaged[Stdcall]<void*, int, int>)Vtbl(format)[4])(format, alignment);
    public static int SetWordWrapping(void* format, int wrapping) => ((delegate* unmanaged[Stdcall]<void*, int, int>)Vtbl(format)[5])(format, wrapping);
    public static int SetTrimming(void* format, Trimming* trimming, void* sign)
        => ((delegate* unmanaged[Stdcall]<void*, Trimming*, void*, int>)Vtbl(format)[9])(format, trimming, sign);

    // ---- IDWriteTextLayout ----
    public static int GetMetrics(void* layout, TextMetrics* metrics) => ((delegate* unmanaged[Stdcall]<void*, TextMetrics*, int>)Vtbl(layout)[60])(layout, metrics);

    public static uint Release(void* p) => ((delegate* unmanaged[Stdcall]<void*, uint>)Vtbl(p)[2])(p);

    private static void** Vtbl(void* p) => *(void***)p;
}

internal static unsafe partial class Gdi32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    /// <summary>32 位、自上而下（高度取负）的 DIB 信息头。</summary>
    public static BITMAPINFOHEADER TopDown32(int width, int height) => new()
    {
        biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
    };

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* info, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, void* bits, BITMAPINFOHEADER* info, uint usage);
}
