using GlideShell.Core.Configuration;
using GlideShell.Core.Gestures;
using GlideShell.Core.Overview;

namespace GlideShell.Tests.Overview;

public class OverviewBrowseTests
{
    private sealed class Host : IOverviewSelectionHost
    {
        public readonly List<int> Moves = [];
        public void MoveSelection(int steps) => Moves.Add(steps);
    }

    private static GestureEvent Swipe(GesturePhase phase, double dx)
        => new(GestureKind.Swipe, phase, 3, GestureAxis.Horizontal, dx >= 0 ? GestureDirection.Right : GestureDirection.Left,
            dx, 0, 0, 0, 0, 0);

    [Fact]
    public void Horizontal_swipe_steps_the_selection_and_steps_back_when_reversed()
    {
        var host = new Host();
        var browse = new OverviewBrowse(host, () => new SwitcherConfig { MmPerWindow = 20 });

        browse.Begin(Swipe(GesturePhase.Began, 2)); // 越过阈值就先走一格
        browse.Update(Swipe(GesturePhase.Changed, 15));
        browse.Update(Swipe(GesturePhase.Changed, 21)); // 第二格
        browse.Update(Swipe(GesturePhase.Changed, 45)); // 第三格
        browse.Update(Swipe(GesturePhase.Changed, 10)); // 往回滑：退两格
        browse.End(Swipe(GesturePhase.Ended, 10));

        Assert.Equal(new[] { 1, 1, 1, -2 }, host.Moves);
    }

    [Fact]
    public void Leftward_swipe_moves_backwards()
    {
        var host = new Host();
        var browse = new OverviewBrowse(host, () => new SwitcherConfig { MmPerWindow = 20 });

        browse.Begin(Swipe(GesturePhase.Began, -3));
        browse.Update(Swipe(GesturePhase.Changed, -25));

        Assert.Equal(new[] { -1, -1 }, host.Moves);
    }
}
