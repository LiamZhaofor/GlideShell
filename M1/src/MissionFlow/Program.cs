namespace M1.MissionFlow;

internal static class RunOptions
{
    public static bool Stats;
    public static bool Dump;
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        RunOptions.Stats = args.Contains("--stats");
        RunOptions.Dump = args.Contains("--dump");

        Win32.timeBeginPeriod(1); // 提升 WM_TIMER 分辨率，动画帧率不再卡 64fps 上限
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext(args.Contains("--simdrag"), RunOptions.Dump));
        Win32.timeEndPeriod(1);
    }

    // 托盘宿主：主窗体常驻隐藏（靠 RIDEV_INPUTSINK 收 WM_INPUT），不占任务栏
    private sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly OverlayForm _overlay;
        private readonly ToolStripMenuItem _pauseItem;

        public TrayContext(bool simDrag, bool dumpMode)
        {
            _overlay = new OverlayForm(dumpMode);
            _ = _overlay.Handle; // 强制创建句柄：注册 Raw Input 与热键

            if (dumpMode)
            {
                _icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "MissionFlow 诊断", Visible = true };
                var d = new System.Windows.Forms.Timer { Interval = 800 };
                d.Tick += (_, _) => { d.Stop(); d.Dispose(); _icon.Visible = false; _overlay.DumpWindows(); };
                d.Start();
                return;
            }

            var menu = new ContextMenuStrip();
            menu.Items.Add("打开调度中心（Ctrl+Alt+O）", null, (_, _) => _overlay.OpenByHotkey());
            _pauseItem = new ToolStripMenuItem("暂停三指手势") { CheckOnClick = true };
            _pauseItem.Click += (_, _) => OverlayForm.GesturesPaused = _pauseItem.Checked;
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("系统触摸板设置…", null, (_, _) =>
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:devices-touchpad") { UseShellExecute = true }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => Exit());
            _icon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "MissionFlow M2",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.DoubleClick += (_, _) => _overlay.OpenByHotkey();

            // 手势屏蔽检测：不认外部写入时引导用户手动设置
            if (!SystemGestureGuard.TryBlockUpDown())
            {
                _icon.BalloonTipTitle = "三指上下滑可能未屏蔽";
                _icon.BalloonTipText = "若三指上滑仍弹出系统任务视图，请到 设置→蓝牙和其他设备→触摸板→高级手势，把三指向上/向下轻扫设为'无'（点击本提示直接打开）。";
                _icon.BalloonTipClicked += (_, _) =>
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:devices-touchpad") { UseShellExecute = true });
                _icon.ShowBalloonTip(8000);
            }

            if (simDrag)
            {
                var t = new System.Windows.Forms.Timer { Interval = 700 };
                t.Tick += (_, _) => { t.Stop(); t.Dispose(); _overlay.RunSimDrag(); };
                t.Start();
            }
        }

        private void Exit()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _overlay.Dispose();
            Application.ExitThread(); // 不动系统手势设置：那是用户的配置，由用户管理
        }
    }
}
