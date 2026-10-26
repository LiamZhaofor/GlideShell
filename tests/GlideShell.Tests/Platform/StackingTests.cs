using GlideShell.Platform.Overview;

namespace GlideShell.Tests.Platform;

public class StackingTests
{
    [Fact]
    public void Entries_follow_the_live_z_order_bottom_up()
    {
        // 缩略图当前从下往上：A B C；真实 Z 序（最上层在前）已经变成 B A C（B 刚被激活）
        nint[] entries = [10, 20, 30];
        int[] order = Stacking.BottomUp(entries, h => h, [20, 10, 30, 99]);

        Assert.Equal(new[] { 2, 0, 1 }, order); // 从下往上 C A B
    }

    [Fact]
    public void Closed_windows_go_to_the_bottom_in_their_old_order()
    {
        nint[] entries = [10, 20, 30, 40];
        int[] order = Stacking.BottomUp(entries, h => h, [40, 20]); // 10 与 30 已经关掉

        Assert.Equal(new[] { 0, 2, 1, 3 }, order);
    }
}
