# S3 虚拟桌面技术验证

> 2026-10-26，本机实测。工具：`spikes/S3.VirtualDesktop/`（独立控制台项目，不在 `GlideShell.slnx` 里）。
> 结论只写在本文；`docs/ROADMAP.md` 第 5 节由主会话汇总。

## 0. 结论速览

| 问题 | 结论 |
|---|---|
| 接口布局 | 26300 上就是 24H2 布局：`IVirtualDesktopManagerInternal` {53F5CA0B…} 25 个方法，`IVirtualDesktop` {3F07F4BE…} 7 个方法，通知 {B9E5E94D…} 14 个方法。**用系统代理 DLL 的 MIDL 格式串逐槽核对过**，不只是照抄参考项目（第 2 节） |
| 无动画切换 | **能**。`SwitchDesktop`（槽 9）没有任何系统动画：调用 5–13 ms 返回，画面在调用后 6–23 ms 内一帧切完。`SwitchDesktopWithAnimation`（槽 22）才有滑动动画，调用要阻塞 120–210 ms，整个动画约 0.5 s |
| 覆盖层能否盖住切换 | **能**。`WS_EX_TOOLWINDOW` 置顶窗口没有 ApplicationView，不受虚拟桌面管理，无动画切换的每一帧里它都在（0 帧丢失）；`WS_EX_APPWINDOW` 窗口会被 cloak |
| 目标桌面的画面 | **DWM 缩略图对其他桌面上（被 cloak）的窗口照样是实时的**：GDI 窗口和 DXGI 翻转模型窗口都实测，滞后与当前桌面上一样（0–2 帧）；WGC、PrintWindow 也都拿得到实时内容。不需要「离开时截图缓存」作为主方案（第 4 节） |
| 焦点 | **问题点**。后台进程调 `SwitchDesktop` 后，前台焦点留在旧桌面那个已被 cloak 的窗口上（键盘输入会打进看不见的窗口），目标桌面的窗口在任务栏上闪烁（HSHELL_FLASH）。后台进程自己 `SetForegroundWindow` 不可靠。P5 必须单独解决（第 3.3 节） |
| 原生基线 | Win11 原生四指切桌面在本机**是跟手的**：手指移动约 6–10 mm 后画面开始滑，约 1.45 % 屏宽 / mm（≈ 69 mm 滑满一屏），中途停住画面就停，反向画面就退回，**松手才提交**（通知在抬指后 14–34 ms），收尾动画 150–200 ms（第 5 节）。P5 的门槛因此不低 |

**推荐的 P5 路线**（详见第 6 节）：关闭原生四指滑动 → 全屏置顶 `WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE` 覆盖层，用 DirectComposition + DWM 缩略图把「当前桌面」和「目标桌面」各自的壁纸层与窗口层拼出来跟手滑动 → 松手判定提交后，在覆盖层下面调 `SwitchDesktop`（无动画）→ 等切换通知后再多等 2–3 个合成帧 → 撤掉覆盖层 → 激活目标桌面的顶层窗口。启动时用代理格式串自检接口布局，不通过就整条路线禁用、交还原生四指手势。

## 1. 测试环境

- 系统：Windows 11 Pro 26H2，构建 **26300.9711**；`twinui.pcshell.dll` 10.0.26100.9278，`ActXPrxy.dll` 10.0.26100.9549，`OneCoreUAPCommonProxyStub.dll` 10.0.26100.9711。
- 显示：2560×1600，测试期间刷新率是 **165 Hz**（`Win32_VideoController.CurrentRefreshRate`；ROADMAP 第 7 节写的是 60 Hz，主会话汇总时请更正或注明）。另有 RTX 4050 独显（混合显卡），S4 可能需要留意。
- 桌面：3 个桌面，用户窗口都在桌面 1，桌面 2、3 是空的。测试只在桌面 1 ↔ 桌面 2 之间切换，没有新建/删除桌面，没有移动用户的窗口（只移动过本工具自己创建的测试窗口），结束时都回到桌面 1。
- IDA Pro MCP 没有连上（`check_connection` 超时），没有用。方法表的权威依据改用系统代理 DLL 的格式串（见 2.1），比反汇编 vtable 更直接：跨进程调用 explorer 必须走这些代理，代理认的槽位和参数就是 explorer 实际实现的。

## 2. 接口布局（问题 1）

### 2.1 证据来源（三路互相印证）

