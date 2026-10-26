# S4 渲染延迟（P1 技术验证）

> 日期：2026-10-26 · 分支 `claude/spike-s4-render-latency-5ac2c3` · 代码 `spikes/S4.RenderLatency/`（独立项目，不在 `GlideShell.slnx` 里）
> 原始数据：`spikes/S4.RenderLatency/results/`（见第 7 节）。本文只记录 spike 结论，ROADMAP 的汇总由主会话来做。

## 结论速览

**能做到，而且渲染线程本身不是瓶颈。** 非分层弹出窗口 + DirectComposition + DWM 缩略图，由专门的渲染线程按合成器时钟驱动：60Hz 下帧间隔严格等于刷新周期，每帧 CPU 工作不到 1ms（5 个缩略图），热路径零分配，UI 线程卡死也不影响画面。真正要设计的是四件事：

1. **在帧内什么时候取样**：决定延迟是 1 帧还是 2 帧。
2. **每秒一次的 `DwmUpdateThumbnailProperties` 阻塞**。
3. **缩略图多了以后 DWM 自己掉帧**。
4. **高刷新率**：本机接上电源后是 165Hz，此时经典缩略图路径在 N=2 时 DWM 就只能呈现约 60% 的刷新。在这台机器上，只有未公开的共享缩略图视觉能在 165Hz 下跟满刷新率（N≤15）。**要不要依赖这个未公开 API，需要主会话 / 用户决定**（见 2.6 节）。

### 关键数字（本机 ASUS / Radeon 780M / 2560×1600，覆盖层 1280×800，P50 / P95 / max）

| 项目 | 60Hz（周期 16.67ms） | 165Hz（周期 6.06ms） |
|---|---|---|
| 渲染线程帧间隔，5 个缩略图 | 16.66 / 16.93 / 17.97，掉帧 0/240 | 经典 6.07 / 7.40 / 13.86，掉帧 11/660；共享视觉 6.06 / 6.12 / 19.02，掉帧 4/655 |
| 渲染线程每帧工作，5 个缩略图 | 0.87 / 1.26 / 12.79（max 是每秒一次的阻塞） | 经典 0.67 / 0.98 / 12.42；共享视觉 0.07 / 0.21 / 1.40 |
| 每帧托管分配 | 0 B（所有场景） | 0 B |
| tick 时取样 → 上屏（VBlank） | 33.24 / 33.41（恰好 2 帧） | N=0 时 12.03 / 12.11（2 帧）；经典 N=5 因 DWM 掉帧升到 21.28 / 27.27 |
| 晚取样（下一帧起点前约 4ms）→ 上屏 | 21.25 / 21.45（1 帧 + 余量） | 共享视觉 9.98 / 10.02（lead2 时 7.98 / 8.01）；经典 17.72 / 25.17（DWM 掉帧） |
| DComp 与缩略图落在同一合成帧 | 远离帧边界时 100%（305/305）；tick 时立刻发 98.5%（338/343） | 共享视觉 100%（911/911）；经典 tick 时 89%（461/517），lead4 100%（509/509） |
| 共享缩略图视觉（未公开，dwmapi#147） | 可用；每帧工作 0.08ms，无阻塞，227/227 同帧 | 可用；N=20 时每帧工作 0.12ms，726/726 同帧 |

### 给 P3 的推荐管线（详见第 4 节）

1. 覆盖层：`WS_POPUP` + `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP`，**不要** `WS_EX_LAYERED`；内容全部来自 DComp 视觉树（`CreateTargetForHwnd(topmost: FALSE)`）和 DWM 缩略图。
2. 专门的渲染线程：`DCompositionWaitForCompositorClock(1, &stopEvent, timeout)` 作帧时钟（Win11）；`DwmFlush` 只作退路。
3. **不要在 tick 一到就取样提交**：tick 就是 DWM 一帧的起点，这时提交的东西只能进下一帧，延迟固定 2 帧，而且在起点后约 0.1–1ms 内还存在「抢进当前帧」的竞争（这就是错帧的来源）。改为在「下一帧起点 − lead」取样：先取最新输入快照，再更新全部缩略图和 DComp 属性，最后 Commit，lead 要盖住本帧工作量加余量。
4. 缩略图做成可插拔的后端，**优先走共享缩略图视觉**：运行时探测 dwmapi#147，失败就退回 `DwmUpdateThumbnailProperties`。共享视觉的属性随 DComp Commit 原子生效，没有每帧同步跨进程调用，也就没有每秒一次的阻塞；60Hz、5 个缩略图时 DWM 合成耗时从 5.06ms 降到 2.24ms；165Hz 下 N=15 仍保持 98% 呈现率，晚取样时取样 → 上屏只要 8–10ms。
5. 运行时自测延迟：用 `DCompositionGetStatistics` / `GetTargetStatistics` 按时间戳找落点帧（第 1.3 节的算法），与桌面复制的地面真值一致率 ≥ 95%，P3 可以直接拿来做延迟直方图，不需要桌面复制。
6. 运动期间的实时缩略图数量要有上限，或者降级：经典缩略图在 60Hz 下 N=10 时 DWM 只能呈现 77% 的刷新，N=20 时只剩 63%；165Hz 下 N=2 就只有 63%。共享视觉在 165Hz、N=20 时也降到 83%。这些都是 DWM 跟不上，不是我们的线程慢。

