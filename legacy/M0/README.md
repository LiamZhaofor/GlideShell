> **已归档**：GlideShell 重构前的旧原型，只读参考，见 [legacy/README.md](../README.md)。

# M0 — 可行性验证 Demo

「Windows 跟手式调度中心」项目的 M0 里程碑：两个独立 demo，分别验证
**输入侧**（三指连续追踪）与**渲染侧**（DWM 实时窗口缩略图）的技术可行性。

## 构建

需要 .NET 10 SDK（已安装 `10.0.402`）。

```powershell
dotnet build -c Release
```

## Demo1：M0.TouchpadMonitor — PTP 触摸板原始数据监控

- 通过 Raw Input API（`WM_INPUT` + `RIDEV_INPUTSINK`）订阅精确式触摸板（PTP）的原始 HID 报文
- 用 `hid.dll` 的 `HidP_*` 按 preparsed data 逐字段解析（Scan Time / Contact Count / 每触点 Id/X/Y），
  不假设固定位布局，兼容各 OEM 报告描述符差异
- 界面显示：触点数、报告率（Hz）、报告间隔（ms）、逐条报文日志、触摸板画布上的实时触点
- **三指上滑实验**：三指按下时记录基线，之后 ΔY（上滑为正）实时驱动进度条 —— 这就是
  「任务视图动画跟手」的输入侧原型

运行：`src/TouchpadMonitor/bin/Release/net10.0-windows/M0.TouchpadMonitor.exe`

### 验证清单（需要真机手指）

- [x] 检测到 PTP 设备（本机：VID_2808/PID_0218）
- [x] RegisterRawInputDevices 注册成功，窗口后台时仍接收输入
- [ ] 报告率稳定在 90~125Hz（触摸时观察「报告率 ~N Hz」）
- [ ] 三指同时按下能拿到 3 个触点的完整坐标（日志 cc=3 且有 3 组坐标）
- [ ] 三指上滑时 ΔY 平滑递增，进度条跟手，无跳变/丢帧
- [ ] 突然抬手 count 归零（松手可检测，M1 状态机的「取消」分支依赖此事件）
- [ ] 三指上滑若同时触发了系统任务视图 → 去系统设置把三指上滑设为「无」再试

### 已知设计点

- 只处理 `dwCount` 份报告的最后一份；PTP 通常每次 WM_INPUT 一份
- 归一化坐标用 X/Y 的 HID Logical Min/Max 计算，跨设备可比
- 解析失败计数显示在右上角（橙色），用于发现 quirk 固件

## Demo2：M0.DwmOverview — DWM 实时窗口缩略图平铺

- 全屏无边框覆盖层（覆盖整个虚拟屏），把当前虚拟桌面的顶层窗口平铺为**实时**缩略图
  （`DwmRegisterThumbnail`，DWM 直接合成、非截图，窗口里播视频也能看到）
- 只显示客户区（`fSourceClientAreaOnly`），观感贴近 macOS 调度中心
- 用官方 `IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop` 过滤其他桌面的窗口
- 交互：点击缩略图切换并关闭；Esc / 失焦关闭；悬停高亮；淡入动画

运行：`src/DwmOverview/bin/Release/net10.0-windows/M0.DwmOverview.exe`

### 验证清单

- [x] 编译通过，0 警告 0 错误
- [x] 全屏覆盖 + 深色背景 + 网格布局 + 标题条
- [x] 缩略图为窗口实时内容（DWM 合成）
- [x] 点击缩略图 → 前台切换目标窗口 → 覆盖层关闭（已用桌面自动化验证）
- [x] 只显示当前虚拟桌面窗口；最小化/工具窗/cloaked 窗口被正确过滤
- [ ] 多显示器布局（本机虚拟屏 2560×1600 已覆盖，待确认多屏行为）

### 已知限制（M1 处理）

- 最小化窗口不显示（DWM 拿不到其实时缩略图）：M1 用 `DwmSetIconicLivePreviewBitmap` 静态图回退
- 无虚拟桌面切换条：M3 用未文档化 COM（参考 VirtualDesktopAccessor）
- DWM 缩略图数量有系统上限，M0 限 24 个；失败时显示 ⚠ 标题

## 结论 → M1

输入侧 + 渲染侧均可行后，M1 将把两者接起来：三指按下 → 覆盖层窗口从桌面淡入、
各窗口从原位向平铺位插值，**插值进度直接由三指累计位移驱动**；松手按位移量与
离手速度判定进入或回弹。Demo1 的状态机 + Demo2 的覆盖层就是 M1 的骨架。

## 许可致谢

- `references/TouchpadHelper.cs`：来自 [ClementGre/ThreeFingerDragOnWindows](https://github.com/ClementGre/ThreeFingerDragOnWindows)（MIT），
  改编自 [emoacht/RawInput.Touchpad](https://github.com/emoacht/RawInput.Touchpad)（MIT）。
  本仓库的 `PtpRawInput.cs` 基于其解析思路重写，保留同样的 MIT 义务。
