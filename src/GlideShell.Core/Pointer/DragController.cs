using GlideShell.Core.Configuration;
using GlideShell.Core.Gestures;

namespace GlideShell.Core.Pointer;

/// <summary>三指拖移的宿主：按下 / 移动 / 松开鼠标左键（输入线程上同步调用，必须很快）。</summary>
public interface IDragHost
{
    void Press();

    /// <summary>指针相对移动（物理像素，已含加速）。</summary>
    void MoveBy(double dxPx, double dyPx);

    void Release();
}

/// <summary>
/// 三指拖移（macOS 同款）：三指滑动超过阈值时按下左键，之后指针跟随手指移动，抬指松开。
/// 指针增益 = 基础增益 × (1 + 加速 × 速度系数)：慢速精确定位，快速大范围拖动。
/// 识别器从越过阈值那一刻起算位移，所以按下前的那几毫米不会让窗口「跳」一下。
/// </summary>
public sealed class DragController(IDragHost host, Func<DragConfig> config) : IGestureTarget
{
    private bool _dragging;
    private double _lastDx, _lastDy;
    private double _residualX, _residualY;

    public bool IsDragging => _dragging;

    public void Prepare(long hostUs)
    {
    }

    public void Unprepare(long hostUs)
    {
    }

    public void Begin(in GestureEvent e)
    {
        _dragging = true;
        _lastDx = _lastDy = 0;
        _residualX = _residualY = 0;
        host.Press();
    }

    public void Update(in GestureEvent e)
    {
        if (!_dragging) return;
        double dxMm = e.DxMm - _lastDx, dyMm = e.DyMm - _lastDy;
        _lastDx = e.DxMm;
        _lastDy = e.DyMm;
        double gain = Gain(config(), Math.Sqrt(e.VxMmS * e.VxMmS + e.VyMmS * e.VyMmS));
        // 小数部分累积到下一帧，慢速拖动时不会因为取整而「卡格子」
        double px = dxMm * gain + _residualX, py = dyMm * gain + _residualY;
        double ix = Math.Truncate(px), iy = Math.Truncate(py);
        _residualX = px - ix;
        _residualY = py - iy;
        if (ix != 0 || iy != 0) host.MoveBy(ix, iy);
    }

    public void End(in GestureEvent e)
    {
        if (!_dragging) return;
        if (e.Phase == GesturePhase.Ended) Update(e); // 抬指前最后一点位移
        _dragging = false;
        host.Release();
    }

    /// <summary>每毫米手指位移对应的像素数。</summary>
    public static double Gain(DragConfig cfg, double speedMmS)
        => cfg.PixelsPerMm * (1 + cfg.Acceleration * Math.Clamp(speedMmS / 100, 0, 4));
}