### 与旧原型对比

| | 旧原型（MissionFlow M2，60Hz） | S4 管线（60Hz，5 个缩略图） |
|---|---|---|
| 帧间隔 P50 / P95 | 15 / 36ms（`ApplyVisual` 调用间隔，不对齐 vsync，也不等于上屏帧） | 16.66 / 16.93ms（对齐合成器时钟；桌面复制确认几乎每个刷新都推进一帧） |
| 每帧工作 | 主线程约 12ms（含同步 `Update()` 整窗重绘） | 渲染线程 0.87ms；UI 线程每帧 0 工作 |
| 缩略图位移何时生效 | 依赖宿主窗口重绘；WM_INPUT 处理中更新「不生效」 | 不重绘、不 Commit 也生效（305/305）；UI 线程每 500ms 卡死 200ms 也照常（306/306） |
| 输入 → 画面 | 自适应平滑额外滞后 25–90ms | 取样 → 上屏 33ms（tick 取样）/ 21ms（晚取样），不含输入栈 |

### 还没测到的

见第 6 节。最重要的四项：
- 全屏覆盖层：GPU 开销随面积增长，按约束未经同意不做。
- 60Hz 下共享视觉 N=10–20 的曲线：测量途中刷新率切到了 165Hz。
- 经典缩略图在 165Hz 下掉帧的根因。
- 真实输入（ScanTime）到取样这一段：属于 S1 / P2。

---

## 1. 测量方法

### 1.1 被测管线

- **窗口**（UI 线程，`Overlay.cs`）：主显示器居中 1280×800 物理像素（2560×1600 的四分之一面积），`WS_POPUP` + `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP`，不抢焦点。退出方式：全局轮询 Esc（`GetAsyncKeyState`，不装钩子、不吞按键）、鼠标点击、按场景总时长自动关闭，另有看门狗线程到点直接结束进程。UI 线程只跑消息循环。
- **DComp 视觉树**（渲染线程，`Compositor.cs`）：D3D11 设备 → `DCompositionCreateDevice3` 得 `IDCompositionDesktopDevice` → `CreateTargetForHwnd(hwnd, topmost: FALSE)`。根下三个视觉：整窗深灰底色（`IDCompositionVisual3::SetOpacity` 每帧随进度变化，模拟总览压暗）、顶部不透明黑色「校验条」、红色标记。纯色内容是 DComp surface 加 D3D11.1 `ClearView` 填一次（`BeginDraw` 返回的是图集里的一块，只能按 offset 清那一块），之后每帧只改属性，不重绘。
- **DWM 缩略图**（`Thumbnails.cs`）：真实窗口沿用旧原型的收录规则；不足 N 个时循环复用源窗口，凑够 N 个缩略图。本机当时收录 5 个：2 个 Chrome（2582×1550）、2 个 Tauri 窗口（1624×1033、2558×1398）、1 个资源管理器（1709×1118）。每帧矩形在「源窗口按比例映射到覆盖层的位置」和「网格平铺位」之间插值，进度 p(t) = 0.5 − 0.5·cos(2πt / 2s)，用 QPC 计算。另有 1 个测试缩略图（见 1.2）。注册和每帧更新都在渲染线程上。
- **渲染循环**（`Renderer.cs`）：`DCompositionWaitForCompositorClock(1, &stopEvent, 100ms)` 或 `DwmFlush()` → 记醒来时刻 → （可选晚取样：自旋到指定时刻）→ 取样（QPC）→ 更新全部缩略图 → 改 DComp 属性 → `Commit()`。每帧记录写进预分配的结构体数组；测量窗口内用 `GC.GetAllocatedBytesForCurrentThread()` 核对分配量。

### 1.2 地面真值：桌面复制 + 帧序号编码

为了不靠肉眼判断「哪一帧上了屏」，校验条里放两行可解码的标记，横坐标都编码帧序号：x = 40 + (seq mod 64)·6。

- 上行：红色 DComp 视觉（`SetOffsetX`）。
- 下行：测试缩略图，源窗口是一个客户区纯绿的 GDI 窗口，藏在不透明校验条正下方；缩略图 1:1、只取客户区，所以屏幕上的颜色是确定的。`--external-probe` 时这个源窗口改由子进程创建，用来验证跨进程的情况。

另一个线程（`DesktopCapture.cs`）用 DXGI 桌面复制（`IDXGIOutputDuplication`）逐帧取合成结果，**只把校验条里的两行像素**拷回 CPU 解码，不读、也不保存任何其他屏幕内容。每一帧桌面复制得到：`LastPresentTime`（QPC），以及屏幕上 DComp 标记和缩略图各自是第几帧。据此能算出：

- 取样 → DWM 呈现：`LastPresentTime − 该帧取样时刻`；
- 同帧与否：两行解码出的 seq 是否相等；
- 屏幕平滑度：相邻两次刷新之间，屏幕上的帧序号前进了多少（1 = 平滑，0 = 重复，≥2 = 跳帧）。

桌面复制本身会改变 DWM 的时序（第一次冒烟测试里 DWM 帧起点后移了约 4ms），所以帧间隔和缩略图开销曲线都在**不开**桌面复制的场景里测；开桌面复制的场景（`cap-*`）只用来校准下面的估算方法、回答同帧问题。

