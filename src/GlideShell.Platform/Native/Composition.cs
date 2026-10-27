using System.Numerics;
using System.Runtime.InteropServices;

namespace GlideShell.Platform.Native;

// DirectComposition（只用到桌面设备、目标、视觉）与未公开的共享缩略图视觉 dwmapi#147。
// COM 调用用原始 vtable：槽位取自 win32metadata（S4 spike 里 CsWin32 生成的代码），
// 注意 C++ 重载方法各占一个槽：SetOffsetX(动画)=3、SetOffsetX(float)=4、SetTransform(变换)=7、SetTransform(矩阵)=8 …
internal static unsafe partial class Dcomp
{
    public static readonly Guid IID_IDCompositionDesktopDevice = new("5F4633FE-1E08-4CB8-8C75-CE24333F5602");
    public static readonly Guid IID_IDCompositionVisual3 = new("2775F462-B6C1-4015-B0BE-B3E7D6A4976D");
    public static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    public static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_BOX
    {
        public uint left, top, front, right, bottom, back;
    }

    [LibraryImport("d3d11.dll")]
    public static partial int D3D11CreateDevice(void* adapter, int driverType, nint software, uint flags, int* featureLevels, uint levels,
        uint sdkVersion, void** device, int* featureLevel, void** context);

    [LibraryImport("dcomp.dll")]
    public static partial int DCompositionCreateDevice3(void* renderingDevice, Guid* iid, void** device);

    // ---- IUnknown ----
    public static int QueryInterface(void* p, Guid* iid, void** result) => ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Vtbl(p)[0])(p, iid, result);
    public static uint Release(void* p) => ((delegate* unmanaged[Stdcall]<void*, uint>)Vtbl(p)[2])(p);

    // ---- IDCompositionDesktopDevice（继承 IDCompositionDevice2）----
    public static int Commit(void* device) => ((delegate* unmanaged[Stdcall]<void*, int>)Vtbl(device)[3])(device);
    public static int CreateVisual(void* device, void** visual2) => ((delegate* unmanaged[Stdcall]<void*, void**, int>)Vtbl(device)[6])(device, visual2);
    public static int CreateSurface(void* device, uint width, uint height, int format, int alphaMode, void** surface)
        => ((delegate* unmanaged[Stdcall]<void*, uint, uint, int, int, void**, int>)Vtbl(device)[8])(device, width, height, format, alphaMode, surface);

    // ---- IDCompositionSurface ----
    public static int BeginDraw(void* surface, void* updateRect, Guid* iid, void** updateObject, POINT* offset)
        => ((delegate* unmanaged[Stdcall]<void*, void*, Guid*, void**, POINT*, int>)Vtbl(surface)[3])(surface, updateRect, iid, updateObject, offset);
    public static int EndDraw(void* surface) => ((delegate* unmanaged[Stdcall]<void*, int>)Vtbl(surface)[4])(surface);

    // ---- ID3D11DeviceContext ----
    public static void UpdateSubresource(void* context, void* resource, uint subresource, D3D11_BOX* box, void* data, uint rowPitch, uint depthPitch)
        => ((delegate* unmanaged[Stdcall]<void*, void*, uint, D3D11_BOX*, void*, uint, uint, void>)Vtbl(context)[48])(context, resource, subresource, box, data, rowPitch, depthPitch);
    public static int CreateTargetForHwnd(void* device, nint hwnd, bool topmost, void** target)
        => ((delegate* unmanaged[Stdcall]<void*, nint, int, void**, int>)Vtbl(device)[24])(device, hwnd, topmost ? 1 : 0, target);

    // ---- IDCompositionTarget ----
    public static int SetRoot(void* target, void* visual) => ((delegate* unmanaged[Stdcall]<void*, void*, int>)Vtbl(target)[3])(target, visual);

    // ---- IDCompositionVisual / Visual2 / VisualDebug / Visual3 ----
    public const int BITMAP_INTERPOLATION_MODE_LINEAR = 1;

    public static int SetTransform(void* visual, Matrix3x2* matrix) // D2D_MATRIX_3X2_F 与 Matrix3x2 布局相同
        => ((delegate* unmanaged[Stdcall]<void*, Matrix3x2*, int>)Vtbl(visual)[8])(visual, matrix);
    public static int SetContent(void* visual, void* content) => ((delegate* unmanaged[Stdcall]<void*, void*, int>)Vtbl(visual)[15])(visual, content);
    /// <summary>D2D_RECT_F：left / top / right / bottom。</summary>
    public struct D2D_RECT_F
    {
        public float Left, Top, Right, Bottom;
    }

    /// <summary>矩形裁剪（视觉自己的坐标系里，随它的变换一起缩放）。头文件里 SetClip 的两个重载在 vtable 里倒序，矩形版在 14。</summary>
    public static int SetClip(void* visual, D2D_RECT_F* rect) => ((delegate* unmanaged[Stdcall]<void*, D2D_RECT_F*, int>)Vtbl(visual)[14])(visual, rect);
    public static int SetBitmapInterpolationMode(void* visual, int mode) => ((delegate* unmanaged[Stdcall]<void*, int, int>)Vtbl(visual)[11])(visual, mode);
    /// <summary>insertAbove=false 且 reference=null：加到兄弟的最上层。</summary>
    public static int AddVisual(void* parent, void* visual) => ((delegate* unmanaged[Stdcall]<void*, void*, int, void*, int>)Vtbl(parent)[16])(parent, visual, 0, null);
    /// <summary>插到 <paramref name="reference"/> 的上面（<paramref name="above"/>）或下面。</summary>
    public static int AddVisual(void* parent, void* visual, bool above, void* reference)
        => ((delegate* unmanaged[Stdcall]<void*, void*, int, void*, int>)Vtbl(parent)[16])(parent, visual, above ? 1 : 0, reference);
    public static int RemoveVisual(void* parent, void* visual) => ((delegate* unmanaged[Stdcall]<void*, void*, int>)Vtbl(parent)[17])(parent, visual);
    public static int SetOpacity(void* visual3, float opacity) => ((delegate* unmanaged[Stdcall]<void*, float, int>)Vtbl(visual3)[30])(visual3, opacity);

    // ---- 圆角裁剪：IDCompositionDevice2::CreateRectangleClip = 22；IDCompositionRectangleClip 的各属性同样是「动画版在前、数值版在后」，
    // 数值版依次是 SetLeft 4、SetTop 6、SetRight 8、SetBottom 10、左上 X/Y 12/14、右上 16/18、左下 20/22、右下 24/26 ----
    public static int CreateRectangleClip(void* device, void** clip) => ((delegate* unmanaged[Stdcall]<void*, void**, int>)Vtbl(device)[22])(device, clip);

    /// <summary>视觉用裁剪对象（SetClip 的对象版在 13）；null 取消裁剪。</summary>
    public static int SetClipObject(void* visual, void* clip) => ((delegate* unmanaged[Stdcall]<void*, void*, int>)Vtbl(visual)[13])(visual, clip);

    /// <summary>裁剪矩形（裁剪对象自己的坐标系 = 所在视觉的坐标系）。</summary>
    public static void SetClipRect(void* clip, float left, float top, float right, float bottom)
    {
        var v = Vtbl(clip);
        ((delegate* unmanaged[Stdcall]<void*, float, int>)v[4])(clip, left);
        ((delegate* unmanaged[Stdcall]<void*, float, int>)v[6])(clip, top);
        ((delegate* unmanaged[Stdcall]<void*, float, int>)v[8])(clip, right);
        ((delegate* unmanaged[Stdcall]<void*, float, int>)v[10])(clip, bottom);
    }

    /// <summary>四个角同样的椭圆圆角（横向半径 <paramref name="rx"/>、纵向 <paramref name="ry"/>）。</summary>
    public static void SetClipRadius(void* clip, float rx, float ry)
    {
        var v = Vtbl(clip);
        for (int slot = 12; slot <= 26; slot += 2)
            ((delegate* unmanaged[Stdcall]<void*, float, int>)v[slot])(clip, (slot / 2) % 2 == 0 ? rx : ry);
    }

    /// <summary>任意视觉 QI 成 IDCompositionVisual3（成功时释放原指针）。</summary>
    public static int ToVisual3(void* visual, void** visual3)
    {
        Guid iid = IID_IDCompositionVisual3;
        int hr = QueryInterface(visual, &iid, visual3);
        Release(visual);
        return hr;
    }

    private static void** Vtbl(void* p) => *(void***)p;
}

