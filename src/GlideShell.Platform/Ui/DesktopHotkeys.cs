using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>
/// 接管 Ctrl+Win+←/→（系统切换虚拟桌面的快捷键）：在低级键盘钩子里吞掉方向键，交给 <c>onStep</c>（−1 左 / +1 右）。
/// 吞掉的方向键松开时也一起吞掉；再补发一个未分配的虚拟键（0xE8），否则系统看到 Win 按下又松开、中间没有别的键，会弹出开始菜单。
/// <c>onStep</c> 返回 false 表示这次不接管（例如功能关闭、暂停），按键照常交给系统。
/// </summary>
public sealed unsafe class DesktopHotkeys : IDisposable
{
    private const uint VkLeft = 0x25, VkRight = 0x27, VkControl = 0x11, VkLWin = 0x5B, VkRWin = 0x5C, VkShift = 0x10, VkAlt = 0x12;
    private const ushort VkUnassigned = 0xE8;

    private readonly KeyboardHook _hook;
    private readonly Func<int, bool> _onStep;
    private uint _swallowed; // 吞掉了按下的那个方向键：它的松开也要吞

    public DesktopHotkeys(Func<int, bool> onStep)
    {
        _onStep = onStep;
        _hook = new KeyboardHook(OnKey);
        _hook.Install();
    }

    private bool OnKey(uint vk, bool down)
    {
        if (vk is not (VkLeft or VkRight)) return false;
        if (!down)
        {
            if (_swallowed != vk) return false;
            _swallowed = 0;
            return true;
        }
        bool ctrl = Pressed(VkControl), win = Pressed(VkLWin) || Pressed(VkRWin);
        if (!ctrl || !win || Pressed(VkShift) || Pressed(VkAlt)) return false;
        if (!_onStep(vk == VkLeft ? -1 : 1)) return false;
        _swallowed = vk;
        SuppressStartMenu();
        return true;
    }

    private static bool Pressed(uint vk) => User32.GetAsyncKeyState((int)vk) < 0;

    private static void SuppressStartMenu()
    {
        var inputs = stackalloc User32.INPUT[2];
        inputs[0] = new User32.INPUT { type = User32.INPUT_KEYBOARD, ki = new User32.KEYBDINPUT { wVk = VkUnassigned } };
        inputs[1] = new User32.INPUT { type = User32.INPUT_KEYBOARD, ki = new User32.KEYBDINPUT { wVk = VkUnassigned, dwFlags = User32.KEYEVENTF_KEYUP } };
        User32.SendInput(2, inputs, sizeof(User32.INPUT));
    }

    public void Dispose() => _hook.Dispose();
}