### 1.3 Win11 合成帧统计与「落点帧」估算

本机 dcomp.dll 有按名字导出的 `DCompositionGetFrameId` / `GetStatistics` / `GetTargetStatistics` / `WaitForCompositorClock` / `BoostCompositorClock`（#1100–#1104）。渲染线程每帧收集已完成合成帧的统计（startTime、targetTime、presentTime、completedStats.time），并按帧 ID 存起来。实测得到的 DWM 时序模型：

- **合成器时钟 tick = DWM 一帧的 startTime**：醒来时刻 − 帧起点 P50 0.00–0.01ms，P95 ≤ 0.05ms；帧起点比 VBlank 晚约 0.08ms。
- DWM **每个刷新周期建两个帧 ID**：第一个在 VBlank 时开始并呈现（startTime → presentTime 就是 DWM 的合成耗时）；第二个在呈现之后开始，**不呈现**。所以「Commit 后读 CREATED，再 +1」这种估算是错的。
- `completedStats.time` 是该帧真正开始扫描输出的那个 VBlank，本文的「上屏」都指这个时刻。
- tick 比新帧 ID 的出现早几十微秒：tick 后立刻读 `COMPOSITION_FRAME_ID_CREATED`，读到的是上一帧。

**落点估算**（`Analysis.Landing`）：startTime ≥ 更新发出时刻的第一个「会呈现」的合成帧，它的 `completedStats.time` 就是上屏时刻。与桌面复制核对：cap-wait-n5 255/259、delay12 297/301、uistall 302/302、nodcomp 305/305；165Hz 共享视觉 845/891。不一致的几乎都是 1.4 节说的帧起点竞争。

### 1.4 场景（`S4.RenderLatency list`）

| 场景 | 说明 |
|---|---|
| `wait-nN`（N = 0/1/2/5/10/15/20） | 合成器时钟，N 个真实缩略图 + 1 个测试缩略图，不开桌面复制 |
| `wait-n5-rectonly` | 每帧只带 `DWM_TNP_RECTDESTINATION`（其余属性注册时设一次） |
| `wait-n5-nostats` | 循环里不调任何帧统计 / 帧 ID API，排除测量本身的干扰 |
| `flush-n5` / `flush-n20` | `DwmFlush` 作帧时钟 |
| `cap-*` | 同上，加桌面复制地面真值 |
| `cap-wait-n5-thumbsafter` | 先 Commit 再更新缩略图（默认相反） |
| `cap-wait-n5-nodcomp` / `cap-flush-n5-nodcomp` | 只更新缩略图，不 Commit、也不重绘（旧原型「缩略图不动」的条件） |
| `cap-wait-n5-uistall` | UI 线程每 500ms 故意 `Sleep(200)` |
| `cap-wait-n5-delay4/8/12` | 醒来后固定等 4/8/12ms 再取样 |
| `cap-wait-n5-lead2/4`、`cap-wait-n20-lead4` | 在「下一帧起点前 2/4ms」取样（按实测周期换算，60Hz / 165Hz 通用） |

每个场景预热 0.8s、测量 4s（60Hz 约 240 帧，165Hz 约 660 帧）。`--shared-visual` 把全部缩略图（含测试缩略图）换成共享缩略图视觉，见 2.6 节。165Hz 下 `delay8` / `delay12` 超过了一个周期，没有意义，不引用这两项；晚取样看 `lead2` / `lead4`。

### 1.5 环境与干扰因素

- Windows 11 Pro 10.0.26300；dcomp.dll 文件版本 10.0.26100.9549，dwmapi.dll 10.0.26100.8875。
- Ryzen 7 8845H，Radeon 780M 负责显示和 DWM 合成（另有 RTX 4050，未参与）。
- **刷新率在测量途中变了**：run1 / run2 在 60Hz 下完成；之后笔记本接上电源，显示器切到 165Hz（`Win32_VideoController.CurrentRefreshRate` = 165，`Win32_Battery.BatteryStatus` = 2）。共享视觉的曲线和 run3 都是 165Hz 的数据。我没有去改刷新率（属于系统设置）。
- 测量期间用户在正常使用电脑（Chrome、Claude 桌面应用等都开着），所以数字包含真实背景负载。run1 期间我在跑编译，N=10–20 那几项受了干扰，已作废，由 run2 干净重测（两次的趋势一致）。
- 渲染线程是普通优先级，没开 MMCSS。

## 2. 结果

### 2.1 帧间隔（问题 1）

渲染线程醒来间隔（ms）：

| 场景 | 60Hz P50 / P95 / max | 掉帧（>1.5 周期） | 165Hz P50 / P95 / max | 掉帧 |
|---|---|---|---|---|
| 经典 wait-n0 | 16.66 / 16.90 / 17.53 | 0 / 240 | 6.06 / 6.13 / 6.49 | 0 / 660 |
| 经典 wait-n5 | 16.66 / 16.93 / 17.97 | 0 | 6.07 / 7.40 / 13.86 | 11 |
| 经典 wait-n10 | 16.66 / 17.15 / 17.45 | 0 | 6.09 / 13.01 / 24.03 | **128** |
| 经典 wait-n15 | 16.67 / 17.22 / 33.43 | 3 | 7.12 / 14.16 / 38.76 | **157** |
| 经典 wait-n20 | 16.67 / 20.80 / 54.28 | 3 | 8.70 / 14.95 / 22.34 | **194** |
| 经典 flush-n5 | 16.63 / 19.13 / 23.05 | 0 | 9.09 / 12.41 / 17.20 | **271** |
| 经典 flush-n20 | 17.01 / 31.47 / 42.57 | **65** | 15.03 / 26.45 / 45.47 | **230** |
| 共享视觉 wait-n5 | 16.66 / 16.78 / 17.14（cap-wait-n5） | 0 | 6.06 / 6.12 / 19.02 | 4 |
| 共享视觉 wait-n20 | 未测 | | 6.06 / 6.14 / 8.57 | 0 |

