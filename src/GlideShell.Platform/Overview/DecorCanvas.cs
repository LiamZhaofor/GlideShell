using System.Globalization;
using GlideShell.Core.Overview;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 总览装饰的画布（UI 线程）：Direct2D 软件渲染到一块 32 位 DIB，文字用 DirectWrite（灰度抗锯齿、按系统字体回退显示中文与表情符号），
/// 画完取出预乘 BGRA，交给 <see cref="SharedThumbnailBackend.UploadImage"/> 做成 DComp 表面。
/// 用法：<see cref="Begin"/> → 若干绘制 → <see cref="End"/>。长度单位都是物理像素。
/// </summary>
internal sealed unsafe class DecorCanvas : IDisposable
{
    private void* _d2d;
    private void* _dwrite;
    private void* _target;
    private void* _brush;
    private nint _dc;
    private nint _dib;
    private byte* _bits;
    private int _dibWidth, _dibHeight;
    private int _width, _height;
    private bool _drawing;
    private readonly Dictionary<(int Size, int Weight), nint> _formats = [];
    private readonly List<nint> _pending = []; // 本次绘制用到的位图，EndDraw 之后再释放
    private readonly string _family;
    private readonly string _locale;

    private DecorCanvas(void* d2d, void* dwrite, void* target, void* brush, nint dc)
    {
        _d2d = d2d;
        _dwrite = dwrite;
        _target = target;
        _brush = brush;
        _dc = dc;
        // Windows 11 的界面字体（可变字重）；Windows 10 没有，用 Segoe UI。中文等由 DirectWrite 的系统回退补上
        _family = Environment.OSVersion.Version.Build >= 22000 ? "Segoe UI Variable Text" : "Segoe UI";
        _locale = CultureInfo.CurrentUICulture.Name is { Length: > 0 } name ? name : "zh-CN";
    }

    public static DecorCanvas? TryCreate(out string status)
    {
        void* d2d = null, dwrite = null, target = null, brush = null;
        nint dc = 0;
        Guid d2dIid = D2D.IID_ID2D1Factory, dwIid = D2D.IID_IDWriteFactory;
        int hr = D2D.D2D1CreateFactory(0 /* 单线程 */, &d2dIid, null, &d2d);
        if (hr >= 0) hr = D2D.DWriteCreateFactory(0 /* 共享 */, &dwIid, &dwrite);
        if (hr >= 0)
        {
            var props = new D2D.RenderTargetProperties
            {
                Type = D2D.RENDER_TARGET_TYPE_SOFTWARE, Format = D2D.DXGI_FORMAT_B8G8R8A8_UNORM, AlphaMode = D2D.ALPHA_MODE_PREMULTIPLIED,
                DpiX = 96, DpiY = 96, // 一律按物理像素画（默认会用系统 DPI）
            };
            hr = D2D.CreateDCRenderTarget(d2d, &props, &target);
        }
        if (hr >= 0)
        {
            var white = new D2D.ColorF(1, 1, 1, 1);
            hr = D2D.CreateSolidColorBrush(target, &white, &brush);
        }
        if (hr >= 0)
        {
            dc = Gdi32.CreateCompatibleDC(0);
            if (dc == 0) hr = unchecked((int)0x80004005);
        }
        if (hr < 0)
        {
            if (brush != null) D2D.Release(brush);
            if (target != null) D2D.Release(target);
            if (dwrite != null) D2D.Release(dwrite);
            if (d2d != null) D2D.Release(d2d);
            status = $"Direct2D / DirectWrite 不可用 0x{hr:X8}";
            return null;
        }
        D2D.SetTextAntialiasMode(target, D2D.TEXT_ANTIALIAS_MODE_GRAYSCALE); // 透明底上不能用 ClearType
        status = "Direct2D / DirectWrite 可用";
        return new DecorCanvas(d2d, dwrite, target, brush, dc);
    }

    /// <summary>一行文字（不换行，超出 <paramref name="maxWidth"/> 时末尾省略号）的宽高。</summary>
    public (float Width, float Height) MeasureText(string text, float sizePx, int weight, float maxWidth)
    {
        void* layout = Layout(text, sizePx, weight, maxWidth);
        if (layout == null) return (0, sizePx * 1.33f);
        D2D.TextMetrics m;
        int hr = D2D.GetMetrics(layout, &m);
        D2D.Release(layout);
        return hr < 0 ? (0, sizePx * 1.33f) : (Math.Min(m.Width, maxWidth), m.Height);
    }

