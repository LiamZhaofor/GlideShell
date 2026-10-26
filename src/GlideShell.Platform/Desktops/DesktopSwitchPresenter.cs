using GlideShell.Core.Desktops;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;
using GlideShell.Platform.Overview;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;
using GlideShell.Platform.VirtualDesktops;

namespace GlideShell.Platform.Desktops;

/// <summary>
/// 四指切桌面的 Windows 实现（<see cref="IDesktopSwitchHost"/>，S3 推荐路线）：
/// 每块屏一个置顶工具窗口覆盖层（工具窗口不属于任何虚拟桌面，切换全程可见），
/// 把左 / 中 / 右三个桌面各自的壁纸与窗口（DWM 缩略图，其他桌面上的窗口同样是实时的）拼成一条，随位置平移；
/// 松手决定切换后立刻在覆盖层下面无动画切换真实桌面；动画落定、收到切换通知、再多等 3 帧后撤层
/// （盖住 explorer 逐个解除 cloak 的中间帧），最后把焦点交给新桌面最上层的窗口。
/// </summary>
public sealed class DesktopSwitchPresenter : IDesktopSwitchHost, IDisposable
{
    private const int HideWaitTimeoutMs = 400;

    private readonly UiThread _ui;
    private readonly VirtualDesktopService _desktops;
    private readonly RenderLoop _render;
    private readonly List<DesktopOverlay> _overlays = [];
    private volatile DesktopOverlay[] _renderOverlays = [];
    private DesktopSwitchController? _controller;
    private IReadOnlyList<OverviewWindow> _windows = [];
    private DesktopState _sessionState = DesktopState.Empty;
    private bool _prepared;
    private volatile bool _shown;
    private Guid? _committedTarget;

    public DesktopSwitchPresenter(UiThread ui, VirtualDesktopService desktops)
    {
        _ui = ui;
        _desktops = desktops;
        _render = new RenderLoop(RenderFrame, stats => Log.Info($"切桌面渲染：{stats}，刷新周期 {_render?.RefreshPeriodMs:F2}ms"));
    }

    public DesktopSwitchController Controller => _controller ?? throw new InvalidOperationException("先调用 Attach");

    public void Attach(DesktopSwitchController controller) => _controller = controller;

    private IReadOnlyCollection<nint> OwnWindows => _overlays.Select(o => o.Handle).ToHashSet();

    // ---------- IDesktopSwitchHost ----------

    public void Prewarm() => _ui.Post(Prepare);

    public void CancelPrewarm() => _ui.Post(() =>
    {
        if (!_shown) Release();
    });

    public void Show() => _ui.Post(() =>
    {
        if (!_prepared) Prepare();
        // 先把第一帧摆好再显示：否则显示出来的第一帧是还没定位缩略图的黑底，看起来就是一次闪屏
        double position = Controller.PositionAt(HostClock.NowUs());
        foreach (var o in _overlays) o.Layer.Apply(position);
        foreach (var o in _overlays) o.ShowOverlay();
        _shown = true;
        _render.Wake();
    });

    public void Commit(int offset)
    {
        // 输入线程上调用：桌面切换在虚拟桌面服务的工作线程上执行（5–13ms），不阻塞输入
        var target = _sessionState.Neighbor(offset);
        if (target is not { } id) return;
        _committedTarget = id;
        _ = _desktops.SwitchToAsync(id).ContinueWith(t =>
        {
            if (t.Result < 0) Log.Warn($"切换桌面失败 0x{t.Result:X8}");
        }, TaskScheduler.Default);
    }

    public void Hide(int committedOffset) => _ui.Post(() =>
    {
        var target = committedOffset != 0 ? _committedTarget : null;
        _committedTarget = null;
        if (target is { } id) WaitForSwitchThenHide(id, _sessionState.Current, Environment.TickCount64, -1);
        else HideNow();
    });

    public void Dispose()
    {
        _render.Dispose();
        foreach (var o in _overlays)
        {
            o.ReleaseLayer();
            o.Dispose();
        }
        _overlays.Clear();
    }

    // ---------- UI 线程 ----------

