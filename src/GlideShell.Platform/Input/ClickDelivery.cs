using System.Diagnostics;
using System.Runtime.InteropServices;
using GlideShell.Core.Configuration;
using GlideShell.Platform.Diagnostics;

namespace GlideShell.Platform.Input;

/// <summary>
/// 注入点击（三指轻点的中键、鼠标按键手势没拖开时补发的原来那一下）的发送与兜底。
/// <para>
/// 为什么要兜底：新型待机唤醒之后，长期运行的进程注入输入偶尔会一直被拒（SendInput 返回 0、错误 5），
/// 前台是普通窗口也一样；同一时刻新启动的进程却能正常注入。Windows 自带的三指轻点中键也会这样失灵
/// （重启资源管理器才好），GlideShell 的中键也一样（2026-11-17 实测：唤醒前全成功、唤醒后全失败，重启就好）。
/// </para>
/// 直接发送失败时（在新线程上做，不卡调用方的输入线程 / 钩子线程）按顺序试：
/// <list type="number">
/// <item>换一个新线程重发（问题如果只在原线程上，这一步就够了）；</item>
/// <item>启动一个新的 GlideShell 进程（<c>--click</c>）代发；</item>
/// <item>代发成功后在本进程再探测一次（零位移的鼠标移动）：仍被拒 = 本进程坏了（不是锁屏 / UAC 切桌面那一瞬间），
/// 之后的点击交给新进程，并通知宿主找空闲时重启自己。</item>
/// </list>
/// 新进程在普通桌面上也发不出去时同样通知宿主重启（重启是已知有效的办法）；在锁屏 / 安全桌面上只记一笔。
/// 每一步用了哪种、成没成功都写日志，诊断页显示计数，下次失效时能看出问题出在线程上还是进程上。
/// </summary>
public static partial class ClickDelivery
{
    /// <summary>代发进程的命令行开关：<c>GlideShell.exe --click middle|back|forward</c>，退出码 0 = 发出去了。</summary>
    public const string HelperSwitch = "--click";

    /// <summary>开发用开关：本进程的注入一律当作被拒（错误 5），用来走一遍兜底与自动重启。代发进程不受影响。</summary>
    public const string SimulateFailureSwitch = "--simulate-inject-failure";

    private const long HelperBackoffMs = 5000; // 代发失败后这么久之内不再起进程

    private static string? s_exePath;
    private static Action? s_onProcessStuck;
    private static volatile bool s_stuck; // 已确认本进程注入被拒、新进程能发
    private static int s_stuckReported;
    private static long s_helperFailedAtMs; // 0 = 还没失败过（Environment.TickCount64，开机后从 0 往上数）
    private static int s_directFailures, s_threadRetries, s_helperClicks, s_lost;

    /// <summary>开发用：本进程的注入一律失败（见 <see cref="SimulateFailureSwitch"/>）。</summary>
    public static bool SimulateFailure { get; set; }

    /// <param name="exePath">代发用的 GlideShell.exe（UIAccess 版必须走 ShellExecute 启动）。</param>
    /// <param name="onProcessStuck">确认本进程注入坏了时调用一次（任意线程）。</param>
    public static void Configure(string exePath, Action onProcessStuck)
    {
        s_exePath = exePath;
        s_onProcessStuck = onProcessStuck;
    }

    /// <summary>直接发送失败的次数（每次都会走兜底）。</summary>
    public static int DirectFailures => Volatile.Read(ref s_directFailures);

    /// <summary>换新线程重发成功的次数。</summary>
    public static int ThreadRetries => Volatile.Read(ref s_threadRetries);

    /// <summary>新进程代发成功的次数。</summary>
    public static int HelperClicks => Volatile.Read(ref s_helperClicks);

    /// <summary>所有办法都没发出去的次数。</summary>
    public static int Lost => Volatile.Read(ref s_lost);

    /// <summary>本进程的注入已确认坏了（等宿主重启）。</summary>
    public static bool ProcessStuck => s_stuck;

    /// <summary>任意线程调用，立即返回。直接发出去了返回 true；否则兜底在后台进行。</summary>
    public static bool Send(MouseDragButton button)
    {
        if (TrySend(button, out int error))
        {
            if (s_stuck)
            {
                s_stuck = false;
                Log.Info("注入点击：本进程又能直接发送了");
            }
            return true;
        }
        int n = Interlocked.Increment(ref s_directFailures);
        if (!s_stuck) Log.Warn($"注入点击：{Name(button)}没有发出去（SendInput 错误 {error}，本次运行第 {n} 次），开始兜底");
        var thread = new Thread(() => Recover(button)) { IsBackground = true, Name = "GlideShell.ClickRecovery" };
        // 代发要走 ShellExecute，按文档在 STA 线程上调用
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return false;
    }

    private static bool TrySend(MouseDragButton button, out int error)
    {
        if (SimulateFailure)
        {
            error = 5;
            return false;
        }
        return MouseButtonGestures.SendClick(button, out error);
    }

