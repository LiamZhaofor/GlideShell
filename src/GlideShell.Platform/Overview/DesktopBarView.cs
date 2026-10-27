using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Shell;
using GlideShell.Platform.VirtualDesktops;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 一块屏的总览桌面条里的缩略图（共享缩略图视觉；经典缩略图下只有 GDI 画的桌面名，没有缩略图）。
/// 每个桌面一个 DComp 分支：壁纸 + 该桌面在本屏的窗口 + 窗口阴影，按工作区大小裁剪，整体等比缩放到桌面条上的位置；
/// 点击后被选中的那个从桌面条放大到全屏，落定时与切换后的真实桌面一致，直接撤层。
/// 分支按桌面缓存、跨会话复用（只重建变化的窗口）。UI 线程准备，渲染线程每帧摆放。
/// </summary>
internal sealed class DesktopBarView : IDisposable
{
    private sealed class Desktop(OverlayComposition branch) : IDisposable
    {
        public OverlayComposition Branch { get; } = branch;

        public SharedThumbnailLayer? Layer { get; set; }

        public void Dispose()
        {
            Layer?.Unregister();
            Layer = null;
            Branch.Dispose();
        }
    }

    /// <param name="ForegroundIndex">用活动阴影的窗口（当前桌面的前台窗口，-1 = 没有）。</param>
    private sealed record Slot(Desktop Desktop, SharedThumbnailLayer Layer, RectD[] Rects, double[] Opacity, double[] Active, int ForegroundIndex);

    private readonly Dictionary<Guid, Desktop> _cache = [];
    private SharedThumbnailBackend? _backend;
    private OverlayComposition? _root;
    private volatile Slot[] _slots = [];
    private volatile DesktopBarGeometry? _geometry;
    private bool _hiddenApplied;
    private double _radiusPx; // 桌面缩略图在屏幕上的圆角（物理像素）

    /// <summary>桌面缩略图的圆角相对窗口卡片圆角的比例（缩略图小得多）。</summary>
    public const double RadiusRatio = 0.6;

    /// <summary>本次会话的桌面条几何（没有桌面条时为 null）。</summary>
    public DesktopBarGeometry? Geometry => _geometry;

    /// <summary>是否有缩略图（经典缩略图模式下只有桌面名）。</summary>
    public bool HasThumbnails => _slots.Length > 0;

    /// <summary>
    /// UI 线程：为每个桌面注册缩略图（不可见）。<paramref name="windows"/> 是各桌面在本屏的窗口（Z 序，最上层在前）。
    /// <paramref name="backend"/> 或 <paramref name="root"/> 为空时只保留几何（只画桌面名）。失败返回 HRESULT（0 = 正常）。
    /// </summary>
    /// <param name="cardRadiusDip">总览窗口卡片的圆角（DIP）：桌面缩略图按 <see cref="RadiusRatio"/> 取圆角。</param>
    public int Prepare(SharedThumbnailBackend? backend, OverlayComposition? root, nint overlay, MonitorInfo monitor, DesktopBarGeometry? geometry,
        DesktopState state, IReadOnlyList<OverviewWindow> windows, nint foreground, double cardRadiusDip = 0)
    {
        _geometry = geometry;
        _radiusPx = cardRadiusDip * RadiusRatio * monitor.DpiScale;
        if (geometry is null || backend is null || root is null)
        {
            _slots = [];
            return 0;
        }
        _backend = backend;
        _root = root;

        foreach (var id in _cache.Keys.Where(id => !state.Desktops.Contains(id)).ToList())
        {
            _cache[id].Dispose();
            _cache.Remove(id);
        }

        var slots = new Slot[state.Count];
        for (int i = 0; i < state.Count; i++)
        {
            Guid id = state.Desktops[i];
            if (!_cache.TryGetValue(id, out var desktop))
            {
                var branch = root.CreateBranch(out int created);
                if (branch is null) return created;
                branch.SetRoundedClip(monitor.WorkArea.Width, monitor.WorkArea.Height, 0); // 越出工作区的窗口不能画到别的桌面缩略图上
                _cache[id] = desktop = new Desktop(branch);
            }
            var mine = windows.Where(w => w.DesktopId == id).ToList();
            var layer = SharedThumbnailLayer.Register(backend, desktop.Branch, overlay, monitor.WorkArea, monitor.DpiScale,
                mine.Select((w, k) => (k, (nint)w.Id)).ToList(), desktop.Layer, out int hr);
            desktop.Layer = layer; // 失败时上一层已被释放
            if (layer is null) return hr;
            slots[i] = new Slot(desktop, layer, mine.Select(w => w.Source).ToArray(), new double[mine.Count], new double[mine.Count],
                i == state.CurrentIndex ? mine.FindIndex(w => w.Id == foreground) : -1);
        }
        _slots = slots;
        HideAll();
        return 0;
    }

    /// <summary>UI 线程：点击后，被选中的桌面挪到其余桌面上面（放大时盖住它们）。</summary>
    public void BringToFront(int index)
    {
        var slots = _slots;
        if (index >= 0 && index < slots.Length) slots[index].Desktop.Branch.BringToFront();
    }

    /// <summary>
    /// 渲染线程：按本帧的展开程度 / 放大进度摆放各桌面缩略图（不提交；调用方随后一次 Commit）。返回是否改动了任何东西。
    /// </summary>
    public bool Apply(in OverviewFrame frame)
    {
        var g = _geometry;
        var slots = _slots;
        if (g is null || slots.Length == 0) return false;
        double e = Math.Clamp(frame.BarExpansion, 0, 1), z = Math.Clamp(frame.Zoom, 0, 1);
        int target = frame.ZoomDesktop;
        bool hidden = e <= 0 && target < 0;
        if (hidden && _hiddenApplied) return false; // 收起着：缩略图已经全透明，不必每帧再设
        _hiddenApplied = hidden;

        for (int i = 0; i < slots.Length && i < g.Count; i++)
        {
            var s = slots[i];
            bool chosen = i == target;
            var r = chosen ? g.ZoomRect(i, e, z) : g.ThumbRect(i, e);
            // 选中的那个点下去就不透明（可能还没完全展开就点了）；其余随放大淡出
            double alpha = chosen ? Math.Max(e, OverviewVisuals.SmoothStep(0, 0.15, z)) : e * (1 - z);
            double scale = r.Width / g.Area.Width;
            s.Desktop.Branch.SetTransform(scale, r.Left - g.Area.Left, r.Top - g.Area.Top);
            // 圆角在屏幕上保持不变（分支整体缩放，半径要除回去）；放大进入时随进度收成直角，落定时与真实桌面一致
            s.Desktop.Branch.SetRoundedClip(g.Area.Width, g.Area.Height, _radiusPx * (chosen ? 1 - z : 1) / Math.Max(1e-3, scale));
            for (int k = 0; k < s.Opacity.Length; k++)
            {
                s.Opacity[k] = alpha;
                // 活动阴影：当前桌面是原前台窗口；放大进入的桌面，最上层窗口随进度变成活动的（落定后它会被激活）
                s.Active[k] = chosen ? (k == 0 ? z : 0) : k == s.ForegroundIndex ? 1 : 0;
            }
            s.Layer.Apply(s.Rects, s.Opacity, s.Active, alpha, commit: false);
        }
        return true;
    }

    public void HideAll()
    {
        foreach (var s in _slots) s.Layer.HideAll();
        _hiddenApplied = true;
    }

    public void Dispose()
    {
        foreach (var d in _cache.Values) d.Dispose();
        _cache.Clear();
        _slots = [];
    }
}