- 60Hz 下合成器时钟非常稳定，偶发的掉帧几乎都是每秒一次的缩略图调用阻塞（2.5 节）：阻塞超过一个周期，渲染线程就错过一次 tick。
- 165Hz 下经典缩略图路径从 N=5 起渲染线程就跟不上 tick；N≥10 时帧间隔 P50 都超过一个周期。原因不在我们的线程（每帧工作 P50 只有 0.6–2ms），而是 DWM 整帧不呈现时 tick 也会跟着跳过（2.4 节）。共享视觉到 N=20 仍然 0 掉帧。
- `DwmFlush` 作时钟抖动明显更大（60Hz P95 19ms），165Hz 下更是半数帧都掉了，只适合作退路。
- **只看渲染线程不够**，还要看屏幕。下面是屏幕平滑度：用桌面复制按刷新聚合，统计相邻刷新之间屏幕上帧序号的「前进量:次数」，以及没有新画面的刷新次数。

| 场景 | 60Hz 前进量 | 60Hz 无新画面 | 165Hz 前进量 | 165Hz 无新画面 |
|---|---|---|---|---|
| 经典 cap-wait-n5（tick 取样） | 0:11 1:218 2:9 | 1 / 240 | 0:27 1:322 2:27 | **124** / 660 |
| 经典 晚取样（60Hz delay12 / 165Hz lead4） | 0:3 1:235 | 1 | 0:9 1:370 2:16 | **102** |
| 经典 cap-wait-n5-uistall | 0:1 1:239 | 0 | 0:16 1:363 2:20 | 111 |
| 经典 cap-flush-n5 | 0:3 1:236 | 0 | 0:81 1:183 2:1 | **198** |
| 经典 cap-wait-n20 | 0:18 1:102 2:2 | **52** | 几乎全是空帧 | **203** |
| 共享视觉 cap-wait-n5（tick 取样） | 1:119（2 秒） | 0 | 0:26 1:612 2:22 | 1 |
| 共享视觉 晚取样 lead4，N=5 / N=20 | — | — | 0:6 1:653 2:1 / 0:31 1:642 2:1 | 2 / 8 |

tick 时取样会出现「0 后面跟 2」的成对抖动，原因就是 1.3 节说的帧起点竞争：某帧抢进了当前帧，下一帧就显得重复。晚取样之后基本消失。165Hz 下桌面复制自己也会漏读（合并帧变多），所以经典路径的「无新画面」次数会偏高一些，但与共享视觉（同样开着桌面复制，只漏 1–8 次）对比，结论不变。

### 2.2 取样 → 上屏延迟（问题 2）

「上屏」= 该帧开始扫描输出的 VBlank（`completedStats.time`）。表里不含输入栈延迟（ScanTime → 取样，属于 S1/P2），也不含屏幕内扫描位置（覆盖层中心在屏幕中线附近，60Hz 时约再加 8ms，165Hz 约 3ms）和面板响应。

| 取样时机 | 60Hz P50 / P95 | 帧数分布（四舍五入到周期） | 165Hz P50 / P95 |
|---|---|---|---|
| tick 时立刻（合成器时钟） | 33.24 / 33.41（地面真值 33.27 / 35.68） | 2:232 3:7 4:1 | N=0：12.03 / 12.11；经典 N=5：21.28 / 27.27（DWM 掉帧）（共享视觉 12.04 / 12.76） |
| `DwmFlush` 返回后 | 28.19 / 29.30（地面真值 27.47 / 29.00） | 1:4 2:233 3:3 | 经典 N=5：17.64 / 19.82 |
| tick + 4ms | 29.23 / 29.39 | 2:236 3:3 | — |
| tick + 8ms | 25.22 / 25.35 | 1:15 2:220 3:3 | — |
| tick + 12ms（≈ 下一帧起点前 4.7ms） | **21.25 / 21.45** | 1:232 2:5 | — |
| 下一帧起点前 4ms（lead4） | — | — | 经典 N=5：17.72 / 25.17（地面真值）（共享视觉 **9.98 / 10.02**，同帧 911/911） |
| 下一帧起点前 2ms（lead2） | — | — | 经典 N=5：14.63 / 22.64（地面真值）（共享视觉 **7.98 / 8.01**，同帧 906/906） |

规律：**延迟 = 下一帧起点到取样时刻的距离 + 1 个周期 + 约 0.1ms**。DWM 在帧起点收集更新，合成大约 3–5ms 后呈现，在下一个 VBlank 开始扫描输出。所以 tick 时取样是最差的时机：刚错过当前帧，要整整等 2 个周期。`DwmFlush` 在 DWM 呈现之后才返回（比帧起点晚约 5ms），延迟反而低 5ms，代价是抖动大。

