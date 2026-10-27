using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;

namespace GlideShell.Core.Pointer;

/// <summary>
/// 触发角：宿主按固定间隔报告指针位置，指针停在某块屏的某个角上（且那个角外面没有别的屏，指针真的会被挡在那里）
/// 超过 <see cref="HotCornersConfig.DwellMs"/> 就触发一次；离开角落一段距离后才能再次触发。按着鼠标键时不触发（正在拖东西）。
/// </summary>
public sealed class HotCornerDetector
{
    /// <summary>离角落多近算在角上（物理像素）。指针被屏幕边缘挡住时就在那个像素上。</summary>
    public const double Reach = 2;

    /// <summary>离开角落多远才重新可触发。</summary>
    public const double Rearm = 24;

    private int _corner = -1; // 正停在哪个角（0 左上、1 右上、2 左下、3 右下）
    private int _monitor = -1;
    private long _enteredUs;
    private bool _fired;
    private bool _armed = true;
    private (double X, double Y) _firedAt;

    /// <summary>
    /// 一次采样。返回这次要触发的动作（没有为 <see cref="HotCornerAction.None"/>）。
    /// <paramref name="screens"/> 是各块屏的整块范围（含任务栏）。
    /// </summary>
    public HotCornerAction Update(double x, double y, bool buttonsDown, long nowUs, IReadOnlyList<RectD> screens, HotCornersConfig config)
    {
        if (!_armed)
        {
            if (Math.Abs(x - _firedAt.X) > Rearm || Math.Abs(y - _firedAt.Y) > Rearm) _armed = true;
            else return HotCornerAction.None;
        }
        var (monitor, corner) = Find(x, y, screens);
        if (corner < 0 || buttonsDown)
        {
            _corner = -1;
            return HotCornerAction.None;
        }
        if (corner != _corner || monitor != _monitor)
        {
            _corner = corner;
            _monitor = monitor;
            _enteredUs = nowUs;
            _fired = false;
        }
        var action = Action(corner, config);
        if (_fired || action == HotCornerAction.None || nowUs - _enteredUs < config.DwellMs * 1000) return HotCornerAction.None;
        _fired = true;
        _armed = false;
        _firedAt = (x, y);
        return action;
    }

    public static HotCornerAction Action(int corner, HotCornersConfig c) => corner switch
    {
        0 => c.TopLeft,
        1 => c.TopRight,
        2 => c.BottomLeft,
        3 => c.BottomRight,
        _ => HotCornerAction.None,
    };

    /// <summary>指针所在的「真」角：某块屏的角，而且沿对角线往外一点不在任何屏上（两块屏相接处的角指针会滑过去，不算）。</summary>
    public static (int Monitor, int Corner) Find(double x, double y, IReadOnlyList<RectD> screens)
    {
        for (int m = 0; m < screens.Count; m++)
        {
            var s = screens[m];
            for (int c = 0; c < 4; c++)
            {
                double cx = c % 2 == 0 ? s.Left : s.Right - 1, cy = c < 2 ? s.Top : s.Bottom - 1;
                if (Math.Abs(x - cx) > Reach || Math.Abs(y - cy) > Reach) continue;
                double ox = cx + (c % 2 == 0 ? -4 : 4), oy = cy + (c < 2 ? -4 : 4);
                bool open = true;
                foreach (var other in screens)
                    if (other.Contains(ox, oy) || other.Contains(ox, cy) || other.Contains(cx, oy)) open = false;
                if (open) return (m, c);
            }
        }
        return (-1, -1);
    }
}
