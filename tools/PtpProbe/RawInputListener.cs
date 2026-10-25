using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GlideShell.Core.Input;
using GlideShell.Core.Tracing;
using static GlideShell.Tools.PtpProbe.Native;

namespace GlideShell.Tools.PtpProbe;

/// <summary>
/// 专用线程 + 消息窗口接收 PTP 原始输入（RIDEV_INPUTSINK，前台在别的程序也能收到），
/// 每份 HID 报文完整解析后入队，由调用方线程取走。到达时刻在 WM_INPUT 处理瞬间取 QPC。
/// </summary>
internal sealed class RawInputListener : IDisposable
{
    private const ushort PageDigitizer = 0x0D, PageGeneric = 0x01, PageButton = 0x09;
    private const ushort UsageTipSwitch = 0x42, UsageConfidence = 0x47, UsageContactId = 0x51;
    private const ushort UsageContactCount = 0x54, UsageScanTime = 0x56, UsageX = 0x30, UsageY = 0x31;

    // 窗口过程委托必须一直被引用，否则 GC 回收后系统回调会崩
    private static RawInputListener? s_current;
    private static readonly WndProc s_proc = (h, m, w, l) => s_current?.Proc(h, m, w, l) ?? DefWindowProcW(h, m, w, l);

    private readonly PtpDevice _device;
    private readonly bool _includeMouse;
    private readonly ConcurrentQueue<(long Qpc, RawReport Report)> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ushort[] _usages = new ushort[32];
    private readonly uint _headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
    private IntPtr _buffer;
    private uint _bufferSize;
    private IntPtr _hwnd;

    public bool Registered { get; private set; }
    public string? Error { get; private set; }
    public long ParseFailures;
    public long OtherDeviceReports;
    public long MouseEvents;

