using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Motion;
using GlideShell.Core.Overview;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 总览的 Windows 实现（<see cref="IOverviewHost"/>）：每块显示器一个覆盖层，窗口画面全部是 DWM 实时缩略图，
/// 背景是桌面壁纸缩略图（不透明度随进度降低 = 变暗）。
/// 线程：控制器从输入 / 渲染线程回调本类，本类把窗口操作都投递到 UI 线程；渲染线程只更新缩略图属性。
/// </summary>
public sealed class OverviewPresenter : IOverviewHost, IOverviewSelectionHost, IDisposable
{
    private readonly UiThread _ui;
    private readonly Func<OverviewConfig> _config;
    private readonly RenderLoop _render;
    private readonly KeyboardHook _keys;
    private readonly List<OverlayWindow> _overlays = [];
    private volatile OverlayWindow[] _renderOverlays = [];
    private OverviewController? _controller;
    private RectD[] _rects = new RectD[WindowCatalog.MaxWindows];
    private double[] _opacity = new double[WindowCatalog.MaxWindows];
    private bool _prepared;
    private bool _shown;
    private volatile bool _labelsShown;
    private long? _hoveredId;
    private long _sessionStartUs;
    private volatile bool _applyErrorLogged;
    private SharedThumbnailBackend? _shared;
    private bool _sharedUnavailable; // 探测或注册失败过：本次运行不再尝试

    public OverviewPresenter(UiThread ui, Func<OverviewConfig> config)
    {
        _ui = ui;
        _config = config;
        _keys = new KeyboardHook(OnKey);
        _render = new RenderLoop(RenderFrame, stats => Log.Info($"总览渲染：{stats}，刷新周期 {_render?.RefreshPeriodMs:F2}ms"));
    }

    public OverviewController Controller => _controller ?? throw new InvalidOperationException("先调用 Attach");

    public void Attach(OverviewController controller) => _controller = controller;

    /// <summary>GlideShell 自己的窗口（窗口枚举时排除）。</summary>
    public IReadOnlyCollection<nint> OwnWindows => _renderOverlays.Select(o => o.Handle).ToHashSet();

    // ---------- IOverviewHost（输入线程 / 渲染线程调用，全部投递到 UI 线程） ----------

    public void Prewarm() => _ui.Post(PrepareScene);

    public void CancelPrewarm() => _ui.Post(() =>
    {
        if (!_shown) ReleaseScene();
    });

    public void Show() => _ui.Post(() =>
    {
        if (!_prepared) PrepareScene();
        // 先在 UI 线程把第一帧摆好再显示覆盖层：否则显示出来的第一帧是还没定位缩略图的黑底，看起来像闪屏
        ApplyFrame(Controller.ProgressAt(HostClock.NowUs()));
        foreach (var o in _overlays)
            if (o.Layer is not null) o.ShowOverlay();
        _shown = true;
        _applyErrorLogged = false;
        _sessionStartUs = HostClock.NowUs();
        Controller.OnShown(_sessionStartUs);
        _render.Wake();
    });

    public void Activate() => _ui.Post(() =>
    {
        _keys.Install();
        Log.Info($"总览展开（{(HostClock.NowUs() - _sessionStartUs) / 1000.0:F0}ms），{Controller.Scene.Count} 个窗口");
    });

    public void Hide(long? focusWindowId) => _ui.Post(() =>
    {
        _keys.Uninstall();
        // 先在覆盖层还盖着的时候把选中的窗口切到前台（最小化的无动画还原），等 DWM 呈现两帧再撤层：
        // 撤层那一刻屏幕上已经是它在最上面的样子，不会「撤层后窗口才跳到前面 / 播还原动画」
        if (focusWindowId is { } id)
        {
            if (!WindowActivator.Activate((nint)id, instant: true)) Log.Warn($"无法把窗口 0x{id:X} 置为前台");
            Dwmapi.DwmFlush();
            Dwmapi.DwmFlush();
        }
        // 诊断：日志目录旁放了 capture-dismiss 标记文件时，截下撤层前、撤层后、300ms 后三张图，排查撤层瞬间的画面突变
        string? captures = ScreenCapture.EnabledDirectory("capture-dismiss");
        string stamp = DateTime.Now.ToString("HHmmss-fff");
        if (captures is not null) ScreenCapture.Save(Path.Combine(captures, $"{stamp}-1-before.bmp"));
        foreach (var o in _overlays) o.HideOverlay();
        _shown = false;
        ReleaseScene();
        if (captures is not null)
        {
            for (int i = 0; i < 3; i++) Dwmapi.DwmFlush();
            ScreenCapture.Save(Path.Combine(captures, $"{stamp}-2-after.bmp"));
            Thread.Sleep(300);
            ScreenCapture.Save(Path.Combine(captures, $"{stamp}-3-later.bmp"));
            Log.Info($"已截下撤层前后三张图：{Path.Combine(captures, stamp)}-*.bmp");
        }
    });