    private void Prepare()
    {
        long t0 = HostClock.NowUs();
        var state = _desktops.State;
        if (state.Current is not { } current) return;
        var monitors = Monitors.Enumerate();
        EnsureOverlays(monitors);
        var windows = WindowCatalog.Snapshot(monitors, OwnWindows.ToHashSet(), allDesktops: true)
            .Where(w => !w.IsMinimized) // 最小化窗口不在桌面画面里
            .ToList();
        Guid? left = state.Neighbor(-1), right = state.Neighbor(1);
        foreach (var o in _overlays)
        {
            var mine = windows.Where(w => w.MonitorIndex == o.Monitor.Index).ToList();
            o.Prepare(mine, current, left, right);
        }
        _windows = windows;
        _sessionState = state;
        Controller.SetNeighbors(left is not null, right is not null, HostClock.NowUs());
        _prepared = true;
        Log.Info($"切桌面预热：桌面 {state.CurrentIndex + 1}/{state.Count}，" +
                 $"本桌面 {windows.Count(w => w.DesktopId == current)} 个窗口，相邻桌面 {windows.Count(w => w.DesktopId == left || w.DesktopId == right)} 个，" +
                 $"耗时 {(HostClock.NowUs() - t0) / 1000.0:F1}ms");
    }

    private void Release()
    {
        foreach (var o in _overlays) o.ReleaseLayer();
        _prepared = false;
    }

    /// <summary>
    /// 撤层前要等真实桌面完全切好：先等切换通知，再等 explorer 把窗口的外壳 cloak 换过来——
    /// 新桌面的窗口都解除了 cloak、旧桌面的窗口都 cloak 上了。只等通知的话，有窗口的桌面（真机反馈：桌面 1 ↔ 2 闪、
    /// 只有壁纸的 2 ↔ 3 不闪）会在撤层后才看到旧窗口消失 / 新窗口出现，就是一次闪烁。
    /// 然后在覆盖层还盖着时把焦点交出去（标题栏的激活变化也藏在下面），等两帧再撤层。
    /// </summary>
    private void WaitForSwitchThenHide(Guid target, Guid? previous, long startedMs, long switchedMs)
    {
        long now = Environment.TickCount64;
        bool switched = _desktops.State.Current == target;
        if (switched && switchedMs < 0) switchedMs = now;
        bool settled = switched && CloakSettled(target, previous);
        if (!settled && now - startedMs < HideWaitTimeoutMs)
        {
            _ui.PostDelayed(5, () => WaitForSwitchThenHide(target, previous, startedMs, switchedMs));
            return;
        }
        if (!switched) Log.Warn("等待桌面切换通知超时，直接撤层");
        else if (!settled) Log.Warn("等待窗口 cloak 状态切换超时，直接撤层");
        Log.Info($"切桌面撤层：切换通知 {(switchedMs < 0 ? -1 : switchedMs - startedMs)}ms，窗口到位 {now - startedMs}ms");

        // 焦点交给新桌面 Z 序最上面的窗口；空桌面交给桌面本身，避免键盘输入打进旧桌面上看不见的窗口（S3）
        var top = _windows.FirstOrDefault(w => w.DesktopId == target);
        nint hwnd = top is not null ? (nint)top.Id : User32.FindWindowW("Progman", null);
        if (!WindowActivator.Activate(hwnd)) Log.Warn($"切桌面后未能把焦点交给 {(top?.Title ?? "桌面")}");
        Dwmapi.DwmFlush();
        Dwmapi.DwmFlush();
        HideNow();
    }

    /// <summary>新桌面的窗口都没有外壳 cloak、旧桌面的窗口都有（只看外壳那一位，应用自己的 cloak 不算）。</summary>
    private unsafe bool CloakSettled(Guid target, Guid? previous)
    {
        const int DWM_CLOAKED_SHELL = 2;
        foreach (var w in _windows)
        {
            if (w.DesktopId != target && w.DesktopId != previous) continue;
            int cloaked = 0;
            if (Dwmapi.DwmGetWindowAttribute((nint)w.Id, Dwmapi.DWMWA_CLOAKED, &cloaked, sizeof(int)) != 0) continue; // 窗口已关闭
            bool shellCloaked = (cloaked & DWM_CLOAKED_SHELL) != 0;
            if (w.DesktopId == target ? shellCloaked : !shellCloaked) return false;
        }
        return true;
    }

