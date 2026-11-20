using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Gestures;
using GlideShell.Core.Overview;

namespace GlideShell.Tests.Overview;

public class DesktopBarTests
{
    private static readonly RectD Area = new(0, 0, 2560, 1528);

    [Fact]
    public void Thumbnails_keep_the_screen_aspect_are_centered_and_line_up_with_the_names()
    {
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        Assert.Equal(3, bar.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(Area.Width / Area.Height, bar.Thumbs[i].Width / bar.Thumbs[i].Height, 6);
            Assert.Equal(bar.Thumbs[i].CenterX, bar.Pills[i].CenterX, 6); // 收起时的名字就在缩略图那一列
            Assert.Equal(bar.Thumbs[i].CenterX, bar.Labels[i].CenterX, 6);
            Assert.True(bar.Labels[i].Top >= bar.Thumbs[i].Bottom);
            Assert.True(bar.Labels[i].Bottom <= Area.Top + bar.ExpandedHeight);
        }
        Assert.Equal(Area.CenterX, (bar.Thumbs[0].Left + bar.AddTile.Right) / 2, 6); // 连同末尾的「+」整排居中
        Assert.True(bar.AddTile.Left > bar.Thumbs[2].Right);
        Assert.Equal(bar.Thumbs[0].Height, bar.AddTile.Height, 6);
        Assert.True(bar.ExpandedHeight > bar.CollapsedHeight);
    }

    [Fact]
    public void Many_desktops_shrink_to_fit_the_width()
    {
        var bar = DesktopBarGeometry.Compute(Area, 20, 1.5);
        Assert.True(bar.Thumbs[0].Left >= Area.Left && bar.Thumbs[^1].Right <= Area.Right);
    }

    [Fact]
    public void Plus_and_remove_buttons_are_hit_only_where_they_are_shown()
    {
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        Assert.Equal(DesktopBarPart.Add, bar.Hit(bar.AddPill.CenterX, bar.AddPill.CenterY, 0).Part);
        Assert.Equal(DesktopBarPart.Add, bar.Hit(bar.AddTile.CenterX, bar.AddTile.CenterY, 1).Part);

        var x = bar.RemoveButton(1);
        Assert.True(x.Right <= bar.Labels[1].Right && x.Left > bar.Labels[1].CenterX); // 名字右端
        Assert.Equal(new DesktopBarHit(DesktopBarPart.Remove, 1), bar.Hit(x.CenterX, x.CenterY, 1));
        Assert.Equal(new DesktopBarHit(DesktopBarPart.Desktop, 1), bar.Hit(x.CenterX, x.CenterY, 1, removable: false));
        Assert.Equal(new DesktopBarHit(DesktopBarPart.Desktop, 1), bar.Hit(bar.Labels[1].Left + 2, bar.Labels[1].CenterY, 1));

        // 只剩一个桌面：不能删
        var single = DesktopBarGeometry.Compute(Area, 1, 1.5);
        var only = single.RemoveButton(0);
        Assert.Equal(DesktopBarPart.Desktop, single.Hit(only.CenterX, only.CenterY, 1).Part);
    }

    [Fact]
    public void Content_area_shrinks_from_the_top_keeping_the_bottom_edge_when_expanded()
    {
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        var tile = new RectD(400, 600, 800, 500);
        Assert.Equal(tile, bar.MapContent(tile, 0));

        var a0 = bar.ContentArea;
        var whole = bar.MapContent(a0, 1);
        Assert.Equal(bar.ExpandedContentArea.Top, whole.Top, 6);
        Assert.Equal(a0.Bottom, whole.Bottom, 6);
        Assert.Equal(a0.CenterX, whole.CenterX, 6);
        var moved = bar.MapContent(tile, 1);
        Assert.True(moved.Top > tile.Top && moved.Width < tile.Width);
    }

    [Fact]
    public void Zoom_grows_the_chosen_thumbnail_into_the_whole_screen()
    {
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        Assert.Equal(bar.Thumbs[1], bar.ZoomRect(1, 1, 0));
        Assert.Equal(Area, bar.ZoomRect(1, 1, 1));
    }

    [Fact]
    public void Hit_test_uses_names_when_collapsed_and_thumbnails_when_expanded()
    {
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        var pill = bar.Pills[2];
        var thumb = bar.Thumbs[2];
        Assert.Equal(2, bar.HitTest(pill.CenterX, pill.CenterY, 0));
        Assert.Equal(2, bar.HitTest(thumb.CenterX, thumb.CenterY, 1));
        Assert.Equal(-1, bar.HitTest(Area.CenterX, Area.Bottom - 10, 1));
        Assert.True(bar.Contains(pill.CenterX, pill.CenterY, 0));
        Assert.False(bar.Contains(thumb.CenterX, thumb.Bottom + 1, 0)); // 收起时只有桌面名那一条
        Assert.True(bar.Contains(thumb.CenterX, thumb.Bottom + 1, 1));
    }

    // ---------- 控制器：悬停展开、点击放大进入 ----------

