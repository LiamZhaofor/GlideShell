using GlideShell.Core.Geometry;
using GlideShell.Platform.Native;
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

    public static bool Run(bool shared, Action<string> log)
    {
        SharedThumbnailBackend? backend = null;
        if (shared)
        {
            backend = SharedThumbnailBackend.TryCreate(out string status);
            log(status);
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
                layer = SharedThumbnailLayer.Register(backend, composition, dest.Handle, workArea, windows, null, out hr);
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
            int applied = layer.Apply([rect], [1.0], 0);
            for (int i = 0; i < 4; i++) Dwmapi.DwmFlush();
            Pump(100);

            uint left = ScreenPixel(x + 110, y + 100), right = ScreenPixel(x + 210, y + 100), outside = ScreenPixel(x + 20, y + 20);
            bool red = (left & 0xFF) > 180 && ((left >> 16) & 0xFF) < 80;
            bool blue = ((right >> 16) & 0xFF) > 180 && (right & 0xFF) < 80;
            log($"Apply 0x{applied:X8} · 左半 {Rgb(left)} · 右半 {Rgb(right)} · 外部 {Rgb(outside)}（期望：红 / 蓝 / 黑）");
            return applied == 0 && red && blue;
        }
        finally
        {
            layer?.Unregister();
            composition?.Dispose();
            backend?.Dispose();
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
            Create("GlideShell.ThumbnailSelfTest", User32.WS_POPUP, User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW_U | User32.WS_EX_NOACTIVATE_U, x, y, W, H);
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
            else User32.FillRect(hdc, &all, black);
            Gdi32.DeleteObject(black);
            Gdi32.DeleteObject(red);
            Gdi32.DeleteObject(blue);
            User32.EndPaint(Handle, &ps);
            return 0;
        }
    }

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
