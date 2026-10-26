using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.VirtualDesktops;

/// <summary>虚拟桌面状态快照：桌面按 explorer 的顺序排列（就是任务视图里从左到右的顺序）。</summary>
public sealed record DesktopState(IReadOnlyList<Guid> Desktops, int CurrentIndex)
{
    public static DesktopState Empty { get; } = new([], -1);

    public int Count => Desktops.Count;

    public Guid? Current => CurrentIndex >= 0 && CurrentIndex < Desktops.Count ? Desktops[CurrentIndex] : null;

    /// <summary>相对当前桌面偏移 <paramref name="offset"/> 的桌面（-1 = 左，+1 = 右）；不存在返回 null。</summary>
    public Guid? Neighbor(int offset)
    {
        int i = CurrentIndex + offset;
        return CurrentIndex >= 0 && i >= 0 && i < Desktops.Count ? Desktops[i] : null;
    }
}

/// <summary>
/// explorer 虚拟桌面服务（未公开接口，S3 结论）。所有 COM 调用都在一条专用 MTA 线程上执行
/// （跨进程调用会阻塞 5–13ms，不能放在输入或渲染线程）。启动自检不通过时 <see cref="Available"/> 为 false，
/// 调用方应整条路线禁用、把四指手势交还系统。
/// </summary>
public sealed unsafe class VirtualDesktopService : IDisposable
{
    private const int RpcDisconnected = unchecked((int)0x80010108);
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);

    private readonly BlockingCollection<Action> _work = [];
    private readonly Thread _thread;
    private readonly Action<string> _log;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Dictionary<Guid, nint> _desktops = []; // 已 AddRef 的 IVirtualDesktop，只在工作线程上访问
    private VirtualDesktopLayout? _layout;
    private nint _shell;
    private nint _manager;
    private NotificationSink? _sink;
    private volatile DesktopState _state = DesktopState.Empty;

    private VirtualDesktopService(Action<string> log)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "GlideShell.VirtualDesktops" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>自检通过、可以切换桌面。</summary>
    public bool Available { get; private set; }

    /// <summary>布局名或不可用的原因（日志 / 诊断用）。</summary>
    public string Status { get; private set; } = "未启动";

    public DesktopState State => _state;

    /// <summary>当前桌面变化（任何途径：GlideShell 自己、任务视图、Ctrl+Win+方向键……）。在 RPC 线程上回调，必须立刻返回。</summary>
    public event Action<Guid, Guid>? CurrentChanged;

    public static VirtualDesktopService Start(Action<string>? log = null)
    {
        var service = new VirtualDesktopService(log ?? (_ => { }));
        service._thread.Start();
        service._ready.Wait(TimeSpan.FromSeconds(5));
        return service;
    }

    /// <summary>无动画切换到指定桌面（S3：SwitchDesktop 本身没有动画，5–13ms 返回、一帧切完）。返回 HRESULT。</summary>
    public Task<int> SwitchToAsync(Guid desktop)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (!EnsureConnected() || !_desktops.TryGetValue(desktop, out nint d))
            {
                tcs.SetResult(unchecked((int)0x80004005));
                return;
            }
            int hr = ((delegate* unmanaged<nint, nint, int>)Slot(_manager, _layout!.SwitchDesktop))(_manager, d);
            if (hr is RpcDisconnected or RpcServerUnavailable) Disconnect();
            tcs.SetResult(hr);
        });
        return tcs.Task;
    }

    /// <summary>重新读取桌面列表（桌面增删、顺序变化后）。</summary>
    public void Refresh() => Post(() =>
    {
        if (EnsureConnected()) ReadState();
    });

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    // ---------- 工作线程 ----------

    private void Post(Action action)
    {
        if (!_work.IsAddingCompleted) _work.Add(action);
    }

    private void Run()
    {
        Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED);
        try
        {
            Available = Connect();
        }
        catch (Exception ex)
        {
            Status = $"初始化异常：{ex.Message}";
            Available = false;
        }
        _log($"虚拟桌面：{Status}");
        _ready.Set();

        foreach (var action in _work.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log($"虚拟桌面工作线程异常：{ex.Message}");
            }
        }
        Disconnect();
    }

    private bool Connect()
    {
        int build = Environment.OSVersion.Version.Build;
        _layout = VirtualDesktopLayout.ForBuild(build);
        if (_layout is null)
        {
            Status = $"系统构建 {build} 没有适配的接口布局";
            return false;
        }
        int? methods = ProxyCheck.MethodCount(_layout.IidManagerInternal);
        if (methods != _layout.ManagerInternalMethodCount)
        {
            Status = $"接口自检失败：代理里方法数 {methods?.ToString() ?? "（未登记）"}，{_layout.Name} 布局应为 {_layout.ManagerInternalMethodCount}";
            return false;
        }

        Guid clsid = VirtualDesktopLayout.ClsidImmersiveShell, iidSp = VirtualDesktopLayout.IidServiceProvider;
        void* sp;
        int hr = Ole32.CoCreateInstance(&clsid, 0, 0x4 /* CLSCTX_LOCAL_SERVER */, &iidSp, &sp);
        if (hr < 0)
        {
            Status = $"连接 explorer 失败 0x{hr:X8}";
            return false;
        }
        _shell = (nint)sp;
        hr = QueryService(_shell, VirtualDesktopLayout.SidManagerInternal, _layout.IidManagerInternal, out _manager);
        if (hr < 0)
        {
            Status = $"QueryService(IVirtualDesktopManagerInternal) 失败 0x{hr:X8}";
            return false;
        }
        if (!ReadState())
        {
            Status = "接口自检失败：桌面数与桌面列表不一致";
            return false;
        }
        _sink = NotificationSink.TryRegister(this, _shell, _layout, out string? sinkProblem);
        Status = $"{_layout.Name} 布局，{_state.Count} 个桌面" + (sinkProblem is null ? "" : $"（{sinkProblem}）");
        return true;
    }

    private bool EnsureConnected()
    {
        if (_manager != 0) return true;
        Disconnect();
        Available = Connect();
        _log($"虚拟桌面重新连接：{Status}");
        return Available;
    }

    private void Disconnect()
    {
        _sink?.Dispose();
        _sink = null;
        foreach (var d in _desktops.Values) Release(d);
        _desktops.Clear();
        Release(_manager);
        Release(_shell);
        _manager = _shell = 0;
    }

    /// <summary>读桌面列表与当前桌面；互相核对（GetCount 与列表长度一致、当前桌面在列表里）。</summary>
    private bool ReadState()
    {
        var layout = _layout!;
        int count;
        int hr = ((delegate* unmanaged<nint, int*, int>)Slot(_manager, layout.GetCount))(_manager, &count);
        if (hr < 0)
        {
            if (hr is RpcDisconnected or RpcServerUnavailable) Disconnect();
            return false;
        }

        nint array;
        if (((delegate* unmanaged<nint, nint*, int>)Slot(_manager, layout.GetDesktops))(_manager, &array) < 0) return false;
        var ids = new List<Guid>();
        var fresh = new Dictionary<Guid, nint>();
        try
        {
            uint n;
            if (((delegate* unmanaged<nint, uint*, int>)Slot(array, 3))(array, &n) < 0) return false;
            Guid iid = layout.IidVirtualDesktop;
            for (uint i = 0; i < n; i++)
            {
                nint d;
                if (((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Slot(array, 4))(array, i, &iid, &d) < 0) continue;
                Guid id = GetId(d);
                ids.Add(id);
                fresh[id] = d;
            }
        }
        finally
        {
            Release(array);
        }

        nint current;
        Guid currentId = Guid.Empty;
        if (((delegate* unmanaged<nint, nint*, int>)Slot(_manager, layout.GetCurrentDesktop))(_manager, &current) >= 0)
        {
            currentId = GetId(current);
            Release(current);
        }

        foreach (var d in _desktops.Values) Release(d);
        _desktops.Clear();
        foreach (var (id, d) in fresh) _desktops[id] = d;
        int index = ids.IndexOf(currentId);
        _state = new DesktopState(ids, index);
        return count == ids.Count && index >= 0;
    }

    internal Guid GetId(nint desktop)
    {
        Guid g = Guid.Empty;
        ((delegate* unmanaged<nint, Guid*, int>)Slot(desktop, _layout!.DesktopGetId))(desktop, &g);
        return g;
    }

    // ---------- 通知（RPC 线程） ----------

    internal void OnCurrentChanged(Guid from, Guid to)
    {
        var s = _state;
        int index = -1;
        for (int i = 0; i < s.Desktops.Count; i++)
            if (s.Desktops[i] == to) index = i;
        if (index >= 0) _state = s with { CurrentIndex = index };
        else Refresh(); // 新桌面还不在缓存里
        CurrentChanged?.Invoke(from, to);
    }

    internal void OnDesktopsChanged() => Refresh();

    // ---------- COM 小工具 ----------

    internal static nint Slot(nint obj, int index) => (*(nint**)obj)[index];

    internal static void Release(nint obj)
    {
        if (obj != 0) ((delegate* unmanaged<nint, uint>)Slot(obj, 2))(obj);
    }

    internal static int QueryService(nint serviceProvider, Guid sid, Guid iid, out nint result)
    {
        nint r = 0;
        int hr = ((delegate* unmanaged<nint, Guid*, Guid*, nint*, int>)Slot(serviceProvider, 3))(serviceProvider, &sid, &iid, &r);
        result = r;
        return hr;
    }
}

