using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Diagnostics;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Shell;

/// <summary>
/// 替用户点托盘图标：经过资源管理器自己的无障碍元素（UI 自动化），左键 = Invoke，右键菜单 = <c>IUIAutomationElement3::ShowContextMenu</c>
/// （拿不到程序的托盘回调消息，不能绕过资源管理器直接发）。图标按位置找：<c>Shell_NotifyIconGetRect</c> 给出图标此刻的位置，
/// 和任务栏里每个托盘按钮的位置比对；藏在「^」里的图标先打开溢出区，等它展开、图标位置稳定（约 0.3 秒）再在溢出区里找。
/// 实测（Win11 26300）：溢出区开着时两者的位置只差 1 像素；右键菜单弹出后溢出区自己收起。
/// UIA 接口按虚表下标调用（只用到十来个方法），在专门的多线程套间线程上执行。
/// </summary>
public sealed unsafe partial class TrayInvoker : IDisposable
{
    private readonly ComWorker _worker = new("GlideShell.TrayClick", sta: false);
    private void* _uia; // 工作线程
    private bool _uiaFailed;

    /// <summary>最近一次点击的结果（诊断用；null = 还没点过）。</summary>
    public string? LastResult { get; private set; }

    /// <summary>最近一次点击各步的耗时（日志用）。</summary>
    public string LastTiming { get; private set; } = "";

