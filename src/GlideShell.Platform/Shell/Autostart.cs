using Microsoft.Win32;

namespace GlideShell.Platform.Shell;

/// <summary>开机自启：HKCU\...\Run（当前用户，无需管理员权限）。P7 改为 UIAccess 签名版本时换成计划任务。</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GlideShell";

    public static string Command(string exePath) => $"\"{exePath}\" --autostart";

    public static bool IsEnabled(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string v && v.Equals(Command(exePath), StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(string exePath, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command(exePath), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