    /// <summary>总览展开时的三指横滑（任意线程）：按阅读顺序移动选中项。</summary>
    public void MoveSelection(int steps) => _ui.Post(() =>
    {
        if (!_shown || steps == 0) return;
        // 还没有选中项时第一格先选中原来的前台窗口
        if (_hoveredId is null)
        {
            MoveSelection(Selected);
            steps -= Math.Sign(steps);
        }
        for (int i = 0; i < Math.Abs(steps); i++)
            MoveSelection(scene => scene.Cycle(Selected(scene), backwards: steps < 0));
    });

    /// <summary>
    /// 启动后在 UI 线程静默预热一次（不显示）：JIT、覆盖层窗口创建、首次缩略图注册都提前完成，
    /// 第一次手势的预热从约 80ms 降到几毫秒。
    /// </summary>
    public void WarmUp() => _ui.Post(() =>
    {
        if (_shown || _prepared) return;
        PrepareScene();
        ReleaseScene();
    });

    public void Dispose()
    {
        _render.Dispose();
        _keys.Dispose();
        foreach (var o in _overlays)
        {
            o.ReleaseAll();
            o.Dispose();
        }
        _overlays.Clear();
        _shared?.Dispose();
    }

    // ---------- UI 线程 ----------

    private void PrepareScene()
    {
        long t0 = HostClock.NowUs();
        var monitors = Monitors.Enumerate();
        EnsureOverlays(monitors);
        var cfg = _config();
        long t1 = HostClock.NowUs();
        var windows = WindowCatalog.Snapshot(monitors, OwnWindows.ToHashSet());
        long t2 = HostClock.NowUs();
        int primary = Math.Max(0, monitors.FindIndex(m => m.IsPrimary));
        var scene = OverviewScene.Build(windows, monitors.Select(m => new OverviewArea(m.WorkArea, m.DpiScale)).ToList(),
            cfg.PrimaryMonitorOnly, primary);
        if (scene.Count > _rects.Length)
        {
            _rects = new RectD[scene.Count];
            _opacity = new double[scene.Count];
        }

        var shared = SharedBackend(cfg);
        foreach (var o in _overlays)
        {
            var members = Enumerable.Range(0, scene.Count)
                .Where(i => (cfg.PrimaryMonitorOnly ? primary : scene.Windows[i].MonitorIndex) == o.Monitor.Index)
                .ToList();
            int failure = o.Prepare(scene, members, shared);
            if (failure != 0 && !_sharedUnavailable)
            {
                _sharedUnavailable = true;
                Log.Warn($"共享缩略图视觉失败 0x{failure:X8}，本次运行改用经典缩略图");
            }
        }
        Controller.SetScene(scene);
        _prepared = true;
        long t3 = HostClock.NowUs();
        Log.Info($"总览预热：{scene.Count} 个窗口，{monitors.Count} 块屏，耗时 {(t3 - t0) / 1000.0:F1}ms" +
                 $"（显示器 {(t1 - t0) / 1000.0:F1} / 枚举 {(t2 - t1) / 1000.0:F1} / 布局+缩略图 {(t3 - t2) / 1000.0:F1}）");
    }

    /// <summary>配置开启且探测通过时返回共享缩略图后端；第一次用到时才探测（dwmapi#147 + DComp 设备）。</summary>
    private SharedThumbnailBackend? SharedBackend(OverviewConfig cfg)
    {
        if (!cfg.SharedThumbnails || _sharedUnavailable) return null;
        if (_shared is null)
        {
            _shared = SharedThumbnailBackend.TryCreate(out string status);
            Log.Info($"缩略图后端：{status}");
            if (_shared is null) _sharedUnavailable = true;
        }
        return _shared;
    }

    /// <summary>UI 线程：按某个进度把全部缩略图摆好（显示覆盖层之前用；用局部数组，不碰渲染线程的缓冲）。</summary>
    private void ApplyFrame(double progress)
    {
        var scene = Controller.Scene;
        var rects = new RectD[scene.Count];
        var opacity = new double[scene.Count];
        var frame = new OverviewFrame(progress, 0, 0, TransitionPhase.Tracking, true, null);
        OverviewController.WindowRects(scene, frame, rects, opacity);
        double wallpaper = 1 - _config().BackdropDim * Math.Clamp(progress, 0, 1);
        foreach (var o in _overlays) o.Layer.Apply(rects, opacity, wallpaper);
    }

    private void ReleaseScene()
    {
        foreach (var o in _overlays) o.Release();
        Controller.SetScene(OverviewScene.Empty);
        _prepared = false;
        _hoveredId = null;
        Controller.SetHovered(null);
    }

    // 显示器布局变了（接 / 拔显示器、改分辨率或缩放）就重建覆盖层
    private void EnsureOverlays(List<MonitorInfo> monitors)
    {
        bool same = _overlays.Count == monitors.Count &&
                    _overlays.Zip(monitors).All(p => p.First.Monitor.WorkArea == p.Second.WorkArea && p.First.Monitor.DpiScale == p.Second.DpiScale);
        if (same) return;
        foreach (var o in _overlays)
        {
            o.ReleaseAll();
            o.Dispose();
        }
        _overlays.Clear();
        foreach (var m in monitors) _overlays.Add(new OverlayWindow(m, OnMouseMove, OnClick));
        _renderOverlays = _overlays.ToArray();
    }

