using System.Runtime.InteropServices;
using GlideShell.Core.Input;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Input;

/// <summary>
/// 专用输入线程：消息窗口 + Raw Input（RIDEV_INPUTSINK，前台在别的程序也能收到；RIDEV_DEVNOTIFY 跟踪热插拔）。
/// 每份报文解析后推进该设备的 <see cref="FrameAssembler"/>，凑齐的帧在本线程上同步交给 <c>onFrame</c>。
/// 渲染、窗口枚举等任何重活都不能放进回调——这条线程只负责「输入 → 手势状态」。
/// </summary>
public sealed unsafe class PtpInputThread : IDisposable
{
    private const nuint TickTimerId = 1;
    private const uint TickIntervalMs = 250;

    // 一个进程只有一条输入线程；窗口过程是静态的非托管回调，通过它找到实例
    private static PtpInputThread? s_instance;

    private readonly Action<ContactFrame> _onFrame;
    private readonly Action<long> _onTick;
    private readonly Action<long> _onDeviceLost;
    private readonly Action<string> _log;
    private readonly Dictionary<nint, (PtpDevice Device, FrameAssembler Assembler)> _devices = [];
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private nint _hwnd;
    private byte* _buffer;
    private uint _bufferSize;
    private string? _startError;

    /// <param name="onFrame">每个完整触点帧（输入线程上调用）。</param>
    /// <param name="onTick">约每 250ms 一次，参数为主机时间（µs），用于识别器的异常恢复检查。</param>
    /// <param name="onDeviceLost">正在使用的触摸板被移除时调用（应取消进行中的手势）。</param>
    public PtpInputThread(Action<ContactFrame> onFrame, Action<long>? onTick = null, Action<long>? onDeviceLost = null, Action<string>? log = null)
    {
        _onFrame = onFrame;
        _onTick = onTick ?? (_ => { });
        _onDeviceLost = onDeviceLost ?? (_ => { });
        _log = log ?? (_ => { });
    }

    /// <summary>已连接的触摸板（输入线程维护；读取时是快照）。</summary>
    public IReadOnlyList<PtpDevice> Devices
    {
        get { lock (_devices) return _devices.Values.Select(d => d.Device).ToList(); }
    }

    public long ParseFailures { get; private set; }

