# GlideShell

> 让 Windows 触摸板手势真正跟手：手指走多少，画面走多少；停住就停住，反向就倒退，松手才决定去留。

Windows 原生的多指手势大多是「识别到手势 → 播放一段固定动画」，不可交互、不跟手。GlideShell 直接读取精确式触摸板（PTP）的原始触点流，用自建的合成界面重做多任务交互：

- **三指上滑**：窗口从原位逐渐飞向总览布局，进度由手指位移连续驱动
- **三指横滑**：跟手切换窗口
- **四指横滑**：连续切换虚拟桌面
- **物理反馈**：惯性、阻尼、弹簧回弹、可打断动画，松手按距离和速度决定完成或取消
- **可定制**：手势映射、灵敏度、排除程序；三指轻点中键、三指拖移互不冲突
- **轻量稳定**：不装内核驱动，开机启动、异常恢复、一键退回 Windows 原生手势

首发平台：Windows 11 + 精确式触摸板。

## 当前状态

**分层重建中，主要功能已有首个可运行版本**（分支 `glideshell/p0`）：三指上滑总览、三指横滑切换窗口、四指横滑切换虚拟桌面、三指拖移、托盘快捷设置、看门狗与一键回退、单文件发布。阈值目前是先验值，要等真机轨迹录制（S1）后再校准。

旧原型 MissionFlow 已归档在 [`legacy/`](legacy/README.md)（基线 tag `missionflow-final`）。规划、接手评估、架构、各阶段实测结果与待定决策见 **[docs/ROADMAP.md](docs/ROADMAP.md)**。

## 安装

```powershell
pwsh build\install.ps1      # 没有发布产物时会先发布；装到 %LOCALAPPDATA%\Programs\GlideShell，加开始菜单与开机启动
pwsh build\uninstall.ps1    # 退出、确认系统手势已还原、删除程序（加 -Purge 连配置与日志一起删）
```

两个脚本都不需要管理员权限（Windows PowerShell 5.1 也可以运行），重装即升级：运行中的 GlideShell 会先正常退出（`GlideShell.exe --quit`）。

## 使用

运行 `GlideShell.exe` 后常驻托盘。GlideShell 只接管它已实现的那几个系统手势，三指轻点（中键）等其余手势始终归系统；退出、暂停或进程崩溃时都会把系统手势还原成你原来的设置。

| 手势 | 默认 | 开启「三指拖移」后 |
|---|---|---|
| 三指上 / 下滑 | 总览（跟手展开 / 收起） | 拖移 |
| 三指左 / 右滑 | 切换窗口 | 拖移 |
| 四指上 / 下滑 | 归系统 | 总览 |
| 四指左 / 右滑 | 切换虚拟桌面 | 切换虚拟桌面 |
| 三指轻点 | 归系统（中键） | 归系统（中键） |

- **托盘菜单**：打开总览（也可按 Ctrl+Alt+O）；暂停（即退回 Windows 原生手势）；三指拖移；四指切桌面；灵敏度高 / 标准 / 低；在当前程序中不使用手势；开机启动；打开配置文件 / 日志目录。
- **总览里**：点击窗口切过去，Esc 收起，Enter 切到鼠标所指的窗口。
- **配置文件** `%APPDATA%\GlideShell\config.json`（可带注释，保存即生效，写错了会回退默认值并记日志）：手势映射、滑动阈值、各功能行程与弹簧参数、背景变暗、拖移速度、排除程序等。
- **日志** `%LOCALAPPDATA%\GlideShell\logs\glideshell.log`。
- **紧急还原**：万一系统手势没有恢复，运行 `GlideShell.exe --restore-native`；`GlideShell.exe --quit` 让运行中的实例正常退出。

## 目录结构

```
GlideShell.slnx
src/
  GlideShell.Core/       纯逻辑：手势识别、仲裁、物理、布局、交互模型（禁止 P/Invoke）
  GlideShell.Platform/   Windows 实现：Raw Input、DWM、DirectComposition、虚拟桌面、注册表
  GlideShell/            常驻宿主：托盘、线程编排、配置、控制器装配
tests/GlideShell.Tests/  单元测试、轨迹回放测试、架构守卫测试
tools/PtpProbe/          只读探针：打印报告描述符、录制与分析输入轨迹
legacy/                  MissionFlow 旧原型（只读参考）
docs/                    路线图与设计记录
```

## 构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```powershell
dotnet build GlideShell.slnx
dotnet test GlideShell.slnx

# 发布自包含单文件（裁剪 + ReadyToRun），产物在 artifacts\publish\win-x64\GlideShell.exe
pwsh build\publish.ps1
# 装有 MSVC（Visual Studio「使用 C++ 的桌面开发」）时可发布 NativeAOT
pwsh build\publish.ps1 -Aot

# 不碰触摸板，用合成手势演示：overview / switch / desktop-right 等，脚本列表见 SyntheticTouch
dotnet run --project src\GlideShell -- --simulate overview
```

诊断工具 `tools/PtpProbe`（只读）：

```powershell
dotnet run --project tools\PtpProbe                 # 报告描述符（触点槽数、TipSwitch/Confidence、物理尺寸）
dotnet run --project tools\PtpProbe -- live         # 实时打印手势识别结果
dotnet run --project tools\PtpProbe -- record       # 按场景录制输入轨迹（S1）
dotnet run --project tools\PtpProbe -- analyze artifacts\traces\<时间>
dotnet run --project tools\PtpProbe -- windows      # 窗口收录诊断
dotnet run --project tools\PtpProbe -- native       # 系统手势：注册表值与 Explorer 生效值对照
dotnet run --project tools\PtpProbe -- desktops     # 虚拟桌面接口自检
```

## 致谢

- [ClementGre/ThreeFingerDragOnWindows](https://github.com/ClementGre/ThreeFingerDragOnWindows)（MIT）— PTP 解析思路来自其 TouchpadHelper
- [emoacht/RawInput.Touchpad](https://github.com/emoacht/RawInput.Touchpad)（MIT）— 上游的 Raw Input 触摸板读取方案
- 调研阶段参考了 GestureSign、TouchpadGestures_Advanced、windows-expose-clone、GNOME Shell workspace.js 等项目

## License

[MIT](LICENSE) © LiamZhaofor。所含第三方 MIT 代码保留其原始版权声明。
