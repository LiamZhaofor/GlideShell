using GlideShell.Core.Desktops;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Native;
using GlideShell.Platform.Overview;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;

namespace GlideShell.Platform.Desktops;

/// <summary>
/// 一块屏的切桌面覆盖层：置顶工具窗口（不属于任何虚拟桌面），黑底（桌面之间的缝隙），不激活、鼠标穿透。
/// 共享缩略图模式下每个桌面一个 DComp 分支（壁纸 + 窗口 + 窗口阴影），整个分支随位置平移；
/// 分支按桌面缓存、跨会话复用（切过去再切回来不用重建阴影图），不再相邻的桌面才释放。
/// </summary>
internal sealed unsafe class DesktopOverlay : NativeWindow
{
    private readonly Dictionary<Guid, SharedDesktopLayer.Desktop> _cache = [];
    private OverlayComposition? _composition;
    private volatile IDesktopLayer _layer = DesktopLayer.Empty;

    public DesktopOverlay(MonitorInfo monitor)
    {
        Monitor = monitor;
        var wa = monitor.WorkArea;
        Create("GlideShell.Desktops", User32.WS_POPUP,
            User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW_U | User32.WS_EX_NOACTIVATE_U | 0x20 /* WS_EX_TRANSPARENT */,
            (int)wa.Left, (int)wa.Top, (int)wa.Width, (int)wa.Height);
    }

    public MonitorInfo Monitor { get; }

    public IDesktopLayer Layer => _layer;

    /// <summary>
    /// UI 线程：注册三个桌面的缩略图（不可见）。<paramref name="windows"/> 按 Z 序、最上层在前。
    /// <paramref name="shared"/> 非空时用共享缩略图视觉；那条路失败就退回经典缩略图并返回失败的 HRESULT（0 = 正常）。
    /// </summary>
    public int Prepare(IReadOnlyList<OverviewWindow> windows, Guid current, Guid? left, Guid? right, nint foreground, SharedThumbnailBackend? shared)
    {
        var slots = new List<(int Slot, Guid Id)> { (0, current) };
        if (left is { } l) slots.Add((-1, l));
        if (right is { } r) slots.Add((1, r));
        var old = _layer;
        int failure = 0;
        if (shared is not null)
        {
            if (old is DesktopLayer classic) classic.Unregister();
            var next = SharedDesktopLayer.Register(shared, ref _composition, Handle, Monitor, windows, slots, foreground, _cache, out failure);
            if (next is not null)
            {
                _layer = next;
                return 0;
            }
            DisposeShared();
        }
        if (old is DesktopLayer previous) previous.Unregister();
        _layer = DesktopLayer.Register(Handle, Monitor, windows, slots);
        return failure;
    }

    /// <summary>会话结束：隐藏（共享缩略图保留注册供下次复用，经典缩略图直接注销）。</summary>
    public void ReleaseLayer()
    {
        var old = _layer;
        if (old is DesktopLayer)
        {
            _layer = DesktopLayer.Empty;
            old.Unregister();
        }
        else old.HideAll();
    }

    public void ShowOverlay()
    {
        var wa = Monitor.WorkArea;
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, (int)wa.Left, (int)wa.Top, (int)wa.Width, (int)wa.Height,
            User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
    }

    public void HideOverlay()
    {
        // 先藏窗口再藏缩略图：反过来的话中间可能合成出一帧「覆盖层还在、缩略图没了」的黑底
        User32.ShowWindow(Handle, User32.SW_HIDE);
        _layer.HideAll();
    }

    public override void Dispose()
    {
        var old = _layer;
        _layer = DesktopLayer.Empty;
        old.Unregister();
        DisposeShared();
        base.Dispose();
    }

    private void DisposeShared()
    {
        foreach (var d in _cache.Values) d.Dispose();
        _cache.Clear();
        _composition?.Dispose();
        _composition = null;
    }

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case User32.WM_MOUSEACTIVATE:
                return User32.MA_NOACTIVATE;
            case User32.WM_ERASEBKGND:
                return 1;
            case User32.WM_PAINT:
                User32.PAINTSTRUCT ps;
                nint hdc = User32.BeginPaint(Handle, &ps);
                nint black = Gdi32.CreateSolidBrush(0);
                User32.FillRect(hdc, &ps.rcPaint, black); // 桌面之间的缝隙是黑色
                Gdi32.DeleteObject(black);
                User32.EndPaint(Handle, &ps);
                return 0;
        }
        return null;
    }
}

