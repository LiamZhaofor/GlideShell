using System.Numerics;
using GlideShell.Core.Geometry;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Overview;

/// <summary>覆盖层上的一组窗口缩略图：经典 DWM 缩略图（<see cref="ThumbnailLayer"/>）或共享缩略图视觉（<see cref="SharedThumbnailLayer"/>）。</summary>
internal interface IThumbnailLayer
{
    /// <summary>渲染线程：按本帧的窗口矩形（屏幕坐标）、窗口不透明度与壁纸不透明度更新。返回第一个失败的 HRESULT（0 = 全部成功）。</summary>
    int Apply(ReadOnlySpan<RectD> sceneRects, ReadOnlySpan<double> sceneOpacity, double wallpaperOpacity);

    void HideAll();

    void Unregister();

    bool HasWallpaper { get; }

    /// <summary>UI 线程（重绘时）：该场景窗口是否有缩略图画面；没有时覆盖层画占位卡片。</summary>
    bool ShowsContent(int sceneIndex);

    /// <summary>UI 线程：把某个窗口提到最上层（选中的窗口飞回原位时不能被别的窗口挡住）。返回替换后的层。</summary>
    IThumbnailLayer BringToTop(nint overlay, int sceneIndex);

    /// <summary>
    /// UI 线程：按真实桌面当前的 Z 序（<paramref name="topFirst"/>，最上层在前）重排缩略图的层次，返回替换后的层。
    /// 撤层前用：会话开始时的 Z 序可能已经过时（上一次切换的激活还没生效、程序自己把窗口提上来等）。
    /// </summary>
    IThumbnailLayer Restack(nint overlay, IReadOnlyList<nint> topFirst);
}

internal static class Stacking
{
    /// <summary>按真实 Z 序给出「从下往上」的新顺序（下标）；已经不在 Z 序里的窗口（关掉了）放最下面，保持原相对顺序。</summary>
    public static int[] BottomUp<T>(IReadOnlyList<T> entries, Func<T, nint> hwnd, IReadOnlyList<nint> topFirst)
    {
        var rank = new Dictionary<nint, int>(topFirst.Count);
        for (int i = 0; i < topFirst.Count; i++) rank.TryAdd(topFirst[i], i);
        return Enumerable.Range(0, entries.Count)
            .OrderByDescending(i => rank.TryGetValue(hwnd(entries[i]), out int r) ? r : int.MaxValue)
            .ThenBy(i => i)
            .ToArray();
    }
}

/// <summary>
/// 共享缩略图视觉后端（实验，默认关闭，见 docs/CONFIG.md 的 overview.sharedThumbnails）：一个 DComp 桌面设备，
/// 所有 DComp 调用都在 <see cref="Gate"/> 锁内——UI 线程注册 / 注销、渲染线程逐帧改属性与 Commit 互不踩踏。
/// 依赖未公开的 dwmapi#147（S4），任何一步失败都退回经典缩略图。
/// </summary>
internal sealed unsafe class SharedThumbnailBackend : IDisposable
{
    private void* _device;

    private SharedThumbnailBackend(void* device) => _device = device;

    public object Gate { get; } = new();

    public void* Device => _device;

    public static SharedThumbnailBackend? TryCreate(out string status)
    {
        status = SharedThumbnailApi.Load();
        if (!SharedThumbnailApi.Available) return null;
        Guid iid = Dcomp.IID_IDCompositionDesktopDevice;
        void* device;
        int hr = Dcomp.DCompositionCreateDevice3(null, &iid, &device);
        if (hr < 0 || device == null)
        {
            status = $"创建 DirectComposition 设备失败 0x{hr:X8}";
            return null;
        }
        status = "共享缩略图视觉可用（dwmapi#147 + DirectComposition）";
        return new SharedThumbnailBackend(device);
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_device != null) Dcomp.Release(_device);
            _device = null;
        }
    }
}

/// <summary>一个覆盖层窗口的 DComp 目标（topmost：画在窗口自己的 GDI 内容之上，与经典缩略图的层次一致）与根视觉。</summary>
internal sealed unsafe class OverlayComposition : IDisposable
{
    private readonly SharedThumbnailBackend _backend;
    private void* _target;
    private void* _root;

    private OverlayComposition(SharedThumbnailBackend backend, void* target, void* root)
    {
        _backend = backend;
        _target = target;
        _root = root;
    }

