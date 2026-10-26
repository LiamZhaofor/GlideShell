using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 总览的 Windows 实现（<see cref="IOverviewHost"/>）：每块显示器一个覆盖层，窗口画面全部是 DWM 实时缩略图，
/// 背景是桌面壁纸缩略图（不透明度随进度降低 = 变暗）。
/// 线程：控制器从输入 / 渲染线程回调本类，本类把窗口操作都投递到 UI 线程；渲染线程只更新缩略图属性。
/// </summary>
public sealed class OverviewPresenter : IOverviewHost, IDisposable
{
    private readonly UiThread _ui;
    private readonly Func<OverviewConfig> _config;
    private readonly RenderLoop _render;
    private readonly KeyboardHook _keys;
    private readonly List<OverlayWindow> _overlays = [];
    private volatile OverlayWindow[] _renderOverlays = [];
    private OverviewController? _controller;
    private RectD[] _rects = new RectD[WindowCatalog.MaxWindows];
    private bool _prepared;
    private bool _shown;
    private volatile bool _labelsShown;
    private long? _hoveredId;
    private long _sessionStartUs;
    private volatile bool _applyErrorLogged;

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
        foreach (var o in _overlays) o.HideOverlay();
        _shown = false;
        ReleaseScene();
        if (focusWindowId is { } id && !WindowActivator.Activate((nint)id))
            Log.Warn($"无法把窗口 0x{id:X} 置为前台");
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
        if (scene.Count > _rects.Length) _rects = new RectD[scene.Count];

        foreach (var o in _overlays)
        {
            var members = Enumerable.Range(0, scene.Count)
                .Where(i => (cfg.PrimaryMonitorOnly ? primary : scene.Windows[i].MonitorIndex) == o.Monitor.Index)
                .ToList();
            o.Prepare(scene, members);
        }
        Controller.SetScene(scene);
        _prepared = true;
        long t3 = HostClock.NowUs();
        Log.Info($"总览预热：{scene.Count} 个窗口，{monitors.Count} 块屏，耗时 {(t3 - t0) / 1000.0:F1}ms" +
                 $"（显示器 {(t1 - t0) / 1000.0:F1} / 枚举 {(t2 - t1) / 1000.0:F1} / 布局+缩略图 {(t3 - t2) / 1000.0:F1}）");
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
        _hoveredId = id;
        Controller.SetHovered(id);
        UpdateDecorations();
    }

    private void OnClick(OverlayWindow overlay, double x, double y)
    {
        var scene = Controller.Scene;
        int index = scene.HitTest(x, y, Controller.ProgressAt(HostClock.NowUs()));
        Controller.Close(index >= 0 ? scene.Windows[index].Id : null, HostClock.NowUs());
    }

    private bool OnKey(uint vk)
    {
        const uint VkEscape = 0x1B, VkReturn = 0x0D;
        switch (vk)
        {
            case VkEscape:
                Controller.Close(null, HostClock.NowUs());
                return true;
            case VkReturn:
                Controller.Close(_hoveredId, HostClock.NowUs());
                return true;
            default:
                return false;
        }
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
        int count = Math.Min(scene.Count, rects.Length);
        OverviewController.WindowRects(scene, frame, rects.AsSpan(0, count));

        double wallpaper = 1 - _config().BackdropDim * Math.Clamp(frame.Progress, 0, 1);
        foreach (var o in _renderOverlays)
        {
            if (!o.IsShown) continue;
            int hr = o.Layer.Apply(rects.AsSpan(0, count), wallpaper);
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