/// <summary>一块屏上三个桌面的缩略图（UI 线程整体替换，渲染线程按位置逐帧平移）。</summary>
internal interface IDesktopLayer
{
    /// <summary>渲染线程：位置 p（0 = 当前桌面，±1 = 相邻桌面完全滑入）下摆好三个桌面。</summary>
    void Apply(double position);

    void HideAll();

    void Unregister();
}

/// <summary>两条路线共用的几何：缝隙宽度与每个桌面的横向位移。</summary>
internal static class DesktopStrip
{
    public static double Gap(double dpiScale) => 48 * dpiScale;

    public static double Offset(int slot, double position, RectD area, double dpiScale)
        => DesktopSwitchController.SlideOffset(slot, position, area.Width, Gap(dpiScale));
}

/// <summary>
/// 共享缩略图视觉版：每个桌面一个分支（<see cref="OverlayComposition.CreateBranch"/>），分支里是
/// <see cref="SharedThumbnailLayer"/>（壁纸 + 窗口 + 窗口阴影）。每帧只平移分支、按位置交接活动阴影，三个桌面一次 Commit。
/// </summary>
internal sealed class SharedDesktopLayer : IDesktopLayer
{
    /// <summary>缓存的一个桌面（跨会话复用）。</summary>
    internal sealed class Desktop(OverlayComposition branch) : IDisposable
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

    /// <param name="ActiveIndex">用活动阴影的窗口（下标，-1 = 没有）：当前桌面是原前台窗口，相邻桌面是最上层窗口（切过去后会被激活）。</param>
    private sealed record Slot(int Offset, Desktop Desktop, SharedThumbnailLayer Layer, RectD[] Rects, double[] Opacity, double[] Active, int ActiveIndex);

    private readonly SharedThumbnailBackend _backend;
    private readonly Slot[] _slots;
    private readonly RectD _area;
    private readonly double _dpi;

    private SharedDesktopLayer(SharedThumbnailBackend backend, Slot[] slots, RectD area, double dpi)
    {
        _backend = backend;
        _slots = slots;
        _area = area;
        _dpi = dpi;
    }

    /// <summary>UI 线程：注册（不可见）。失败返回 null（缓存里已经释放掉的不用管，调用方会整体释放）。</summary>
    public static SharedDesktopLayer? Register(SharedThumbnailBackend backend, ref OverlayComposition? composition, nint overlay, MonitorInfo monitor,
        IReadOnlyList<OverviewWindow> windows, IReadOnlyList<(int Slot, Guid Id)> slots, nint foreground, Dictionary<Guid, Desktop> cache, out int hr)
    {
        hr = 0;
        composition ??= OverlayComposition.Create(backend, overlay, out hr);
        if (composition is null) return null;

        // 不再相邻的桌面释放掉
        foreach (var id in cache.Keys.Where(id => !slots.Any(s => s.Id == id)).ToList())
        {
            cache[id].Dispose();
            cache.Remove(id);
        }

        var result = new Slot[slots.Count];
        for (int k = 0; k < slots.Count; k++)
        {
            var (offset, id) = slots[k];
            if (!cache.TryGetValue(id, out var desktop))
            {
                var branch = composition.CreateBranch(out hr);
                if (branch is null) return null;
                cache[id] = desktop = new Desktop(branch);
            }
            var mine = windows.Where(w => w.DesktopId == id).ToList(); // 最上层在前
            var layer = SharedThumbnailLayer.Register(backend, desktop.Branch, overlay, monitor.WorkArea, monitor.DpiScale,
                mine.Select((w, i) => (i, (nint)w.Id)).ToList(), desktop.Layer, out hr);
            desktop.Layer = layer; // 失败时上一层已被释放
            if (layer is null) return null;
            int active = offset == 0 ? mine.FindIndex(w => w.Id == foreground) : mine.Count > 0 ? 0 : -1;
            result[k] = new Slot(offset, desktop, layer, mine.Select(w => w.Source).ToArray(), mine.Select(_ => 1.0).ToArray(),
                new double[mine.Count], active);
        }
        var r = new SharedDesktopLayer(backend, result, monitor.WorkArea, monitor.DpiScale);
        r.HideAll();
        return r;
    }

    public void Apply(double position)
    {
        foreach (var s in _slots)
        {
            s.Desktop.Branch.SetOffset(DesktopStrip.Offset(s.Offset, position, _area, _dpi), 0);
            if (s.ActiveIndex >= 0)
            {
                // 活动阴影随位置从原前台窗口交接给滑入桌面的最上层窗口：落定时与撤层后的真实桌面一致
                double p = Math.Clamp(position, -1, 1);
                s.Active[s.ActiveIndex] = s.Offset == 0 ? 1 - Math.Abs(p) : Math.Clamp(p * s.Offset, 0, 1);
            }
            s.Layer.Apply(s.Rects, s.Opacity, s.Active, 1, commit: false);
        }
        _backend.Commit();
    }

