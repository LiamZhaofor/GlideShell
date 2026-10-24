# M1/M2 — MissionFlow：跟手式调度中心

把 M0 的输入侧（PTP 三指追踪）和渲染侧（DWM 实时缩略图）接成了完整原型：

**三指按下 → 覆盖层出现，窗口缩略图从原位向平铺位飞行，进度由三指上滑位移 1:1 驱动；
松手按进度/离手速度判定「进入交互态」或「回弹」；交互态可点击切换、三指下滑跟手收起。**

## M2 新增（第三轮：问题 4-7）

- **手势启动去阻塞（问题4）**：快照阶段 320ms → **21ms**——枚举+布局先行，缩略图注册延后到
  首帧之后、冷缓存慢快照逐帧排队补拍；覆盖层只在最初几帧半透明（0.85），80ms 后转不透明
  （脱离昂贵的分层窗口合成）。帧率 P50=5.5ms（183fps）、**P95=15.2ms 首次满足 <17ms 验收线**
- **缩放系数封顶 0.8（问题5）**：满屏单窗也有明显的收缩动画
- **下滑收起时指针下的窗口接管焦点（问题6）**：下滑过程中该窗口随进度放大（最大 +30%），
  松手/Esc 后还原（若最小化）并置前——macOS 同款行为，Esc 路径同样生效
- **聚焦窗口带头（问题7）**：手势发起时的前台窗口零延迟、全不透明先动；其余窗口按序错峰
  （0.08 起 + 每窗 +0.015）淡入浮现——「主窗滑出、其余展开」的调度中心编排

## M2 新增（第二轮：问题 1-3）

- **macOS 式等比布局**：所有窗口同一缩放系数（大窗大、小窗小、比例 1:1），按行贪心打包 +
  二分搜索最大系数（`LayoutGroup`）——替换了 M1 的统一网格
- **快照有效性验证**：PrintWindow 产物按 32×18 网格采样亮度，stddev<4 判空白丢弃
  （实证抓到 Windows Terminal 的 DX 内容对 PrintWindow 免疫，产出全黑废片；
  这类窗口降级为「已最小化（无预览）」灰卡，M3 用 Windows.Graphics.Capture 解决）
- **手感参数配置文件**：`%APPDATA%\MissionFlow\config.json`，每次手势开始时热重载（实测改
  `EnterAnimMs=500` 后下一节动画立即变 532ms），含 `PrimaryMonitorOnly`、`PauseOnFullscreen` 开关
- **性能埋点**：`--stats` 启动，每节手势结束输出一条 `Stats:` 日志（帧间隔 P50/P95、ApplyVisual/OnPaint
  分段耗时、快照耗时）
- **帧率优化**（28fps → **86fps P50**）：WM_TIMER 被重绘挤饿 → 线程池定时器 + BeginInvoke 帧源；
  GDI 每帧全尺寸缩放缓照 17.8ms → 布局时预缩放平铺位尺寸 + NearestNeighbor 搬运 5.0ms；
  等比布局天然减小绘制面积；手势路径限 ~85fps 平滑帧间隔。剩余 P95 尖刺（~33ms，疑似 GC/分层合成）
  需 DirectComposition（M3）
- **多显示器**：窗口按原位归入所属显示器，各自独立网格（`PrimaryMonitorOnly` 可只在主屏）
- **全屏暂停**：前台为全屏应用时忽略三指手势（`PauseOnFullscreen` 可关）；托盘新增「暂停三指手势」
  「系统触摸板设置…」菜单
- **系统手势屏蔽重做**（实证）：`ThreeFingerSlideEnabled=0` 是无效值！生效编码是
  `0xFFFF`（自定义按方向模式）+ `ThreeFingerUp/Down=0`；且外部裸写注册表输入栈可能不认
  （设置界面走内部通知）。Guard 改为「检测 + 尝试写生效编码 + 气泡引导手动设置」，
  **永不还原用户配置**（退出不再动注册表）

