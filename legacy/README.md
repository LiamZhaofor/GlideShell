# legacy — MissionFlow 旧原型（只读归档）

GlideShell 分层重建之前的原型，基线 tag 为 `missionflow-final`。这里的代码**不再加功能**，只作为迁移时的参考；保留了哪些、丢弃了哪些，见 [docs/ROADMAP.md](../docs/ROADMAP.md) 第 2.3 节。

| 目录 | 内容 |
|---|---|
| [M0/](M0/README.md) | 可行性验证：TouchpadMonitor（PTP 触点流监控）、DwmOverview（DWM 实时缩略图平铺） |
| [M1/](M1/README.md) | MissionFlow 本体：三指上滑跟手式调度中心（M1/M2 迭代） |

单独构建（不在 GlideShell.slnx 里）：

```powershell
dotnet build legacy\M0\M0.slnx -c Release
dotnet build legacy\M1\M1.slnx -c Release
```

旧原型运行时文件：配置 `%APPDATA%\MissionFlow\config.json`，日志 `%TEMP%\MissionFlow.log`。
