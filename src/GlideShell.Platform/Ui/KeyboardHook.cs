using System.Runtime.InteropServices;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>
/// 低级键盘钩子。可以有多个订阅者（总览展开时的 Esc / Enter，常驻的 Ctrl+Win+←/→），共用一个系统钩子：
/// 有订阅者安装时才挂上，最后一个卸下时摘掉。后安装的先收到按键。
/// 回调在安装线程（UI 线程）的消息循环里执行，必须极快返回；返回 true 表示吞掉该按键。
/// </summary>
public sealed unsafe class KeyboardHook : IDisposable
{
    private static readonly List<KeyboardHook> s_installed = [];
    private static nint s_hook;
    private readonly Func<uint, bool, bool> _onKey;

    /// <param name="onKeyDown">参数为虚拟键码（只收按下）；返回 true 吞掉按键。</param>
    public KeyboardHook(Func<uint, bool> onKeyDown) => _onKey = (vk, down) => down && onKeyDown(vk);

    /// <param name="onKey">参数为虚拟键码与是否按下（false = 松开）；返回 true 吞掉这次事件。</param>
    public KeyboardHook(Func<uint, bool, bool> onKey) => _onKey = onKey;

    public bool IsInstalled { get; private set; }

    /// <summary>系统钩子挂着（有订阅者且安装成功），诊断用。</summary>
    public static bool Active => s_hook != 0;

    public void Install()
    {
        if (IsInstalled) return;
        IsInstalled = true;
        s_installed.Add(this);
        if (s_hook == 0) s_hook = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, &Proc, Kernel32.GetModuleHandleW(null), 0);
    }

    public void Uninstall()
    {
        if (!IsInstalled) return;
        IsInstalled = false;
        s_installed.Remove(this);
        if (s_installed.Count == 0 && s_hook != 0)
        {
            User32.UnhookWindowsHookEx(s_hook);
            s_hook = 0;
        }
    }

    public void Dispose() => Uninstall();

    [UnmanagedCallersOnly]
    private static nint Proc(int code, nint wParam, nint lParam)
    {
        bool down = wParam == (nint)User32.WM_KEYDOWN || wParam == (nint)User32.WM_SYSKEYDOWN;
        bool up = wParam == (nint)0x0101 /* WM_KEYUP */ || wParam == (nint)0x0105 /* WM_SYSKEYUP */;
        if (code >= 0 && (down || up))
        {
            uint vk = ((User32.KBDLLHOOKSTRUCT*)lParam)->vkCode;
            for (int i = s_installed.Count - 1; i >= 0; i--)
            {
                if (i >= s_installed.Count) continue; // 回调里有订阅者卸下了
                try
                {
                    if (s_installed[i]._onKey(vk, down)) return 1;
                }
                catch
                {
                    // 钩子回调里不能抛出；按键照常传递
                }
            }
        }
        return User32.CallNextHookEx(0, code, wParam, lParam);
    }
}