1. **注册表**：`HKCR\Interface` 下这些 IID 都**没有登记名称**（默认值为空），所以按名称查不到；按 IID 查，24H2 这组都已注册，代理是 `ActXPrxy.dll`（{C90250F3…}）或 `OneCoreUAPCommonProxyStub.dll`（{95E15D0A…}）。22H2 及更早的 IID 都未注册。
2. **QueryService / QueryInterface 探测**（`S3.VirtualDesktop info`）：explorer 对 53F5CA0B 和 4970BA3D 返回 S_OK，对 22H2 / 21H2 / Win10 的 IID 返回 E_NOINTERFACE；当前桌面对象只响应 3F07F4BE。
3. **代理格式串**（`S3.VirtualDesktop proxy`）：两个代理 DLL 都导出 `GetProxyDllInfo`，从 `ProxyFileInfo → CInterfaceStubHeader → MIDL_SERVER_INFO` 读出每个接口的方法总数、每个槽位的 `proc_num`、参数方向和类型（接口指针带 IID、HSTRING 等 user_marshal 类型经延迟导入表反查成名字）。代理 DLL 的接口名称表是空的，名字只能靠已知 IID 对上。
4. **参考项目**（只用于给槽位起名字，槽位本身以 3 为准）：
   - MScholtes/VirtualDesktop **v1.21（2025-08-11）**：`VirtualDesktop11-24H2.cs`（24H2）、`VirtualDesktop11.cs`（22631.3085 起的 23H2）、`VirtualDesktop.cs`（Win10）。
   - Ciantic/VirtualDesktopAccessor **rust 分支（最新提交 2026-10-11）**`src/interfaces.rs`；README 写明要求 24H2 26100.2605 起，测于 25H2 26200.8117。

`S3.VirtualDesktop selftest` 把本工具的槽位表与本机格式串逐项核对，26300.9711 上 16 项全部通过。

### 2.2 IID / CLSID 表（26300.9711）

| 名称 | GUID | 取法 |
|---|---|---|
| CLSID_ImmersiveShell | C2F03A33-21F5-47FA-B4BB-156362A2F239 | `CoCreateInstance(CLSCTX_LOCAL_SERVER)` 取 `IServiceProvider` |
| SID_VirtualDesktopManagerInternal | C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B | QueryService 的服务 ID |
| **IVirtualDesktopManagerInternal** | **53F5CA0B-158F-4124-900C-057158060B27** | 24H2 / 26100 / 26200 / 26300；23H2（22631.3085+）IID 相同但布局不同 |
| IVirtualDesktopManagerInternal（旧） | 4970BA3D-FD4E-4647-BEA3-D89076EF4B9C | explorer 仍实现，20 个方法（23H2 早期布局），不用 |
| **IVirtualDesktop** | **3F07F4BE-B107-441A-AF0F-39D82529072C** | 22H2 起未变 |
| SID_VirtualDesktopNotificationService | A501FDEC-4A09-464C-AE4E-1B9C21B84918 | |
| IVirtualDesktopNotificationService | 0CD45E71-D927-4F15-8B0A-8FEF525337BF | Register / Unregister |
| **IVirtualDesktopNotification** | **B9E5E94D-233E-49AB-AF5C-2B4541C3AADE** | 由我们实现、explorer 回调 |
| IApplicationViewCollection | 1841C6D7-4F9D-42C0-AF41-8747538F10E5 | 服务 ID 与 IID 相同 |
| IApplicationView | 372E1D3B-38D3-42E4-A15B-8AB2B178F513 | 派生自 IInspectable，62 个方法 |
| SID / IID VirtualDesktopPinnedApps | B5A399E7-1C87-46B8-88E9-FC5747B171BD / 4CE81583-1E4C-4632-A621-07A53543148F | |
| IVirtualDesktopManager（**已文档化**） | **A5CD92FF**-29BE-454C-8D04-D82879FB3F1B，CLSID AA509086-5CA9-4C25-8F95-589D3C07B48A | 只查询 + 移动**本进程**窗口 |

> **旧原型的 bug**：`legacy/M1/src/MissionFlow/Win32.cs` 把 IVirtualDesktopManager 写成 `a5cd`**`82`**`ff-…`，实测 `CoCreateInstance` 返回 E_NOINTERFACE，`TryCreateVirtualDesktopManager()` 永远返回 null——旧原型的「其他虚拟桌面(VDM)」过滤从来没生效过，只靠 cloaked 位在起作用。新代码迁移时要用 `a5cd92ff`。

