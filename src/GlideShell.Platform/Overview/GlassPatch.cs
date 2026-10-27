using System.Numerics;
using GlideShell.Core.Geometry;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 一块「毛玻璃」：屏幕上某个圆角矩形里显示模糊后的壁纸（总览背景就是壁纸），上面再由调用方叠一层很淡的白色与描边。
/// 只取这一小块壁纸（外扩 3σ，边缘不发暗）做共享缩略图、在 DComp 里高斯模糊，开销只和这块大小有关。
/// 结构：分组视觉（模糊效果 + 圆角裁剪）下面一块不透明黑底 + 壁纸缩略图；壁纸的不透明度跟总览背景变暗同步，
/// 叠在黑底上就和覆盖层里变暗的壁纸一致，只是模糊了。所有方法都在 <see cref="SharedThumbnailBackend.Gate"/> 锁内调用（Create 自己加锁）。
/// </summary>
internal sealed unsafe class GlassPatch
{
    private readonly nint _group, _black, _wallpaper, _thumb, _clip, _effect;
    private bool _hidden = true;

    private GlassPatch(nint group, nint black, nint wallpaper, nint thumb, nint clip, nint effect)
    {
        _group = group;
        _black = black;
        _wallpaper = wallpaper;
        _thumb = thumb;
        _clip = clip;
        _effect = effect;
    }

    /// <summary>屏幕上的位置（创建时定下，之后不变）。</summary>
    public RectD Rect { get; private init; }

    /// <param name="parent">挂到哪个视觉下（加在最上层）。</param>
    /// <param name="overlay">覆盖层窗口（缩略图的目标）。</param>
    /// <param name="rect">玻璃在屏幕上的位置；<paramref name="origin"/> 是覆盖层左上角（视觉坐标相对它）。</param>
    /// <param name="black">1×1 不透明黑色表面（缓存的公用图）。</param>
    public static GlassPatch? Create(SharedThumbnailBackend backend, void* parent, nint overlay, RectD rect, RectD origin, double radiusPx, double sigmaPx,
        nint black)
    {
        nint progman = User32.FindWindowW("Progman", null);
        if (progman == 0 || black == 0) return null;
        User32.RECT pr;
        User32.GetWindowRect(progman, &pr);
        double margin = Math.Ceiling(3 * sigmaPx);
        var src = rect.Inflate(margin, margin);
        var source = new User32.RECT
        {
            Left = (int)Math.Floor(src.Left) - pr.Left, Top = (int)Math.Floor(src.Top) - pr.Top,
            Right = (int)Math.Ceiling(src.Right) - pr.Left, Bottom = (int)Math.Ceiling(src.Bottom) - pr.Top,
        };
        double x0 = source.Left + pr.Left - origin.Left, y0 = source.Top + pr.Top - origin.Top;
        lock (backend.Gate)
        {
            if (backend.CreateDecorVisual(parent, 0, out nint group) < 0) return null;
            nint blackVisual = 0, wallpaper = 0, clip = 0, effect = 0;
            nint thumb = 0;
            bool ok = backend.CreateDecorVisual((void*)group, black, out blackVisual) >= 0;
            if (ok)
            {
                var m = new Matrix3x2(source.Width, 0, 0, source.Height, (float)x0, (float)y0); // 1×1 黑点拉满整块
                Dcomp.SetTransform((void*)blackVisual, &m);
                Dcomp.SetOpacity((void*)blackVisual, 1);
                var props = new Dwmapi.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = Dwmapi.DWM_TNP_RECTDESTINATION | Dwmapi.DWM_TNP_RECTSOURCE | Dwmapi.DWM_TNP_VISIBLE | Dwmapi.DWM_TNP_OPACITY,
                    rcDestination = new User32.RECT { Right = Math.Max(1, source.Width), Bottom = Math.Max(1, source.Height) },
                    rcSource = source,
                    opacity = 255,
                    fVisible = 1,
                };
                void* v;
                nint t;
                ok = SharedThumbnailApi.Create(overlay, progman, &props, backend.Device, &v, &t) >= 0;
                if (ok)
                {
                    wallpaper = (nint)v;
                    thumb = t;
                    var tm = new Matrix3x2(1, 0, 0, 1, (float)x0, (float)y0);
                    Dcomp.SetTransform(v, &tm);
                    Dcomp.SetOpacity(v, 0);
                    ok = Dcomp.AddVisual((void*)group, v) >= 0;
                }
            }
            if (ok)
            {
                void* c, e;
                ok = Dcomp.CreateRectangleClip(backend.Device, &c) >= 0;
                if (ok)
                {
                    clip = (nint)c;
                    var local = rect.Offset(-origin.Left, -origin.Top);
                    Dcomp.SetClipRect(c, (float)local.Left, (float)local.Top, (float)local.Right, (float)local.Bottom);
                    Dcomp.SetClipRadius(c, (float)radiusPx, (float)radiusPx);
                    Dcomp.SetClipObject((void*)group, c);
                    ok = Dcomp.CreateGaussianBlur(backend.Device, (float)sigmaPx, &e) >= 0;
                    if (ok)
                    {
                        effect = (nint)e;
                        ok = Dcomp.SetEffect((void*)group, e) >= 0;
                    }
                }
            }
            var patch = new GlassPatch(group, blackVisual, wallpaper, thumb, clip, effect) { Rect = rect };
            if (ok) return patch;
            patch.Remove(parent);
            return null;
        }
    }

    /// <summary>锁内：不透明度 <paramref name="opacity"/>（0 时只设一次）；<paramref name="wallpaperOpacity"/> 是总览背景里壁纸此刻的不透明度。</summary>
    public void Place(double opacity, double wallpaperOpacity)
    {
        if (opacity <= 0.002)
        {
            if (!_hidden) Dcomp.SetOpacity((void*)_group, 0);
            _hidden = true;
            return;
        }
        _hidden = false;
        Dcomp.SetOpacity((void*)_group, (float)Math.Min(1, opacity));
        Dcomp.SetOpacity((void*)_wallpaper, (float)Math.Clamp(wallpaperOpacity, 0, 1));
    }

    /// <summary>锁内：摘下并释放。</summary>
    public void Remove(void* parent)
    {
        if (_group != 0)
        {
            Dcomp.SetEffect((void*)_group, null);
            Dcomp.SetClipObject((void*)_group, null);
            Dcomp.RemoveVisual(parent, (void*)_group);
        }
        foreach (nint p in new[] { _black, _wallpaper, _clip, _effect, _group })
            if (p != 0) Dcomp.Release((void*)p);
        if (_thumb != 0) Dwmapi.DwmUnregisterThumbnail(_thumb);
    }
}
