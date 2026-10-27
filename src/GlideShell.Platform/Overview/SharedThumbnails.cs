using System.Numerics;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Overview;

/// <summary>覆盖层上的一组窗口缩略图：经典 DWM 缩略图（<see cref="ThumbnailLayer"/>）或共享缩略图视觉（<see cref="SharedThumbnailLayer"/>）。</summary>
internal interface IThumbnailLayer
{
    /// <summary>
    /// 渲染线程：按本帧的窗口矩形（屏幕坐标）、窗口不透明度、「活动阴影」权重（<see cref="WindowShadow.ActiveWeight"/>）
    /// 与壁纸不透明度更新。返回第一个失败的 HRESULT（0 = 全部成功）。
    /// </summary>
    int Apply(ReadOnlySpan<RectD> sceneRects, ReadOnlySpan<double> sceneOpacity, ReadOnlySpan<double> sceneActive, double wallpaperOpacity);

    /// <summary>是否画窗口阴影：画了的话撤层那一刻缩略图与真实窗口一致，可以直接撤层，不用淡出。</summary>
    bool DrawsShadows { get; }

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
/// 共享缩略图视觉后端（默认开启，见 docs/CONFIG.md 的 overview.sharedThumbnails）：一个 DComp 桌面设备，
/// 所有 DComp 调用都在 <see cref="Gate"/> 锁内——UI 线程注册 / 注销、渲染线程逐帧改属性与 Commit 互不踩踏。
/// 依赖未公开的 dwmapi#147（S4），任何一步失败都退回经典缩略图。
/// 设备挂在一个 D3D11 设备上，用来上传窗口阴影图（DComp 表面）；D3D 不可用时照常工作，只是不画阴影。
/// </summary>
internal sealed unsafe class SharedThumbnailBackend : IDisposable
{
    private void* _device;
    private void* _d3d;
    private void* _context;

    private SharedThumbnailBackend(void* device, void* d3d, void* context)
    {
        _device = device;
        _d3d = d3d;
        _context = context;
    }

    public object Gate { get; } = new();

    public void* Device => _device;

    /// <summary>能不能画窗口阴影（需要 D3D11 设备上传表面）。</summary>
    public bool CanDrawShadows => _context != null;

    public static SharedThumbnailBackend? TryCreate(out string status)
    {
        status = SharedThumbnailApi.Load();
        if (!SharedThumbnailApi.Available) return null;

        void* d3d = null, context = null, dxgi = null;
        int fl;
        // 用 WARP（软件光栅化）：阴影图只在注册时上传一次，用不着 GPU；硬件设备要加载显卡驱动，实测多 28MB 私有内存、126ms，WARP 只多 1.4MB、51ms。
        // 只在持有 Gate 锁的 UI 线程上用，所以单线程设备即可
        if (Dcomp.D3D11CreateDevice(null, 5 /* WARP */, 0, 0x20 | 0x1 /* BGRA_SUPPORT | SINGLETHREADED */, null, 0, 7, &d3d, &fl, &context) >= 0)
        {
            Guid dxgiIid = Dcomp.IID_IDXGIDevice;
            if (Dcomp.QueryInterface(d3d, &dxgiIid, &dxgi) < 0) dxgi = null;
        }

        Guid iid = Dcomp.IID_IDCompositionDesktopDevice;
        void* device;
        int hr = Dcomp.DCompositionCreateDevice3(dxgi, &iid, &device);
        if (dxgi != null) Dcomp.Release(dxgi);
        if (hr < 0 && dxgi != null) hr = Dcomp.DCompositionCreateDevice3(null, &iid, &device); // 带 D3D 失败就不画阴影
        if (hr < 0 || device == null)
        {
            if (context != null) Dcomp.Release(context);
            if (d3d != null) Dcomp.Release(d3d);
            status = $"创建 DirectComposition 设备失败 0x{hr:X8}";
            return null;
        }
        if (context == null && d3d != null)
        {
            Dcomp.Release(d3d);
            d3d = null;
        }
        status = context != null ? "共享缩略图视觉可用（dwmapi#147 + DirectComposition + 窗口阴影）" : "共享缩略图视觉可用（没有 D3D11，不画窗口阴影）";
        return new SharedThumbnailBackend(device, d3d, context);
    }