    private sealed class FakeHost : IOverviewHost
    {
        public readonly List<string> Calls = [];
        public void Prewarm() { }
        public void CancelPrewarm() { }
        public void Show() { }
        public void Activate() => Calls.Add("Activate");
        public void Closing(long? focusWindowId) => Calls.Add($"Closing:{focusWindowId?.ToString() ?? "-"}");
        public void Hide(long? focusWindowId) => Calls.Add($"Hide:{focusWindowId?.ToString() ?? "-"}");
        public void SwitchDesktop(int desktopIndex) => Calls.Add($"SwitchDesktop:{desktopIndex}");
        public void PageChanged(int desktopIndex) => Calls.Add($"PageChanged:{desktopIndex}");
        public void DesktopReorderSettled() => Calls.Add("DesktopReorderSettled");
    }

    private static (OverviewController C, FakeHost Host, long T) Opened()
    {
        var host = new FakeHost();
        var c = new OverviewController(host, () => new OverviewConfig());
        c.SetDesktops(new DesktopBarState(["桌面 1", "桌面 2", "桌面 3"], 0));
        c.Open(0);
        c.OnShown(0);
        long t = 2_000_000;
        Assert.True(c.Sample(t).Visible);
        return (c, host, t);
    }

    [Fact]
    public void Hovering_the_bar_expands_it_and_leaving_collapses_it()
    {
        var (c, _, t) = Opened();
        c.SetBarHovered(true, t);
        Assert.Equal(1, c.Sample(t + 2_000_000).BarExpansion, 3);
        c.SetBarHovered(false, t + 2_000_000);
        Assert.Equal(0, c.Sample(t + 4_000_000).BarExpansion, 3);
    }

    [Fact]
    public void Bar_does_not_expand_while_the_overview_is_closing()
    {
        var (c, _, t) = Opened();
        c.Close(null, t);
        c.SetBarHovered(true, t + 1000);
        Assert.Equal(0, c.Sample(t + 50_000).BarExpansion, 6);
    }

    [Fact]
    public void Clicking_another_desktop_switches_at_once_zooms_in_then_hides()
    {
        var (c, host, t) = Opened();
        c.SetBarHovered(true, t);
        c.Sample(t + 2_000_000);
        c.GoToDesktop(2, t + 2_000_000);
        Assert.Equal("SwitchDesktop:2", host.Calls[^1]); // 真实桌面立刻切换，不等动画

        var mid = c.Sample(t + 2_100_000);
        Assert.Equal(2, mid.ZoomDesktop);
        Assert.InRange(mid.Zoom, 0.01, 0.99);
        Assert.True(mid.Visible);

        // 动画中手势与 Esc（Close）都不接管
        c.Close(null, t + 2_100_000);
        c.Begin(new GestureEvent(GestureKind.Swipe, GesturePhase.Began, 3, GestureAxis.Vertical, GestureDirection.Down, 0, 0, 0, 0, t + 2_100_000, 0));
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("Closing", StringComparison.Ordinal));

        var end = c.Sample(t + 4_000_000);
        Assert.Equal("Hide:-", host.Calls[^1]);
        Assert.True(end.Visible); // 撤层那一帧仍要画（停在全屏）
        Assert.Equal(1, end.Zoom, 3);

