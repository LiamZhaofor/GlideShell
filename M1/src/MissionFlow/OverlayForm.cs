using System.Diagnostics;

namespace M1.MissionFlow;

// M1：跟手式调度中心原型。
// 三指按下 → 覆盖层出现，窗口缩略图从原位向平铺位飞行，进度 p 由三指累计上滑位移
// 1:1 驱动；松手按进度/离手速度判定「进入交互态」或「回弹取消」。
// 交互态里点击切换窗口、Esc/失焦/三指下滑关闭（下滑过程同样跟手）。
internal sealed class OverlayForm : Form
{
    private enum Phase { Idle, Entering, Animating, Interactive }

    private sealed class Entry
    {
        public required IntPtr Hwnd;
        public required string Title;
        public RectangleF SrcRect;     // 屏幕坐标 → 覆盖层客户区坐标
        public RectangleF TileRect;    // 平铺位（缩略图区域，不含标题条）
        public RectangleF LabelRect;   // 平铺位标题条
        public RectangleF Current;     // 当前帧位置（绘制占位与悬停框用）
        public IntPtr Thumb;
        public Label? Label;
        public bool Failed;
        public bool IsMinimized;       // 最小化：DWM 无实时缩略图，用 PrintWindow 静态快照
        public bool IsFocused;         // 手势发起时的前台窗口：动画带头、全程全显
        public bool Pending;           // 缩略图尚未注册（首帧先画背景，注册延后避免启动突刺）
        public double FadeIn = 1.0;    // 非聚焦窗口随进度的淡入因子
        public bool WasDrawn;          // GDI 卡片上一帧是否绘制（淡出转跳过时需失效旧区域，防残影）
        public Bitmap? Snapshot;       // 慢路径自有位图（FromCache=false 时才由 Entry 释放）
        public bool FromCache;         // 位图借自 _snapCache，释放权在缓存
        public Bitmap? TileBitmap;     // 预缩放到平铺位的一次性拷贝（每帧 GDI 缩放太贵）
        public RectangleF DrawRect;    // 快照等比缩放后的绘制区（Current 内 letterbox）
        public RectangleF LastPaint;   // 上一帧的失效区（局部重绘用）

        public Bitmap? EffectiveBitmap(Dictionary<IntPtr, Bitmap> cache)
            => FromCache ? (cache.TryGetValue(Hwnd, out var b) ? b : null) : Snapshot;
    }

    // 托盘「暂停三指手势」开关（热键不受影响）
    public static volatile bool GesturesPaused;

    // 手感参数：每次手势开始时检查 config.json 变化并热重载
    private MissionConfig _cfg = new();

    // 性能埋点（--stats）：一节手势内每次视觉帧的时间戳 + 快照阶段耗时
    private readonly List<double> _frameTs = new();
    private double _snapshotPhaseMs;
    private double _sessionStartMs;
    private readonly List<double> _applyMs = new();  // ApplyVisual 本体耗时（DWM 调用等）
    private readonly List<double> _paintMs = new(); // OnPaint 耗时（GDI 绘制）
    private List<Entry> _captureQueue = new();      // 手势启动后逐帧处理的慢快照队列
    private int _deferStage = 2;                    // 延后工作阶段
    private double _enterStartMs;

    private Phase _phase = Phase.Idle;
    private double _p;               // 进入进度 0..1（也是飞行插值进度；交互态下滑时 = 1-q）
    private bool _exitDrag;          // 交互态正在三指下滑
    private double _interactiveAtMs = -1; // BecomeInteractive 时刻（失焦宽限判定）
    private readonly GestureEngine _gesture = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private List<Entry> _entries = new(); // 当前会话窗口（整体重赋值以支持复用池切换）
    private readonly System.Threading.Timer _threadTimer;
    private int _tickPending;
    private int _hover = -1;
    private double _lastApplyMs;
    private bool _suppressDeactivate;

    // 脚本动画（Complete/Cancel/Dismiss）与 --simdrag
    private bool _animating;
    private double _animFrom, _animTo, _animStartMs, _animDur;
    private Action? _animDone;
    private bool _sim;
    private double _simStartMs;
    private readonly bool _dump;

    // 最小化快照缓存：窗口最小化的瞬间窗口表面还是全尺寸，直接 PrintWindow 即可（无闪动）。
    // 手势开始时查缓存，未命中才走「悄悄还原→拍→再最小化」的慢路径。
    private readonly Dictionary<IntPtr, Bitmap> _snapCache = new();
    private readonly List<Bitmap> _pendingDispose = new(); // 手势进行中失效的缓存位图，收尾统一释放
    private Win32.WinEventDelegate? _winEventProc; // 持引用防 GC
    private IntPtr _winEventHook;

    // 重绘优化
    private double _lastOpacity = -1;
    private string _lastHint = "";

