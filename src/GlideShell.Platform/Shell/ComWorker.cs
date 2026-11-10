using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Shell;

/// <summary>
/// 专门做 COM 调用的后台线程（外壳文件夹枚举、UI 自动化）：按顺序执行投递来的工作，不占 UI 线程。
/// <paramref name="sta"/>：外壳对象要单线程套间；UI 自动化客户端官方建议多线程套间。
/// </summary>
public sealed class ComWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly bool _sta;

    public ComWorker(string name, bool sta)
    {
        _sta = sta;
        var thread = new Thread(Run) { IsBackground = true, Name = name };
        if (sta) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>投递一项工作；结果（或异常）通过返回的任务给出。线程已停止时任务直接取消。</summary>
    public Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued;
        try
        {
            queued = _queue.TryAdd(() =>
            {
                try
                {
                    tcs.SetResult(work());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            queued = false;
        }
        if (!queued) tcs.SetCanceled();
        return tcs.Task;
    }

    private void Run()
    {
        Ole32.CoInitializeEx(0, _sta ? 2u /* COINIT_APARTMENTTHREADED */ : Ole32.COINIT_MULTITHREADED);
        foreach (var work in _queue.GetConsumingEnumerable()) work();
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>COM 指针的小工具（按虚表下标调用的接口都用它释放）。</summary>
internal static unsafe partial class Com
{
    public static void Release(void* p)
    {
        if (p != null) ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)p)[2])(p);
    }

    public static int QueryInterface(void* p, Guid* iid, void** result)
    {
        *result = null;
        return p == null ? unchecked((int)0x80004003) : ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)(*(void***)p)[0])(p, iid, result);
    }

    /// <summary>CoTaskMem 分配的宽字符串取出后释放。</summary>
    public static string? TakeString(char* s)
    {
        if (s == null) return null;
        var text = new string(s);
        CoTaskMemFree(s);
        return text;
    }

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(void* p);

    [LibraryImport("oleaut32.dll")]
    public static partial void SysFreeString(char* s);

    [LibraryImport("oleaut32.dll")]
    public static partial char* SysAllocString(char* s);
}

/// <summary>外壳给出的「已知文件夹 GUID 开头」的路径（如 <c>{6D809377-…}\7-Zip\7zFM.exe</c>）换成完整路径。</summary>
internal static unsafe partial class KnownFolders
{
    private static readonly ConcurrentDictionary<Guid, string?> s_cache = new();

    public static string Resolve(string path)
    {
        if (path.Length < 38 || path[0] != '{' || path[37] != '}' || !Guid.TryParse(path.AsSpan(0, 38), out var id)) return path;
        string? root = s_cache.GetOrAdd(id, static g =>
        {
            char* p;
            if (SHGetKnownFolderPath(&g, 0, 0, &p) < 0) return null;
            return Com.TakeString(p);
        });
        return root is null ? path : root + path[38..];
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(Guid* id, uint flags, nint token, char** path);
}
