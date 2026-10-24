using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace M1.MissionFlow;

// 系统三指手势的检测与（尽力）修复。
// 实证编码（2026-10-24，用户经设置界面修改后 diff 注册表得出）：
//   ThreeFingerSlideEnabled: 2 = 默认预设，0xFFFF = 自定义按方向模式（0 不是合法的"关"）
//   自定义模式：ThreeFingerUp/Down/Left/Right 按方向动作，0 = 无，1 = 切换应用，…
// 外部裸写注册表对输入栈可能不生效（设置界面走系统内部通知），因此本类定位：
//   启动时检测上下滑是否已禁用 → 未禁用则尝试写入生效编码 → 并提示用户必要时手动设置。
// 永远不会"还原"用户的手动配置。
internal static class SystemGestureGuard
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
    private const int CustomMode = unchecked((int)0xFFFF);

    /// <summary>三指上/下滑是否已被禁用（仅自定义模式且两方向都为 0 时成立）。</summary>
    public static bool IsUpDownBlocked()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key?.GetValue("ThreeFingerSlideEnabled") is not int master || master != CustomMode)
            return false; // 预设模式 = 上下滑生效
        return key.GetValue("ThreeFingerUp") is int up && up == 0
            && key.GetValue("ThreeFingerDown") is int down && down == 0;
    }

    /// <summary>
    /// 尝试把上/下滑禁为"无"（保留左右滑现状）。返回是否无需人工介入。
    /// 因输入栈可能不认外部写入，false 时调用方应引导用户去系统设置手动确认。
    /// </summary>
    public static bool TryBlockUpDown()
    {
        if (IsUpDownBlocked())
        {
            Log.W("GestureGuard: 上下滑已禁用（自定义模式）");
            return true;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            int left = key.GetValue("ThreeFingerLeft") is int l ? l : 1;
            int right = key.GetValue("ThreeFingerRight") is int r ? r : 1;
            key.SetValue("ThreeFingerSlideEnabled", CustomMode, RegistryValueKind.DWord);
            key.SetValue("ThreeFingerUp", 0, RegistryValueKind.DWord);
            key.SetValue("ThreeFingerDown", 0, RegistryValueKind.DWord);
            key.SetValue("ThreeFingerLeft", left, RegistryValueKind.DWord);
            key.SetValue("ThreeFingerRight", right, RegistryValueKind.DWord);
            BroadcastChange();
            Log.W($"GestureGuard: 已写入禁用编码（左右滑保留 Left={left} Right={right}）。若系统手势仍触发，请到 设置→蓝牙和其他设备→触摸板 手动设为'无'");
            return false;
        }
        catch (Exception ex)
        {
            Log.W($"GestureGuard: 写入失败 {ex.Message}");
            return false;
        }
    }

    private static void BroadcastChange()
    {
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        _ = SendMessageTimeout((IntPtr)0xFFFF, WM_SETTINGCHANGE, UIntPtr.Zero, KeyPath,
            SMTO_ABORTIFHUNG, 1000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
}
