using System.Runtime.InteropServices;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>
/// 低级键盘钩子（只在总览展开时安装）：覆盖层不抢焦点，键盘靠它拦截 Esc / Enter 等。
/// 回调在安装线程（UI 线程）的消息循环里执行，必须极快返回；返回 true 表示吞掉该按键。
/// </summary>
public sealed unsafe class KeyboardHook : IDisposable
{
    private static KeyboardHook? s_active;
    private readonly Func<uint, bool> _onKeyDown;
    private nint _hook;

    /// <param name="onKeyDown">参数为虚拟键码；返回 true 吞掉按键。</param>
    public KeyboardHook(Func<uint, bool> onKeyDown)
    {
        _onKeyDown = onKeyDown;
    }

    public bool IsInstalled => _hook != 0;

    public void Install()
    {
        if (_hook != 0) return;
        s_active = this;
        _hook = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, &Proc, Kernel32.GetModuleHandleW(null), 0);
    }

    public void Uninstall()
    {
        if (_hook == 0) return;
        User32.UnhookWindowsHookEx(_hook);
        _hook = 0;
        if (ReferenceEquals(s_active, this)) s_active = null;
    }

    public void Dispose() => Uninstall();

    [UnmanagedCallersOnly]
    private static nint Proc(int code, nint wParam, nint lParam)
    {
        var self = s_active;
        if (code >= 0 && self is not null && (wParam == (nint)User32.WM_KEYDOWN || wParam == (nint)User32.WM_SYSKEYDOWN))
        {
            try
            {
                if (self._onKeyDown(((User32.KBDLLHOOKSTRUCT*)lParam)->vkCode)) return 1;
            }
            catch
            {
                // 钩子回调里不能抛出；按键照常传递
            }
        }
        return User32.CallNextHookEx(0, code, wParam, lParam);
    }
}