晚取样的风险在于余量：一旦取样到 Commit 的工作超过 lead，这一帧就晚一个周期。60Hz delay12 的 240 帧里有 3 帧晚了（38ms），都是每秒一次的缩略图调用阻塞造成的。

### 2.3 同帧与渲染线程上的可靠性（问题 3）

**同帧**（桌面复制解码，DComp 标记 seq − 缩略图 seq，「差值:次数」）：

| 场景 | 同帧 | 差值分布 |
|---|---|---|
| cap-wait-n5（tick 时：先缩略图后 Commit） | 338/343 | −1:5 |
| cap-wait-n5-thumbsafter（tick 时：先 Commit 后缩略图） | 280/287 | +1:7 |
| cap-wait-n5-delay4 / 8 / 12 | 313/313、300/300、305/305 | — |
| cap-wait-n5-uistall | 306/306 | — |
| cap-flush-n5 | 316/316 | — |
| cap-wait-n20（DWM 过载） | 152/223 | −1:71 |
| 165Hz 经典 cap-wait-n5 / uistall / lead4 | 461/517、501/530、509/509 | −1:56、−1:29、— |
| 165Hz 经典 cap-wait-n20 | 198/212 | −1:14 |
| 共享视觉 cap-wait-n5 / cap-wait-n20（165Hz，tick 取样） | 894/894、726/726 | — |
| 共享视觉 lead2 / lead4 / N=20 lead4（165Hz） | 906/906、911/911、738/738 | — |

- **`DwmUpdateThumbnailProperties` 和 DComp Commit 不是原子的**，各走各的通道，各自在「到达时已经开始的那帧之后」生效。
- 两种错帧机制都实测到了：
  1. **帧起点竞争**：tick 后约 0.1–1ms 内发出的更新，有时还能抢进当前帧（桌面复制显示这些缩略图只隔 7–10ms 就上屏了）；稍晚发出的那一半就进了下一帧。先发出的一方总是更早上屏，所以调换顺序时差值的符号也跟着反过来（−1 ↔ +1）。远离帧边界发出（delay 4/8/12）就是 100% 同帧。
  2. **DWM 过载**：N=20 时 DWM 来不及每个刷新都合成，DComp 批次会排队（屏幕上 DComp 落后 2 帧），缩略图属性却是「最后写入的生效」，于是缩略图跑到 DComp 前面去了。
- 共享缩略图视觉的属性随 DComp Commit 一起生效，在所有测试里 100% 同帧，包括 N=20 的过载情况。
- **在渲染线程调用 `DwmUpdateThumbnailProperties` 是可靠的**：
  - 缩略图在渲染线程上注册和更新，窗口在 UI 线程上创建，没有问题；
  - `nodcomp`：只更新缩略图，不 Commit、窗口也不重绘（`WS_EX_NOREDIRECTIONBITMAP`，根本没有 GDI 内容），缩略图照样每帧移动，落点估算 305/305 命中，延迟与正常情况相同；
  - `uistall`：UI 线程每 500ms 卡死 200ms，306/306 同帧，延迟不变。
  - 旧原型「缩略图位移要靠宿主窗口重绘才生效」的现象，在非分层窗口上不存在。推测是 WinForms 分层窗口（`Opacity` → `WS_EX_LAYERED`）走 `UpdateLayeredWindow` 一类路径造成的，S4 没有复现分层窗口去证实。

### 2.4 缩略图数量 → 开销（问题 4）

60Hz，经典 `DwmUpdateThumbnailProperties`（run2）：

| N | 每帧 N+1 次调用 P50 / P95 | 每秒阻塞（单次最慢调用） | DWM 合成 start→present P50 / P95 | DWM 呈现 / 刷新 | 估算取样→上屏 P50 / P95 |
|---|---|---|---|---|---|
| 0 | 0.12 / 0.20 | 5.6 | 2.99 / 3.86 | 1.00 | 33.23 / 33.35 |
| 1 | 0.32 / 0.46 | 7.0 | 3.05 / 4.24 | 0.99 | 33.24 / 33.35 |
| 2 | 0.43 / 0.69 | 19.6 | 3.66 / 6.02 | 0.99 | 33.24 / 33.38 |
| 5 | 0.82 / 1.22 | 12.2 | 5.06 / 6.93 | 0.98 | 33.24 / 33.41 |
| 10 | 1.31 / 1.87 | 8.7 | 7.31 / 10.34 | **0.77** | 33.48 / 66.75 |
| 15 | 1.77 / 2.27 | 28.2 | 9.33 / 13.33 | **0.70** | 49.97 / 78.93 |
| 20 | 2.26 / 2.91 | 47.9 | 11.18 / 15.31 | **0.63** | 54.51 / 92.89 |

165Hz（run3 经典 vs 共享视觉，按平铺尺寸创建）：

