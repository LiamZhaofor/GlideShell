using System.Numerics;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 总览装饰共用的资源（UI 线程）：画布、程序图标、按大小缓存的固定图（关闭按钮、圆角框）。DComp 表面属于同一个设备，各块屏的覆盖层共用。
/// 只在共享缩略图视觉 + D3D11 可用时创建；否则覆盖层退回 GDI 画标题。
/// </summary>
internal sealed unsafe class DecorResources : IDisposable
{
    private readonly Dictionary<(string App, int Size), nint> _icons = [];
    private readonly Dictionary<string, NineSliceShape> _shapes = [];
    private readonly Dictionary<int, nint> _closeButtons = [];

    private DecorResources(SharedThumbnailBackend backend, DecorCanvas canvas, AppIcons icons, UiThread ui)
    {
        Backend = backend;
        Canvas = canvas;
        Icons = icons;
        Ui = ui;
    }

    public SharedThumbnailBackend Backend { get; }

    public DecorCanvas Canvas { get; }

    public AppIcons Icons { get; }

    public UiThread Ui { get; }

    public static DecorResources? TryCreate(SharedThumbnailBackend? backend, UiThread ui, out string status)
    {
        if (backend is null || !backend.CanDrawShadows)
        {
            status = "没有共享缩略图视觉或 D3D11：标题用 GDI 画";
            return null;
        }
        var canvas = DecorCanvas.TryCreate(out status);
        if (canvas is null) return null;
        return new DecorResources(backend, canvas, new AppIcons(ui), ui);
    }

    /// <summary>上传一张图（失败返回 0）。调用方把它设为视觉内容后即可释放自己的引用。</summary>
    public nint Upload(ShadowImage? image) => image is not null && Backend.UploadImage(image, out nint s) >= 0 ? s : 0;

    /// <summary>
    /// 程序图标的表面（<paramref name="sizePx"/> 像素见方，缓存）。还没取到返回 0，取到后在 UI 线程上调用 <paramref name="ready"/>；取不到也返回 0。
    /// </summary>
    public nint Icon(AppId app, int sizePx, Action ready)
    {
        var key = (app.Key, sizePx);
        if (_icons.TryGetValue(key, out nint cached)) return cached;
        var image = Icons.Get(app, sizePx, img =>
        {
            if (!_icons.ContainsKey(key)) _icons[key] = Upload(img);
            ready();
        }, out bool pending);
        if (pending) return 0;
        nint surface = Upload(image);
        _icons[key] = surface;
        return surface;
    }

    /// <summary>关闭按钮（<see cref="CloseButton.Render"/>，缓存）。</summary>
    public nint CloseButtonSurface(int sizePx)
    {
        if (_closeButtons.TryGetValue(sizePx, out nint s)) return s;
        s = Upload(new ShadowImage(sizePx, sizePx, 1, 0, CloseButton.Render(sizePx)));
        _closeButtons[sizePx] = s;
        return s;
    }

    private readonly Dictionary<string, (nint Surface, int Width, int Height)> _images = [];

    /// <summary>按 <paramref name="key"/> 缓存的一张固定图（用 <see cref="Canvas"/> 现画）；画不出来返回 0。</summary>
    public (nint Surface, int Width, int Height) Image(string key, Func<DecorCanvas, ShadowImage?> render)
    {
        if (_images.TryGetValue(key, out var cached)) return cached;
        var image = render(Canvas);
        var entry = (Upload(image), image?.Width ?? 0, image?.Height ?? 0);
        _images[key] = entry;
        return entry;
    }

    /// <summary>
    /// 圆角矩形的九宫格图：<paramref name="radius"/> 圆角、<paramref name="stroke"/> 描边宽（0 = 实心填充），描边贴着外沿。
    /// 颜色非预乘。按参数缓存。
    /// </summary>
    public NineSliceShape? Shape(double radius, double stroke, D2D.ColorF color)
    {
        string key = $"{radius:F2}/{stroke:F2}/{color.R:F3},{color.G:F3},{color.B:F3},{color.A:F3}";
        if (_shapes.TryGetValue(key, out var cached)) return cached;
        int c = (int)Math.Ceiling(Math.Max(radius, stroke)) + 1;
        int size = 2 * c + 3;
        if (!Canvas.Begin(size, size)) return null;
        float r = (float)radius;
        if (stroke <= 0) Canvas.FillRoundRect(0, 0, size, size, r, color);
        else
        {
            float h = (float)stroke / 2;
            Canvas.StrokeRoundRect(h, h, size - h, size - h, Math.Max(0, r - h), (float)stroke, color);
        }
        nint surface = Upload(Canvas.End());
        if (surface == 0) return null;
        var shape = new NineSliceShape(surface, c, stroke > 0);
        _shapes[key] = shape;
        return shape;
    }

    public void Dispose()
    {
        lock (Backend.Gate)
        {
            foreach (nint s in _icons.Values)
                if (s != 0) Dcomp.Release((void*)s);
            foreach (nint s in _closeButtons.Values)
                if (s != 0) Dcomp.Release((void*)s);
            foreach (var shape in _shapes.Values) Dcomp.Release((void*)shape.Surface);
            foreach (var image in _images.Values)
                if (image.Surface != 0) Dcomp.Release((void*)image.Surface);
        }
        _images.Clear();
        _icons.Clear();
        _closeButtons.Clear();
        _shapes.Clear();
        Icons.Dispose();
        Canvas.Dispose();
    }
}

