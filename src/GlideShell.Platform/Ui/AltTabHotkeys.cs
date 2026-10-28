using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>
/// 接管 Alt+Tab：按住 Alt 时的 Tab / Shift+Tab 在低级键盘钩子里吞掉，交给 <c>onStep</c>（+1 下一个 / −1 上一个）。
/// 会话进行中（Alt 还按着）：←/→ 也移动一格，Esc 取消（<c>onCancel</c>），Enter 立即切换（<c>onCommit</c>），
/// Delete 关闭选中的窗口（<c>onClose</c>，同 Windows 原生），其余按键吞掉不处理；
/// 松开 Alt 时切换（<c>onCommit</c>）。Alt 的松开照常交给系统，否则系统里 Alt 的按键状态会卡在按下。
/// 开始接管时补发一个未分配的虚拟键：否则前台程序看到 Alt 按下又松开、中间没有别的键，会激活自己的菜单栏。
/// <c>onStep</c> 第一次返回 false 表示这次不接管（功能关闭、暂停、排除的程序等），Tab 照常交给系统的 Alt+Tab。
/// Ctrl+Alt+Tab（系统的「按住不放」版）不接管。
/// </summary>
public sealed class AltTabHotkeys : IDisposable
{
    private const uint VkTab = 0x09, VkEnter = 0x0D, VkEscape = 0x1B, VkDelete = 0x2E, VkLeft = 0x25, VkRight = 0x27, VkLWin = 0x5B, VkRWin = 0x5C, VkUnassigned = 0xE8;
    private const uint VkShift = 0x10, VkControl = 0x11, VkAlt = 0x12, VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLAlt = 0xA4, VkRAlt = 0xA5;

    /// <summary>会话中每隔多久确认一次 Alt 还按着（钩子超时被系统跳过时可能收不到 Alt 的松开，切换条不能一直挂着）。</summary>
    private const int AltCheckMs = 100;

    private readonly UiThread _ui;
    private readonly KeyboardHook _hook;
    private readonly Func<int, bool> _onStep;
    private readonly Action _onCommit, _onCancel, _onClose;
    private readonly HashSet<uint> _swallowed = []; // 吞掉了按下的键：它们的松开也要吞
    private bool _active;
    private bool _disposed;

    public AltTabHotkeys(UiThread ui, Func<int, bool> onStep, Action onCommit, Action onCancel, Action onClose)
    {
        _onClose = onClose;
        _ui = ui;
        _onStep = onStep;
        _onCommit = onCommit;
        _onCancel = onCancel;
        _hook = new KeyboardHook(OnKey);
        _hook.Install();
    }

    private bool OnKey(uint vk, bool down)
    {
        if (vk == VkUnassigned) return false; // 自己补发的
        if (!down && _swallowed.Remove(vk)) return true;
        if (_active) return OnSessionKey(vk, down);
        if (vk != VkTab || !down) return false;
        if (!Pressed(VkAlt) || Pressed(VkControl) || Pressed(VkLWin) || Pressed(VkRWin)) return false;
        if (!_onStep(Pressed(VkShift) ? -1 : 1)) return false;
        _active = true;
        _swallowed.Add(vk);
        DesktopHotkeys.SendUnassignedKey();
        _ui.PostDelayed(AltCheckMs, CheckAlt);
        return true;
    }

    private bool OnSessionKey(uint vk, bool down)
    {
        if (vk is VkAlt or VkLAlt or VkRAlt)
        {
            if (!down) End(commit: true);
            return false; // Alt 的按下（自动重复）与松开都照常传递
        }
        if (vk is VkShift or VkLShift or VkRShift or VkControl or VkLControl or VkRControl)
        {
            // 修饰键照常传递（Shift+Tab 要读到 Shift）。Shift 按下后补一个键（排在它后面）：只按了 Shift 又松开也不会切输入法
            if (down && vk is VkShift or VkLShift or VkRShift) DesktopHotkeys.SendUnassignedKey();
            return false;
        }
        if (!down) return false;
        _swallowed.Add(vk);
        // 吞掉的键系统看不到：Alt+Shift（常用的切换输入法热键）之间要有别的键，松开 Shift 才不会切输入法
        DesktopHotkeys.SendUnassignedKey();
        switch (vk)
        {
            case VkTab:
                _onStep(Pressed(VkShift) ? -1 : 1);
                break;
            case VkLeft:
            case VkRight:
                _onStep(vk == VkLeft ? -1 : 1);
                break;
            case VkEscape:
                End(commit: false);
                break;
            case VkEnter:
                End(commit: true);
                break;
            case VkDelete:
                _onClose(); // 会话继续：选中项落到下一个窗口
                break;
        }
        return true;
    }

    private void End(bool commit)
    {
        if (!_active) return;
        _active = false;
        if (commit) _onCommit();
        else _onCancel();
    }

    private void CheckAlt()
    {
        if (_disposed || !_active) return;
        if (!Pressed(VkAlt)) End(commit: true);
        else _ui.PostDelayed(AltCheckMs, CheckAlt);
    }

    private static bool Pressed(uint vk) => User32.GetAsyncKeyState((int)vk) < 0;

    public void Dispose()
    {
        _disposed = true;
        _hook.Dispose();
    }
}