    public void HideAll()
    {
        foreach (var s in _slots) s.Layer.HideAll();
    }

    /// <summary>注册归缓存所有（<see cref="DesktopOverlay"/> 负责释放），这里只隐藏。</summary>
    public void Unregister() => HideAll();
}

/// <summary>
/// 经典 DWM 缩略图版（共享缩略图不可用时）：每个桌面一份壁纸（Progman 缩略图）+ 该桌面在本屏的窗口（按 Z 序从下往上注册）。
/// 没有窗口阴影。不可变，UI 线程整体替换。
/// </summary>
internal sealed unsafe class DesktopLayer : IDesktopLayer
{
    private readonly record struct Entry(nint Thumb, int Slot, RectD Dest, User32.RECT Source);

    public static DesktopLayer Empty { get; } = new([], default, 1);

    private readonly Entry[] _entries;
    private readonly RectD _area;
    private readonly double _dpi;

    private DesktopLayer(Entry[] entries, RectD area, double dpi)
    {
        _entries = entries;
        _area = area;
        _dpi = dpi;
    }

    public static DesktopLayer Register(nint overlay, MonitorInfo monitor, IReadOnlyList<OverviewWindow> windows, IReadOnlyList<(int Slot, Guid Id)> slots)
    {
        var area = monitor.WorkArea;
        var entries = new List<Entry>();

        nint progman = User32.FindWindowW("Progman", null);
        User32.RECT pr = default;
        if (progman != 0) User32.GetWindowRect(progman, &pr);
        foreach (var (slot, _) in slots)
        {
            nint thumb;
            if (progman == 0 || Dwmapi.DwmRegisterThumbnail(overlay, progman, &thumb) != 0) continue;
            var source = new User32.RECT
            {
                Left = (int)area.Left - pr.Left, Top = (int)area.Top - pr.Top,
                Right = (int)area.Right - pr.Left, Bottom = (int)area.Bottom - pr.Top,
            };
            entries.Add(new Entry(thumb, slot, area, source));
        }

        // 窗口按 Z 序（列表最上层在前）从下往上注册，后注册的画在上面
        for (int i = windows.Count - 1; i >= 0; i--)
        {
            var w = windows[i];
            int slot = int.MinValue;
            foreach (var s in slots)
                if (w.DesktopId == s.Id) slot = s.Slot;
            if (slot == int.MinValue) continue;
            nint thumb;
            if (Dwmapi.DwmRegisterThumbnail(overlay, (nint)w.Id, &thumb) != 0) continue;
            entries.Add(new Entry(thumb, slot, w.Source, ThumbnailLayer.ThumbnailSource((nint)w.Id, thumb).Source));
        }
        var layer = new DesktopLayer(entries.ToArray(), area, monitor.DpiScale);
        layer.HideAll();
        return layer;
    }

    public void Apply(double position)
    {
        var props = new Dwmapi.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Dwmapi.DWM_TNP_RECTDESTINATION | Dwmapi.DWM_TNP_RECTSOURCE | Dwmapi.DWM_TNP_VISIBLE,
            fVisible = 1,
        };
        foreach (var e in _entries)
        {
            double dx = DesktopStrip.Offset(e.Slot, position, _area, _dpi);
            var r = e.Dest;
            props.rcDestination = new User32.RECT
            {
                Left = (int)Math.Round(r.Left + dx - _area.Left), Top = (int)Math.Round(r.Top - _area.Top),
                Right = (int)Math.Round(r.Right + dx - _area.Left), Bottom = (int)Math.Round(r.Bottom - _area.Top),
            };
            props.rcSource = e.Source;
            Dwmapi.DwmUpdateThumbnailProperties(e.Thumb, &props);
        }
    }

    public void HideAll()
    {
        var props = new Dwmapi.DWM_THUMBNAIL_PROPERTIES { dwFlags = Dwmapi.DWM_TNP_VISIBLE, fVisible = 0 };
        foreach (var e in _entries) Dwmapi.DwmUpdateThumbnailProperties(e.Thumb, &props);
    }

    public void Unregister()
    {
        foreach (var e in _entries) Dwmapi.DwmUnregisterThumbnail(e.Thumb);
    }
}