## 构建

```powershell
dotnet build -c Release
```

exe：`src/MissionFlow/bin/Release/net10.0-windows/M1.MissionFlow.exe`

## 运行方式

| 方式 | 命令/操作 | 用途 |
|---|---|---|
| 常驻 | 直接运行（托盘出现图标） | 三指手势 / Ctrl+Alt+O / 托盘双击 |
| 自测 | `M1.MissionFlow.exe --simdrag` | 无手指验证：脚本驱动同一套状态机（起飞→回撤→推进→停在交互态） |

调试日志：`%TEMP%\MissionFlow.log`（每次相位迁移都记录，排查手势问题必看）。

## 状态机

```
Idle ──三指按下且上滑>0.02──▶ Entering ──松手(p≥0.55 或 速度≥1.2/s)──▶ Interactive
                                 │                                          │
                                 └─松手(未达标)─▶ 回弹取消 ▶ Idle           ├─点击缩略图 ▶ 切换窗口+收起
                                                                            ├─Esc / 失焦 ▶ 收起
                                                                            └─三指下滑(跟手) ▶ 收起/弹回
```

- `Entering`：p = 三指上滑位移 / 0.30（触摸板高度归一化）；窗口位置 = smoothstep(p) 在原位↔平铺位之间插值；
  覆盖层不透明度 0.70→1.00 随 p 加深（低进度时桌面隐约可见）
- 手感参数集中在 `OverlayForm` 顶部常量（EnterThreshold / EnterCommit / CommitVelocity / ExitThreshold / ExitCommit），M2 调参区
- 交互态的下滑收起同样是跟手的：p = 1 - q，窗口沿原路飞回

## 屏蔽系统三指手势

否则三指上滑会同时弹出系统任务视图。**本机实证的生效编码**（用户经设置界面修改后 diff 得出）：

```
HKCU\...\PrecisionTouchPad
  ThreeFingerSlideEnabled = 0xFFFF   # 自定义按方向模式（0 和 2 都不是"关"！）
  ThreeFingerUp   = 0   # 无
  ThreeFingerDown = 0   # 无
  ThreeFingerLeft/Right = 自行保留（如 1 = 切换应用）
```

注意：外部裸写注册表对输入栈**可能不生效**（设置界面走系统内部通知路径）。因此 Guard 策略为：
启动时检测（0xFFFF 且上下=0）→ 未屏蔽则尝试写入生效编码 + 气泡引导手动设置（点击直达设置页）。
**永远不会还原用户的手动配置。**

## 验证记录（2026-10-24，本机自动化）

- ✅ simdrag 全链路：BeginEnter（2 窗口快照）→ 脚本 → CompleteEnter → BecomeInteractive（拿到前台焦点）
- ✅ 交互态布局正确（2560×1600 全覆盖、实时缩略图、标题条）
- ✅ 点击缩略图 → 覆盖层收起 + 目标窗口激活
- ✅ Ctrl+Alt+O 热键唤起（260ms 进入交互态）、Esc 收起、失焦收起
- ✅ 日志完整记录每次相位迁移
- ✅ 系统三指手势屏蔽：实证 `ThreeFingerSlideEnabled 0=无/2=默认`（改注册表后设置页实时显示"无"）；启动自动备份+屏蔽、退出自动还原
- ✅ 最小化窗口收录：dump 实证根因（Win11 最小化窗口 GetWindowRect=237×40 → 误过滤），GetWindowPlacement 修复；5 窗口（3 最小化）截图验证；点击最小化卡片还原+置前成功
- ✅ 性能：WinEventHook 快照缓存（实测首次 334ms → 之后 40ms）；timeBeginPeriod + 局部重绘
- ✅ M2：配置热重载（EnterAnimMs=500 → 动画实测 532ms）；--stats 埋点；帧率 28→53fps（P50 18.7ms，
  ApplyVisual 0.4ms / OnPaint 7.8ms）；Guard 检测模式正确识别用户手动配置
