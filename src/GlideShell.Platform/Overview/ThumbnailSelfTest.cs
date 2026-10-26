using GlideShell.Core.Geometry;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;
using GlideShell.Platform.Ui;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 缩略图后端自检（PtpProbe thumbnails 调用）：屏幕左下角显示一个 320×200 的测试窗口约 1 秒，
/// 用共享缩略图视觉（或经典缩略图）把一个左红右蓝的测试源缩小画进去，读回屏幕像素判断是否真的画出来了。
/// 不抢焦点、不进任务栏。必须在有消息循环的线程（STA 主线程）上调用。
/// </summary>
public static unsafe partial class ThumbnailSelfTest
{
    private const int W = 320, H = 200;

    public static bool Run(bool shared, Action<string> log, bool fadeProbe = false)
    {
        SharedThumbnailBackend? backend = null;
        string Mem()
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return $"私有 {p.PrivateMemorySize64 / 1048576.0:F1}MB / 工作集 {p.WorkingSet64 / 1048576.0:F1}MB";
        }
        if (shared)
        {
            log($"创建后端前：{Mem()}");
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            backend = SharedThumbnailBackend.TryCreate(out string status);
            log($"{status}（{System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0}ms）；创建后端后：{Mem()}");
            if (backend is null) return false;
        }

        var area = Shell.Monitors.Enumerate().FirstOrDefault(m => m.IsPrimary)?.WorkArea ?? new RectD(0, 0, 1280, 800);
        int x = (int)area.Left + 16, y = (int)area.Bottom - H - 16;
        using var source = new TestWindow(x + W + 8, y, paint: true); // 测试源也要在屏幕上：完全在屏幕外的窗口 DWM 不一定有画面
        using var dest = new TestWindow(x, y, paint: false);
        Pump(150);