### 2.3 方法表（槽位 0–2 为 IUnknown）

记法：接口名本身表示接口指针，`out IVirtualDesktop*` 即 C 里的 `IVirtualDesktop**`；`i32*` 等为 out 指针；返回值都是 HRESULT。

**IVirtualDesktopManagerInternal {53F5CA0B}，共 25 个方法**

| 槽 | 方法 | 参数（代理格式串） | 本 spike |
|---|---|---|---|
| 3 | GetCount | out i32* | ✓ 用过 |
| 4 | MoveViewToDesktop | in IApplicationView, in IVirtualDesktop | 未用（会移动别人的窗口） |
| 5 | CanViewMoveDesktops | in IApplicationView, out i32* | |
| 6 | GetCurrentDesktop | out IVirtualDesktop* | ✓ |
| 7 | GetDesktops | out IObjectArray* | ✓ |
| 8 | GetAdjacentDesktop | in IVirtualDesktop, in i32（3=左，4=右）, out IVirtualDesktop* | ✓ 无相邻时返回失败 HRESULT |
| 9 | **SwitchDesktop** | in IVirtualDesktop | ✓ **无动画** |
| 10 | SwitchDesktopAndMoveForegroundView | in IVirtualDesktop | 未测（会把用户的前台窗口带到目标桌面） |
| 11 | CreateDesktop | out IVirtualDesktop* | 未用 |
| 12 | MoveDesktop | in IVirtualDesktop, in i32 | |
| 13 | RemoveDesktop | in IVirtualDesktop, in IVirtualDesktop（回退桌面） | 未用 |
| 14 | FindDesktop | in GUID*, out IVirtualDesktop* | |
| 15 | GetDesktopSwitchIncludeExcludeViews | in IVirtualDesktop, out IObjectArray*, out IObjectArray* | |
| 16 | SetDesktopName | in IVirtualDesktop, in HSTRING | |
| 17 | SetDesktopWallpaper | in IVirtualDesktop, in HSTRING | |
| 18 | UpdateWallpaperPathForAllDesktops | in HSTRING | |
| 19 | CopyDesktopState | in IApplicationView, in IApplicationView | |
| 20 | CreateRemoteDesktop | in HSTRING, out IVirtualDesktop* | |
| 21 | SwitchRemoteDesktop | in IVirtualDesktop, in enum16 | |
| 22 | **SwitchDesktopWithAnimation** | in IVirtualDesktop | ✓ 有动画 |
| 23 | GetLastActiveDesktop | out IVirtualDesktop* | |
| 24 | WaitForAnimationToComplete | （无） | ✓ |

**IVirtualDesktop {3F07F4BE}，共 7 个方法**：3 IsViewVisible(in IApplicationView, out i32*)，4 GetId(out GUID*)，5 GetName(out HSTRING*)，6 GetWallpaperPath(out HSTRING*)。

**IVirtualDesktopNotification {B9E5E94D}，共 14 个方法**：3 VirtualDesktopCreated(d)，4 DestroyBegin(d, fallback)，5 DestroyFailed(d, fallback)，6 Destroyed(d, fallback)，7 Moved(d, **i32** from, **i32** to)，8 NameChanged(d, HSTRING)，9 ViewVirtualDesktopChanged(IApplicationView)，10 **CurrentVirtualDesktopChanged(old, new)**，11 WallpaperChanged(d, HSTRING)，12 **VirtualDesktopSwitched(d, enum16)**，13 RemoteVirtualDesktopConnected(d)。本 spike 实测只用 MTA + 手写 vtable 收回调，API 切换和原生手势切换都是先 CurrentChanged、3–25 ms 后 Switched（kind 都是 0）。

**IVirtualDesktopNotificationService {0CD45E71}**：3 Register(in IUnknown, out i32* cookie)，4 Unregister(in i32)。

**IApplicationViewCollection {1841C6D7}，共 14 个方法**：3 GetViews，4 GetViewsByZOrder，5 GetViewsByAppUserModelId(wchar_t*)，6 **GetViewForHwnd(HWND, out IApplicationView*)**，7 GetViewForApplication，8 GetViewForAppUserModelId，9 GetViewInFocus，10 TryGetLastActiveVisibleView，11 RefreshCollection，12 RegisterForApplicationViewChanges，13 Unregister…。