    private static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "tooltips_class32", "TaskListThumbnailWnd",
    };

    public OverlayForm(bool dump = false)
    {
        _dump = dump;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(14, 14, 18);
        Opacity = 0; // 隐藏时也不占视觉

        // WM_TIMER 会被大面积重绘挤饿（实测只有 ~10Hz），动画帧用线程池定时器 + BeginInvoke 驱动
        _threadTimer = new(_ =>
        {
            if (Interlocked.Exchange(ref _tickPending, 1) == 1) return; // 上一帧未处理完，跳过
            try { BeginInvoke(TimerTickBody); }
            catch { Interlocked.Exchange(ref _tickPending, 0); }
        });

        _winEventProc = WinEventProc;
        _winEventHook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _winEventProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
    }

    // OUTOFCONTEXT 钩子在 UI 线程经消息循环回调
    private void WinEventProc(IntPtr hHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || idObject != Win32.OBJID_WINDOW) return;
        try
        {
            if (eventType == Win32.EVENT_SYSTEM_MINIMIZESTART)
            {
                if (!Win32.IsWindowVisible(hwnd) || Win32.GetWindowTextLength(hwnd) == 0) return;
                if (_printImmune.Contains(hwnd)) return; // 已知抓不到的窗口不再浪费钩子路径的尝试
                if (_snapCache.ContainsKey(hwnd)) return; // 已有缓存（如慢路径刚拍好）不必重拍，省一次必失败的空白尝试
                var wp = new Win32.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
                if (!Win32.GetWindowPlacement(hwnd, ref wp) || wp.rcNormalPosition.Width < 160) return;
                var bmp = PrintWindowSnapshot(hwnd, wp.rcNormalPosition.Width, wp.rcNormalPosition.Height);
                if (bmp is not null)
                {
                    _snapCache.TryGetValue(hwnd, out var old);
                    _snapCache[hwnd] = bmp;
                    old?.Dispose();
                    Log.W($"SnapCache: 捕获 {(Win32.GetWindowText(hwnd) is { Length: > 24 } t ? t[..24] : Win32.GetWindowText(hwnd))} {bmp.Width}x{bmp.Height}");
                }
            }
            else if (eventType == Win32.EVENT_SYSTEM_MINIMIZEEND)
            {
                if (_snapCache.Remove(hwnd, out var bmp))
                {
                    if (_phase == Phase.Idle) bmp.Dispose();
                    else _pendingDispose.Add(bmp); // 手势还在引用它，收尾再释放
                    Log.W("SnapCache: 窗口已还原，清除缓存");
                }
            }
        }
        catch { }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        bool raw = PtpRawInput.RegisterInput(Handle);
        bool hot = Win32.RegisterHotKey(Handle, 1, Win32.MOD_CONTROL | Win32.MOD_ALT, 0x4F); // Ctrl+Alt+O
        Log.W($"OnHandleCreated rawInput={raw} hotkey={hot}");
        _threadTimer.Change(5, 5); // 句柄就绪后启动动画帧源（5ms 间隔，实际受每帧工作量限制）
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == PtpRawInput.WM_INPUT && m.LParam != IntPtr.Zero)
        {
            double nowMs = _sw.Elapsed.TotalMilliseconds;
            if (_lastAnyInputMs > 0) _reportGaps.Add(nowMs - _lastAnyInputMs);
            _lastAnyInputMs = nowMs;
            if (PtpRawInput.TryParse(m.LParam) is { } report)
            {
                // 诊断：cc 与实际解析触点数不一致（分包/固件怪癖）——定位 ΔY 冻结类问题
                if (report.ContactCount != report.Contacts.Count && _sw.Elapsed.TotalMilliseconds - _lastParseLogMs > 300)
                {
                    _lastParseLogMs = _sw.Elapsed.TotalMilliseconds;
                    Log.W($"Parse: cc={report.ContactCount} 解析触点={report.Contacts.Count}");
                }
                _gesture.Feed(report, _sw.Elapsed.TotalMilliseconds);
                if (!_sim) HandleGesture();
            }
        }
        else if (m.Msg == Win32.WM_HOTKEY && m.WParam.ToInt32() == 1)
        {
            OpenByHotkey();
        }
        base.WndProc(ref m);
    }

    private double _lastAnyInputMs; // 最近一份任意报文的时间（看门狗区分「按住不动」和「完全松开」）
    private double _lastParseLogMs;

    // ---- 对外入口 ----

    /// <summary>热键/托盘打开：无手势直接放完整动画进入交互态。</summary>
    public void OpenByHotkey()
    {
        if (_phase != Phase.Idle) return;
        BeginEnter();
        AnimateProgress(1.0, _cfg.EnterAnimMs, () => BecomeInteractive());
    }

    /// <summary>--simdrag 自测：脚本驱动 p 曲线（同一套 ApplyVisual/状态机），结束后停在交互态。</summary>
    public void RunSimDrag()
    {
        if (_phase != Phase.Idle) return;
        BeginEnter();
        _sim = true;
        _simStartMs = _sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>--dump 诊断：枚举窗口并记录每个的过滤判定，然后退出。</summary>
    public void DumpWindows()
    {
        SnapshotWindows();
        Application.ExitThread();
    }

    // ---- 手势状态机 ----

    private void HandleGesture()
    {
        switch (_phase)
        {
            case Phase.Idle:
                if (GesturesPaused) break;
                if (_cfg.PauseOnFullscreen && ForegroundIsFullscreen())
                {
                    Log.W("Gesture: 前台为全屏应用，忽略手势（可在 config.json 关闭 PauseOnFullscreen）");
                    break;
                }
                if (_gesture.IsThreeFinger && _gesture.DeltaYUp > 0.02)
                    BeginEnter();
                break;

            case Phase.Entering:
                // 防御：万一注册帧被饿死，100ms 后从输入路径强制补上
                if (_deferStage == 0 && _sw.Elapsed.TotalMilliseconds - _enterStartMs > 100)
                {
                    Log.W("Defer: 输入路径强制注册缩略图");
                    RegisterThumbnails();
                    _deferStage = 1;
                }
                if (!_gesture.IsThreeFingerEffective(_sw.Elapsed.TotalMilliseconds))
                {
                    _fullSinceMs = null;
                    EvaluateEnterRelease();
                }
                else
                {
                    SetProgress(Math.Clamp(_gesture.DeltaYUp / _cfg.EnterThreshold, 0, 1));

                    // 快甩即时提交：手指还在飞就进入（macOS 行为），消除甩后按住时的死寂等待
                    if (_gesture.ReleaseVelocityUp >= _cfg.DragCommitVelocity && _p >= _cfg.DragCommitMinP)
                    {
                        Log.W($"Gesture: 快甩即时进入 v={_gesture.ReleaseVelocityUp:F1} p={_p:F2}");
                        _fullSinceMs = null;
                        CompleteEnter();
                        break;
                    }

                    // 顶格保持 120ms 自动进入——滑到底不需要松手
                    if (_p >= 0.98)
                    {
                        _fullSinceMs ??= _sw.Elapsed.TotalMilliseconds;
                        if (_sw.Elapsed.TotalMilliseconds - _fullSinceMs > 120)
                        {
                            Log.W("Gesture: 顶格自动进入");
                            _fullSinceMs = null;
                            CompleteEnter();
                            break;
                        }
                    }
                    else _fullSinceMs = null;
                    LogDragFlow();
                }
                break;

            case Phase.Animating:
                // 仅「收起/回弹类」动画（目标朝 0）期间的上滑意图才回开；
                // 进入动画期间手指仍向上是快甩提交的正常延续，重开会变成两次动画（本机实证的回归）
                if (_animTo < _animFrom &&
                    _gesture.IsThreeFingerEffective(_sw.Elapsed.TotalMilliseconds) && _gesture.DeltaYUp > 0.06)
                {
                    Log.W($"Gesture: 收起动画期间上滑意图 → 反向回开（visP {_visP:F2}→1.0）");
                    _animTo = 1.0;
                    _animFrom = _visP;
                    _animStartMs = _sw.Elapsed.TotalMilliseconds;
                    _animDur = _cfg.EnterAnimMs;
                    _animDone = () => BecomeInteractive();
                    _ffLogged = true;
                    break;
                }
                // 其余情况（仍按住/下滑中）：快进当前动画，下一份报文回到正常相位
                if (_gesture.IsThreeFingerEffective(_sw.Elapsed.TotalMilliseconds) && _sw.Elapsed.TotalMilliseconds - _animStartMs > 150)
                {
                    if (!_ffLogged) { Log.W($"Gesture: 动画期间手势，快进（p {_animFrom:F2}→{_animTo:F2}）"); _ffLogged = true; }
                    _animFrom = _animTo;
                    _animStartMs = _sw.Elapsed.TotalMilliseconds - _animDur - 1; // 下一 tick 即完成
                }
                break;

            case Phase.Interactive:
                if (!_gesture.IsThreeFingerEffective(_sw.Elapsed.TotalMilliseconds))
                {
                    if (_exitDrag) EvaluateExitRelease();
                    _hadThreeFingers = false;
                    break;
                }

                // 交互态新一轮三指按下（上升沿）：基线重置到当前位置。
                // 否则若进入交互态时已松手（快甩后提前抬指），基线停留在原上滑起点（板底），
                // 随后的下滑全程 ΔY 为正、收起永远触发不了——"第一次下滑没反应"的根因。
                if (!_hadThreeFingers)
                {
                    _gesture.Rebase();
                    _hadThreeFingers = true;
                    _exitDrag = false;
                    Log.W("Gesture: 交互态新一轮三指按下，基线已重置");
                }
                else
                {
                    if (!_exitDrag)
                    {
                        // 三指重新按下：等待下滑才开始关闭（先小幅上滑忽略）
                        if (_gesture.DeltaYUp < -0.015) _exitDrag = true;
                    }
                    if (_exitDrag)
                    {
                        // 指针下的窗口就是要放大的那个（问题6）
                        int hoverNow = HitTest(PointToClient(Cursor.Position));
                        if (hoverNow != _hover) { _hover = hoverNow; Invalidate(); }
                        double q = Math.Clamp(-_gesture.DeltaYUp / _cfg.ExitThreshold, 0, 1);

                        // 快甩即时收起（对称于快甩即时进入）
                        if (-_gesture.ReleaseVelocityUp >= _cfg.DragCommitVelocity && q >= 0.30)
                        {
                            Log.W($"Gesture: 快甩即时收起 v={_gesture.ReleaseVelocityUp:F1} q={q:F2}");
                            DismissFocusHovered();
                            break;
                        }
                        if (q >= 1) { DismissFocusHovered(); break; }
                        SetProgress(1 - q); // 反向飞行：跟手收起
                        LogDragFlow();
                    }
                }
                break;
        }
    }

    private double _lastDragLogMs;
    private double? _fullSinceMs; // 顶格保持计时（自动进入）
    private bool _ffLogged;       // 快进日志只记一次/动画
    private bool _hadThreeFingers; // 交互态三指按下状态（上升沿触发基线重置）

    // 手势数据流节流日志：每 ~150ms 一条，用于定位「滑动中无反应」这类间歇性问题
    private void LogDragFlow()
    {
        double now = _sw.Elapsed.TotalMilliseconds;
        if (now - _lastDragLogMs < 150) return;
        _lastDragLogMs = now;
        Log.W($"Drag: cc={_gesture.ContactCount} ΔY={_gesture.DeltaYUp:+0.000;-0.000} v={_gesture.ReleaseVelocityUp:+0.0;-0.0} p={_p:F2} visP={_visP:F2} phase={_phase} stage={_deferStage} hover={_hover} pend={_entries.Count(e => e.Pending)}/{_entries.Count} op={Opacity:F2} paints={_paintCount} repP50={ReportIntervalP50():F0}ms");
    }

    private int _paintCount;      // 本会话 OnPaint 实际执行次数（区分"位置在更新"和"画面在绘制"）
    private readonly List<double> _reportGaps = new(); // WM_INPUT 间隔采样（确认输入洪峰强度）

    private double ReportIntervalP50()
    {
        if (_reportGaps.Count < 4) return 0;
        var sorted = _reportGaps.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    // 前台是全屏应用（无边框铺满整块显示器，排除桌面/任务栏）→ 游戏演示等场景暂停手势
    private static bool ForegroundIsFullscreen()
    {
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        string cls = Win32.GetClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!Win32.TryGetVisibleRect(fg, out var r)) return false;
        foreach (var s in Screen.AllScreens)
        {
            if (r.Left <= s.Bounds.Left + 2 && r.Top <= s.Bounds.Top + 2 &&
                r.Right >= s.Bounds.Right - 2 && r.Bottom >= s.Bounds.Bottom - 2)
                return true;
        }
        return false;
    }

    private void EvaluateEnterRelease()
    {
        if (_p >= _cfg.EnterCommit || _gesture.ReleaseVelocityUp >= _cfg.CommitVelocity)
            CompleteEnter();
        else
            CancelEnter();
    }

    private void EvaluateExitRelease()
    {
        double q = 1 - _p;
        _exitDrag = false;
        if (q >= _cfg.ExitCommit) DismissFocusHovered();
        else AnimateProgress(1.0, 160, () => BecomeInteractive());
    }

    /// <summary>收起并让指针下的窗口接管焦点（macOS：下滑退出时指针下的窗口成为聚焦窗口）。</summary>
    private void DismissFocusHovered()
    {
        // 防御：hover 状态可能滞后（报文流间歇），收起瞬间用指针实位置现算一次
        if ((_hover < 0 || _hover >= _entries.Count) && _entries.Count > 0)
        {
            try { _hover = HitTest(PointToClient(Cursor.Position)); } catch { }
        }
        if (_hover >= 0 && _hover < _entries.Count && !_entries[_hover].Pending)
        {
            var target = _entries[_hover];
            if (target.IsMinimized) Win32.ShowWindow(target.Hwnd, Win32.SW_RESTORE);
            Win32.SetForegroundWindow(target.Hwnd);
            Log.W($"DismissFocus: {target.Title.TruncateLog(24)}");
        }
        else Log.W($"DismissFocus: 指针下无窗口（hover={_hover}）");
        Dismiss();
    }

    // ---- 相位推进 ----

    private void BeginEnter()
    {
        _cfg = MissionConfig.LoadOrReload(_cfg); // 手感参数热重载
        Log.W("BeginEnter");
        _phase = Phase.Entering;
        _exitDrag = false;
        _hover = -1;
        _frameTs.Clear();
        _applyMs.Clear();
        _paintMs.Clear();
        _paintCount = 0;
        _reportGaps.Clear();
        _sessionStartMs = _sw.Elapsed.TotalMilliseconds;
        _visP = 0;
        _lastVisMs = _sessionStartMs;
        // 只铺工作区：任务栏保持可见（macOS 调度中心的菜单栏/Dock 同样不消失）
        Bounds = OverlayArea();
        _areaScr = Bounds;
        Opacity = 0.55; // 逐帧淡入到 1.0，避免瞬间压黑的闪屏感
        _enterStartMs = _sw.Elapsed.TotalMilliseconds;
        Show(); // 先显示覆盖层：最小化窗口快照的「悄悄还原→拍→再最小化」被它挡住
        double snap0 = _sw.Elapsed.TotalMilliseconds;
        SnapshotWindows();
        _snapshotPhaseMs = _sw.Elapsed.TotalMilliseconds - snap0;
        ApplyVisual(0, force: true);
        Log.W($"BeginEnter done entries={_entries.Count} area={_areaScr.Width}x{_areaScr.Height} snapshot={_snapshotPhaseMs:F0}ms 触发延迟={_sw.Elapsed.TotalMilliseconds - _lastAnyInputMs:F0}ms");
    }

    // 所有显示器工作区的并集（排除任务栏）
    private static Rectangle OverlayArea()
    {
        var r = Screen.AllScreens[0].WorkingArea;
        for (int i = 1; i < Screen.AllScreens.Length; i++)
            r = Rectangle.Union(r, Screen.AllScreens[i].WorkingArea);
        return r;
    }

    private Rectangle _areaScr = Rectangle.Empty; // 覆盖层覆盖的屏幕区域（工作区并集）

    private void CompleteEnter()
    {
        AnimateProgress(1.0, _cfg.EnterAnimMs, () => BecomeInteractive());
    }

    private void BecomeInteractive()
    {
        _phase = Phase.Interactive;
        _interactiveAtMs = _sw.Elapsed.TotalMilliseconds;
        foreach (var e in _entries)
            if (e.Label is not null) e.Label.Visible = true;
        ApplyVisual(1, force: true);
        if (_gesture.ContactCount == 3)
        {
            _gesture.Rebase(); // 仍按住时重置基线：随后下滑即收起
            _hadThreeFingers = true;
        }
        else _hadThreeFingers = false;
        ActivateHarder(); // 此时才接管键盘（Esc）；非前台进程直接 Activate 常被系统拒绝
        Log.W($"BecomeInteractive activated={Focused}");
    }

    // Windows 前台锁定规则的经典绕法：轻点一下 ALT 再激活
    private void ActivateHarder()
    {
        Win32.keybd_event(0x12, 0, 0, IntPtr.Zero);
        try { Activate(); } finally { Win32.keybd_event(0x12, 0, 2, IntPtr.Zero); }
    }

    private void CancelEnter()
    {
        Log.W("CancelEnter");
        AnimateProgress(0, _cfg.CancelAnimMs, HideAndReset);
    }

    private void Dismiss()
    {
        Log.W($"Dismiss p={_p:F2}");
        _suppressDeactivate = true;
        AnimateProgress(0, _cfg.DismissAnimMs, HideAndReset);
    }

    private void HideAndReset()
    {
        Log.W("HideAndReset");
        _suppressDeactivate = false;
        _phase = Phase.Idle;
        _p = 0;
        _exitDrag = false;
        _hover = -1;
        _gesture.Reset();
        Hide();
        Opacity = 0;
        foreach (var e in _entries)
        {
            if (e.Label is not null) e.Label.Visible = false;
            // 复用的缩略图先隐藏：否则下次手势 Show() 的头几帧会闪现上一会话的平铺布局
            if (e.Thumb != IntPtr.Zero)
            {
                var hide = new Win32.DWM_THUMBNAIL_PROPERTIES { dwFlags = Win32.DWM_TNP_VISIBLE, fVisible = false };
                Win32.DwmUpdateThumbnailProperties(e.Thumb, ref hide);
            }
            if (!e.FromCache) e.Snapshot?.Dispose(); // 缓存位图不归 Entry 管
            e.Snapshot = null;
            e.FromCache = false;
            e.TileBitmap?.Dispose(); // 布局可能变，下次按新平铺位重建
            e.TileBitmap = null;
            e.LastPaint = RectangleF.Empty;
        }
        // 资源保留复用：DWM 缩略图/标签留在 _retired，快速连滑不再反复注册/销毁
        _retired = _entries;
        _entries = new List<Entry>();
        _captureQueue.Clear();
        _deferStage = 2;
        foreach (var b in _pendingDispose) b.Dispose();
        _pendingDispose.Clear();
        Invalidate();
        LogStats();
    }

    // --stats：本节手势的帧间隔分布与快照耗时（排除 >100ms 的暂停间隙，单独计数）
    private void LogStats()
    {
        if (!RunOptions.Stats || _frameTs.Count < 3) return;
        var all = new List<double>(_frameTs.Count - 1);
        for (int i = 1; i < _frameTs.Count; i++) all.Add(_frameTs[i] - _frameTs[i - 1]);
        var iv = all.Where(x => x <= 100).OrderBy(x => x).ToList();
        int pauses = all.Count - iv.Count;
        if (iv.Count < 2) return;
        double p50 = iv[iv.Count / 2];
        double p95 = iv[(int)(iv.Count * 0.95)];
        double total = _frameTs[^1] - _sessionStartMs;
        double applyP50 = P50(_applyMs);
        double paintP50 = P50(_paintMs);
        Log.W($"Stats: 视觉帧 {iv.Count + 1}（暂停间隙 {pauses}） | 帧间隔 P50={p50:F1}ms P95={p95:F1}ms max={iv[^1]:F1}ms | ≈{1000 / Math.Max(p50, 0.1):F0}fps(P50) | ApplyVisual P50={applyP50:F1}ms OnPaint P50={paintP50:F1}ms | 快照={_snapshotPhaseMs:F0}ms | 会话={total:F0}ms");
    }

    private static double P50(List<double> list)
    {
        if (list.Count == 0) return 0;
        var sorted = list.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    private void SetProgress(double p)
    {
        _p = p;
        _needsFrame = true; // 渲染统一由定时器帧驱动：实证 WM_INPUT 上下文发出的 DWM 缩略图
                            // 位移不生效（窗口停在原位），而定时器上下文（提交动画）的生效
    }

    private bool _needsFrame; // 输入置脏，定时器帧取走

    private void AnimateProgress(double to, double durationMs, Action? done)
    {
        _phase = Phase.Animating;
        _animating = true;
        _ffLogged = false;
        // 从「视觉进度」起飞而非逻辑进度：快甩时 p 已瞬移到 1.0，若从 p 起播则动画零长度
        // （窗口瞬移到平铺位=没动画）。从弹簧所在的视觉位置起播，飞行始终可见。
        _animFrom = _visP;
        _animTo = to;
        _animStartMs = _sw.Elapsed.TotalMilliseconds;
        _animDur = durationMs;
        _animDone = done;
    }

    // ---- 每帧视觉：p 驱动一切 ----

    private void ApplyVisual(double p, bool force = false)
    {
        double t0 = _sw.Elapsed.TotalMilliseconds;
        double now = t0;
        if (!force && now - _lastApplyMs < 12) { _p = p; return; } // 手势路径限 ~85fps：均衡输入洪峰，帧间隔更稳
        _lastApplyMs = now;
        _p = p;
        if (RunOptions.Stats) _frameTs.Add(now);

        // 自适应弹簧：时间常数随误差变大——慢滑时 τ≈25ms 紧贴手指（消除主窗口延迟感），
        // 快甩时 τ→90ms 把瞬移拉成可见飞行。固定 70ms 曾让所有窗口整体拖后 ~80-100ms，
        // 在"原地收缩"的主窗口上最显眼。
        double dt = Math.Clamp((now - _lastVisMs) / 1000.0, 0.001, 0.05);
        _lastVisMs = now;
        if (_animating) _visP = p; // 仅脚本动画旁路弹簧
        else
        {
            double tau = Math.Clamp(0.025 + Math.Abs(p - _visP) * 0.25, 0.025, 0.09);
            _visP += (p - _visP) * (1 - Math.Exp(-dt / tau));
        }

        try
        {
            ApplyVisualBody(_visP, force);
        }
        finally
        {
            if (RunOptions.Stats && force) _applyMs.Add(_sw.Elapsed.TotalMilliseconds - t0);
        }
    }

    private double _visP;      // 弹簧平滑后的视觉进度
    private double _lastVisMs;

    private void ApplyVisualBody(double p, bool force)
    {
        int i = 0;
        foreach (var e in _entries)
        {
            // GNOME/macOS 式：所有窗口同时动，不排队、不逐个淡入（错峰+淡入曾引发
            // 变灰/残影/慢起步一连串问题，现代 GNOME 也已移除旧版错峰）
            double k = Math.Clamp(p, 0, 1);
            double ease = _animating ? k * k * (3 - 2 * k) : k; // 跟手线性，脚本动画两端缓
            e.FadeIn = 1;
            e.Current = Lerp(e.SrcRect, e.TileRect, ease);

            if (e.Pending)
            {
                i++;
                continue; // 缩略图还没注册：只算位置不画（首帧后自动补上）
            }

            if (e.Thumb != IntPtr.Zero)
            {
                var props = new Win32.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = Win32.DWM_TNP_RECTDESTINATION | Win32.DWM_TNP_OPACITY
                              | Win32.DWM_TNP_VISIBLE | Win32.DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = ToRECT(e.Current),
                    opacity = (byte)Math.Round(255 * e.FadeIn),
                    fVisible = true,
                    fSourceClientAreaOnly = true,
                };
                Win32.DwmUpdateThumbnailProperties(e.Thumb, ref props);
            }
            else if (e.IsMinimized && e.EffectiveBitmap(_snapCache) is { } bmp)
            {
                // 静态快照按原窗口比例在 Current 内 letterbox（和 DWM 缩略图观感一致）
                double scale = Math.Min(e.Current.Width / bmp.Width, e.Current.Height / bmp.Height);
                double dw = bmp.Width * scale, dh = bmp.Height * scale;
                e.DrawRect = new RectangleF(
                    (float)(e.Current.Left + (e.Current.Width - dw) / 2),
                    (float)(e.Current.Top + (e.Current.Height - dh) / 2),
                    (float)dw, (float)dh);

                // 首帧把快照预缩放到平铺位尺寸：飞行中 DrawRect 始终在平铺位附近，
                // 每帧搬运近似 1:1，避免 GDI 每帧全尺寸缩放（实测 17.8ms → 目标 <5ms）
                if (e.TileBitmap is null && e.TileRect.Width > 1)
                    e.TileBitmap = new Bitmap(bmp, (int)e.TileRect.Width, (int)e.TileRect.Height);
            }

            // GDI 卡片（最小化/失败占位）状态跟踪
            if (e.IsMinimized || e.Failed)
            {
                e.WasDrawn = e.FadeIn > 0.05;
                e.LastPaint = e.Current;
            }
            i++;
        }

        // 实证（18:04 vs 18:08 对照）：DWM 缩略图的位移刷新依赖宿主窗口重绘——
        // 有 GDI 卡片时每帧 Invalidate+Update 的会话"画面流畅"，
        // 纯 DWM 缩略图（无最小化窗口）不 Invalidate 的会话缩略图纹丝不动。
        // 因此活动相位一律整帧重绘（全量绘制实测 5-7ms，80fps 预算内）。
        if (_phase != Phase.Idle) Invalidate();

        // 下滑收起时：指针下的窗口随下滑进度放大（问题6），松手后它接管焦点
        if (_exitDrag && _hover >= 0 && _hover < _entries.Count)
        {
            var he = _entries[_hover];
            if (!he.Pending)
            {
                float grow = (float)(1 + 0.30 * (1 - p));
                var before = he.Current;
                he.Current = new RectangleF(
                    before.X - before.Width * (grow - 1) / 2,
                    before.Y - before.Height * (grow - 1) / 2,
                    before.Width * grow, before.Height * grow);
                Invalidate(Rectangle.Ceiling(Union(before, he.Current)));
                he.LastPaint = he.Current;
            }
        }

        // 提示文字只在变化时重绘
        string hint = HintText;
        if (hint != _lastHint || force)
        {
            Invalidate(new Rectangle(0, ClientRectangle.Height - 64, ClientRectangle.Width, 64));
            _lastHint = hint;
        }

        // 关键修复（WM_PAINT 饥饿）：绘制消息只在消息队列清空时生成，而滑动中触摸板以
        // ~100Hz 持续投递 WM_INPUT，队列永不清空 → 位置每帧都在正确更新（日志可证）但
        // 画面一帧不画，直到松手队列清空才集中渲染——即"滑动没动画、释放才出现"。
        // Update() = UpdateWindow：同步绘制当前失效区域，无视队列状态。
        if (!_dump) Update();
    }

    private string HintText => _phase switch
    {
        Phase.Entering => "三指上滑展开 · 松手进入",
        Phase.Interactive => $"共 {_entries.Count} 个窗口 · 点击切换 · 三指下滑 / Esc 关闭",
        _ => "",
    };

    private static RectangleF Union(RectangleF a, RectangleF b)
    {
        float l = Math.Min(a.Left, b.Left), t = Math.Min(a.Top, b.Top);
        float r = Math.Max(a.Right, b.Right), bo = Math.Max(a.Bottom, b.Bottom);
        return new RectangleF(l, t, r - l, bo - t);
    }

    private static RectangleF Lerp(RectangleF a, RectangleF b, double t)
        => new((float)(a.Left + (b.Left - a.Left) * t),
               (float)(a.Top + (b.Top - a.Top) * t),
               (float)(a.Width + (b.Width - a.Width) * t),
               (float)(a.Height + (b.Height - a.Height) * t));

    private static Win32.RECT ToRECT(RectangleF r)
        => new() { Left = (int)Math.Round(r.Left), Top = (int)Math.Round(r.Top),
                   Right = (int)Math.Round(r.Right), Bottom = (int)Math.Round(r.Bottom) };

    // ---- 窗口快照与平铺布局 ----

    private void SnapshotWindows()
    {
        var vdm = Win32.TryCreateVirtualDesktopManager();
        var area = _areaScr;
        IntPtr foreground = Win32.GetForegroundWindow(); // 聚焦窗口：动画带头

        var fresh = new List<Entry>();
        var reused = new HashSet<Entry>();

        Win32.EnumWindows((hwnd, _) =>
        {
            if (fresh.Count >= 24) return false;
            string title = Win32.GetWindowText(hwnd);
            string cls = Win32.GetClassName(hwnd);
            string brief = (title.Length > 30 ? title[..30] + "…" : title) + $" [{cls}]";

            void Skip(string why)
            {
                if (_dump) Log.W($"[win] ✗ {brief} — {why}");
            }

            if (hwnd == Handle) return true;
            if (!Win32.IsWindowVisible(hwnd)) { Skip("不可见(托盘/隐藏)"); return true; }
            if ((Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0) { Skip("工具窗口"); return true; }
            if (Win32.GetWindowLong(hwnd, Win32.GWLP_HWNDPARENT) != 0) { Skip("有owner的悬浮窗"); return true; }

            // cloaked: 1=应用自遮(挂起的UWP，任务视图会显示) 2=在其他虚拟桌面 4=继承
            Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int));
            if ((cloaked & 2) != 0) { Skip("其他虚拟桌面"); return true; }
            if ((cloaked & 4) != 0) { Skip("继承遮挡"); return true; }
            if (SkipClasses.Contains(cls)) { Skip("系统类"); return true; }

            Win32.GetWindowRect(hwnd, out var rect);
            bool minimized = Win32.IsIconic(hwnd);
            if (minimized)
            {
                // Win11 最小化窗口 GetWindowRect 只有 237x40；真实还原尺寸在 WINDOWPLACEMENT
                var wp = new Win32.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
                if (Win32.GetWindowPlacement(hwnd, ref wp)) rect = wp.rcNormalPosition;
            }
            if (rect.Width < 160 || rect.Height < 120)
            {
                Skip($"尺寸过小 {rect.Width}x{rect.Height}");
                return true;
            }
            if (title.Length == 0) { Skip("无标题"); return true; }

            if (vdm is not null)
            {
                try { if (!vdm.IsWindowOnCurrentVirtualDesktop(hwnd)) { Skip("其他虚拟桌面(VDM)"); return true; } }
                catch { }
            }

            if (_dump) Log.W($"[win] ✓ {brief} — minimized={minimized} cloaked={cloaked}");

            var srcRect = minimized
                // 最小化窗口用还原位置，收进工作区内，从其原位起飞
                ? new RectangleF(Math.Clamp(rect.Left, area.Left, Math.Max(area.Left, area.Right - rect.Width)) - area.Left,
                                 Math.Clamp(rect.Top, area.Top, Math.Max(area.Top, area.Bottom - rect.Height)) - area.Top,
                                 rect.Width, rect.Height)
                : FromScreen(Win32.TryGetVisibleRect(hwnd, out var vis) ? vis : rect, area);

            // 优先复用上一节会话的资源（DWM 缩略图/标签），快速连滑不再反复拆装
            var old = _retired.FirstOrDefault(x => x.Hwnd == hwnd && x.IsMinimized == minimized && !reused.Contains(x));
            if (old is not null)
            {
                reused.Add(old);
                old.Title = title;
                old.SrcRect = srcRect;
                old.IsFocused = hwnd == foreground;
                old.Pending = !minimized;
                old.Failed = false;
                old.FadeIn = 1;
                old.Current = old.DrawRect = old.LastPaint = RectangleF.Empty;
                if (minimized)
                {
                    if (_snapCache.TryGetValue(hwnd, out var cb2)) { old.FromCache = true; old.Snapshot = cb2; old.LastPaint = srcRect; }
                    else { old.FromCache = false; old.Snapshot = null; }
                }
                fresh.Add(old);
                return true;
            }

            Bitmap? cachedBmp = null;
            bool cached = minimized && _snapCache.TryGetValue(hwnd, out cachedBmp);
            fresh.Add(new Entry
            {
                Hwnd = hwnd,
                Title = title,
                SrcRect = srcRect,
                IsMinimized = minimized,
                IsFocused = hwnd == foreground,
                FromCache = cached,
                Pending = !minimized, // 缩略图注册延后到首帧之后
                Snapshot = cached ? cachedBmp : null,
            });
            if (cached)
            {
                fresh[^1].LastPaint = fresh[^1].SrcRect;
                Log.W($"SnapCache: 命中 {title.TruncateLog(24)}（零延迟）");
            }
            return true;
        }, IntPtr.Zero);

        // 淘汰未复用的旧资源
        foreach (var old in _retired)
        {
            if (reused.Contains(old)) continue;
            if (old.Thumb != IntPtr.Zero) { Win32.DwmUnregisterThumbnail(old.Thumb); old.Thumb = IntPtr.Zero; }
            if (old.Label is not null) { Controls.Remove(old.Label); old.Label.Dispose(); old.Label = null; }
            if (!old.FromCache) old.Snapshot?.Dispose();
            old.Snapshot = null;
            old.TileBitmap?.Dispose();
            old.TileBitmap = null;
        }
        _retired.Clear();
        _entries = fresh;

        LayoutTiles();

        // 标签：新建或更新（含复用）
        foreach (var e in _entries)
        {
            if (e.Label is null)
            {
                e.Label = new Label
                {
                    ForeColor = Color.FromArgb(225, 225, 230),
                    BackColor = Color.FromArgb(30, 30, 36),
                    TextAlign = ContentAlignment.MiddleCenter,
                    AutoEllipsis = true,
                    Font = new Font("Segoe UI", 9.5F),
                    Visible = false, // 交互态才显示
                };
                Controls.Add(e.Label);
            }
            e.Label.Bounds = Rectangle.Round(e.LabelRect);
            e.Label.ForeColor = Color.FromArgb(225, 225, 230);
            e.Label.Text = e.IsMinimized ? $"{e.Title}（最小化）" : e.Title;
            if (e.IsMinimized && e.Snapshot is null && _printImmune.Contains(e.Hwnd))
                e.Label.Text = $"⚠ {e.Title}（无预览）";
        }

        // 慢快照（冷缓存的最小化窗口）排队延后：不再阻塞手势启动
        _captureQueue = _entries.Where(e => e.IsMinimized && !e.FromCache && e.Snapshot is null && !_printImmune.Contains(e.Hwnd)).ToList();
        _deferStage = _dump ? 2 : 0; // 0=待注册缩略图 1=逐个慢快照 2=无待办
        Log.W($"SnapshotWindows: {_entries.Count} 个窗口（最小化 {(_entries.Count(e => e.IsMinimized))}，慢快照排队 {_captureQueue.Count}）");
    }

    private List<Entry> _retired = new(); // 上一节会话的窗口资源池（快速连滑时复用）

    private static RectangleF FromScreen(Win32.RECT r, System.Drawing.Rectangle vs)
        => new(r.Left - vs.Left, r.Top - vs.Top, r.Width, r.Height);

    /// <summary>PrintWindow 内容快照（PW_RENDERFULLCONTENT，硬件加速内容也可抓）。</summary>
    private static Bitmap? PrintWindowSnapshot(IntPtr hwnd, int w, int h)
    {
        if (w <= 0 || h <= 0 || (long)w * h > 4096 * 4096) return null;
        try
        {
            using var full = new Bitmap(w, h);
            using (var g = Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = Win32.PrintWindow(hwnd, hdc, Win32.PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
                if (!ok) return null;
            }
            // 降采样控制内存（≤1024 宽）
            Bitmap result = full.Width > 1024
                ? new Bitmap(full, 1024, (int)Math.Round((double)h * 1024 / w))
                : new Bitmap(full);
            if (LooksBlank(result))
            {
                result.Dispose();
                Log.W($"Snapshot: 内容空白，丢弃 hwnd={hwnd} {w}x{h}");
                return null;
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 快照有效性启发式：按 32×18 网格采样亮度，标准差过低说明是空白/全黑
    /// （窗口已缩到最小化占位尺寸时 PrintWindow 就会产出这种废片）。
    /// </summary>
    private static bool LooksBlank(Bitmap bmp)
    {
        const int gw = 32, gh = 18;
        double sum = 0, sum2 = 0;
        const int n = gw * gh;
        for (int y = 0; y < gh; y++)
        {
            for (int x = 0; x < gw; x++)
            {
                var px = bmp.GetPixel(x * bmp.Width / gw, y * bmp.Height / gh);
                double luma = 0.299 * px.R + 0.587 * px.G + 0.114 * px.B;
                sum += luma;
                sum2 += luma * luma;
            }
        }
        double mean = sum / n;
        double variance = sum2 / n - mean * mean;
        return variance < 16; // stddev < 4 ≈ 纯色
    }

    /// <summary>
    /// 最小化窗口的快照：悄悄恢复显示（不抢焦点，过程被最顶层覆盖层挡住）→ PrintWindow → 再最小化。
    /// 每窗口约 30ms 出帧等待，仅手势开始时执行一次。
    /// </summary>
    private static Bitmap? RestoreAndPrint(IntPtr hwnd, int w, int h)
    {
        try
        {
            Win32.ShowWindow(hwnd, Win32.SW_SHOWNOACTIVATE);
            Thread.Sleep(30); // 等 DWM 重建窗口表面
            var bmp = PrintWindowSnapshot(hwnd, w, h);
            Win32.ShowWindow(hwnd, Win32.SW_MINIMIZE);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // 平铺布局：每个显示器的窗口归入本显示器网格（原位在哪个屏，就飞到哪个屏的网格）
    private void LayoutTiles()
    {
        if (_entries.Count == 0) return;
        var vs = _areaScr;

        var screens = _cfg.PrimaryMonitorOnly && Screen.PrimaryScreen is { } primaryScreen
            ? new[] { primaryScreen }
            : Screen.AllScreens;

        var groups = new List<Entry>[screens.Length];
        for (int i = 0; i < groups.Length; i++) groups[i] = new List<Entry>();
        foreach (var e in _entries)
        {
            var center = new Point((int)(e.SrcRect.Left + e.SrcRect.Width / 2) + vs.Left,
                                   (int)(e.SrcRect.Top + e.SrcRect.Height / 2) + vs.Top);
            int idx = Array.FindIndex(screens, s => s.WorkingArea.Contains(center));
            if (idx < 0) idx = 0; // 不在任何屏内（极端坐标）→ 归第一块
            groups[idx].Add(e);
        }

        for (int i = 0; i < screens.Length; i++)
        {
            if (groups[i].Count == 0) continue;
            var b = screens[i].WorkingArea; // 工作区：任务栏不被覆盖
            var area = new Rectangle(b.Left - vs.Left, b.Top - vs.Top, b.Width, b.Height);
            LayoutGroup(groups[i], area);
        }
    }

    // 就近布局（GNOME workspace.js 思想）：按原位 Y→X 排序贪心分行，平铺位落在原位附近、
    // 飞行距离最小化；统一缩放 × 窗高补偿（小窗最多 +25% 保可读），尝试 1..n 行按
    // 「缩放优先 + 空间利用」评分取最优，评分变差即停。
    private void LayoutGroup(List<Entry> group, Rectangle areaScr)
    {
        int n = group.Count;
        if (n == 0) return;
        const double gap = 20;
        const double labelH = 36;
        double margin = Math.Min(areaScr.Width, areaScr.Height) * 0.06;
        double availW = areaScr.Width - 2 * margin;
        double availH = areaScr.Height - 2 * margin;

        var items = group.OrderBy(e => e.SrcRect.Top).ThenBy(e => e.SrcRect.Left).ToList();
        double totalW = items.Sum(e => e.SrcRect.Width);

        List<List<Entry>>? bestRows = null;
        double bestScore = double.MinValue, bestScale = 0.02;

        for (int numRows = 1; numRows <= n; numRows++)
        {
            double idealRowW = totalW / numRows;
            var rows = new List<List<Entry>> { new() };
            foreach (var e in items)
            {
                var row = rows[^1];
                if (row.Count > 0 && row.Sum(x => x.SrcRect.Width) + e.SrcRect.Width > idealRowW * 1.2)
                    rows.Add(row = new List<Entry>());
                row.Add(e);
            }
            if (rows.Count < numRows && numRows < n) continue; // 该行数分不出来

            double scaleW = double.MaxValue, sumMaxH = 0;
            foreach (var row in rows)
            {
                double wSum = row.Sum(x => x.SrcRect.Width);
                if (wSum > 0) scaleW = Math.Min(scaleW, (availW - gap * (row.Count - 1)) / wSum);
                sumMaxH += row.Max(x => x.SrcRect.Height);
            }
            double scaleH = (availH - gap * (rows.Count - 1) - labelH * rows.Count) / Math.Max(1, sumMaxH);
            double scale = Math.Min(Math.Min(scaleW, scaleH), 0.65);
            if (scale <= 0.02) break;

            double usedW = rows.Max(r => r.Sum(x => x.SrcRect.Width) * scale + gap * (r.Count - 1));
            double score = scale + 0.55 * Math.Min(1, usedW / availW);
            if (score <= bestScore + 0.002) break; // 评分变差即停（GNOME 策略）
            bestScore = score;
            bestRows = rows;
            bestScale = scale;
        }

        bestRows ??= new List<List<Entry>> { items };
        double s = bestScale;

        double HeightFactor(Entry e) => 1 + 0.25 * Math.Clamp(1 - e.SrcRect.Height / areaScr.Height, 0, 1);

        var rowHeights = new List<double>();
        double rowsH = 0;
        foreach (var row in bestRows)
        {
            double h = row.Max(e => e.SrcRect.Height * s * HeightFactor(e)) + labelH;
            rowHeights.Add(h);
            rowsH += h;
        }

        double y = areaScr.Top + margin + Math.Max(0, (availH - rowsH - gap * (bestRows.Count - 1)) / 2);
        for (int r = 0; r < bestRows.Count; r++)
        {
            var row = bestRows[r];
            double rowW = row.Sum(e => e.SrcRect.Width * s * HeightFactor(e)) + gap * (row.Count - 1);
            double x = areaScr.Left + margin + Math.Max(0, (availW - rowW) / 2);
            foreach (var e in row)
            {
                double f = HeightFactor(e);
                double w = e.SrcRect.Width * s * f;
                double h = e.SrcRect.Height * s * f;
                e.TileRect = new RectangleF((float)x, (float)(y + (rowHeights[r] - labelH - h) / 2), (float)w, (float)h);
                e.LabelRect = new RectangleF((float)x, (float)(e.TileRect.Bottom + 4), Math.Max(120f, (float)w), (float)labelH);
                x += w + gap;
            }
            y += rowHeights[r] + gap;
        }
    }

    private void RegisterThumbnails()
    {
        foreach (var e in _entries)
        {
            if (e.IsMinimized) continue; // 最小化没有实时缩略图，走 OnPaint 里的静态快照
            e.Pending = false;
            if (e.Thumb != IntPtr.Zero) continue; // 复用上一节已注册的缩略图

            int hr = Win32.DwmRegisterThumbnail(Handle, e.Hwnd, out IntPtr thumb);
            if (hr == 0) e.Thumb = thumb;
            else
            {
                e.Failed = true;
                if (e.Label is not null) { e.Label.Text = $"⚠ {e.Title}"; e.Label.ForeColor = Color.DarkOrange; }
            }
        }
    }

    // ---- 定时器：脚本动画 + simdrag ----

    // PrintWindow 抓不到内容的窗口（如 Windows Terminal 的 DX 渲染）：
    // 记入名单后不再每次手势重试「还原→拍→黑片→丢弃」的 150ms 白费功夫
    private static readonly HashSet<IntPtr> _printImmune = new();

    private void TimerTickBody()
    {
        Interlocked.Exchange(ref _tickPending, 0);
        double now = _sw.Elapsed.TotalMilliseconds;

        // 手势启动后的延后工作：先注册缩略图，再逐帧补慢快照（问题4：不再阻塞启动）
        if (_deferStage < 2 && _phase is Phase.Entering or Phase.Interactive)
        {
            if (_deferStage == 0)
            {
                RegisterThumbnails();
                _deferStage = 1;
            }
            else if (_captureQueue.Count > 0)
            {
                var e = _captureQueue[0];
                _captureQueue.RemoveAt(0);
                if (_printImmune.Contains(e.Hwnd))
                {
                    e.Snapshot = null;
                }
                else
                {
                    e.Snapshot = RestoreAndPrint(e.Hwnd, (int)e.SrcRect.Width, (int)e.SrcRect.Height);
                    Log.W($"SnapQueue: {e.Title.TruncateLog(20)} hwnd={e.Hwnd} → {(e.Snapshot is null ? "失败，加入免疫名单" : "成功并入缓存")}");
                    if (e.Snapshot is not null)
                    {
                        // 成功即入缓存：后续手势零延迟复用，不再每手势重拍（窗口还原时钩子会清缓存）
                        _snapCache.TryGetValue(e.Hwnd, out var oldSnap);
                        _snapCache[e.Hwnd] = e.Snapshot;
                        e.FromCache = true;
                        oldSnap?.Dispose();
                    }
                    else _printImmune.Add(e.Hwnd);
                }
                if (e.Snapshot is not null) e.LastPaint = e.SrcRect;
            }
            else _deferStage = 2;
        }
        // 淡入：每帧 +0.18 直到不透明（替代 0.85→1.0 的突变，消除闪屏感）
        if (Opacity < 1.0 && _phase is Phase.Entering or Phase.Interactive)
            Opacity = Math.Min(1.0, Opacity + 0.18);

        // 跟手帧：输入只置脏，这里统一渲染（与提交动画同一条已验证的路径）
        if (_needsFrame && !_animating && _phase is Phase.Entering or Phase.Interactive)
        {
            _needsFrame = false;
            ApplyVisual(_p, force: true);
        }

        // 静默决断：本机触摸板按住静止时也会停发报文，「无报文」无法区分按住/松手。
        // 规则：静默超 250ms 且进度已达进入线 → 直接进入（停住=意图明确；顶格只等 180ms）；
        //       进度不足时宽容到 600ms 才取消（慢滑的微停顿不误杀）；彻底卡死不再发生。
        if (!_animating && !_sim &&
            now - _lastAnyInputMs > (_p >= 0.98 ? 180 : 250) &&
            !_gesture.IsThreeFingerEffective(now))
        {
            if (_phase == Phase.Entering)
            {
                if (_p >= _cfg.EnterCommit)
                {
                    Log.W($"Gesture: 静默 {(now - _lastAnyInputMs):F0}ms 且 p={_p:F2} → 进入");
                    _fullSinceMs = null;
                    CompleteEnter();
                }
                else if (now - _lastAnyInputMs > 600)
                {
                    Log.W($"Gesture: 静默 {(now - _lastAnyInputMs):F0}ms 且 p={_p:F2} → 取消");
                    _fullSinceMs = null;
                    CancelEnter();
                }
            }
            else if (_phase == Phase.Interactive && _exitDrag)
            {
                Log.W("Gesture: 静默决断（ExitDrag）");
                EvaluateExitRelease();
            }
        }

        if (_animating)
        {
            double t = Math.Clamp((now - _animStartMs) / _animDur, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3); // easeOutCubic
            ApplyVisual(_animFrom + (_animTo - _animFrom) * eased, force: true);
            if (t >= 1)
            {
                _animating = false;
                _animDone?.Invoke();
                _animDone = null;
            }
        }

        if (_sim && _phase == Phase.Entering)
        {
            double t = now - _simStartMs;
            double p = t switch
            {
                < 600 => Smooth01(t / 600),                       // 0 → 1 起飞
                < 900 => 1 - 0.5 * Smooth01((t - 600) / 300),     // 回撤到 0.5（验证可逆跟手）
                < 1300 => 0.5 + 0.35 * Smooth01((t - 900) / 400), // 再推到 0.85
                _ => 0.85,
            };
            if (t >= 1300)
            {
                _sim = false;
                Log.W("sim script end -> CompleteEnter");
                ApplyVisual(0.85, force: true);
                CompleteEnter(); // 满足 EnterCommit → 交互态
            }
            else ApplyVisual(p, force: true);
        }
    }

    private static double Smooth01(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    // ---- 鼠标/键盘 ----

    private int HitTest(Point p)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Current.Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_phase != Phase.Interactive) return;
        int hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_phase != Phase.Interactive) return;
        int hit = HitTest(e.Location);
        if (hit >= 0)
        {
            var target = _entries[hit];
            if (target.IsMinimized) Win32.ShowWindow(target.Hwnd, Win32.SW_RESTORE);
            Win32.SetForegroundWindow(target.Hwnd);
            Dismiss();
        }
        else
        {
            Dismiss();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Escape) return;
        if (_phase == Phase.Entering) { Dismiss(); return; }
        if (_phase == Phase.Interactive) DismissFocusHovered(); // Esc 也遵循「指针下的窗口接管焦点」
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // 进入交互态后 300ms 内的失焦是激活弹跳，忽略
        if (_sw.Elapsed.TotalMilliseconds - _interactiveAtMs < 300)
        {
            Log.W("OnDeactivate ignored (grace)");
            return;
        }
        if (_phase == Phase.Interactive && !_suppressDeactivate && !_animating)
        {
            Log.W("OnDeactivate -> Dismiss");
            BeginInvoke(Dismiss);
        }
    }

    // ---- 绘制 ----

    protected override void OnPaint(PaintEventArgs e)
    {
        double t0 = _sw.Elapsed.TotalMilliseconds;
        _paintCount++;
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low; // 快照缩放：飞行中低质量够用，落定后无变化不明显

        foreach (var entry in _entries)
        {
            if (entry.Pending) continue; // 缩略图未注册（首帧后补上）
            var snap = entry.TileBitmap ?? entry.EffectiveBitmap(_snapCache);
            if (entry.IsMinimized)
            {
                // 最小化窗口：静态快照；落定后加轻微暗化和实时缩略图区分（飞行中省掉这层填充）
                if (snap is not null && entry.DrawRect.Width > 1 && entry.FadeIn > 0.05)
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.DrawImage(snap, Rectangle.Round(entry.DrawRect));
                    if (_phase == Phase.Interactive)
                    {
                        using var dim = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
                        g.FillRectangle(dim, entry.DrawRect);
                    }
                }
                else
                {
                    using var fill = new SolidBrush(Color.FromArgb(40, 40, 46));
                    g.FillRectangle(fill, entry.Current);
                    TextRenderer.DrawText(g, "已最小化（无预览）", Font,
                        new Point((int)(entry.Current.Left + entry.Current.Width / 2 - 50),
                                  (int)(entry.Current.Top + entry.Current.Height / 2)),
                        Color.FromArgb(150, 150, 158));
                }
                continue;
            }
            if (!entry.Failed) continue;
            using var failFill = new SolidBrush(Color.FromArgb(40, 40, 46));
            g.FillRectangle(failFill, entry.Current);
            var size = TextRenderer.MeasureText(entry.Title, Font);
            TextRenderer.DrawText(g, entry.Title, Font,
                new Point((int)(entry.Current.Left + entry.Current.Width / 2 - size.Width / 2),
                          (int)(entry.Current.Top + entry.Current.Height / 2 - size.Height / 2)),
                Color.FromArgb(180, 180, 188));
        }

        if (_hover >= 0 && _hover < _entries.Count && _phase == Phase.Interactive)
        {
            var r = Rectangle.Round(_entries[_hover].Current);
            r.Inflate(3, 3);
            using var pen = new Pen(Color.FromArgb(160, 200, 255), 2.5f);
            g.DrawRectangle(pen, r);
        }

        string hint = HintText;
        if (hint.Length > 0)
        {
            using var font = new Font("Segoe UI", 11F);
            TextRenderer.DrawText(g, hint, font, new Point(24, ClientRectangle.Height - 56), Color.FromArgb(150, 150, 160));
        }
        if (RunOptions.Stats) _paintMs.Add(_sw.Elapsed.TotalMilliseconds - t0);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Win32.UnregisterHotKey(Handle, 1);
        if (_winEventHook != IntPtr.Zero) { Win32.UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
        _threadTimer.Dispose();
        foreach (var entry in _entries.Concat(_retired))
        {
            if (entry.Thumb != IntPtr.Zero) Win32.DwmUnregisterThumbnail(entry.Thumb);
            if (entry.Label is not null) { Controls.Remove(entry.Label); entry.Label.Dispose(); entry.Label = null; }
            if (!entry.FromCache) entry.Snapshot?.Dispose();
            entry.Snapshot = null;
            entry.TileBitmap?.Dispose();
            entry.TileBitmap = null;
        }
        _entries.Clear();
        _retired.Clear();
        foreach (var b in _snapCache.Values) b.Dispose();
        _snapCache.Clear();
        foreach (var b in _pendingDispose) b.Dispose();
        _pendingDispose.Clear();
        base.OnFormClosed(e);
    }
}
