using System.Runtime.InteropServices;
using GlideShell.Core.Pointer;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Input;

/// <summary>
/// 三指拖移的鼠标注入（SendInput）。移动用虚拟桌面绝对坐标，不受系统「指针速度 / 提高指针精确度」影响，
/// 手感完全由 <see cref="DragController"/> 的增益决定。
/// 注意：没有 UIAccess 时，注入的输入到不了以管理员身份运行的窗口（UIPI），P7 签名后解决。
/// </summary>
public sealed unsafe partial class MouseInjector : IDragHost
{
    private const uint InputMouse = 0;
    private const uint MouseeventfMove = 0x0001, MouseeventfLeftDown = 0x0002, MouseeventfLeftUp = 0x0004;
    private const uint MouseeventfAbsolute = 0x8000, MouseeventfVirtualDesk = 0x4000;
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public uint Type;
        private uint _pad;
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }

    private bool _pressed;
    private double _x, _y; // 当前指针位置（小数，避免逐帧取整漂移）

    public void Press()
    {
        User32.POINT p;
        User32.GetCursorPos(&p);
        _x = p.X;
        _y = p.Y;
        Send(MouseeventfLeftDown, 0, 0);
        _pressed = true;
    }

    public void MoveBy(double dxPx, double dyPx)
    {
        int left = GetSystemMetrics(SmXVirtualScreen), top = GetSystemMetrics(SmYVirtualScreen);
        int width = Math.Max(2, GetSystemMetrics(SmCxVirtualScreen)), height = Math.Max(2, GetSystemMetrics(SmCyVirtualScreen));
        _x = Math.Clamp(_x + dxPx, left, left + width - 1);
        _y = Math.Clamp(_y + dyPx, top, top + height - 1);
        // 绝对坐标归一化到 0..65535（覆盖整个虚拟桌面）
        int nx = (int)Math.Round((_x - left) * 65535.0 / (width - 1));
        int ny = (int)Math.Round((_y - top) * 65535.0 / (height - 1));
        Send(MouseeventfMove | MouseeventfAbsolute | MouseeventfVirtualDesk, nx, ny);
    }

    public void Release()
    {
        if (!_pressed) return;
        Send(MouseeventfLeftUp, 0, 0);
        _pressed = false;
    }

    private static void Send(uint flags, int dx, int dy)
    {
        var input = new MouseInput { Type = InputMouse, Dx = dx, Dy = dy, Flags = flags };
        SendInput(1, &input, sizeof(MouseInput));
    }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, MouseInput* inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