**IVirtualDesktopPinnedApps {4CE81583}，共 9 个方法**：3 IsAppIdPinned，4 PinAppID，5 UnpinAppID，6 IsViewPinned(IApplicationView, out i32*)，7 PinView，8 UnpinView。

**IVirtualDesktopManager（文档化）**：3 IsWindowOnCurrentVirtualDesktop(HWND, out BOOL*)，4 GetWindowDesktopId(HWND, out GUID*)，5 MoveWindowToDesktop(HWND, in GUID*)。

**IApplicationView {372E1D3B}，共 62 个方法**：3–5 IInspectable；6、7 是两个无参方法，按 MScholtes 的顺序为 SetFocus / SwitchTo；8 TryInvokeBack，9 GetThumbnailWindow(out HWND)，…，16 GetExtendedFramePosition(out RECT)。完整格式串见 `S3.VirtualDesktop proxy` 输出。

### 2.4 与参考项目不一致的地方（在 26300 上）

- **IVirtualDesktop 没有 IsRemote**：MScholtes 24H2 版在槽 7 声明了 `IsRemote()`，本机这个接口只有 7 个方法（到槽 6 为止），调它会越过 vtable。Ciantic 的定义（4 个方法）是对的。
- **VirtualDesktopMoved 的两个索引是 i32**：Ciantic 写成 i64。x64 上走寄存器，读成 i64 会带上高 32 位垃圾。
- **VirtualDesktopSwitched 多一个 enum16 参数**（Ciantic 没有）。实现回调时多收一个参数无害，但签名要按格式串写。
- **MScholtes 的 IApplicationView 只有 58 个方法**，本机是 62 个；中间哪里少了不确定，GlideShell 不要调用槽 16 以后的 IApplicationView 方法，除非重新核对。
- **同一个 IID，不同布局**：22631.3085+ 与 26100+ 的 IVirtualDesktopManagerInternal IID 都是 53F5CA0B，但 26100 在 SwitchDesktop 后面插入了 `SwitchDesktopAndMoveForegroundView`，之后的槽位整体后移一位（MScholtes 的 23H2 / 24H2 两个文件对照可见）。**所以「按 IID 选布局」不安全，必须核对方法数和槽位签名**（见第 7 节）。

## 3. 切换（问题 2）

测法（`S3.VirtualDesktop switch --yes`）：桌面 1 ↔ 桌面 2 来回切，每程用 DXGI 桌面复制逐帧记录主显示器合成结果（带 QPC 时间戳），同时记录切换通知、前台窗口（2 ms 轮询）、Shell 钩子事件，并在屏幕上放两个 120 px 置顶探针窗口：洋红 = `WS_EX_TOOLWINDOW`，青色 = `WS_EX_APPWINDOW`，检查每一帧里它们是否还在。

### 3.1 有动画 vs 无动画

| | SwitchDesktop（槽 9） | SwitchDesktopWithAnimation（槽 22） |
|---|---|---|
| 调用返回 | 5.3–13.2 ms（6 次去程 + 6 次回程） | 121–211 ms（阻塞到动画开始） |
| CurrentChanged 通知 | 调用后 6.5–22.1 ms | 与调用返回同时（+1–2 ms） |
| 画面 | **一帧切完**，首次变化在调用后 6–23 ms，之后不再变化 | 水平滑动：旧桌面（窗口 + 壁纸 + 图标）向左滑出、新桌面从右滑入，任务栏不动；首帧变化在调用后 109–282 ms，变化跨度 262–431 ms，约 45 帧 |
| WaitForAnimationToComplete | — | 在调用后 470–567 ms 返回 |
| 置顶 TOOLWINDOW 探针 | **每一帧都在**（0 丢失） | 动画期间约 31–33 帧（≈190 ms）看不到——系统动画把它拍进旧桌面快照一起滑走，动画层在置顶窗口之上 |
| 置顶 APPWINDOW 探针 | 去了别的桌面就被 cloak（cloaked=2） | 同左 |
| Shell 钩子 | 无特别事件 | 动画开始和结束各有一个未公开代码 0x35 / 0x36（窗口属于一个短命线程） |

结论：**`SwitchDesktop` 本身就是无动画切换**，不需要任何「关动画」的技巧（MScholtes v1.20 的 `/Animation:Off` 也只是改调 SwitchDesktop）。另外 MScholtes v1.21 的更新说明提到并发调用动画版会让 explorer 崩溃，GlideShell 不要用动画版。