    /// <summary>
    /// 点 <paramref name="app"/> 的托盘图标：<paramref name="menu"/> 为 true 时弹出它的右键菜单（菜单希望出现在 <paramref name="x"/>, <paramref name="y"/>，
    /// 屏幕物理像素；只有直接发消息时能告诉程序），否则左键单击。结果：null = 成功，否则是失败原因（中文，写日志用）。
    /// </summary>
    public Task<string?> Click(TrayApp app, bool menu, double x = 0, double y = 0) => _worker.Run(() =>
    {
        string? error;
        try
        {
            error = menu && TryDirectMenu(app, x, y) ? null : Act(app, menu);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        LastResult = error ?? "ok";
        return error;
    });

    // ---------- 工作线程 ----------

    private const int TreeScope_Descendants = 4;
    private const int UIA_AutomationIdPropertyId = 30011, UIA_InvokePatternId = 10000;
    private static readonly Guid CLSID_CUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");
    private static readonly Guid IID_IUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
    private static readonly Guid IID_IUIAutomationElement3 = new("8471DF34-AEE0-4A01-A7DE-7DB9AF12C296");
    private static readonly Guid IID_IUIAutomationInvokePattern = new("fb377fbe-8ea6-46d5-9c73-6499642d3059");
    private const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";

    /// <summary>
    /// 托盘回调消息已知的框架（<c>Shell_NotifyIcon</c> 时程序自己定的，资源管理器之外拿不到，只能按宿主窗口的类名认）：
    /// 直接给程序发右键，几十毫秒弹出菜单，也不用打开「^」溢出区。实测（2026-11-09）：PixPin（Qt）27 毫秒、微信 25 毫秒、
    /// G-Helper（WinForms）22 毫秒、QQ（Electron）256 毫秒。微信对资源管理器转来的右键（UI 自动化 ShowContextMenu）根本没反应，只能这样。
    /// </summary>
    private static (uint Message, bool Version4)? KnownCallback(string ownerClass)
    {
        // Qt 5 / 6 的 QSystemTrayIcon：WM_APP+101，NOTIFYICON_VERSION_4（只认 WM_CONTEXTMENU，坐标在 wParam）
        if (QtTray().IsMatch(ownerClass)) return (0x8000 + 101, true);
        // 微信 4.x 改过的 Qt 托盘：消息号是试出来的（WM_APP+0x385），格式与 Qt 相同
        if (WeChatTray().IsMatch(ownerClass)) return (0x8385, true);
        // WinForms NotifyIcon：WM_USER+1024，旧格式（lParam 就是鼠标消息），右键松开时在指针处弹菜单
        if (ownerClass.StartsWith("WindowsForms10.Window.0.app.", StringComparison.Ordinal)) return (0x0400 + 1024, false);
        // Electron：WM_APP+1，旧格式
        if (ownerClass == "Electron_NotifyIconHostWindow") return (0x8000 + 1, false);
        return null;
    }

    [GeneratedRegex(@"^(Qt\d+)?Q?TrayIconMessageWindowClass$")]
    private static partial Regex QtTray();

    [GeneratedRegex(@"^Qt\d+WxTrayIconMessageWindowClass$")]
    private static partial Regex WeChatTray();

    private bool TryDirectMenu(TrayApp app, double x, double y)
    {
        nint owner = (nint)app.OwnerWindow;
        if (owner == 0 || !User32.IsWindow(owner) || KnownCallback(WindowCatalog.ClassName(owner)) is not { } known) return false;
        var (message, v4) = known;
        uint pid = 0;
        User32.GetWindowThreadProcessId(owner, &pid);
        User32.AllowSetForegroundWindow(pid); // 和资源管理器转发右键时一样：让程序能把自己的菜单切到前台（点外面才会收起）
        nint uid = (nint)(uint)app.Uid;
        bool ok;
        if (v4)
        {
            nint at = (nint)(((int)Math.Round(y) & 0xFFFF) << 16 | ((int)Math.Round(x) & 0xFFFF));
            ok = User32.PostMessageW(owner, message, at, (nint)(uid << 16 | 0x007B /* WM_CONTEXTMENU */));
        }
        else
        {
            ok = User32.PostMessageW(owner, message, uid, 0x0204 /* WM_RBUTTONDOWN */) &&
                 User32.PostMessageW(owner, message, uid, 0x0205 /* WM_RBUTTONUP */);
        }
        if (ok) LastTiming = $"直接发给程序（{WindowCatalog.ClassName(owner)}）";
        return ok;
    }

    private string? Act(TrayApp app, bool menu)
    {
        var watch = Stopwatch.StartNew();
        double foundMs = 0, flyoutMs = 0;
        LastTiming = "";
        if (!EnsureUia()) return "UI 自动化不可用";
        if (!TrayCatalog.TryGetRect(app, out var closed)) return "图标已经不在托盘里";
        nint taskbar = User32.FindWindowW("Shell_TrayWnd", null);
        if (taskbar == 0) return "找不到任务栏";
        var owned = new List<nint>(); // 用完统一释放
        try
        {
            // 任务栏上的托盘按钮缓存着（整棵任务栏找一遍约 170 毫秒），位置每次现读；对不上再重新找一遍
            void* target = Match(Cached(taskbar, "NotifyItemIcon", false), closed);
            void* chevron = target == null ? Match(Cached(taskbar, "SystemTrayIcon", false), closed) : null;
            if (target == null && chevron == null)
            {
                target = Match(Cached(taskbar, "NotifyItemIcon", true), closed);
                chevron = target == null ? Match(Cached(taskbar, "SystemTrayIcon", true), closed) : null;
            }
            bool opened = false;
            nint flyout = 0;
            if (target == null)
            {
                // 藏在「^」里：此刻给出的位置就是「^」按钮。先打开溢出区（已经开着就不再点，点一下会把它关上）
                if (chevron == null) return "找不到「显示隐藏的图标」按钮";
                flyout = User32.FindWindowW(OverflowClass, null);
                if (flyout == 0 || !User32.IsWindowVisible(flyout))
                {
                    int hr = Invoke(chevron);
                    if (hr < 0) return $"打开溢出区失败 0x{hr:X8}";
                    opened = true;
                }
                // 等溢出区展开、图标位置稳定（两次读到的一样）再点：展开动画途中点，资源管理器不理（实测 Argus、IDM 的菜单不出来）
                foundMs = watch.Elapsed.TotalMilliseconds;
                RectD last = closed;
                bool settled = false;
                long deadline = Environment.TickCount64 + 1000;
                while (Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(20);
                    if (!TrayCatalog.TryGetRect(app, out var now)) break;
                    if (now != closed && now == last)
                    {
                        settled = true;
                        break;
                    }
                    last = now;
                }
                flyout = User32.FindWindowW(OverflowClass, null);
                if (!settled || flyout == 0)
                {
                    if (opened) CloseFlyout(chevron, flyout);
                    return "溢出区没有展开";
                }
                target = Match(Cached(flyout, "NotifyItemIcon", false), last);
                if (target == null) target = Match(Cached(flyout, "NotifyItemIcon", true), last);
                flyoutMs = watch.Elapsed.TotalMilliseconds;
                if (target == null)
                {
                    if (opened) CloseFlyout(chevron, flyout);
                    return "溢出区里找不到这个图标";
                }
            }
            if (foundMs == 0) foundMs = watch.Elapsed.TotalMilliseconds;
            int result = menu ? ShowContextMenu(target) : Invoke(target);
            LastTiming = chevron == null
                ? $"任务栏里找到 {foundMs:F0}ms，点完 {watch.Elapsed.TotalMilliseconds:F0}ms"
                : $"任务栏 {foundMs:F0}ms，溢出区里找到 {flyoutMs:F0}ms，点完 {watch.Elapsed.TotalMilliseconds:F0}ms";
            if (result < 0)
            {
                if (opened) CloseFlyout(chevron, flyout);
                return $"{(menu ? "弹出右键菜单" : "点击")}失败 0x{result:X8}";
            }
            // 程序打开了窗口或菜单，溢出区会自己收起；过一会儿还开着、而且还是前台（程序没反应），就替用户收起。
            // 放到后面单独做：总览等这次点击完成才撤层，不能让它多等
            if (opened) _ = _worker.Run(() => CloseFlyoutIfIdle(closed));
            return null;
        }
        finally
        {
            foreach (nint p in owned) Com.Release((void*)p);
        }
    }

    /// <summary>点完 0.4 秒后溢出区还开着、而且还是前台：按位置重新找到「^」（<paramref name="chevronRect"/>）点一下收起。</summary>
    private bool CloseFlyoutIfIdle(RectD chevronRect)
    {
        Thread.Sleep(400);
        nint flyout = User32.FindWindowW(OverflowClass, null);
        if (flyout == 0 || !User32.IsWindowVisible(flyout) || User32.GetForegroundWindow() != flyout) return false;
        var owned = new List<nint>();
        try
        {
            void* chevron = Match(Cached(User32.FindWindowW("Shell_TrayWnd", null), "SystemTrayIcon", false), chevronRect);
            CloseFlyout(chevron, flyout);
            return true;
        }
        finally
        {
            foreach (nint p in owned) Com.Release((void*)p);
        }
    }

    private void CloseFlyout(void* chevron, nint flyout)
    {
        if (chevron != null && flyout != 0 && User32.IsWindowVisible(flyout)) Invoke(chevron);
    }

    private bool EnsureUia()
    {
        if (_uia != null) return true;
        if (_uiaFailed) return false;
        Guid clsid = CLSID_CUIAutomation8, iid = IID_IUIAutomation;
        void* uia;
        int hr = Ole32.CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, &uia);
        if (hr < 0 || uia == null)
        {
            _uiaFailed = true;
            Log.Warn($"后台程序栏：创建 UI 自动化失败 0x{hr:X8}");
            return false;
        }
        _uia = uia;
        return true;
    }