    public void* Root => _root;

    public static OverlayComposition? Create(SharedThumbnailBackend backend, nint hwnd, out int hr)
    {
        lock (backend.Gate)
        {
            void* target = null, root = null;
            hr = Dcomp.CreateTargetForHwnd(backend.Device, hwnd, topmost: true, &target);
            if (hr >= 0) hr = Dcomp.CreateVisual(backend.Device, &root);
            if (hr >= 0) hr = Dcomp.SetRoot(target, root);
            if (hr >= 0) hr = Dcomp.Commit(backend.Device);
            if (hr >= 0) return new OverlayComposition(backend, target, root);
            if (root != null) Dcomp.Release(root);
            if (target != null) Dcomp.Release(target);
            return null;
        }
    }

    public void Dispose()
    {
        lock (_backend.Gate)
        {
            if (_target == null) return;
            Dcomp.SetRoot(_target, null);
            if (_backend.Device != null) Dcomp.Commit(_backend.Device);
            Dcomp.Release(_root);
            Dcomp.Release(_target);
            _root = _target = null;
        }
    }
}

/// <summary>
/// 共享缩略图视觉版的 <see cref="IThumbnailLayer"/>：每个窗口一个 DComp 视觉，按源的可见尺寸 1:1 创建，
/// 每帧只改变换矩阵（缩放 + 平移）、不透明度与可见性，最后一次 Commit 原子生效；没有逐帧同步的 DWM 调用。
/// 跨会话复用规则同经典路径（从最下层起、窗口与源矩形都没变的前缀），源矩形变了（窗口改过大小）就重建。
/// </summary>
internal sealed unsafe class SharedThumbnailLayer : IThumbnailLayer
{
    private readonly record struct Entry(nint Visual, nint Thumb, nint Hwnd, int SceneIndex, User32.RECT Source, bool HasContent);

    private readonly SharedThumbnailBackend _backend;
    private readonly OverlayComposition _composition;
    private readonly Entry _wallpaper;
    private readonly Entry[] _windows; // 从下往上
    private readonly RectD _origin;
    private bool _released; // 锁内读写：注销后渲染线程不得再碰这些视觉

    private SharedThumbnailLayer(SharedThumbnailBackend backend, OverlayComposition composition, Entry wallpaper, Entry[] windows, RectD origin)
    {
        _backend = backend;
        _composition = composition;
        _wallpaper = wallpaper;
        _windows = windows;
        _origin = origin;
    }

    /// <summary>
    /// 注册（不可见）。先把新视觉全部建好（加在最上层），成功后才撤掉上一层里不再复用的部分——
    /// 留下的复用前缀在最下面、新建的按 Z 序在上面，顺序自然正确。
    /// 任何一步失败：撤掉本次新建的、把上一层整个释放，返回 null，调用方退回经典缩略图。
    /// </summary>
    public static SharedThumbnailLayer? Register(SharedThumbnailBackend backend, OverlayComposition composition, nint overlay, RectD workArea,
        IReadOnlyList<(int SceneIndex, nint Hwnd)> windows, SharedThumbnailLayer? previous, out int hr)
    {
        lock (backend.Gate)
        {
            hr = 0;
            void* root = composition.Root;
            var created = new List<Entry>();
            var old = previous?._windows ?? [];

            // 壁纸：Progman 里本屏工作区那一块；Explorer 重启后句柄会变
            nint progman = User32.FindWindowW("Progman", null);
            bool wallpaperKept = previous is not null && previous._wallpaper.Visual != 0 && previous._wallpaper.Hwnd == progman;
            Entry wallpaper = wallpaperKept ? previous!._wallpaper : default;
            if (!wallpaperKept && progman != 0)
            {
                User32.RECT pr;
                User32.GetWindowRect(progman, &pr);
                var source = new User32.RECT
                {
                    Left = (int)workArea.Left - pr.Left, Top = (int)workArea.Top - pr.Top,
                    Right = (int)workArea.Right - pr.Left, Bottom = (int)workArea.Bottom - pr.Top,
                };
                hr = Create(backend, root, overlay, progman, source, out wallpaper);
                if (hr < 0) return Fail();
                created.Add(wallpaper);
            }

            // 窗口：壁纸重建过就不复用（新壁纸已经加在它们上面了）
            int keep = 0;
            if (wallpaperKept)
                while (keep < old.Length && keep < windows.Count)
                {
                    var (_, hwnd) = windows[windows.Count - 1 - keep];
                    if (old[keep].Hwnd != hwnd || !SameRect(old[keep].Source, SourceFor(overlay, hwnd).Source)) break;
                    keep++;
                }

            var entries = new List<Entry>(windows.Count);
            for (int k = 0; k < windows.Count; k++) // 从下往上：后加的在上层
            {
                var (index, hwnd) = windows[windows.Count - 1 - k];
                if (k < keep)
                {
                    entries.Add(old[k] with { SceneIndex = index });
                    continue;
                }
                var (source, hasContent) = SourceFor(overlay, hwnd);
                if (!hasContent)
                {
                    entries.Add(new Entry(0, 0, hwnd, index, source, false));
                    continue;
                }
                hr = Create(backend, root, overlay, hwnd, source, out var entry);
                if (hr < 0) return Fail();
                created.Add(entry);
                entries.Add(entry with { SceneIndex = index });
            }

            // 成功：撤掉上一层里不再复用的部分，复用的部分转交给新层
            if (previous is not null)
            {
                if (!wallpaperKept) Remove(root, previous._wallpaper);
                for (int i = keep; i < old.Length; i++) Remove(root, old[i]);
                previous._released = true;
            }
            hr = Dcomp.Commit(backend.Device);
            return new SharedThumbnailLayer(backend, composition, wallpaper, [.. entries], workArea);

            SharedThumbnailLayer? Fail()
            {
                foreach (var e in created) Remove(root, e);
                if (previous is not null && !previous._released)
                {
                    Remove(root, previous._wallpaper);
                    foreach (var e in old) Remove(root, e);
                    previous._released = true;
                }
                Dcomp.Commit(backend.Device);
                return null;
            }
        }
    }

