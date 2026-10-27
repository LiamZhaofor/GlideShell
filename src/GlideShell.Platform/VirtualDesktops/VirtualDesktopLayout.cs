namespace GlideShell.Platform.VirtualDesktops;

/// <summary>
/// 未公开虚拟桌面接口的「IID + vtable 槽位」表，按构建号选择（S3 用系统代理 DLL 的 MIDL 格式串逐槽核对过 26300）。
/// 同一个 IID 不代表同一布局：22631.3085+ 与 26100+ 的 IVirtualDesktopManagerInternal IID 相同，
/// 但 26100 在 SwitchDesktop 之后插入了一个方法，后面的槽位整体后移。所以启动时还要核对方法数（<see cref="ProxyCheck"/>）。
/// GlideShell 用 IVirtualDesktopManagerInternal 的槽 3/4/6/7/8/9（两种布局里位置相同）与新建 / 删除桌面（26100 起后移一位），
/// IVirtualDesktop 的 4/5，以及 IApplicationViewCollection 的 GetViewForHwnd（移动窗口用），其余槽位不调用。
/// </summary>
internal sealed record VirtualDesktopLayout
{
    public required string Name { get; init; }
    public required int MinBuild { get; init; }
    public required Guid IidManagerInternal { get; init; }
    public required Guid IidVirtualDesktop { get; init; }
    public required Guid IidNotification { get; init; }

    /// <summary>IVirtualDesktopManagerInternal 的方法总数（含 IUnknown），与代理 DLL 核对。</summary>
    public required int ManagerInternalMethodCount { get; init; }

    public Guid IidNotificationService { get; init; } = new("0CD45E71-D927-4F15-8B0A-8FEF525337BF");

    // IVirtualDesktopManagerInternal
    public int GetCount { get; init; } = 3;
    public int MoveViewToDesktop { get; init; } = 4; // (IApplicationView, IVirtualDesktop)
    public int GetCurrentDesktop { get; init; } = 6;
    public int GetDesktops { get; init; } = 7;
    public int GetAdjacentDesktop { get; init; } = 8;
    public int SwitchDesktop { get; init; } = 9; // 无动画；动画版（22/21）不用：并发调用可能让 explorer 崩溃
    public int CreateDesktop { get; init; } = 11; // (out IVirtualDesktop)：加在最后
    public int RemoveDesktop { get; init; } = 13; // (IVirtualDesktop 删除, IVirtualDesktop 窗口并入)

    // IApplicationViewCollection（服务 id 与接口 id 相同）
    public Guid IidApplicationViewCollection { get; init; } = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");
    public int ApplicationViewCollectionMethodCount { get; init; } = 14;
    public int GetViewForHwnd { get; init; } = 6; // (HWND, out IApplicationView)

    // IVirtualDesktop
    public int DesktopGetId { get; init; } = 4;
    public int DesktopGetName { get; init; } = 5; // HSTRING；用户没改过名时为空

    public static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    public static readonly Guid SidManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    public static readonly Guid SidNotificationService = new("A501FDEC-4A09-464C-AE4E-1B9C21B84918");
    public static readonly Guid IidServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    public static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    /// <summary>26100 / 26200 / 26300（Win11 24H2 起）。本机 26300.9711 实测。</summary>
    public static readonly VirtualDesktopLayout Win11_24H2 = new()
    {
        Name = "Win11 24H2+",
        MinBuild = 26100,
        IidManagerInternal = new("53F5CA0B-158F-4124-900C-057158060B27"),
        IidVirtualDesktop = new("3F07F4BE-B107-441A-AF0F-39D82529072C"),
        IidNotification = new("B9E5E94D-233E-49AB-AF5C-2B4541C3AADE"),
        ManagerInternalMethodCount = 25,
    };

    /// <summary>22631.3085 起的 23H2（按参考项目列出，未实测）。</summary>
    public static readonly VirtualDesktopLayout Win11_23H2 = Win11_24H2 with
    {
        Name = "Win11 23H2",
        MinBuild = 22631,
        ManagerInternalMethodCount = 24,
        CreateDesktop = 10, // 没有 26100 在 SwitchDesktop 之后插入的那个方法
        RemoveDesktop = 12,
    };

    public static VirtualDesktopLayout? ForBuild(int build)
        => build >= Win11_24H2.MinBuild ? Win11_24H2 : build >= Win11_23H2.MinBuild ? Win11_23H2 : null;
}