| N | 经典：调用 P50 / DWM 合成 P50 / 呈现率 | 共享视觉：每帧工作 P50 / DWM 合成 P50 / 呈现率 |
|---|---|---|
| 0 | 0.06 / 1.89 / 1.00 | 0.05 / 1.86 / 0.99 |
| 1 | 0.23 / 2.22 / 0.99 | 0.06 / 2.05 / 0.97 |
| 2 | 0.32 / 3.48 / **0.63** | 0.06 / 1.98 / 0.99 |
| 5 | 0.63 / 4.98 / **0.52** | 0.07 / 2.65 / 0.99 |
| 10 | 1.10 / 8.03 / **0.50** | 0.08 / 3.05 / 0.99 |
| 15 | 1.52 / 10.51 / **0.44** | 0.11 / 3.21 / 0.98 |
| 20 | 1.97 / 12.49 / **0.44** | 0.12 / 3.50 / 0.83 |

「呈现率」= 测量窗口内真正呈现的合成帧数 / 刷新数，来自 Win11 合成帧统计。帧统计偶尔取不到时这个值会偏低，所以屏幕上的情况以 2.1 节的桌面复制平滑度为准；两者的结论一致。

- **CPU 侧**：每个缩略图调用约 0.1ms（P50），呈线性，N=20 每帧 2.3ms，本身不成问题。问题在每秒一次的阻塞，而且阻塞时长随 N 增大（2.5 节）。
- **DWM 侧**：才是瓶颈。60Hz 下经典缩略图每多一个，DWM 合成耗时增加约 0.4ms，N≥10 时开始整帧不呈现（呈现率 0.77），屏幕上表现为卡顿，与我们的线程无关。165Hz 下更糟：**N=2 就掉到 0.63**，而此时 DWM 合成 start→present 只有 3.5ms，远小于 6ms 的周期。所以经典缩略图路径在高刷新率下有一块额外的、不能只用合成耗时解释的开销，根因未查。这里的源窗口都很大（约 2560×1600），运动起点又是大面积互相重叠，接近总览展开时的最坏情况；全屏覆盖层的面积是这里的 4 倍，只会更重（未测）。
- 共享视觉呈现的是同样 N 个窗口的内容，但 60Hz、N=5 时 DWM 合成只要 2.24ms（经典 5.06ms），165Hz 下到 N=15 仍保持 98% 呈现率。按「起点（大）尺寸」创建共享视觉（`--shared-base-from`）的开销与按平铺尺寸几乎相同（N=5 2.50ms，N=10 2.73ms），说明省下来的不是分辨率，而是经典缩略图路径本身的开销。N=20 时共享视觉也开始掉帧（0.83；起点尺寸 0.52）。

### 2.5 每秒一次的 `DwmUpdateThumbnailProperties` 阻塞

- 现象：每 60 帧（每秒）有一帧，**这一帧的第一个缩略图调用**会阻塞，时长随 N 增大：N=0 时约 3–6ms，N=5 时 6–16ms，N=20 时 48–59ms。
- 已排除的原因：不是测量本身（`nostats` 照样有）；不是属性标志（`rectonly` 照样有）；不是某个特定的源窗口（总是阻塞在每帧第一个调用上，不管它对应哪个源）；也不是 DWM 那一帧合成变慢（同一时刻 DWM 的 start→present 正常）。
- 用 `DwmFlush` 作时钟时，缩略图调用里看不到阻塞（单次最慢 0.4–0.75ms），推测是阻塞落在了 `DwmFlush` 的等待里，表现为帧间隔抖动（P95 19ms）。
- 共享视觉每帧没有任何同步 DWM 调用，所以完全没有这个阻塞（渲染线程每帧工作 max 1.4ms）。
- 165Hz 下同样是每秒一次，时长差不多（N=5 时约 12ms），也就是一次就占掉两个周期。
- 影响：60Hz、N≤5 时阻塞在 tick 取样的余量（约 15ms）以内，只是偶尔晚一帧；但如果晚取样，或者到了 165Hz，或者 N 更大，每秒都会丢一帧甚至几帧。根因没有查清（IDA 当时没有连上），P3 应把它当成已知约束。

### 2.6 共享缩略图视觉（问题 5，可选，不作基线）

- **导出**（`S4.RenderLatency exports`，直接读 PE 导出表）：本机 dwmapi.dll 有 72 个只按序号导出的函数，其中包括社区逆向资料里的 #147（`DwmpCreateSharedThumbnailVisual`）和 #162–#164（`DwmpQueryWindowThumbnailSourceSize`、`DwmpCreateSharedMultiWindowVisual`、`DwmpUpdateSharedMultiWindowVisual`）。dcomp.dll 也有 `DwmpEnableDDASupport` 等按名字导出的私有函数，以及只按序号、转发到 win32u 的 `NtDCompositionGetFrameStatistics` 等。
- **调用**：按社区签名 `HRESULT (HWND dst, HWND src, DWORD flags=2, DWM_THUMBNAIL_PROPERTIES*, IDCompositionDesktopDevice*, void** visual, HTHUMBNAIL*)` 调用 #147（`SharedVisual.cs`），返回 S_OK 和一个 `IDCompositionVisual2`。把它挂进我们自己的 DComp 视觉树，用 `SetOffsetX` / `SetTransform` 移动和缩放，随 Commit 生效。
- **桌面复制核实**：同进程测试源 227/227 同帧（60Hz）；跨进程测试源（子进程创建的窗口）216/216 同帧（经典路径同条件 146/146）；165Hz 下 N=20 726/726 同帧，晚取样 lead2 906/906，取样 → 上屏 7.98 / 8.01ms。真实窗口的共享视觉调用全部成功，DWM 合成耗时随 N 增长，说明确实在渲染；但出于隐私，没有对真实窗口内容做像素级核对。
- **限制**：
  - 未公开 API，只按序号导出，签名来自逆向资料，任何系统更新都可能改变；
  - 创建时就定了分辨率（`rcDestination`），放大会糊，所以要按最大显示尺寸创建；
  - 没有测源窗口最小化、cloak（其他虚拟桌面）、DPI 变化、源窗口销毁这些情况下的行为。