    private readonly Dictionary<(nint Window, string Id), List<nint>> _cache = []; // 工作线程：按窗口与 AutomationId 缓存的元素（持有引用）

    /// <summary>
    /// <paramref name="window"/> 里 AutomationId 为 <paramref name="automationId"/> 的元素与它们此刻的位置。元素缓存着（找一遍要上百毫秒，
    /// 读位置只要零点几毫秒）；<paramref name="refresh"/> 或还没找过时重新找。元素归缓存，调用方不释放。
    /// </summary>
    private List<(nint Element, RectD Rect)> Cached(nint window, string automationId, bool refresh)
    {
        var key = (window, automationId);
        if (refresh || !_cache.TryGetValue(key, out var elements))
        {
            if (_cache.Remove(key, out var old))
                foreach (nint e in old) Com.Release((void*)e);
            var found = new List<nint>();
            var list = FindAll(window, automationId, found);
            // FindAll 把根元素、条件、数组也记进了 found：只留下结果里的元素，其余释放
            elements = list.Select(x => x.Element).ToList();
            foreach (nint p in found)
                if (!elements.Contains(p)) Com.Release((void*)p);
            _cache[key] = elements;
            return list;
        }
        var result = new List<(nint, RectD)>(elements.Count);
        foreach (nint e in elements)
        {
            User32.RECT r;
            // IUIAutomationElement::get_CurrentBoundingRectangle（元素已失效时失败，跳过）
            if (((delegate* unmanaged[Stdcall]<void*, User32.RECT*, int>)(*(void***)e)[43])((void*)e, &r) < 0) continue;
            result.Add((e, new RectD(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)));
        }
        return result;
    }