    private void OnMouseMove(OverlayWindow overlay, double x, double y)
    {
        var scene = Controller.Scene;
        int index = scene.HitTest(x, y, Controller.ProgressAt(HostClock.NowUs()));
        long? id = index >= 0 ? scene.Windows[index].Id : null;
        if (id == _hoveredId) return;
        Select(index);
    }

    private void OnClick(OverlayWindow overlay, double x, double y)
    {
        var scene = Controller.Scene;
        int index = scene.HitTest(x, y, Controller.ProgressAt(HostClock.NowUs()));
        Controller.Close(index >= 0 ? scene.Windows[index].Id : null, HostClock.NowUs());
    }

    private bool OnKey(uint vk)
    {
        const uint VkEscape = 0x1B, VkReturn = 0x0D, VkTab = 0x09, VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28;
        switch (vk)
        {
            case VkEscape:
                Controller.Close(null, HostClock.NowUs());
                return true;
            case VkReturn:
                Controller.Close(_hoveredId, HostClock.NowUs());
                return true;
            case VkTab:
                MoveSelection(scene => scene.Cycle(Selected(scene), backwards: User32.GetAsyncKeyState(User32.VK_SHIFT) < 0));
                return true;
            case VkLeft or VkUp or VkRight or VkDown:
                var direction = vk switch
                {
                    VkLeft => NavigationDirection.Left,
                    VkUp => NavigationDirection.Up,
                    VkRight => NavigationDirection.Right,
                    _ => NavigationDirection.Down,
                };
                // 还没有选中项时，第一下先选中当前前台窗口（而不是直接跳走）
                MoveSelection(scene => _hoveredId is null ? Selected(scene) : scene.Navigate(Selected(scene), direction));
                return true;
            default:
                return false;
        }
    }

    /// <summary>键盘选中项：鼠标悬停的窗口；没有时是原来的前台窗口；再没有就是最上层窗口。</summary>
    private int Selected(OverviewScene scene)
    {
        int index = _hoveredId is { } id ? scene.IndexOf(id) : -1;
        if (index < 0)
            for (int i = 0; i < scene.Count && index < 0; i++)
                if (scene.Windows[i].IsForeground) index = i;
        return Math.Max(index, 0);
    }

    /// <summary>键盘移动选中项（与鼠标悬停共用同一个高亮；之后动鼠标会接着按鼠标位置更新）。</summary>
    private void MoveSelection(Func<OverviewScene, int> next)
    {
        var scene = Controller.Scene;
        if (scene.Count == 0) return;
        int index = next(scene);
        if (index < 0 || index >= scene.Count) return;
        Select(index);
    }

    /// <summary>
    /// 选中（悬停 / 键盘 / 三指横滑）某个窗口，-1 = 不选。选中的窗口同时提到最上层：
    /// 平铺时窗口互不重叠、看不出区别，收起飞回原位时它才不会被别的窗口挡住、到最后才「凭空出现」。
    /// </summary>
    private void Select(int index)
    {
        var scene = Controller.Scene;
        _hoveredId = index >= 0 ? scene.Windows[index].Id : null;
        Controller.SetHovered(_hoveredId);
        if (index >= 0)
            foreach (var o in _overlays) o.RaiseWindow(index);
        UpdateDecorations();
    }

    private void UpdateDecorations()
    {
        var scene = Controller.Scene;
        int hovered = _hoveredId is { } id ? scene.IndexOf(id) : -1;
        foreach (var o in _overlays) o.SetDecorations(_labelsShown, hovered);
    }

    // ---------- 渲染线程 ----------

    private bool RenderFrame(long nowUs)
    {
        var controller = Controller;
        var frame = controller.Sample(nowUs);
        var scene = controller.Scene;
        var rects = _rects;
        var opacity = _opacity;
        int count = Math.Min(scene.Count, Math.Min(rects.Length, opacity.Length));
        OverviewController.WindowRects(scene, frame, rects.AsSpan(0, count), opacity.AsSpan(0, count));

        double wallpaper = 1 - _config().BackdropDim * Math.Clamp(frame.Progress, 0, 1);
        foreach (var o in _renderOverlays)
        {
            if (!o.IsShown) continue;
            int hr = o.Layer.Apply(rects.AsSpan(0, count), opacity.AsSpan(0, count), wallpaper);
            if (hr != 0 && !_applyErrorLogged)
            {
                _applyErrorLogged = true; // 每次会话只记一次
                Log.Warn($"更新缩略图失败 0x{hr:X8}（壁纸缩略图：{o.Layer.HasWallpaper}）");
            }
        }

        bool labels = frame.LabelOpacity >= 0.5;
        if (labels != _labelsShown)
        {
            _labelsShown = labels;
            _ui.Post(UpdateDecorations);
        }
        return frame.Visible;
    }
}