/// <summary>
/// IVirtualDesktopNotification 的手写 vtable 实现（26300：11 个回调；Moved 的两个索引是 i32，Switched 多一个 enum16）。
/// 进程是 MTA，回调直接在 RPC 线程上到达，不需要消息泵。
/// </summary>
internal sealed unsafe class NotificationSink : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeObject
    {
        public nint Vtbl;
        public int RefCount;
        public nint Owner;
    }

    private static Guid s_iid;
    private static nint s_vtbl;

    private readonly VirtualDesktopService _service;
    private GCHandle _self;
    private NativeObject* _obj;
    private nint _notificationService;
    private uint _cookie;

    private NotificationSink(VirtualDesktopService service) => _service = service;

    public static NotificationSink? TryRegister(VirtualDesktopService service, nint shell, VirtualDesktopLayout layout, out string? problem)
    {
        problem = null;
        EnsureVtable(layout);
        var sink = new NotificationSink(service);
        if (VirtualDesktopService.QueryService(shell, VirtualDesktopLayout.SidNotificationService, layout.IidNotificationService, out sink._notificationService) < 0)
        {
            problem = "切换通知不可用";
            return null;
        }
        sink._self = GCHandle.Alloc(sink);
        sink._obj = (NativeObject*)NativeMemory.AllocZeroed((nuint)sizeof(NativeObject));
        sink._obj->Vtbl = s_vtbl;
        sink._obj->RefCount = 1;
        sink._obj->Owner = GCHandle.ToIntPtr(sink._self);
        uint cookie;
        int hr = ((delegate* unmanaged<nint, nint, uint*, int>)VirtualDesktopService.Slot(sink._notificationService, 3))(sink._notificationService, (nint)sink._obj, &cookie);
        if (hr < 0)
        {
            problem = $"注册切换通知失败 0x{hr:X8}";
            VirtualDesktopService.Release(sink._notificationService);
            sink._self.Free();
            return null;
        }
        sink._cookie = cookie;
        return sink;
    }

    public void Dispose()
    {
        if (_notificationService != 0)
        {
            ((delegate* unmanaged<nint, uint, int>)VirtualDesktopService.Slot(_notificationService, 4))(_notificationService, _cookie);
            VirtualDesktopService.Release(_notificationService);
            _notificationService = 0;
        }
        // explorer 可能仍持有代理引用：对象内存不释放（几十字节），只断开到托管对象的链接
        if (_obj is not null) _obj->Owner = 0;
        if (_self.IsAllocated) _self.Free();
    }

    private static void EnsureVtable(VirtualDesktopLayout layout)
    {
        if (s_vtbl != 0) return;
        s_iid = layout.IidNotification;
        var v = (nint*)NativeMemory.Alloc((nuint)(sizeof(nint) * 14));
        v[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        v[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        v[2] = (nint)(delegate* unmanaged<nint, uint>)&ReleaseImpl;
        v[3] = (nint)(delegate* unmanaged<nint, nint, int>)&Created;
        v[4] = (nint)(delegate* unmanaged<nint, nint, nint, int>)&DestroyBegin;
        v[5] = (nint)(delegate* unmanaged<nint, nint, nint, int>)&DestroyFailed;
        v[6] = (nint)(delegate* unmanaged<nint, nint, nint, int>)&Destroyed;
        v[7] = (nint)(delegate* unmanaged<nint, nint, int, int, int>)&Moved;
        v[8] = (nint)(delegate* unmanaged<nint, nint, nint, int>)&NameChanged;
        v[9] = (nint)(delegate* unmanaged<nint, nint, int>)&ViewChanged;
        v[10] = (nint)(delegate* unmanaged<nint, nint, nint, int>)&CurrentChanged;
        v[11] = (nint)(delegate* unmanaged<nint, nint, nint, int>)&WallpaperChanged;
        v[12] = (nint)(delegate* unmanaged<nint, nint, int, int>)&Switched;
        v[13] = (nint)(delegate* unmanaged<nint, nint, int>)&RemoteConnected;
        s_vtbl = (nint)v;
    }

    private static NotificationSink? Owner(nint self)
    {
        nint owner = ((NativeObject*)self)->Owner;
        return owner == 0 ? null : GCHandle.FromIntPtr(owner).Target as NotificationSink;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(nint self, Guid* riid, nint* ppv)
    {
        if (*riid == VirtualDesktopLayout.IidUnknown || *riid == s_iid)
        {
            Interlocked.Increment(ref ((NativeObject*)self)->RefCount);
            *ppv = self;
            return 0;
        }
        *ppv = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(nint self) => (uint)Interlocked.Increment(ref ((NativeObject*)self)->RefCount);

    [UnmanagedCallersOnly]
    private static uint ReleaseImpl(nint self) => (uint)Interlocked.Decrement(ref ((NativeObject*)self)->RefCount);

    [UnmanagedCallersOnly]
    private static int CurrentChanged(nint self, nint from, nint to)
    {
        try
        {
            if (Owner(self) is { } s) s._service.OnCurrentChanged(from == 0 ? Guid.Empty : s._service.GetId(from), to == 0 ? Guid.Empty : s._service.GetId(to));
        }
        catch
        {
            // 回调里不能把异常抛回 COM
        }
        return 0;
    }

    private static int DesktopsChanged(nint self)
    {
        try
        {
            Owner(self)?._service.OnDesktopsChanged();
        }
        catch
        {
            // 同上
        }
        return 0;
    }

    [UnmanagedCallersOnly] private static int Created(nint self, nint d) => DesktopsChanged(self);
    [UnmanagedCallersOnly] private static int DestroyBegin(nint self, nint d, nint f) => 0;
    [UnmanagedCallersOnly] private static int DestroyFailed(nint self, nint d, nint f) => 0;
    [UnmanagedCallersOnly] private static int Destroyed(nint self, nint d, nint f) => DesktopsChanged(self);
    [UnmanagedCallersOnly] private static int Moved(nint self, nint d, int from, int to) => DesktopsChanged(self);
    [UnmanagedCallersOnly] private static int NameChanged(nint self, nint d, nint name) => 0;
    [UnmanagedCallersOnly] private static int ViewChanged(nint self, nint view) => 0;
    [UnmanagedCallersOnly] private static int WallpaperChanged(nint self, nint d, nint path) => 0;
    [UnmanagedCallersOnly] private static int Switched(nint self, nint d, int kind) => 0;
    [UnmanagedCallersOnly] private static int RemoteConnected(nint self, nint d) => 0;
}
