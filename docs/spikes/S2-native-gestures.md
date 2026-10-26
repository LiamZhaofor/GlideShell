# S2 原生手势的程序化开关

> 状态：**已验证，可以自动完成**（2026-10-26）。静态分析给出生效路径，本机实测 Explorer 生效值随之改变，用户手指验证 F1–F8 全部符合预期（第 7 节）。
> 工具：`spikes/S2.NativeGestures/`（独立控制台项目，不在 `GlideShell.slnx` 里）。

## 1. 结论

- **能自动生效，不需要打开「设置」**。方法：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad` 下的值，然后调用 Explorer 内部「设置缓存」服务的 `Refresh(SettingId)`。这与「设置」应用自己的做法一模一样。
- 三指 / 四指手势真正的生效值在 **Explorer 进程（twinui.dll）里的设置缓存**。注册表只是持久化层；Explorer 不监听这个键的变化。所以：
  - 只写注册表：注册表变了，Explorer 继续用旧值（E1）。这就是旧原型「写了不一定生效」的原因：值要等 Explorer 重启或注销后才会生效。
  - 广播 `WM_SETTINGCHANGE`（旧原型的做法）：无效（E2）。
  - `SPI_GETTOUCHPADPARAMETERS` → `SPI_SETTOUCHPADPARAMETERS` 原样回写：无效（E3）。`TOUCHPAD_PARAMETERS` 里本来就没有三指 / 四指字段，它管的是内核侧的轻点、双指等设置。
  - 写注册表 + `Refresh(SettingId)`：立即生效，读回一致（E4）。手指确认：只写注册表时三指上滑没有反应，`Refresh` 之后立刻弹出任务视图（F2/F3）。
- 同一个服务还提供只读的 `GetValue(SettingId)`，可以**读出 Explorer 当前的生效值**。因此「是否已接管 / 是否已还原」可以由程序自己校验，不靠用户感觉。
- 「一键退回原生手势」和「崩溃后自动还原」都可以做成全自动：看门狗按备份还原注册表，再对全部手势项调用 `Refresh`，最后读回校验。不可用时（接口在未来版本变了、Explorer 没在运行）退化为「只写注册表，提示 Explorer 重启后生效，可由用户确认重启 Explorer」。
- 顺带纠正旧原型的一条结论：`*FingerSlideEnabled = 0` **是合法值**（资源名 `SlideSet_Nothing` = 无）。手指验证中，`FourFingerSlideEnabled=0` 后四指滑动完全不触发（F5）。旧原型看到「0 不生效」，很可能也是缓存没刷新造成的。

## 2. 生效机制（证据链）

分析对象（本机 Windows 11 Pro 26H2，OS 10.0.26300，UBR 9711）：

| 文件 | 版本 | SHA-256 前 16 位 |
|---|---|---|
| `twinui.dll`（Explorer 进程内） | 10.0.26100.9549 | B2262A842ADF22F7 |
| `SettingsHandlers_nt.dll`（设置应用的触摸板页） | 10.0.26100.9711 | BEBC0CAEFD7F3CB0 |
| `OneCoreUAPCommonProxyStub.dll`（接口代理/存根） | 10.0.26100.9711 | 6BF71990707394CB |

IDA MCP 当时没有连上，分析用的是自写的启发式 x64 交叉引用工具：RIP 相对寻址、IAT 调用、`.pdata` 函数边界、数据指针表。下文地址都是 RVA。

### 2.1 谁在用这些值：twinui.dll 的设置缓存

- 在 System32 里按值名搜索：只有 `twinui.dll`、`SettingsHandlers_nt.dll`、`SettingsEnvironment.Desktop.dll` 含有 `ThreeFingerUp` 等值名。手势识别和动作分发位于 twinui 的 `shell\twinui\touchpadgestureprovider\…`、`shell\twinui\touchpadactions\…`。
- twinui 有一张设置描述表，位于 `0x3C2BE0`，每项 40 字节：`{ SettingId, 键路径, 值名, 缺省值, 0 }`。触摸板相关的编号是 `0x0F`–`0x27`（见第 3 节）。
- 读取函数 `0x30198`：持 SRW 锁查缓存，未命中才调用 `RegGetValueW`。
- 刷新函数 `0x2FF88(this, id, notify)`：调用 `0x30198` 从注册表重读这一项；值变了就 `SendNotifyMessageW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, L"SettingsCacheChangeMessage")`，并逐个回调进程内订阅者。
- `0x2D2010` 是接口方法槽 [3] 的 adjustor thunk：`this -= 0x68; r8b = 1; call 0x2FF88`。也就是 `Refresh(id)` = 带通知的刷新。
- twinui 里 `RegNotifyChangeKeyValue` 的全部调用者只监听 ContentDeliveryManager、PlayToReceiver、Accessibility\ATConfig 这几个键，**没有监听 PrecisionTouchPad**。所以外部写注册表不会被察觉。
- twinui 窗口过程 `0x4CBA0` 处理的是 `WM_SETTINGCHANGE` + `"SettingsCacheChangeMessage"`：缓存刷新**之后**，由它通知界面。外部广播这条消息不会让缓存重读注册表。

### 2.2 设置应用怎么应用：SettingsHandlers_nt.dll

- 手势页的处理器（`touchpadadvancedgesturehandlers.cpp`）写值走 `0x50704 → 0x529CC`（`RegCreateKeyExW` + `RegSetValueExW`），紧接着调用 `0x1CA48C`（`touchpadsingleton.cpp`）。
- `0x1C9D3C` 负责取服务：`CoCreateInstance(CLSID_ImmersiveShell {C2F03A33-21F5-47FA-B4BB-156362A2F239}, IServiceProvider)`，再 `QueryService(SID {53660488-8855-460B-A9AB-5CFC6B5012CA}, IID {4214F6FA-EB36-4E2F-9CA2-23FDC1832DF7})`。
- `0x1CA48C` 的实际逻辑：`mov rax,[rcx]; call [rax+18h]`，即 `vtbl[3](settingId)`，`settingId` 取自处理器对象的成员（`[rbx+18h]` 等）。随后的 `0x1CA508` 只刷新设置页自己的 SPI 状态和 UI。
- 设置应用里的编号映射表位于 `0x2CCD50`，`{Id, 值名, 缺省值}` 与 twinui 的表**逐项一致**，是同一个枚举。

### 2.3 接口方法表（MIDL 格式串）

- `HKCR\Interface\{4214F6FA-…}` 的 ProxyStubClsid32 是 `{95E15D0A-66E6-93D9-C53C-76E6219D3341}`，对应 `OneCoreUAPCommonProxyStub.dll`。接口在注册表里没有名字。
- 存根头（RVA `0x3F330`）：`DispatchTableCount = 8`，即 IUnknown 3 个方法 + 5 个自有方法。各方法的过程格式串（`ProcString 0x4F1D02` + `FmtStringOffset 0x4F21EA`）：

| 槽 | 参数 | 用途（结合 twinui 实现与实测） |
|---|---|---|
| 3 | `[in] enum32 id` → HRESULT | `Refresh(id)`：从注册表重读并通知（设置应用写完后调用的就是它） |
| 4 | `[in] enum32 id, [out] long*` | 对触摸板编号返回 `E_FAIL`（实测），用途不明，不用 |
| 5 | `[in] enum32 id, [out] long*` | `GetValue(id)`：返回缓存里的生效值，注册表没有该值时返回缺省值（实测） |
| 6 | `[in] interface*, [out] long*` | Advise（订阅） |
| 7 | `[in] long` | Unadvise |

## 3. 取值编码

编号与缺省值取自 twinui 表（`0x3C2BE0`），含义取自 SettingsHandlers_nt 的「值 → 资源名」表（`0x2CCCB0`、`0x2CC160`、`0x2CC080`）。

| 值名 | Id | 缺省 | 取值 |
|---|---|---|---|
| ThreeFingerTapEnabled / FourFingerTapEnabled | 0x0F / 0x10 | 1 / 2 | 轻点表 |
| ThreeFingerSlideEnabled / FourFingerSlideEnabled | 0x11 / 0x12 | 1 / 2 | 0 无，1 切换应用并显示桌面，2 切换桌面并显示桌面，3 音频和音量，**0xFFFF 按方向自定义** |
| ThreeFingerLeft / Up / Right / Down | 0x14 / 0x16 / 0x18 / 0x1A | 1 / 2 / 1 / 3 | 方向动作表（仅自定义模式下生效） |
| FourFingerLeft / Right / Up / Down | 0x1C / 0x1E / 0x20 / 0x22 | 4 / 4 / 2 / 3 | 同上 |
| CustomThreeFingerTap / CustomFourFingerTap | 0x24 / 0x26 | 1 / 2 | 轻点表 |
| `*KeyParams`（自定义快捷键参数） | 对应方向 Id + 1 | — | 本工具不写，只原样备份 / 还原 |

- 方向动作表：0 无，1 切换应用，2 任务视图，3 显示桌面，4 切换桌面，5 FocusApp，6 新建桌面，7 删除桌面，8 前进，9 后退，10 SnapLeft，11 SnapUp，12 SnapRight，13 SnapDown，14 下一曲，15 上一曲，16 音量+，17 音量-，18 静音，0xFFFF 自定义快捷键。
- 轻点表：0 无，1 搜索，2 通知中心，3 播放/暂停，4 鼠标中键。
- 四指在本机注册表里没有 `FourFingerSlideEnabled`，Explorer 用缺省值 2，`GetValue` 读到的也是 2。还原时必须把「备份里不存在的值」删掉，而不是写成 0（E6/E7 验证过）。
- 预设 1 / 2 展开成方向值，就是上表的缺省方向值。预设 3 的左右对应关系没有验证，工具拒绝展开，需要显式写值。

## 4. 候选机制对比

| 机制 | 结果 | 证据 |
|---|---|---|
| 只写注册表 | ✗ Explorer 继续用旧值，直到重启或注销 | E1；twinui 不监听该键 |
| 写注册表 + 广播 `WM_SETTINGCHANGE`（lParam = 键路径） | ✗ | E2；twinui 只认 `"SettingsCacheChangeMessage"`，而且那是刷新之后的通知 |
| 写注册表 + `SPI_GET/SETTOUCHPADPARAMETERS` 原样回写 | ✗ | E3；该结构不含三指/四指字段 |
| **写注册表 + 设置缓存 `Refresh(id)`** | ✓ 立即生效，可读回校验 | E4–E7；与设置应用同一路径 |
| 重启 Explorer | 应该有效（缓存重建时从注册表读），但会闪任务栏、关掉资源管理器窗口 | 未测（会打断用户），只作降级手段 |
| 重启服务 | 不相关：生效值在 Explorer 进程里，不在服务里 | 静态分析 |

## 5. 本机实验记录（2026-10-26，用户同意后执行）

动手前先备份：`reg export` 生成 `.reg`，工具 `backup` 生成 `.json`。两份都放在共享笔记目录 `S2-ptp-original-backup.*`。每次写入前，工具还会自动备份到 `%LOCALAPPDATA%\GlideShell\S2\backups\`。

| # | 命令 | 注册表 | Explorer 生效值 | 结论 |
|---|---|---|---|---|
| E0 | `show` | ThreeFingerUp=0 | 0 | 起点：两者一致 |
| E1 | `apply ThreeFingerUp=2 --via none` | 2 | **0** | 只写注册表不生效 |
| E2 | `apply ThreeFingerUp=2 --via wm` | 2 | **0** | 广播无效 |
| E3 | `apply ThreeFingerUp=2 --via spi` | 2 | **0** | SPI 回写无效（返回成功） |
| E4 | `apply ThreeFingerUp=2 --via cache` | 2 | **2** | Refresh 立即生效 |
| E5 | `restore <原始备份>` | 0 | 0 | 14 项全部 Refresh，注册表与备份逐值一致 |
| E6 | `apply takeover-4` | FourFingerSlideEnabled=0（新建） | 0 | 新建值同样生效 |
| E7 | `restore <原始备份>` | 该值已删除 | 2（缺省） | 删值 + Refresh 后回到缺省 |

收尾核对：重新 `reg export`，与动手前的 `.reg` **逐行相同**（25 行，含 `Status` 子键）。`show` 显示 14 项生效值都与注册表一致。

## 6. 工具用法（`spikes/S2.NativeGestures`）

```
dotnet run --project spikes/S2.NativeGestures -- show
dotnet run --project spikes/S2.NativeGestures -- backup <file.json>
dotnet run --project spikes/S2.NativeGestures -- apply <profile | Name=Value,...> [--via cache|none|wm|spi[,...]] [--dry-run]
dotnet run --project spikes/S2.NativeGestures -- restore <file.json> [--via ...] [--dry-run]
dotnet run --project spikes/S2.NativeGestures -- notify <all | Name ...>
dotnet run --project spikes/S2.NativeGestures -- profiles
```

- `show` 只读：每一项的注册表值、Explorer 生效值和含义，主键下的其他值，以及 SPI 触摸板参数。
- `apply` / `restore` 写入前都自动备份，结束时读回校验。退出码：0 = 注册表与生效值一致；1 = 注册表不符；3 = 注册表已写入但 Explorer 仍是旧值。
- 预设：`takeover-3up`（三指上下 = 无，左右保持；本机当前就是这个状态）、`takeover-3`、`takeover-4`、`takeover-34`、`windows-default`、`test-3up-taskview`、`test-4-desktops`。

## 7. 需要用户手指验证的步骤

第 5 节证明的是「Explorer 缓存里的生效值变了」。本节用**可见的动作**确认「手势分发确实只看这个缓存」。每段做完都 `restore`，所有步骤结束后跑一次 `show`，确认生效值与注册表一致。

**2026-10-26 已由用户在本机执行 F1–F8，结果全部符合预期**（见下表「实测」列）。之后 `reg export` 与动手前逐行一致，`show` 里 14 项没有任何不一致。F9 未做。

准备：
- 先 `dotnet build spikes/S2.NativeGestures`，下表命令都用 `spikes\S2.NativeGestures\bin\Debug\net10.0-windows\S2.NativeGestures.exe` 执行。
- 先做一份备份 `backup ptp-before.json`。
- 打开至少两个窗口，并建好至少两个虚拟桌面。

| # | 操作 | 手势 | 预期 | 实测 |
|---|---|---|---|---|
| F1 | （本机当前状态，不写） | 三指上滑 | 无反应（ThreeFingerUp=0） | ✓ 无反应 |
| F2 | `apply ThreeFingerUp=2 --via none` | 三指上滑 | **仍无反应**：只写注册表无效 | ✓ 无反应（注册表 2 / 生效值 0） |
| F3 | `notify ThreeFingerUp` | 三指上滑 | **出现任务视图**：Refresh 立即生效 | ✓ 出现任务视图 |
| F4 | `restore ptp-before.json` | 三指上滑 | 无反应 | ✓ 无反应 |
| F5 | `apply takeover-4` | 四指左/右滑；四指上滑 | 不切换桌面；不出现任务视图（确认 `FourFingerSlideEnabled=0` 就是「无」） | ✓ 都无反应 |
| F6 | `restore ptp-before.json` | 四指左/右滑 | 恢复切换桌面 | ✓ 恢复切换（该值已删除，生效值回到缺省 2） |
| F7 | `apply takeover-3` | 三指左/右滑 | 不切换应用 | ✓ 不切换 |
| F8 | `restore ptp-before.json` | 三指左/右滑；三指轻点 | 恢复切换应用；轻点仍是中键（确认轻点不受影响） | ✓ 恢复切换；轻点仍是中键 |
| F9（可选，会闪任务栏） | `apply ThreeFingerUp=2 --via none` 后手动重启 Explorer | 三指上滑 | 出现任务视图：证明重启是有效降级；随后 `restore` | 未做 |

## 8. 对 GlideShell 的设计建议（供主会话汇总）

- `Platform` 层放一个原生手势控制器，分三步：
  1. 首次接管前把整个主键备份成持久文件（不能只放内存，看门狗也要能读到）。
  2. 接管 = 写 GlideShell 要独占的方向 + 对改动项调用 `Refresh` + 用 `GetValue` 读回校验。
  3. 退出、托盘「退回原生」、看门狗发现主进程崩溃时，都执行同一个还原流程：按备份还原注册表（备份里没有的值要删掉）→ 对全部手势 Id 调用 `Refresh` → 读回校验。
- 只接管需要的方向。三指轻点（`ThreeFingerTapEnabled`）不动，符合「三指轻点用系统原生」的目标。
- 顺序必须先写注册表、再 `Refresh`：缓存是从注册表重读的，Explorer 重启后也只认注册表。
- 降级与检测：`QueryService` 失败、方法返回失败，或读回不一致时，不再自动处理，降级为「注册表已写，Explorer 重启后生效」，并提示用户可以一键重启 Explorer（需用户确认）或打开设置页。每个系统版本首次运行时都要走这条读回校验，不能假设 GUID 和方法槽不变。
- 风险：接口未公开，GUID 和槽位来自 26100 系列，未来的大版本可能变。好在读回校验能发现这种变化，不会静默失效。

## 9. 未覆盖

- F9：重启 Explorer 作为降级手段是否有效（理论上有效，未实测）。
- 多用户、远程桌面会话：设置缓存是每个 Explorer 会话一份，理论上只影响当前会话，未测。
- 设置页开着时外部改值，设置页的界面会显示旧值，直到重新进入该页。只影响显示，未测。