        OverlayComposition? composition = null;
        IThumbnailLayer? layer = null;
        try
        {
            var windows = new List<(int, nint)> { (0, source.Handle) };
            var workArea = new RectD(x, y, W, H);
            if (backend is not null)
            {
                composition = OverlayComposition.Create(backend, dest.Handle, out int hr);
                if (composition is null)
                {
                    log($"创建 DComp 目标失败 0x{hr:X8}");
                    return false;
                }
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                layer = SharedThumbnailLayer.Register(backend, composition, dest.Handle, workArea, 1.0, windows, null, out hr);
                log($"注册 1 个窗口（含两种阴影）{System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds:F0}ms；之后：{Mem()}");
                if (layer is null)
                {
                    log($"创建共享缩略图视觉失败 0x{hr:X8}");
                    return false;
                }
            }
            else
            {
                layer = ThumbnailLayer.Register(dest.Handle, workArea, windows, null);
            }

            // 缩小一半放在测试窗口中间：同时验证缩放、平移与不透明度
            var rect = new RectD(x + 80, y + 50, 160, 100);
            int applied = layer.Apply([rect], [1.0], [1.0], 0); // 活动阴影
            for (int i = 0; i < 4; i++) Dwmapi.DwmFlush();
            Pump(100);

            uint left = ScreenPixel(x + 110, y + 100), right = ScreenPixel(x + 210, y + 100), outside = ScreenPixel(x + 20, y + 20);
            bool red = (left & 0xFF) > 180 && ((left >> 16) & 0xFF) < 80;
            bool blue = ((right >> 16) & 0xFF) > 180 && (right & 0xFF) < 80;
            uint below = ScreenPixel(x + 160, y + 158); // 缩略图下沿往下 8px：活动阴影最深的一侧
            log($"Apply 0x{applied:X8} · 左半 {Rgb(left)} · 右半 {Rgb(right)} · 外部 {Rgb(outside)} · 下方阴影 {Rgb(below)}（期望：红 / 蓝 / 白 / 灰）");
            bool ok = applied == 0 && red && blue;
            if (layer.DrawsShadows)
            {
                bool shadow = (below & 0xFF) < 230 && (outside & 0xFF) > 245;
                log(shadow ? "窗口阴影：已画出" : "窗口阴影：没有画出来");
                ok &= shadow;
            }
            {
                // 别的虚拟桌面上的窗口是被外壳 cloak 的：切桌面要画它们，缩略图得照样有画面。
                // 自己的窗口只能做应用 cloak（DWMWA_CLOAK），与外壳 cloak 同属 DWM 的隐藏，作为近似验证
                int on = 1, off = 0;
                Dwmapi.DwmSetWindowAttribute(source.Handle, 13 /* DWMWA_CLOAK */, &on, sizeof(int));
                for (int i = 0; i < 4; i++) Dwmapi.DwmFlush();
                Pump(100);
                uint cl = ScreenPixel(x + 110, y + 100), cr = ScreenPixel(x + 210, y + 100);
                bool live = (cl & 0xFF) > 180 && ((cl >> 16) & 0xFF) < 80 && ((cr >> 16) & 0xFF) > 180 && (cr & 0xFF) < 80;
                log($"源窗口被 cloak 后：左半 {Rgb(cl)} · 右半 {Rgb(cr)} → {(live ? "缩略图仍有画面" : "缩略图没有画面")}");
                Dwmapi.DwmSetWindowAttribute(source.Handle, 13, &off, sizeof(int));
                ok &= live;
            }
            if (fadeProbe)
            {
                // 实验：把目标窗口改成分层窗口并整体调透明度，看缩略图 / 共享视觉是否跟着淡出（撤层交叉淡出方案的前提）
                const int GWL_EXSTYLE = -20;
                nint ex = User32.GetWindowLongPtrW(dest.Handle, GWL_EXSTYLE);
                User32.SetWindowLongPtrW(dest.Handle, GWL_EXSTYLE, ex | 0x00080000); // WS_EX_LAYERED
                foreach (byte alpha in new byte[] { 255, 128, 0 })
                {
                    SetLayeredWindowAttributes(dest.Handle, 0, alpha, 2); // LWA_ALPHA
                    for (int i = 0; i < 4; i++) Dwmapi.DwmFlush();
                    Pump(60);
                    uint l = ScreenPixel(x + 110, y + 100), o = ScreenPixel(x + 20, y + 20);
                    log($"分层透明度 {alpha}：缩略图处 {Rgb(l)} · 黑底处 {Rgb(o)}");
                }
            }
            return ok;
        }
        finally
        {
            layer?.Unregister();
            composition?.Dispose();
            backend?.Dispose();
        }
    }

    /// <summary>
    /// 阴影对照（PtpProbe thumbnails --shadow）：白底上放一个普通窗口，分别截下它活动 / 非活动时系统画的真实阴影，
    /// 再用总览同一套代码（共享缩略图 + 自画阴影）在同一位置画一遍，逐点比较四条边的变暗比例。
    /// </summary>
    public static bool CompareShadows(Action<string> log, int sourceWidth = 600, int sourceHeight = 400, string? dumpPath = null, int? monitorIndex = null)
    {
        using var backend = SharedThumbnailBackend.TryCreate(out string status);
        if (backend is null)
        {
            log(status);
            return false;
        }
        var monitors = Shell.Monitors.Enumerate();
        var monitor = monitors.FirstOrDefault(m => m.Index == monitorIndex) ?? monitors.First(m => m.IsPrimary);
        double scale = monitor.DpiScale;
        var area = monitor.WorkArea;
        int bw = sourceWidth + 500, bh = sourceHeight + 420, bx = (int)(area.Left + (area.Width - bw) / 2), by = (int)(area.Top + (area.Height - bh) / 2);
        const uint Normal = User32.WS_CAPTION | User32.WS_SYSMENU | 0x00040000; // 标题栏 + 可调边框：有系统阴影
        using var backdrop = new PlainWindow(0xFFFFFF, User32.WS_POPUP, User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW_U | User32.WS_EX_NOACTIVATE_U, bx, by, bw, bh, activate: false);
        using var source = new PlainWindow(0x808080, Normal, User32.WS_EX_TOPMOST, bx + 250, by + 200, sourceWidth, sourceHeight, activate: true);
        using var taker = new PlainWindow(0x808080, Normal, User32.WS_EX_TOPMOST, (int)area.Left + 8, (int)area.Top + 8, 240, 160, activate: true);
        Pump(300);

        User32.RECT bounds;
        Dwmapi.DwmGetWindowAttribute(source.Handle, Dwmapi.DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, (uint)sizeof(User32.RECT));
        var vb = bounds;
        log($"显示器 #{monitor.Index}{(monitor.IsPrimary ? "（主）" : "")} · 缩放 {scale:P0} · 测试窗口可见边界 {vb.Left},{vb.Top} {vb.Width}×{vb.Height}（白底 {bx},{by} {bw}×{bh}）");

        uint[] Shot(nint foreground)
        {
            if (foreground != 0)
            {
                WindowActivator.Activate(foreground);
                Pump(400);
                if (User32.GetForegroundWindow() != foreground) log($"警告：没能切到前台 0x{foreground:X}");
            }
            for (int i = 0; i < 3; i++) Dwmapi.DwmFlush();
            Pump(80);
            return Diagnostics.ScreenCapture.Grab(bx, by, bw, bh);
        }
        var realActive = Shot(source.Handle);
        var realInactive = Shot(taker.Handle);

        using var dest = new PlainWindow(0xFFFFFF, User32.WS_POPUP, User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW_U | User32.WS_EX_NOACTIVATE_U, bx, by, bw, bh, activate: false);
        Pump(150);
        using var composition = OverlayComposition.Create(backend, dest.Handle, out int hr);
        if (composition is null)
        {
            log($"创建 DComp 目标失败 0x{hr:X8}");
            return false;
        }
        var layer = SharedThumbnailLayer.Register(backend, composition, dest.Handle, new RectD(bx, by, bw, bh), scale, [(0, source.Handle)], null, out hr);
        if (layer is null)
        {
            log($"创建共享缩略图视觉失败 0x{hr:X8}");
            return false;
        }
        try
        {
            if (dumpPath is not null) Dump(dumpPath);
            var rect = Monitors.ToRect(vb);
            layer.Apply([rect], [1.0], [1.0], 0);
            var oursActive = Shot(0);
            layer.Apply([rect], [1.0], [0.0], 0);
            var oursInactive = Shot(0);

            double worst = 0;
            foreach (var (name, real, ours) in new[] { ("活动", realActive, oursActive), ("非活动", realInactive, oursInactive) })
            {
                log($"—— {name}窗口（真实 / 自画，变暗比例）");
                foreach (var side in new[] { "下", "右", "上", "左" })
                {
                    var line = new System.Text.StringBuilder($"  {side}：");
                    foreach (int d in new[] { 1, 2, 4, 8, 12, 18, 25, 35, 50, 70, 95 })
                    {
                        int cx = (vb.Left + vb.Right) / 2 - bx, cy = (vb.Top + vb.Bottom) / 2 - by;
                        var (px, py) = side switch
                        {
                            "下" => (cx, vb.Bottom - by - 1 + d),
                            "右" => (vb.Right - bx - 1 + d, cy),
                            "上" => (cx, vb.Top - by - d),
                            _ => (vb.Left - bx - d, cy),
                        };
                        if (px < 0 || py < 0 || px >= bw || py >= bh) continue;
                        double a = Dark(real[py * bw + px]), b = Dark(ours[py * bw + px]);
                        worst = Math.Max(worst, Math.Abs(a - b));
                        line.Append($" {d}:{a:F3}/{b:F3}");
                    }
                    log(line.ToString());
                }
                // 整圈（窗口外 120px 内）的平均差：正数 = 自画的更深
                double sum = 0;
                int n = 0;
                for (int py = 0; py < bh; py++)
                    for (int px = 0; px < bw; px++)
                    {
                        int sxp = px + bx, syp = py + by;
                        bool inside = sxp >= vb.Left && sxp < vb.Right && syp >= vb.Top && syp < vb.Bottom;
                        bool near = sxp > vb.Left - 120 && sxp < vb.Right + 120 && syp > vb.Top - 120 && syp < vb.Bottom + 120;
                        if (inside || !near) continue;
                        sum += Dark(ours[py * bw + px]) - Dark(real[py * bw + px]);
                        n++;
                    }
                log($"  窗口外 120px 内平均差（自画 − 真实）：{sum / n:+0.0000;-0.0000}");
            }
            log($"逐点最大差 {worst:F3}");
            return worst < 0.03;
        }
        finally
        {
            layer.Unregister();
        }

        // 拟合用：真实阴影在窗口外的整块变暗比例（x,y 相对可见边界左上角，物理像素）
        void Dump(string path)
        {
            using var w = new StreamWriter(path);
            w.WriteLine($"# {vb.Width} {vb.Height} {scale}");
            w.WriteLine("state,x,y,dark");
            for (int py = 0; py < bh; py += 2)
                for (int px = 0; px < bw; px += 2)
                {
                    int x = px + bx - vb.Left, y = py + by - vb.Top;
                    if (x >= 0 && x < vb.Width && y >= 0 && y < vb.Height) continue;
                    w.WriteLine($"a,{x},{y},{Dark(realActive[py * bw + px]):F4}");
                    w.WriteLine($"i,{x},{y},{Dark(realInactive[py * bw + px]):F4}");
                }
        }

        static double Dark(uint argb) => 1 - ((argb >> 8) & 0xFF) / 255.0; // 白底：绿色通道的变暗比例
    }

    /// <summary>
    /// 多显示器实测用（PtpProbe testwindow）：在指定位置（物理像素）显示一个橙色的普通窗口（标题栏 + 可调边框，置顶），
    /// 保持 <paramref name="ms"/> 毫秒。例如横跨两块屏的交界，配合 GlideShell --simulate 检查覆盖层两侧是否都画出了它。
    /// </summary>
    public static void ShowTestWindow(int x, int y, int w, int h, int ms)
    {
        using var window = new PlainWindow(0x008CFF, User32.WS_CAPTION | User32.WS_SYSMENU | 0x00040000, User32.WS_EX_TOPMOST, x, y, w, h, activate: true);
        User32.SetWindowTextW(window.Handle, "GlideShell 多显示器测试窗口"); // 没有标题的窗口不进总览
        User32.SetWindowPos(window.Handle, User32.HWND_TOPMOST, x, y, w, h, 0x0040 /* SWP_SHOWWINDOW */);
        Pump(ms);
    }

    /// <summary>纯色窗口（颜色 0xBBGGRR）。</summary>
    private sealed class PlainWindow : NativeWindow
    {
        private readonly uint _color;

        public PlainWindow(uint color, uint style, uint exStyle, int x, int y, int w, int h, bool activate)
        {
            _color = color;
            Create("GlideShell.ShadowCompare", style, exStyle, x, y, w, h);
            User32.ShowWindow(Handle, activate ? 5 /* SW_SHOW */ : User32.SW_SHOWNOACTIVATE);
        }

        protected override nint? WndProc(uint msg, nint wParam, nint lParam)
        {
            if (msg == User32.WM_ERASEBKGND) return 1;
            if (msg != User32.WM_PAINT) return null;
            User32.PAINTSTRUCT ps;
            nint hdc = User32.BeginPaint(Handle, &ps);
            nint brush = Gdi32.CreateSolidBrush(_color);
            User32.FillRect(hdc, &ps.rcPaint, brush);
            Gdi32.DeleteObject(brush);
            User32.EndPaint(Handle, &ps);
            return 0;
        }
    }

    private static string Rgb(uint colorRef) => $"#{colorRef & 0xFF:X2}{(colorRef >> 8) & 0xFF:X2}{(colorRef >> 16) & 0xFF:X2}";

    private static uint ScreenPixel(int x, int y)
    {
        nint dc = GetDC(0);
        try
        {
            return GetPixel(dc, x, y);
        }
        finally
        {
            ReleaseDC(0, dc);
        }
    }

    private static void Pump(int ms)
    {
        long end = Environment.TickCount64 + ms;
        User32.MSG msg;
        while (Environment.TickCount64 < end)
        {
            while (PeekMessageW(&msg, 0, 0, 0, 1))
            {
                User32.TranslateMessage(&msg);
                User32.DispatchMessageW(&msg);
            }
            Thread.Sleep(5);
        }
    }

    /// <summary>不激活、不进任务栏的置顶小窗口；paint=true 时左红右蓝（测试源），否则黑底（测试目标）。</summary>
    private sealed class TestWindow : NativeWindow
    {
        private readonly bool _paint;

        public TestWindow(int x, int y, bool paint)
        {
            _paint = paint;
            // 测试源带标题栏与可调边框（有系统阴影的普通窗口），才会给它画阴影；目标是无边框的
            uint style = paint ? User32.WS_CAPTION | 0x00040000 /* WS_THICKFRAME */ : User32.WS_POPUP;
            Create("GlideShell.ThumbnailSelfTest", style, User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW_U | User32.WS_EX_NOACTIVATE_U, x, y, W, H);
            User32.ShowWindow(Handle, User32.SW_SHOWNOACTIVATE);
        }

        protected override nint? WndProc(uint msg, nint wParam, nint lParam)
        {
            if (msg == User32.WM_ERASEBKGND) return 1;
            if (msg != User32.WM_PAINT) return null;
            User32.PAINTSTRUCT ps;
            nint hdc = User32.BeginPaint(Handle, &ps);
            var all = new User32.RECT { Right = W, Bottom = H };
            var l = new User32.RECT { Right = W / 2, Bottom = H };
            var r = new User32.RECT { Left = W / 2, Right = W, Bottom = H };
            nint black = Gdi32.CreateSolidBrush(0), red = Gdi32.CreateSolidBrush(Gdi32.Rgb(255, 0, 0)), blue = Gdi32.CreateSolidBrush(Gdi32.Rgb(0, 0, 255));
            if (_paint)
            {
                User32.FillRect(hdc, &l, red);
                User32.FillRect(hdc, &r, blue);
            }
            else
            {
                nint white = Gdi32.CreateSolidBrush(Gdi32.Rgb(255, 255, 255)); // 白底：看得出阴影
                User32.FillRect(hdc, &all, white);
                Gdi32.DeleteObject(white);
            }
            Gdi32.DeleteObject(black);
            Gdi32.DeleteObject(red);
            Gdi32.DeleteObject(blue);
            User32.EndPaint(Handle, &ps);
            return 0;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [System.Runtime.InteropServices.LibraryImport("gdi32.dll")]
    private static partial uint GetPixel(nint dc, int x, int y);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool PeekMessageW(User32.MSG* msg, nint hwnd, uint min, uint max, uint remove);
}
