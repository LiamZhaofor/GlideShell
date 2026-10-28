using GlideShell.Platform.Overview;

namespace GlideShell.Tests.Platform;

/// <summary>渲染会话的帧统计：2.0 调优按「晚到」帧数比较改动前后（tools/bench/pacing.py）。</summary>
public class FrameStatsTests
{
    [Fact]
    public void Intervals_longer_than_one_and_a_half_periods_count_as_late()
    {
        // 200Hz：5ms 一帧；7.4ms 还算抖动，7.6ms 与 10ms 各少取样了一帧
        var intervals = new List<double> { 5, 5.1, 4.9, 7.4, 7.6, 5, 10, 5 };
        var s = FrameStats.From(intervals, [0.4, 0.5, 0.3, 0.4, 0.6, 0.4, 0.5, 0.4], periodMs: 5, wokeLate: 1, overran: 1);

        Assert.Equal(2, s.Late);
        Assert.Equal(1, s.WokeLate);
        Assert.Equal(1, s.Overran);
        Assert.Equal(10, s.MaxMs);
        Assert.Equal(6, s.MaxAt);
        Assert.Contains("晚到 2 帧（醒晚 1 / 超时 1）", s.ToString());
    }

    [Fact]
    public void Without_a_known_period_the_median_interval_is_used()
    {
        var s = FrameStats.From([6, 6.1, 6, 12.2, 6]);
        Assert.Equal(1, s.Late);
    }
}
