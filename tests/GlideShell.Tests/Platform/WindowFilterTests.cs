using GlideShell.Platform.Shell;

namespace GlideShell.Tests.Platform;

public class WindowFilterTests
{
    private static readonly WindowProbe Normal = new(
        Visible: true, ToolWindow: false, AppWindow: false, NoActivate: false, HasOwner: false, Cloaked: 0,
        ClassName: "Chrome_WidgetWin_1", TitleLength: 12, Width: 1200, Height: 800, OnCurrentDesktop: true, IsOwnWindow: false);

    [Fact]
    public void Ordinary_app_window_is_included() => Assert.Null(WindowFilter.Reject(Normal));

    [Fact]
    public void Suspended_uwp_app_cloak_is_still_included()
        => Assert.Null(WindowFilter.Reject(Normal with { Cloaked = 1 }));

    public static TheoryData<string, WindowProbe> Rejected => new()
    {
        { "不可见", Normal with { Visible = false } },
        { "工具窗口", Normal with { ToolWindow = true } },
        { "有 owner", Normal with { HasOwner = true } },
        { "其他桌面（cloak）", Normal with { Cloaked = 2 } },
        { "继承隐藏", Normal with { Cloaked = 4 } },
        { "外壳", Normal with { ClassName = "Shell_TrayWnd" } },
        { "无标题", Normal with { TitleLength = 0 } },
        { "太小", Normal with { Width = 150 } },
        { "其他桌面（VDM）", Normal with { OnCurrentDesktop = false } },
        { "自己的覆盖层", Normal with { IsOwnWindow = true } },
        { "不可激活", Normal with { NoActivate = true } },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Non_switchable_windows_are_rejected(string because, WindowProbe probe)
        => Assert.True(WindowFilter.Reject(probe) is not null, because);

    [Fact]
    public void App_window_style_overrides_tool_window_and_owner()
        => Assert.Null(WindowFilter.Reject(Normal with { ToolWindow = true, HasOwner = true, AppWindow = true }));

    [Fact]
    public void Unknown_desktop_membership_does_not_reject()
        => Assert.Null(WindowFilter.Reject(Normal with { OnCurrentDesktop = null }));
}
