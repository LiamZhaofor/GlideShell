using GlideShell.Core.Geometry;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 一块覆盖层上的一组 DWM 缩略图（不可变：UI 线程整体创建 / 替换，渲染线程只读并逐帧更新属性）。
/// 第 0 个是桌面壁纸（不透明度兼作背景变暗），之后是窗口，按 Z 序从下往上注册（后注册的在上层）。
/// </summary>
internal sealed unsafe class ThumbnailLayer
{
    public readonly record struct Entry(nint Thumb, nint Hwnd, int SceneIndex, User32.RECT Source);

    private readonly Entry[] _windows; // 从下往上
    private readonly nint _wallpaper;
    private readonly nint _wallpaperSourceHwnd;
    private readonly User32.RECT _wallpaperSource;
    private readonly RectD _origin;

    private ThumbnailLayer(nint wallpaper, nint wallpaperSourceHwnd, User32.RECT wallpaperSource, Entry[] windows, RectD origin)
    {
        _wallpaper = wallpaper;
        _wallpaperSourceHwnd = wallpaperSourceHwnd;
        _wallpaperSource = wallpaperSource;
        _windows = windows;
        _origin = origin;
    }

    public static ThumbnailLayer Empty(RectD origin) => new(0, 0, default, [], origin);

    /// <summary>
    /// 注册（不可见）。<paramref name="previous"/> 里能复用的缩略图直接沿用：DWM 按注册顺序叠放，
    /// 所以只复用「从最下层起、与新 Z 序一致」的那一段前缀，其余的注销后按新顺序重新注册。
    /// 连续两次打开总览时通常只有最上面一两个窗口的顺序变了，注册开销省掉大半。
    /// </summary>
    /// <param name="overlay">目标覆盖层窗口。</param>
    /// <param name="workArea">覆盖层覆盖的屏幕区域（物理像素）。</param>
    /// <param name="windows">本屏参与的窗口：（场景下标，窗口句柄），按 Z 序最上层在前。</param>
    public static ThumbnailLayer Register(nint overlay, RectD workArea, IReadOnlyList<(int SceneIndex, nint Hwnd)> windows, ThumbnailLayer? previous)
    {
        // 壁纸：Progman 覆盖整个虚拟屏，取本屏工作区那一块；Explorer 重启后句柄会变，需要重新注册
        nint progman = User32.FindWindowW("Progman", null);
        nint wallpaper = 0;
        if (previous is not null && previous._wallpaper != 0 && previous._wallpaperSourceHwnd == progman)
        {
            wallpaper = previous._wallpaper;
        }
        else
        {
            if (previous is not null && previous._wallpaper != 0) Dwmapi.DwmUnregisterThumbnail(previous._wallpaper);
            if (progman == 0 || Dwmapi.DwmRegisterThumbnail(overlay, progman, &wallpaper) != 0) wallpaper = 0;
        }
        User32.RECT wallpaperSource = default;
        if (wallpaper != 0)
        {
            User32.RECT pr;
            User32.GetWindowRect(progman, &pr);
            wallpaperSource = new User32.RECT
            {
                Left = (int)workArea.Left - pr.Left, Top = (int)workArea.Top - pr.Top,
                Right = (int)workArea.Right - pr.Left, Bottom = (int)workArea.Bottom - pr.Top,
            };
        }

        var old = previous?._windows ?? [];
        int keep = 0;
        while (keep < old.Length && keep < windows.Count && old[keep].Hwnd == windows[windows.Count - 1 - keep].Hwnd) keep++;
        for (int i = keep; i < old.Length; i++) Dwmapi.DwmUnregisterThumbnail(old[i].Thumb);

        var entries = new List<Entry>(windows.Count);
        for (int k = 0; k < windows.Count; k++) // k = 从下往上的序号；最上层最后注册、画在最上面
        {
            var (index, hwnd) = windows[windows.Count - 1 - k];
            if (k < keep)
            {
                entries.Add(old[k] with { SceneIndex = index, Source = VisibleSource(hwnd) });
                continue;
            }
            nint thumb;
            if (Dwmapi.DwmRegisterThumbnail(overlay, hwnd, &thumb) != 0) continue;
            entries.Add(new Entry(thumb, hwnd, index, VisibleSource(hwnd)));
        }
        var layer = new ThumbnailLayer(wallpaper, progman, wallpaperSource, entries.ToArray(), workArea);
        layer.HideAll();
        return layer;
    }