    /// <summary><paramref name="window"/> 里 AutomationId 为 <paramref name="automationId"/> 的全部元素与它们的位置（元素记进 <paramref name="owned"/>，由调用方释放）。</summary>
    private List<(nint Element, RectD Rect)> FindAll(nint window, string automationId, List<nint> owned)
    {
        var result = new List<(nint, RectD)>();
        void* root;
        // IUIAutomation::ElementFromHandle
        if (((delegate* unmanaged[Stdcall]<void*, nint, void**, int>)(*(void***)_uia)[6])(_uia, window, &root) < 0 || root == null) return result;
        owned.Add((nint)root);
        void* condition;
        char* bstr;
        fixed (char* s = automationId) bstr = Com.SysAllocString(s);
        try
        {
            var value = new Variant { vt = 8 /* VT_BSTR */, value = (nint)bstr };
            // IUIAutomation::CreatePropertyCondition(PROPERTYID, VARIANT, IUIAutomationCondition**)
            if (((delegate* unmanaged[Stdcall]<void*, int, Variant, void**, int>)(*(void***)_uia)[23])(_uia, UIA_AutomationIdPropertyId, value, &condition) < 0 || condition == null)
                return result;
        }
        finally
        {
            Com.SysFreeString(bstr);
        }
        owned.Add((nint)condition);
        void* array;
        // IUIAutomationElement::FindAll(TreeScope, IUIAutomationCondition*, IUIAutomationElementArray**)
        if (((delegate* unmanaged[Stdcall]<void*, int, void*, void**, int>)(*(void***)root)[6])(root, TreeScope_Descendants, condition, &array) < 0 || array == null)
            return result;
        owned.Add((nint)array);
        int length = 0;
        ((delegate* unmanaged[Stdcall]<void*, int*, int>)(*(void***)array)[3])(array, &length); // get_Length
        for (int i = 0; i < length; i++)
        {
            void* element;
            if (((delegate* unmanaged[Stdcall]<void*, int, void**, int>)(*(void***)array)[4])(array, i, &element) < 0 || element == null) continue; // GetElement
            owned.Add((nint)element);
            User32.RECT r;
            // IUIAutomationElement::get_CurrentBoundingRectangle
            if (((delegate* unmanaged[Stdcall]<void*, User32.RECT*, int>)(*(void***)element)[43])(element, &r) < 0) continue;
            result.Add(((nint)element, new RectD(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)));
        }
        return result;
    }

    /// <summary>位置与 <paramref name="rect"/> 对得上的那个按钮（中心相差不到按钮短边的一半；取最近的），没有返回 null。</summary>
    private static void* Match(List<(nint Element, RectD Rect)> buttons, RectD rect)
    {
        void* best = null;
        double bestD = double.MaxValue;
        foreach (var (element, r) in buttons)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;
            double dx = r.CenterX - rect.CenterX, dy = r.CenterY - rect.CenterY, d = Math.Sqrt(dx * dx + dy * dy);
            if (d < Math.Min(r.Width, r.Height) / 2 && d < bestD)
            {
                bestD = d;
                best = (void*)element;
            }
        }
        return best;
    }

    private static int Invoke(void* element)
    {
        void* unknown;
        // IUIAutomationElement::GetCurrentPattern(PATTERNID, IUnknown**)
        int hr = ((delegate* unmanaged[Stdcall]<void*, int, void**, int>)(*(void***)element)[16])(element, UIA_InvokePatternId, &unknown);
        if (hr < 0 || unknown == null) return hr < 0 ? hr : unchecked((int)0x80004002);
        try
        {
            Guid iid = IID_IUIAutomationInvokePattern;
            void* pattern;
            hr = Com.QueryInterface(unknown, &iid, &pattern);
            if (hr < 0 || pattern == null) return hr;
            try
            {
                return ((delegate* unmanaged[Stdcall]<void*, int>)(*(void***)pattern)[3])(pattern); // Invoke
            }
            finally
            {
                Com.Release(pattern);
            }
        }
        finally
        {
            Com.Release(unknown);
        }
    }

    private static int ShowContextMenu(void* element)
    {
        Guid iid = IID_IUIAutomationElement3;
        void* e3;
        int hr = Com.QueryInterface(element, &iid, &e3);
        if (hr < 0 || e3 == null) return hr < 0 ? hr : unchecked((int)0x80004002);
        try
        {
            // IUIAutomationElement3::ShowContextMenu（IUIAutomationElement 82 个方法、Element2 6 个之后的第一个）
            return ((delegate* unmanaged[Stdcall]<void*, int>)(*(void***)e3)[91])(e3);
        }
        finally
        {
            Com.Release(e3);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public nint value;
        public nint extra;
    }

    public void Dispose()
    {
        _worker.Dispose();
    }
}