/// <summary>
/// 未公开的 dwmapi 序号 147「DwmpCreateSharedThumbnailVisual」：把 DWM 缩略图做成调用方 DComp 设备里的视觉，
/// 位置 / 缩放 / 不透明度走 DComp 属性、随 Commit 原子生效，没有逐帧同步的 DwmUpdateThumbnailProperties。
/// 签名取自社区逆向资料（Win10 1809+ / Win11），S4 在本机 26300 上验证可用。
/// </summary>
internal static unsafe partial class SharedThumbnailApi
{
    private static delegate* unmanaged[Stdcall]<nint, nint, uint, Dwmapi.DWM_THUMBNAIL_PROPERTIES*, void*, void**, nint*, int> s_create;

    public static bool Available => s_create != null;

    /// <summary>找到导出；返回诊断说明。</summary>
    public static string Load()
    {
        if (s_create != null) return "已加载";
        nint dwm = LoadLibraryW("dwmapi.dll");
        if (dwm == 0) return "dwmapi.dll 加载失败";
        void* fn = GetProcAddress(dwm, 147);
        if (fn == null) return "dwmapi 没有序号 147 的导出";
        s_create = (delegate* unmanaged[Stdcall]<nint, nint, uint, Dwmapi.DWM_THUMBNAIL_PROPERTIES*, void*, void**, nint*, int>)fn;
        return "dwmapi#147 可用";
    }

    /// <summary>创建共享缩略图视觉（返回 IDCompositionVisual3，调用方 Release；缩略图句柄用 DwmUnregisterThumbnail 注销）。</summary>
    public static int Create(nint destination, nint source, Dwmapi.DWM_THUMBNAIL_PROPERTIES* props, void* device, void** visual3, nint* thumbnail)
    {
        *visual3 = null;
        *thumbnail = 0;
        if (s_create == null) return unchecked((int)0x80004001); // E_NOTIMPL
        void* visual = null;
        int hr = s_create(destination, source, 2, props, device, &visual, thumbnail);
        if (hr < 0 || visual == null) return hr < 0 ? hr : unchecked((int)0x80004005);
        return Dcomp.ToVisual3(visual, visual3);
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryW(string name);

    [LibraryImport("kernel32.dll")]
    private static partial void* GetProcAddress(nint module, nint ordinal);
}