    public bool HasWallpaper => _wallpaper.Visual != 0;

    public bool ShowsContent(int sceneIndex)
    {
        foreach (var e in _windows)
            if (e.SceneIndex == sceneIndex) return e.HasContent;
        return false;
    }

    public int Apply(ReadOnlySpan<RectD> sceneRects, ReadOnlySpan<double> sceneOpacity, double wallpaperOpacity)
    {
        lock (_backend.Gate)
        {
            if (_released || _backend.Device == null) return 0;
            int firstError = 0;
            void Check(int hr)
            {
                if (hr < 0 && firstError == 0) firstError = hr;
            }

            if (_wallpaper.Visual != 0)
            {
                var v = (void*)_wallpaper.Visual;
                Check(Dcomp.SetOpacity(v, (float)Math.Clamp(wallpaperOpacity, 0, 1)));
            }
            foreach (var e in _windows)
            {
                if (e.Visual == 0) continue;
                var v = (void*)e.Visual;
                if (e.SceneIndex >= sceneRects.Length)
                {
                    Check(Dcomp.SetOpacity(v, 0));
                    continue;
                }
                var r = sceneRects[e.SceneIndex];
                float sx = (float)(r.Width / Math.Max(1, e.Source.Width)), sy = (float)(r.Height / Math.Max(1, e.Source.Height));
                var m = new Matrix3x2(sx, 0, 0, sy, (float)(r.Left - _origin.Left), (float)(r.Top - _origin.Top));
                Check(Dcomp.SetTransform(v, &m));
                Check(Dcomp.SetOpacity(v, (float)Math.Clamp(sceneOpacity[e.SceneIndex], 0, 1)));
            }
            Check(Dcomp.Commit(_backend.Device));
            return firstError;
        }
    }

    public IThumbnailLayer BringToTop(nint overlay, int sceneIndex)
    {
        lock (_backend.Gate)
        {
            int idx = Array.FindIndex(_windows, e => e.SceneIndex == sceneIndex);
            if (_released || idx < 0 || idx == _windows.Length - 1 || _windows[idx].Visual == 0) return this;
            void* root = _composition.Root;
            void* v = (void*)_windows[idx].Visual;
            Dcomp.RemoveVisual(root, v);
            Dcomp.AddVisual(root, v); // 加回去就在兄弟的最上层
            var entries = _windows.Where((_, i) => i != idx).Append(_windows[idx]).ToArray();
            _released = true; // 视觉转交给新层
            return new SharedThumbnailLayer(_backend, _composition, _wallpaper, entries, _origin);
        }
    }

