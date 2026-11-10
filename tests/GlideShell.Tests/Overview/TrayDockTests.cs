using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;

namespace GlideShell.Tests.Overview;

public class TrayDockTests
{
    private static readonly RectD Area = new(0, 0, 2560, 1528);
    private const string WinDir = @"C:\Windows";

    private static TrayWindowInfo Win(string title, bool visible = false, int w = 1200, int h = 800, bool tool = false, bool owner = false,
        bool appWindow = false, bool seen = false, bool minimized = false, long handle = 1)
        => new(handle, title, visible, minimized, tool, appWindow, owner, w, h, seen);

    // ---------- 判断 ----------

    [Fact]
    public void An_app_with_a_visible_window_stays_out_of_the_bar()
    {
        Assert.Null(TrayClassifier.HiddenMainWindow([Win("Clash Verge", visible: true), Win("", w: 1920, h: 1116)]));
        // 最小化的窗口也在总览里
        Assert.Null(TrayClassifier.HiddenMainWindow([Win("Telegram", visible: true, minimized: true, w: 160, h: 28)]));
    }

    [Fact]
    public void Visible_helper_windows_do_not_count_as_visible()
    {
        long? main = TrayClassifier.HiddenMainWindow([
            Win("", visible: true, w: 1, h: 1),                 // 不可见的 1×1 宿主
            Win("工具面板", visible: true, tool: true),            // 工具窗
            Win("对话框", visible: true, owner: true),             // 附属窗口
            Win("悬浮球", visible: true, w: 80, h: 80),            // 太小
        ]);
        Assert.Equal(0L, main);
    }

    [Fact]
    public void The_hidden_main_window_is_the_biggest_one_seen_before()
    {
        long? main = TrayClassifier.HiddenMainWindow([
            Win("QTrayIconMessageWindow", w: 1920, h: 1116, handle: 1),   // 没见过：不算
            Win("微信", w: 1000, h: 700, seen: true, handle: 2),
            Win("设置", w: 600, h: 400, seen: true, handle: 3),
            Win("工具", w: 2000, h: 1400, seen: true, tool: true, handle: 4),
        ]);
        Assert.Equal(2L, main);
        Assert.Equal(0L, TrayClassifier.HiddenMainWindow([Win("Tailscale 1.104.5", w: 1920, h: 1116)]));
    }

    [Fact]
    public void Bar_order_is_by_name()
    {
        TrayApp App(string name) => new(name, name, name + ".exe", 0, 0, null);
        // 名字按当前语言的排序规则（中文系统里汉字与英文的先后由系统定），这里只用英文名
        var ordered = TrayClassifier.Order([App("Tailscale"), App("Weixin"), App("PixPin"), App("Steam")]);
        Assert.Equal(["PixPin", "Steam", "Tailscale", "Weixin"], ordered.Select(a => a.Name));
    }

    // ---------- 进程关系 ----------

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe", true)]
    [InlineData(@"C:\Users\a\AppData\Roaming\baidu\BaiduNetdisk\module\BrowserEngine\BaiduNetdiskUnite.exe", @"C:\Users\a\AppData\Roaming\baidu\BaiduNetdisk\BaiduNetdisk.exe", true)]
    [InlineData(@"C:\Program Files\Tencent\QQNT\QQ.exe", @"C:\Program Files\Tencent\QQNT\QQ.exe", true)]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\magpie\magpie.exe", @"C:\Users\a\AppData\Local\Programs\CC Switch\cc-switch.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\A_1.0_x64__x\a.exe", @"C:\Program Files\WindowsApps\B_1.0_x64__y\b.exe", false)]
    [InlineData(@"C:\Users\a\Downloads\magpie.exe", @"C:\Users\a\Downloads\tools\other.exe", false)]
    [InlineData(@"C:\Windows\System32\SecurityHealthSystray.exe", @"C:\Windows\System32\Taskmgr.exe", false)]
    [InlineData(@"C:\Program Files\Tailscale\tailscale-ipn.exe", @"C:\Program Files\Clash Verge\clash-verge.exe", false)]
    public void Processes_of_one_app_are_related_by_its_own_folder(string a, string b, bool related)
    {
        Assert.Equal(related, TrayPaths.Related(a, b, WinDir));
        Assert.Equal(related, TrayPaths.Related(b, a, WinDir));
    }

    // ---------- 几何 ----------

