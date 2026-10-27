# GlideShell

> 让 Windows 触摸板手势真正跟手：手指走多少，画面走多少；停住就停住，反向就倒退，松手才决定去留。

Windows 原生的多指手势大多是「识别到手势 → 播放一段固定动画」。GlideShell 直接读取精确式触摸板（PTP）的原始触点，用自己的合成界面重做多任务交互，体验对标 macOS 调度中心：

- **三指上滑 · 窗口总览**：每个窗口从原位飞向总览布局，进度完全由手指位移驱动。停住，画面就停在半路；往回滑，画面就倒退。
- **总览桌面条**：总览顶部列出所有虚拟桌面；指针移上去就展开成实时缩略图，点另一个桌面会立刻切过去，并从缩略图放大进入那个桌面。末尾的「+」新建桌面，名字旁的「×」删除桌面。
- **三指横滑 · 切换窗口**：窗口条随手指平移，松手吸附到最近的窗口并切到前台。
- **四指横滑 · 切换虚拟桌面**：相邻桌面跟着手指滑入，没有起滑死区。到首尾桌面时有橡皮筋，切换结束时没有闪烁，窗口阴影也与系统一致。
- **macOS 式物理**：松手时按距离和速度决定完成还是取消，松手速度直接传给弹簧动画。动画随时可以被新手势接住。
- **与系统一致的细节**：总览与切桌面的缩略图都带有按真机校准的窗口阴影（125% / 150% / 175% 实测一致），撤掉覆盖层的那一刻画面与真实桌面无缝衔接。
- **多显示器**：每块屏各自排布；横跨两块屏的窗口在两块屏上都会画出来，展开或收起时不会缺一块。
- **可定制**：手势映射、灵敏度、排除程序、三指拖移。三指轻点（中键）始终交给系统。
- **轻量、可退回**：
  - 不装驱动，4.6MB 的原生程序，常驻私有内存约 13MB。
  - 退出、暂停或崩溃时，自动把系统手势还原成你原来的设置。

## 系统要求

- Windows 11（在 24H2 / 26300 上开发与验证）
- 精确式触摸板（设置 → 蓝牙和其他设备 → 触摸板里显示「你的电脑具有精确式触摸板」）

## 安装