    /// <summary>一张已上传的阴影表面：同样大小、同样 DPI 的窗口共用（总览、桌面条、切桌面里同一个窗口各有一个视觉，只画一次图）。</summary>
    private sealed class CachedShadow(nint surface, int downscale, double marginPx, (ShadowStyle, int, int, int) key)
    {
        public nint Surface { get; } = surface;
        public int Downscale { get; } = downscale;
        public double MarginPx { get; } = marginPx;
        public (ShadowStyle, int, int, int) Key { get; } = key;
        public int Users { get; set; }
    }

    private readonly Dictionary<(ShadowStyle, int, int, int), CachedShadow> _shadows = [];
    private readonly Dictionary<nint, CachedShadow> _shadowsBySurface = [];

    /// <summary>
    /// 锁内调用：取某个大小窗口的阴影（没有就画好图、上传成 DComp 表面并缓存），挂到一个新视觉上返回
    /// （IDCompositionVisual3，初始不透明度 0）。不用了调用 <see cref="ReleaseShadow"/>。
    /// </summary>
    public int CreateShadowVisual(ShadowStyle style, double widthPx, double heightPx, double dpiScale,
        out nint visual, out nint surface, out int downscale, out double marginPx)
    {
        visual = surface = 0;
        downscale = 1;
        marginPx = 0;
        if (_context == null) return unchecked((int)0x80004001);
        var key = (style, (int)Math.Round(widthPx), (int)Math.Round(heightPx), (int)Math.Round(dpiScale * 96));
        if (!_shadows.TryGetValue(key, out var cached))
        {
            var image = WindowShadow.Render(style, widthPx, heightPx, dpiScale);
            int uploaded = Upload(image, out nint s);
            image.Return();
            if (uploaded < 0) return uploaded;
            cached = new CachedShadow(s, image.Downscale, image.MarginPx, key);
            _shadows[key] = cached;
            _shadowsBySurface[s] = cached;
        }

        void* v2, v3;
        int hr = Dcomp.CreateVisual(_device, &v2);
        if (hr >= 0) hr = Dcomp.SetContent(v2, (void*)cached.Surface);
        if (hr >= 0)
        {
            Dcomp.SetBitmapInterpolationMode(v2, Dcomp.BITMAP_INTERPOLATION_MODE_LINEAR);
            hr = Dcomp.ToVisual3(v2, &v3);
        }
        else
        {
            if (v2 != null) Dcomp.Release(v2);
            v3 = null;
        }
        if (hr < 0) return hr;
        Dcomp.SetOpacity(v3, 0);
        cached.Users++;
        visual = (nint)v3;
        surface = cached.Surface;
        downscale = cached.Downscale;
        marginPx = cached.MarginPx;
        return 0;
    }

    /// <summary>
    /// 一张固定图片（预乘 BGRA，<paramref name="size"/>×<paramref name="size"/>）做成的视觉，挂到 <paramref name="parent"/> 下面，
    /// 初始不可见（不透明度 0）。用于总览里的关闭按钮。失败返回 HRESULT。
    /// </summary>
    public int CreateImageVisual(OverlayComposition parent, int size, byte[] bgra, out nint visual)
    {
        lock (Gate)
        {
            visual = 0;
            if (_device == null) return unchecked((int)0x80004001);
            int hr = Upload(new ShadowImage(size, size, 1, 0, bgra), out nint surface);
            if (hr < 0) return hr;
            void* v2, v3 = null;
            hr = Dcomp.CreateVisual(_device, &v2);
            if (hr >= 0) hr = Dcomp.SetContent(v2, (void*)surface);
            if (hr >= 0) hr = Dcomp.ToVisual3(v2, &v3);
            else if (v2 != null) Dcomp.Release(v2);
            Dcomp.Release((void*)surface); // 视觉持有表面
            if (hr < 0) return hr;
            Dcomp.SetOpacity(v3, 0);
            hr = Dcomp.AddVisual(parent.Root, v3);
            if (hr < 0)
            {
                Dcomp.Release(v3);
                return hr;
            }
            Dcomp.Commit(_device);
            visual = (nint)v3;
            return 0;
        }
    }