    [Fact]
    public void Dock_sits_centered_above_the_bottom_with_one_tile_per_app()
    {
        var g = TrayDockGeometry.Compute(Area, 1.5, 5);
        Assert.Equal(5, g.Count);
        Assert.Equal(Area.CenterX, g.Dock.CenterX, 0);
        Assert.Equal(Area.Bottom - TrayDockGeometry.BottomMargin * 1.5, g.Dock.Bottom, 0);
        Assert.Equal(TrayDockGeometry.DockHeight * 1.5, g.Dock.Height, 0);
        for (int i = 0; i < g.Count; i++)
        {
            Assert.True(g.Dock.Contains(g.Tiles[i].CenterX, g.Tiles[i].CenterY));
            Assert.True(g.Tiles[i].Contains(g.Icons[i].CenterX, g.Icons[i].CenterY));
            Assert.True(g.Labels[i].Top >= g.Icons[i].Bottom && g.Labels[i].Bottom <= g.Tiles[i].Bottom + 1);
            Assert.Equal(g.Tiles[0].Width, g.Tiles[i].Width, 6); // 一视同仁：每个方块一样大
            if (i > 0) Assert.True(g.Tiles[i].Left >= g.Tiles[i - 1].Right);
        }
    }

    [Fact]
    public void Hidden_offset_puts_the_whole_dock_below_the_screen_edge()
    {
        var g = TrayDockGeometry.Compute(Area, 1.5, 3);
        Assert.True(g.Dock.Top + g.HiddenOffset > Area.Bottom); // 进度 0 时整条沉在工作区底边以下（覆盖层外，看不见）
        Assert.True(g.HiddenOffset < 2 * g.Dock.Height); // 但不多沉：升起的距离与手指移动的距离相称
        Assert.Equal(0, TrayDockGeometry.Compute(Area, 1.5, 0).HiddenOffset);
    }

    [Fact]
    public void An_empty_dock_has_nothing()
    {
        var empty = TrayDockGeometry.Compute(Area, 1, 0);
        Assert.Equal(0, empty.Count);
        Assert.False(empty.Contains(Area.CenterX, Area.Bottom - 30));
    }

    [Fact]
    public void Too_many_apps_shrink_the_dock_then_drop_the_tail()
    {
        var narrow = new RectD(0, 0, 900, 700);
        var shrunk = TrayDockGeometry.Compute(narrow, 1, 12);
        Assert.Equal(12, shrunk.Count);
        Assert.True(shrunk.Scale < 1 && shrunk.Scale >= TrayDockGeometry.MinScale);
        Assert.True(shrunk.Dock.Width <= narrow.Width - 2 * TrayDockGeometry.SideMargin + 1);

        var tooMany = TrayDockGeometry.Compute(narrow, 1, 60);
        Assert.InRange(tooMany.Count, 1, 59);
        Assert.Equal(TrayDockGeometry.MinScale, tooMany.Scale, 6);
        Assert.True(tooMany.Dock.Width <= narrow.Width - 2 * TrayDockGeometry.SideMargin + 1);
    }

    [Fact]
    public void Hit_testing_finds_tiles_and_ignores_the_padding()
    {
        var g = TrayDockGeometry.Compute(Area, 1.5, 5);
        Assert.Equal(2, g.HitTest(g.Tiles[2].CenterX, g.Tiles[2].CenterY));
        Assert.Equal(-1, g.HitTest(g.Dock.Left + 2, g.Dock.CenterY)); // 栏的左边距
        Assert.True(g.Contains(g.Dock.Left + 2, g.Dock.CenterY));
        Assert.Equal(-1, g.HitTest(Area.CenterX, Area.CenterY));
        Assert.Equal(0, g.Nearest(0));
        Assert.Equal(4, g.Nearest(Area.Right));
    }

    // ---------- 窗口区域给栏让位 ----------

    [Fact]
    public void Windows_are_laid_out_above_the_dock()
    {
        double inset = TrayDockGeometry.LayoutInset(Area, 1.5);
        Assert.True(inset > 0 && inset < TrayDockGeometry.ReservedHeight(1.5));
        var dock = TrayDockGeometry.Compute(Area, 1.5, 8);
        var windows = Enumerable.Range(0, 6).Select(i => new OverviewWindow(i + 1, $"w{i}", new RectD(100 + i * 300, 100 + i * 150, 1200, 900), false, i == 0, 0, "p")).ToList();
        foreach (var bar in new[] { null, DesktopBarGeometry.Compute(Area, 3, 1.5, inset) })
        {
            var scene = OverviewScene.Build(windows, [new OverviewArea(Area, 1.5, bar, inset)]);
            for (int i = 0; i < scene.Count; i++)
            {
                Assert.True(scene.Slots[i].Label.Bottom < dock.Dock.Top, $"window {i} label overlaps the dock");
                if (bar is not null) Assert.True(scene.Tile(i, 1).Bottom < dock.Dock.Top, $"window {i} overlaps the dock with the bar expanded");
            }
        }
        // 没有栏时与原来一样
        var plain = OverviewScene.Build(windows, [new OverviewArea(Area, 1.5)]);
        var plainBar = DesktopBarGeometry.Compute(Area, 3, 1.5);
        Assert.Equal(Area.Bottom, plainBar.ContentArea.Bottom);
        Assert.True(plain.Slots.Max(s => s.Label.Bottom) > OverviewScene.Build(windows, [new OverviewArea(Area, 1.5, null, inset)]).Slots.Max(s => s.Label.Bottom));
    }
}