**闪烁**：无动画切换回到有多个窗口的桌面时，有 2 次观察到**一帧中间态**（165 Hz 下约 6 ms）：屏幕上露出了 Z 序第二的窗口，最上面的窗口还没恢复——explorer 是逐个解除 cloak 的，DWM 在中间合成了一帧。切到空桌面没有中间态。P5 用覆盖层盖住切换，在 CurrentChanged 之后再多等 2–3 个合成帧（`DwmFlush`）再撤掉覆盖层即可消除。

### 3.2 帧时间线举例（无动画，去空桌面）

```
-309.9 ms  基线帧                                   tool=FF00FF app=00FFFF
  19.5 ms  Δ前=91.9%  Δ末=0.5%（这一帧就是最终画面）tool=FF00FF app=707075（app 探针被 cloak，露出壁纸）
事件 +22.1 ms CurrentChanged CB00E64E → C1D46317；+27.3 ms Switched
```

### 3.3 焦点落在哪里

| 场景 | 结果 |
|---|---|
| 切到空桌面（后台进程调用） | 前台窗口不变：仍是桌面 1 的 Claude 窗口，它此时已被 cloak——键盘输入会打进看不见的窗口 |
| 目标桌面上有一个本工具的窗口 | 前台仍不变；该窗口立刻收到 **HSHELL_FLASH**（任务栏按钮闪烁），之后约每 530 ms 交替 FLASH / REDRAW，一直闪 |
| 切换后本进程 `SetForegroundWindow(目标窗口)` | 第一次（用户刚点击过、前台锁有效）**失败**，主线程上 `AttachThreadInput` 后也失败；后几次在没有新用户输入时成功——本工具由前台应用（Claude）启动，享受「由前台进程启动」的豁免，有新输入时豁免失效。`SPI_GETFOREGROUNDLOCKTIMEOUT` 是 2147483647，不是超时放行 |
| 切换后调目标窗口的 `IApplicationView.SwitchTo()`（槽 7） | 返回 S_OK，但没有激活，仍在闪 |
| 不调 SwitchDesktop，直接对另一桌面上的窗口调 `SwitchTo()` | 只引起闪烁；约 0.58 s 后本进程 `SetForegroundWindow` 成功，系统随即**自动切到该窗口所在桌面**（无动画，一帧切完）——即「激活另一桌面上的窗口会让系统跟过去」 |

结论：后台进程调 `SwitchDesktop` **不会**把焦点交给目标桌面，而 GlideShell 常驻时不是由前台进程启动的，没有豁免，`SetForegroundWindow` 会被前台锁挡住。P5 需要可靠的激活办法，候选（都需要 P5 前置实验，用正常方式启动的 GlideShell 测）：
1. MScholtes 的做法：切换前用 `AttachThreadInput` 把任务栏（`Shell_TrayWnd`）设为前台，让 explorer 成为前台进程，切换时由 explorer 自己激活目标桌面最后活动的窗口；切完再 `ShowWindow(Shell_TrayWnd, SW_MINIMIZE)` 让任务栏交出焦点。他的更新说明称这样「几乎总能」避免任务栏闪烁。
2. 在**有消息队列的线程**上 `AttachThreadInput(本线程, 前台线程)` 后再 `SetForegroundWindow(目标桌面顶层窗口)`（本次主线程无消息队列，Attach 本身可能就没成功）。
3. P7 的 UIAccess 签名是否放开前台限制——待验证，文档没有明说。
4. 兜底：接受闪烁，但至少把前台设到目标桌面（例如让 Progman 成为前台），避免键盘打进看不见的窗口。

## 4. 目标桌面的画面（问题 3）

测法（`thumbtest --yes [--dx]`）：本工具创建一个「帧计数编码色」测试窗口（整块填充，R/G = 计数低/高字节，B = 0x80，任何截到的像素都能解出是第几帧），先在当前桌面做对照（A），再用文档化 `MoveWindowToDesktop` 把**它**移到桌面 2（B，cloaked=2），最后移回（C）。三种取法同时采：DWM 缩略图（注册到当前桌面上的宿主窗口，桌面复制读中心像素）、WGC（cloak 前和 cloak 后各建一个会话）、PrintWindow(PW_RENDERFULLCONTENT)。