    /// <summary>把 <see cref="CreateImageVisual"/> 建的视觉移到 (<paramref name="x"/>, <paramref name="y"/>)（相对父视觉）并设不透明度，立即提交。</summary>
    public void PlaceImageVisual(nint visual, double x, double y, double opacity)
    {
        lock (Gate)
        {
            if (_device == null || visual == 0) return;
            var m = new System.Numerics.Matrix3x2(1, 0, 0, 1, (float)x, (float)y);
            Dcomp.SetTransform((void*)visual, &m);
            Dcomp.SetOpacity((void*)visual, (float)opacity);
            Dcomp.Commit(_device);
        }
    }

    /// <summary>锁内调用：一个视觉不再用这张阴影表面；没人用了就释放。</summary>
    public void ReleaseShadow(nint surface)
    {
        if (!_shadowsBySurface.TryGetValue(surface, out var cached) || --cached.Users > 0) return;
        _shadowsBySurface.Remove(surface);
        _shadows.Remove(cached.Key);
        Dcomp.Release((void*)surface);
    }

    /// <summary>把阴影图上传成 DComp 表面（预乘 BGRA）。</summary>
    private int Upload(ShadowImage image, out nint surface)
    {
        surface = 0;
        void* s;
        int hr = Dcomp.CreateSurface(_device, (uint)image.Width, (uint)image.Height, 87 /* B8G8R8A8_UNORM */, 1 /* 预乘 */, &s);
        if (hr < 0) return hr;
        Guid texIid = Dcomp.IID_ID3D11Texture2D;
        void* texture;
        Dcomp.POINT offset;
        hr = Dcomp.BeginDraw(s, null, &texIid, &texture, &offset);
        if (hr < 0)
        {
            Dcomp.Release(s);
            return hr;
        }
        // BeginDraw 给的可能是图集里的一块：只更新 offset 起的那一块
        var box = new Dcomp.D3D11_BOX
        {
            left = (uint)offset.X, top = (uint)offset.Y, front = 0,
            right = (uint)(offset.X + image.Width), bottom = (uint)(offset.Y + image.Height), back = 1,
        };
        fixed (byte* pixels = image.Bgra) Dcomp.UpdateSubresource(_context, texture, 0, &box, pixels, (uint)image.Width * 4, 0);
        Dcomp.Release(texture);
        hr = Dcomp.EndDraw(s);
        if (hr < 0)
        {
            Dcomp.Release(s);
            return hr;
        }
        surface = (nint)s;
        return 0;
    }

    /// <summary>渲染线程：把本帧几处不提交的修改一次生效。</summary>
    public int Commit()
    {
        lock (Gate) return _device == null ? 0 : Dcomp.Commit(_device);
    }

    public void Dispose()
    {
        lock (Gate)
        {
            foreach (var c in _shadows.Values) Dcomp.Release((void*)c.Surface);
            _shadows.Clear();
            _shadowsBySurface.Clear();
            if (_device != null) Dcomp.Release(_device);
            if (_context != null) Dcomp.Release(_context);
            if (_d3d != null) Dcomp.Release(_d3d);
            _device = _context = _d3d = null;
        }
    }
}

/// <summary>
/// 一个覆盖层窗口的 DComp 目标（topmost：画在窗口自己的 GDI 内容之上，与经典缩略图的层次一致）与根视觉；
/// 或者挂在某个根视觉下的分支（<see cref="CreateBranch"/>：一组视觉整体平移，例如切桌面时的一整个桌面）。
/// </summary>
internal sealed unsafe class OverlayComposition : IDisposable
{
    private readonly SharedThumbnailBackend _backend;
    private void* _target; // 顶层才有
    private void* _parent; // 分支才有
    private void* _root;

    private OverlayComposition(SharedThumbnailBackend backend, void* target, void* parent, void* root)
    {
        _backend = backend;
        _target = target;
        _parent = parent;
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
            if (hr >= 0) return new OverlayComposition(backend, target, null, root);
            if (root != null) Dcomp.Release(root);
            if (target != null) Dcomp.Release(target);
            return null;
        }
    }

