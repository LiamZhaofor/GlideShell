using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>托盘菜单项。<see cref="Text"/> 为 null 表示分隔线。</summary>
public sealed record TrayMenuItem(string? Text, Action? OnClick = null, bool Checked = false, bool Enabled = true)
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

    /// <summary>气泡通知（Windows 11 显示为系统通知）。</summary>
    public void ShowBalloon(string title, string text, bool warning = false)
    {
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
        else if (evt is User32.WM_CONTEXTMENU or User32.WM_RBUTTONUP)
        {
            int x = (short)(wParam & 0xFFFF), y = (short)((wParam >> 16) & 0xFFFF);
            ShowMenu(x, y);
        }
        return 0;
    }

    private void ShowMenu(int x, int y)
    {
        var items = _menu();
        nint menu = User32.CreatePopupMenu();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Text is null) User32.AppendMenuW(menu, User32.MF_SEPARATOR, 0, null);
                else
                {
                    uint flags = User32.MF_STRING | (it.Checked ? User32.MF_CHECKED : 0) | (it.Enabled ? 0 : User32.MF_GRAYED);
                    User32.AppendMenuW(menu, flags, (nuint)(i + 1), it.Text);
                }
            }
            // 弹出菜单前必须让本窗口成为前台，否则点菜单外面菜单不会消失（Win32 文档的要求）
            User32.SetForegroundWindow(_ui.Handle);
            int cmd = User32.TrackPopupMenuEx(menu, User32.TPM_RIGHTBUTTON | User32.TPM_RETURNCMD | User32.TPM_NONOTIFY, x, y, _ui.Handle, 0);
            User32.PostMessageW(_ui.Handle, User32.WM_NULL, 0, 0);
            if (cmd > 0 && cmd <= items.Count) items[cmd - 1].OnClick?.Invoke();
        }
        finally
        {
            User32.DestroyMenu(menu);
        }
    }
}

/// <summary>应用图标：优先用 exe 内嵌的图标（资源 ID 32512），没有时用系统默认图标。</summary>
internal static unsafe class AppIcon
{
    private static nint s_handle;

    public static nint Handle
    {
        get
        {
            if (s_handle != 0) return s_handle;
            s_handle = User32.LoadIconW(Kernel32.GetModuleHandleW(null), User32.IDI_APPLICATION);
            if (s_handle == 0) s_handle = User32.LoadIconW(0, User32.IDI_APPLICATION);
            return s_handle;
        }
    }
}