        var after = c.Sample(t + 4_010_000);
        Assert.False(after.Visible);
        Assert.Equal(0, after.Progress, 6); // 复位：下次从收起态开始
        Assert.Equal(-1, after.ZoomDesktop);
    }

    private static GestureEvent Swipe4(GesturePhase phase, double dxMm, double vx, long t, GestureDirection direction = GestureDirection.Left)
        => new(GestureKind.Swipe, phase, 4, GestureAxis.Horizontal, direction, dxMm, 0, vx, 0, t, t);

    /// <summary>展开好的总览，三个桌面各一页（每页一个窗口），当前第 1 个桌面。</summary>
    private static (OverviewController C, FakeHost Host, long T, OverviewScene[] Pages) OpenedWithPages()
    {
        var (c, host, t) = Opened();
        var pages = Enumerable.Range(0, 3)
            .Select(d => OverviewScene.Build([new OverviewWindow(d + 1, $"w{d}", new RectD(100, 100, 800, 600), false, d == 0, 0, "p")],
                [new OverviewArea(Area, 1.5)]))
            .ToArray();
        c.SetPages(pages, 0);
        return (c, host, t, pages);
    }

    [Fact]
    public void Four_finger_swipe_in_the_overview_pages_to_the_neighbor_and_stays_in_the_overview()
    {
        var (c, host, t, pages) = OpenedWithPages();
        c.DesktopSwipe.Begin(Swipe4(GesturePhase.Began, 0, 0, t));
        c.DesktopSwipe.Update(Swipe4(GesturePhase.Changed, -27, -200, t + 100_000));
        var mid = c.Sample(t + 110_000);
        Assert.Equal(27 / 54.0, mid.PagePosition, 3); // 1:1 跟手（一页 = 总览行程 × 1.8）
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("PageChanged", StringComparison.Ordinal)); // 手指在板上不切

        c.DesktopSwipe.End(Swipe4(GesturePhase.Ended, -27, -200, t + 120_000));
        Assert.Equal("PageChanged:1", host.Calls[^1]); // 松手就切真实桌面
        Assert.Same(pages[1], c.Scene); // 之后收起、选窗口都按新桌面那一页
        Assert.Equal(1, c.Desktops.Current); // 桌面条高亮跟过去

        var settled = c.Sample(t + 3_000_000);
        Assert.Equal(1, settled.PagePosition, 3);
        Assert.True(settled.Visible); // 留在总览里
        Assert.Equal(1, settled.Progress, 3);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("Hide", StringComparison.Ordinal));

        // 换页后照常收起
        c.Close(null, t + 3_000_000);
        c.Sample(t + 6_000_000);
        Assert.Equal("Hide:-", host.Calls[^1]);
    }

    [Fact]
    public void Short_four_finger_swipe_in_the_overview_springs_back_to_the_same_page()
    {
        var (c, host, t, pages) = OpenedWithPages();
        c.DesktopSwipe.Begin(Swipe4(GesturePhase.Began, 0, 0, t));
        c.DesktopSwipe.Update(Swipe4(GesturePhase.Changed, -8, 0, t + 100_000));
        c.DesktopSwipe.End(Swipe4(GesturePhase.Ended, -8, 0, t + 120_000));
        Assert.Equal(0, c.Sample(t + 3_000_000).PagePosition, 3);
        Assert.Same(pages[0], c.Scene);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("PageChanged", StringComparison.Ordinal));
    }

    [Fact]
    public void Four_finger_swipe_past_the_first_desktop_rubber_bands_back()
    {
        var (c, host, t, _) = OpenedWithPages();
        const GestureDirection right = GestureDirection.Right; // 手指向右 = 左边的桌面，第 1 个桌面左边没有
        c.DesktopSwipe.Begin(Swipe4(GesturePhase.Began, 0, 0, t, right));
        c.DesktopSwipe.Update(Swipe4(GesturePhase.Changed, 40, 0, t + 100_000, right));
        var pulled = c.Sample(t + 110_000);
        Assert.InRange(pulled.PagePosition, -0.2, -0.01); // 橡皮筋：跟一点但有阻尼
        c.DesktopSwipe.End(Swipe4(GesturePhase.Ended, 40, 300, t + 120_000, right));
        Assert.Equal(0, c.Sample(t + 3_000_000).PagePosition, 3);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("PageChanged", StringComparison.Ordinal));
    }

    [Fact]
    public void Four_finger_swipe_is_ignored_without_pages()
    {
        var (c, host, t) = Opened(); // 有桌面条但没有多页（例如经典缩略图模式）
        c.DesktopSwipe.Begin(Swipe4(GesturePhase.Began, 0, 0, t));
        c.DesktopSwipe.Update(Swipe4(GesturePhase.Changed, -40, 0, t + 100_000));
        c.DesktopSwipe.End(Swipe4(GesturePhase.Ended, -40, -300, t + 120_000));
        Assert.Equal(0, c.Sample(t + 200_000).PagePosition, 6);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("PageChanged", StringComparison.Ordinal));
    }

    [Fact]
    public void Clicking_the_current_desktop_just_closes()
    {
        var (c, host, t) = Opened();
        c.GoToDesktop(0, t);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("SwitchDesktop", StringComparison.Ordinal));
        Assert.Equal("Closing:-", host.Calls[^1]);
    }

    // ---------- 拖动桌面排序 ----------

    [Fact]
    public void Reorder_slots_move_the_dragged_desktop_and_shift_the_others()
    {
        // 0 拖到 2：预览为 1、2、0
        Assert.Equal(2, DesktopBarReorder.Slot(0, 0, 2, 3));
        Assert.Equal(0, DesktopBarReorder.Slot(1, 0, 2, 3));
        Assert.Equal(1, DesktopBarReorder.Slot(2, 0, 2, 3));
        // 2 拖到 0：预览为 2、0、1
        Assert.Equal(1, DesktopBarReorder.Slot(0, 2, 0, 3));
        Assert.Equal(2, DesktopBarReorder.Slot(1, 2, 0, 3));
        Assert.Equal(0, DesktopBarReorder.Slot(2, 2, 0, 3));
        // 原样放下或无效下标不改变任何槽位
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(i, DesktopBarReorder.Slot(i, 1, 1, 3));
            Assert.Equal(i, DesktopBarReorder.Slot(i, -1, 2, 3));
        }
    }

    [Fact]
    public void Reorder_target_changes_only_after_crossing_neighbor_centers()
    {
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        Assert.Equal(0, DesktopBarReorder.Target(bar, 0, bar.Thumbs[0].CenterX));
        Assert.Equal(0, DesktopBarReorder.Target(bar, 0, bar.Thumbs[1].CenterX - 1));
        Assert.Equal(1, DesktopBarReorder.Target(bar, 0, bar.Thumbs[1].CenterX + 1));
        Assert.Equal(2, DesktopBarReorder.Target(bar, 0, bar.Thumbs[2].CenterX + 1));
        Assert.Equal(1, DesktopBarReorder.Target(bar, 2, bar.Thumbs[1].CenterX - 1));
        Assert.Equal(0, DesktopBarReorder.Target(bar, 2, bar.Thumbs[0].CenterX - 1));
        Assert.Equal(-1, DesktopBarReorder.Target(bar, 3, 0));
    }

    /// <summary>展开好桌面条的总览（三个桌面），返回落定后的时间。</summary>
    private static (OverviewController C, FakeHost Host, long T, DesktopBarGeometry Bar) OpenedBar()
    {
        var (c, host, t) = Opened();
        c.SetBarHovered(true, t);
        t += 2_000_000;
        c.Sample(t);
        return (c, host, t, DesktopBarGeometry.Compute(Area, 3, 1.5));
    }

    private static RectD ThumbOf(in OverviewFrame frame, DesktopBarGeometry bar, int index)
        => frame.DesktopDrag is { } d ? DesktopBarReorder.Layout(bar, d, index, frame.BarExpansion).Thumb : bar.ThumbRect(index, frame.BarExpansion);

    private static double Distance(RectD a, RectD b) => Math.Max(Math.Abs(a.Left - b.Left), Math.Abs(a.Top - b.Top));

    [Fact]
    public void Fractional_slots_interpolate_between_neighbors_and_clamp_at_the_ends()
    {
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        Assert.Equal((bar.Thumbs[0].CenterX + bar.Thumbs[1].CenterX) / 2, bar.ThumbRectAt(0.5, 1).CenterX, 6);
        Assert.Equal(bar.Thumbs[2], bar.ThumbRectAt(2, 1));
        Assert.Equal(bar.Thumbs[0], bar.ThumbRectAt(-0.4, 1));
        Assert.Equal(bar.Thumbs[2], bar.ThumbRectAt(7, 1));
        Assert.Equal(bar.Thumbs[0], bar.ThumbRectAt(double.NaN, 1));
        Assert.Equal((bar.Labels[1].Left + bar.Labels[2].Left) / 2, bar.LabelAt(1.5).Left, 6);
        Assert.Equal((bar.Pills[1].Left + bar.Pills[2].Left) / 2, bar.PillAt(1.5).Left, 6);

        // 桌面数对不上（列表刚变过）：按原位，不越界
        var drag = new DesktopBarDragFrame(0, 0, 0, 0.5, 0.5, -1, DesktopReorderMotion.Identity(3, 0).Retarget(0, 2, 0, snap: 0), 1_000_000);
        Assert.Equal(2, drag.SlotOf(0, 3), 6);
        Assert.Equal(0, drag.SlotOf(0, 4), 6);
        Assert.Equal(3, drag.SlotOf(3, 4), 6);
    }

    [Fact]
    public void Neighbors_slide_out_of_the_way_instead_of_jumping()
    {
        var (c, _, t, bar) = OpenedBar();
        var thumb = bar.Thumbs[0];
        Assert.True(c.BeginDesktopDrag(0, thumb.CenterX, thumb.CenterY, thumb, t));
        c.DragDesktopTo(bar.Thumbs[2].CenterX + 1, thumb.CenterY + 20, bar, t + 10_000);

        var dragging = c.Sample(t + 20_000).DesktopDrag!.Value;
        Assert.Equal(0, dragging.Source);
        Assert.Equal(bar.Thumbs[2].CenterX + 1, dragging.X);
        Assert.Equal(thumb.CenterY + 20, dragging.Y);
        Assert.Equal(0.5, dragging.GrabX, 6);
        Assert.Equal(-1, dragging.Fly); // 跟着指针
        var follow = DesktopBarReorder.Layout(bar, dragging, 0, 1);
        Assert.Equal(bar.Thumbs[2].CenterX + 1, follow.Thumb.CenterX, 6);
        Assert.Equal(follow.Thumb.Top + (bar.Labels[2].Top - bar.Thumbs[2].Top), follow.Label.Top, 6); // 桌面名紧跟在缩略图下面

        // 让位途中：在新旧槽位之间
        var mid = c.Sample(t + 80_000).DesktopDrag!.Value;
        Assert.InRange(mid.SlotOf(1, 3), 0.05, 0.95);
        Assert.InRange(mid.SlotOf(2, 3), 1.05, 1.95);
        // 落定：到新槽位
        var end = c.Sample(t + 2_000_000).DesktopDrag!.Value;
        Assert.Equal(0, end.SlotOf(1, 3), 6);
        Assert.Equal(1, end.SlotOf(2, 3), 6);
    }

    [Fact]
    public void Successful_drop_flies_into_the_slot_and_finishes_only_after_settling_once()
    {
        var (c, host, t, bar) = OpenedBar();
        var thumb = bar.Thumbs[0];
        Assert.True(c.BeginDesktopDrag(0, thumb.CenterX, thumb.CenterY, thumb, t));
        c.DragDesktopTo(bar.Thumbs[2].CenterX + 1, thumb.CenterY + 20, bar, t + 10_000);
        c.Sample(t + 20_000);

        long drop = t + 30_000;
        var move = c.DropDesktop(drop);
        Assert.Equal(new DesktopBarMove(0, 2), move.GetValueOrDefault());
        Assert.Null(c.DropDesktop(drop)); // 重复松开不能再次调用系统排序
        Assert.False(c.CompleteDesktopMove(move!.Value, success: true, drop + 5_000)); // 系统很快返回，动画还在播

        var flying = c.Sample(drop + 60_000);
        Assert.InRange(flying.DesktopDrag!.Value.Fly, 0.01, 0.99);
        var at = ThumbOf(flying, bar, 0);
        Assert.InRange(at.Top, bar.Thumbs[2].Top + 0.5, bar.Thumbs[2].Top + 19.5); // 从松手处飞向槽位
        Assert.True(c.IsDesktopDragging);
        Assert.DoesNotContain("DesktopReorderSettled", host.Calls);

        var settled = c.Sample(drop + 2_000_000);
        Assert.Equal(bar.Thumbs[2], ThumbOf(settled, bar, 0));
        Assert.Single(host.Calls, x => x == "DesktopReorderSettled");
        Assert.NotNull(settled.DesktopDrag); // 新顺序要等宿主重建桌面条，不能自己结束
        c.Sample(drop + 2_010_000);
        Assert.Single(host.Calls, x => x == "DesktopReorderSettled");

        Assert.True(c.FinishSettledDesktopMove());
        Assert.False(c.FinishSettledDesktopMove());
        Assert.False(c.IsDesktopDragging);
        Assert.Null(c.Sample(drop + 2_020_000).DesktopDrag);
    }

    [Fact]
    public void System_result_after_the_animation_settled_finishes_at_once()
    {
        var (c, host, t, bar) = OpenedBar();
        Assert.True(c.BeginDesktopDrag(0, bar.Thumbs[0].CenterX, bar.Thumbs[0].CenterY, bar.Thumbs[0], t));
        c.DragDesktopTo(bar.Thumbs[1].CenterX + 1, bar.Thumbs[0].CenterY, bar, t + 10_000);
        var move = c.DropDesktop(t + 20_000)!.Value;
        c.Sample(t + 2_000_000); // 等系统结果：不通知宿主
        Assert.DoesNotContain("DesktopReorderSettled", host.Calls);
        Assert.True(c.CompleteDesktopMove(move, success: true, t + 2_000_000));
        Assert.False(c.IsDesktopDragging);
        Assert.False(c.CompleteDesktopMove(move, success: true, t + 2_010_000)); // 过时的结果
        c.Sample(t + 2_020_000);
        Assert.DoesNotContain("DesktopReorderSettled", host.Calls);
    }

    [Theory]
    [InlineData(false)] // Esc / 捕获丢失
    [InlineData(true)] // 放在桌面条外、或系统排序失败
    public void Cancelled_or_failed_drop_flies_back_and_ends_by_itself(bool dropped)
    {
        var (c, host, t, bar) = OpenedBar();
        var thumb = bar.Thumbs[1];
        Assert.True(c.BeginDesktopDrag(1, thumb.CenterX, thumb.CenterY, thumb, t));
        c.DragDesktopTo(bar.Thumbs[0].CenterX - 1, thumb.CenterY + 30, bar, t + 10_000);
        c.Sample(t + 2_000_000); // 桌面 0 已经让到槽位 1
        long end = t + 2_000_000;
        if (dropped) c.CompleteDesktopMove(c.DropDesktop(end)!.Value, success: false, end + 5_000);
        else c.CancelDesktopDrag(end);
        Assert.Null(c.DropDesktop(end));

        var back = c.Sample(end + 60_000);
        Assert.InRange(back.DesktopDrag!.Value.SlotOf(0, 3), 0.05, 0.95); // 弹回途中
        var at = ThumbOf(back, bar, 1);
        Assert.InRange(at.Top, thumb.Top + 0.5, thumb.Top + 29.5); // 飞回途中

        var done = c.Sample(end + 2_000_000);
        Assert.Null(done.DesktopDrag); // 原顺序：自己结束，不用宿主重建
        Assert.False(c.IsDesktopDragging);
        Assert.Single(host.Calls, x => x == "DesktopReorderSettled"); // 只让宿主补上拖动期间跳过的刷新
        Assert.False(c.FinishSettledDesktopMove());
        c.Sample(end + 2_010_000);
        Assert.Single(host.Calls, x => x == "DesktopReorderSettled");
    }

    [Fact]
    public void Desktop_drag_needs_an_expanded_bar()
    {
        var (c, _, t) = Opened();
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        Assert.False(c.BeginDesktopDrag(0, bar.Thumbs[0].CenterX, bar.Thumbs[0].CenterY, bar.Thumbs[0], t));
        Assert.Null(c.DropDesktop(t));
        Assert.Null(c.Sample(t + 20_000).DesktopDrag);
    }

    [Fact]
    public void A_press_skips_the_settle_animation_but_not_the_drag_or_the_system_call()
    {
        var (c, _, t, bar) = OpenedBar();
        Assert.True(c.SkipDesktopDragAnimation(out bool refresh)); // 没在拖：照常按下
        Assert.False(refresh);

        Assert.True(c.BeginDesktopDrag(0, bar.Thumbs[0].CenterX, bar.Thumbs[0].CenterY, bar.Thumbs[0], t));
        c.DragDesktopTo(bar.Thumbs[2].CenterX + 1, bar.Thumbs[0].CenterY, bar, t + 10_000);
        Assert.False(c.SkipDesktopDragAnimation(out _)); // 拖着
        var move = c.DropDesktop(t + 20_000)!.Value;
        Assert.False(c.SkipDesktopDragAnimation(out _)); // 等系统排序
        c.CompleteDesktopMove(move, success: true, t + 25_000);
        Assert.True(c.SkipDesktopDragAnimation(out refresh)); // 排好了、动画还在播：跳到终点
        Assert.True(refresh);
        Assert.False(c.IsDesktopDragging);
        Assert.False(c.FinishSettledDesktopMove()); // 之后到的落定通知什么也不做

        Assert.True(c.BeginDesktopDrag(1, bar.Thumbs[1].CenterX, bar.Thumbs[1].CenterY, bar.Thumbs[1], t + 100_000));
        c.CancelDesktopDrag(t + 110_000);
        Assert.True(c.SkipDesktopDragAnimation(out refresh)); // 弹回原顺序途中：不用重建
        Assert.False(refresh);
        Assert.Null(c.Sample(t + 120_000).DesktopDrag);
    }

    [Fact]
    public void Closing_the_overview_mid_animation_clears_the_drag_without_finishing_it()
    {
        var (c, host, t, bar) = OpenedBar();
        Assert.True(c.BeginDesktopDrag(0, bar.Thumbs[0].CenterX, bar.Thumbs[0].CenterY, bar.Thumbs[0], t));
        c.DragDesktopTo(bar.Thumbs[2].CenterX + 1, bar.Thumbs[0].CenterY, bar, t + 10_000);
        var move = c.DropDesktop(t + 20_000)!.Value;
        c.Close(null, t + 30_000);
        for (long u = t + 36_000; u < t + 3_000_000; u += 6_000) c.Sample(u);
        Assert.Contains("Hide:-", host.Calls);
        Assert.False(c.IsDesktopDragging);
        Assert.False(c.CompleteDesktopMove(move, success: true, t + 3_000_000)); // 撤层后才到的系统结果
        Assert.DoesNotContain("DesktopReorderSettled", host.Calls);
    }

    /// <summary>在展开好的桌面条上把桌面 0 拖到 2 松手，系统马上返回；返回落定动画播到一半的时刻。</summary>
    private static long DropAndSettleHalfway(OverviewController c, long t, DesktopBarGeometry bar, bool success)
    {
        Assert.True(c.BeginDesktopDrag(0, bar.Thumbs[0].CenterX, bar.Thumbs[0].CenterY, bar.Thumbs[0], t));
        c.DragDesktopTo(bar.Thumbs[2].CenterX + 1, bar.Thumbs[0].CenterY + 20, bar, t + 10_000);
        var move = c.DropDesktop(t + 20_000)!.Value;
        Assert.False(c.CompleteDesktopMove(move, success, t + 25_000));
        long mid = t + 120_000;
        Assert.NotNull(c.Sample(mid).DesktopDrag); // 还在动
        return mid;
    }

    private static GestureEvent Swipe3Down(GesturePhase phase, double downMm, long t)
        => new(GestureKind.Swipe, phase, 3, GestureAxis.Vertical, GestureDirection.Down, 0, downMm, 0, downMm * 5, t, t);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_gesture_mid_settle_skips_the_animation_instead_of_being_ignored(bool success)
    {
        var (c, host, t, bar) = OpenedBar();
        long u = DropAndSettleHalfway(c, t, bar, success);
        c.Begin(Swipe3Down(GesturePhase.Began, 0, u));
        Assert.Single(host.Calls, x => x == "DesktopReorderSettled"); // 宿主收尾（排好了的按新顺序重建，没排成的补上跳过的刷新）

        var frame = c.Sample(u + 1_000);
        if (success)
        {
            // 停在新顺序上、不闪回旧顺序，等宿主重建桌面条
            Assert.NotNull(frame.DesktopDrag);
            for (int i = 0; i < 3; i++)
                Assert.True(Distance(ThumbOf(frame, bar, i), bar.ThumbRect(DesktopBarReorder.Slot(i, 0, 2, 3), frame.BarExpansion)) < 0.5, $"桌面 {i} 没到终点");
            Assert.True(c.FinishSettledDesktopMove());
        }
        else Assert.Null(frame.DesktopDrag); // 弹回原顺序：直接结束
        Assert.False(c.IsDesktopDragging);

        c.Update(Swipe3Down(GesturePhase.Changed, 15, u + 50_000));
        Assert.True(c.Sample(u + 60_000).Progress < 0.9); // 这次手势照常跟手
        c.Sample(u + 3_000_000);
        Assert.Single(host.Calls, x => x == "DesktopReorderSettled"); // 不会再发一次
    }

    [Fact]
    public void The_rebuild_can_land_while_the_gesture_is_already_tracking()
    {
        var (c, host, t, pages) = OpenedWithPages();
        c.SetBarHovered(true, t);
        t += 2_000_000;
        c.Sample(t);
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        long u = DropAndSettleHalfway(c, t, bar, success: true);

        c.Begin(Swipe3Down(GesturePhase.Began, 0, u));
        c.Update(Swipe3Down(GesturePhase.Changed, 5, u + 20_000));
        double before = c.Sample(u + 30_000).Progress;
        Assert.True(before < 1);

        // 宿主的收尾投递到 UI 线程，晚于手势开始才到：按新顺序重建（当前页的场景不变）
        Assert.True(c.FinishSettledDesktopMove());
        c.SetDesktops(new DesktopBarState(["桌面 2", "桌面 3", "桌面 1"], 2));
        c.SetPages([pages[1], pages[2], pages[0]], 2);

        c.Update(Swipe3Down(GesturePhase.Changed, 15, u + 50_000));
        var frame = c.Sample(u + 60_000);
        Assert.True(frame.Progress < before); // 照常跟手
        Assert.Null(frame.DesktopDrag);
        c.End(Swipe3Down(GesturePhase.Ended, 40, u + 70_000));
        c.Sample(u + 3_000_000);
        Assert.Contains(host.Calls, x => x.StartsWith("Closing", StringComparison.Ordinal));
        Assert.Single(host.Calls, x => x == "DesktopReorderSettled");
    }

    [Fact]
    public void Gestures_still_wait_while_dragging_or_waiting_for_the_system()
    {
        var (c, host, t, bar) = OpenedBar();
        Assert.True(c.BeginDesktopDrag(0, bar.Thumbs[0].CenterX, bar.Thumbs[0].CenterY, bar.Thumbs[0], t));
        c.DragDesktopTo(bar.Thumbs[2].CenterX + 1, bar.Thumbs[0].CenterY, bar, t + 10_000);
        for (int k = 0; k < 2; k++)
        {
            if (k == 1) c.DropDesktop(t + 20_000); // 松手了、系统排序还没返回
            long u = t + 30_000 + k * 200_000;
            c.Begin(Swipe3Down(GesturePhase.Began, 0, u));
            c.Update(Swipe3Down(GesturePhase.Changed, 15, u + 50_000));
            c.End(Swipe3Down(GesturePhase.Ended, 15, u + 60_000));
            Assert.Equal(1, c.Sample(u + 70_000).Progress, 3);
            Assert.True(c.IsDesktopDragging);
        }
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("Closing", StringComparison.Ordinal));
        Assert.DoesNotContain("DesktopReorderSettled", host.Calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Paging_mid_settle_waits_only_for_the_new_order(bool success, bool swipe)
    {
        var (c, host, t, pages) = OpenedWithPages();
        c.SetBarHovered(true, t);
        t += 2_000_000;
        c.Sample(t);
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        long u = DropAndSettleHalfway(c, t, bar, success);

        // Ctrl+Win+←/→ 或四指横滑一次；返回是否翻了页
        bool Page(int direction, long at)
        {
            if (!swipe) return c.StepPage(direction, at);
            int before = host.Calls.Count(x => x.StartsWith("PageChanged", StringComparison.Ordinal));
            var dir = direction > 0 ? GestureDirection.Left : GestureDirection.Right;
            double dx = direction > 0 ? -40 : 40;
            c.DesktopSwipe.Begin(Swipe4(GesturePhase.Began, 0, 0, at, dir));
            c.DesktopSwipe.Update(Swipe4(GesturePhase.Changed, dx, dx * 5, at + 10_000, dir));
            c.DesktopSwipe.End(Swipe4(GesturePhase.Ended, dx, dx * 5, at + 20_000, dir));
            return host.Calls.Count(x => x.StartsWith("PageChanged", StringComparison.Ordinal)) > before;
        }

        if (success)
        {
            // 系统已按新顺序排好、各页还是旧顺序：这一下不翻，动画跳到终点，等宿主按新顺序重建
            Assert.False(Page(1, u));
            Assert.Single(host.Calls, x => x == "DesktopReorderSettled");
            Assert.NotNull(c.Sample(u + 30_000).DesktopDrag);
            Assert.True(c.FinishSettledDesktopMove());
            c.SetDesktops(new DesktopBarState(["桌面 2", "桌面 3", "桌面 1"], 2)); // 宿主重建：原第 0 个桌面（当前）现在是第 2 个
            c.SetPages([pages[1], pages[2], pages[0]], 2);
            Assert.True(Page(-1, u + 40_000));
        }
        else
        {
            Assert.True(Page(1, u)); // 弹回原顺序：直接结束，照常翻页
            Assert.Single(host.Calls, x => x == "DesktopReorderSettled");
        }
        Assert.Equal("PageChanged:1", host.Calls[^1]);
        Assert.False(c.IsDesktopDragging);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_frame_moves_a_little_from_pick_up_to_the_final_order(bool success)
    {
        var (c, host, t, bar) = OpenedBar();
        double pitch = bar.Thumbs[1].Left - bar.Thumbs[0].Left;
        var thumb = bar.Thumbs[0];
        long u = t;
        RectD[]? last = null;
        int frames = 0;

        void Step(bool draggedFollows)
        {
            var frame = c.Sample(u);
            var rects = Enumerable.Range(0, 3).Select(i => ThumbOf(frame, bar, i)).ToArray();
            if (last is not null)
                for (int i = 0; i < 3; i++)
                    if (!(draggedFollows && i == 0)) // 跟着指针的那个由指针决定
                        Assert.True(Distance(last[i], rects[i]) < 0.2 * pitch, $"第 {frames} 帧桌面 {i} 跳了 {Distance(last[i], rects[i]):F1}px");
            last = rects;
            frames++;
            u += 6_000;
        }

        Assert.True(c.BeginDesktopDrag(0, thumb.CenterX, thumb.CenterY, thumb, u));
        // 指针往右拖过两个槽位，中途折回一个（让位动画中途改道）
        for (int k = 0; k <= 40; k++)
        {
            double x = thumb.CenterX + (k <= 30 ? k / 30.0 * 2.2 * pitch : (2.2 - (k - 30) / 10.0 * 1.0) * pitch);
            c.DragDesktopTo(x, thumb.CenterY + 15, bar, u);
            Step(draggedFollows: true);
        }
        var move = c.DropDesktop(u)!.Value;
        Assert.Equal(new DesktopBarMove(0, 1), move);
        Step(draggedFollows: false); // 松手那一帧不能跳
        for (int k = 0; k < 3; k++) Step(draggedFollows: false);
        Assert.False(c.CompleteDesktopMove(move, success, u));
        while (c.IsDesktopDragging && frames < 400)
        {
            Step(draggedFollows: false);
            if (host.Calls.Contains("DesktopReorderSettled") && c.FinishSettledDesktopMove()) break;
        }
        Assert.False(c.IsDesktopDragging);

        // 结束预览后的第一帧（成功：宿主已按新顺序重建，原第 i 个桌面现在是第 Slot(i) 个）与最后一帧预览一致
        var after = c.Sample(u);
        Assert.Null(after.DesktopDrag);
        for (int i = 0; i < 3; i++)
        {
            int now = success ? DesktopBarReorder.Slot(i, move.From, move.To, 3) : i;
            Assert.True(Distance(last![i], bar.ThumbRect(now, after.BarExpansion)) < 0.5, $"桌面 {i} 结束时跳了");
        }
    }

    [Fact]
    public void Desktop_drag_keeps_the_current_page_and_blocks_paging_clicks_and_swipes()
    {
        var (c, host, t, pages) = OpenedWithPages();
        c.SetBarHovered(true, t);
        t += 2_000_000;
        c.Sample(t);
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        Assert.True(c.BeginDesktopDrag(0, bar.Thumbs[0].CenterX, bar.Thumbs[0].CenterY, bar.Thumbs[0], t));
        c.DragDesktopTo(bar.Thumbs[2].CenterX, bar.Thumbs[0].CenterY, bar, t + 10_000);
        Assert.Same(pages[0], c.Scene);
        Assert.Equal(0, c.Desktops.Current);
        Assert.False(c.StepPage(1, t + 20_000));
        c.GoToDesktop(2, t + 20_000);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("SwitchDesktop", StringComparison.Ordinal));
        c.DesktopSwipe.Begin(Swipe4(GesturePhase.Began, 0, 0, t + 20_000));
        c.DesktopSwipe.Update(Swipe4(GesturePhase.Changed, -40, -200, t + 30_000));
        c.DesktopSwipe.End(Swipe4(GesturePhase.Ended, -40, -200, t + 40_000));
        Assert.Equal(0, c.Sample(t + 50_000).PagePosition, 6);
        Assert.DoesNotContain(host.Calls, x => x.StartsWith("PageChanged", StringComparison.Ordinal));
    }

    [Fact]
    public void MoveDesktop_uses_the_checked_vtable_slot_for_each_supported_layout()
    {
        Assert.Equal(12, global::GlideShell.Platform.VirtualDesktops.VirtualDesktopLayout.Win11_24H2.MoveDesktop);
        Assert.Equal(11, global::GlideShell.Platform.VirtualDesktops.VirtualDesktopLayout.Win11_23H2.MoveDesktop);
        Assert.Equal(10, global::GlideShell.Platform.VirtualDesktops.VirtualDesktopLayout.Win11_23H2.CreateDesktop);
        Assert.Equal(12, global::GlideShell.Platform.VirtualDesktops.VirtualDesktopLayout.Win11_23H2.RemoveDesktop);
    }

    [Fact]
    public void Windows_fade_out_while_zooming_into_another_desktop()
    {
        var windows = new[] { new OverviewWindow(1, "a", new RectD(100, 100, 800, 600), false, true, 0, "p") };
        var bar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        var scene = OverviewScene.Build(windows, [new OverviewArea(Area, 1.5, bar)]);
        Assert.True(scene.Slots[0].Tile.Top >= bar.ContentArea.Top); // 平铺在桌面条下面
        var rects = new RectD[1];
        var opacity = new double[1];
        OverviewController.WindowRects(scene, new OverviewFrame(1, 0, 0, default, true, null, 1, 2, 0.75), rects, opacity);
        Assert.Equal(0.25, opacity[0], 6);
        Assert.Equal(scene.Tile(0, 1), rects[0]);
    }
}