    /// <summary>在本根视觉下建一个分支（加在最上层）。分支要在本对象之前释放。</summary>
    public OverlayComposition? CreateBranch(out int hr)
    {
        lock (_backend.Gate)
        {
            void* visual = null;
            hr = _root == null ? unchecked((int)0x80004005) : Dcomp.CreateVisual(_backend.Device, &visual);
            if (hr >= 0) hr = Dcomp.AddVisual(_root, visual);
            if (hr >= 0) return new OverlayComposition(_backend, null, _root, visual);
            if (visual != null) Dcomp.Release(visual);
            return null;
        }
    }

    /// <summary>渲染线程：整体平移（不提交，随后由层的 Apply 或 <see cref="SharedThumbnailBackend.Commit"/> 一起生效）。</summary>
    public void SetOffset(double x, double y) => SetTransform(1, x, y);

    /// <summary>渲染线程：整体等比缩放 <paramref name="scale"/> 再平移（不提交）。</summary>
    public void SetTransform(double scale, double x, double y)
    {
        lock (_backend.Gate)
        {
            if (_root == null || _backend.Device == null) return;
            var m = new Matrix3x2((float)scale, 0, 0, (float)scale, (float)x, (float)y);
            Dcomp.SetTransform(_root, &m);
        }
    }

    /// <summary>裁剪到自己坐标系里的 (0,0)–(width,height)（缩放后随之缩小）。</summary>
    public void SetClip(double width, double height)
    {
        lock (_backend.Gate)
        {
            if (_root == null) return;
            var r = new Dcomp.D2D_RECT_F { Right = (float)width, Bottom = (float)height };
            Dcomp.SetClip(_root, &r);
        }
    }

    /// <summary>分支：挪到兄弟的最上层（不提交）。</summary>
    public void BringToFront()
    {
        lock (_backend.Gate)
        {
            if (_root == null || _parent == null) return;
            Dcomp.RemoveVisual(_parent, _root);
            Dcomp.AddVisual(_parent, _root);
        }
    }

    public void Dispose()
    {
        lock (_backend.Gate)
        {
            if (_root == null) return;
            if (_target != null) Dcomp.SetRoot(_target, null);
            if (_parent != null) Dcomp.RemoveVisual(_parent, _root);
            if (_backend.Device != null) Dcomp.Commit(_backend.Device);
            Dcomp.Release(_root);
            if (_target != null) Dcomp.Release(_target);
            _root = _target = _parent = null;
        }
    }
}

/// <summary>
/// 共享缩略图视觉版的 <see cref="IThumbnailLayer"/>。每个窗口一个分组视觉，组内从下往上：非活动阴影、活动阴影、缩略图；
/// 缩略图按源的可见尺寸 1:1 创建，阴影图按窗口真实大小画好（<see cref="WindowShadow"/>，1/4 分辨率），
/// 每帧只改变换矩阵与不透明度，最后一次 Commit 原子生效；没有逐帧同步的 DWM 调用。
/// 层次操作（复用前缀、提到最上层、按 Z 序重排）都以分组为单位，阴影始终紧贴在自己窗口的下面。
/// 跨会话复用规则同经典路径（从最下层起、窗口与源矩形都没变的前缀），源矩形变了（窗口改过大小）就重建。
/// </summary>
internal static unsafe class SharedThumbnailBackendExtensions
{
    /// <summary>把挂在 <paramref name="branch"/> 下、已经不属于任何层的分组摘下来（它所在的层已注销）。</summary>
    public static void Detach(this SharedThumbnailBackend backend, OverlayComposition branch, nint group)
    {
        lock (backend.Gate)
        {
            if (backend.Device == null) return;
            Dcomp.RemoveVisual(branch.Root, (void*)group);
            Dcomp.Commit(backend.Device);
        }
    }
}

internal sealed unsafe partial class SharedThumbnailLayer : IThumbnailLayer
{
    private readonly record struct Shadow(nint Visual, nint Surface, int Downscale, double MarginPx);

