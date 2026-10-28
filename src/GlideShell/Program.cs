using System.Diagnostics;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.NativeGestures;
using GlideShell.Platform.Ui;

namespace GlideShell;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 任何线程上没接住的异常：先把堆栈写进日志和崩溃记录（下次启动提示、诊断报告带上），再让进程照常结束；
        // 系统手势由看门狗按接管记录还原
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Error("未处理的异常，程序即将退出", ex);
            CrashRecord.Append($"未处理的异常：{ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "（未知）"}");
            Log.Flush(TimeSpan.FromSeconds(1));
        };

        // 看门狗与紧急还原不受单实例限制
        if (args.Length >= 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int pid)) return Watchdog(pid);
        if (args.Contains("--restore-native")) return RestoreNative();
        // 安装 / 卸载脚本用：让运行中的实例正常退出（会还原系统手势），等它结束
        if (args.Contains("--quit")) return InstanceControl.RequestQuit(TimeSpan.FromSeconds(10)) ? 0 : 4;
        // 让运行中的实例生成诊断报告（设置窗口打不开时也能用）
        if (args.Contains("--diagnostics")) return InstanceControl.RequestDiagnostics() ? 0 : 5;
        // 让运行中的实例一键升级到最新版本
        if (args.Contains("--upgrade")) return InstanceControl.RequestUpgrade() ? 0 : 5;

        using var instance = new Mutex(initiallyOwned: true, @"Local\GlideShell.Instance", out bool first);
        if (!first)
        {
            // 已在运行：用户再次启动（开始菜单、双击）多半是想调设置，交给运行实例打开设置窗口；
            // 开机启动的重复实例直接退出
            if (args.Contains("--autostart")) return 1;
            return InstanceControl.RequestShowSettings() ? 0 : 1;
        }

        int sim = Array.IndexOf(args, "--simulate");
        return new AppHost(sim >= 0 && sim + 1 < args.Length ? args[sim + 1] : null).Run();
    }

    /// <summary>
    /// 看门狗：等主进程退出。正常退出时主进程已经还原了系统手势（接管记录被删除）；
    /// 崩溃或被结束时记录还在，由这里按记录还原，保证用户不会失去系统手势。
    /// </summary>
    private static int Watchdog(int pid)
    {
        int? exitCode = null;
        try
        {
            using var parent = Process.GetProcessById(pid);
            _ = parent.SafeHandle; // 先拿到句柄：退出之后才打开就读不到退出码了
            parent.WaitForExit();
            exitCode = parent.ExitCode;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 主进程已经不在了，或读不到退出码
        }

        var native = new NativeGestureManager();
        bool pending = native.HasPendingTakeover;
        // 正常退出（含 --quit、注销）退出码是 0、接管记录已删；暂停时崩溃没有接管记录，靠退出码发现
        if (!pending && exitCode is null or 0) return 0;
        CrashRecord.Append($"主进程 {pid} 异常退出，退出码 {(exitCode is { } code ? $"0x{code:X8}" : "未知")}{(pending ? "，看门狗已还原系统手势" : "")}");
        if (!pending) return 0;
        Log.Start();
        Log.Warn($"主进程 {pid} 异常退出（退出码 {(exitCode is { } c ? $"0x{c:X8}" : "未知")}），看门狗还原系统手势");
        foreach (var m in native.Restore().Messages) Log.Info($"系统手势：{m}");
        Log.Flush(TimeSpan.FromSeconds(2));
        return 0;
    }

    private static int RestoreNative()
    {
        var result = new NativeGestureManager().Restore();
        Log.Start();
        foreach (var m in result.Messages) Log.Info($"系统手势（--restore-native）：{m}");
        Log.Flush(TimeSpan.FromSeconds(2));
        return result.Verified ? 0 : 3;
    }
}
