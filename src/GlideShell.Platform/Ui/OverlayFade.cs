using System.Runtime.InteropServices;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>
/// 撤层时的交叉淡出：真实窗口已经在覆盖层下面摆好之后，把覆盖层整体透明度在约 100ms 内降到 0 再隐藏。
/// <para>
/// 缩略图（经典与共享视觉）画不出窗口阴影，被激活的程序还会在随后几帧重绘激活态；直接撤层时这些差异一下子出现，
/// 看起来就是「选中窗口下面那层突变」（真机截图：差异集中在选中窗口的阴影一圈）。淡出让它们逐渐出现。
/// 实测分层窗口的整体透明度对其上的经典缩略图与共享视觉同样生效（PtpProbe thumbnails --fade）。
/// </para>
/// 只在撤层的那一小段用分层窗口：平时保持非分层（S4：分层窗口上的缩略图更新有问题）。在 UI 线程上同步执行。
/// </summary>
public static partial class OverlayFade
{
    public const int DefaultMs = 100;

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 2;
    private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_UPDATENOW = 0x100;

    /// <summary>把这些窗口一起淡出，结束后调用 <paramref name="hide"/>（负责隐藏），再恢复成非分层、不透明。</summary>
    public static void FadeOutThenHide(IReadOnlyList<nint> windows, Action hide, int durationMs = DefaultMs)
    {
        var styles = new nint[windows.Count];
        for (int i = 0; i < windows.Count; i++)
        {
            styles[i] = User32.GetWindowLongPtrW(windows[i], GWL_EXSTYLE);
            User32.SetWindowLongPtrW(windows[i], GWL_EXSTYLE, styles[i] | WS_EX_LAYERED);
            SetLayeredWindowAttributes(windows[i], 0, 255, LWA_ALPHA);
            // 刚改成分层窗口时内容要重画：同步画完，免得合成出一帧空白
            RedrawWindow(windows[i], 0, 0, RDW_INVALIDATE | RDW_ERASE | RDW_UPDATENOW);
        }

        long start = Environment.TickCount64;
        while (true)
        {
            double t = Math.Clamp((Environment.TickCount64 - start) / (double)durationMs, 0, 1);
            byte alpha = (byte)Math.Round(255 * (1 - t * t * (3 - 2 * t))); // smoothstep：两端平缓
            foreach (var w in windows) SetLayeredWindowAttributes(w, 0, alpha, LWA_ALPHA);
            if (t >= 1) break;
            Dwmapi.DwmFlush(); // 每个合成帧更新一次
        }

        hide();
        for (int i = 0; i < windows.Count; i++)
        {
            SetLayeredWindowAttributes(windows[i], 0, 255, LWA_ALPHA);
            User32.SetWindowLongPtrW(windows[i], GWL_EXSTYLE, styles[i]);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);
}
