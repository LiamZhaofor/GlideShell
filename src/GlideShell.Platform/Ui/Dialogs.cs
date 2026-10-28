using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>系统消息框（宿主程序集用；平台层里的窗口直接调 User32）。</summary>
public static class Dialogs
{
    /// <summary>「确定 / 取消」询问框，确定返回 true。</summary>
    public static bool Confirm(nint owner, string text, string caption)
        => User32.MessageBoxW(owner, text, caption, 0x00000001 /* MB_OKCANCEL */ | 0x00000020 /* MB_ICONQUESTION */ | 0x00010000 /* MB_SETFOREGROUND */) == 1;
}
