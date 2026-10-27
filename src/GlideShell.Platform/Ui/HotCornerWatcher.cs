using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Pointer;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;

namespace GlideShell.Platform.Ui;

/// <summary>
/// 触发角：在 UI 线程上每 40ms 读一次指针位置交给 <see cref="HotCornerDetector"/>（比全局鼠标钩子省事，也不会拖慢系统的鼠标输入）。
/// 没有设置任何角时不轮询。显示器布局每 2 秒重读一次。
/// </summary>
public sealed unsafe class HotCornerWatcher : IDisposable
{
    private const int IntervalMs = 40;
    private const uint VkLButton = 0x01, VkRButton = 0x02, VkMButton = 0x04;

    private readonly UiThread _ui;
    private readonly Func<HotCornersConfig> _config;
    private readonly Action<HotCornerAction> _fire;
    private readonly HotCornerDetector _detector = new();
    private RectD[] _screens = [];
    private long _screensReadMs;
    private bool _running;
    private bool _disposed;

    /// <param name="fire">触发时在 UI 线程上调用（宿主在这里判断暂停、全屏等，再执行动作）。</param>
    public HotCornerWatcher(UiThread ui, Func<HotCornersConfig> config, Action<HotCornerAction> fire)
    {
        _ui = ui;
        _config = config;
        _fire = fire;
        Refresh();
    }

    /// <summary>配置变了（UI 线程）：有角要盯就开始轮询，否则停下。</summary>
    public void Refresh()
    {
        if (_disposed || _running || !_config().Any) return;
        _running = true;
        _ui.PostDelayed(IntervalMs, Tick);
    }

    private void Tick()
    {
        var cfg = _config();
        if (_disposed || !cfg.Any)
        {
            _running = false;
            return;
        }
        long nowMs = Environment.TickCount64;
        if (nowMs - _screensReadMs > 2000)
        {
            _screens = Monitors.Enumerate().Select(m => m.Bounds).ToArray();
            _screensReadMs = nowMs;
        }
        User32.POINT p;
        if (User32.GetCursorPos(&p))
        {
            bool buttons = Pressed(VkLButton) || Pressed(VkRButton) || Pressed(VkMButton);
            var action = _detector.Update(p.X, p.Y, buttons, nowMs * 1000, _screens, cfg);
            if (action != HotCornerAction.None) _fire(action);
        }
        _ui.PostDelayed(IntervalMs, Tick);
    }

    private static bool Pressed(uint vk) => User32.GetAsyncKeyState((int)vk) < 0;

    public void Dispose() => _disposed = true;
}
