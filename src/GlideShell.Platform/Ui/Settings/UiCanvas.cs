using System.Globalization;
using System.Runtime.InteropServices;
using GlideShell.Core.Geometry;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui.Settings;

/// <summary>一种字体：字族、字号（物理像素）、字重；<see cref="Wrap"/> 时按宽度换行，否则单行、超出末尾省略号。</summary>
internal readonly record struct UiFont(string Family, double Size, int Weight = 400, bool Wrap = false);

/// <summary>文字在框里的横向对齐。</summary>
internal enum UiAlign
{
    Left,
    Center,
    Right,
}

/// <summary>
/// 设置窗口的画布（UI 线程）：整个客户区用 Direct2D（软件光栅化）画到一块 32 位 DIB 上，再 BitBlt 到窗口。
/// 保留每像素透明度：窗口把客户区延伸成 Mica 背景时，没画东西的地方是透明的（露出 Mica），卡片是半透明的。
/// 文字用 DirectWrite（灰度抗锯齿，透明底上不能用 ClearType），中文等由系统字体回退补上。长度单位都是物理像素。
/// </summary>
internal sealed unsafe partial class UiCanvas : IDisposable
{
    private void* _d2d, _dwrite, _target, _brush;
    private nint _dc, _dib;
    private byte* _bits;
    private int _dibW, _dibH, _w, _h;
    private bool _drawing;
    private readonly Dictionary<(string, int, int, bool, UiAlign, bool), nint> _formats = [];
    private readonly string _locale = CultureInfo.CurrentUICulture.Name is { Length: > 0 } n ? n : "zh-CN";

    private UiCanvas(void* d2d, void* dwrite, void* target, void* brush, nint dc)
    {
        _d2d = d2d;
        _dwrite = dwrite;
        _target = target;
        _brush = brush;
        _dc = dc;
    }

    public static UiCanvas? Create()
    {
        void* d2d = null, dwrite = null, target = null, brush = null;
        Guid d2dIid = D2D.IID_ID2D1Factory, dwIid = D2D.IID_IDWriteFactory;
        int hr = D2D.D2D1CreateFactory(0, &d2dIid, null, &d2d);
        if (hr >= 0) hr = D2D.DWriteCreateFactory(0, &dwIid, &dwrite);
        if (hr >= 0)
        {
            var props = new D2D.RenderTargetProperties
            {
                Type = D2D.RENDER_TARGET_TYPE_SOFTWARE, Format = D2D.DXGI_FORMAT_B8G8R8A8_UNORM, AlphaMode = D2D.ALPHA_MODE_PREMULTIPLIED, DpiX = 96, DpiY = 96,
            };
            hr = D2D.CreateDCRenderTarget(d2d, &props, &target);
        }
        if (hr >= 0)
        {
            var white = new D2D.ColorF(1, 1, 1, 1);
            hr = D2D.CreateSolidColorBrush(target, &white, &brush);
        }
        nint dc = hr >= 0 ? Gdi32.CreateCompatibleDC(0) : 0;
        if (hr < 0 || dc == 0)
        {
            if (brush != null) D2D.Release(brush);
            if (target != null) D2D.Release(target);
            if (dwrite != null) D2D.Release(dwrite);
            if (d2d != null) D2D.Release(d2d);
            return null;
        }
        D2D.SetTextAntialiasMode(target, D2D.TEXT_ANTIALIAS_MODE_GRAYSCALE);
        return new UiCanvas(d2d, dwrite, target, brush, dc);
    }

    public int Width => _w;

    public int Height => _h;