    private static void Recover(MouseDragButton button)
    {
        try
        {
            if (!s_stuck)
            {
                if (TrySend(button, out int error))
                {
                    Interlocked.Increment(ref s_threadRetries);
                    Log.Info($"注入点击：换新线程重发{Name(button)}成功（问题在原线程上）");
                    return;
                }
                Log.Warn($"注入点击：换新线程重发{Name(button)}也失败（错误 {error}），交给新进程代发");
            }
            long failedAt = Volatile.Read(ref s_helperFailedAtMs);
            if (failedAt != 0 && Environment.TickCount64 - failedAt < HelperBackoffMs)
            {
                Interlocked.Increment(ref s_lost);
                return; // 刚失败过：不每次轻点都起进程
            }
            if (SendViaHelper(button, out string detail))
            {
                Interlocked.Increment(ref s_helperClicks);
                if (s_stuck) return;
                // 只失败了一下（锁屏 / UAC 切桌面的那一瞬间）不算坏：本进程再探测一次，仍被拒才认定
                if (Probe(out int probeError))
                {
                    Log.Info($"注入点击：新进程代发{Name(button)}成功（{detail}），本进程随后的探测也正常，当作偶发失败");
                    return;
                }
                s_stuck = true;
                Log.Warn($"注入点击：新进程代发{Name(button)}成功（{detail}），本进程探测仍被拒（错误 {probeError}），" +
                    "本进程的注入坏了：之后的点击交给新进程，并准备重启 GlideShell");
                ReportStuck();
                return;
            }
            Interlocked.Increment(ref s_lost);
            Volatile.Write(ref s_helperFailedAtMs, Environment.TickCount64);
            string desktop = InputDesktopName();
            if (desktop == "Default")
            {
                Log.Warn($"注入点击：新进程也没把{Name(button)}发出去（{detail}），而输入桌面是普通桌面，试着重启 GlideShell");
                ReportStuck();
            }
            else Log.Warn($"注入点击：新进程也没把{Name(button)}发出去（{detail}），输入桌面是「{desktop}」（锁屏 / 安全桌面），这次放弃");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref s_lost);
            Log.Error("注入点击兜底异常", ex);
        }
    }

    private static void ReportStuck()
    {
        if (Interlocked.Exchange(ref s_stuckReported, 1) == 0) s_onProcessStuck?.Invoke();
    }

    /// <summary>本进程现在能不能注入：发一个零位移的相对移动（指针不动）。</summary>
    private static unsafe bool Probe(out int error)
    {
        if (SimulateFailure)
        {
            error = 5;
            return false;
        }
        var input = new ProbeInput { Type = 0, Flags = 0x0001 /* MOUSEEVENTF_MOVE */ };
        bool ok = SendInput(1, &input, sizeof(ProbeInput)) == 1;
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    /// <summary>当前接收输入的桌面名（普通桌面是 Default，锁屏 / UAC 是 Winlogon；打不开时返回「未知」）。</summary>
    private static unsafe string InputDesktopName()
    {
        nint desktop = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (desktop == 0) return "未知";
        try
        {
            char* name = stackalloc char[64];
            return GetUserObjectInformationW(desktop, 2 /* UOI_NAME */, name, 64 * sizeof(char), out _) ? new string(name) : "未知";
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    /// <summary>启动 <c>GlideShell.exe --click …</c>，等它结束（最多 3 秒）。</summary>
    private static bool SendViaHelper(MouseDragButton button, out string detail)
    {
        if (s_exePath is null)
        {
            detail = "没有配置程序路径";
            return false;
        }
        var sw = Stopwatch.StartNew();
        // UIAccess 版用 CreateProcess 启动自己会被拒（错误 740），必须走 ShellExecute（见 AppHost.StartWatchdog）
        using var process = Process.Start(new ProcessStartInfo(s_exePath, $"{HelperSwitch} {Arg(button)}")
        {
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory,
        });
        if (process is null)
        {
            detail = "没有拿到进程";
            return false;
        }
        if (!process.WaitForExit(3000))
        {
            detail = "3 秒内没有结束";
            return false;
        }
        detail = $"退出码 {process.ExitCode}，耗时 {sw.ElapsedMilliseconds}ms";
        return process.ExitCode == 0;
    }

    /// <summary>代发进程的入口：发一次点击，退出码 0 = 两个事件都送进去了，6 = 被拒，2 = 参数不对。</summary>
    public static int RunHelper(string? arg)
    {
        MouseDragButton? button = arg switch
        {
            "middle" => MouseDragButton.Middle,
            "back" => MouseDragButton.Back,
            "forward" => MouseDragButton.Forward,
            _ => null,
        };
        if (button is null) return 2;
        return MouseButtonGestures.SendClick(button.Value, out _) ? 0 : 6;
    }

    private static string Arg(MouseDragButton button) => button switch
    {
        MouseDragButton.Middle => "middle",
        MouseDragButton.Forward => "forward",
        _ => "back",
    };

    private static string Name(MouseDragButton button) => button switch
    {
        MouseDragButton.Middle => "中键",
        MouseDragButton.Forward => "前进键",
        _ => "后退键",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeInput
    {
        public uint Type;
        private uint _pad;
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial uint SendInput(uint count, ProbeInput* inputs, int size);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetUserObjectInformationW(nint handle, int index, char* info, int length, out int needed);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);
}
