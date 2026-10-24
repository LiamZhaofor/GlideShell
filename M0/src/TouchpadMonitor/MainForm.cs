using System.Diagnostics;

namespace M0.TouchpadMonitor;

// M0 Demo1：订阅 PTP 原始输入，实时显示触点坐标流、报告率，
// 并做「三指上滑进度」实验——用三指平均位移直接驱动一个进度条，
// 验证「任务视图动画跟手」的输入侧可行性。
public sealed class MainForm : Form
{
    // UI
    private readonly Label _deviceLabel = new();
    private readonly Label _hzLabel = new();
    private readonly Label _intervalLabel = new();
    private readonly Label _failLabel = new();
    private readonly Label _contactsLabel = new();
    private readonly Label _fingerLabel = new();
    private readonly ProgressBar _progress = new();
    private readonly Panel _canvas = new();
    private readonly CheckBox _rawCheckBox = new();
    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();

    // 状态
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly Queue<string> _logQueue = new();
    private readonly List<double> _arrivalMs = new();
    private readonly List<(double nx, double ny)> _dots = new();
    private PtpReport? _lastReport;
    private int _lastCount = -1;
    private double? _baseNX, _baseNY, _baseLineNY;
    private double _deltaX, _deltaY;

    private const double SwipeThreshold = 0.30; // 归一化单位：三指上滑多少算 100%