    public void Unregister()
    {
        if (_wallpaper != 0) Dwmapi.DwmUnregisterThumbnail(_wallpaper);
        foreach (var e in _windows) Dwmapi.DwmUnregisterThumbnail(e.Thumb);
    }

    /// <summary>渲染线程：按本帧的窗口矩形（屏幕坐标）与壁纸不透明度更新全部缩略图。返回第一个失败的 HRESULT（0 = 全部成功）。</summary>
    public int Apply(ReadOnlySpan<RectD> sceneRects, double wallpaperOpacity)
    {
        int firstError = 0;
        var props = new Dwmapi.DWM_THUMBNAIL_PROPERTIES();
        if (_wallpaper != 0)
        {
            props.dwFlags = Dwmapi.DWM_TNP_RECTDESTINATION | Dwmapi.DWM_TNP_RECTSOURCE | Dwmapi.DWM_TNP_OPACITY | Dwmapi.DWM_TNP_VISIBLE;
            props.rcDestination = new User32.RECT { Left = 0, Top = 0, Right = (int)_origin.Width, Bottom = (int)_origin.Height };
            props.rcSource = _wallpaperSource;
            props.opacity = (byte)Math.Round(255 * Math.Clamp(wallpaperOpacity, 0, 1));
            props.fVisible = 1;
            int hr = Dwmapi.DwmUpdateThumbnailProperties(_wallpaper, &props);
            if (hr != 0) firstError = hr;
        }

        props.dwFlags = Dwmapi.DWM_TNP_RECTDESTINATION | Dwmapi.DWM_TNP_RECTSOURCE | Dwmapi.DWM_TNP_OPACITY | Dwmapi.DWM_TNP_VISIBLE;
        props.opacity = 255;
        props.fVisible = 1;
        foreach (var e in _windows)
        {
            if (e.SceneIndex >= sceneRects.Length) continue;
            var r = sceneRects[e.SceneIndex];
            props.rcDestination = new User32.RECT
            {
                Left = (int)Math.Round(r.Left - _origin.Left), Top = (int)Math.Round(r.Top - _origin.Top),
                Right = (int)Math.Round(r.Right - _origin.Left), Bottom = (int)Math.Round(r.Bottom - _origin.Top),
            };
            props.rcSource = e.Source;
            int hr = Dwmapi.DwmUpdateThumbnailProperties(e.Thumb, &props);
            if (hr != 0 && firstError == 0) firstError = hr;
        }
        return firstError;
    }

    public bool HasWallpaper => _wallpaper != 0;

    public void HideAll()
    {
        var props = new Dwmapi.DWM_THUMBNAIL_PROPERTIES { dwFlags = Dwmapi.DWM_TNP_VISIBLE, fVisible = 0 };
        if (_wallpaper != 0) Dwmapi.DwmUpdateThumbnailProperties(_wallpaper, &props);
        foreach (var e in _windows) Dwmapi.DwmUpdateThumbnailProperties(e.Thumb, &props);
    }

    // 缩略图源坐标相对窗口左上角；只取可见边框内的部分（去掉 Win10/11 的隐形缩放边框），与 Source 矩形对齐
    private static User32.RECT VisibleSource(nint hwnd)
    {
        User32.RECT wr, fr;
        User32.GetWindowRect(hwnd, &wr);
        if (Dwmapi.DwmGetWindowAttribute(hwnd, Dwmapi.DWMWA_EXTENDED_FRAME_BOUNDS, &fr, (uint)sizeof(User32.RECT)) != 0) fr = wr;
        return new User32.RECT { Left = fr.Left - wr.Left, Top = fr.Top - wr.Top, Right = fr.Right - wr.Left, Bottom = fr.Bottom - wr.Top };
    }
}
