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

**分层重建中。** 旧原型 MissionFlow 已验证输入侧（Raw Input 读 PTP 触点流）和渲染侧（DWM 实时缩略图）可行，但结构无法承载上面的目标，现归档在 [`legacy/`](legacy/README.md)（基线 tag `missionflow-final`）。

规划、接手评估、架构与阶段完成标准见 **[docs/ROADMAP.md](docs/ROADMAP.md)**。

## 目录结构

```
GlideShell.slnx
src/
  GlideShell.Core/       纯逻辑：手势识别、仲裁、物理、布局、交互模型（禁止 P/Invoke）
  GlideShell.Platform/   Windows 实现：Raw Input、DWM、DirectComposition、虚拟桌面、注册表
  GlideShell/            常驻宿主：托盘、线程编排、配置、控制器装配
tests/GlideShell.Tests/  单元测试、轨迹回放测试、架构守卫测试
tools/PtpProbe/          只读探针：打印触摸板 HID 报告描述符
legacy/                  MissionFlow 旧原型（只读参考）
docs/                    路线图与设计记录
```

## 构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```powershell
dotnet build GlideShell.slnx
dotnet test GlideShell.slnx

# 查看本机触摸板的报告描述符（触点槽数、TipSwitch/Confidence、物理尺寸）
dotnet run --project tools\PtpProbe
```

## 致谢

- [ClementGre/ThreeFingerDragOnWindows](https://github.com/ClementGre/ThreeFingerDragOnWindows)（MIT）— PTP 解析思路来自其 TouchpadHelper
- [emoacht/RawInput.Touchpad](https://github.com/emoacht/RawInput.Touchpad)（MIT）— 上游的 Raw Input 触摸板读取方案
- 调研阶段参考了 GestureSign、TouchpadGestures_Advanced、windows-expose-clone、GNOME Shell workspace.js 等项目

## License

[MIT](LICENSE) © LiamZhaofor。所含第三方 MIT 代码保留其原始版权声明。
