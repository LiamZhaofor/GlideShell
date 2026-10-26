using GlideShell.Platform.Diagnostics;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 总览与切桌面共用的共享缩略图后端（一个 DComp 设备 + 一个 WARP 设备，各建一套要多占内存）。
/// 第一次用到时才探测（dwmapi#147 + DComp 设备）；探测或任一处注册失败后，本次运行都改用经典缩略图。
/// 只在 UI 线程上调用 <see cref="Get"/> / <see cref="Disable"/>；在两个呈现器之后释放。
/// </summary>
public sealed class SharedThumbnailSource : IDisposable
{
    private readonly Func<bool> _enabled;
    private SharedThumbnailBackend? _backend;
    private bool _unavailable;

    /// <param name="enabled">配置开关（overview.sharedThumbnails），每次取用时读。</param>
    public SharedThumbnailSource(Func<bool> enabled) => _enabled = enabled;

    /// <summary>配置开启且探测通过时返回后端，否则 null（调用方用经典缩略图）。</summary>
    internal SharedThumbnailBackend? Get()
    {
        if (!_enabled() || _unavailable) return null;
        if (_backend is null)
        {
            _backend = SharedThumbnailBackend.TryCreate(out string status);
            Log.Info($"缩略图后端：{status}");
            if (_backend is null) _unavailable = true;
        }
        return _backend;
    }

    /// <summary>某处注册失败：本次运行不再使用共享缩略图（已注册的照常释放）。</summary>
    internal void Disable(int hr, string where)
    {
        if (_unavailable) return;
        _unavailable = true;
        Log.Warn($"{where}：共享缩略图视觉失败 0x{hr:X8}，本次运行改用经典缩略图");
    }

    public void Dispose()
    {
        _backend?.Dispose();
        _backend = null;
    }
}