    /// <summary>启动并等待注册完成；失败时抛出（例如原始输入注册被拒）。</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
            throw new InvalidOperationException("一个进程只能有一条 PtpInputThread");
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "GlideShell.Input", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) _startError ??= "输入线程启动超时";
        if (_startError is not null)
        {
            Dispose();
            throw new InvalidOperationException(_startError);
        }
    }

    public void Dispose()
    {
        if (_hwnd != 0) User32.PostMessageW(_hwnd, User32.WM_CLOSE, 0, 0);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        lock (_devices)
        {
            foreach (var d in _devices.Values) d.Device.Dispose();
            _devices.Clear();
        }
        if (_buffer is not null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
        }
        Interlocked.CompareExchange(ref s_instance, null, this);
    }

    private void ThreadMain()
    {
        const string className = "GlideShell.Input";
        nint instance = Kernel32.GetModuleHandleW(null);
        fixed (char* cls = className)
        {
            try
            {
                var wc = new User32.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(User32.WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = instance,
                    lpszClassName = cls,
                };
                const int ErrorClassAlreadyExists = 1410; // 同一进程里重启输入线程时窗口类还在
                if (User32.RegisterClassExW(&wc) == 0 && Marshal.GetLastPInvokeError() != ErrorClassAlreadyExists)
                {
                    _startError = $"RegisterClassEx 失败（{Marshal.GetLastPInvokeError()}）";
                    return;
                }
                _hwnd = User32.CreateWindowExW(0, cls, null, 0, 0, 0, 0, 0, User32.HWND_MESSAGE, 0, instance, 0);
                if (_hwnd == 0)
                {
                    _startError = $"CreateWindowEx 失败（{Marshal.GetLastPInvokeError()}）";
                    return;
                }

                foreach (var dev in PtpDevice.EnumerateAll()) AddDevice(dev);
                var rid = new User32.RAWINPUTDEVICE
                {
                    usUsagePage = 0x0D,
                    usUsage = 0x05,
                    dwFlags = User32.RIDEV_INPUTSINK | User32.RIDEV_DEVNOTIFY,
                    hwndTarget = _hwnd,
                };
                if (!User32.RegisterRawInputDevices(&rid, 1, (uint)sizeof(User32.RAWINPUTDEVICE)))
                {
                    _startError = $"RegisterRawInputDevices 失败（{Marshal.GetLastPInvokeError()}）";
                    User32.DestroyWindow(_hwnd);
                    _hwnd = 0;
                    return;
                }
                User32.SetTimer(_hwnd, TickTimerId, TickIntervalMs, 0);
                _log($"输入线程就绪：{_devices.Count} 台触摸板");
            }
            finally
            {
                _ready.Set();
            }

            User32.MSG msg;
            while (User32.GetMessageW(&msg, 0, 0, 0) > 0)
            {
                User32.TranslateMessage(&msg);
                User32.DispatchMessageW(&msg);
            }

            var remove = new User32.RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = User32.RIDEV_REMOVE };
            User32.RegisterRawInputDevices(&remove, 1, (uint)sizeof(User32.RAWINPUTDEVICE));
            User32.UnregisterClassW(cls, instance);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var self = s_instance;
        try
        {
            switch (msg)
            {
                case User32.WM_INPUT:
                    self?.OnInput(lParam);
                    break; // WM_INPUT 必须交给 DefWindowProc 做清理
                case User32.WM_INPUT_DEVICE_CHANGE:
                    self?.OnDeviceChange(wParam, lParam);
                    return 0;
                case User32.WM_TIMER when wParam == (nint)TickTimerId:
                    self?._onTick(HostClock.NowUs());
                    return 0;
                case User32.WM_CLOSE:
                    User32.KillTimer(hwnd, TickTimerId);
                    User32.DestroyWindow(hwnd);
                    return 0;
                case User32.WM_DESTROY:
                    User32.PostQuitMessage(0);
                    return 0;
            }
        }
        catch (Exception ex)
        {
            // 非托管回调里抛出异常会直接终止进程：记录后吞掉，输入线程继续运行
            self?._log($"输入线程异常：{ex}");
        }
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnInput(nint hRawInput)
    {
        long hostUs = HostClock.NowUs();
        uint headerSize = (uint)sizeof(User32.RAWINPUTHEADER);
        uint size = 0;
        if (User32.GetRawInputData(hRawInput, User32.RID_INPUT, null, &size, headerSize) != 0 || size == 0) return;
        if (size > _bufferSize)
        {
            if (_buffer is not null) NativeMemory.Free(_buffer);
            _bufferSize = Math.Max(size, 1024);
            _buffer = (byte*)NativeMemory.Alloc(_bufferSize);
        }
        if (User32.GetRawInputData(hRawInput, User32.RID_INPUT, _buffer, &size, headerSize) != size)
        {
            ParseFailures++;
            return;
        }

        var header = (User32.RAWINPUTHEADER*)_buffer;
        if (header->dwType != User32.RIM_TYPEHID) return;
        if (!_devices.TryGetValue(header->hDevice, out var entry))
        {
            // 注册前就存在、但枚举时漏掉的设备：现场补上
            if (PtpDevice.TryOpen(header->hDevice) is not { } dev) return;
            entry = AddDevice(dev);
        }

        var hid = (User32.RAWHID*)(_buffer + headerSize);
        long total = (long)hid->dwSizeHid * hid->dwCount;
        if (hid->dwSizeHid == 0 || total <= 0 || total > size)
        {
            ParseFailures++;
            return;
        }
        // 报文字节在 RAWINPUT 末尾；dwCount > 1 时一次 WM_INPUT 打包了多份报文，按顺序逐份处理
        byte* first = _buffer + (size - total);
        for (uint i = 0; i < hid->dwCount; i++)
        {
            var device = entry.Device;
            if (!device.Parse(first + i * hid->dwSizeHid, hid->dwSizeHid))
            {
                ParseFailures++;
                continue;
            }
            entry.Assembler.Push(hostUs, device.ScanTime, device.ContactCount, device.Buttons, device.Slots);
        }
    }

    private void OnDeviceChange(nint change, nint handle)
    {
        if (change == User32.GIDC_ARRIVAL)
        {
            if (!_devices.ContainsKey(handle) && PtpDevice.TryOpen(handle) is { } dev)
            {
                AddDevice(dev);
                _log($"触摸板接入：VID={dev.VendorId:X4} PID={dev.ProductId:X4}");
            }
        }
        else if (change == User32.GIDC_REMOVAL && _devices.TryGetValue(handle, out var entry))
        {
            lock (_devices) _devices.Remove(handle);
            entry.Device.Dispose();
            _log($"触摸板移除：VID={entry.Device.VendorId:X4} PID={entry.Device.ProductId:X4}");
            _onDeviceLost(HostClock.NowUs());
        }
    }

    private (PtpDevice Device, FrameAssembler Assembler) AddDevice(PtpDevice dev)
    {
        var entry = (dev, new FrameAssembler(dev.Geometry, _onFrame));
        lock (_devices) _devices[dev.Handle] = entry;
        return entry;
    }
}