- ✅ M2 三轮优化后：帧率 P50=5.5ms（183fps）**P95=15.2ms 达标**；手势启动 21ms；macOS 式等比布局 +
  聚焦窗口带头编排 + 指针窗口接管焦点（Esc 路径已自动化验证）
- ⬜ 多显示器分屏、全屏暂停、托盘暂停菜单：代码已就位，待相应环境验证
- ⬜ M2 验收缺口：~升级为已达标~；剩余 P95 尖刺单帧 max~67ms（偶发，DComp 可再磨）
- ⬜ 真手指：三指按下滑动跟手 / 松手判定 / 交互态三指下滑收起 + 指针窗口放大聚焦 / 满屏单窗动画 / 快滑无卡顿（等你体验）

## 窗口收录规则（含最小化）

- 可见窗口：DWM 实时缩略图
- **最小化窗口**：纳入显示（任务视图行为）。用 `GetWindowPlacement` 拿还原尺寸
  （Win11 最小化窗口 `GetWindowRect` 只有 237×40，这是个坑），快照走
  「悄悄还原(SW_SHOWNOACTIVATE)→PrintWindow(PW_RENDERFULLCONTENT)→再最小化」，
  过程被已显示的覆盖层挡住；每窗口约 160ms，显示为静态快照卡（轻微暗化 + 标注"最小化"），点击还原
- cloaked 只拦"其他虚拟桌面"（bit 2）和"继承"（bit 4）；挂起的 UWP（bit 1）保留
- 托盘隐藏/工具窗/有 owner/无标题/过小：排除
- 诊断：`M1.MissionFlow.exe --dump` 把每个窗口的过滤判定写入日志后退出
- M2 优化：用 WinEventHook 在后台缓存最小化快照，消除手势启动时的 ~160ms/窗口延迟

## 性能（已做）

- **快照缓存（消除启动卡顿）**：`SetWinEventHook` 监听 `EVENT_SYSTEM_MINIMIZESTART`，
  窗口最小化瞬间其 DWM 表面仍是全尺寸，直接 `PrintWindow` 缓存（无闪动）；
  手势开始时查缓存，命中零延迟。慢路径（悄悄还原→拍→再最小化，~160ms/窗口）只兜底冷启动。
  意外收获：慢路径结尾的"再最小化"会触发钩子，自动把该窗口也补进缓存——所以第一次手势后全部命中。
  实测（本机 5 窗口 3 最小化）：首次 ~334ms → 之后 **40ms**。
- **动画帧率**：`timeBeginPeriod(1)` 提升 WM_TIMER 分辨率（原本 ~15.6ms ≈ 64fps 上限）；
  局部重绘（只失效 GDI 绘制内容新旧位置并集，DWM 缩略图无需重绘）；Opacity 变化加 0.01 门槛；
  进入 200ms / 收起 230ms / 回弹 180ms
- M2 若仍不够：迁 DirectComposition 渲染管线（GDI 局部重绘是权宜）

## 已知问题与设计取舍（M2 处理）

- **激活弹跳**：非前台进程的 `Activate()` 可能被 Windows 拒绝 → 已用 ALT 轻点技巧 + 300ms 失焦宽限绕过；
  彻底方案是 UIAccess 签名
- 松手瞬间基线重置导致 p 跳变的极端情况未处理（快速连按）
- 飞行中点击被忽略（按 macOS 行为）
- 逻辑双击 Dismiss 的无害重复日志

## 文件结构

- `PtpRawInput.cs` — PTP 报文解析（自 M0，无改动）
- `GestureEngine.cs` — 接触流 → 手势语义（基线/位移/释放速度）
- `OverlayForm.cs` — 状态机 + 快照 + DWM 缩略图飞行插值 + 交互
- `Win32.cs` — interop（枚举/DWM/热键/虚拟桌面查询）
- `Log.cs` — `%TEMP%\MissionFlow.log`