| 源窗口 | 阶段 | DWM 缩略图 | WGC | WGC（cloak 后新建） | PrintWindow |
|---|---|---|---|---|---|
| GDI（WM_TIMER 10 ms + WM_PAINT） | A 当前桌面 | 实时，滞后 ≤1 计数 | 实时，86 帧/1.5 s | — | 实时，8.9 ms |
| | **B 其他桌面** | **实时，滞后 0**（348 帧里 126 个不同值） | **实时**，113 帧/2 s | **实时**，113 帧/2 s | **实时**，9.5 ms |
| | C 移回 | 实时 | 实时 | — | 实时 |
| DXGI FLIP_DISCARD，Present(1) | A | 实时，滞后 2 帧 | 实时，滞后 3 帧 | — | 实时，7.4 ms |
| | **B 其他桌面** | **实时，滞后 2 帧**（与当前桌面一样） | **实时** | **实时** | **实时**，8.4 ms |

补充观察：
- cloak 期间应用照常工作：GDI 窗口 2 s 内 WM_PAINT +144 次；翻转模型 Present 2 s 内 +352 次（≈165 Hz，没有被节流），**没有一次返回 DXGI_STATUS_OCCLUDED**。也就是说在系统层面，其他桌面上的窗口和当前桌面上被遮住的窗口没有区别。
- WGC 在本机约 56–58 帧/秒封顶，滞后 3 帧左右，还需要 D3D 设备和回读；DWM 缩略图由 DWM 直接合成，零拷贝、滞后最小，**P5 首选缩略图**。
- 桌面背景：`Progman` 窗口的 DWM 缩略图正常（壁纸 + 图标，25/25 个探针非黑），内容静止；PrintWindow 它要 66 ms，太慢。`IVirtualDesktop.GetWallpaperPath` 在本机三个桌面都返回空（没设分桌面壁纸，用全局壁纸）。
- 工具窗口（`WS_EX_TOOLWINDOW`、无 ApplicationView）的 `GetWindowDesktopId` 失败——它们不属于任何桌面，所有桌面上都显示。

**「离开桌面时截图缓存」评估**：作为主方案不需要。缩略图对 cloak 窗口是实时的，切换前后都能直接用。仍要留意两类情况：
1. **应用自己选择在 cloak 时停画**（未验证）：Chromium 系浏览器、部分视频播放器会根据 `DWMWA_CLOAKED` / 遮挡状态停止渲染，这时缩略图显示的是 cloak 前的最后一帧——效果正好等于「离开时的截图」，不需要另做缓存；需要真机确认它不会变黑。
2. **最小化窗口**：缩略图没有内容（旧原型已知），但最小化窗口本来就不出现在桌面滑动画面里，不需要处理。

如果将来确实需要缓存：最便宜的时机是 GlideShell 自己发起切换之前（我们知道时刻），用桌面复制拿当前帧（GPU 内复制一次 2560×1600，留在显存里给 DComp 用，不回读）；逐窗口 PrintWindow 每个 7–12 ms，不适合在手势热路径上做。

## 5. 原生四指切桌面基线（问题 4）

测法（`watch 25 --frames`，只读）：用户在触摸板上做原生四指横滑；本工具同时记录切换通知、屏幕帧和触摸板触点（Raw Input `RIDEV_INPUTSINK`，只解触点数和第 1 个手指的 X），然后逐帧估计画面整体水平平移占屏宽的比例（与起止帧做平移匹配），和手指 X 位移对照。

> 第一次录制时 `FourFingerSlideEnabled=0`（四指滑动被关闭，S2 01:46 的原始备份里没有这一项，应是之后写入的），7 次四指滑动都没有切桌面，只录到了触点。写入方式和时刻不明，是否需要额外通知才能生效以 S2 的结论为准。之后用户在「设置 → 蓝牙和设备 → 触摸板 → 四指手势」里临时打开，设置界面的做法是**删除** `FourFingerSlideEnabled` 值（不是写 1），与 S2 备份的原始状态一致（供 S2 参考）。

第二次录制（6 个四指手势、4 次切换）：

