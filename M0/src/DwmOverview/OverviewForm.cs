namespace M0.DwmOverview;

// M0 Demo2：全屏覆盖层，把当前桌面的顶层窗口用 DWM 缩略图实时平铺。
// 验证「自建任务视图」的渲染侧可行性：DwmRegisterThumbnail 由 DWM 合成，
// 内容是实时的（窗口里播视频也能看到），非截图。
internal sealed class OverviewForm : Form
{
    private sealed class Entry
    {
        public required IntPtr Hwnd;
        public required string Title;
        public required Rectangle Dest;      // 缩略图目标区域（客户区坐标）
        public IntPtr Thumb;                  // DWM 缩略图句柄，0 表示注册失败
        public Label? TitleLabel;
    }

    private readonly List<Entry> _entries = new();
    private int _hover = -1;
    private bool _closing;
    private readonly System.Windows.Forms.Timer _fadeTimer = new();

    private static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "tooltips_class32", "TaskListThumbnailWnd",
    };

    public OverviewForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(16, 16, 20);
        Opacity = 0.0;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Bounds = SystemInformation.VirtualScreen;
        CollectWindows();
        LayoutAndRegister();

        _fadeTimer.Interval = 12;
        _fadeTimer.Tick += (_, _) =>
        {
            if (Opacity >= 1.0) _fadeTimer.Stop();
            else Opacity = Math.Min(1.0, Opacity + 0.08);
        };
        _fadeTimer.Start();
        Activate();
    }

    private void CollectWindows()
    {
        var vdm = Win32.TryCreateVirtualDesktopManager();

        Win32.EnumWindows((hwnd, _) =>
        {
            if (_entries.Count >= 24) return false; // 控制缩略图数量
            if (hwnd == Handle) return true;
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return true;
            if ((Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0) return true;
            if (Win32.GetWindowLong(hwnd, Win32.GWLP_HWNDPARENT) != 0) return true; // 有 owner 的悬浮窗
            if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (SkipClasses.Contains(Win32.GetClassName(hwnd))) return true;
            if (!Win32.GetWindowRect(hwnd, out var rect) || rect.Width < 160 || rect.Height < 120) return true;

            string title = Win32.GetWindowText(hwnd);
            if (title.Length == 0) return true;

            if (vdm is not null)
            {
                try
                {
                    if (!vdm.IsWindowOnCurrentVirtualDesktop(hwnd)) return true; // 只显示当前虚拟桌面
                }
                catch { /* 某些窗口过渡态会失败，忽略 */ }
            }

            _entries.Add(new Entry { Hwnd = hwnd, Title = title, Dest = Rectangle.Empty });
            return true;
        }, IntPtr.Zero);
    }

    private void LayoutAndRegister()
    {
        int n = _entries.Count;
        if (n == 0) return;

        var area = ClientRectangle;
        int margin = (int)(Math.Min(area.Width, area.Height) * 0.06);
        const int gap = 20;
        const int labelH = 36;

        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling(n / (double)cols);
        int cellW = (area.Width - 2 * margin - (cols - 1) * gap) / cols;
        int cellH = (area.Height - 2 * margin - (rows - 1) * gap) / rows;
        int thumbH = cellH - labelH;

        for (int i = 0; i < n; i++)
        {
            int col = i % cols;
            int row = i / cols;
            int x = margin + col * (cellW + gap);
            int y = margin + row * (cellH + gap);

            var entry = _entries[i];
            entry.Dest = new Rectangle(x + 2, y + 2, cellW - 4, thumbH - 4);

            var label = new Label
            {
                Bounds = new Rectangle(x, y + thumbH, cellW, labelH),
                Text = entry.Title,
                ForeColor = Color.FromArgb(225, 225, 230),
                BackColor = Color.FromArgb(28, 28, 34),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 9.5F),
            };
            entry.TitleLabel = label;
            Controls.Add(label);

            int hr = Win32.DwmRegisterThumbnail(Handle, entry.Hwnd, out IntPtr thumb);
            if (hr == 0)
            {
                entry.Thumb = thumb;
                var props = new Win32.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = Win32.DWM_TNP_RECTDESTINATION | Win32.DWM_TNP_OPACITY
                              | Win32.DWM_TNP_VISIBLE | Win32.DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = new Win32.RECT
                    {
                        Left = entry.Dest.Left, Top = entry.Dest.Top,
                        Right = entry.Dest.Right, Bottom = entry.Dest.Bottom
                    },
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = true, // 只显示客户区，和 macOS 调度中心观感一致
                };
                Win32.DwmUpdateThumbnailProperties(thumb, ref props);
            }
            else
            {
                label.Text = $"⚠ {entry.Title}（hr=0x{hr:X8}）";
                label.ForeColor = Color.DarkOrange;
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        string hint = _entries.Count == 0
            ? "当前桌面没有可显示的窗口（点击或按 Esc 关闭）"
            : $"共 {_entries.Count} 个窗口 — 点击缩略图切换 / Esc 关闭";
        using var hintFont = new Font("Segoe UI", 11F);
        TextRenderer.DrawText(g, hint, hintFont, new Point(24, ClientRectangle.Height - 56), Color.FromArgb(140, 140, 150));

        foreach (var entry in _entries)
        {
            if (entry.Thumb != IntPtr.Zero) continue;
            using var fill = new SolidBrush(Color.FromArgb(38, 38, 44));
            g.FillRectangle(fill, entry.Dest);
        }

        if (_hover >= 0 && _hover < _entries.Count)
        {
            var r = _entries[_hover].Dest;
            r.Inflate(3, 3);
            using var pen = new Pen(Color.FromArgb(160, 200, 255), 2.5f);
            g.DrawRectangle(pen, r);
        }
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Dest.Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
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
        int hit = HitTest(e.Location);
        if (hit >= 0)
        {
            var target = _entries[hit];
            Win32.SetForegroundWindow(target.Hwnd);
            BeginInvoke(Close);
        }
        else
        {
            BeginInvoke(Close);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_closing) BeginInvoke(Close); // 失焦即退，贴近任务视图习惯
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true;
        _fadeTimer.Stop();
        foreach (var entry in _entries)
            if (entry.Thumb != IntPtr.Zero)
                Win32.DwmUnregisterThumbnail(entry.Thumb);
        base.OnFormClosed(e);
    }
}