    /// <summary>开始画一张 <paramref name="width"/>×<paramref name="height"/> 的透明图。</summary>
    public bool Begin(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width > _dibWidth || height > _dibHeight)
        {
            if (_dib != 0) Gdi32.DeleteObject(_dib);
            _dib = 0;
            int w = Math.Max(width, _dibWidth), h = Math.Max(height, _dibHeight);
            var info = Gdi32.TopDown32(w, h);
            void* bits;
            nint dib = Gdi32.CreateDIBSection(_dc, &info, 0, &bits, 0, 0);
            if (dib == 0) return false;
            Gdi32.SelectObject(_dc, dib);
            _dib = dib;
            _bits = (byte*)bits;
            _dibWidth = w;
            _dibHeight = h;
        }
        var rect = new User32.RECT { Right = width, Bottom = height };
        if (D2D.BindDC(_target, _dc, &rect) < 0) return false;
        _width = width;
        _height = height;
        D2D.BeginDraw(_target);
        var clear = new D2D.ColorF(0, 0, 0, 0);
        D2D.Clear(_target, &clear);
        _drawing = true;
        return true;
    }

    /// <summary>
    /// 一行文字，左上角在 (<paramref name="x"/>, <paramref name="y"/>)（文字行框的顶边），超出 <paramref name="maxWidth"/> 时末尾省略号。
    /// 颜色按非预乘给出。
    /// </summary>
    public void Text(string text, float x, float y, float maxWidth, float sizePx, int weight, D2D.ColorF color)
    {
        if (!_drawing || text.Length == 0) return;
        void* layout = Layout(text, sizePx, weight, maxWidth);
        if (layout == null) return;
        SetBrush(color);
        D2D.DrawTextLayout(_target, new D2D.Point2F(x, y), layout, _brush, D2D.DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
        D2D.Release(layout);
    }

    public void FillRoundRect(float left, float top, float right, float bottom, float radius, D2D.ColorF color)
    {
        if (!_drawing) return;
        SetBrush(color);
        var r = new D2D.RoundedRect(new D2D.RectF(left, top, right, bottom), radius, radius);
        D2D.FillRoundedRectangle(_target, &r, _brush);
    }

    /// <summary>圆角矩形的描边：笔画中线在给定矩形上。</summary>
    public void StrokeRoundRect(float left, float top, float right, float bottom, float radius, float width, D2D.ColorF color)
    {
        if (!_drawing) return;
        SetBrush(color);
        var r = new D2D.RoundedRect(new D2D.RectF(left, top, right, bottom), radius, radius);
        D2D.DrawRoundedRectangle(_target, &r, _brush, width);
    }

    public void FillCircle(float cx, float cy, float radius, D2D.ColorF color)
    {
        if (!_drawing) return;
        SetBrush(color);
        var e = new D2D.Ellipse(new D2D.Point2F(cx, cy), radius, radius);
        D2D.FillEllipse(_target, &e, _brush);
    }

    public void Line(float x0, float y0, float x1, float y1, float width, D2D.ColorF color)
    {
        if (!_drawing) return;
        SetBrush(color);
        D2D.DrawLine(_target, new D2D.Point2F(x0, y0), new D2D.Point2F(x1, y1), _brush, width);
    }

    /// <summary>一张预乘 BGRA 图（例如程序图标）缩放画到目标矩形。</summary>
    public void Image(ShadowImage image, float left, float top, float right, float bottom, float opacity = 1)
    {
        if (!_drawing || image.Width <= 0 || image.Height <= 0) return;
        var props = new D2D.BitmapProperties { Format = D2D.DXGI_FORMAT_B8G8R8A8_UNORM, AlphaMode = D2D.ALPHA_MODE_PREMULTIPLIED, DpiX = 96, DpiY = 96 };
        void* bitmap;
        fixed (byte* pixels = image.Bgra)
            if (D2D.CreateBitmap(_target, new D2D.SizeU((uint)image.Width, (uint)image.Height), pixels, (uint)image.Width * 4, &props, &bitmap) < 0) return;
        var dest = new D2D.RectF(left, top, right, bottom);
        D2D.DrawBitmap(_target, bitmap, &dest, opacity);
        _pending.Add((nint)bitmap);
    }

    /// <summary>结束绘制，取出预乘 BGRA（失败返回 null）。</summary>
    public ShadowImage? End()
    {
        if (!_drawing) return null;
        _drawing = false;
        int hr = D2D.EndDraw(_target);
        foreach (nint p in _pending) D2D.Release((void*)p);
        _pending.Clear();
        if (hr < 0) return null;
        var bgra = new byte[_width * _height * 4];
        int stride = _dibWidth * 4;
        fixed (byte* dst = bgra)
            for (int y = 0; y < _height; y++)
                Buffer.MemoryCopy(_bits + y * stride, dst + y * _width * 4, _width * 4, _width * 4);
        return new ShadowImage(_width, _height, 1, 0, bgra);
    }

    private void SetBrush(D2D.ColorF color) => D2D.SetColor(_brush, &color);

    private void* Layout(string text, float sizePx, int weight, float maxWidth)
    {
        nint format = Format(sizePx, weight);
        if (format == 0) return null;
        void* layout;
        fixed (char* p = text)
            if (D2D.CreateTextLayout(_dwrite, p, (uint)text.Length, (void*)format, Math.Max(1, maxWidth), sizePx * 3, &layout) < 0) return null;
        return layout;
    }

    private nint Format(float sizePx, int weight)
    {
        var key = ((int)Math.Round(sizePx * 100), weight);
        if (_formats.TryGetValue(key, out nint cached)) return cached;
        void* format;
        int hr;
        fixed (char* family = _family)
        fixed (char* locale = _locale)
            hr = D2D.CreateTextFormat(_dwrite, family, weight, sizePx, locale, &format);
        if (hr < 0) return 0;
        D2D.SetWordWrapping(format, D2D.DWRITE_WORD_WRAPPING_NO_WRAP);
        void* sign;
        if (D2D.CreateEllipsisTrimmingSign(_dwrite, format, &sign) >= 0)
        {
            var trimming = new D2D.Trimming { Granularity = D2D.DWRITE_TRIMMING_GRANULARITY_CHARACTER };
            D2D.SetTrimming(format, &trimming, sign);
            D2D.Release(sign); // 文字格式持有它
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
}