- **建议**：P3 设计成可插拔的缩略图后端：启动时用 #147 创建一个视觉试探，成功就用共享视觉，失败或抛异常就退回 `DwmUpdateThumbnailProperties`；两条路径都要保留。

## 3. 每帧时间线（60Hz，实测）

```
VBlank₀ ──0.08ms── DWM 帧 A₀ 起点 = 合成器时钟 tick（渲染线程醒来，0.01ms 后）
   │                 DWM 收集「起点之前」到达的更新（起点后约 0.1–1ms 内到达的有时也能赶上 → 竞争窗口）
   │       +3–5ms    A₀ presentTime（合成耗时随缩略图数增长）；之后 DWM 建一个不呈现的帧 B₀
   │
   │  tick 时取样 → 提交 ─────────────────┐ 赶不上 A₀，进 A₁
   │  晚取样（A₁ 起点前 ≥ 本帧工作 + 余量）─┤ 进 A₁
VBlank₁ ─── DWM 帧 A₁ 起点 ───────────────┘
   │       +3–5ms    A₁ present
VBlank₂ ─── A₁ 开始扫描输出（completedStats.time）＝「上屏」
```

## 4. 给 P3 的推荐管线

1. **窗口**：非分层 `WS_POPUP`，`WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP`。DComp target 用 `topmost: FALSE`：实测经典 DWM 缩略图画在 DComp 内容之上（绿色测试缩略图盖在黑色校验条上）；共享视觉的层级则由 DComp 树决定，可以自由穿插。
2. **线程**：UI 线程只管消息；渲染线程独占 DComp 设备、缩略图句柄和视觉树。输入线程把最新的「进度 + 速度」快照写进无锁槽位，渲染线程在取样点读取。
3. **帧时钟**：`DCompositionWaitForCompositorClock(1, &stopEvent, 100)`。超时只作异常恢复（本次所有场景超时计数都是 0），并单独计数。可变刷新率的屏幕上配合 `DCompositionBoostCompositorClock`（本机没测）。`DwmFlush` 只给没有合成器时钟的系统兜底。
4. **取样点**：醒来后读 `DWM_TIMING_INFO.qpcRefreshPeriod`（或合成帧统计的 framePeriod），自旋或高精度定时器等到「下一帧起点 − lead」再取样，lead = 本帧工作 P99 + 余量。经典缩略图路径还要考虑每秒一次的阻塞：如果 lead 盖不住，接受每秒晚一帧；如果连这个都不能接受，就退回 tick 后约 1–2ms 取样（避开竞争窗口，固定 2 帧延迟）。共享视觉路径可以放心晚取样。
5. **同帧**：同一帧的全部缩略图更新和 DComp 改动都用同一份取样，背靠背发出，并且远离帧起点。用共享视觉时，所有属性一次 Commit，天然原子。
6. **自测遥测**：每帧记录取样时刻和发出时刻，用 `DCompositionGetStatistics` / `GetTargetStatistics` 找落点帧，统计取样 → 上屏的直方图和 DWM 呈现率；这些只在热路径里计数，不写盘。
7. **缩略图预算**：运动期间限制实时缩略图数量，或者对视野外 / 太小的窗口改用静态快照。本机参考值：经典路径 60Hz 下 N≈5–8 以内，165Hz 下只有 1 个；共享视觉 165Hz 下 N≤15。上限要等 P3 用全屏覆盖层实测后再定。如果只能用经典路径，又是高刷新率屏幕，就要接受总览动画期间的实际帧率低于屏幕刷新率。
8. **零分配**：本 spike 的循环零分配（CsWin32 结构体 COM 指针 + 预分配数组），P3 沿用同样的写法。

## 5. COM 互操作的取舍（P3 沿用）

选的是 **CsWin32 0.3.367，`allowMarshaling: false`**（`NativeMethods.json`）：

- COM 接口生成为非托管结构体加函数指针 vtable（`IDCompositionVisual3*` → `visual->SetOffsetX(x)`）。没有 RCW / ComWrappers 对象，没有每次调用的封送，**热路径零分配**，NativeAOT 安全，引用计数显式管理（`Com.Release`）。
- 签名来自 Win32 元数据，**正确处理了 MSVC 对重载虚函数的 vtable 倒序**：`IDCompositionVisual::SetOffsetX(IDCompositionAnimation*)` 在第 3 槽、`SetOffsetX(float)` 在第 4 槽，与头文件里的声明顺序相反。手写 `[ComImport]` 或手写函数指针最容易在这里踩坑，DComp 的 Visual / Transform / Effect 接口满是这种重载。
- 生成的友好方法在失败时调 `ThrowOnFailure()`：成功路径没有开销。预期内会失败的调用（如 `AcquireNextFrame` 的 `DXGI_ERROR_WAIT_TIMEOUT`）在 `preserveSigMethods` 里改为直接返回 HRESULT。
- 窗口过程、`EnumWindows` 回调都是 `[UnmanagedCallersOnly]` 静态方法，取地址传进去，也是 AOT 友好的。
- 代价：
  1. 代码里到处是 `unsafe` 和裸指针，需要约定好谁 AddRef、谁 Release（P3 建议包一层 `ComPtr<T>` 结构体）。
  2. CA1416 平台分析：DComp 方法标注了 windows8.0 / 8.1，Win11 帧统计 API 标注了 10.0.22000。spike 直接把 TFM 设成 `net10.0-windows10.0.22000.0`，但这会拉进 WinRT 投影 `Microsoft.Windows.SDK.NET.dll`（25.7MB）。Platform 项目可以保持 `net10.0-windows`，改在互操作类型上标 `[SupportedOSPlatform("windows10.0.22000")]`；如果 S3 要用 Windows.Graphics.Capture，那时再接受这个 TFM。
  3. 构建期依赖一个源生成器包（`PrivateAssets=all`，运行时没有依赖）。