    /// <summary>开始画一帧（<paramref name="width"/>×<paramref name="height"/>，透明底）。</summary>
    public bool Begin(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width > _dibW || height > _dibH)
        {
            if (_dib != 0) Gdi32.DeleteObject(_dib);
            _dib = 0;
            int w = Math.Max(width, _dibW), h = Math.Max(height, _dibH);
            var info = Gdi32.TopDown32(w, h);
            void* bits;
            nint dib = Gdi32.CreateDIBSection(_dc, &info, 0, &bits, 0, 0);
            if (dib == 0) return false;
            Gdi32.SelectObject(_dc, dib);
            _dib = dib;
            _bits = (byte*)bits;
            _dibW = w;
            _dibH = h;
        }
        var rect = new User32.RECT { Right = width, Bottom = height };
        if (D2D.BindDC(_target, _dc, &rect) < 0) return false;
        _w = width;
        _h = height;
        D2D.BeginDraw(_target);
        var clear = new D2D.ColorF(0, 0, 0, 0);
        D2D.Clear(_target, &clear);
        _drawing = true;
        return true;
    }

    /// <summary>结束这一帧并贴到 <paramref name="hdc"/> 的 (0,0)（连同透明度）。</summary>
    public void Present(nint hdc)
    {
        if (!_drawing) return;
        _drawing = false;
        D2D.EndDraw(_target);
        BitBlt(hdc, 0, 0, _w, _h, _dc, 0, 0, 0x00CC0020 /* SRCCOPY */);
    }

    public void Fill(RectD r, double radius, D2D.ColorF color)
    {
        if (!_drawing || color.A <= 0 || r.Width <= 0 || r.Height <= 0) return;
        SetBrush(color);
        var rr = new D2D.RoundedRect(new D2D.RectF((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom), (float)radius, (float)radius);
        D2D.FillRoundedRectangle(_target, &rr, _brush);
    }

    /// <summary>描边画在矩形里面（外沿贴着矩形）。</summary>
    public void Stroke(RectD r, double radius, double width, D2D.ColorF color)
    {
        if (!_drawing || color.A <= 0) return;
        SetBrush(color);
        double h = width / 2;
        var rr = new D2D.RoundedRect(new D2D.RectF((float)(r.Left + h), (float)(r.Top + h), (float)(r.Right - h), (float)(r.Bottom - h)),
            (float)Math.Max(0, radius - h), (float)Math.Max(0, radius - h));
        D2D.DrawRoundedRectangle(_target, &rr, _brush, (float)width);
    }

    public void Circle(double cx, double cy, double r, D2D.ColorF color)
    {
        if (!_drawing || color.A <= 0) return;
        SetBrush(color);
        var e = new D2D.Ellipse(new D2D.Point2F((float)cx, (float)cy), (float)r, (float)r);
        D2D.FillEllipse(_target, &e, _brush);
    }

    public void Line(double x0, double y0, double x1, double y1, double width, D2D.ColorF color)
    {
        if (!_drawing || color.A <= 0) return;
        SetBrush(color);
        D2D.DrawLine(_target, new D2D.Point2F((float)x0, (float)y0), new D2D.Point2F((float)x1, (float)y1), _brush, (float)width);
    }

    /// <summary>只在 <paramref name="r"/> 里画（滚动区域），与 <see cref="PopClip"/> 配对。</summary>
    public void PushClip(RectD r)
    {
        if (!_drawing) return;
        var rect = new D2D.RectF((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom);
        ((delegate* unmanaged[Stdcall]<void*, D2D.RectF*, int, void>)(*(void***)_target)[45])(_target, &rect, 0 /* PER_PRIMITIVE */);
    }

    public void PopClip()
    {
        if (!_drawing) return;
        ((delegate* unmanaged[Stdcall]<void*, void>)(*(void***)_target)[46])(_target);
    }

    /// <summary>文字的宽高（<see cref="UiFont.Wrap"/> 时按 <paramref name="maxWidth"/> 换行后的高度）。</summary>
    public (double Width, double Height) Measure(string text, UiFont font, double maxWidth)
    {
        void* layout = Layout(text.Length > 0 ? text : " ", font, UiAlign.Left, false, maxWidth, 10000);
        if (layout == null) return (0, font.Size * 1.4);
        D2D.TextMetrics m;
        int hr = D2D.GetMetrics(layout, &m);
        D2D.Release(layout);
        return hr < 0 ? (0, font.Size * 1.4) : (Math.Min(m.Width, maxWidth), m.Height);
    }

    /// <summary>文字画在框里：<paramref name="align"/> 横向对齐，<paramref name="center"/> 时竖向居中（否则顶对齐）。</summary>
    public void Text(string text, RectD box, UiFont font, D2D.ColorF color, UiAlign align = UiAlign.Left, bool center = true)
    {
        if (!_drawing || text.Length == 0 || color.A <= 0) return;
        void* layout = Layout(text, font, align, center, Math.Max(1, box.Width), Math.Max(1, box.Height));
        if (layout == null) return;
        SetBrush(color);
        D2D.DrawTextLayout(_target, new D2D.Point2F((float)box.Left, (float)box.Top), layout, _brush, D2D.DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
        D2D.Release(layout);
    }

    private void SetBrush(D2D.ColorF color) => D2D.SetColor(_brush, &color);

    private void* Layout(string text, UiFont font, UiAlign align, bool center, double maxWidth, double maxHeight)
    {
        nint format = Format(font, align, center);
        if (format == 0) return null;
        void* layout;
        fixed (char* p = text)
            if (D2D.CreateTextLayout(_dwrite, p, (uint)text.Length, (void*)format, (float)maxWidth, (float)maxHeight, &layout) < 0) return null;
        return layout;
    }

    private nint Format(UiFont font, UiAlign align, bool center)
    {
        var key = (font.Family, (int)Math.Round(font.Size * 100), font.Weight, font.Wrap, align, center);
        if (_formats.TryGetValue(key, out nint cached)) return cached;
        void* format;
        int hr;
        fixed (char* family = font.Family)
        fixed (char* locale = _locale)
            hr = D2D.CreateTextFormat(_dwrite, family, font.Weight, (float)font.Size, locale, &format);
        if (hr < 0) return 0;
        D2D.SetWordWrapping(format, font.Wrap ? D2D.DWRITE_WORD_WRAPPING_WRAP : D2D.DWRITE_WORD_WRAPPING_NO_WRAP);
        D2D.SetTextAlignment(format, align switch { UiAlign.Center => D2D.DWRITE_TEXT_ALIGNMENT_CENTER, UiAlign.Right => D2D.DWRITE_TEXT_ALIGNMENT_TRAILING, _ => 0 });
        D2D.SetParagraphAlignment(format, center ? D2D.DWRITE_PARAGRAPH_ALIGNMENT_CENTER : D2D.DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
        if (!font.Wrap)
        {
            void* sign;
            if (D2D.CreateEllipsisTrimmingSign(_dwrite, format, &sign) >= 0)
            {
                var trimming = new D2D.Trimming { Granularity = D2D.DWRITE_TRIMMING_GRANULARITY_CHARACTER };
                D2D.SetTrimming(format, &trimming, sign);
                D2D.Release(sign);
            }
        }
        _formats[key] = (nint)format;
        return (nint)format;
    }

    public void Dispose()
    {
        foreach (nint f in _formats.Values) D2D.Release((void*)f);
        _formats.Clear();
        if (_brush != null) D2D.Release(_brush);
        if (_target != null) D2D.Release(_target);
        if (_dwrite != null) D2D.Release(_dwrite);
        if (_d2d != null) D2D.Release(_d2d);
        _brush = _target = _dwrite = _d2d = null;
        if (_dib != 0) Gdi32.DeleteObject(_dib);
        if (_dc != 0) Gdi32.DeleteDC(_dc);
        _dib = _dc = 0;
    }

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
}