    public RawInputListener(PtpDevice device, bool includeMouse = false)
    {
        if (s_current is not null) throw new InvalidOperationException("同一时间只支持一个监听器");
        s_current = this;
        _device = device;
        _includeMouse = includeMouse;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "RawInput" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(3))) Error ??= "输入线程启动超时";
    }

    public bool TryDequeue(out long qpc, out RawReport report)
    {
        if (_queue.TryDequeue(out var item))
        {
            (qpc, report) = item;
            return true;
        }
        qpc = 0;
        report = null!;
        return false;
    }

    public void Clear() => _queue.Clear();

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_APP, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
        s_current = null;
    }

    private void ThreadMain()
    {
        try
        {
            string className = $"GlideShell.PtpProbe.{Environment.ProcessId}";
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = className,
            };
            if (RegisterClassExW(ref wc) == 0)
            {
                Error = $"RegisterClassEx 失败（{Marshal.GetLastWin32Error()}）";
                return;
            }
            _hwnd = CreateWindowExW(0, className, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Error = $"CreateWindowEx 失败（{Marshal.GetLastWin32Error()}）";
                return;
            }

            var devices = new List<RAWINPUTDEVICE>
            {
                new() { usUsagePage = PageDigitizer, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            };
            if (_includeMouse)
                devices.Add(new() { usUsagePage = PageGeneric, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd });
            Registered = RegisterRawInputDevices(devices.ToArray(), (uint)devices.Count, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            if (!Registered) Error = $"RegisterRawInputDevices 失败（{Marshal.GetLastWin32Error()}）";
        }
        finally
        {
            _ready.Set();
        }

        if (!Registered) return;
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        var remove = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = PageDigitizer, usUsage = 0x05, dwFlags = RIDEV_REMOVE },
            new RAWINPUTDEVICE { usUsagePage = PageGeneric, usUsage = 0x02, dwFlags = RIDEV_REMOVE },
        };
        RegisterRawInputDevices(remove, _includeMouse ? 2u : 1u, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        DestroyWindow(_hwnd);
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            OnInput(lParam);
            return DefWindowProcW(hwnd, msg, wParam, lParam); // WM_INPUT 必须交给 DefWindowProc 做清理
        }
        if (msg == WM_APP)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnInput(IntPtr hRawInput)
    {
        long qpc = Stopwatch.GetTimestamp();
        uint size = 0;
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, _headerSize) != 0 || size == 0)
        {
            Interlocked.Increment(ref ParseFailures);
            return;
        }
        if (size > _bufferSize)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _bufferSize = Math.Max(size, 1024);
            _buffer = Marshal.AllocHGlobal((int)_bufferSize);
        }
        if (GetRawInputData(hRawInput, RID_INPUT, _buffer, ref size, _headerSize) != size)
        {
            Interlocked.Increment(ref ParseFailures);
            return;
        }

        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(_buffer);
        if (header.dwType == RIM_TYPEMOUSE)
        {
            Interlocked.Increment(ref MouseEvents);
            return;
        }
        if (header.dwType != RIM_TYPEHID) return;
        if (header.hDevice != _device.Handle)
        {
            Interlocked.Increment(ref OtherDeviceReports);
            return;
        }

        var hid = Marshal.PtrToStructure<RAWHID>(_buffer + (int)_headerSize);
        long total = (long)hid.dwSizeHid * hid.dwCount;
        if (hid.dwSizeHid == 0 || total <= 0 || total > size)
        {
            Interlocked.Increment(ref ParseFailures);
            return;
        }
        // 报文字节在 RAWINPUT 末尾；dwCount>1 时一次 WM_INPUT 打包了多份报文，逐份记录
        IntPtr first = _buffer + (int)(size - total);
        for (int i = 0; i < hid.dwCount; i++)
        {
            var report = Parse(first + (int)(hid.dwSizeHid * i), hid.dwSizeHid);
            if (report is null) Interlocked.Increment(ref ParseFailures);
            else _queue.Enqueue((qpc, report));
        }
    }

    private RawReport? Parse(IntPtr report, uint length)
    {
        IntPtr pp = _device.Preparsed;
        var input = HidpReportType.Input;
        // 两个都取不到 = 不是触点报文（例如其他 report id），跳过
        bool hasCount = HidP_GetUsageValue(input, PageDigitizer, 0, UsageContactCount, out uint cc, pp, report, length) == HIDP_STATUS_SUCCESS;
        bool hasScan = HidP_GetUsageValue(input, PageDigitizer, 0, UsageScanTime, out uint scan, pp, report, length) == HIDP_STATUS_SUCCESS;
        if (!hasCount && !hasScan) return null;

        int buttons = 0;
        uint n = (uint)_usages.Length;
        if (HidP_GetUsages(input, PageButton, 0, _usages, ref n, pp, report, length) == HIDP_STATUS_SUCCESS)
        {
            for (int i = 0; i < n; i++)
                if (_usages[i] is >= 1 and <= 31) buttons |= 1 << (_usages[i] - 1);
        }

        var slots = new RawContact[_device.Slots.Length];
        for (int s = 0; s < slots.Length; s++)
        {
            ushort lc = _device.Slots[s];
            HidP_GetUsageValue(input, PageDigitizer, lc, UsageContactId, out uint id, pp, report, length);
            HidP_GetUsageValue(input, PageGeneric, lc, UsageX, out uint x, pp, report, length);
            HidP_GetUsageValue(input, PageGeneric, lc, UsageY, out uint y, pp, report, length);
            bool tip = false, confidence = false;
            n = (uint)_usages.Length;
            if (HidP_GetUsages(input, PageDigitizer, lc, _usages, ref n, pp, report, length) == HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < n; i++)
                {
                    if (_usages[i] == UsageTipSwitch) tip = true;
                    else if (_usages[i] == UsageConfidence) confidence = true;
                }
            }
            slots[s] = new RawContact((int)id, tip, confidence, (int)x, (int)y);
        }
        return new RawReport(0, (int)scan, (int)cc, buttons, slots);
    }
}
