using GlideShell.Core.Configuration;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>
/// 设置里可改的全局快捷键（低级键盘钩子，不用 RegisterHotKey）：
/// 用钩子才能在不接管时（例如前台是全屏游戏）把按键原样交给程序，也能用 Win 组合键。
/// 按下时问 <c>fire</c>：返回 true 就吞掉这个键（连同它的松开与按住时的连发），返回 false 照常交给系统。
/// 带 Alt 或 Win 时补发一个未分配的键，免得系统把「单按 Alt / Win」当成激活菜单栏 / 弹开始菜单（见 <see cref="DesktopHotkeys.SendUnassignedKey"/>）。
/// 回调在 UI 线程的钩子里，必须极快返回：<c>fire</c> 只做判断、把真正的工作投递出去。
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const uint VkShift = 0x10, VkControl = 0x11, VkAlt = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;

    private readonly KeyboardHook _hook;
    private readonly Func<Hotkey> _hotkey;
    private readonly Func<bool> _fire;
    private uint _swallowed; // 吞掉了按下的键：连发与松开也吞

    /// <param name="hotkey">此刻的快捷键（每次按键都问，改了设置立即生效；<see cref="Hotkey.None"/> = 不用）。</param>
    /// <param name="fire">按下了快捷键：返回 true 表示接管（吞掉按键）。</param>
    public GlobalHotkey(Func<Hotkey> hotkey, Func<bool> fire)
    {
        _hotkey = hotkey;
        _fire = fire;
        _hook = new KeyboardHook(OnKey);
        _hook.Install();
    }

    /// <summary>设置里正在录新的快捷键：所有按键都放过（交给录制）。</summary>
    public static bool Paused { get; set; }

    private bool OnKey(uint vk, bool down)
    {
        if (!down)
        {
            if (_swallowed != vk) return false;
            _swallowed = 0;
            return true;
        }
        if (_swallowed == vk) return true; // 按住不放的连发
        if (Paused) return false;
        var hotkey = _hotkey();
        if (!Matches(hotkey, vk) || !_fire()) return false;
        _swallowed = vk;
        if ((hotkey.Modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0) DesktopHotkeys.SendUnassignedKey();
        return true;
    }

    /// <summary>按下 <paramref name="vk"/> 时，连同此刻按着的修饰键是不是 <paramref name="hotkey"/>（修饰键要完全一致）。</summary>
    public static bool Matches(Hotkey hotkey, uint vk) => !hotkey.IsNone && vk == hotkey.Key && CurrentModifiers() == hotkey.Modifiers;

    /// <summary>此刻按着的修饰键。</summary>
    public static HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        if (Pressed(VkControl)) m |= HotkeyModifiers.Ctrl;
        if (Pressed(VkAlt)) m |= HotkeyModifiers.Alt;
        if (Pressed(VkShift)) m |= HotkeyModifiers.Shift;
        if (Pressed(VkLWin) || Pressed(VkRWin)) m |= HotkeyModifiers.Win;
        return m;
    }

    private static bool Pressed(uint vk) => User32.GetAsyncKeyState((int)vk) < 0;

    public void Dispose() => _hook.Dispose();
}