| 手势 | 手指 | 画面 | 通知 |
|---|---|---|---|
| 1 慢滑 1.4 s，X 91→34 mm | 位移 −6 mm 时（按下后 332 ms）画面开始动 | 进度随手指增长：−22 mm ≈ 20 %，−34 mm ≈ 40 %，−46 mm ≈ 60 %，−56 mm ≈ 73 %；抬指时 77 %，再 157 ms 补到 100 % | CurrentChanged 在**抬指后 34 ms** |
| 2/4/5 快甩 190–256 ms，位移 26–82 mm | 按下后 96–163 ms（位移 8–10 mm）开始动 | 抬指时画面还在中途，抬指后 185–197 ms 完成 | 抬指后 14–24 ms |
| 3 滑出 34 mm 再反向回到起点，2.7 s | — | 画面先滑出约 40 %，随后随手指一路退回 | **没有切换**（反向 = 取消） |
| 6 滑出 24 mm 后按住约 3 s | — | 滑到位后画面停住，按住期间只有零星的 2 帧小变化 | （录制在抬指时结束） |

结论：本机原生四指切桌面**跟手、可中途停住、可反向取消、松手才提交**；有约 6–10 mm 的起滑死区；比例约 1.45 % 屏宽 / mm，即手指滑约 69 mm（触摸板宽度的 55 %）画面走满一屏；收尾动画 150–200 ms。这与 GlideShell 的目标 1 基本一致，所以 **P5 要做的不是「从无到有」，而是比原生更好**：去掉死区、松手速度传递到弹簧（原生快甩的收尾是固定时长）、与三指手势同一套物理与手感、可定制比例。如果 P5 做不到明显更好，保留原生四指手势是合理的默认值。

> 进度估计的精度有限：画面里大块单色区域会让平移匹配在两个解之间跳，表中取匹配误差低、前后连续的那一组；结论（跟手、可逆）不依赖精确数值。

**仍需用户主观确认**：原生四指的跟手感是否有明显延迟；69 mm 一屏的比例是否舒服；6–10 mm 死区是否感觉「粘」；快甩的收尾速度是否自然；中途反向时有没有卡顿。

## 6. 推荐的 P5 实现路线

1. **接管手势**：P5 启用时关闭原生四指横滑（实测 `FourFingerSlideEnabled=0` 时原生四指横滑不再切桌面；如何让写入可靠生效、如何崩溃还原走 S2 的结论），GlideShell 自己识别四指横滑。
2. **覆盖层**：每个显示器一个全屏（或工作区）置顶窗口，`WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST`，不带 `WS_EX_APPWINDOW`。这类窗口没有 ApplicationView，跨桌面切换全程可见（实测 0 帧丢失）。内容用 DirectComposition 视觉树（与 S4 的渲染方案共用）。
3. **两层画面**：
   - 背景层：当前桌面背景用 `Progman` 的 DWM 缩略图；目标桌面若设置了分桌面壁纸（`GetWallpaperPath` 非空）就自己解码壁纸文件，否则两边共用背景。
   - 窗口层：按 `EnumWindows` 的 Z 序列出顶层窗口，用 `IVirtualDesktopManager.GetWindowDesktopId`（文档化接口）分到「当前桌面 / 目标桌面」，各自注册 DWM 缩略图，摆在 `DWMWA_EXTENDED_FRAME_BOUNDS` 的位置。两边都是实时内容。
   - 手势进行中只改两层的水平偏移（进度 × 屏宽），在渲染线程按合成器时钟更新。
4. **提交**：松手后按投影落点决定提交 / 取消（ROADMAP 3.2「松手才决定」）。提交时尽早在一个专用 MTA 线程上调 `SwitchDesktop(target)`（调用要阻塞 5–13 ms，不能放在输入或渲染线程）；真实切换在覆盖层下面一帧完成，覆盖层继续把弹簧动画播完（切换后旧桌面窗口被 cloak，但它们的缩略图仍然实时，不影响动画）。
5. **收尾**：动画结束且已收到 `CurrentVirtualDesktopChanged` 后，再等 2–3 个合成帧（盖住逐个解除 cloak 的那一帧中间态）后隐藏覆盖层。覆盖层最后一帧与真实桌面逐像素对齐时，撤层无跳变。
6. **焦点**：撤层后激活目标桌面 Z 序最上面的可激活窗口（办法见 3.3，需要 P5 前置实验选定）；空桌面就把前台交给 Progman。
7. **外部切换**：注册 `IVirtualDesktopNotification`，跟踪 Task View、Ctrl+Win+←/→ 等其他途径的切换，用于更新「当前桌面」和缩略图注册。
8. **取消**：不调任何切换 API，覆盖层弹回 0 后撤掉，系统状态完全不变。

## 7. 风险与应对

