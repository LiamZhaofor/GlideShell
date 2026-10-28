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
    private DecorResources? _decor;
    private bool _decorTried;

    /// <param name="enabled">配置开关（overview.sharedThumbnails），每次取用时读。</param>
    public SharedThumbnailSource(Func<bool> enabled) => _enabled = enabled;

    /// <summary>探测结果（还没用到过为 null），诊断用。</summary>
    public string? Status { get; private set; }

    /// <summary>合成层装饰的探测结果（还没用到过为 null）。</summary>
    public string? DecorStatus { get; private set; }

    /// <summary>本次运行已改用经典缩略图（探测失败或某处注册失败）。</summary>
    public bool Unavailable => _unavailable;

    /// <summary>配置开启且探测通过时返回后端，否则 null（调用方用经典缩略图）。</summary>
    internal SharedThumbnailBackend? Get()
    {
        if (!_enabled() || _unavailable) return null;
        if (_backend is null)
        {
            _backend = SharedThumbnailBackend.TryCreate(out string status);
            Status = status;
            Log.Info($"缩略图后端：{status}");
            if (_backend is null) _unavailable = true;
        }
        return _backend;
    }

    /// <summary>
    /// 合成层里画文字与装饰用的资源（总览与切换条共用）：共享缩略图可用时第一次取用才创建；不可用时为 null（调用方退回 GDI）。
    /// </summary>
    internal DecorResources? Decor(Ui.UiThread ui)
    {
        var backend = Get();
        if (backend is null) return null;
        if (!_decorTried)
        {
            _decorTried = true;
            _decor = DecorResources.TryCreate(backend, ui, out string status);
            DecorStatus = status;
            Log.Info($"总览装饰：{status}");
        }
        return _decor;
    }

    /// <summary>某处注册失败：本次运行不再使用共享缩略图（已注册的照常释放）。</summary>
    internal void Disable(int hr, string where)
    {
        if (_unavailable) return;
        _unavailable = true;
        Status = $"{where}注册失败 0x{hr:X8}，本次运行改用经典缩略图";
        Log.Warn($"{where}：共享缩略图视觉失败 0x{hr:X8}，本次运行改用经典缩略图");
    }

    public void Dispose()
    {
        _decor?.Dispose();
        _decor = null;
        _backend?.Dispose();
        _backend = null;
    }
}