/// <summary>
/// 九宫格图：边长 2c+3 的正方形，四角各 c×c，中间三行 / 三列是均匀的（只取正中那一行 / 列拉伸，线性插值采样不会混进圆角）。
/// <paramref name="Hollow"/>：中间是空的（描边框），不用画中心块。
/// </summary>
internal sealed record NineSliceShape(nint Surface, int Corner, bool Hollow);

/// <summary>
/// 用九宫格图拼出任意大小的圆角矩形：一个分组视觉（不透明度）下 8 或 9 个子视觉，各自裁剪出图里的一块再拉伸到位。
/// 矩形取整到像素：各块接缝落在整像素上，不会出现半透明的缝；四角 1:1 不缩放，圆角始终清晰。
/// 所有方法都要在 <see cref="SharedThumbnailBackend.Gate"/> 锁内调用（创建除外，它自己加锁）。
/// </summary>
internal sealed unsafe class NineSlice
{
    private readonly nint _group;
    private readonly nint[] _parts;
    private readonly int _c;
    private bool _hidden = true;

    private NineSlice(nint group, nint[] parts, int c)
    {
        _group = group;
        _parts = parts;
        _c = c;
    }

    public static NineSlice? Create(SharedThumbnailBackend backend, void* parent, NineSliceShape shape)
    {
        lock (backend.Gate)
        {
            if (backend.CreateDecorVisual(parent, 0, out nint group) < 0) return null;
            Dcomp.SetOpacity((void*)group, 0);
            int n = shape.Hollow ? 8 : 9;
            var parts = new nint[n];
            int c = shape.Corner;
            for (int k = 0; k < n; k++)
            {
                if (backend.CreateDecorVisual((void*)group, shape.Surface, out parts[k]) < 0)
                {
                    for (int j = 0; j < k; j++) Dcomp.Release((void*)parts[j]);
                    Dcomp.RemoveVisual(parent, (void*)group);
                    Dcomp.Release((void*)group);
                    return null;
                }
                var (sx, sy, sw, sh) = Source(k, c);
                var clip = new Dcomp.D2D_RECT_F { Left = sx, Top = sy, Right = sx + sw, Bottom = sy + sh };
                Dcomp.SetClip((void*)parts[k], &clip);
                Dcomp.SetOpacity((void*)parts[k], 1);
            }
            return new NineSlice(group, parts, c);
        }
    }

    /// <summary>锁内：摆到 <paramref name="rect"/>（覆盖层坐标），不透明度 <paramref name="opacity"/>；不透明度为 0 时只设一次。</summary>
    public void Place(RectD rect, double opacity)
    {
        if (opacity <= 0.001)
        {
            if (_hidden) return;
            _hidden = true;
            Dcomp.SetOpacity((void*)_group, 0);
            return;
        }
        _hidden = false;
        Dcomp.SetOpacity((void*)_group, (float)Math.Min(1, opacity));
        double c = _c;
        double x = Math.Round(rect.Left), y = Math.Round(rect.Top);
        double w = Math.Max(2 * c, Math.Round(rect.Right) - x), h = Math.Max(2 * c, Math.Round(rect.Bottom) - y);
        for (int k = 0; k < _parts.Length; k++)
        {
            var (sx, sy, sw, sh) = Source(k, _c);
            var (dx, dy, dw, dh) = k switch
            {
                0 => (x, y, c, c),
                1 => (x + w - c, y, c, c),
                2 => (x, y + h - c, c, c),
                3 => (x + w - c, y + h - c, c, c),
                4 => (x + c, y, w - 2 * c, c),
                5 => (x + c, y + h - c, w - 2 * c, c),
                6 => (x, y + c, c, h - 2 * c),
                7 => (x + w - c, y + c, c, h - 2 * c),
                _ => (x + c, y + c, w - 2 * c, h - 2 * c),
            };
            float kx = (float)(dw / sw), ky = (float)(dh / sh);
            var m = new Matrix3x2(kx, 0, 0, ky, (float)(dx - sx * kx), (float)(dy - sy * ky));
            Dcomp.SetTransform((void*)_parts[k], &m);
        }
    }

    /// <summary>锁内：立即隐藏（会话结束）。</summary>
    public void Hide()
    {
        _hidden = true;
        Dcomp.SetOpacity((void*)_group, 0);
    }

    // 图里的第 k 块：四角、上下左右四条边（取正中一列 / 行）、中心
    private static (float X, float Y, float W, float H) Source(int k, int c) => k switch
    {
        0 => (0, 0, c, c),
        1 => (c + 3, 0, c, c),
        2 => (0, c + 3, c, c),
        3 => (c + 3, c + 3, c, c),
        4 => (c + 1, 0, 1, c),
        5 => (c + 1, c + 3, 1, c),
        6 => (0, c + 1, c, 1),
        7 => (c + 3, c + 1, c, 1),
        _ => (c + 1, c + 1, 1, 1),
    };

    /// <summary>锁内：从父视觉摘下并释放。</summary>
    public void Remove(void* parent)
    {
        foreach (nint p in _parts) Dcomp.Release((void*)p);
        Dcomp.RemoveVisual(parent, (void*)_group);
        Dcomp.Release((void*)_group);
    }
}