    public IThumbnailLayer Restack(nint overlay, IReadOnlyList<nint> topFirst)
    {
        lock (_backend.Gate)
        {
            var order = Stacking.BottomUp(_windows, e => e.Hwnd, topFirst);
            if (_released || order.Select((v, i) => v == i).All(x => x)) return this;
            void* root = _composition.Root;
            foreach (var e in _windows)
                if (e.Visual != 0) Dcomp.RemoveVisual(root, (void*)e.Visual);
            var entries = order.Select(i => _windows[i]).ToArray();
            foreach (var e in entries)
                if (e.Visual != 0) Dcomp.AddVisual(root, (void*)e.Visual); // 依次加到最上层 = 从下往上
            Dcomp.Commit(_backend.Device);
            _released = true; // 视觉转交给新层
            return new SharedThumbnailLayer(_backend, _composition, _wallpaper, entries, _origin);
        }
    }

    public void HideAll()
    {
        lock (_backend.Gate)
        {
            if (_released || _backend.Device == null) return;
            if (_wallpaper.Visual != 0) Dcomp.SetOpacity((void*)_wallpaper.Visual, 0);
            foreach (var e in _windows)
                if (e.Visual != 0) Dcomp.SetOpacity((void*)e.Visual, 0);
            Dcomp.Commit(_backend.Device);
        }
    }

    public void Unregister()
    {
        lock (_backend.Gate)
        {
            if (_released) return;
            _released = true;
            if (_backend.Device == null) return;
            void* root = _composition.Root;
            Remove(root, _wallpaper);
            foreach (var e in _windows) Remove(root, e);
            Dcomp.Commit(_backend.Device);
        }
    }

    // ---------- 锁内的小工具 ----------

    private static int Create(SharedThumbnailBackend backend, void* root, nint overlay, nint source, User32.RECT sourceRect, out Entry entry)
    {
        entry = default;
        var props = new Dwmapi.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Dwmapi.DWM_TNP_RECTDESTINATION | Dwmapi.DWM_TNP_RECTSOURCE | Dwmapi.DWM_TNP_VISIBLE | Dwmapi.DWM_TNP_OPACITY,
            rcDestination = new User32.RECT { Right = Math.Max(1, sourceRect.Width), Bottom = Math.Max(1, sourceRect.Height) },
            rcSource = sourceRect,
            opacity = 255,
            fVisible = 1,
        };
        void* visual;
        nint thumb;
        int hr = SharedThumbnailApi.Create(overlay, source, &props, backend.Device, &visual, &thumb);
        if (hr < 0) return hr;
        Dcomp.SetBitmapInterpolationMode(visual, Dcomp.BITMAP_INTERPOLATION_MODE_LINEAR);
        Dcomp.SetOpacity(visual, 0); // 共享视觉不支持 SetVisible（返回 E_INVALIDARG），一律用不透明度 0 隐藏
        hr = Dcomp.AddVisual(root, visual);
        if (hr < 0)
        {
            Dcomp.Release(visual);
            if (thumb != 0) Dwmapi.DwmUnregisterThumbnail(thumb);
            return hr;
        }
        entry = new Entry((nint)visual, thumb, source, -1, sourceRect, true);
        return 0;
    }

    private static void Remove(void* root, in Entry e)
    {
        if (e.Visual == 0) return;
        Dcomp.RemoveVisual(root, (void*)e.Visual);
        Dcomp.Release((void*)e.Visual);
        if (e.Thumb != 0) Dwmapi.DwmUnregisterThumbnail(e.Thumb);
    }

    /// <summary>源矩形：普通窗口取可见边框；最小化窗口要临时注册一个经典缩略图才能问到 DWM 保留画面的尺寸。</summary>
    private static (User32.RECT Source, bool HasContent) SourceFor(nint overlay, nint hwnd)
    {
        if (!User32.IsIconic(hwnd)) return ThumbnailLayer.ThumbnailSource(hwnd, 0);
        nint probe;
        if (Dwmapi.DwmRegisterThumbnail(overlay, hwnd, &probe) != 0) return (default, false);
        try
        {
            return ThumbnailLayer.ThumbnailSource(hwnd, probe);
        }
        finally
        {
            Dwmapi.DwmUnregisterThumbnail(probe);
        }
    }

    private static bool SameRect(User32.RECT a, User32.RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
}