    private void HideNow()
    {
        _shown = false;
        foreach (var o in _overlays) o.HideOverlay(); // 不淡出：淡出期间画面停在最后一帧，用户感觉是「卡一下」
        Release();
    }

    private void EnsureOverlays(List<MonitorInfo> monitors)
    {
        bool same = _overlays.Count == monitors.Count &&
                    _overlays.Zip(monitors).All(p => p.First.Monitor.WorkArea == p.Second.WorkArea && p.First.Monitor.DpiScale == p.Second.DpiScale);
        if (same) return;
        foreach (var o in _overlays)
        {
            o.ReleaseLayer();
            o.Dispose();
        }
        _overlays.Clear();
        foreach (var m in monitors) _overlays.Add(new DesktopOverlay(m));
        _renderOverlays = _overlays.ToArray();
    }

    // ---------- 渲染线程 ----------

    private bool RenderFrame(long nowUs)
    {
        var frame = Controller.Sample(nowUs);
        if (_shown)
            foreach (var o in _renderOverlays) o.Layer.Apply(frame.Position);
        return frame.Visible;
    }
}

/// <summary>一块屏的切桌面覆盖层：置顶工具窗口（不属于任何虚拟桌面），黑底，不激活、鼠标穿透。</summary>
internal sealed unsafe class DesktopOverlay : NativeWindow
{
    private volatile DesktopLayer _layer;

    public DesktopOverlay(MonitorInfo monitor)
    {
        Monitor = monitor;
        _layer = DesktopLayer.Empty;
        var wa = monitor.WorkArea;
        Create("GlideShell.Desktops", User32.WS_POPUP,
            User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW_U | User32.WS_EX_NOACTIVATE_U | 0x20 /* WS_EX_TRANSPARENT */,
            (int)wa.Left, (int)wa.Top, (int)wa.Width, (int)wa.Height);
    }

    public MonitorInfo Monitor { get; }

    public DesktopLayer Layer => _layer;

    public void Prepare(IReadOnlyList<OverviewWindow> windows, Guid current, Guid? left, Guid? right)
    {
        var old = _layer;
        _layer = DesktopLayer.Register(Handle, Monitor, windows, current, left, right);
        old.Unregister();
    }

    public void ReleaseLayer()
    {
        var old = _layer;
        _layer = DesktopLayer.Empty;
        old.Unregister();
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

/// <summary>
/// 一块屏上三个桌面的缩略图（不可变，UI 线程整体替换，渲染线程按位置逐帧平移）。
/// 每个桌面一份壁纸（Progman 缩略图，所有桌面共用全局壁纸）+ 该桌面在本屏的窗口（按 Z 序从下往上注册）。
/// </summary>
internal sealed unsafe class DesktopLayer
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

    public static DesktopLayer Register(nint overlay, MonitorInfo monitor, IReadOnlyList<OverviewWindow> windows, Guid current, Guid? left, Guid? right)
    {
        var area = monitor.WorkArea;
        var entries = new List<Entry>();
        var slots = new List<(int Slot, Guid Id)> { (0, current) };
        if (left is { } l) slots.Add((-1, l));
        if (right is { } r) slots.Add((1, r));

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
            int slot = w.DesktopId == current ? 0 : w.DesktopId == left ? -1 : w.DesktopId == right ? 1 : int.MinValue;
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
        double gap = 48 * _dpi;
        var props = new Dwmapi.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Dwmapi.DWM_TNP_RECTDESTINATION | Dwmapi.DWM_TNP_RECTSOURCE | Dwmapi.DWM_TNP_VISIBLE,
            fVisible = 1,
        };
        foreach (var e in _entries)
        {
            double dx = DesktopSwitchController.SlideOffset(e.Slot, position, _area.Width, gap);
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
