using System.Runtime.InteropServices;

namespace GlideShell.Platform.VirtualDesktops;

/// <summary>
/// 启动自检：从系统自带的 MIDL 代理 DLL 读出某个接口的方法总数（S3 方法的精简版）。
/// explorer 的虚拟桌面接口跨进程调用必须走这些代理，所以代理里登记的方法数就是本机真实的方法表大小；
/// 与布局表不符说明系统更新改了接口，整条 P5 路线禁用、交还原生四指手势。只读：LoadLibrary + GetProxyDllInfo。
/// </summary>
internal static unsafe partial class ProxyCheck
{
    private static readonly string[] ProxyDlls = ["ActXPrxy.dll", "OneCoreUAPCommonProxyStub.dll"];

    [StructLayout(LayoutKind.Sequential)]
    private struct ProxyFileInfo
    {
        public nint pProxyVtblList, pStubVtblList, pNamesArray, pDelegatedIIDs, pIIDLookupRtn;
        public ushort TableSize, TableVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CInterfaceStubHeader
    {
        public Guid* piid;
        public nint pServerInfo;
        public uint DispatchTableCount;
        public nint pDispatchTable;
    }

    /// <summary>接口的方法总数（含 IUnknown）；代理里找不到返回 null。</summary>
    public static int? MethodCount(Guid iid)
    {
        foreach (var dll in ProxyDlls)
        {
            nint module = LoadLibraryW(Path.Combine(Environment.SystemDirectory, dll));
            if (module == 0) continue;
            nint fn = GetProcAddress(module, "GetProxyDllInfo");
            if (fn == 0) continue;
            nint* infos;
            Guid* clsid;
            ((delegate* unmanaged<nint**, Guid**, void>)fn)(&infos, &clsid);
            for (int i = 0; infos[i] != 0; i++)
            {
                var pfi = (ProxyFileInfo*)infos[i];
                var stubs = (nint*)pfi->pStubVtblList;
                for (int j = 0; j < pfi->TableSize; j++)
                {
                    var header = (CInterfaceStubHeader*)stubs[j];
                    if (*header->piid == iid) return (int)header->DispatchTableCount;
                }
            }
        }
        return null;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryW(string path);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string name);
}
