using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>托盘菜单项。<see cref="Text"/> 为 null 表示分隔线；有 <see cref="Children"/> 时是子菜单（自身不可点击）。</summary>
public sealed record TrayMenuItem(string? Text, Action? OnClick = null, bool Checked = false, bool Enabled = true,
    IReadOnlyList<TrayMenuItem>? Children = null)
{
    public static TrayMenuItem Separator { get; } = new((string?)null);
}

/// <summary>
/// 托盘图标（NOTIFYICON_VERSION_4）：左键单击执行默认动作，右键弹出菜单；Explorer 重启后自动重新添加。
/// 只能在 UI 线程上使用。
/// </summary>
public sealed unsafe class TrayIcon : IDisposable
{
    private const uint CallbackMessage = User32.WM_APP + 2;
    private const uint IconId = 1;

    private readonly UiThread _ui;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private readonly Action? _onClick;
    private readonly uint _taskbarCreated;
    private string _tip;
    private bool _added;
    private Action? _balloonClick; // 当前这条通知被点击时

    public TrayIcon(UiThread ui, string tip, Func<IReadOnlyList<TrayMenuItem>> menu, Action? onClick = null)
    {
        _ui = ui;
        _tip = tip;
        _menu = menu;
        _onClick = onClick;
        _taskbarCreated = User32.RegisterWindowMessageW("TaskbarCreated");
        ui.OnMessage(CallbackMessage, OnCallback);
        ui.OnMessage(_taskbarCreated, (_, _) => { _added = false; Add(); return 0; });
        Add();
    }

    public string Tooltip
    {
        get => _tip;
        set
        {
            _tip = value;
            var data = Data(Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
            Shell32.Shell_NotifyIconW(Shell32.NIM_MODIFY, &data);
        }
    }

    /// <summary>气泡通知（Windows 11 显示为系统通知）。<paramref name="onClick"/>：用户点了这条通知时（UI 线程）。</summary>
    public void ShowBalloon(string title, string text, bool warning = false, Action? onClick = null)
    {
        _balloonClick = onClick;
        var data = Data(Shell32.NIF_INFO);
        Shell32.CopyTo(title, data.szInfoTitle, 64);
        Shell32.CopyTo(text, data.szInfo, 256);
        data.dwInfoFlags = warning ? Shell32.NIIF_WARNING : Shell32.NIIF_INFO;
        Shell32.Shell_NotifyIconW(Shell32.NIM_MODIFY, &data);
    }

    public void Dispose()
    {
        if (!_added) return;
        var data = Data(0);
        Shell32.Shell_NotifyIconW(Shell32.NIM_DELETE, &data);
        _added = false;
    }

    private void Add()
    {
        if (_added) return;
        var data = Data(Shell32.NIF_MESSAGE | Shell32.NIF_ICON | Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
        if (!Shell32.Shell_NotifyIconW(Shell32.NIM_ADD, &data)) return; // Explorer 还没起来：等 TaskbarCreated
        data.uVersion = Shell32.NOTIFYICON_VERSION_4;
        Shell32.Shell_NotifyIconW(Shell32.NIM_SETVERSION, &data);
        _added = true;
    }

    private Shell32.NOTIFYICONDATAW Data(uint flags)
    {
        var data = new Shell32.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(Shell32.NOTIFYICONDATAW),
            hWnd = _ui.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = AppIcon.Handle,
        };
        Shell32.CopyTo(_tip, data.szTip, 128);
        return data;
    }

    // VERSION_4：lParam 低字 = 事件，wParam = 鼠标位置（屏幕坐标）
    private nint? OnCallback(nint wParam, nint lParam)
    {
        uint evt = (uint)(lParam & 0xFFFF);
        if (evt is Shell32.NIN_SELECT or Shell32.NIN_KEYSELECT) _onClick?.Invoke();
        else if (evt == Shell32.NIN_BALLOONUSERCLICK)
        {
            var click = _balloonClick;
            _balloonClick = null;
            click?.Invoke();
        }
        else if (evt is User32.WM_CONTEXTMENU or User32.WM_RBUTTONUP)
        {
            int x = (short)(wParam & 0xFFFF), y = (short)((wParam >> 16) & 0xFFFF);
            ShowMenu(x, y);
        }
        return 0;
    }

    /// <summary>右键菜单：Windows 11 风格的弹出菜单（<see cref="Settings.FluentMenu"/>），底边在指针处（托盘在屏幕底部）。</summary>
    private void ShowMenu(int x, int y)
    {
        User32.SetForegroundWindow(_ui.Handle); // 本进程拿到前台权，菜单窗口才能激活
        var chosen = Settings.FluentMenu.Show(Convert(_menu()), x, y, Settings.MenuStyle.Context, preferAbove: true);
        (chosen?.Tag as TrayMenuItem)?.OnClick?.Invoke();
    }

    private static IReadOnlyList<Settings.MenuEntry> Convert(IReadOnlyList<TrayMenuItem> items)
        => items.Select(it => it.Text is null
            ? Settings.MenuEntry.Separator
            : new Settings.MenuEntry(it.Text, it.Checked, it.Enabled, it.Children is { Count: > 0 } c ? Convert(c) : null, it)).ToList();
}

/// <summary>
/// 应用图标：优先用 exe 内嵌的图标（ApplicationIcon，资源 ID 32512），按系统小图标尺寸取最合适的那一帧
/// （LoadIcon 只给 32 像素，托盘里缩小会发虚）；没有时用系统默认图标。
/// </summary>
internal static unsafe class AppIcon
{
    private static nint s_handle;

    public static nint Handle
    {
        get
        {
            if (s_handle != 0) return s_handle;
            uint dpi = User32.GetDpiForSystem();
            s_handle = Load(User32.GetSystemMetricsForDpi(User32.SM_CXSMICON, dpi));
            return s_handle;
        }
    }

    /// <summary>按指定边长加载图标（窗口标题栏等）。调用方不必释放：LoadImage 未带 LR_SHARED 时句柄随进程存在，数量很少。</summary>
    public static nint Load(int size)
    {
        nint h = User32.LoadImageW(Kernel32.GetModuleHandleW(null), (char*)User32.IDI_APPLICATION, User32.IMAGE_ICON, size, size, 0);
        return h != 0 ? h : User32.LoadIconW(0, User32.IDI_APPLICATION);
    }
}
