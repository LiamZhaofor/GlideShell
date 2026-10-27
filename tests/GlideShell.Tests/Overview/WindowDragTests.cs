using GlideShell.Core.Configuration;
using GlideShell.Core.Geometry;
using GlideShell.Core.Gestures;
using GlideShell.Core.Overview;

namespace GlideShell.Tests.Overview;

/// <summary>总览里拖动窗口：跟着指针、在桌面条上缩小、放到别的桌面后移走并补位、放在别处飞回原位。</summary>
public class WindowDragTests
{
    private static readonly RectD Area = new(0, 0, 2560, 1528);
    private static readonly DesktopBarGeometry Bar = DesktopBarGeometry.Compute(Area, 3, 1.5);

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
    }

    private static OverviewScene ThreeWindows() => OverviewScene.Build(
    [
        new OverviewWindow(1, "a", new RectD(100, 200, 900, 700), false, true, 0, "p"),
        new OverviewWindow(2, "b", new RectD(1100, 150, 1000, 800), false, false, 0, "p"),
        new OverviewWindow(3, "c", new RectD(600, 700, 800, 600), false, false, 0, "p"),
    ], [new OverviewArea(Area, 1.5, Bar)]);

    private static (OverviewController C, FakeHost Host, long T) Opened(OverviewScene? scene = null)
    {
        var host = new FakeHost();
        var c = new OverviewController(host, () => new OverviewConfig());
        c.SetScene(scene ?? ThreeWindows());
        c.SetDesktops(new DesktopBarState(["桌面 1", "桌面 2", "桌面 3"], 0));
        c.Open(0);
        c.OnShown(0);
        long t = 2_000_000;
        Assert.True(c.Sample(t).Visible);
        return (c, host, t);
    }

    private static RectD Rect(OverviewController c, long t, int index, out double opacity)
    {
        var scene = c.Scene;
        var rects = new RectD[scene.Count];
        var op = new double[scene.Count];
        OverviewController.WindowRects(scene, c.Sample(t), rects, op);
        opacity = op[index];
        return rects[index];
    }

    // ---------- 场景：移走 / 加入窗口 ----------

    [Fact]
    public void Removing_a_window_keeps_indices_relayouts_the_rest_and_hands_over_the_foreground()
    {
        var scene = ThreeWindows();
        var next = scene.Without(0);
        Assert.Equal(3, next.Count); // 下标不变（缩略图注册按下标）
        Assert.True(next.IsRemoved(0));
        Assert.False(next.IsRemoved(1));
        Assert.False(next.Windows[0].IsForeground);
        Assert.True(next.Windows[1].IsForeground); // 剩下最上层的接过前台（活动阴影）

        // 其余窗口按两个窗口重新排版：与只用这两个窗口排出来的一样
        var only = OverviewScene.Build([scene.Windows[1], scene.Windows[2]], [new OverviewArea(Area, 1.5, Bar)]);
        Assert.Equal(only.Slots[0].Tile, next.Slots[1].Tile);
        Assert.Equal(only.Slots[1].Tile, next.Slots[2].Tile);

        // 重排从旧位置过渡过去
        Assert.Equal(scene.Tile(1, 0), next.Tile(1, 0, 0));
        Assert.Equal(next.Slots[1].Tile, next.Tile(1, 0, 1));

        // 移走的不参与命中与键盘选择
        var old = scene.Tile(0, 0);
        Assert.NotEqual(0, next.HitTest(old.CenterX, old.CenterY, 1));
        for (int i = 0; i < 6; i++) Assert.NotEqual(0, next.Cycle(i % 3, false));
        Assert.Equal(1, next.FirstPresent());
    }

    [Fact]
    public void Adding_a_window_appends_it_and_relayouts()
    {
        var scene = ThreeWindows().Without(2);
        var w = new OverviewWindow(9, "x", new RectD(300, 300, 600, 400), false, false, 0, "p");
        var next = scene.With(w);
        Assert.Equal(4, next.Count);
        Assert.Equal(9, next.Windows[3].Id);
        Assert.True(next.IsRemoved(2));
        Assert.False(next.IsRemoved(3));
        Assert.True(next.Slots[3].Tile.Width > 0);
        Assert.Equal(next.Slots[3].Tile, next.Tile(3, 0, 0)); // 新加入的没有旧位置
    }

    // ---------- 控制器：拖动 ----------

    [Fact]
    public void Dragged_window_follows_the_pointer_and_shrinks_over_the_bar()
    {
        var (c, _, t) = Opened();
        var tile = c.Scene.Tile(1, 0);
        double gx = tile.Left + tile.Width * 0.25, gy = tile.Top + tile.Height * 0.5;
        Assert.True(c.BeginDrag(2, gx, gy, new RectD(0, 0, 200, 150), t));
        Assert.True(c.IsDragging);

        c.DragTo(gx + 300, gy - 100, false, t + 10_000);
        var r = Rect(c, t + 20_000, 1, out double op);
        Assert.Equal(tile.Width, r.Width, 3); // 大小不变
        Assert.Equal(gx + 300, r.Left + r.Width * 0.25, 3); // 按下的那一点跟着指针
        Assert.Equal(gy - 100, r.Top + r.Height * 0.5, 3);
        Assert.Equal(1, op);

        c.DragTo(1000, 60, true, t + 30_000);
        var small = Rect(c, t + 2_000_000, 1, out _);
        Assert.Equal(200, small.Width, 2);
        Assert.Equal(150, small.Height, 2);
        Assert.Equal(1000, small.Left + small.Width * 0.25, 2); // 缩小时指针还在同一个相对位置
        Assert.True(c.Sample(t + 2_000_000).Dragging);
    }

    [Fact]
    public void Dropping_on_another_desktop_flies_into_it_fades_out_and_the_rest_fill_the_gap()
    {
        var (c, _, t) = Opened();
        var before = c.Scene;
        var other = before.Tile(2, 0);
        var tile = before.Tile(1, 0);
        c.BeginDrag(2, tile.CenterX, tile.CenterY, new RectD(0, 0, 200, 150), t);
        c.DragTo(Bar.Thumbs[1].CenterX, Bar.Thumbs[1].CenterY, true, t + 10_000);
        c.Sample(t + 500_000);

        var into = Bar.InThumb(1, before.Windows[1].Source);
        var after = c.EndDrag(into, t + 500_000);
        Assert.NotNull(after);
        Assert.Same(after, c.Scene);
        Assert.True(c.Scene.IsRemoved(1));

        // 刚放下：其余窗口还在原来的位置（从那里开始补位）
        Assert.Equal(other, Rect(c, t + 500_000, 2, out _));
        var mid = Rect(c, t + 600_000, 1, out double midOpacity);
        Assert.True(midOpacity > 0); // 飞行途中还看得见

        var end = Rect(c, t + 3_000_000, 1, out double endOpacity);
        Assert.Equal(0, endOpacity, 3);
        Assert.False(c.IsDragging);
        Assert.Equal(c.Scene.Slots[2].Tile, Rect(c, t + 3_000_000, 2, out _)); // 补位完成
        Assert.NotEqual(other, c.Scene.Slots[2].Tile);
    }

    [Fact]
    public void Dropping_elsewhere_flies_back_to_the_tile()
    {
        var (c, _, t) = Opened();
        var tile = c.Scene.Tile(0, 0);
        c.BeginDrag(1, tile.CenterX, tile.CenterY, new RectD(0, 0, 200, 150), t);
        c.DragTo(tile.CenterX + 400, tile.CenterY + 200, false, t + 10_000);
        c.Sample(t + 20_000);
        Assert.Null(c.EndDrag(null, t + 20_000));
        Assert.True(c.IsDragging); // 飞回途中

        var start = Rect(c, t + 20_000, 0, out _);
        Assert.Equal(tile.CenterX + 400, start.CenterX, 3);
        var end = Rect(c, t + 3_000_000, 0, out double op);
        Assert.Equal(tile, end);
        Assert.Equal(1, op);
        Assert.False(c.IsDragging);
        Assert.False(c.Scene.IsRemoved(0));
    }

    [Fact]
    public void Drag_needs_a_settled_overview_and_blocks_gestures_while_held()
    {
        var host = new FakeHost();
        var c = new OverviewController(host, () => new OverviewConfig());
        c.SetScene(ThreeWindows());
        c.Open(0);
        c.OnShown(0);
        var tile = c.Scene.Tile(0, 0);
        Assert.False(c.BeginDrag(1, tile.CenterX, tile.CenterY, default, 50_000)); // 还在展开

        var (d, _, t) = Opened();
        Assert.False(d.BeginDrag(99, 0, 0, default, t)); // 不在场景里
        Assert.True(d.BeginDrag(1, tile.CenterX, tile.CenterY, default, t));
        d.Begin(new GestureEvent(GestureKind.Swipe, GesturePhase.Began, 3, GestureAxis.Vertical, GestureDirection.Down, 0, 0, 0, 0, t, t));
        d.Update(new GestureEvent(GestureKind.Swipe, GesturePhase.Changed, 3, GestureAxis.Vertical, GestureDirection.Down, 0, 20, 0, 0, t + 10_000, t + 10_000));
        Assert.Equal(1, d.Sample(t + 20_000).Progress, 3); // 拖着窗口时三指手势不接管
    }

    [Fact]
    public void Escape_while_dragging_closes_with_the_window_flying_home()
    {
        var (c, host, t) = Opened();
        var tile = c.Scene.Tile(0, 0);
        c.BeginDrag(1, tile.CenterX, tile.CenterY, default, t);
        c.DragTo(tile.CenterX + 500, tile.CenterY, false, t + 10_000);
        c.Close(null, t + 20_000);
        var end = Rect(c, t + 3_000_000, 0, out _);
        Assert.Equal(c.Scene.Windows[0].Source, end); // 收起后回到真实位置
        Assert.Equal("Hide:-", host.Calls[^1]);
        Assert.False(c.IsDragging);
    }

    [Fact]
    public void A_window_dropped_on_another_page_is_added_to_that_page()
    {
        var (c, _, t) = Opened();
        var pages = new[]
        {
            ThreeWindows(),
            OverviewScene.Build([new OverviewWindow(7, "d", new RectD(100, 100, 800, 600), false, true, 0, "p")], [new OverviewArea(Area, 1.5, Bar)]),
        };
        c.SetPages(pages, 0);
        var tile = c.Scene.Tile(2, 0);
        c.BeginDrag(3, tile.CenterX, tile.CenterY, default, t);
        c.EndDrag(Bar.InThumb(1, c.Scene.Windows[2].Source), t + 10_000);
        int index = c.AddToPage(1, c.Scene.Windows[2] with { IsForeground = false });
        Assert.Equal(1, index);
        Assert.Equal(2, c.Pages[1].Count);
        Assert.True(c.Pages[0].IsRemoved(2));
        Assert.Same(c.Pages[0], c.Scene);
        Assert.Equal(-1, c.AddToPage(0, c.Scene.Windows[2])); // 当前页不能加
    }

    // ---------- 关闭窗口 ----------

    [Fact]
    public void Close_button_sits_on_the_top_left_corner_and_is_a_round_dark_disc_with_a_white_cross()
    {
        var tile = new RectD(400, 300, 800, 500);
        var b = CloseButton.Rect(tile, 1.5);
        Assert.Equal(CloseButton.DiameterDip * 1.5, b.Width, 6);
        Assert.True(b.Contains(tile.Left + 1, tile.Top + 1)); // 盖着窗口左上角
        Assert.True(b.Left < tile.Left && b.Top < tile.Top); // 也露出一点在外面
        Assert.True(CloseButton.Hit(b, b.CenterX, b.CenterY));
        Assert.False(CloseButton.Hit(b, b.Left + 1, b.Top + 1)); // 外接正方形的角不在圆里

        int size = 39;
        var px = CloseButton.Render(size);
        int Alpha(int x, int y) => px[(y * size + x) * 4 + 3];
        int Red(int x, int y) => px[(y * size + x) * 4 + 2];
        Assert.Equal(0, Alpha(0, 0)); // 角上透明
        Assert.Equal(255, Alpha(size / 2, size / 2)); // 正中是「×」的交点：白色不透明
        Assert.Equal(255, Red(size / 2, size / 2));
        Assert.InRange(Alpha(size / 2, 4), 200, 240); // 上方圆底：深色半透明
        Assert.True(Red(size / 2, 4) < 80);
    }

    [Fact]
    public void A_closed_window_shrinks_out_in_place_and_the_rest_fill_the_gap()
    {
        var (c, _, t) = Opened();
        var before = c.Scene;
        var tile = before.Tile(0, 0);
        var other = before.Tile(1, 0);
        var after = c.RemoveWindow(1, t);
        Assert.NotNull(after);
        Assert.True(c.Scene.IsRemoved(0));
        Assert.True(c.Scene.Windows[1].IsForeground);

        var start = Rect(c, t, 0, out double op0);
        Assert.Equal(tile, start);
        Assert.Equal(1, op0);
        Assert.Equal(other, Rect(c, t, 1, out _));

        var end = Rect(c, t + 3_000_000, 0, out double op1);
        Assert.Equal(0, op1, 3);
        Assert.Equal(tile.CenterX, end.CenterX, 3); // 原地缩小
        Assert.True(end.Width < tile.Width);
        Assert.Equal(c.Scene.Slots[1].Tile, Rect(c, t + 3_000_000, 1, out _));
        Assert.Null(c.RemoveWindow(1, t + 3_000_000)); // 已经移走
    }

    // ---------- 应用窗口视图 ----------

    [Fact]
    public void Focusing_an_app_tiles_only_its_windows_and_fades_the_rest_in_place()
    {
        var full = ThreeWindows(); // 窗口 1（前台）、2、3
        var mixed = OverviewScene.Build(full.Windows.Select((w, i) => w with { ProcessName = i == 0 ? "a" : "b" }).ToList(), [new OverviewArea(Area, 1.5, Bar)]);
        var app = mixed.Focus(w => w.ProcessName == "b", [new OverviewArea(Area, 1.5)]);
        Assert.Equal(3, app.Count);
        Assert.True(app.IsExcluded(0));
        Assert.True(app.IsPresent(1) && app.IsPresent(2));
        Assert.Equal(2, app.PresentCount);
        Assert.True(app.Windows[1].IsForeground); // 前台标记交给这个程序最上层的窗口
        Assert.False(app.Windows[0].IsForeground);
        var only = OverviewScene.Build([mixed.Windows[1], mixed.Windows[2]], [new OverviewArea(Area, 1.5)]);
        Assert.Equal(only.Slots[0].Tile, app.Slots[1].Tile); // 没有桌面条，按两个窗口排版

        var source = app.Windows[0].Source;
        Assert.NotEqual(0, app.HitTest(source.CenterX, source.CenterY, 1));
        for (int i = 0; i < 6; i++) Assert.NotEqual(0, app.Cycle(i % 3, false));

        var rects = new RectD[3];
        var op = new double[3];
        OverviewController.WindowRects(app, new OverviewFrame(0.25, 0, 0, Core.Motion.TransitionPhase.Tracking, true, null), rects, op);
        Assert.Equal(source, rects[0]); // 不动
        Assert.Equal(0.75, op[0], 6); // 随进度淡出
        Assert.Equal(1, op[1]);
        OverviewController.WindowRects(app, new OverviewFrame(1, 0, 0, Core.Motion.TransitionPhase.Resting, true, null), rects, op);
        Assert.Equal(0, op[0], 6);
        Assert.Equal(app.Slots[1].Tile, rects[1]);
    }

    [Fact]
    public void App_view_puts_minimized_windows_on_a_shelf_at_the_bottom()
    {
        var full = ThreeWindows();
        var windows = full.Windows.Select((w, i) => w with { IsMinimized = i == 2 }).ToList(); // 窗口 3 最小化
        var scene = OverviewScene.Build(windows, [new OverviewArea(Area, 1.5)]);
        var areas = new[] { new OverviewArea(Area, 1.5) };
        var app = scene.Focus(_ => true, areas, minimizedShelf: true);
        double shelfTop = Area.Bottom - Area.Height * OverviewScene.ShelfFraction - 20 * 1.5;
        Assert.True(app.Slots[2].Tile.Top >= shelfTop); // 最小化的在底部那一排
        foreach (int i in new[] { 0, 1 }) Assert.True(app.Slots[i].Label.Bottom <= Area.Bottom - Area.Height * OverviewScene.ShelfFraction);
        Assert.True(app.Slots[2].Tile.Height < app.Slots[0].Tile.Height); // 缩得更小

        // 补位后还是这个规则
        var after = app.Without(0);
        Assert.True(after.Slots[2].Tile.Top >= shelfTop);

        // 全是最小化（或全没有）时不分排
        var all = OverviewScene.Build(full.Windows.Select(w => w with { IsMinimized = true }).ToList(), areas).Focus(_ => true, areas, minimizedShelf: true);
        var plain = OverviewScene.Build(full.Windows.Select(w => w with { IsMinimized = true }).ToList(), areas);
        Assert.Equal(plain.Slots[0].Tile, all.Slots[0].Tile);
    }

    [Fact]
    public void Switching_an_open_overview_to_app_view_relayouts_from_the_current_tiles()
    {
        var full = ThreeWindows();
        var mixed = OverviewScene.Build(full.Windows.Select((w, i) => w with { ProcessName = i == 0 ? "a" : "b" }).ToList(), [new OverviewArea(Area, 1.5, Bar)]);
        var (c, _, t) = Opened(mixed);
        Assert.Equal(1, mixed.LabelHitTest(mixed.Slots[1].Label.CenterX, mixed.Slots[1].Label.CenterY));
        Assert.Equal(-1, mixed.LabelHitTest(mixed.Slots[1].Tile.CenterX, mixed.Slots[1].Tile.CenterY));

        var app = mixed.Focus(w => w.ProcessName == "b", [new OverviewArea(Area, 1.5)], animate: true);
        Assert.True(c.FocusApp(app, t));
        Assert.Equal(OverviewMode.App, c.Mode);
        Assert.Equal(0, c.Desktops.Count); // 桌面条撤掉
        Assert.False(c.FocusApp(app, t)); // 已经是应用窗口视图

        var start = Rect(c, t, 1, out double op1);
        Assert.Equal(mixed.Tile(1, 0), start); // 从原来的平铺位开始
        Assert.Equal(1, op1, 6);
        var other = Rect(c, t, 0, out double op0);
        Assert.Equal(mixed.Tile(0, 0).Left, other.Left, 6); // 其他程序的窗口在原平铺位淡出
        Assert.Equal(mixed.Tile(0, 0).Width, other.Width, 6);
        Assert.Equal(1, op0, 6);

        var end = Rect(c, t + 3_000_000, 1, out _);
        Assert.Equal(app.Slots[1].Tile, end);
        Rect(c, t + 3_000_000, 0, out double gone);
        Assert.Equal(0, gone, 6);
    }

    [Fact]
    public void Returning_from_app_view_relayouts_back_and_fades_other_apps_in()
    {
        var full = ThreeWindows();
        var mixed = OverviewScene.Build(full.Windows.Select((w, i) => w with { ProcessName = i == 0 ? "a" : "b" }).ToList(), [new OverviewArea(Area, 1.5, Bar)]);
        var (c, _, t) = Opened(mixed);
        var app = mixed.Focus(w => w.ProcessName == "b", [new OverviewArea(Area, 1.5)], animate: true);
        Assert.True(c.FocusApp(app, t));
        long t2 = t + 3_000_000;
        c.Sample(t2);

        var back = mixed.ReturnFrom(app);
        Assert.True(back.IsEntering(0));
        Assert.False(back.IsEntering(1));
        Assert.True(c.UnfocusApp(back, t2));
        Assert.Equal(OverviewMode.All, c.Mode);
        Assert.False(c.UnfocusApp(back, t2));

        var start = Rect(c, t2, 1, out _);
        Assert.Equal(app.Slots[1].Tile.Left, start.Left, 6); // 从应用窗口视图里的位置开始
        Rect(c, t2, 0, out double fading);
        Assert.Equal(0, fading, 6); // 其他程序的窗口从透明开始淡入
        Assert.Equal(mixed.Slots[1].Tile, Rect(c, t2 + 3_000_000, 1, out _));
        Rect(c, t2 + 3_000_000, 0, out double shown);
        Assert.Equal(1, shown, 6);
    }

    [Fact]
    public void In_thumb_maps_screen_rects_proportionally()
    {
        var r = Bar.InThumb(0, Area);
        Assert.Equal(Bar.Thumbs[0].Left, r.Left, 6);
        Assert.Equal(Bar.Thumbs[0].Width, r.Width, 6);
        var half = Bar.InThumb(2, new RectD(1280, 764, 1280, 764));
        Assert.Equal(Bar.Thumbs[2].CenterX, half.Left, 6);
        Assert.Equal(Bar.Thumbs[2].CenterY, half.Top, 6);
    }
}
