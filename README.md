# MissionFlow — Windows 跟手式调度中心

> 三指上滑，窗口跟着手指飞 —— macOS Mission Control 式的任务视图替代品（Windows 10/11，精确式触摸板）

Windows 原生的三指上滑只是**播放一段固定动画**，不可交互、不跟手。MissionFlow 用触摸板的原始 HID 数据流驱动自建的窗口调度界面：手指移动多少，窗口就飞多少；滑到一半停住，画面就停住；松手按位移和速度决定进入或回弹。

## 功能

- **1:1 跟手**：三指上滑实时驱动窗口从原位飞向平铺位（Raw Input 直读精确式触摸板 HID 报文，~125Hz）
- **macOS 式编排**：聚焦窗口零延迟带头起飞，其余窗口错峰淡入；等比缩放布局（大窗大、小窗小，比例 1:1）
- **松手语义**：快甩轻弹直接进入、滑过阈值进入、不足回弹；滑到顶保持 120ms 自动进入（无需松手）
- **交互态**：点击切换窗口；三指下滑跟手收起，**指针下的窗口放大并接管焦点**（Esc 同效）
- **最小化窗口**：WinEventHook 在最小化瞬间抓取 PrintWindow 快照缓存（零闪动、零延迟），点击还原
- **多显示器**：各屏独立网格，窗口从所属屏幕起飞；`PrimaryMonitorOnly` 可选
- **任务栏保留**：覆盖层只铺工作区，任务栏全程可见（macOS 顶栏体验）
- **手感可调**：`%APPDATA%\MissionFlow\config.json` 热重载（阈值/速度判定/动画时长/错峰/开关）
- **全屏暂停**：前台是全屏应用（游戏演示等）时自动忽略手势；托盘可暂停/恢复
- **工具链**：`--simdrag` 无手指自测、`--stats` 帧率/延迟埋点、`--dump` 窗口收录诊断

## 一次性设置（屏蔽系统三指手势）

系统三指上下滑会与本项目冲突。到 **设置 → 蓝牙和其他设备 → 触摸板 → 高级手势**，把三指**向上/向下轻扫**设为「无」。

> 生效的注册表编码（供程序化参考）：`HKCU\...\PrecisionTouchPad` 下
> `ThreeFingerSlideEnabled=0xFFFF`（自定义按方向模式）+ `ThreeFingerUp/Down=0`。
> 注意：外部写注册表对输入栈可能不生效，设置界面走系统内部通知路径。
> 左右轻扫是否禁用自选——不禁用时上滑若带横向漂移会触发系统的「切换应用」浮层。

## 构建与运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)（Windows Desktop 运行时）。

```powershell
dotnet build M1\MissionFlow.sln -c Release

# 常驻（托盘出现图标，三指手势 / Ctrl+Alt+O / 双击托盘）
M1\src\MissionFlow\bin\Release\net10.0-windows\M1.MissionFlow.exe

# 自测模式（脚本驱动同一套状态机，无需手指）
M1.MissionFlow.exe --simdrag

# 性能埋点（每节手势输出帧率/耗时统计到 %TEMP%\MissionFlow.log）
M1.MissionFlow.exe --stats

# 窗口收录诊断（枚举窗口并记录过滤判定后退出）
M1.MissionFlow.exe --dump
```

调试日志：`%TEMP%\MissionFlow.log`（含每次手势的轨迹、相位迁移、缓存命中、帧率统计）。

## 原理

```
触摸板(HID/PTP) ─RawInput(WM_INPUT+RIDEV_INPUTSINK)→ HidP_* 解析触点坐标流(~125Hz)
                                            ↓
                                     GestureEngine（基线/位移/速度，触点抖动宽限）
                                            ↓ p∈[0,1]
                          OverlayForm 状态机（Idle→Entering→Interactive→…）
                            ├─ 实时窗口：DWM Thumbnail（DwmRegisterThumbnail，DWM 合成）
                            ├─ 最小化窗口：PrintWindow 静态快照（WinEventHook 即时缓存）
                            └─ 弹簧平滑(τ=70ms)：慢滑 1:1，快甩有最短可见飞行
```

关键技术点（都在代码注释与各目录 README 里有实证记录）：

- Win11 最小化窗口 `GetWindowRect` 只有 237×40，真实还原尺寸要用 `GetWindowPlacement`
- WM_TIMER 会被大面积重绘挤饿到 ~10Hz，动画帧源用线程池定时器 + `BeginInvoke`
- GDI 每帧全尺寸缩放位图是性能大头：预缩放到平铺位尺寸 + 局部重绘（帧率 28→100fps）
- PrintWindow 对部分硬件加速窗口（如 Windows Terminal）产出全黑废片：亮度方差校验 + 免疫名单
- 触摸板固件异常行为（按住静止停发报文/不发 cc=0/触点数抖动）的完整对策见 `GestureEngine`

## 目录结构

```
M0/  可行性验证 demo（2026-10）
     ├─ TouchpadMonitor  输入侧：PTP 触点坐标流监控 + 三指上滑实验
     └─ DwmOverview      渲染侧：DWM 实时缩略图平铺
M1/  MissionFlow 本体（持续迭代）
     └─ src/MissionFlow/
         ├─ PtpRawInput.cs        PTP 报文解析（HidP 标准 HID 路线）
         ├─ GestureEngine.cs      手势语义（基线/位移/速度/有效性）
         ├─ OverlayForm.cs        状态机 + 布局 + 动画 + 交互
         ├─ SystemGestureGuard.cs 系统手势检测（注册表实证编码）
         ├─ Config.cs             热重载配置
         └─ Win32.cs / Log.cs     interop / 诊断日志
```

## 路线图

- [x] M0 输入/渲染双侧可行性验证
- [x] M1 跟手原型（状态机、松手判定、交互态）
- [x] M2 打磨（等比布局、聚焦带头编排、指针窗口聚焦、多显示器、性能、配置化）
- [ ] M3 DirectComposition 渲染管线（帧率 P95 尖刺）、Windows.Graphics.Capture（DX 窗口快照）、虚拟桌面条（未文档化 COM）、应用内三指左右滑切桌面
- [ ] M4 发布工程（UIAccess 签名、安装包、开机自启）

## 已知限制

- Windows Terminal 等硬件加速渲染窗口的最小化快照为灰卡（PrintWindow 免疫，待 WGC）
- 仅支持精确式触摸板（PTP）；系统三指手势需手动设为「无」
- 释放动画偶发 P95 帧间隔尖刺（GDI 管线天花板，M3 迁 DirectComposition）

## 致谢

- [ClementGre/ThreeFingerDragOnWindows](https://github.com/ClementGre/ThreeFingerDragOnWindows)（MIT）— `PtpRawInput.cs` 的解析思路改编自其 TouchpadHelper
- [emoacht/RawInput.Touchpad](https://github.com/emoacht/RawInput.Touchpad)（MIT）— 上游的 Raw Input 触摸板读取方案
- 调研阶段参考了 GestureSign、TouchpadGestures_Advanced、windows-expose-clone 等项目，详见 M0 调研记录

## License

[MIT](LICENSE) © LiamZhaofor。所含第三方 MIT 代码保留其原始版权声明。
