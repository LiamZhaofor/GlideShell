namespace GlideShell.Core.Input;

/// <summary>
/// 报文里一个触点槽的原值：逻辑坐标、TipSwitch（按下）、Confidence（设备认为是手指而非手掌）。
/// 这是 Platform 解析 HID 报文后交给 Core 的边界类型，也是轨迹文件里记录的内容。
/// </summary>
public readonly record struct RawContact(int Id, bool Tip, bool Confidence, int X, int Y);
