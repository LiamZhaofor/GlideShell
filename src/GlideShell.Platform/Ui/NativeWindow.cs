using System.Runtime.InteropServices;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>
/// Win32 顶层窗口的托管包装：每个实例通过 GWLP_USERDATA 里的 GCHandle 找回自己，
/// 子类重写 <see cref="WndProc"/> 处理消息。窗口过程里的异常记录后吞掉（非托管回调不能抛出）。
/// 必须在同一个有消息循环的线程上创建和使用。
/// </summary>
public abstract unsafe class NativeWindow : IDisposable
{
    private static readonly HashSet<string> s_registered = [];
    private GCHandle _self;

    public nint Handle { get; private set; }

    /// <param name="classStyle">窗口类样式（例如 CS_DROPSHADOW），同一个类名只在第一次注册时生效。</param>
    protected void Create(string className, uint style, uint exStyle, int x, int y, int width, int height, nint parent = 0, uint classStyle = 0)
    {
        nint instance = Kernel32.GetModuleHandleW(null);
        fixed (char* cls = className)
        {
            lock (s_registered)
            {
                if (s_registered.Add(className))
                {
                    var wc = new User32.WNDCLASSEXW
                    {
                        cbSize = (uint)sizeof(User32.WNDCLASSEXW),
                        style = classStyle,
                        lpfnWndProc = &StaticWndProc,
                        hInstance = instance,
                        hCursor = User32.LoadCursorW(0, User32.IDC_ARROW), // 不设的话指针停在窗口上时保持进入前的样子（可能是转圈）
                        lpszClassName = cls,
                    };
                    if (User32.RegisterClassExW(&wc) == 0 && Marshal.GetLastPInvokeError() != 1410)
                        throw new InvalidOperationException($"RegisterClassEx({className}) 失败：{Marshal.GetLastPInvokeError()}");
                }
            }
            _self = GCHandle.Alloc(this);
            Handle = User32.CreateWindowExW(exStyle, cls, null, style, x, y, width, height, parent, 0, instance, GCHandle.ToIntPtr(_self));
        }
        if (Handle == 0)
        {
            _self.Free();
            throw new InvalidOperationException($"CreateWindowEx({className}) 失败：{Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>返回 null 表示交给 DefWindowProc。</summary>
    protected virtual nint? WndProc(uint msg, nint wParam, nint lParam) => null;

    protected virtual void OnDestroyed()
    {
    }

    public virtual void Dispose()
    {
        if (Handle != 0) User32.DestroyWindow(Handle);
        GC.SuppressFinalize(this);
    }

    [UnmanagedCallersOnly]
    private static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == User32.WM_NCCREATE)
            {
                var cs = (User32.CREATESTRUCTW*)lParam;
                User32.SetWindowLongPtrW(hwnd, User32.GWLP_USERDATA, cs->lpCreateParams);
            }
            nint data = User32.GetWindowLongPtrW(hwnd, User32.GWLP_USERDATA);
            if (data != 0 && GCHandle.FromIntPtr(data).Target is NativeWindow self)
            {
                if (msg == User32.WM_NCDESTROY)
                {
                    User32.SetWindowLongPtrW(hwnd, User32.GWLP_USERDATA, 0);
                    self.Handle = 0;
                    self.OnDestroyed();
                    self._self.Free();
                }
                else
                {
                    if (self.Handle == 0) self.Handle = hwnd; // WM_NCCREATE 到 CreateWindowEx 返回之间
                    if (self.WndProc(msg, wParam, lParam) is { } result) return result;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"窗口过程异常（msg=0x{msg:X4}）", ex);
        }
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
