using GlideShell.Core.Configuration;
using GlideShell.Platform.Native;
using GlideShell.Platform.Ui.Settings;
using static GlideShell.Core.Localization.L;

namespace GlideShell.Platform.Ui;

/// <summary>
/// 设置窗口里的快捷键框：点一下进入录制，按下新的组合键就记下（Esc 取消，Backspace / Delete 清除）。
/// 录制用低级键盘钩子（后挂的先收到）：Win 组合键在到达窗口之前就会被系统拿走，钩子里才截得到；
/// 录制期间全局快捷键暂停（<see cref="GlobalHotkey.Paused"/>），按下现有的快捷键不会打开总览。
/// 窗口失去前台、点了别处、换页、关窗时停止录制。
/// </summary>
public sealed unsafe partial class SettingsWindow
{
    private string? _recording; // 正在录的快捷键框
    private Action<string>? _recordSet;
    private KeyboardHook? _recordHook;
    private uint _recordSwallowed; // 吞掉了按下的键：它的松开也吞
    private string _recordHint = "";

    /// <summary>快捷键框：<paramref name="get"/> / <paramref name="set"/> 读写 <see cref="Hotkey"/> 的文字（空串 = 不用）。</summary>
    private Item HotkeyBox(string key, Func<string> get, Action<string> set, bool enabled = true) =>
        new(ItemKind.Hotkey, key)
        {
            Enabled = enabled,
            Label = () => _recording == key ? _recordHint : get() is { Length: > 0 } text ? text.Replace("+", " + ") : T("未设置"),
            Active = () => _recording == key,
            Click = () =>
            {
                if (_recording == key) StopRecording();
                else StartRecording(key, set);
            },
        };

    private void StartRecording(string key, Action<string> set)
    {
        StopRecording();
        _recording = key;
        _recordSet = set;
        _recordHint = T("按下新的快捷键…");
        _recordSwallowed = 0;
        GlobalHotkey.Paused = true;
        _recordHook = new KeyboardHook(OnRecordKey);
        _recordHook.Install();
        User32.InvalidateRect(Handle, null, 0);
    }

    private void StopRecording()
    {
        if (_recording is null) return;
        _recording = null;
        _recordSet = null;
        _recordHook?.Dispose();
        _recordHook = null;
        GlobalHotkey.Paused = false;
        if (Handle != 0) User32.InvalidateRect(Handle, null, 0);
    }

    /// <summary>录制中的按键（UI 线程，键盘钩子里）。修饰键放过去（框里显示按着的），其余的键都吞掉。</summary>
    private bool OnRecordKey(uint vk, bool down)
    {
        if (_recording is null) return false;
        if (User32.GetForegroundWindow() != Handle)
        {
            StopRecording();
            return false;
        }
        // 钩子里系统的按键状态还没算上这一次：按下 / 松开的修饰键自己补上 / 去掉
        var mods = GlobalHotkey.CurrentModifiers();
        if (Hotkey.IsModifierKey(vk))
        {
            mods = down ? mods | ModifierOf(vk) : mods & ~ModifierOf(vk);
            _recordHint = mods == HotkeyModifiers.None ? T("按下新的快捷键…") : ModifierText(mods) + " + …";
            User32.InvalidateRect(Handle, null, 0);
            return false;
        }
        if (!down)
        {
            if (vk != _recordSwallowed) return false;
            _recordSwallowed = 0;
            return true;
        }
        _recordSwallowed = vk;
        // 吞掉的键夹在 Alt / Win 的按下与松开之间，系统会当成单按：补一个未分配的键
        if ((mods & (HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0) DesktopHotkeys.SendUnassignedKey();
        if (mods == HotkeyModifiers.None && vk == 0x1B) // Esc：取消
        {
            StopRecording();
            return true;
        }
        if (mods == HotkeyModifiers.None && vk is 0x08 or 0x2E) // Backspace / Delete：清除
        {
            Finish("");
            return true;
        }
        var hotkey = new Hotkey(mods, vk);
        if (!hotkey.IsValid)
        {
            _recordHint = T("要带 Ctrl、Alt 或 Win");
            User32.InvalidateRect(Handle, null, 0);
            return true;
        }
        Finish(hotkey.ToString());
        return true;

        void Finish(string text)
        {
            var set = _recordSet;
            StopRecording();
            set?.Invoke(text);
            Changed();
        }
    }

    private static HotkeyModifiers ModifierOf(uint vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
        0x11 or 0xA2 or 0xA3 => HotkeyModifiers.Ctrl,
        0x12 or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
        0x5B or 0x5C => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    private static string ModifierText(HotkeyModifiers mods)
    {
        var parts = new List<string>(4);
        if ((mods & HotkeyModifiers.Win) != 0) parts.Add("Win");
        if ((mods & HotkeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
        if ((mods & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((mods & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        return string.Join(" + ", parts);
    }
}