| 风险 | 应对 |
|---|---|
| **系统更新改 IID 或方法表**（23H2→24H2 就是同 IID 改布局） | ① 布局表按构建号选（`VirtualDesktopLayout`）；② **启动自检**：`GetProxyDllInfo` 读代理格式串，核对 IID 存在、方法数、每个用到的槽位的参数指纹（`selftest`，耗时几毫秒、只读）；③ 不通过就禁用 P5 直接切换，恢复原生四指手势并提示「当前系统版本未适配」；④ 不用 IsRemote、IApplicationView 高位槽等未核对的方法。新构建出来时只要跑一次 `proxy` 就能看到新表，对照参考项目更新布局 |
| explorer 重启 | COM 代理全部失效（RPC_E_DISCONNECTED / 0x800706BA）、通知断开：调用失败时重建 ImmersiveShell 并重新 Register；同时监听 `TaskbarCreated` 消息 |
| 焦点交接不可靠 | 见 3.3；P5 开工前先做激活实验，选不出可靠方案就降级为「至少不把焦点留在看不见的窗口上」 |
| 应用在 cloak 时停画 | 缩略图停在最后一帧，可接受；需真机确认 Chromium / 视频播放器不会变黑 |
| 一帧中间态 | 覆盖层多保留 2–3 个合成帧 |
| 动画版 API 并发崩溃 explorer（MScholtes v1.21 说明） | 只用 SwitchDesktop，不用 SwitchDesktopWithAnimation |
| 跨进程调用阻塞 | 所有 VD COM 调用放在专用 MTA 线程；本 spike 全程 MTA，通知回调直接到 RPC 线程，不需要消息泵 |
| 多显示器 | 虚拟桌面是所有显示器一起切；每个显示器一个覆盖层，窗口按所在显示器分配（本机单屏，未测） |
| 与原生手势冲突 | 依赖 S2：P5 启用 = 关闭原生四指滑动，退出 / 崩溃时还原 |

## 8. 待真机验证

1. 原生四指的主观手感（第 5 节末尾的问题），作为 P5 手感对照。
2. 真实应用（Chrome/Edge 播放视频、Windows Terminal、UWP）放在其他桌面时，缩略图是实时、停在最后一帧还是变黑。本次用户选择跳过。
3. 焦点交接：用正常方式启动（不是由前台应用启动）的进程，测 3.3 的候选 1/2/3。
4. 分桌面壁纸：目标桌面壁纸不同时 `GetWallpaperPath` 的返回和背景层的拼法。
5. 60 Hz 下的中间态帧是否仍出现（本次 165 Hz）。
6. 多显示器。

## 9. 工具用法（`spikes/S3.VirtualDesktop`）

```
dotnet run -c Release --project spikes/S3.VirtualDesktop -- <命令>
```

| 命令 | 作用 | 是否改变状态 |
|---|---|---|
| `info` | 系统版本、IID 注册、QueryService / QI 探测、legacy IID 对照 | 只读 |
| `proxy [IID…]` | 从代理 DLL 读方法表 | 只读 |
| `selftest` | 槽位表与本机格式串逐项核对（GlideShell 启动自检的原型） | 只读 |
| `list` / `current` | 枚举桌面（与注册表 VirtualDesktopIDs 交叉核对）/ 当前桌面 | 只读 |
| `windows` | 顶层窗口所在桌面、cloak、有无 ApplicationView、是否钉住 | 只读 |
| `watch [秒] [--frames]` | 切换通知；`--frames` 同时录屏幕帧和触点，输出手势对照表 | 只读 |
| `thumbs` | 其他桌面上的真实窗口与 Progman 的缩略图 / WGC / PrintWindow | 只读（显示一个宿主窗口） |
| `thumbtest --yes [--dx]` | 自建测试窗口移到其他桌面，测三种取法 | 移动本工具自己的窗口 |
| `switch --yes [--mode plain\|anim\|both\|viewswitch] [--with-window [--activate fg\|fgui\|view\|setfocus]]` | 切换测试 | **切换桌面** |

实现要点：未公开接口一律按 vtable 槽位用函数指针调用（不用 `[ComImport]`：.NET 5+ 不支持 HString 封送，GlideShell 还要 NativeAOT）；通知接收器是手写 vtable + `UnmanagedCallersOnly`。这两块可以直接作为 `GlideShell.Platform` 虚拟桌面服务的起点。