从 [Releases](https://github.com/LiamZhaofor/GlideShell/releases) 下载 `GlideShell-1.2.0-win-x64.zip`，解压后双击 **安装.cmd**。

- 程序装到 `%LOCALAPPDATA%\Programs\GlideShell`，并添加开始菜单与开机启动，不需要管理员权限。
- 卸载：双击 **卸载.cmd**。它会先退出程序、确认系统手势已还原，再删除程序。配置与日志会保留；要连同它们一起删除，运行 `uninstall.ps1 -Purge`。
- 程序没有正式签名，第一次运行时 Windows 可能提示「无法验证发布者」。

重装即升级：运行中的 GlideShell 会先正常退出，再被替换。

## 使用

运行后常驻在任务栏通知区域。GlideShell 只接管它实现了的那几个系统手势，其余手势照旧由 Windows 处理。

| 手势 | 默认 | 开启「三指拖移」后 |
|---|---|---|
| 三指上 / 下滑 | 总览（跟手展开 / 收起） | 拖移 |
| 三指左 / 右滑 | 切换窗口 | 拖移 |
| 四指上 / 下滑 | 系统默认 | 总览 |
| 四指左 / 右滑 | 切换虚拟桌面 | 切换虚拟桌面 |
| 三指轻点 | 系统默认（中键） | 系统默认（中键） |

**总览里**
- 点击窗口，或三指下滑 / Enter，切到选中的窗口；Esc 收起。
- 有两个以上虚拟桌面时，顶部是桌面条：指针移上去展开成缩略图，点另一个桌面就切过去；点当前桌面等于收起。
- 总览里四指左右滑：相邻桌面的缩略图随手指放大，松手过半就切过去，不够就缩回。
- 桌面条末尾的「+」新建一个桌面（加在最后，不切过去）；鼠标停在某个非当前桌面上时，名字右边出现「×」，点它删除这个桌面，上面的窗口并入左边的桌面。
- 三指左右滑、方向键或 Tab（Shift+Tab 反向）移动选中项。
- Ctrl+Alt+O 也能打开总览。

**托盘菜单**
- 打开总览；暂停（立即退回 Windows 原生手势）；设置…
- 三指拖移、四指切桌面开关；灵敏度高 / 标准 / 低。
- 不使用手势的程序；全屏时暂停手势；开机启动；打开日志目录。

**设置窗口**
- 每个手势做什么。
- 灵敏度与各功能的行程、总览背景变暗、拖移速度。
- 排除程序、开机启动。

**进阶**
- **配置文件**：`%APPDATA%\GlideShell\config.json`（可带注释，保存即生效）。弹簧参数等不常用的选项也在这里，逐项说明见 [docs/CONFIG.md](docs/CONFIG.md)。
- **日志**：`%LOCALAPPDATA%\GlideShell\logs\glideshell.log`。
- **紧急还原**：万一系统手势没有恢复，运行 `GlideShell.exe --restore-native`。`GlideShell.exe --quit` 可以让运行中的实例正常退出。

## 性能（1.2.0，本机实测）

测试机：Radeon 780M、2560×1600、150% 缩放、165Hz。

| 项目 | 数值 |
|---|---|
| 程序大小 | 4.6MB（NativeAOT） |
| 常驻内存 | 工作集约 50MB、私有约 13MB |
| 总览 / 切桌面帧间隔 | P50 6.1ms / P95 6.2ms（跟满 165Hz） |
| 输入到上屏 | 约 1 帧（按合成器时钟晚取样） |
| 再次打开总览的预热 | 约 3–6ms |

## 工作原理

```
输入线程   Raw Input 读 PTP 原始触点（零分配解析，坐标换算成毫米，时间用硬件 ScanTime）
    ↓
纯逻辑     手势识别（只有抬指才决定）→ 手势映射与排除 → 总览 / 切换窗口 / 切桌面控制器 → 物理（速度、落点、弹簧）
    ↓
渲染线程   按显示器刷新取样，驱动 DirectComposition + DWM 实时缩略图
```

- 覆盖层只显示画面，不抢焦点。真正切换前台窗口或虚拟桌面，都在覆盖层下面完成，结束时撤掉覆盖层，画面无缝衔接。
- 接管系统手势时写注册表，并通知 Explorer 刷新设置缓存。接管前先落盘一份记录，看门狗进程在主进程意外退出时按这份记录还原。
- 用到两个未公开接口：虚拟桌面 COM 和共享缩略图（dwmapi#147）。启动时都会自检，不通过就自动退回：把四指手势交还系统，或改用普通缩略图。

设计、各阶段的实测结论和后续路线见 **[docs/ROADMAP.md](docs/ROADMAP.md)**。

## 已知限制

- 只在一台机器上完整验证过（ASUS 精确式触摸板）。
- 多显示器只用合成手势验证过（175% + 125% 双屏：跨屏窗口、混合缩放下的阴影），还没有用真手势在双屏上验证体验。
- 窗口阴影在 125% / 150% / 175% 下实测与系统一致，100% 还没实测。
- 发布包是普通权限版。覆盖层盖不住开始菜单等系统界面；需要的话可以从源码构建 UIAccess 版（见下文）。
- 当前桌面不能在桌面条上删除（删除会让系统立刻切走），请先切到别的桌面再删。
- 还不支持 Windows 10。

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```powershell
dotnet build GlideShell.slnx
dotnet test GlideShell.slnx

pwsh build\publish.ps1                 # 自包含单文件 → artifacts\publish\win-x64\GlideShell.exe
pwsh build\publish.ps1 -Aot            # NativeAOT（需要 Visual Studio「使用 C++ 的桌面开发」）
pwsh build\package.ps1 -Aot            # 发布包 → artifacts\GlideShell-<版本>-win-x64.zip
pwsh build\install.ps1                 # 安装到本机（没有发布产物时会先发布）
```

**UIAccess 版**：覆盖层能盖住开始菜单等系统界面，切换前台也更可靠。它使用本机自签名证书，只对这台电脑有效。

```powershell
pwsh build\publish.ps1 -Aot -UIAccess  # 首次会弹 UAC，信任本机证书
pwsh build\install.ps1 -UIAccess       # 装到 %ProgramFiles%\GlideShell；只有复制这一步需要管理员权限
```

不碰触摸板，用合成手势演示：

```powershell
dotnet run --project src\GlideShell -- --simulate overview   # 还有 switch / desktop-right / desktop-left 等
```

### 诊断工具 `tools/PtpProbe`

```powershell
dotnet run --project tools\PtpProbe                         # 触摸板报告描述符（触点槽数、物理尺寸等）
dotnet run --project tools\PtpProbe -- live                 # 实时打印手势识别结果
dotnet run --project tools\PtpProbe -- record               # 按场景录制输入轨迹
dotnet run --project tools\PtpProbe -- calibrate <轨迹目录>  # 由轨迹给出识别参数建议并回放验证
dotnet run --project tools\PtpProbe -- windows              # 总览会收录哪些窗口
dotnet run --project tools\PtpProbe -- native               # 系统手势：注册表值与 Explorer 生效值对照
dotnet run --project tools\PtpProbe -- desktops             # 虚拟桌面接口自检
dotnet run --project tools\PtpProbe -- thumbnails           # 缩略图后端自检
dotnet run --project tools\PtpProbe -- thumbnails --shadow  # 自画阴影与系统真实阴影逐点对照
```

### 目录结构

```
src/
  GlideShell.Core/       纯逻辑：手势识别、映射、物理、布局、交互控制器（不调用 Windows API，可回放测试）
  GlideShell.Platform/   Windows 实现：Raw Input、DWM、DirectComposition、虚拟桌面、系统手势接管
  GlideShell/            常驻程序：托盘、设置窗口、线程编排、配置
tests/GlideShell.Tests/  单元测试、真机轨迹回放测试、架构守卫测试
tools/PtpProbe/          诊断探针
build/                   发布、打包、安装、证书脚本
docs/                    路线图、配置说明、技术验证记录
legacy/                  旧原型 MissionFlow（只读参考，tag missionflow-final）
```

## 致谢

- [ClementGre/ThreeFingerDragOnWindows](https://github.com/ClementGre/ThreeFingerDragOnWindows)（MIT）：PTP 解析思路来自它的 TouchpadHelper
- [emoacht/RawInput.Touchpad](https://github.com/emoacht/RawInput.Touchpad)（MIT）：上游的 Raw Input 触摸板读取方案
- 调研阶段参考了 GestureSign、TouchpadGestures_Advanced、windows-expose-clone、GNOME Shell workspace.js 等项目

## License

[MIT](LICENSE) © LiamZhaofor。所含第三方 MIT 代码保留其原始版权声明。