- 没选的方案：`[GeneratedComInterface]` / ComWrappers 要手写接口声明（重载顺序同样要自己保证），而且每个接口指针都对应一个托管包装对象；手写函数指针没有依赖，但 vtable 槽位全靠手数，维护成本最高。
- 未公开的 dwmapi#147 不在元数据里，用 `GetProcAddress(dwm, (PCSTR)147)` 加 `delegate* unmanaged<...>` 手写（`SharedVisual.cs`），与 CsWin32 生成的类型混用没有问题。

## 6. 没测到 / 局限

- **全屏覆盖层**：GlideShell 的总览是全屏的，面积是这里的 4 倍，DWM 的 GPU 开销只会更高。按约束，全屏测试需要先征得用户同意，本次没有做。P3 应在用户同意后用 `--size 2560x1600` 重测。
- **60Hz 下共享视觉 N=10–20 的曲线**：测量途中刷新率切到了 165Hz，60Hz 下只有共享视觉 N=0 / N=5 的数据。
- **输入段延迟**：真实 PTP ScanTime → 输入线程 → 取样，依赖 S1 / P2。本文的延迟从「取样」算起。
- **物理上屏**：扫描位置和面板响应只给了估算，没有用光电方法测。
- **每秒阻塞的根因**：只做了排除法，没有查到 DWM 内部原因。
- 没覆盖到的条件：CPU 压力、省电模式、混合显卡下切到 dGPU、多显示器 / 跨屏、HDR、可变刷新率、源窗口最小化 / cloak / 销毁、MMCSS 或线程优先级的影响。Win10 上的 `DwmFlush` 路径只在 Win11 上跑过。
- 分层窗口（旧原型）的缩略图生效问题没有复现，只是推测。
- 经典缩略图在 165Hz 下从 N=2 起 DWM 就掉帧，此时 DWM 合成耗时只有约 3.5ms，用合成耗时解释不了，根因未查。

## 7. 复现与数据文件

```bash
cd spikes/S4.RenderLatency
dotnet build -c Release
bin/Release/net10.0-windows10.0.22000.0/S4.RenderLatency.exe run --measure 4 --out results/run-x        # 全部 24 个场景，约 130s
bin/Release/net10.0-windows10.0.22000.0/S4.RenderLatency.exe run --shared-visual --only wait-n5,cap-wait-n5
bin/Release/net10.0-windows10.0.22000.0/S4.RenderLatency.exe run --external-probe --only cap-wait-n5       # 跨进程测试源
bin/Release/net10.0-windows10.0.22000.0/S4.RenderLatency.exe exports                                       # dcomp / dwmapi 导出
```

运行时屏幕中间会出现 1280×800 的半透明窗口，Esc 或点击可以随时退出，到时也会自动关闭。

`results/` 下每组测量包括：
- `summary.csv`：每场景一行，列名见 `Analysis.Columns`；
- 控制台输出 `console.txt`（有的话）；
- 开了桌面复制的场景的 `matched-*.csv`：每次桌面复制帧一行，内容是屏幕上的 DComp / 缩略图 seq、匹配到的合成帧、取样 / 发出时刻。

2.1 节的屏幕平滑度表由 `scripts/screen-advance.awk` 离线从 `matched-*.csv` 算出，例如：

```bash
awk -v period=16.67 -v name=cap-wait-n5 -f scripts/screen-advance.awk results/run2-60hz/matched-cap-wait-n5.csv
```

程序内置的 `screen_advance` 列不按刷新聚合，只作参考。逐帧的 `frames-*` / `compframes-*` / `capture-*` 体积较大，没有提交，用上面的命令可以重新生成。

| 目录 | 刷新率 | 内容 |
|---|---|---|
| `results/run2-60hz/` | 60Hz | 经典缩略图，全部 21 个场景（干净重测，本文 60Hz 数字的来源） |
| `results/run3-165hz/` | 165Hz | 经典缩略图，全部 24 个场景 |
| `results/shared-60hz/` | 60Hz | 共享视觉 N=0 / cap N=5 |
| `results/shared-165hz-tile/`、`shared-165hz-from/`、`shared-165hz-lead/` | 165Hz | 共享视觉曲线（平铺尺寸 / 起点尺寸）与晚取样 |
| `results/external-probe-60hz/` | 60Hz | 跨进程测试源：经典与共享视觉各一次 |
