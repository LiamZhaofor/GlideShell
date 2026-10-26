using System.Collections.Concurrent;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>
/// UI 线程的隐藏主窗口 + 跨线程调度。输入线程、渲染线程要做「重活」或碰窗口时，都用 <see cref="Post"/> 投递到这里。
/// 用普通的隐藏顶层窗口而不是消息专用窗口：托盘图标需要收到 TaskbarCreated 等广播消息。
/// </summary>
public sealed unsafe class UiThread : NativeWindow
{
    /// <summary>隐藏主窗口的类名（<see cref="InstanceControl"/> 按它找到运行实例）。</summary>
    public const string WindowClass = "GlideShell.Ui";

    private const uint WM_DISPATCH = User32.WM_APP + 1;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly int _threadId;
    private readonly Dictionary<uint, Func<nint, nint, nint?>> _handlers = [];

    public UiThread()
    {
        _threadId = Environment.CurrentManagedThreadId;
        Create(WindowClass, User32.WS_POPUP, User32.WS_EX_TOOLWINDOW_U, 0, 0, 0, 0);
    }

    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>任意线程调用：在 UI 线程上异步执行。</summary>
    public void Post(Action action)
    {
        _queue.Enqueue(action);
        User32.PostMessageW(Handle, WM_DISPATCH, 0, 0);
    }

    /// <summary>任意线程调用：<paramref name="delayMs"/> 毫秒后在 UI 线程上执行。</summary>
    public void PostDelayed(int delayMs, Action action)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            Post(action);
        }, null, delayMs, Timeout.Infinite);
    }

    /// <summary>为某个消息挂处理器（托盘回调、热键等）；处理器返回 null 表示继续默认处理。</summary>
    public void OnMessage(uint message, Func<nint, nint, nint?> handler) => _handlers[message] = handler;

    /// <summary>运行消息循环，直到 <see cref="Quit"/>。</summary>
    public void Run()
    {
        User32.MSG msg;
        while (User32.GetMessageW(&msg, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(&msg);
            User32.DispatchMessageW(&msg);
        }
    }

    public void Quit() => Post(() => User32.PostQuitMessage(0));

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_DISPATCH)
        {
            while (_queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Error("UI 线程任务异常", ex);
                }
            }
            return 0;
        }
        return _handlers.TryGetValue(msg, out var handler) ? handler(wParam, lParam) : null;
    }
}