    /// <param name="Group">挂在根视觉下的那个（壁纸就是缩略图视觉本身）。</param>
    private readonly record struct Entry(nint Group, nint Visual, nint Thumb, nint Hwnd, int SceneIndex, User32.RECT Source, bool HasContent,
        Shadow Inactive, Shadow Active);

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

    public bool DrawsShadows => _backend.CanDrawShadows;

    /// <summary>
    /// 注册（不可见）。先把新视觉全部建好（加在最上层），成功后才撤掉上一层里不再复用的部分——
    /// 留下的复用前缀在最下面、新建的按 Z 序在上面，顺序自然正确。
    /// 任何一步失败：撤掉本次新建的、把上一层整个释放，返回 null，调用方退回经典缩略图。
    /// <paramref name="withWallpaper"/> 为 false 时只有窗口（总览里各桌面的窗口页共用一层静止的背景）。
    /// </summary>
    public static SharedThumbnailLayer? Register(SharedThumbnailBackend backend, OverlayComposition composition, nint overlay, RectD workArea,
        double dpiScale, IReadOnlyList<(int SceneIndex, nint Hwnd)> windows, SharedThumbnailLayer? previous, out int hr, bool withWallpaper = true)
    {
        lock (backend.Gate)
        {
            hr = 0;
            void* root = composition.Root;
            var created = new List<Entry>();
            var old = previous?._windows ?? [];

            // 壁纸：Progman 里本屏工作区那一块；Explorer 重启后句柄会变
            nint progman = User32.FindWindowW("Progman", null);
            bool wallpaperKept = withWallpaper
                ? previous is not null && previous._wallpaper.Visual != 0 && previous._wallpaper.Hwnd == progman
                : previous is not null && previous._wallpaper.Visual == 0; // 不要壁纸：上一层也没有壁纸才能复用
            Entry wallpaper = wallpaperKept ? previous!._wallpaper : default;
            if (withWallpaper && !wallpaperKept && progman != 0)
            {
                User32.RECT pr;
                User32.GetWindowRect(progman, &pr);
                var source = new User32.RECT
                {
                    Left = (int)workArea.Left - pr.Left, Top = (int)workArea.Top - pr.Top,
                    Right = (int)workArea.Right - pr.Left, Bottom = (int)workArea.Bottom - pr.Top,
                };
                hr = CreateThumbnail(backend, overlay, progman, source, out nint visual, out nint thumb);
                if (hr >= 0) hr = Dcomp.AddVisual(root, (void*)visual);
                if (hr < 0) return Fail();
                wallpaper = new Entry(visual, visual, thumb, progman, -1, source, true, default, default);
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
                    entries.Add(new Entry(0, 0, 0, hwnd, index, source, false, default, default));
                    continue;
                }
                hr = CreateWindowGroup(backend, root, overlay, hwnd, source, dpiScale, out var entry);
                if (hr < 0) return Fail();
                created.Add(entry);
                entries.Add(entry with { SceneIndex = index });
            }

            // 成功：撤掉上一层里不再复用的部分，复用的部分转交给新层
            if (previous is not null)
            {
                if (!wallpaperKept) Remove(backend, root, previous._wallpaper);
                for (int i = keep; i < old.Length; i++) Remove(backend, root, old[i]);
                previous._released = true;
            }
            hr = Dcomp.Commit(backend.Device);
            return new SharedThumbnailLayer(backend, composition, wallpaper, [.. entries], workArea);

            SharedThumbnailLayer? Fail()
            {
                foreach (var e in created) Remove(backend, root, e);
                if (previous is not null && !previous._released)
                {
                    Remove(backend, root, previous._wallpaper);
                    foreach (var e in old) Remove(backend, root, e);
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

    public int Apply(ReadOnlySpan<RectD> sceneRects, ReadOnlySpan<double> sceneOpacity, ReadOnlySpan<double> sceneActive, double wallpaperOpacity)
        => Apply(sceneRects, sceneOpacity, sceneActive, wallpaperOpacity, commit: true);

    /// <param name="commit">false：不提交，由调用方把几层的修改一起 Commit（切桌面的三个桌面要同一帧生效）。</param>
    public int Apply(ReadOnlySpan<RectD> sceneRects, ReadOnlySpan<double> sceneOpacity, ReadOnlySpan<double> sceneActive, double wallpaperOpacity, bool commit)
    {
        lock (_backend.Gate)
        {
            if (_released || _backend.Device == null) return 0;
            int firstError = 0;
            void Check(int hr)
            {
                if (hr < 0 && firstError == 0) firstError = hr;
            }

            if (_wallpaper.Visual != 0) Check(Dcomp.SetOpacity((void*)_wallpaper.Visual, (float)Math.Clamp(wallpaperOpacity, 0, 1)));
            foreach (var e in _windows)
            {
                if (e.Visual == 0) continue;
                var v = (void*)e.Visual;
                if (e.SceneIndex >= sceneRects.Length)
                {
                    Check(Dcomp.SetOpacity(v, 0));
                    if (e.Inactive.Visual != 0) Check(Dcomp.SetOpacity((void*)e.Inactive.Visual, 0));
                    if (e.Active.Visual != 0) Check(Dcomp.SetOpacity((void*)e.Active.Visual, 0));
                    continue;
                }
                var r = sceneRects[e.SceneIndex];
                double sx = r.Width / Math.Max(1, e.Source.Width), sy = r.Height / Math.Max(1, e.Source.Height);
                double left = r.Left - _origin.Left, top = r.Top - _origin.Top;
                var m = new Matrix3x2((float)sx, 0, 0, (float)sy, (float)left, (float)top);
                Check(Dcomp.SetTransform(v, &m));
                double opacity = Math.Clamp(sceneOpacity[e.SceneIndex], 0, 1);
                Check(Dcomp.SetOpacity(v, (float)opacity));

                // 阴影图覆盖窗口四周 MarginPx，按 Downscale 缩小存储：缩放 = 窗口缩放 × Downscale，左上角再往外挪 MarginPx
                double active = e.SceneIndex < sceneActive.Length ? Math.Clamp(sceneActive[e.SceneIndex], 0, 1) : 0;
                ApplyShadow(e.Inactive, opacity * (1 - active));
                ApplyShadow(e.Active, opacity * active);

                void ApplyShadow(Shadow s, double alpha)
                {
                    if (s.Visual == 0) return;
                    var sm = new Matrix3x2((float)(sx * s.Downscale), 0, 0, (float)(sy * s.Downscale),
                        (float)(left - s.MarginPx * sx), (float)(top - s.MarginPx * sy));
                    Check(Dcomp.SetTransform((void*)s.Visual, &sm));
                    Check(Dcomp.SetOpacity((void*)s.Visual, (float)alpha));
                }
            }
            if (commit) Check(Dcomp.Commit(_backend.Device));
            return firstError;
        }
    }

    /// <summary>
    /// 拖动窗口时把它的分组（连同阴影）从本层摘下、挂到 <paramref name="into"/>（桌面条上面那一支），返回分组视觉（0 = 没有）。
    /// 两支坐标系相同，渲染照常按本层更新它的变换。放回用 <see cref="PutBack"/>。
    /// </summary>
    public nint Lift(int sceneIndex, OverlayComposition into)
    {
        lock (_backend.Gate)
        {
            int idx = Array.FindIndex(_windows, e => e.SceneIndex == sceneIndex);
            if (_released || idx < 0 || _windows[idx].Group == 0) return 0;
            void* g = (void*)_windows[idx].Group;
            if (Dcomp.RemoveVisual(_composition.Root, g) < 0) return 0;
            Dcomp.AddVisual(into.Root, g);
            Dcomp.Commit(_backend.Device);
            return (nint)g;
        }
    }

    /// <summary>把 <see cref="Lift"/> 摘走的分组从 <paramref name="from"/> 放回本层原来的层次位置；不是本层的返回 false。</summary>
    public bool PutBack(nint group, OverlayComposition from)
    {
        lock (_backend.Gate)
        {
            int idx = Array.FindIndex(_windows, e => e.Group == group);
            if (_released || idx < 0 || group == 0) return false;
            void* root = _composition.Root, g = (void*)group;
            Dcomp.RemoveVisual(from.Root, g);
            // 插在下面最近一个窗口的上面；没有就插在上面最近一个的下面；都没有就在壁纸上面（或最上层）
            int below = Array.FindLastIndex(_windows, idx, e => e.Group != 0 && e.Group != group);
            int above = idx + 1 < _windows.Length ? Array.FindIndex(_windows, idx + 1, e => e.Group != 0) : -1;
            if (below >= 0) Dcomp.AddVisual(root, g, true, (void*)_windows[below].Group);
            else if (above >= 0) Dcomp.AddVisual(root, g, false, (void*)_windows[above].Group);
            else if (_wallpaper.Visual != 0) Dcomp.AddVisual(root, g, true, (void*)_wallpaper.Visual);
            else Dcomp.AddVisual(root, g);
            Dcomp.Commit(_backend.Device);
            return true;
        }
    }

    /// <summary>
    /// 加入一个窗口（从别的桌面拖到这一页），分组挂在最上层，场景下标 <paramref name="sceneIndex"/>；返回接管全部视觉的新层，失败返回 null（本层不变）。
    /// </summary>
    public SharedThumbnailLayer? AddWindow(nint overlay, int sceneIndex, nint hwnd, double dpiScale, out int hr)
    {
        lock (_backend.Gate)
        {
            hr = unchecked((int)0x80004005);
            if (_released) return null;
            var (source, hasContent) = SourceFor(overlay, hwnd);
            Entry entry;
            if (!hasContent) entry = new Entry(0, 0, 0, hwnd, sceneIndex, source, false, default, default);
            else
            {
                hr = CreateWindowGroup(_backend, _composition.Root, overlay, hwnd, source, dpiScale, out entry);
                if (hr < 0) return null;
                entry = entry with { SceneIndex = sceneIndex };
                Dcomp.Commit(_backend.Device);
            }
            hr = 0;
            _released = true; // 视觉转交给新层
            return new SharedThumbnailLayer(_backend, _composition, _wallpaper, [.. _windows, entry], _origin);
        }
    }

    public IThumbnailLayer BringToTop(nint overlay, int sceneIndex)
    {
        lock (_backend.Gate)
        {
            int idx = Array.FindIndex(_windows, e => e.SceneIndex == sceneIndex);
            if (_released || idx < 0 || idx == _windows.Length - 1 || _windows[idx].Group == 0) return this;
            void* root = _composition.Root;
            void* g = (void*)_windows[idx].Group;
            Dcomp.RemoveVisual(root, g);
            Dcomp.AddVisual(root, g); // 加回去就在兄弟的最上层（连同它的阴影）
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
                if (e.Group != 0) Dcomp.RemoveVisual(root, (void*)e.Group);
            var entries = order.Select(i => _windows[i]).ToArray();
            foreach (var e in entries)
                if (e.Group != 0) Dcomp.AddVisual(root, (void*)e.Group); // 依次加到最上层 = 从下往上
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
            {
                if (e.Visual != 0) Dcomp.SetOpacity((void*)e.Visual, 0);
                if (e.Inactive.Visual != 0) Dcomp.SetOpacity((void*)e.Inactive.Visual, 0);
                if (e.Active.Visual != 0) Dcomp.SetOpacity((void*)e.Active.Visual, 0);
            }
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
            Remove(_backend, root, _wallpaper);
            foreach (var e in _windows) Remove(_backend, root, e);
            Dcomp.Commit(_backend.Device);
        }
    }

    // ---------- 锁内的小工具 ----------

    private static int CreateThumbnail(SharedThumbnailBackend backend, nint overlay, nint source, User32.RECT sourceRect, out nint visual, out nint thumbnail)
    {
        visual = thumbnail = 0;
        var props = new Dwmapi.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Dwmapi.DWM_TNP_RECTDESTINATION | Dwmapi.DWM_TNP_RECTSOURCE | Dwmapi.DWM_TNP_VISIBLE | Dwmapi.DWM_TNP_OPACITY,
            rcDestination = new User32.RECT { Right = Math.Max(1, sourceRect.Width), Bottom = Math.Max(1, sourceRect.Height) },
            rcSource = sourceRect,
            opacity = 255,
            fVisible = 1,
        };
        void* v;
        nint thumb;
        int hr = SharedThumbnailApi.Create(overlay, source, &props, backend.Device, &v, &thumb);
        if (hr < 0) return hr;
        Dcomp.SetBitmapInterpolationMode(v, Dcomp.BITMAP_INTERPOLATION_MODE_LINEAR);
        Dcomp.SetOpacity(v, 0); // 共享视觉不支持 SetVisible（返回 E_INVALIDARG），一律用不透明度 0 隐藏
        visual = (nint)v;
        thumbnail = thumb;
        return 0;
    }

    /// <summary>建一个窗口的分组：非活动阴影、活动阴影（窗口有系统阴影时）、缩略图，挂到根视觉最上层。</summary>
    private static int CreateWindowGroup(SharedThumbnailBackend backend, void* root, nint overlay, nint hwnd, User32.RECT source, double dpiScale, out Entry entry)
    {
        entry = default;
        void* created;
        int hr = Dcomp.CreateVisual(backend.Device, &created);
        if (hr < 0) return hr;
        nint group = (nint)created;
        Shadow inactive = default, active = default;
        if (backend.CanDrawShadows && CastsShadow(hwnd))
        {
            inactive = MakeShadow(ShadowStyle.Inactive);
            active = MakeShadow(ShadowStyle.Active);
        }
        hr = CreateThumbnail(backend, overlay, hwnd, source, out nint visual, out nint thumb);
        if (hr >= 0) hr = Dcomp.AddVisual((void*)group, (void*)visual);
        if (hr >= 0) hr = Dcomp.AddVisual(root, (void*)group);
        entry = new Entry(group, visual, thumb, hwnd, -1, source, true, inactive, active);
        if (hr < 0)
        {
            Remove(backend, null, entry);
            entry = default;
        }
        return hr;

        Shadow MakeShadow(ShadowStyle style)
        {
            if (backend.CreateShadowVisual(style, source.Width, source.Height, dpiScale, out nint v, out nint s, out int downscale, out double margin) < 0)
                return default;
            Dcomp.AddVisual((void*)group, (void*)v); // 先加的在下面：阴影垫在缩略图下
            return new Shadow(v, s, downscale, margin);
        }
    }

    /// <summary>
    /// 窗口有没有系统阴影：有标题栏或可调边框、而且不是最大化（最大化窗口没有阴影；最小化前是最大化的，还原后也是最大化）。
    /// </summary>
    private static bool CastsShadow(nint hwnd)
    {
        const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
        long style = User32.GetWindowLongPtrW(hwnd, User32.GWL_STYLE);
        if ((style & (WS_CAPTION | WS_THICKFRAME)) == 0) return false;
        if (IsZoomed(hwnd)) return false;
        if (User32.IsIconic(hwnd))
        {
            var wp = new User32.WINDOWPLACEMENT { length = (uint)sizeof(User32.WINDOWPLACEMENT) };
            if (User32.GetWindowPlacement(hwnd, &wp) && (wp.flags & User32.WPF_RESTORETOMAXIMIZED) != 0) return false;
        }
        return true;
    }

    /// <summary>从父视觉上摘下并释放（root 为 null 时只释放：还没挂上去）。</summary>
    private static void Remove(SharedThumbnailBackend backend, void* root, in Entry e)
    {
        if (e.Group == 0 && e.Visual == 0) return;
        if (root != null && e.Group != 0) Dcomp.RemoveVisual(root, (void*)e.Group);
        foreach (var s in new[] { e.Inactive, e.Active })
        {
            if (s.Visual != 0) Dcomp.Release((void*)s.Visual);
            if (s.Surface != 0) backend.ReleaseShadow(s.Surface);
        }
        if (e.Visual != 0) Dcomp.Release((void*)e.Visual);
        if (e.Group != 0 && e.Group != e.Visual) Dcomp.Release((void*)e.Group);
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

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint hwnd);
}