    public MainForm()
    {
        Text = "M0 Demo1 — PTP 触摸板原始数据监控";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1150, 780);
        MinimumSize = new Size(900, 600);
        Font = new Font("Segoe UI", 9.5F);
        BackColor = Color.FromArgb(24, 24, 28);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 60, ColumnCount = 4, BackColor = Color.FromArgb(32, 32, 38), Padding = new Padding(10, 8, 10, 8) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));

        _deviceLabel.Dock = _hzLabel.Dock = _intervalLabel.Dock = _failLabel.Dock = DockStyle.Fill;
        _deviceLabel.ForeColor = _hzLabel.ForeColor = _intervalLabel.ForeColor = Color.Gainsboro;
        _failLabel.ForeColor = Color.OrangeRed;
        _deviceLabel.TextAlign = ContentAlignment.MiddleLeft;
        _hzLabel.TextAlign = _intervalLabel.TextAlign = _failLabel.TextAlign = ContentAlignment.MiddleLeft;
        _deviceLabel.Text = "正在检测触摸板…";
        top.Controls.Add(_deviceLabel, 0, 0);
        top.Controls.Add(_hzLabel, 1, 0);
        top.Controls.Add(_intervalLabel, 2, 0);
        top.Controls.Add(_failLabel, 3, 0);
        Controls.Add(top);

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 400, BackColor = BackColor };
        top.BringToFront(); // TableLayoutPanel Dock Top 会被后加的 Fill 遮住，提到前面

        // 左栏
        _contactsLabel.Dock = DockStyle.Top;
        _contactsLabel.Height = 64;
        _contactsLabel.Font = new Font("Segoe UI", 22F, FontStyle.Bold);
        _contactsLabel.ForeColor = Color.White;
        _contactsLabel.TextAlign = ContentAlignment.MiddleCenter;
        _contactsLabel.Text = "触点数: -";
        _contactsLabel.BackColor = Color.FromArgb(32, 32, 38);

        _fingerLabel.Dock = DockStyle.Top;
        _fingerLabel.Height = 76;
        _fingerLabel.Font = new Font("Segoe UI", 11F);
        _fingerLabel.ForeColor = Color.Gainsboro;
        _fingerLabel.TextAlign = ContentAlignment.MiddleLeft;
        _fingerLabel.Text = "在触摸板上滑动手指试试\n三指按下后向上滑动，观察进度条";
        _fingerLabel.BackColor = Color.FromArgb(28, 28, 33);
        _fingerLabel.Padding = new Padding(10, 0, 0, 0);

        _progress.Dock = DockStyle.Top;
        _progress.Height = 26;
        _progress.Minimum = 0;
        _progress.Maximum = 1000;

        _canvas.Dock = DockStyle.Top;
        _canvas.Height = 268;
        _canvas.BackColor = Color.FromArgb(28, 28, 33);
        _canvas.Paint += Canvas_Paint;

        _rawCheckBox.Dock = DockStyle.Top;
        _rawCheckBox.Height = 32;
        _rawCheckBox.Text = "日志显示原始坐标（而非归一化）";
        _rawCheckBox.ForeColor = Color.Gainsboro;
        _rawCheckBox.BackColor = Color.FromArgb(28, 28, 33);
        _rawCheckBox.Padding = new Padding(8, 0, 0, 0);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Padding = new Padding(10, 8, 10, 8),
            Text = "M0 验证点：\n1) 报告率稳定（多数触摸板 90~125Hz）\n2) 三指同时按下能拿到全部 3 个触点坐标\n3) 三指上滑时 ΔY 平滑递增、进度条跟手\n4) 突然抬手 count 归零（松手事件可检测）\n\n本窗口在后台时依然接收输入（RIDEV_INPUTSINK）。"
        };

        split.Panel1.Controls.Add(hint);
        split.Panel1.Controls.Add(_rawCheckBox);
        split.Panel1.Controls.Add(_canvas);
        split.Panel1.Controls.Add(_progress);
        split.Panel1.Controls.Add(_fingerLabel);
        split.Panel1.Controls.Add(_contactsLabel);

        // 右栏：日志
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.FromArgb(20, 20, 24);
        _log.ForeColor = Color.FromArgb(210, 228, 210);
        _log.Font = new Font("Consolas", 9F);
        _log.WordWrap = false;
        split.Panel2.Controls.Add(_log);

        Controls.Add(split);

        _uiTimer.Interval = 100;
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!PtpRawInput.RegisterInput(Handle))
        {
            _deviceLabel.Text = "RegisterRawInputDevices 失败！";
            _deviceLabel.ForeColor = Color.OrangeRed;
            return;
        }
        RefreshDeviceLabel();
    }

    private void RefreshDeviceLabel()
    {
        var pads = PtpRawInput.EnumerateTouchpads();
        if (pads.Count == 0)
        {
            _deviceLabel.Text = "未检测到精确式触摸板（PTP）";
            _deviceLabel.ForeColor = Color.OrangeRed;
            return;
        }
        _deviceLabel.ForeColor = Color.Gainsboro;
        _deviceLabel.Text = $"PTP × {pads.Count}：{string.Join("  ", pads.Select(p => $"VID_{p.VendorId:X4}/PID_{p.ProductId:X4}"))}";
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == PtpRawInput.WM_INPUT && m.LParam != IntPtr.Zero)
        {
            var report = PtpRawInput.TryParse(m.LParam);
            if (report is not null) OnReport(report);
        }
        else if (m.Msg == PtpRawInput.WM_INPUT_DEVICE_CHANGE)
        {
            RefreshDeviceLabel();
        }
        base.WndProc(ref m);
    }

    private void OnReport(PtpReport r)
    {
        double now = _sw.Elapsed.TotalMilliseconds;
        _arrivalMs.Add(now);
        while (_arrivalMs.Count > 2000) _arrivalMs.RemoveAt(0);

        _lastReport = r;
        _lastCount = (int)r.ContactCount;

        _dots.Clear();
        foreach (var c in r.Contacts) _dots.Add((c.NX, c.NY));

        // 三指状态机：进入 3 指瞬间记基线，期间累计平均位移（上滑为正）
        if (r.ContactCount == 3 && r.Contacts.Count >= 3)
        {
            double avgNX = r.Contacts.Average(c => c.NX);
            double avgNY = r.Contacts.Average(c => c.NY);
            double baseNX = _baseNX ?? avgNX;
            double baseNY = _baseNY ?? avgNY;
            _baseNX = baseNX;
            _baseNY = baseNY;
            _baseLineNY = baseNY;
            _deltaX = avgNX - baseNX;
            _deltaY = baseNY - avgNY;
        }
        else
        {
            _baseNX = _baseNY = _baseLineNY = null;
            _deltaX = _deltaY = 0;
        }

        if (_logQueue.Count < 2000)
        {
            string coord = _rawCheckBox.Checked ? "{0}:({1},{2})" : "{0}:({3:F2},{4:F2})";
            var parts = r.Contacts.Select(c => string.Format(coord, c.Id, c.X, c.Y, c.NX, c.NY));
            string delta = r.ContactCount == 3 ? $"  ΔX={_deltaX:+0.000;-0.000} ΔY={_deltaY:+0.000;-0.000}" : "";
            _logQueue.Enqueue($"[{now,9:F1}ms] cc={r.ContactCount} sT={r.ScanTime,5} {string.Join(" ", parts)}{delta}");
        }
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        double now = _sw.Elapsed.TotalMilliseconds;
        for (int i = 0; i < _arrivalMs.Count;)
        {
            if (now - _arrivalMs[i] > 1000) _arrivalMs.RemoveAt(i);
            else i++;
        }
        _hzLabel.Text = $"报告率 ~{_arrivalMs.Count} Hz";
        if (_arrivalMs.Count >= 2)
            _intervalLabel.Text = $"报告间隔 {Math.Max(0, _arrivalMs[^1] - _arrivalMs[^2]):F1} ms";
        _failLabel.Text = PtpRawInput.FailCount > 0 ? $"解析失败 {PtpRawInput.FailCount}" : "";

        _contactsLabel.Text = $"触点数: {_lastCount}";
        if (_lastCount == 3 && _baseNX is not null)
        {
            double pct = Math.Clamp(_deltaY / SwipeThreshold, 0, 1) * 100;
            _fingerLabel.Text = $"三指追踪中：ΔX={_deltaX:+0.000;-0.000}  ΔY={_deltaY:+0.000;-0.000}\n上滑进度 {pct:F0}%（阈值 {SwipeThreshold:F2}）";
            _progress.Value = (int)(Math.Clamp(_deltaY / SwipeThreshold, 0, 1) * 1000);
        }
        else
        {
            _fingerLabel.Text = _lastCount < 0 ? "在触摸板上滑动手指试试" : $"当前 {_lastCount} 指（三指按下后向上滑动，观察进度条）";
            _progress.Value = 0;
        }

        _canvas.Invalidate();

        if (_logQueue.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            while (_logQueue.Count > 0) sb.AppendLine(_logQueue.Dequeue());
            _log.AppendText(sb.ToString());
            if (_log.TextLength > 60_000) _log.Text = _log.Text[^30_000..];
        }
    }

    private void Canvas_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        double aspect = 1.6;
        if (PtpRawInput.TryGetLogicalBounds(PtpRawInput.LastDevice, out int xMin, out int xMax, out int yMin, out int yMax)
            && xMax > xMin && yMax > yMin)
        {
            aspect = (xMax - xMin) / (double)(yMax - yMin);
        }

        var client = _canvas.ClientRectangle;
        int pad = 16;
        double boxW = client.Width - 2 * pad;
        double boxH = boxW / aspect;
        if (boxH > client.Height - 2 * pad)
        {
            boxH = client.Height - 2 * pad;
            boxW = boxH * aspect;
        }
        var box = new RectangleF((float)((client.Width - boxW) / 2), (float)((client.Height - boxH) / 2), (float)boxW, (float)boxH);

        using var borderPen = new Pen(Color.FromArgb(90, 90, 100), 1.6f);
        g.DrawRectangle(borderPen, box.X, box.Y, box.Width, box.Height);

        if (_baseLineNY is double baseLine)
        {
            float y = box.Y + (float)(baseLine * box.Height);
            using var basePen = new Pen(Color.FromArgb(200, 231, 116), 1.2f) { DashPattern = new float[] { 4, 4 } };
            g.DrawLine(basePen, box.X, y, box.X + box.Width, y);
        }

        foreach (var (nx, ny) in _dots)
        {
            float px = box.X + (float)(Math.Clamp(nx, 0, 1) * box.Width);
            float py = box.Y + (float)(Math.Clamp(ny, 0, 1) * box.Height);
            using var brush = new SolidBrush(Color.FromArgb(120, 210, 255));
            g.FillEllipse(brush, px - 6, py - 6, 12, 12);
            using var outline = new Pen(Color.FromArgb(30, 60, 90), 1.4f);
            g.DrawEllipse(outline, px - 6, py - 6, 12, 12);
        }
    }
}
