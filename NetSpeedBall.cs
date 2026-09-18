using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using FormsTimer = System.Windows.Forms.Timer;
using ThreadingTimer = System.Threading.Timer;

namespace NetSpeedBall
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            using (Mutex singleInstance = new Mutex(true, "NetSpeedBall.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // 已经有一个实例在跑了：让那个把悬浮球亮出来，别让用户以为双击没反应。
                    uint message = NativeMethods.RegisterWindowMessage("NetSpeedBall.ShowBall");
                    if (message != 0)
                    {
                        NativeMethods.PostMessage(new IntPtr(NativeMethods.HwndBroadcast), message,
                            IntPtr.Zero, IntPtr.Zero);
                    }

                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (args != null && args.Length >= 2 &&
                    string.Equals(args[0], "--render-preview", StringComparison.OrdinalIgnoreCase))
                {
                    SpeedBallForm.SavePreview(args[1]);
                    return;
                }

                Application.Run(new SpeedBallApplication());
            }
        }
    }

    internal sealed class SpeedBallApplication : ApplicationContext
    {
        private const string AppName = "NetSpeedBall";
        private readonly NotifyIcon trayIcon;
        private readonly SpeedBallForm ballForm;
        private readonly ThreadingTimer sampleTimer;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem showBallItem;
        private readonly ToolStripMenuItem alwaysOnTopItem;
        private readonly ToolStripMenuItem startupItem;
        private readonly ToolStripMenuItem opacityItem;
        private readonly ToolStripMenuItem megabytesItem;
        private readonly ToolStripMenuItem megabitsItem;
        private readonly NetworkSpeedSampler sampler = new NetworkSpeedSampler();
        private readonly TrafficStore trafficStore = new TrafficStore();
        private readonly AppTrafficMonitor appMonitor;
        private TrafficWindow trafficWindow;
        private int flushCounter;
        private volatile bool disposed;
        private int sampling;
        private bool hasNetworkState;
        private bool lastNetworkState;

        public SpeedBallApplication()
        {
            appMonitor = new AppTrafficMonitor(trafficStore);
            ballForm = new SpeedBallForm(trafficStore);
            ballForm.Show();

            showBallItem = new ToolStripMenuItem("\u663e\u793a\u60ac\u6d6e\u7403", null, OnToggleBall)
            {
                Checked = true,
                CheckOnClick = true
            };
            alwaysOnTopItem = new ToolStripMenuItem("\u7a97\u53e3\u7f6e\u9876", null, OnToggleTopMost)
            {
                Checked = true,
                CheckOnClick = true
            };
            startupItem = new ToolStripMenuItem("\u5f00\u673a\u81ea\u542f\u52a8", null, OnToggleStartup)
            {
                Checked = StartupManager.IsEnabled(AppName),
                CheckOnClick = true
            };
            opacityItem = new ToolStripMenuItem("\u534a\u900f\u660e\u6a21\u5f0f", null, OnToggleOpacity)
            {
                Checked = false,
                CheckOnClick = true
            };
            bool useMegabits = AppSettings.LoadUseMegabits();
            megabytesItem = new ToolStripMenuItem("MB/s", null, OnUseMegabytes) { Checked = !useMegabits };
            megabitsItem = new ToolStripMenuItem("Mbps", null, OnUseMegabits) { Checked = useMegabits };
            ballForm.UseMegabits = useMegabits;

            menu = new ContextMenuStrip();
            menu.Items.Add(showBallItem);
            menu.Items.Add(alwaysOnTopItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(opacityItem);
            ToolStripMenuItem unitMenu = new ToolStripMenuItem("\u901f\u5ea6\u5355\u4f4d");
            unitMenu.DropDownItems.Add(megabytesItem);
            unitMenu.DropDownItems.Add(megabitsItem);
            menu.Items.Add(unitMenu);
            menu.Items.Add(new ToolStripMenuItem("\u6253\u5f00\u7f51\u7edc\u8be6\u60c5", null, OnShowDetails));
            menu.Items.Add(new ToolStripMenuItem("\u6d41\u91cf\u7edf\u8ba1", null, OnShowTrafficWindow));
            menu.Items.Add(new ToolStripMenuItem("\u6253\u5f00\u4efb\u52a1\u7ba1\u7406\u5668", null, OnOpenTaskManager));
            menu.Items.Add(new ToolStripMenuItem("\u91cd\u7f6e\u4eca\u65e5\u6d41\u91cf", null, OnResetTraffic));
            if (!AppTrafficMonitor.CanCollectAppTraffic)
            {
                // 逐连接的字节统计只对管理员开放，普通权限下这里给一条现成的通路。
                menu.Items.Add(new ToolStripMenuItem("\u4ee5\u7ba1\u7406\u5458\u8eab\u4efd\u91cd\u542f", null, OnRestartElevated));
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("\u9000\u51fa", null, OnExit));

            // 每次弹出菜单都重新读一遍真实状态：Windows 的"启动应用"列表可能把本程序单独
            // 关掉，那样界面会显示成已勾选、开机却根本不启动。
            menu.Opening += delegate
            {
                try
                {
                    startupItem.Checked = StartupManager.IsEnabled(AppName);
                }
                catch
                {
                }
            };

            Icon icon = IconFactory.CreateIcon(64);
            trayIcon = new NotifyIcon
            {
                Text = "\u7f51\u7edc\u6d4b\u901f\u7403",
                Icon = icon,
                ContextMenuStrip = menu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate { ToggleBallVisible(true); };
            ballForm.ContextMenuStrip = menu;
            ballForm.SetIcon(icon);

            sampleTimer = new ThreadingTimer(OnSampleTimer, null, 0, 1000);
        }

        private void OnSampleTimer(object state)
        {
            if (disposed || System.Threading.Interlocked.Exchange(ref sampling, 1) == 1)
            {
                return;
            }

            try
            {
                SpeedSample sample = sampler.Next();
                // NIC-level deltas feed the persistent traffic history (all protocols).
                trafficStore.RecordNic(DateTime.Today, sample.DownloadBytes, sample.UploadBytes);
                try
                {
                    appMonitor.Sample();
                }
                catch
                {
                    // Per-app attribution is best-effort; keep sampling even if the
                    // TCP tables briefly fail.
                }

                if (++flushCounter >= 60)
                {
                    flushCounter = 0;
                    trafficStore.Flush();
                }

                PostSample(sample);
            }
            catch
            {
                // Keep the animation alive even if Windows briefly fails to read an adapter.
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref sampling, 0);
            }
        }

        private void PostSample(SpeedSample sample)
        {
            if (disposed || ballForm.IsDisposed)
            {
                return;
            }

            try
            {
                ballForm.BeginInvoke(new Action(delegate
                {
                    if (disposed || ballForm.IsDisposed)
                    {
                        return;
                    }

                    ballForm.UpdateSpeed(sample);
                    trayIcon.Text = BuildTrayText(sample);
                    UpdateNetworkNotification(sample.HasAnyAdapter);
                }));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void UpdateNetworkNotification(bool connected)
        {
            if (!hasNetworkState)
            {
                hasNetworkState = true;
                lastNetworkState = connected;
                return;
            }

            if (connected == lastNetworkState)
            {
                return;
            }

            lastNetworkState = connected;
            trayIcon.BalloonTipTitle = "\u7f51\u7edc\u6d4b\u901f\u7403";
            trayIcon.BalloonTipText = connected
                ? "\u7f51\u7edc\u5df2\u6062\u590d\u8fde\u63a5"
                : "\u7f51\u7edc\u8fde\u63a5\u5df2\u65ad\u5f00";
            trayIcon.BalloonTipIcon = connected ? ToolTipIcon.Info : ToolTipIcon.Warning;
            trayIcon.ShowBalloonTip(3000);
        }

        private static string BuildTrayText(SpeedSample sample)
        {
            string text = "\u7f51\u7edc\u6d4b\u901f\u7403\r\n" +
                          "\u4e0b\u8f7d " + SpeedFormatter.FormatMegabytes(sample.DownloadBytesPerSecond) + "  " +
                          "\u4e0a\u4f20 " + SpeedFormatter.FormatMegabytes(sample.UploadBytesPerSecond);
            return text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private void OnToggleBall(object sender, EventArgs e)
        {
            ToggleBallVisible(showBallItem.Checked);
        }

        private void ToggleBallVisible(bool visible)
        {
            showBallItem.Checked = visible;
            if (visible)
            {
                ballForm.EnsureVisible();
                ballForm.Show();
                ballForm.BringToFront();
            }
            else
            {
                ballForm.Hide();
            }
        }

        private void OnToggleTopMost(object sender, EventArgs e)
        {
            ballForm.SetTopMost(alwaysOnTopItem.Checked);
            if (alwaysOnTopItem.Checked)
            {
                ballForm.BringToFront();
            }
        }

        private void OnToggleStartup(object sender, EventArgs e)
        {
            try
            {
                StartupManager.SetEnabled(AppName, startupItem.Checked);
            }
            catch (Exception ex)
            {
                startupItem.Checked = StartupManager.IsEnabled(AppName);
                MessageBox.Show("\u8bbe\u7f6e\u5f00\u673a\u81ea\u542f\u52a8\u5931\u8d25\uff1a\r\n" + ex.Message, "\u7f51\u7edc\u6d4b\u901f\u7403",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnToggleOpacity(object sender, EventArgs e)
        {
            ballForm.Opacity = opacityItem.Checked ? 0.82 : 0.96;
        }

        private void OnUseMegabytes(object sender, EventArgs e)
        {
            megabytesItem.Checked = true;
            megabitsItem.Checked = false;
            ballForm.UseMegabits = false;
            AppSettings.SaveUseMegabits(false);
        }

        private void OnUseMegabits(object sender, EventArgs e)
        {
            megabytesItem.Checked = false;
            megabitsItem.Checked = true;
            ballForm.UseMegabits = true;
            AppSettings.SaveUseMegabits(true);
        }

        private void OnShowDetails(object sender, EventArgs e)
        {
            ToggleBallVisible(true);
            ballForm.ShowDetails();
        }

        private void OnShowTrafficWindow(object sender, EventArgs e)
        {
            if (trafficWindow == null || trafficWindow.IsDisposed)
            {
                trafficWindow = new TrafficWindow(trafficStore);
            }

            if (!trafficWindow.Visible)
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                trafficWindow.Location = new Point(
                    area.Left + Math.Max(0, (area.Width - trafficWindow.Width) / 2),
                    area.Top + Math.Max(0, (area.Height - trafficWindow.Height) / 2));
                trafficWindow.Show();
            }

            trafficWindow.Activate();
        }

        private void OnOpenTaskManager(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "taskmgr.exe",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("\u6253\u5f00\u4efb\u52a1\u7ba1\u7406\u5668\u5931\u8d25\uff1a\r\n" + ex.Message,
                    "\u7f51\u7edc\u6d4b\u901f\u7403", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnResetTraffic(object sender, EventArgs e)
        {
            trafficStore.ResetToday();
            ballForm.ResetTodayTraffic();
            trayIcon.BalloonTipTitle = "\u7f51\u7edc\u6d4b\u901f\u7403";
            trayIcon.BalloonTipText = "\u4eca\u65e5\u6d41\u91cf\u7edf\u8ba1\u5df2\u91cd\u7f6e";
            trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            trayIcon.ShowBalloonTip(2000);
        }

        private void OnRestartElevated(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("\u63d0\u6743\u5931\u8d25\uff1a\r\n" + ex.Message, "\u7f51\u7edc\u6d4b\u901f\u7403",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ExitThread();
        }

        private void OnExit(object sender, EventArgs e)
        {
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposed)
            {
                base.Dispose(disposing);
                return;
            }

            disposed = true;
            if (disposing && sampleTimer != null)
            {
                sampleTimer.Dispose();
            }

            // Persist whatever is still pending before the UI goes away.
            if (appMonitor != null)
            {
                appMonitor.Dispose();
            }

            if (trafficStore != null)
            {
                trafficStore.Close();
            }

            if (disposing && trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }

            if (disposing && menu != null)
            {
                menu.Dispose();
            }

            if (disposing && ballForm != null)
            {
                ballForm.Close();
                ballForm.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class SpeedBallForm : Form
    {
        private const int BallSize = 122;
        // 第二个实例启动时会广播这条消息，界面上收到就把悬浮球叫出来。
        private static readonly int ShowBallMessageId =
            (int)NativeMethods.RegisterWindowMessage("NetSpeedBall.ShowBall");
        private SpeedSample currentSample;
        private bool useMegabits;
        private Point dragStart;
        private bool dragMoved;
        private bool dragging;
        private readonly FormsTimer animationTimer;
        private readonly LaunchPadForm launchPadForm;
        private readonly SpeedDetailsForm detailsForm;
        private Font labelFont;
        private Font speedFont;
        private Font unitFont;
        private Font statFont;
        private Font statArrowFont;
        private Icon appIcon;
        private bool overLaunchPad;
        private bool launchAnimating;
        private bool launchVisualMode;
        private float rocketAngle = -58.0f;
        private float rocketOrbitSpeed = 34.0f;
        private double launchElapsed;
        private Point dragOriginLocation;
        private Point launchStartLocation;
        private Point launchTargetLocation;
        private Point launchReturnLocation;
        private double launchReturnOpacity;
        private readonly TrafficStore trafficStore;
        private DockEdge dockEdge;
        private bool docked;
        private Rectangle dockArea;
        private DateTime lastAnimationFrame = DateTime.UtcNow;
        private static readonly Color LaunchTransparentColor = Color.FromArgb(240, 248, 255);

        private enum DockEdge
        {
            None,
            Left,
            Right,
            Top,
            Bottom
        }

        public SpeedBallForm() : this(null)
        {
        }

        public SpeedBallForm(TrafficStore store)
        {
            trafficStore = store;
            Text = "\u7f51\u7edc\u6d4b\u901f\u7403";
            Size = new Size(BallSize, BallSize);
            MinimumSize = Size;
            MaximumSize = Size;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.White;
            TransparencyKey = Color.Empty;
            Opacity = 0.96;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            labelFont = new Font("Segoe UI", 6.8f, FontStyle.Bold, GraphicsUnit.Point);
            speedFont = new Font("Segoe UI", 25.0f, FontStyle.Bold, GraphicsUnit.Point);
            unitFont = new Font("Segoe UI", 8.8f, FontStyle.Regular, GraphicsUnit.Point);
            statFont = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
            statArrowFont = new Font("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Point);

            animationTimer = new FormsTimer { Interval = 33 };
            animationTimer.Tick += OnAnimationTick;
            animationTimer.Start();

            launchPadForm = new LaunchPadForm();
            detailsForm = new SpeedDetailsForm(trafficStore);

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Point savedLocation;
            Location = AppSettings.TryLoadLocation(out savedLocation)
                ? savedLocation
                : new Point(area.Right - Width - 28, area.Bottom - Height - 42);
            UpdateWindowRegion();
            KeepInsideWorkingArea();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public void SetTopMost(bool value)
        {
            TopMost = value;
            detailsForm.TopMost = value;
        }

        public void EnsureVisible()
        {
            if (docked)
            {
                ExpandFromDock();
            }
            else
            {
                KeepInsideWorkingArea();
            }
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
        }

        public bool UseMegabits
        {
            get { return useMegabits; }
            set { useMegabits = value; Invalidate(); }
        }

        public void SetIcon(Icon icon)
        {
            appIcon = icon;
            Icon = icon;
        }

        public void UpdateSpeed(SpeedSample sample)
        {
            currentSample = sample;
            detailsForm.AddSample(sample);
            Invalidate();
        }

        public static void SavePreview(string path)
        {
            using (Bitmap bitmap = new Bitmap(BallSize, BallSize))
            using (Graphics g = Graphics.FromImage(bitmap))
            using (SpeedBallForm preview = new SpeedBallForm())
            {
                g.Clear(Color.Transparent);
                preview.currentSample = new SpeedSample(850.0 * 1024.0, 25.6 * 1024.0, true);
                preview.DrawSurface(g);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private void OnAnimationTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            double elapsed = Math.Min(0.08, Math.Max(0.0, (now - lastAnimationFrame).TotalSeconds));
            lastAnimationFrame = now;

            float targetSpeed = GetRocketOrbitSpeed();
            float blend = Math.Min(1.0f, (float)(elapsed * 4.5));
            rocketOrbitSpeed += (targetSpeed - rocketOrbitSpeed) * blend;
            rocketAngle += (float)(rocketOrbitSpeed * elapsed);
            if (rocketAngle >= 360.0f)
            {
                rocketAngle -= 360.0f;
            }

            if (launchAnimating)
            {
                UpdateLaunchAnimation(elapsed);
            }

            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !launchAnimating)
            {
                ExpandFromDock();
                dragging = true;
                dragMoved = false;
                dragStart = e.Location;
                dragOriginLocation = Location;
                Cursor = Cursors.Default;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging)
            {
                if (!docked && dockEdge == DockEdge.None && IsTouchingScreenEdge())
                {
                    SnapToScreenEdgeIfNeeded();
                }
                return;
            }

            Point screenPoint = PointToScreen(e.Location);
            if (Math.Abs(screenPoint.X - (dragOriginLocation.X + dragStart.X)) > SystemInformation.DragSize.Width / 2 ||
                Math.Abs(screenPoint.Y - (dragOriginLocation.Y + dragStart.Y)) > SystemInformation.DragSize.Height / 2)
            {
                if (!dragMoved)
                {
                    dragMoved = true;
                    Cursor = Cursors.SizeAll;
                    ShowLaunchPad();
                }
            }
            if (!dragMoved)
            {
                return;
            }
            Location = new Point(screenPoint.X - dragStart.X, screenPoint.Y - dragStart.Y);
            UpdateLaunchPadHover();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (launchAnimating)
            {
                return;
            }

            bool shouldLaunch = dragging && overLaunchPad;
            bool shouldToggleDetails = dragging && !dragMoved && !shouldLaunch;
            dragging = false;
            Capture = false;
            Cursor = Cursors.Default;
            if (shouldLaunch)
            {
                BeginLaunchAnimation();
            }
            else
            {
                HideLaunchPad();
                if (!SnapToScreenEdgeIfNeeded())
                {
                    KeepInsideWorkingArea();
                }
                AppSettings.SaveLocation(Location);
                overLaunchPad = false;
                SetLaunchVisualMode(false);
                Invalidate();
            }

            if (shouldToggleDetails)
            {
                ToggleDetails();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            ExpandFromDock();
            if (!docked && dockEdge == DockEdge.None && IsTouchingScreenEdge())
            {
                SnapToScreenEdgeIfNeeded();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (dockEdge == DockEdge.None || dragging || launchAnimating)
            {
                return;
            }

            // Defer the check so the rounded region does not flicker during pointer movement.
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (!dragging && !launchAnimating && dockEdge != DockEdge.None &&
                        !ClientRectangle.Contains(PointToClient(Cursor.Position)))
                    {
                        CollapseToDock();
                    }
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void ShowDetails()
        {
            if (!detailsForm.Visible)
            {
                ToggleDetails();
            }
            else
            {
                detailsForm.BringToFront();
            }
        }

        public void ResetTodayTraffic()
        {
            detailsForm.ResetTodayTraffic();
        }

        private void ToggleDetails()
        {
            if (detailsForm.Visible)
            {
                detailsForm.Hide();
                return;
            }

            Rectangle area = Screen.FromControl(this).WorkingArea;
            int x = Right + 10;
            if (x + detailsForm.Width > area.Right)
            {
                x = Left - detailsForm.Width - 10;
            }

            int y = Math.Max(area.Top, Math.Min(Top - 20, area.Bottom - detailsForm.Height));
            detailsForm.Location = new Point(x, y);
            detailsForm.TopMost = TopMost;
            detailsForm.Show();
        }

        private void ExpandFromDock()
        {
            if (!docked || dockEdge == DockEdge.None)
            {
                return;
            }

            Rectangle area = GetDockArea();
            int x = Left;
            int y = Top;
            switch (dockEdge)
            {
                case DockEdge.Left:
                    x = area.Left;
                    y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
                    break;
                case DockEdge.Right:
                    x = area.Right - Width;
                    y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
                    break;
                case DockEdge.Top:
                    x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
                    y = area.Top;
                    break;
                case DockEdge.Bottom:
                    x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
                    y = area.Bottom - Height;
                    break;
            }

            docked = false;
            Location = new Point(x, y);
        }

        private void CollapseToDock()
        {
            if (dockEdge == DockEdge.None)
            {
                return;
            }

            Rectangle area = GetDockArea();
            int x = Left;
            int y = Top;
            const int peek = 18;
            switch (dockEdge)
            {
                case DockEdge.Left:
                    x = area.Left - Width + peek;
                    y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
                    break;
                case DockEdge.Right:
                    x = area.Right - peek;
                    y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
                    break;
                case DockEdge.Top:
                    x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
                    y = area.Top - Height + peek;
                    break;
                case DockEdge.Bottom:
                    x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
                    y = area.Bottom - peek;
                    break;
            }

            docked = true;
            Location = new Point(x, y);
        }

        private bool SnapToScreenEdgeIfNeeded()
        {
            Rectangle area = GetWorkingArea();
            const int threshold = 16;
            // Use the gap that remains inside the work area. When the window has
            // crossed an edge, the gap is zero rather than a large absolute value.
            int leftDistance = Math.Max(0, Left - area.Left);
            int rightDistance = Math.Max(0, area.Right - Right);
            int topDistance = Math.Max(0, Top - area.Top);
            int bottomDistance = Math.Max(0, area.Bottom - Bottom);
            int closest = Math.Min(Math.Min(leftDistance, rightDistance), Math.Min(topDistance, bottomDistance));

            if (closest > threshold)
            {
                dockEdge = DockEdge.None;
                docked = false;
                dockArea = Rectangle.Empty;
                return false;
            }

            dockArea = area;

            if (closest == leftDistance)
            {
                dockEdge = DockEdge.Left;
            }
            else if (closest == rightDistance)
            {
                dockEdge = DockEdge.Right;
            }
            else if (closest == topDistance)
            {
                dockEdge = DockEdge.Top;
            }
            else
            {
                dockEdge = DockEdge.Bottom;
            }

            CollapseToDock();
            return true;
        }

        private bool IsTouchingScreenEdge()
        {
            Rectangle area = GetWorkingArea();
            return Left <= area.Left || Right >= area.Right || Top <= area.Top || Bottom >= area.Bottom;
        }

        private Rectangle GetWorkingArea()
        {
            return Screen.FromControl(this).WorkingArea;
        }

        private Rectangle GetDockArea()
        {
            if (dockArea.Width > 0 && dockArea.Height > 0)
            {
                return dockArea;
            }

            return GetWorkingArea();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            BringToFront();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg != 0 && m.Msg == ShowBallMessageId)
            {
                Show();
                EnsureVisible();
                BringToFront();
            }

            base.WndProc(ref m);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateWindowRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawSurface(e.Graphics);
        }

        private void DrawSurface(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            Rectangle rect = new Rectangle(5, 5, Width - 11, Height - 11);
            if (launchAnimating || overLaunchPad)
            {
                DrawLaunchRocketMode(g, rect);
                return;
            }

            DrawBall(g, rect);
            DrawSpeedText(g);
        }

        private void DrawBall(Graphics g, Rectangle rect)
        {
            using (GraphicsPath ballPath = new GraphicsPath())
            {
                ballPath.AddEllipse(rect);

                using (PathGradientBrush face = new PathGradientBrush(ballPath))
                {
                    face.CenterPoint = new PointF(rect.X + rect.Width * 0.48f, rect.Y + rect.Height * 0.38f);
                    face.CenterColor = Color.White;
                    face.SurroundColors = new[] { Color.FromArgb(236, 243, 250) };
                    g.FillPath(face, ballPath);
                }

                using (LinearGradientBrush sheen = new LinearGradientBrush(rect,
                           Color.FromArgb(120, 255, 255, 255),
                           Color.FromArgb(0, 255, 255, 255),
                           LinearGradientMode.Vertical))
                {
                    g.FillPath(sheen, ballPath);
                }
            }

            using (GraphicsPath glossPath = new GraphicsPath())
            {
                Rectangle gloss = new Rectangle(rect.X + 15, rect.Y + 8, rect.Width - 30, 34);
                glossPath.AddEllipse(gloss);
                using (PathGradientBrush glow = new PathGradientBrush(glossPath))
                {
                    glow.CenterColor = Color.FromArgb(152, 255, 255, 255);
                    glow.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                    g.FillPath(glow, glossPath);
                }
            }

            DrawClouds(g, rect);

            Rectangle ringRect = Rectangle.Inflate(rect, -4, -4);
            float activity = GetActivityRatio();
            using (Pen glow = new Pen(Color.FromArgb(118, 70, 174, 232), 5.0f))
            using (Pen outer = new Pen(Color.FromArgb(246, 250, 253, 255), 2.4f))
            using (Pen inner = new Pen(Color.FromArgb(205, 132, 196, 232), 1.1f))
            using (Pen progress = new Pen(currentSample.HasAnyAdapter ? Color.FromArgb(212, 38, 143, 233) : Color.FromArgb(88, 164, 183, 205), 2.3f))
            {
                progress.StartCap = LineCap.Round;
                progress.EndCap = LineCap.Round;

                g.DrawEllipse(glow, ringRect);
                g.DrawEllipse(outer, rect);
                g.DrawEllipse(inner, Rectangle.Inflate(rect, -7, -7));
                g.DrawArc(progress, Rectangle.Inflate(rect, -6, -6), 126, Math.Max(32, 74 + activity * 190));
            }

            DrawRocket(g, rect);
        }

        private void DrawClouds(Graphics g, Rectangle rect)
        {
            GraphicsState state = g.Save();
            using (GraphicsPath clipPath = new GraphicsPath())
            {
                clipPath.AddEllipse(rect);
                g.SetClip(clipPath, CombineMode.Intersect);

                using (Brush body = new SolidBrush(Color.FromArgb(246, 251, 255)))
                using (Brush shade = new SolidBrush(Color.FromArgb(221, 235, 244, 250)))
                using (Pen edge = new Pen(Color.FromArgb(142, 198, 217, 232), 1.0f))
                {
                    int y = rect.Bottom - 29;
                    g.FillEllipse(body, rect.X + 7, y + 7, 34, 28);
                    g.FillEllipse(body, rect.X + 25, y, 33, 34);
                    g.FillEllipse(body, rect.X + 48, y + 7, 34, 28);
                    g.FillEllipse(body, rect.X + 72, y + 3, 36, 32);
                    g.FillRectangle(body, rect.X + 11, y + 19, rect.Width - 22, 24);
                    g.FillEllipse(shade, rect.X + 69, y + 18, 36, 19);

                    g.DrawArc(edge, rect.X + 7, y + 7, 34, 28, 194, 126);
                    g.DrawArc(edge, rect.X + 25, y, 33, 34, 190, 128);
                    g.DrawArc(edge, rect.X + 72, y + 3, 36, 32, 210, 112);
                }
            }

            g.Restore(state);
        }

        private void DrawRocket(Graphics g, Rectangle rect)
        {
            float angle = rocketAngle;
            double radians = angle * Math.PI / 180.0;
            PointF center = new PointF(rect.X + rect.Width / 2.0f, rect.Y + rect.Height / 2.0f);
            float radius = rect.Width * 0.37f;
            PointF position = new PointF(
                center.X + (float)Math.Cos(radians) * radius,
                center.Y + (float)Math.Sin(radians) * radius);

            GraphicsState state = g.Save();
            g.TranslateTransform(position.X, position.Y);
            g.RotateTransform(angle + 90.0f);
            g.ScaleTransform(0.86f, 0.86f);

            float flameStrength = GetRocketFlameStrength();
            float visibleFlame = currentSample.HasAnyAdapter ? Math.Max(0.36f, flameStrength) : 0.0f;
            if (visibleFlame > 0.0f)
            {
                float pulse = 0.88f + 0.12f * (float)Math.Sin(rocketAngle * 0.24f);
                float flameLength = (8.0f + 16.0f * visibleFlame) * pulse;
                float flameHalfWidth = 2.0f + 2.8f * visibleFlame;
                int orangeAlpha = (int)(175 + 80 * visibleFlame);
                int yellowAlpha = (int)(150 + 105 * visibleFlame);
                using (GraphicsPath flame = new GraphicsPath())
                using (Brush orange = new SolidBrush(Color.FromArgb(orangeAlpha, 255, 137, 42)))
                using (Brush yellow = new SolidBrush(Color.FromArgb(yellowAlpha, 255, 231, 116)))
                {
                    flame.AddBezier(-9.5f, -flameHalfWidth, -12.0f, -flameHalfWidth * 0.8f, -10.0f - flameLength, -1.0f, -10.0f - flameLength, 0.0f);
                    flame.AddBezier(-10.0f - flameLength, 0.0f, -10.0f - flameLength, 1.0f, -12.0f, flameHalfWidth * 0.8f, -9.5f, flameHalfWidth);
                    flame.CloseFigure();
                    g.FillPath(orange, flame);
                    g.FillEllipse(yellow, -12.0f - flameLength * 0.42f, -1.35f, 3.0f + flameLength * 0.35f, 2.7f);
                }
            }

            using (GraphicsPath leftFin = new GraphicsPath())
            using (GraphicsPath rightFin = new GraphicsPath())
            using (Brush fin = new SolidBrush(Color.FromArgb(255, 45, 132, 224)))
            using (Pen finEdge = new Pen(Color.FromArgb(176, 22, 91, 176), 0.8f))
            {
                leftFin.AddPolygon(new[]
                {
                    new PointF(-5.4f, -3.4f),
                    new PointF(-9.8f, -8.0f),
                    new PointF(-2.0f, -5.1f)
                });
                rightFin.AddPolygon(new[]
                {
                    new PointF(-5.4f, 3.4f),
                    new PointF(-9.8f, 8.0f),
                    new PointF(-2.0f, 5.1f)
                });
                g.FillPath(fin, leftFin);
                g.FillPath(fin, rightFin);
                g.DrawPath(finEdge, leftFin);
                g.DrawPath(finEdge, rightFin);
            }

            using (GraphicsPath body = new GraphicsPath())
            using (LinearGradientBrush bodyBrush = new LinearGradientBrush(
                       new RectangleF(-9.0f, -7.0f, 19.0f, 14.0f),
                       Color.White,
                       Color.FromArgb(203, 231, 255),
                       LinearGradientMode.ForwardDiagonal))
            using (Pen bodyEdge = new Pen(Color.FromArgb(190, 82, 144, 215), 1.0f))
            {
                body.AddBezier(-9.0f, -5.2f, -3.8f, -8.0f, 5.2f, -5.0f, 9.2f, 0.0f);
                body.AddBezier(9.2f, 0.0f, 5.2f, 5.0f, -3.8f, 8.0f, -9.0f, 5.2f);
                body.CloseFigure();
                g.FillPath(bodyBrush, body);
                g.DrawPath(bodyEdge, body);
            }

            using (Brush window = new SolidBrush(Color.FromArgb(255, 39, 132, 227)))
            using (Pen windowEdge = new Pen(Color.FromArgb(210, 255, 255, 255), 0.8f))
            {
                g.FillEllipse(window, 0.6f, -2.6f, 5.2f, 5.2f);
                g.DrawEllipse(windowEdge, 0.6f, -2.6f, 5.2f, 5.2f);
            }

            g.Restore(state);
        }

        private void DrawLaunchRocketMode(Graphics g, Rectangle rect)
        {
            double pulse = 0.5 + 0.5 * Math.Sin(launchElapsed * 42.0);
            float flameScale = launchAnimating
                ? 0.9f + (float)Math.Min(0.85, launchElapsed * 1.8) + (float)pulse * 0.16f
                : 0.68f + (float)pulse * 0.08f;
            float shakeX = launchAnimating && launchElapsed < 0.24
                ? (float)Math.Sin(launchElapsed * 78.0) * 1.8f
                : 0.0f;
            float lift = launchAnimating && launchElapsed < 0.24
                ? (float)(launchElapsed / 0.24 * 4.0)
                : 4.0f;
            PointF center = new PointF(rect.X + rect.Width / 2.0f + shakeX, rect.Y + rect.Height * 0.51f - lift);
            DrawLaunchRocketBody(g, center, 2.55f, -90.0f, true, flameScale);
        }

        private void DrawLaunchRocketBody(Graphics g, PointF center, float scale, float angle, bool flameOn, float flameScale)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(center.X, center.Y);
            g.RotateTransform(angle);
            g.ScaleTransform(scale, scale);

            if (flameOn)
            {
                using (GraphicsPath flame = new GraphicsPath())
                using (Brush orange = new SolidBrush(Color.FromArgb(236, 255, 142, 48)))
                using (Brush yellow = new SolidBrush(Color.FromArgb(225, 255, 230, 110)))
                {
                    float tail = -18.0f * flameScale;
                    flame.AddPolygon(new[]
                    {
                        new PointF(-10.5f, -3.6f),
                        new PointF(tail, 0.0f),
                        new PointF(-10.5f, 3.6f)
                    });
                    g.FillPath(orange, flame);
                    g.FillEllipse(yellow, -14.4f * flameScale, -2.0f, 5.4f * flameScale, 4.0f);
                }
            }

            using (GraphicsPath leftFin = new GraphicsPath())
            using (GraphicsPath rightFin = new GraphicsPath())
            using (Brush fin = new SolidBrush(Color.FromArgb(255, 44, 132, 224)))
            using (Pen finEdge = new Pen(Color.FromArgb(176, 22, 91, 176), 0.8f))
            {
                leftFin.AddPolygon(new[]
                {
                    new PointF(-5.4f, -3.4f),
                    new PointF(-10.0f, -8.0f),
                    new PointF(-2.0f, -5.1f)
                });
                rightFin.AddPolygon(new[]
                {
                    new PointF(-5.4f, 3.4f),
                    new PointF(-10.0f, 8.0f),
                    new PointF(-2.0f, 5.1f)
                });
                g.FillPath(fin, leftFin);
                g.FillPath(fin, rightFin);
                g.DrawPath(finEdge, leftFin);
                g.DrawPath(finEdge, rightFin);
            }

            using (GraphicsPath body = new GraphicsPath())
            using (LinearGradientBrush bodyBrush = new LinearGradientBrush(
                       new RectangleF(-9.0f, -7.0f, 19.5f, 14.0f),
                       Color.White,
                       Color.FromArgb(201, 232, 255),
                       LinearGradientMode.ForwardDiagonal))
            using (Pen bodyEdge = new Pen(Color.FromArgb(196, 70, 143, 220), 1.0f))
            {
                body.AddBezier(-9.0f, -5.2f, -3.8f, -8.0f, 5.2f, -5.0f, 9.2f, 0.0f);
                body.AddBezier(9.2f, 0.0f, 5.2f, 5.0f, -3.8f, 8.0f, -9.0f, 5.2f);
                body.CloseFigure();
                g.FillPath(bodyBrush, body);
                g.DrawPath(bodyEdge, body);
            }

            using (Brush window = new SolidBrush(Color.FromArgb(255, 36, 133, 229)))
            using (Pen windowEdge = new Pen(Color.FromArgb(214, 255, 255, 255), 0.8f))
            {
                g.FillEllipse(window, 0.7f, -2.7f, 5.4f, 5.4f);
                g.DrawEllipse(windowEdge, 0.7f, -2.7f, 5.4f, 5.4f);
            }

            g.Restore(state);
        }

        private void DrawSpeedText(Graphics g)
        {
            if (!currentSample.HasAnyAdapter)
            {
                DrawCenteredLine(g, "\u79bb\u7ebf", speedFont, 35, Color.FromArgb(28, 34, 48), Width - 30);
                DrawCenteredLine(g, "WAITING", unitFont, 70, Color.FromArgb(104, 118, 136), Width - 44);
                return;
            }

            RateText main = useMegabits
                ? SpeedFormatter.FormatBits(currentSample.DownloadBytesPerSecond)
                : SpeedFormatter.FormatMegabytesRate(currentSample.DownloadBytesPerSecond);
            string down = SpeedFormatter.FormatCompact(currentSample.DownloadBytesPerSecond);
            string up = SpeedFormatter.FormatCompact(currentSample.UploadBytesPerSecond);

            DrawCenteredLine(g, main.Value, speedFont, 33, Color.FromArgb(20, 27, 42), Width - 30);
            DrawCenteredLine(g, main.Unit, unitFont, 66, Color.FromArgb(70, 84, 102), Width - 48);
            DrawStatPair(g, "\u2191", up, "\u2193", down, 84);
        }

        private void DrawStatPair(Graphics g, string upArrow, string up, string downArrow, string down, int y)
        {
            Font valueFont = statFont;
            Font arrowFont = statArrowFont;
            bool disposeFonts = false;
            float arrowGap = 1.5f;
            float pairGap = 7.0f;
            // The ball is a circular window and its bottom edge is much narrower than
            // the form, so keep this row inside the safe chord width of the circle.
            float maxWidth = Width - 34.0f;
            float total = MeasureStatRow(g, upArrow, arrowFont, up, valueFont,
                downArrow, down, arrowGap, pairGap);
            if (total > maxWidth)
            {
                float scale = maxWidth / total;
                valueFont = new Font(statFont.FontFamily, Math.Max(8.0f, statFont.Size * scale),
                    statFont.Style, statFont.Unit);
                arrowFont = new Font(statArrowFont.FontFamily, Math.Max(9.5f, statArrowFont.Size * scale),
                    statArrowFont.Style, statArrowFont.Unit);
                disposeFonts = true;
                total = MeasureStatRow(g, upArrow, arrowFont, up, valueFont,
                    downArrow, down, arrowGap, pairGap);
            }

            float x = (Width - total) / 2.0f;
            float numY = y;
            float arrowY = y - (arrowFont.Height - valueFont.Height) / 2.0f;

            using (Brush upBrush = new SolidBrush(Color.FromArgb(255, 16, 198, 94)))
            using (Brush downBrush = new SolidBrush(Color.FromArgb(255, 35, 119, 238)))
            using (Brush light = new SolidBrush(Color.FromArgb(176, 255, 255, 255)))
            {
                float cx = x;
                DrawStatSegment(g, upArrow, arrowFont, light, ref cx, arrowY + 1);
                DrawStatSegment(g, up, valueFont, light, ref cx, numY + 1);
                cx += pairGap;
                DrawStatSegment(g, downArrow, arrowFont, light, ref cx, arrowY + 1);
                DrawStatSegment(g, down, valueFont, light, ref cx, numY + 1);

                cx = x;
                DrawStatSegment(g, upArrow, arrowFont, upBrush, ref cx, arrowY);
                DrawStatSegment(g, up, valueFont, upBrush, ref cx, numY);
                cx += pairGap;
                DrawStatSegment(g, downArrow, arrowFont, downBrush, ref cx, arrowY);
                DrawStatSegment(g, down, valueFont, downBrush, ref cx, numY);
            }

            if (disposeFonts)
            {
                valueFont.Dispose();
                arrowFont.Dispose();
            }
        }

        private static float MeasureStatRow(Graphics g, string upArrow, Font arrowFont, string up, Font valueFont,
            string downArrow, string down, float arrowGap, float pairGap)
        {
            float width = g.MeasureString(upArrow, arrowFont).Width + arrowGap +
                g.MeasureString(up, valueFont).Width + pairGap +
                g.MeasureString(downArrow, arrowFont).Width + arrowGap +
                g.MeasureString(down, valueFont).Width;
            return width;
        }

        private static void DrawStatSegment(Graphics g, string text, Font font, Brush brush, ref float x, float y)
        {
            g.DrawString(text, font, brush, x, y);
            x += g.MeasureString(text, font).Width;
        }

        private float GetActivityRatio()
        {
            if (!currentSample.HasAnyAdapter)
            {
                return 0.0f;
            }

            double bytes = Math.Max(currentSample.DownloadBytesPerSecond, currentSample.UploadBytesPerSecond);
            double ratio = Math.Log(bytes + 1.0) / Math.Log((2.0 * 1024.0 * 1024.0) + 1.0);
            if (bytes > 0 && ratio < 0.08)
            {
                ratio = 0.08;
            }

            if (ratio > 1.0)
            {
                ratio = 1.0;
            }

            return (float)ratio;
        }

        private float GetRocketOrbitSpeed()
        {
            float strength = GetRocketFlameStrength();
            return 24.0f + strength * 156.0f;
        }

        private float GetRocketFlameStrength()
        {
            if (!currentSample.HasAnyAdapter)
            {
                return 0.0f;
            }

            double download = Math.Max(0.0, currentSample.DownloadBytesPerSecond);
            const double referenceSpeed = 8.0 * 1024.0 * 1024.0;
            double strength = Math.Log(1.0 + download / 8192.0) / Math.Log(1.0 + referenceSpeed / 8192.0);
            return (float)Math.Max(0.0, Math.Min(1.0, strength));
        }

        private void DrawCenteredLine(Graphics g, string text, Font font, int y, Color color, float maxWidth)
        {
            Font drawFont = font;
            bool disposeFont = false;
            SizeF size = g.MeasureString(text, drawFont);
            if (size.Width > maxWidth && maxWidth > 10)
            {
                float fittedSize = Math.Max(6.6f, font.Size * maxWidth / size.Width);
                drawFont = new Font(font.FontFamily, fittedSize, font.Style, font.Unit);
                disposeFont = true;
                size = g.MeasureString(text, drawFont);
            }

            Color shadowColor = color.R + color.G + color.B < 420
                ? Color.FromArgb(92, 255, 255, 255)
                : Color.FromArgb(72, 34, 67, 115);

            using (Brush shadow = new SolidBrush(shadowColor))
            using (Brush foreground = new SolidBrush(color))
            {
                float x = (Width - size.Width) / 2.0f;
                g.DrawString(text, drawFont, shadow, x + 1, y + 1);
                g.DrawString(text, drawFont, foreground, x, y);
            }

            if (disposeFont)
            {
                drawFont.Dispose();
            }
        }

        private void ShowLaunchPad()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int x = area.Left + (area.Width - launchPadForm.Width) / 2;
            int y = area.Top + (int)(area.Height * 0.67f) - launchPadForm.Height / 2;
            launchPadForm.Location = new Point(x, Math.Max(area.Top, Math.Min(y, area.Bottom - launchPadForm.Height)));
            launchPadForm.SetActive(false);
            if (!launchPadForm.Visible)
            {
                launchPadForm.Show();
            }

            launchPadForm.TopMost = true;
            BringToFront();
            overLaunchPad = false;
        }

        private void HideLaunchPad()
        {
            if (launchPadForm.Visible)
            {
                launchPadForm.Hide();
            }

            launchPadForm.SetActive(false);
        }

        private void UpdateLaunchPadHover()
        {
            bool active = launchPadForm.Visible && launchPadForm.AcceptsBall(GetBallCenterScreen());
            if (active == overLaunchPad)
            {
                return;
            }

            overLaunchPad = active;
            launchPadForm.SetActive(active);
            SetLaunchVisualMode(active);
            Invalidate();
        }

        private Point GetBallCenterScreen()
        {
            return new Point(Left + Width / 2, Top + Height / 2);
        }

        private void BeginLaunchAnimation()
        {
            dragging = false;
            Capture = false;
            Cursor = Cursors.Default;
            launchAnimating = true;
            launchElapsed = 0.0;
            launchStartLocation = Location;
            launchReturnLocation = dragOriginLocation;
            launchReturnOpacity = Opacity;

            Rectangle area = Screen.FromControl(this).WorkingArea;
            int targetX = launchStartLocation.X + 18;
            int targetY = area.Top - Height - 40;
            launchTargetLocation = new Point(targetX, targetY);

            HideLaunchPad();
            overLaunchPad = true;
            SetLaunchVisualMode(true);
            Invalidate();
        }

        private void UpdateLaunchAnimation(double elapsed)
        {
            const double chargeDuration = 0.24;
            const double flightDuration = 1.05;
            const double totalDuration = chargeDuration + flightDuration;
            launchElapsed += elapsed;

            if (launchElapsed < chargeDuration)
            {
                double charge = launchElapsed / chargeDuration;
                int shakeX = (int)Math.Round(Math.Sin(launchElapsed * 82.0) * (1.0 + charge * 2.2));
                int compression = (int)Math.Round(Math.Sin(charge * Math.PI) * 5.0);
                Location = new Point(launchStartLocation.X + shakeX, launchStartLocation.Y + compression);
                return;
            }

            double t = Math.Min(1.0, (launchElapsed - chargeDuration) / flightDuration);
            double accelerated = t * t * (3.0 - 2.0 * t);
            accelerated = Math.Pow(accelerated, 0.72);
            Point next = Lerp(launchStartLocation, launchTargetLocation, accelerated);
            double sway = Math.Sin(t * Math.PI * 1.35) * (1.0 - t) * 20.0;
            next.X += (int)Math.Round(sway);
            Location = next;
            Opacity = Math.Max(0.18, 0.96 - Math.Max(0.0, t - 0.72) / 0.28 * 0.78);

            if (launchElapsed >= totalDuration)
            {
                launchAnimating = false;
                overLaunchPad = false;
                launchElapsed = 0.0;
                Location = launchReturnLocation;
                Opacity = launchReturnOpacity;
                SetLaunchVisualMode(false);
                KeepInsideWorkingArea();
                Invalidate();
            }
        }

        private void SetLaunchVisualMode(bool active)
        {
            if (launchVisualMode == active)
            {
                return;
            }

            launchVisualMode = active;
            if (active)
            {
                BackColor = LaunchTransparentColor;
                TransparencyKey = LaunchTransparentColor;
            }
            else
            {
                TransparencyKey = Color.Empty;
                BackColor = Color.White;
            }

            UpdateWindowRegion();
        }

        private static double EaseOutCubic(double t)
        {
            double inverse = 1.0 - t;
            return 1.0 - inverse * inverse * inverse;
        }

        private static Point Lerp(Point start, Point end, double t)
        {
            return new Point(
                start.X + (int)Math.Round((end.X - start.X) * t),
                start.Y + (int)Math.Round((end.Y - start.Y) * t));
        }

        private void UpdateWindowRegion()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            if (launchVisualMode)
            {
                Region oldRegion = Region;
                Region = null;
                if (oldRegion != null)
                {
                    oldRegion.Dispose();
                }

                return;
            }

            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(new Rectangle(4, 4, Width - 9, Height - 9));
                Region oldRegion = Region;
                Region = new Region(path);
                if (oldRegion != null)
                {
                    oldRegion.Dispose();
                }
            }
        }

        private void KeepInsideWorkingArea()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
            int y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
            Location = new Point(x, y);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (animationTimer != null) animationTimer.Dispose();
                if (labelFont != null) labelFont.Dispose();
                if (speedFont != null) speedFont.Dispose();
                if (unitFont != null) unitFont.Dispose();
                if (statFont != null) statFont.Dispose();
                if (statArrowFont != null) statArrowFont.Dispose();
                if (appIcon != null) appIcon.Dispose();
                if (launchPadForm != null) launchPadForm.Dispose();
                if (detailsForm != null) detailsForm.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class SpeedDetailsForm : Form
    {
        private const int MaxSamples = 60;
        private readonly double[] downloadHistory = new double[MaxSamples];
        private readonly double[] uploadHistory = new double[MaxSamples];
        private int sampleCount;
        private readonly TrafficStore trafficStore;
        private SpeedSample currentSample;
        private readonly SystemResourceMonitor resourceMonitor = new SystemResourceMonitor();
        private SystemResourceSnapshot resourceSnapshot;
        private readonly Font titleFont = new Font("Segoe UI", 12.0f, FontStyle.Bold);
        private readonly Font valueFont = new Font("Segoe UI", 10.0f, FontStyle.Bold);
        private readonly Font smallFont = new Font("Segoe UI", 8.2f, FontStyle.Regular);

        public SpeedDetailsForm(TrafficStore store)
        {
            trafficStore = store;
            Text = "Network details";
            Size = new Size(330, 372);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(248, 252, 255);
            DoubleBuffered = true;
            Padding = new Padding(12);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public void ResetTodayTraffic()
        {
            // The store itself is reset by SpeedBallApplication; just repaint.
            Invalidate();
        }

        public void AddSample(SpeedSample sample)
        {
            currentSample = sample;
            resourceSnapshot = resourceMonitor.Read();

            if (sampleCount < MaxSamples)
            {
                downloadHistory[sampleCount] = sample.DownloadBytesPerSecond;
                uploadHistory[sampleCount] = sample.UploadBytesPerSecond;
                sampleCount++;
            }
            else
            {
                Array.Copy(downloadHistory, 1, downloadHistory, 0, MaxSamples - 1);
                Array.Copy(uploadHistory, 1, uploadHistory, 0, MaxSamples - 1);
                downloadHistory[MaxSamples - 1] = sample.DownloadBytesPerSecond;
                uploadHistory[MaxSamples - 1] = sample.UploadBytesPerSecond;
            }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle body = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = CreateRoundedRectangle(body, 18))
            using (LinearGradientBrush background = new LinearGradientBrush(body, Color.White, Color.FromArgb(235, 247, 255), LinearGradientMode.Vertical))
            using (Pen border = new Pen(Color.FromArgb(120, 71, 164, 226), 1.2f))
            {
                g.FillPath(background, path);
                g.DrawPath(border, path);
            }

            using (Brush dark = new SolidBrush(Color.FromArgb(28, 38, 56)))
            using (Brush muted = new SolidBrush(Color.FromArgb(96, 112, 132)))
            using (Brush downBrush = new SolidBrush(Color.FromArgb(35, 119, 238)))
            using (Brush upBrush = new SolidBrush(Color.FromArgb(16, 178, 92)))
            {
                g.DrawString("\u7f51\u7edc\u8be6\u60c5", titleFont, dark, 16, 13);
                g.DrawString(currentSample.HasAnyAdapter ? "\u8fde\u63a5\u6b63\u5e38" : "\u7f51\u7edc\u79bb\u7ebf", smallFont, muted, 236, 17);
                g.DrawString("\u4e0b\u8f7d  " + SpeedFormatter.FormatMegabytes(currentSample.DownloadBytesPerSecond), valueFont, downBrush, 16, 43);
                g.DrawString("\u4e0a\u4f20  " + SpeedFormatter.FormatMegabytes(currentSample.UploadBytesPerSecond), valueFont, upBrush, 168, 43);
                g.DrawString("\u6700\u8fd1 60 \u79d2", smallFont, muted, 16, 72);
            }

            Rectangle graph = new Rectangle(16, 91, Width - 32, 82);
            DrawGraph(g, graph);

            long todayDate = TrafficStore.ToDateInt(DateTime.Today);
            long[] todayTotals = trafficStore != null
                ? trafficStore.GetDayTotals(todayDate)
                : new long[] { 0, 0 };
            DateTime monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            long[] monthTotals = trafficStore != null
                ? trafficStore.GetRangeTotals(TrafficStore.ToDateInt(monthStart), todayDate)
                : new long[] { 0, 0 };

            using (Brush muted = new SolidBrush(Color.FromArgb(90, 108, 128)))
            using (Brush dark = new SolidBrush(Color.FromArgb(32, 44, 62)))
            {
                g.DrawString("\u4eca\u65e5\u4e0b\u8f7d", smallFont, muted, 18, 184);
                g.DrawString(SpeedFormatter.FormatTraffic(todayTotals[0]), valueFont, dark, 18, 201);
                g.DrawString("\u4eca\u65e5\u4e0a\u4f20", smallFont, muted, 174, 184);
                g.DrawString(SpeedFormatter.FormatTraffic(todayTotals[1]), valueFont, dark, 174, 201);
                g.DrawString("\u672c\u6708\u4e0b\u8f7d", smallFont, muted, 18, 224);
                g.DrawString(SpeedFormatter.FormatTraffic(monthTotals[0]), valueFont, dark, 18, 241);
                g.DrawString("\u672c\u6708\u4e0a\u4f20", smallFont, muted, 174, 224);
                g.DrawString(SpeedFormatter.FormatTraffic(monthTotals[1]), valueFont, dark, 174, 241);
            }

            DrawResourcePanel(g);
        }

        private void DrawResourcePanel(Graphics g)
        {
            Rectangle panel = new Rectangle(16, 275, Width - 32, 76);
            using (Brush fill = new SolidBrush(Color.FromArgb(235, 245, 252)))
            using (Pen border = new Pen(Color.FromArgb(150, 190, 218), 1.0f))
            {
                g.FillRectangle(fill, panel);
                g.DrawRectangle(border, panel);
            }

            DrawResourceMetric(g, "CPU", FormatPercent(resourceSnapshot.CpuPercent),
                resourceSnapshot.CpuPercent, 20, Color.FromArgb(242, 126, 42));
            DrawResourceMetric(g, "\u5185\u5b58", FormatPercent(resourceSnapshot.MemoryPercent),
                resourceSnapshot.MemoryPercent, 118, Color.FromArgb(35, 119, 238));
            DrawResourceMetric(g, "\u78c1\u76d8", FormatPercent(resourceSnapshot.DiskPercent),
                resourceSnapshot.DiskPercent, 216, Color.FromArgb(16, 178, 92));
        }

        private void DrawResourceMetric(Graphics g, string label, string value, double percent,
            int x, Color color)
        {
            using (Brush muted = new SolidBrush(Color.FromArgb(90, 108, 128)))
            using (Brush dark = new SolidBrush(Color.FromArgb(32, 44, 62)))
            using (Brush track = new SolidBrush(Color.FromArgb(210, 224, 235)))
            using (Brush bar = new SolidBrush(color))
            {
                g.DrawString(label, smallFont, muted, x, 284);
                g.DrawString(value, valueFont, dark, x, 297);
                g.FillRectangle(track, x, 326, 88, 6);
                g.FillRectangle(bar, x, 326, (int)Math.Round(88 * Math.Min(100.0, Math.Max(0.0, percent)) / 100.0), 6);
            }
        }

        private static string FormatPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                value = 0;
            }

            return Math.Max(0.0, Math.Min(100.0, value)).ToString("0.#") + "%";
        }

        private void DrawGraph(Graphics g, Rectangle graph)
        {
            using (Brush fill = new SolidBrush(Color.FromArgb(225, 240, 250)))
            using (Pen grid = new Pen(Color.FromArgb(80, 150, 180, 205), 1.0f))
            {
                g.FillRectangle(fill, graph);
                for (int row = 1; row < 4; row++)
                {
                    int y = graph.Top + graph.Height * row / 4;
                    g.DrawLine(grid, graph.Left, y, graph.Right, y);
                }
            }

            if (sampleCount < 2)
            {
                return;
            }

            double maximum = 1;
            for (int i = 0; i < sampleCount; i++)
            {
                maximum = Math.Max(maximum, Math.Max(downloadHistory[i], uploadHistory[i]));
            }

            DrawHistoryLine(g, graph, downloadHistory, maximum, Color.FromArgb(35, 119, 238));
            DrawHistoryLine(g, graph, uploadHistory, maximum, Color.FromArgb(16, 178, 92));
        }

        private void DrawHistoryLine(Graphics g, Rectangle graph, double[] values, double maximum, Color color)
        {
            PointF[] points = new PointF[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                float x = graph.Left + (graph.Width - 1) * i / (float)(MaxSamples - 1);
                float ratio = (float)Math.Min(1.0, values[i] / maximum);
                float y = graph.Bottom - 2 - ratio * (graph.Height - 5);
                points[i] = new PointF(x, y);
            }

            using (Pen pen = new Pen(color, 2.0f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLines(pen, points);
            }
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(rect.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                valueFont.Dispose();
                smallFont.Dispose();
                resourceMonitor.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal struct SystemResourceSnapshot
    {
        public readonly double CpuPercent;
        public readonly double MemoryPercent;
        public readonly double DiskPercent;
        public readonly ulong MemoryUsedBytes;
        public readonly ulong MemoryTotalBytes;

        public SystemResourceSnapshot(double cpuPercent, double memoryPercent, double diskPercent,
            ulong memoryUsedBytes, ulong memoryTotalBytes)
        {
            CpuPercent = cpuPercent;
            MemoryPercent = memoryPercent;
            DiskPercent = diskPercent;
            MemoryUsedBytes = memoryUsedBytes;
            MemoryTotalBytes = memoryTotalBytes;
        }
    }

    internal sealed class SystemResourceMonitor : IDisposable
    {
        private PerformanceCounter diskActivityCounter;
        private bool hasCpuBaseline;
        private long lastIdle;
        private long lastKernel;
        private long lastUser;

        public SystemResourceMonitor()
        {
            try
            {
                diskActivityCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
                diskActivityCounter.NextValue();
            }
            catch
            {
                diskActivityCounter = null;
            }
        }

        public SystemResourceSnapshot Read()
        {
            double cpu = ReadCpuPercent();
            ulong totalMemory = 0;
            ulong availableMemory = 0;
            double memory = ReadMemoryPercent(ref totalMemory, ref availableMemory);
            double disk = ReadDiskPercent();
            ulong usedMemory = totalMemory >= availableMemory ? totalMemory - availableMemory : 0;
            return new SystemResourceSnapshot(cpu, memory, disk, usedMemory, totalMemory);
        }

        private double ReadCpuPercent()
        {
            NativeMethods.FileTime idle;
            NativeMethods.FileTime kernel;
            NativeMethods.FileTime user;
            if (!NativeMethods.GetSystemTimes(out idle, out kernel, out user))
            {
                return 0;
            }

            long idleValue = idle.ToInt64();
            long kernelValue = kernel.ToInt64();
            long userValue = user.ToInt64();
            if (!hasCpuBaseline)
            {
                hasCpuBaseline = true;
                lastIdle = idleValue;
                lastKernel = kernelValue;
                lastUser = userValue;
                return 0;
            }

            long idleDelta = Math.Max(0, idleValue - lastIdle);
            long totalDelta = Math.Max(0, kernelValue - lastKernel) + Math.Max(0, userValue - lastUser);
            lastIdle = idleValue;
            lastKernel = kernelValue;
            lastUser = userValue;
            if (totalDelta <= 0)
            {
                return 0;
            }

            return Math.Max(0.0, Math.Min(100.0, 100.0 * (1.0 - (double)idleDelta / totalDelta)));
        }

        private static double ReadMemoryPercent(ref ulong totalMemory, ref ulong availableMemory)
        {
            NativeMethods.MemoryStatusEx status = new NativeMethods.MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf(typeof(NativeMethods.MemoryStatusEx));
            if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.TotalPhysicalMemory == 0)
            {
                return 0;
            }

            totalMemory = status.TotalPhysicalMemory;
            availableMemory = status.AvailablePhysicalMemory;
            return Math.Max(0.0, Math.Min(100.0, status.MemoryLoad));
        }

        private double ReadDiskPercent()
        {
            if (diskActivityCounter != null)
            {
                try
                {
                    float activity = diskActivityCounter.NextValue();
                    if (!float.IsNaN(activity) && !float.IsInfinity(activity))
                    {
                        return Math.Max(0.0, Math.Min(100.0, activity));
                    }
                }
                catch
                {
                    diskActivityCounter.Dispose();
                    diskActivityCounter = null;
                }
            }

            // This fallback is capacity usage, used only when the disk activity counter
            // is unavailable on a stripped-down Windows performance-counter installation.
            long total = 0;
            long free = 0;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    {
                        continue;
                    }

                    total += drive.TotalSize;
                    free += drive.AvailableFreeSpace;
                }
                catch
                {
                }
            }

            if (total <= 0)
            {
                return 0;
            }

            return Math.Max(0.0, Math.Min(100.0, 100.0 * (total - free) / total));
        }

        public void Dispose()
        {
            if (diskActivityCounter != null)
            {
                diskActivityCounter.Dispose();
                diskActivityCounter = null;
            }
        }
    }

    internal sealed class TrafficWindow : Form
    {
        private const int RankRows = 12;
        private readonly TrafficStore store;
        private readonly Font sectionFont = new Font("Segoe UI", 9.0f, FontStyle.Bold);
        private readonly Font valueFont = new Font("Segoe UI", 10.0f, FontStyle.Bold);
        private readonly Font smallFont = new Font("Segoe UI", 8.0f, FontStyle.Regular);
        private readonly Font tinyFont = new Font("Segoe UI", 7.4f, FontStyle.Regular);
        private readonly DateTimePicker fromPicker;
        private readonly DateTimePicker toPicker;
        private readonly ToolTip toolTip = new ToolTip();
        private readonly FormsTimer refreshTimer;
        private long rangeFrom;
        private long rangeTo;
        private long[] rangeTotals = new long[] { 0, 0 };
        private List<DayPoint> rangeDays = new List<DayPoint>();
        private List<AppRankRow> rangeRanks = new List<AppRankRow>();
        private Rectangle chartPlot;
        private int chartSlot;
        private int hoverDayIndex = -1;

        public TrafficWindow(TrafficStore trafficStore)
        {
            store = trafficStore;
            Text = "流量统计";
            ClientSize = new Size(700, 700);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            DoubleBuffered = true;

            AddPresetButton("今天", "today", 16);
            AddPresetButton("昨天", "yesterday", 62);
            AddPresetButton("本月", "month", 108);
            AddPresetButton("上月", "lastmonth", 154);
            AddPresetButton("今年", "year", 200);

            Label fromLabel = new Label();
            fromLabel.Text = "从";
            fromLabel.AutoSize = true;
            fromLabel.Location = new Point(254, 16);
            fromLabel.Font = smallFont;
            Controls.Add(fromLabel);

            fromPicker = new DateTimePicker();
            fromPicker.Format = DateTimePickerFormat.Short;
            fromPicker.Font = smallFont;
            fromPicker.Location = new Point(272, 12);
            fromPicker.Width = 96;
            Controls.Add(fromPicker);

            Label toLabel = new Label();
            toLabel.Text = "到";
            toLabel.AutoSize = true;
            toLabel.Location = new Point(378, 16);
            toLabel.Font = smallFont;
            Controls.Add(toLabel);

            toPicker = new DateTimePicker();
            toPicker.Format = DateTimePickerFormat.Short;
            toPicker.Font = smallFont;
            toPicker.Location = new Point(396, 12);
            toPicker.Width = 96;
            Controls.Add(toPicker);

            Button applyButton = new Button();
            applyButton.Text = "查询";
            applyButton.Font = smallFont;
            applyButton.Location = new Point(500, 11);
            applyButton.Size = new Size(56, 25);
            applyButton.UseVisualStyleBackColor = true;
            applyButton.Click += OnApplyClick;
            Controls.Add(applyButton);

            DateTime today = DateTime.Today;
            fromPicker.Value = new DateTime(today.Year, today.Month, 1);
            toPicker.Value = today;
            rangeFrom = TrafficStore.ToDateInt(fromPicker.Value.Date);
            rangeTo = TrafficStore.ToDateInt(toPicker.Value.Date);
            RefreshData();

            refreshTimer = new FormsTimer();
            refreshTimer.Interval = 2000;
            refreshTimer.Tick += OnRefreshTick;
            refreshTimer.Start();
        }

        private void AddPresetButton(string text, string tag, int x)
        {
            Button button = new Button();
            button.Text = text;
            button.Tag = tag;
            button.Font = smallFont;
            button.Location = new Point(x, 11);
            button.Size = new Size(42, 25);
            button.UseVisualStyleBackColor = true;
            button.Click += OnPresetClick;
            Controls.Add(button);
        }

        private void OnPresetClick(object sender, EventArgs e)
        {
            Button button = sender as Button;
            string tag = button != null ? button.Tag as string : null;
            DateTime today = DateTime.Today;
            if (tag == "today")
            {
                SetRange(today, today);
            }
            else if (tag == "yesterday")
            {
                DateTime day = today.AddDays(-1);
                SetRange(day, day);
            }
            else if (tag == "month")
            {
                SetRange(new DateTime(today.Year, today.Month, 1), today);
            }
            else if (tag == "lastmonth")
            {
                DateTime first = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                SetRange(first, first.AddMonths(1).AddDays(-1));
            }
            else if (tag == "year")
            {
                SetRange(new DateTime(today.Year, 1, 1), today);
            }
        }

        private void OnApplyClick(object sender, EventArgs e)
        {
            DateTime from = fromPicker.Value.Date;
            DateTime to = toPicker.Value.Date;
            if (to < from)
            {
                DateTime swap = from;
                from = to;
                to = swap;
            }

            SetRange(from, to);
        }

        private void SetRange(DateTime from, DateTime to)
        {
            fromPicker.Value = from;
            toPicker.Value = to;
            rangeFrom = TrafficStore.ToDateInt(from);
            rangeTo = TrafficStore.ToDateInt(to);
            hoverDayIndex = -1;
            RefreshData();
            Invalidate();
        }

        private void OnRefreshTick(object sender, EventArgs e)
        {
            if (!Visible || Disposing || IsDisposed)
            {
                return;
            }

            if (rangeTo >= TrafficStore.ToDateInt(DateTime.Today))
            {
                RefreshData();
                Invalidate();
            }
        }

        private void RefreshData()
        {
            rangeTotals = store.GetRangeTotals(rangeFrom, rangeTo);
            rangeDays = store.GetRangeDays(rangeFrom, rangeTo);
            rangeRanks = store.GetRangeRanks(rangeFrom, rangeTo, RankRows);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = -1;
            if (rangeDays.Count > 0 && chartPlot.Width > 0 && chartSlot > 0 &&
                e.X >= chartPlot.Left && e.X <= chartPlot.Right &&
                e.Y >= chartPlot.Top - 24 && e.Y <= chartPlot.Bottom)
            {
                index = (e.X - chartPlot.Left) / chartSlot;
                if (index < 0 || index >= rangeDays.Count)
                {
                    index = -1;
                }
            }

            if (index == hoverDayIndex)
            {
                return;
            }

            hoverDayIndex = index;
            if (index >= 0)
            {
                DayPoint point = rangeDays[index];
                DateTime day = TrafficStore.FromDateInt(point.Date);
                toolTip.SetToolTip(this, day.ToString("M月d日 ddd") + "\r\n下载 " +
                    SpeedFormatter.Format(point.Rx) + "\r\n上传 " + SpeedFormatter.Format(point.Tx));
            }
            else
            {
                toolTip.SetToolTip(this, string.Empty);
            }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            DateTime from = TrafficStore.FromDateInt(rangeFrom);
            DateTime to = TrafficStore.FromDateInt(rangeTo);
            string scopeText = rangeFrom == rangeTo
                ? from.ToString("yyyy年M月d日")
                : from.ToString("yyyy年M月d日") + "  -  " + to.ToString("yyyy年M月d日");
            int dayCount = Math.Max(1, (int)TrafficStore.DaysBetween(rangeFrom, rangeTo) + 1);

            using (Brush muted = new SolidBrush(Color.FromArgb(96, 110, 130)))
            {
                // The window caption already shows the title; painting another one here
                // used to bleed through the 4px gaps between the preset buttons.
                g.DrawString(scopeText + "  ·  共 " + dayCount + " 天", smallFont, muted, 16, 40);
            }

            DrawSummaryCards(g, dayCount);
            DrawDailyChart(g);
            DrawRankList(g);

            using (Brush note = new SolidBrush(Color.FromArgb(128, 118, 132, 150)))
            {
                g.DrawString("排行榜按 TCP 连接统计，不含 UDP/QUIC 流量与协议头开销，故各项之和略小于总流量。" +
                    "数据保存在本机 %LOCALAPPDATA%\\NetSpeedBall，年占用不足 1 MB。",
                    tinyFont, note, 16, ClientSize.Height - 20);
            }
        }

        private void DrawSummaryCards(Graphics g, int dayCount)
        {
            long down = rangeTotals[0];
            long up = rangeTotals[1];
            long total = down + up;
            long perDay = total / dayCount;
            int width = (ClientSize.Width - 32 - 30) / 4;
            string[] labels = { "下载", "上传", "合计", "日均" };
            string[] values =
            {
                SpeedFormatter.Format(down),
                SpeedFormatter.Format(up),
                SpeedFormatter.Format(total),
                SpeedFormatter.Format(perDay)
            };
            Color[] colors =
            {
                Color.FromArgb(35, 119, 238),
                Color.FromArgb(16, 178, 92),
                Color.FromArgb(58, 70, 92),
                Color.FromArgb(242, 126, 42)
            };

            for (int i = 0; i < 4; i++)
            {
                Rectangle card = new Rectangle(16 + i * (width + 10), 64, width, 52);
                using (GraphicsPath path = CreateRoundedRectangle(card, 10))
                using (Brush fill = new SolidBrush(Color.FromArgb(244, 249, 254)))
                using (Pen border = new Pen(Color.FromArgb(200, 205, 225, 240)))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);
                }

                using (Brush label = new SolidBrush(Color.FromArgb(110, 122, 142)))
                using (Brush value = new SolidBrush(colors[i]))
                {
                    g.DrawString(labels[i], smallFont, label, card.X + 12, card.Y + 7);
                    g.DrawString(values[i], valueFont, value, card.X + 12, card.Y + 24);
                }
            }
        }

        private void DrawDailyChart(Graphics g)
        {
            Rectangle panel = new Rectangle(16, 128, ClientSize.Width - 32, 236);
            using (GraphicsPath path = CreateRoundedRectangle(panel, 12))
            using (Brush fill = new SolidBrush(Color.FromArgb(247, 251, 255)))
            using (Pen border = new Pen(Color.FromArgb(205, 208, 226, 242)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            using (Brush heading = new SolidBrush(Color.FromArgb(70, 84, 104)))
            {
                g.DrawString("每日流量", sectionFont, heading, panel.X + 12, panel.Y + 8);
            }

            chartPlot = new Rectangle(panel.Left + 14, panel.Top + 34,
                panel.Width - 28, panel.Height - 66);
            chartSlot = rangeDays.Count > 0 ? Math.Max(1, chartPlot.Width / rangeDays.Count) : 0;

            double maximum = 1.0;
            for (int i = 0; i < rangeDays.Count; i++)
            {
                maximum = Math.Max(maximum, (double)(rangeDays[i].Rx + rangeDays[i].Tx));
            }

            using (Pen grid = new Pen(Color.FromArgb(70, 178, 200, 220)))
            using (Brush axisText = new SolidBrush(Color.FromArgb(130, 120, 136, 154)))
            {
                for (int row = 0; row <= 4; row++)
                {
                    int y = chartPlot.Bottom - chartPlot.Height * row / 4;
                    g.DrawLine(grid, chartPlot.Left, y, chartPlot.Right, y);
                    if (row == 4)
                    {
                        string label = SpeedFormatter.Format(maximum);
                        g.DrawString(label, tinyFont, axisText, chartPlot.Left + 2, chartPlot.Top - 16);
                    }
                }
            }

            if (rangeDays.Count == 0)
            {
                return;
            }

            int slot = chartSlot;
            int barWidth = Math.Max(1, (int)(slot * 0.34));
            for (int i = 0; i < rangeDays.Count; i++)
            {
                DayPoint point = rangeDays[i];
                int x = chartPlot.Left + i * slot + (slot - barWidth) / 2;
                if (i == hoverDayIndex)
                {
                    using (Brush hover = new SolidBrush(Color.FromArgb(40, 255, 214, 120)))
                    {
                        g.FillRectangle(hover, chartPlot.Left + i * slot, chartPlot.Top, slot, chartPlot.Height);
                    }
                }

                double downRatio = Math.Min(1.0, point.Rx / maximum);
                double upRatio = Math.Min(1.0, point.Tx / maximum);
                int downHeight = (int)Math.Round(downRatio * (chartPlot.Height - 2));
                int upHeight = (int)Math.Round(upRatio * (chartPlot.Height - 2));
                if (downHeight > 0)
                {
                    using (Brush down = new SolidBrush(Color.FromArgb(200, 62, 138, 245)))
                    {
                        g.FillRectangle(down, x, chartPlot.Bottom - downHeight, barWidth, downHeight);
                    }
                }

                if (upHeight > 0)
                {
                    using (Brush up = new SolidBrush(Color.FromArgb(210, 34, 197, 110)))
                    {
                        g.FillRectangle(up, x + barWidth, chartPlot.Bottom - upHeight, barWidth, upHeight);
                    }
                }
            }

            DrawDateAxis(g, panel);
        }

        private void DrawDateAxis(Graphics g, Rectangle panel)
        {
            using (Brush axisText = new SolidBrush(Color.FromArgb(140, 110, 124, 144)))
            {
                int count = rangeDays.Count;
                if (count == 0)
                {
                    return;
                }

                int step = count <= 16 ? 1 : (count <= 62 ? 7 : 30);
                int y = panel.Bottom - 16;
                for (int i = 0; i < count; i += step)
                {
                    if (i > 0 && chartPlot.Left + i * chartSlot + 30 > chartPlot.Right)
                    {
                        break;
                    }

                    DateTime day = TrafficStore.FromDateInt(rangeDays[i].Date);
                    string label;
                    if (count > 62)
                    {
                        if (day.Day != 1)
                        {
                            continue;
                        }

                        label = day.Month + "月";
                    }
                    else if (count > 16)
                    {
                        label = day.Day + "日";
                    }
                    else
                    {
                        label = day.Month + "/" + day.Day;
                    }

                    g.DrawString(label, tinyFont, axisText, chartPlot.Left + i * chartSlot, y);
                }
            }
        }

        private void DrawRankList(Graphics g)
        {
            using (Brush heading = new SolidBrush(Color.FromArgb(70, 84, 104)))
            {
                g.DrawString("应用排行（Top " + RankRows + "）", sectionFont, heading, 16, 378);
            }

            int rowHeight = 22;
            int y = 404;
            int nameWidth = 168;
            int barLeft = 196;
            int barRight = ClientSize.Width - 250;
            int valueLeft = ClientSize.Width - 240;

            double maxTotal = 1.0;
            for (int i = 0; i < rangeRanks.Count; i++)
            {
                maxTotal = Math.Max(maxTotal, (double)rangeRanks[i].Total);
            }

            using (Brush evenRow = new SolidBrush(Color.FromArgb(248, 251, 254)))
            using (Brush track = new SolidBrush(Color.FromArgb(235, 228, 238, 246)))
            using (Brush nameText = new SolidBrush(Color.FromArgb(52, 64, 84)))
            using (Brush valueText = new SolidBrush(Color.FromArgb(90, 104, 124)))
            using (Brush downBar = new SolidBrush(Color.FromArgb(214, 62, 138, 245)))
            using (Brush upBar = new SolidBrush(Color.FromArgb(214, 34, 197, 110)))
            {
                for (int i = 0; i < rangeRanks.Count; i++)
                {
                    AppRankRow row = rangeRanks[i];
                    if (i % 2 == 0)
                    {
                        g.FillRectangle(evenRow, 12, y, ClientSize.Width - 24, rowHeight);
                    }

                    string name = TruncateName(row.Name, nameWidth - 12, g, smallFont);
                    g.DrawString((i + 1) + ". " + name, smallFont, nameText, 16, y + 4);

                    int barWidth = barRight - barLeft;
                    int barY = y + (rowHeight - 10) / 2;
                    g.FillRectangle(track, barLeft, barY, barWidth, 10);
                    long total = row.Total;
                    if (total > 0)
                    {
                        int filled = (int)Math.Round(barWidth * total / maxTotal);
                        int downWidth = (int)Math.Round(filled * (double)row.Rx / total);
                        if (downWidth > 0)
                        {
                            g.FillRectangle(downBar, barLeft, barY, downWidth, 10);
                        }

                        if (filled - downWidth > 0)
                        {
                            g.FillRectangle(upBar, barLeft + downWidth, barY, filled - downWidth, 10);
                        }
                    }

                    string value = "\u2193 " + SpeedFormatter.Format(row.Rx) +
                                   "   \u2191 " + SpeedFormatter.Format(row.Tx);
                    SizeF size = g.MeasureString(value, smallFont);
                    g.DrawString(value, smallFont, valueText, valueLeft + (236 - size.Width) / 2, y + 4);

                    y += rowHeight;
                }
            }

            if (rangeRanks.Count == 0)
            {
                using (Brush empty = new SolidBrush(Color.FromArgb(150, 120, 134, 154)))
                {
                    // 空列表有两种原因，必须说清楚，否则用户只会以为功能坏了。
                    if (AppTrafficMonitor.CanCollectAppTraffic)
                    {
                        g.DrawString("所选范围内暂无应用流量记录", smallFont, empty, 16, y + 6);
                    }
                    else
                    {
                        g.DrawString("应用流量排行需要管理员权限", smallFont, empty, 16, y + 6);
                        g.DrawString("右键托盘图标 → 以管理员身份重启", smallFont, empty, 16, y + 24);
                    }
                }
            }
        }

        private static string TruncateName(string name, int maxWidth, Graphics g, Font font)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            if (g.MeasureString(name, font).Width <= maxWidth)
            {
                return name;
            }

            string trimmed = name;
            while (trimmed.Length > 1 && g.MeasureString(trimmed + "…", font).Width > maxWidth)
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            return trimmed + "…";
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(rect.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (refreshTimer != null) refreshTimer.Dispose();
                if (toolTip != null) toolTip.Dispose();
                if (sectionFont != null) sectionFont.Dispose();
                if (valueFont != null) valueFont.Dispose();
                if (smallFont != null) smallFont.Dispose();
                if (tinyFont != null) tinyFont.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class LaunchPadForm : Form
    {
        private bool active;

        public LaunchPadForm()
        {
            Text = "LaunchPad";
            Size = new Size(258, 148);
            MinimumSize = Size;
            MaximumSize = Size;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(248, 252, 255);
            Opacity = 0.9;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateWindowRegion();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW |
                              NativeMethods.WS_EX_NOACTIVATE |
                              NativeMethods.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public void SetActive(bool value)
        {
            if (active == value)
            {
                return;
            }

            active = value;
            Invalidate();
        }

        public bool AcceptsBall(Point screenCenter)
        {
            Rectangle bounds = Bounds;
            double dx = (screenCenter.X - (bounds.Left + bounds.Width / 2.0)) / (bounds.Width * 0.42);
            double dy = (screenCenter.Y - (bounds.Top + bounds.Height / 2.0)) / (bounds.Height * 0.42);
            return dx * dx + dy * dy <= 1.0;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateWindowRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle rect = new Rectangle(2, 2, Width - 5, Height - 5);
            using (GraphicsPath shell = CreateRoundedRectangle(rect, 30))
            using (LinearGradientBrush background = new LinearGradientBrush(rect,
                       active ? Color.FromArgb(255, 226, 246, 255) : Color.FromArgb(255, 244, 250, 255),
                       active ? Color.FromArgb(255, 185, 224, 252) : Color.FromArgb(255, 226, 239, 250),
                       LinearGradientMode.Vertical))
            using (Pen edge = new Pen(active ? Color.FromArgb(235, 32, 139, 232) : Color.FromArgb(155, 111, 183, 224), 1.4f))
            {
                g.FillPath(background, shell);
                g.DrawPath(edge, shell);
            }

            PointF center = new PointF(Width / 2.0f, Height / 2.0f - 2.0f);
            DrawEnergyRings(g, center);
            DrawPadBase(g);
        }

        private void DrawPadBase(Graphics g)
        {
            using (Brush baseBrush = new SolidBrush(active ? Color.FromArgb(220, 33, 121, 206) : Color.FromArgb(150, 100, 163, 205)))
            using (Brush highlight = new SolidBrush(Color.FromArgb(active ? 210 : 130, 255, 255, 255)))
            using (Pen baseEdge = new Pen(Color.FromArgb(active ? 220 : 130, 54, 122, 190), 1.2f))
            {
                g.FillEllipse(baseBrush, 58, 91, 142, 28);
                g.DrawEllipse(baseEdge, 58, 91, 142, 28);
                g.FillEllipse(highlight, 76, 96, 106, 7);
            }

            using (Pen beam = new Pen(Color.FromArgb(active ? 190 : 85, 77, 177, 239), 2.0f))
            {
                beam.StartCap = LineCap.Round;
                beam.EndCap = LineCap.Round;
                g.DrawLine(beam, 83, 75, 175, 75);
                g.DrawLine(beam, 94, 82, 164, 82);
            }
        }

        private void DrawEnergyRings(Graphics g, PointF center)
        {
            RectangleF outer = new RectangleF(center.X - 78, center.Y - 45, 156, 90);
            RectangleF inner = new RectangleF(center.X - 48, center.Y - 28, 96, 56);
            using (Pen glow = new Pen(active ? Color.FromArgb(130, 26, 150, 239) : Color.FromArgb(82, 84, 177, 231), active ? 7.0f : 5.0f))
            using (Pen ring = new Pen(active ? Color.FromArgb(235, 28, 143, 234) : Color.FromArgb(185, 76, 168, 230), 2.4f))
            using (Pen small = new Pen(Color.FromArgb(active ? 210 : 140, 110, 202, 238), 1.4f))
            using (Brush core = new SolidBrush(active ? Color.FromArgb(58, 25, 143, 234) : Color.FromArgb(36, 70, 169, 230)))
            {
                glow.StartCap = LineCap.Round;
                glow.EndCap = LineCap.Round;
                ring.StartCap = LineCap.Round;
                ring.EndCap = LineCap.Round;
                small.StartCap = LineCap.Round;
                small.EndCap = LineCap.Round;
                g.DrawArc(glow, outer, 200, 320);
                g.DrawArc(ring, outer, 205, 312);
                g.DrawEllipse(small, inner);
                g.FillEllipse(core, center.X - 24, center.Y - 14, 48, 28);
            }
        }

        private void DrawPadClouds(Graphics g)
        {
            int y = Height - 35;
            using (Brush cloud = new SolidBrush(Color.FromArgb(238, 250, 255)))
            using (Brush shade = new SolidBrush(Color.FromArgb(210, 229, 242, 251)))
            {
                g.FillEllipse(cloud, 43, y + 8, 52, 28);
                g.FillEllipse(cloud, 82, y, 56, 36);
                g.FillEllipse(cloud, 127, y + 7, 62, 30);
                g.FillRectangle(cloud, 48, y + 20, 142, 30);
                g.FillEllipse(shade, 131, y + 20, 58, 18);
            }
        }

        private void UpdateWindowRegion()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            using (GraphicsPath path = CreateRoundedRectangle(new Rectangle(0, 0, Width, Height), 46))
            {
                Region oldRegion = Region;
                Region = new Region(path);
                if (oldRegion != null)
                {
                    oldRegion.Dispose();
                }
            }
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(rect.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class DayAppRow
    {
        public long Rx;
        public long Tx;
    }

    internal sealed class DayEntry
    {
        public long NicRx;
        public long NicTx;
        public readonly Dictionary<string, DayAppRow> Apps = new Dictionary<string, DayAppRow>();
    }

    internal struct DayPoint
    {
        public readonly long Date;
        public readonly long Rx;
        public readonly long Tx;

        public DayPoint(long date, long rx, long tx)
        {
            Date = date;
            Rx = rx;
            Tx = tx;
        }
    }

    internal sealed class AppRankRow
    {
        public readonly string Name;
        public readonly long Rx;
        public readonly long Tx;

        public AppRankRow(string name, long rx, long tx)
        {
            Name = name;
            Rx = rx;
            Tx = tx;
        }

        public long Total
        {
            get { return Rx + Tx; }
        }
    }

    // Keeps every per-day traffic counter in memory and mirrors it to two tiny
    // append-only files under %LOCALAPPDATA%\NetSpeedBall:
    //   traffic-archive.bin  - compacted per-day records, written once a day on rollover
    //   traffic-pending.bin  - recent deltas, rewritten (compacted) on every rollover/exit
    // A full year of heavy use stays well under 1 MB on disk.
    internal sealed class TrafficStore
    {
        private const int ArchiveMagic = 0x4142534E; // "NSBA"
        private const int PendingMagic = 0x5042534E; // "NSBP"
        private const short FormatVersion = 1;
        private const byte RecordKindNic = 0;
        private const byte RecordKindApp = 1;
        private const int MaxNameBytes = 512;

        private readonly object gate = new object();
        private readonly string archivePath;
        private readonly string pendingPath;
        private readonly Dictionary<long, DayEntry> days = new Dictionary<long, DayEntry>();
        // Flushed net totals: "date\x01app" -> {rx, tx}. Empty app name = NIC-level total.
        private readonly Dictionary<string, long[]> flushed = new Dictionary<string, long[]>();
        private long currentDate;

        public TrafficStore()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetSpeedBall");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
            }

            archivePath = Path.Combine(dir, "traffic-archive.bin");
            pendingPath = Path.Combine(dir, "traffic-pending.bin");
            Load();
        }

        public static long ToDateInt(DateTime day)
        {
            return day.Year * 10000L + day.Month * 100L + day.Day;
        }

        public static DateTime FromDateInt(long value)
        {
            return new DateTime((int)(value / 10000L), (int)(value / 100L % 100L), (int)(value % 100L));
        }

        public static long DaysBetween(long fromInt, long toInt)
        {
            return (long)(FromDateInt(toInt).Date - FromDateInt(fromInt).Date).TotalDays;
        }

        public void RecordNic(DateTime day, long rx, long tx)
        {
            if (rx <= 0 && tx <= 0)
            {
                return;
            }

            lock (gate)
            {
                DayEntry entry = GetOrCreateLocked(ToDateInt(day));
                entry.NicRx += Math.Max(0, rx);
                entry.NicTx += Math.Max(0, tx);
            }
        }

        public void RecordApp(DateTime day, string app, long rx, long tx)
        {
            if (string.IsNullOrEmpty(app) || (rx <= 0 && tx <= 0))
            {
                return;
            }

            lock (gate)
            {
                DayEntry entry = GetOrCreateLocked(ToDateInt(day));
                DayAppRow row;
                if (!entry.Apps.TryGetValue(app, out row))
                {
                    row = new DayAppRow();
                    entry.Apps.Add(app, row);
                }

                row.Rx += Math.Max(0, rx);
                row.Tx += Math.Max(0, tx);
            }
        }

        // Must be called once per sampling tick; archives yesterday's data the first
        // time a new calendar day is seen (00:00 rollover).
        public void CheckDayRoll()
        {
            long today = ToDateInt(DateTime.Today);
            lock (gate)
            {
                if (today == currentDate)
                {
                    return;
                }

                FlushLocked();
                ArchivePendingLocked();
                currentDate = today;
            }
        }

        public void Flush()
        {
            lock (gate)
            {
                FlushLocked();
            }
        }

        public void Close()
        {
            lock (gate)
            {
                FlushLocked();
                ArchivePendingLocked();
            }
        }

        public void ResetToday()
        {
            lock (gate)
            {
                long today = ToDateInt(DateTime.Today);
                days.Remove(today);
                RewritePendingWithoutDayLocked(today);
                flushed.Remove(KeyOf(today, string.Empty));
                List<string> stale = new List<string>();
                foreach (KeyValuePair<string, long[]> pair in flushed)
                {
                    if (ExtractDate(pair.Key) == today)
                    {
                        stale.Add(pair.Key);
                    }
                }

                for (int i = 0; i < stale.Count; i++)
                {
                    flushed.Remove(stale[i]);
                }
            }
        }

        public long[] GetDayTotals(long dateInt)
        {
            lock (gate)
            {
                DayEntry entry;
                if (!days.TryGetValue(dateInt, out entry))
                {
                    return new long[] { 0, 0 };
                }

                return new long[] { entry.NicRx, entry.NicTx };
            }
        }

        public long[] GetRangeTotals(long fromInt, long toInt)
        {
            lock (gate)
            {
                long rx = 0;
                long tx = 0;
                foreach (KeyValuePair<long, DayEntry> pair in days)
                {
                    if (pair.Key >= fromInt && pair.Key <= toInt)
                    {
                        rx += pair.Value.NicRx;
                        tx += pair.Value.NicTx;
                    }
                }

                return new long[] { rx, tx };
            }
        }

        public List<DayPoint> GetRangeDays(long fromInt, long toInt)
        {
            List<DayPoint> result = new List<DayPoint>();
            if (toInt < fromInt)
            {
                return result;
            }

            int count = (int)DaysBetween(fromInt, toInt) + 1;
            if (count > 4000)
            {
                count = 4000;
                toInt = ToDateInt(FromDateInt(fromInt).AddDays(count - 1));
            }

            lock (gate)
            {
                for (int i = 0; i < count; i++)
                {
                    long dateInt = ToDateInt(FromDateInt(fromInt).AddDays(i));
                    DayEntry entry;
                    long rx = 0;
                    long tx = 0;
                    if (days.TryGetValue(dateInt, out entry))
                    {
                        rx = entry.NicRx;
                        tx = entry.NicTx;
                    }

                    result.Add(new DayPoint(dateInt, rx, tx));
                }
            }

            return result;
        }

        public List<AppRankRow> GetRangeRanks(long fromInt, long toInt, int maxRows)
        {
            Dictionary<string, long[]> totals = new Dictionary<string, long[]>();
            lock (gate)
            {
                foreach (KeyValuePair<long, DayEntry> pair in days)
                {
                    if (pair.Key < fromInt || pair.Key > toInt)
                    {
                        continue;
                    }

                    foreach (KeyValuePair<string, DayAppRow> app in pair.Value.Apps)
                    {
                        long[] agg;
                        if (!totals.TryGetValue(app.Key, out agg))
                        {
                            agg = new long[2];
                            totals.Add(app.Key, agg);
                        }

                        agg[0] += app.Value.Rx;
                        agg[1] += app.Value.Tx;
                    }
                }
            }

            List<AppRankRow> rows = new List<AppRankRow>(totals.Count);
            foreach (KeyValuePair<string, long[]> pair in totals)
            {
                rows.Add(new AppRankRow(pair.Key, pair.Value[0], pair.Value[1]));
            }

            rows.Sort(delegate(AppRankRow a, AppRankRow b) { return b.Total.CompareTo(a.Total); });
            if (rows.Count > maxRows)
            {
                rows.RemoveRange(maxRows, rows.Count - maxRows);
            }

            return rows;
        }

        private DayEntry GetOrCreateLocked(long dateInt)
        {
            DayEntry entry;
            if (!days.TryGetValue(dateInt, out entry))
            {
                entry = new DayEntry();
                days.Add(dateInt, entry);
            }

            return entry;
        }

        private static string KeyOf(long dateInt, string app)
        {
            return dateInt.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\u0001" + app;
        }

        private static long ExtractDate(string key)
        {
            int sep = key.IndexOf('\u0001');
            long value;
            if (sep > 0 && long.TryParse(key.Substring(0, sep),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return 0;
        }

        private long GetFlushedLocked(long dateInt, string app, int index)
        {
            long[] values;
            if (flushed.TryGetValue(KeyOf(dateInt, app), out values) && values.Length == 2)
            {
                return values[index];
            }

            return 0;
        }

        private void SetFlushedLocked(long dateInt, string app, long rx, long tx)
        {
            flushed[KeyOf(dateInt, app)] = new long[] { rx, tx };
        }

        private struct PendingRecord
        {
            public byte Kind;
            public long Date;
            public string Name;
            public long Rx;
            public long Tx;
        }

        private void FlushLocked()
        {
            List<PendingRecord> diffs = new List<PendingRecord>();
            foreach (KeyValuePair<long, DayEntry> pair in days)
            {
                long dateInt = pair.Key;
                DayEntry entry = pair.Value;
                long nicRx = GetFlushedLocked(dateInt, string.Empty, 0);
                long nicTx = GetFlushedLocked(dateInt, string.Empty, 1);
                if (entry.NicRx > nicRx || entry.NicTx > nicTx)
                {
                    PendingRecord record = new PendingRecord();
                    record.Kind = RecordKindNic;
                    record.Date = dateInt;
                    record.Name = string.Empty;
                    record.Rx = Math.Max(0, entry.NicRx - nicRx);
                    record.Tx = Math.Max(0, entry.NicTx - nicTx);
                    diffs.Add(record);
                }

                foreach (KeyValuePair<string, DayAppRow> app in entry.Apps)
                {
                    long appRx = GetFlushedLocked(dateInt, app.Key, 0);
                    long appTx = GetFlushedLocked(dateInt, app.Key, 1);
                    if (app.Value.Rx > appRx || app.Value.Tx > appTx)
                    {
                        PendingRecord record = new PendingRecord();
                        record.Kind = RecordKindApp;
                        record.Date = dateInt;
                        record.Name = app.Key;
                        record.Rx = Math.Max(0, app.Value.Rx - appRx);
                        record.Tx = Math.Max(0, app.Value.Tx - appTx);
                        diffs.Add(record);
                    }
                }
            }

            if (diffs.Count == 0)
            {
                return;
            }

            try
            {
                using (FileStream stream = new FileStream(pendingPath, FileMode.Append,
                    FileAccess.Write, FileShare.Read))
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    if (stream.Length == 0)
                    {
                        writer.Write(PendingMagic);
                        writer.Write(FormatVersion);
                    }

                    for (int i = 0; i < diffs.Count; i++)
                    {
                        WriteRecord(writer, diffs[i]);
                    }

                    writer.Flush();
                }
            }
            catch
            {
                // Disk issues must never take the floating ball down.
                return;
            }

            for (int i = 0; i < diffs.Count; i++)
            {
                PendingRecord record = diffs[i];
                DayEntry entry;
                long rx = 0;
                long tx = 0;
                if (record.Kind == RecordKindNic)
                {
                    if (days.TryGetValue(record.Date, out entry))
                    {
                        rx = entry.NicRx;
                        tx = entry.NicTx;
                    }
                }
                else
                {
                    if (days.TryGetValue(record.Date, out entry))
                    {
                        DayAppRow row;
                        if (entry.Apps.TryGetValue(record.Name, out row))
                        {
                            rx = row.Rx;
                            tx = row.Tx;
                        }
                    }
                }

                SetFlushedLocked(record.Date, record.Name, rx, tx);
            }
        }

        // Folds the pending log into the archive file, then compacts the pending log
        // back to an empty header. Keeps both files small forever.
        private void ArchivePendingLocked()
        {
            Dictionary<string, PendingRecord> sums = new Dictionary<string, PendingRecord>();
            ReadPendingInto(sums);
            try
            {
                if (sums.Count > 0)
                {
                    using (FileStream stream = new FileStream(archivePath, FileMode.Append,
                        FileAccess.Write, FileShare.Read))
                    using (BinaryWriter writer = new BinaryWriter(stream))
                    {
                        if (stream.Length == 0)
                        {
                            writer.Write(ArchiveMagic);
                            writer.Write(FormatVersion);
                        }

                        foreach (KeyValuePair<string, PendingRecord> pair in sums)
                        {
                            WriteRecord(writer, pair.Value);
                        }

                        writer.Flush();
                    }
                }

                using (FileStream stream = new FileStream(pendingPath, FileMode.Create,
                    FileAccess.Write, FileShare.Read))
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(PendingMagic);
                    writer.Write(FormatVersion);
                    writer.Flush();
                }
            }
            catch
            {
                return;
            }

            RebuildFlushedLocked();
        }

        private void ReadPendingInto(Dictionary<string, PendingRecord> sums)
        {
            if (!File.Exists(pendingPath))
            {
                return;
            }

            try
            {
                using (FileStream stream = new FileStream(pendingPath, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (stream.Length < 6)
                    {
                        return;
                    }

                    if (reader.ReadInt32() != PendingMagic || reader.ReadInt16() != FormatVersion)
                    {
                        return;
                    }

                    while (stream.Position + 15 <= stream.Length)
                    {
                        PendingRecord record;
                        if (!TryReadRecord(reader, out record))
                        {
                            return;
                        }

                        string key = record.Kind.ToString(
                            System.Globalization.CultureInfo.InvariantCulture) + "\u0001" +
                            record.Date.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            "\u0001" + record.Name;
                        PendingRecord sum;
                        if (sums.TryGetValue(key, out sum))
                        {
                            sum.Rx += record.Rx;
                            sum.Tx += record.Tx;
                            sums[key] = sum;
                        }
                        else
                        {
                            sums.Add(key, record);
                        }
                    }
                }
            }
            catch
            {
                // A partially written tail is ignored; the loader reads complete records only.
            }
        }

        private void RewritePendingWithoutDayLocked(long removedDay)
        {
            List<PendingRecord> keep = new List<PendingRecord>();
            try
            {
                if (File.Exists(pendingPath))
                {
                    using (FileStream stream = new FileStream(pendingPath, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite))
                    using (BinaryReader reader = new BinaryReader(stream))
                    {
                        if (stream.Length >= 6 && reader.ReadInt32() == PendingMagic &&
                            reader.ReadInt16() == FormatVersion)
                        {
                            while (stream.Position + 15 <= stream.Length)
                            {
                                PendingRecord record;
                                if (!TryReadRecord(reader, out record))
                                {
                                    break;
                                }

                                if (record.Date != removedDay)
                                {
                                    keep.Add(record);
                                }
                            }
                        }
                    }
                }

                using (FileStream stream = new FileStream(pendingPath, FileMode.Create,
                    FileAccess.Write, FileShare.Read))
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(PendingMagic);
                    writer.Write(FormatVersion);
                    for (int i = 0; i < keep.Count; i++)
                    {
                        WriteRecord(writer, keep[i]);
                    }

                    writer.Flush();
                }
            }
            catch
            {
            }
        }

        private void RebuildFlushedLocked()
        {
            flushed.Clear();
            foreach (KeyValuePair<long, DayEntry> pair in days)
            {
                SetFlushedLocked(pair.Key, string.Empty, pair.Value.NicRx, pair.Value.NicTx);
                foreach (KeyValuePair<string, DayAppRow> app in pair.Value.Apps)
                {
                    SetFlushedLocked(pair.Key, app.Key, app.Value.Rx, app.Value.Tx);
                }
            }
        }

        private void Load()
        {
            LoadFile(archivePath, ArchiveMagic);
            LoadFile(pendingPath, PendingMagic);
            currentDate = ToDateInt(DateTime.Today);
            RebuildFlushedLocked();
        }

        private void LoadFile(string path, int expectedMagic)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (stream.Length < 6)
                    {
                        return;
                    }

                    if (reader.ReadInt32() != expectedMagic || reader.ReadInt16() != FormatVersion)
                    {
                        return;
                    }

                    while (stream.Position + 15 <= stream.Length)
                    {
                        PendingRecord record;
                        if (!TryReadRecord(reader, out record))
                        {
                            return;
                        }

                        DayEntry entry = GetOrCreateLocked(record.Date);
                        if (record.Kind == RecordKindNic)
                        {
                            entry.NicRx += record.Rx;
                            entry.NicTx += record.Tx;
                        }
                        else
                        {
                            DayAppRow row;
                            if (!entry.Apps.TryGetValue(record.Name, out row))
                            {
                                row = new DayAppRow();
                                entry.Apps.Add(record.Name, row);
                            }

                            row.Rx += record.Rx;
                            row.Tx += record.Tx;
                        }
                    }
                }
            }
            catch
            {
                // Corrupt or locked files degrade to whatever was loaded so far.
            }
        }

        private static bool TryReadRecord(BinaryReader reader, out PendingRecord record)
        {
            record = new PendingRecord();
            record.Kind = reader.ReadByte();
            record.Date = reader.ReadInt32();
            int nameLength = reader.ReadUInt16();
            if (record.Date <= 19000101 || record.Date >= 22000101 || nameLength > MaxNameBytes)
            {
                return false;
            }

            record.Name = nameLength > 0 ? Encoding.UTF8.GetString(reader.ReadBytes(nameLength)) : string.Empty;
            record.Rx = reader.ReadInt64();
            record.Tx = reader.ReadInt64();
            if (record.Rx < 0 || record.Tx < 0)
            {
                return false;
            }

            return true;
        }

        private static void WriteRecord(BinaryWriter writer, PendingRecord record)
        {
            byte[] nameBytes = string.IsNullOrEmpty(record.Name)
                ? new byte[0]
                : Encoding.UTF8.GetBytes(record.Name);
            if (nameBytes.Length > MaxNameBytes)
            {
                byte[] trimmed = new byte[MaxNameBytes];
                Array.Copy(nameBytes, trimmed, MaxNameBytes);
                nameBytes = trimmed;
            }

            writer.Write(record.Kind);
            writer.Write((int)record.Date);
            writer.Write((ushort)nameBytes.Length);
            if (nameBytes.Length > 0)
            {
                writer.Write(nameBytes);
            }

            writer.Write(record.Rx);
            writer.Write(record.Tx);
        }
    }

    // Attributes per-connection TCP byte deltas to the owning process, once per
    // second, using GetExtendedTcpTable + GetPerTcpConnectionEStats (the same
    // mechanism Resource Monitor uses; no admin rights required).
    internal sealed class AppTrafficMonitor
    {
        // 连接记录只在闲置超时后清理：如果每轮都重建，某轮没读到某条连接就会被
        // 当成"新连接"重新整笔计入，流量会翻倍。
        private static readonly TimeSpan FlowIdleTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan NameCacheLifetime = TimeSpan.FromSeconds(60);

        private readonly TrafficStore store;
        private readonly Dictionary<TcpFlowKey, FlowState> flows = new Dictionary<TcpFlowKey, FlowState>();
        private readonly Dictionary<int, ProcessNameEntry> processNames = new Dictionary<int, ProcessNameEntry>();
        private byte[] v4Buffer = new byte[0];
        private byte[] v6Buffer = new byte[0];
        private IntPtr statsBuffer;
        private IntPtr enableBuffer;
        private int ticksSincePrune;

        private sealed class FlowState
        {
            public long InBytes;
            public long OutBytes;
            public DateTime LastSeenUtc;
            public bool Enabled;
            public bool HasBaseline;
        }

        private sealed class ProcessNameEntry
        {
            public string Name;
            public DateTime ExpiresUtc;
        }

        public AppTrafficMonitor(TrafficStore trafficStore)
        {
            store = trafficStore;
            statsBuffer = Marshal.AllocHGlobal(NativeMethods.TcpEstatsDataSize);
            enableBuffer = Marshal.AllocHGlobal(NativeMethods.TcpEstatsDataRwSize);
            Marshal.WriteByte(enableBuffer, 1); // EnableCollection = TRUE
        }

        // 每条连接的字节统计默认关闭，必须先向系统申请启用，而该申请只对管理员放行。
        // 界面用它来决定是显示排行榜还是显示"需要管理员权限"的说明。
        public static bool CanCollectAppTraffic
        {
            get { return NativeMethods.IsUserAnAdmin(); }
        }

        public void Sample()
        {
            SampleFamily(NativeMethods.AddressFamilyInet);
            SampleFamily(NativeMethods.AddressFamilyInet6);

            if (++ticksSincePrune >= 60)
            {
                ticksSincePrune = 0;
                PruneFlows();
            }

            store.CheckDayRoll();
        }

        // 连接结束后它的记录会一直留着，这里定期回收，防止长跑之后字典无限膨胀。
        private void PruneFlows()
        {
            DateTime flowDeadline = DateTime.UtcNow - FlowIdleTimeout;
            List<TcpFlowKey> staleFlows = new List<TcpFlowKey>();
            foreach (KeyValuePair<TcpFlowKey, FlowState> pair in flows)
            {
                if (pair.Value.LastSeenUtc < flowDeadline)
                {
                    staleFlows.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleFlows.Count; i++)
            {
                flows.Remove(staleFlows[i]);
            }

            DateTime nameDeadline = DateTime.UtcNow;
            List<int> staleNames = new List<int>();
            foreach (KeyValuePair<int, ProcessNameEntry> pair in processNames)
            {
                if (pair.Value.ExpiresUtc <= nameDeadline)
                {
                    staleNames.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleNames.Count; i++)
            {
                processNames.Remove(staleNames[i]);
            }
        }

        private void SampleFamily(int family)
        {
            int size = 0;
            uint ret = NativeMethods.GetExtendedTcpTable(null, ref size, false, family,
                NativeMethods.TcpTableOwnerPidAll, 0);
            if (ret != NativeMethods.ErrorInsufficientBuffer || size <= 4)
            {
                return;
            }

            byte[] buffer = family == NativeMethods.AddressFamilyInet
                ? EnsureBuffer(ref v4Buffer, size)
                : EnsureBuffer(ref v6Buffer, size);
            if (buffer.Length < size)
            {
                return;
            }

            ret = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, family,
                NativeMethods.TcpTableOwnerPidAll, 0);
            if (ret != 0 || size < 4 || size > buffer.Length)
            {
                return;
            }

            int rowSize = family == NativeMethods.AddressFamilyInet ? 24 : 56;
            int count = BitConverter.ToInt32(buffer, 0);
            int maxRows = (size - 4) / rowSize;
            if (count > maxRows)
            {
                count = maxRows;
            }

            DateTime now = DateTime.UtcNow;
            // IPv6 行的每个字段都比 IPv4 行靠后 3 字节；偏移写错会把流量算到别的进程头上。
            int pidOffset = family == NativeMethods.AddressFamilyInet ? 20 : 52;

            GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                IntPtr basePointer = pinned.AddrOfPinnedObject();
                for (int i = 0; i < count; i++)
                {
                    int offset = 4 + i * rowSize;
                    TcpFlowKey key = family == NativeMethods.AddressFamilyInet
                        ? ReadV4Key(buffer, offset)
                        : ReadV6Key(buffer, offset);

                    FlowState state;
                    if (!flows.TryGetValue(key, out state))
                    {
                        state = new FlowState();
                        state.Enabled = TryEnableStats(basePointer, offset, family);
                        flows.Add(key, state);
                    }

                    state.LastSeenUtc = now;
                    if (!state.Enabled)
                    {
                        // 申请启用被系统拒绝（普通权限），这条连接读不到字节数。
                        continue;
                    }

                    IntPtr rowPointer = new IntPtr(basePointer.ToInt64() + offset);
                    uint status = family == NativeMethods.AddressFamilyInet
                        ? NativeMethods.GetPerTcpConnectionEStats(rowPointer, NativeMethods.TcpEstatsTypeData,
                            IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, statsBuffer, 0, NativeMethods.TcpEstatsDataSize)
                        : NativeMethods.GetPerTcp6ConnectionEStats(rowPointer, NativeMethods.TcpEstatsTypeData,
                            IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, statsBuffer, 0, NativeMethods.TcpEstatsDataSize);
                    if (status != 0)
                    {
                        continue;
                    }

                    long inBytes = Marshal.ReadInt64(statsBuffer, 16);  // DataBytesIn
                    long outBytes = Marshal.ReadInt64(statsBuffer, 0);  // DataBytesOut
                    if (inBytes < 0)
                    {
                        inBytes = 0;
                    }

                    if (outBytes < 0)
                    {
                        outBytes = 0;
                    }

                    long deltaIn;
                    long deltaOut;
                    if (!state.HasBaseline)
                    {
                        // 计数器是从连接建立就开始累计的，所以第一次读到的就是这条连接已经
                        // 发生的全部流量，整笔记下，几秒就结束的短连接才不会被白白丢掉。
                        deltaIn = inBytes;
                        deltaOut = outBytes;
                        state.HasBaseline = true;
                    }
                    else
                    {
                        deltaIn = inBytes >= state.InBytes ? inBytes - state.InBytes : inBytes;
                        deltaOut = outBytes >= state.OutBytes ? outBytes - state.OutBytes : outBytes;
                    }

                    state.InBytes = inBytes;
                    state.OutBytes = outBytes;
                    if (deltaIn <= 0 && deltaOut <= 0)
                    {
                        continue;
                    }

                    int pid = BitConverter.ToInt32(buffer, offset + pidOffset);
                    string app = ResolveName(pid);
                    if (app != null)
                    {
                        store.RecordApp(DateTime.Today, app, deltaIn, deltaOut);
                    }
                }
            }
            finally
            {
                pinned.Free();
            }
        }

        private bool TryEnableStats(IntPtr basePointer, int offset, int family)
        {
            IntPtr rowPointer = new IntPtr(basePointer.ToInt64() + offset);
            uint status = family == NativeMethods.AddressFamilyInet
                ? NativeMethods.SetPerTcpConnectionEStats(rowPointer, NativeMethods.TcpEstatsTypeData,
                    enableBuffer, 0, NativeMethods.TcpEstatsDataRwSize, 0)
                : NativeMethods.SetPerTcp6ConnectionEStats(rowPointer, NativeMethods.TcpEstatsTypeData,
                    enableBuffer, 0, NativeMethods.TcpEstatsDataRwSize, 0);
            return status == 0;
        }

        private static TcpFlowKey ReadV4Key(byte[] buffer, int offset)
        {
            TcpFlowKey key = new TcpFlowKey();
            key.Local1 = BitConverter.ToUInt32(buffer, offset + 4);
            key.Local2 = 0;
            key.Remote1 = BitConverter.ToUInt32(buffer, offset + 12);
            key.Remote2 = 0;
            key.LocalPort = ReadNetworkPort(buffer, offset + 8);
            key.RemotePort = ReadNetworkPort(buffer, offset + 16);
            return key;
        }

        // MIB_TCP6ROW 各字段相对行首的位置：
        //   0 State(4) | 4 LocalAddr(16) | 20 LocalScopeId(4) | 24 LocalPort
        //   | 28 RemoteAddr(16) | 44 RemoteScopeId(4) | 48 RemotePort | 52 OwningPid
        private static TcpFlowKey ReadV6Key(byte[] buffer, int offset)
        {
            TcpFlowKey key = new TcpFlowKey();
            key.Local1 = (ulong)BitConverter.ToInt64(buffer, offset + 4);
            key.Local2 = (ulong)BitConverter.ToInt64(buffer, offset + 12);
            key.Remote1 = (ulong)BitConverter.ToInt64(buffer, offset + 28);
            key.Remote2 = (ulong)BitConverter.ToInt64(buffer, offset + 36);
            key.LocalPort = ReadNetworkPort(buffer, offset + 24);
            key.RemotePort = ReadNetworkPort(buffer, offset + 48);
            return key;
        }

        private static int ReadNetworkPort(byte[] buffer, int offset)
        {
            return (buffer[offset] << 8) | buffer[offset + 1];
        }

        private static byte[] EnsureBuffer(ref byte[] buffer, int size)
        {
            if (buffer.Length < size)
            {
                int capacity = size + 4096;
                byte[] grown = new byte[capacity];
                buffer = grown;
                return grown;
            }

            return buffer;
        }

        private string ResolveName(int pid)
        {
            if (pid <= 0)
            {
                return null;
            }

            if (pid == 4)
            {
                return "系统";
            }

            ProcessNameEntry entry;
            if (processNames.TryGetValue(pid, out entry) && entry.ExpiresUtc > DateTime.UtcNow)
            {
                return entry.Name.Length > 0 ? entry.Name : null;
            }

            string name;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    name = process.ProcessName;
                }
            }
            catch
            {
                // 进程已经不在了。这条缓存只留很短时间，否则进程号被新进程复用后
                // 排行榜会一直顶着旧程序的名字。
                CacheName(pid, string.Empty, false);
                return null;
            }

            if (string.IsNullOrEmpty(name))
            {
                CacheName(pid, string.Empty, false);
                return null;
            }

            CacheName(pid, name, true);
            return name;
        }

        private void CacheName(int pid, string name, bool resolved)
        {
            ProcessNameEntry entry = new ProcessNameEntry();
            entry.Name = name;
            entry.ExpiresUtc = DateTime.UtcNow.Add(
                resolved ? NameCacheLifetime : TimeSpan.FromSeconds(20));
            processNames[pid] = entry;
        }

        public void Dispose()
        {
            if (statsBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(statsBuffer);
                statsBuffer = IntPtr.Zero;
            }

            if (enableBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(enableBuffer);
                enableBuffer = IntPtr.Zero;
            }
        }
    }

    internal struct TcpFlowKey : IEquatable<TcpFlowKey>
    {
        public ulong Local1;
        public ulong Local2;
        public ulong Remote1;
        public ulong Remote2;
        public int LocalPort;
        public int RemotePort;

        public bool Equals(TcpFlowKey other)
        {
            return Local1 == other.Local1 && Local2 == other.Local2 &&
                   Remote1 == other.Remote1 && Remote2 == other.Remote2 &&
                   LocalPort == other.LocalPort && RemotePort == other.RemotePort;
        }

        public override bool Equals(object obj)
        {
            return obj is TcpFlowKey && Equals((TcpFlowKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Local1.GetHashCode();
                hash = (hash * 397) ^ Local2.GetHashCode();
                hash = (hash * 397) ^ Remote1.GetHashCode();
                hash = (hash * 397) ^ Remote2.GetHashCode();
                hash = (hash * 397) ^ LocalPort;
                hash = (hash * 397) ^ RemotePort;
                return hash;
            }
        }
    }

    internal sealed class NetworkSpeedSampler
    {
        private long lastReceived;
        private long lastSent;
        private DateTime lastTime;
        private bool hasBaseline;
        private bool hasSmoothed;
        private double smoothedDownload;
        private double smoothedUpload;

        public void Reset()
        {
            AdapterTotals totals = ReadTotals();
            lastReceived = totals.ReceivedBytes;
            lastSent = totals.SentBytes;
            lastTime = DateTime.UtcNow;
            hasBaseline = true;
            hasSmoothed = false;
            smoothedDownload = 0;
            smoothedUpload = 0;
        }

        public SpeedSample Next()
        {
            AdapterTotals totals = ReadTotals();
            DateTime now = DateTime.UtcNow;

            if (!hasBaseline)
            {
                lastReceived = totals.ReceivedBytes;
                lastSent = totals.SentBytes;
                lastTime = now;
                hasBaseline = true;
                hasSmoothed = false;
                return new SpeedSample(0, 0, totals.HasAnyAdapter, 0, 0, 0);
            }

            double elapsed = Math.Max(0.2, (now - lastTime).TotalSeconds);
            long downBytes = totals.ReceivedBytes >= lastReceived ? totals.ReceivedBytes - lastReceived : 0;
            long upBytes = totals.SentBytes >= lastSent ? totals.SentBytes - lastSent : 0;

            lastReceived = totals.ReceivedBytes;
            lastSent = totals.SentBytes;
            lastTime = now;

            double rawDownload = downBytes / elapsed;
            double rawUpload = upBytes / elapsed;
            if (!hasSmoothed)
            {
                smoothedDownload = rawDownload;
                smoothedUpload = rawUpload;
                hasSmoothed = true;
            }
            else
            {
                // A short exponential average removes one-second counter jitter while
                // still converging quickly when traffic starts or stops.
                double alpha = 1.0 - Math.Exp(-elapsed / 2.0);
                smoothedDownload += (rawDownload - smoothedDownload) * alpha;
                smoothedUpload += (rawUpload - smoothedUpload) * alpha;
            }

            return new SpeedSample(smoothedDownload, smoothedUpload, totals.HasAnyAdapter,
                elapsed, downBytes, upBytes);
        }

        private static AdapterTotals ReadTotals()
        {
            long received = 0;
            long sent = 0;
            bool any = false;

            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch
            {
                return new AdapterTotals(0, 0, false);
            }

            // Mutually exclusive selection: when a VPN/tunnel adapter is up, count only
            // it (the same bytes otherwise appear on both the tunnel and the physical
            // NIC and would be double-counted). Without a tunnel, count physical NICs;
            // as a last resort, count anything up with a gateway.
            List<NetworkInterface> tunnelLike = new List<NetworkInterface>();
            List<NetworkInterface> physical = new List<NetworkInterface>();
            List<NetworkInterface> others = new List<NetworkInterface>();

            foreach (NetworkInterface adapter in interfaces)
            {
                if (adapter == null || adapter.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                NetworkInterfaceType type = adapter.NetworkInterfaceType;
                if (type == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                if (type == NetworkInterfaceType.Tunnel || type == NetworkInterfaceType.Ppp)
                {
                    // Tunnels such as WireGuard often publish /1 routes instead of a
                    // plain default gateway, so do not require a gateway here.
                    tunnelLike.Add(adapter);
                    continue;
                }

                if (!HasDefaultGateway(adapter))
                {
                    continue;
                }

                switch (type)
                {
                    case NetworkInterfaceType.Ethernet:
                    case NetworkInterfaceType.FastEthernetT:
                    case NetworkInterfaceType.FastEthernetFx:
                    case NetworkInterfaceType.GigabitEthernet:
                    case NetworkInterfaceType.Wireless80211:
                        physical.Add(adapter);
                        break;
                    default:
                        others.Add(adapter);
                        break;
                }
            }

            List<NetworkInterface> selected = tunnelLike.Count > 0
                ? tunnelLike
                : (physical.Count > 0 ? physical : others);
            any = selected.Count > 0;

            foreach (NetworkInterface adapter in selected)
            {
                try
                {
                    IPv4InterfaceStatistics stats = adapter.GetIPv4Statistics();
                    received += stats.BytesReceived;
                    sent += stats.BytesSent;
                }
                catch
                {
                    // Some virtual adapters throw while Windows is changing network state.
                }
            }

            return new AdapterTotals(received, sent, any);
        }

        private static bool HasDefaultGateway(NetworkInterface adapter)
        {
            try
            {
                GatewayIPAddressInformationCollection gateways =
                    adapter.GetIPProperties().GatewayAddresses;
                foreach (GatewayIPAddressInformation gateway in gateways)
                {
                    if (gateway != null && gateway.Address != null &&
                        gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !gateway.Address.Equals(System.Net.IPAddress.Any))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // The adapter may disappear while Windows refreshes network state.
            }

            return false;
        }
    }

    internal struct AdapterTotals
    {
        public readonly long ReceivedBytes;
        public readonly long SentBytes;
        public readonly bool HasAnyAdapter;

        public AdapterTotals(long receivedBytes, long sentBytes, bool hasAnyAdapter)
        {
            ReceivedBytes = receivedBytes;
            SentBytes = sentBytes;
            HasAnyAdapter = hasAnyAdapter;
        }
    }

    internal struct SpeedSample
    {
        public readonly double DownloadBytesPerSecond;
        public readonly double UploadBytesPerSecond;
        public readonly bool HasAnyAdapter;
        public readonly double ElapsedSeconds;
        public readonly long DownloadBytes;
        public readonly long UploadBytes;

        public SpeedSample(double downloadBytesPerSecond, double uploadBytesPerSecond, bool hasAnyAdapter)
            : this(downloadBytesPerSecond, uploadBytesPerSecond, hasAnyAdapter, 1.0,
                ToByteCount(downloadBytesPerSecond), ToByteCount(uploadBytesPerSecond))
        {
        }

        public SpeedSample(double downloadBytesPerSecond, double uploadBytesPerSecond,
            bool hasAnyAdapter, double elapsedSeconds, long downloadBytes, long uploadBytes)
        {
            DownloadBytesPerSecond = downloadBytesPerSecond;
            UploadBytesPerSecond = uploadBytesPerSecond;
            HasAnyAdapter = hasAnyAdapter;
            ElapsedSeconds = Math.Max(0, elapsedSeconds);
            DownloadBytes = Math.Max(0, downloadBytes);
            UploadBytes = Math.Max(0, uploadBytes);
        }

        private static long ToByteCount(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0 || double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
            {
                return 0;
            }

            return (long)Math.Min(long.MaxValue, Math.Round(bytesPerSecond));
        }
    }

    internal struct RateText
    {
        public readonly string Value;
        public readonly string Unit;

        public RateText(string value, string unit)
        {
            Value = value;
            Unit = unit;
        }
    }

    internal static class SpeedFormatter
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB" };

        public static string Format(double bytes)
        {
            if (bytes < 0 || double.IsNaN(bytes) || double.IsInfinity(bytes))
            {
                bytes = 0;
            }

            int unit = 0;
            while (bytes >= 1024 && unit < Units.Length - 1)
            {
                bytes /= 1024;
                unit++;
            }

            string value;
            if (bytes >= 100)
            {
                value = bytes.ToString("0");
            }
            else if (bytes >= 10)
            {
                value = bytes.ToString("0.#");
            }
            else
            {
                value = bytes.ToString("0.##");
            }

            return value + Units[unit];
        }

        public static RateText FormatBits(double bytesPerSecond)
        {
            double bitsPerSecond = Clean(bytesPerSecond) * 8.0;
            string unit = "bps";
            double value = bitsPerSecond;

            if (value >= 1000.0 * 1000.0)
            {
                value /= 1000.0 * 1000.0;
                unit = "Mbps";
            }
            else if (value >= 1000.0)
            {
                value /= 1000.0;
                unit = "Kbps";
            }

            return new RateText(FormatNumber(value), unit);
        }

        public static RateText FormatMegabytesRate(double bytesPerSecond)
        {
            double value = Clean(bytesPerSecond) / (1024.0 * 1024.0);
            return new RateText(FormatNumber(value), "MB/s");
        }

        public static string FormatMegabytes(double bytesPerSecond)
        {
            return FormatNumber(Clean(bytesPerSecond) / (1024.0 * 1024.0)) + " MB/s";
        }

        public static string FormatTraffic(double bytes)
        {
            return Format(bytes);
        }

        public static string FormatCompact(double bytesPerSecond)
        {
            double value = Clean(bytesPerSecond) / 1024.0;
            if (value >= 1024.0)
            {
                return FormatNumber(value / 1024.0) + "M";
            }

            return FormatNumber(value) + "K";
        }

        private static double Clean(double value)
        {
            if (value < 0 || double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0;
            }

            return value;
        }

        private static string FormatNumber(double value)
        {
            if (value >= 100.0)
            {
                return value.ToString("0");
            }

            if (value >= 10.0)
            {
                return value.ToString("0.#");
            }

            return value.ToString("0.##");
        }
    }

    internal static class AppSettings
    {
        private const string SettingsKey = @"Software\NetSpeedBall";

        public static bool LoadUseMegabits()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
                {
                    return key != null && Convert.ToInt32(key.GetValue("UseMegabits", 0)) == 1;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SaveUseMegabits(bool value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (key != null) key.SetValue("UseMegabits", value ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch
            {
            }
        }

        public static bool TryLoadLocation(out Point location)
        {
            location = Point.Empty;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
                {
                    if (key == null) return false;
                    object xValue = key.GetValue("X");
                    object yValue = key.GetValue("Y");
                    if (!(xValue is int) || !(yValue is int)) return false;
                    location = new Point((int)xValue, (int)yValue);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SaveLocation(Point location)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (key == null) return;
                    key.SetValue("X", location.X, RegistryValueKind.DWord);
                    key.SetValue("Y", location.Y, RegistryValueKind.DWord);
                }
            }
            catch
            {
            }
        }
    }
    internal static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // Windows 10/11 的"启动应用"列表开关不在 Run 键里，而是单独记在这条键下：
        // 首字节 0x02 = 允许启动，0x03 = 已被禁用（任务管理器里关掉了）。
        // Run 键里即使有本程序，只要这里是 0x03，开机一样不会启动。
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const byte ApprovedEnabled = 0x02;
        private const byte ApprovedDisabled = 0x03;

        public static bool IsEnabled(string appName)
        {
            return HasRunEntry(appName) && !IsBlockedByApprovedList(appName);
        }

        public static void SetEnabled(string appName, bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("\u65e0\u6cd5\u6253\u5f00\u5f53\u524d\u7528\u6237\u542f\u52a8\u9879\u6ce8\u518c\u8868\u3002");
                }

                if (enabled)
                {
                    key.SetValue(appName, GetStartupCommand(), RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(appName, false);
                }
            }

            WriteApprovedFlag(appName, enabled);
        }

        /// <summary>
        /// 判断 Run 键里是否已登记本程序，且指向的仍是当前这个 exe。
        /// </summary>
        private static bool HasRunEntry(string appName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    string value = key.GetValue(appName) as string;
                    if (string.IsNullOrEmpty(value))
                    {
                        return false;
                    }

                    // 允许带引号、不带引号、或后面跟着多余空格，只要路径一致就算已登记。
                    string normalized = value.Trim().Trim('"').Trim();
                    return string.Equals(normalized, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 是否被 Windows"启动应用"列表单独禁用。禁用了就开机不启动，需要清掉这个标记。
        /// </summary>
        private static bool IsBlockedByApprovedList(string appName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ApprovedKey, false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    byte[] data = key.GetValue(appName) as byte[];
                    return data != null && data.Length > 0 && data[0] == ApprovedDisabled;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 同步"启动应用"列表的开关标记。勾选时写成允许启动，取消勾选时直接删除标记，
        /// 免得留下禁用记录把下次勾选又挡住。
        /// </summary>
        private static void WriteApprovedFlag(string appName, bool enabled)
        {
            try
            {
                if (!enabled)
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                    {
                        if (key != null)
                        {
                            key.DeleteValue(appName, false);
                        }
                    }

                    return;
                }

                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ApprovedKey))
                {
                    if (key == null)
                    {
                        return;
                    }

                    byte[] data = new byte[12];
                    data[0] = ApprovedEnabled;
                    key.SetValue(appName, data, RegistryValueKind.Binary);
                }
            }
            catch
            {
                // 写不上这条标记不影响 Run 键，开机启动多半仍是好的，忽略即可。
            }
        }

        private static string GetStartupCommand()
        {
            return "\"" + Application.ExecutablePath + "\"";
        }
    }

    internal static class IconFactory
    {
        public static Icon CreateIcon(int size)
        {
            using (Bitmap bitmap = new Bitmap(size, size))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Rectangle rect = new Rectangle(3, 3, size - 7, size - 7);
                using (GraphicsPath ballPath = new GraphicsPath())
                {
                    ballPath.AddEllipse(rect);
                    using (PathGradientBrush face = new PathGradientBrush(ballPath))
                    {
                        face.CenterPoint = new PointF(rect.X + rect.Width * 0.46f, rect.Y + rect.Height * 0.34f);
                        face.CenterColor = Color.White;
                        face.SurroundColors = new[] { Color.FromArgb(232, 243, 251) };
                        g.FillPath(face, ballPath);
                    }

                    using (GraphicsPath glossPath = new GraphicsPath())
                    {
                        Rectangle gloss = new Rectangle(
                            rect.X + size / 5,
                            rect.Y + size / 9,
                            rect.Width - (size * 2 / 5),
                            Math.Max(4, size / 4));
                        glossPath.AddEllipse(gloss);
                        using (PathGradientBrush glossBrush = new PathGradientBrush(glossPath))
                        {
                            glossBrush.CenterColor = Color.FromArgb(148, 255, 255, 255);
                            glossBrush.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                            g.FillPath(glossBrush, glossPath);
                        }
                    }
                }

                RectangleF ringRect = new RectangleF(
                    rect.X + size * 0.07f,
                    rect.Y + size * 0.07f,
                    rect.Width - size * 0.14f,
                    rect.Height - size * 0.14f);

                using (Pen glow = new Pen(Color.FromArgb(112, 122, 181, 220), Math.Max(1.0f, size / 14.0f)))
                using (Pen outer = new Pen(Color.FromArgb(246, 250, 253, 255), Math.Max(1.0f, size / 25.0f)))
                using (Pen inner = new Pen(Color.FromArgb(190, 176, 211, 232), Math.Max(1.0f, size / 55.0f)))
                using (Pen progress = new Pen(Color.FromArgb(218, 46, 136, 232), Math.Max(1.2f, size / 20.0f)))
                {
                    progress.StartCap = LineCap.Round;
                    progress.EndCap = LineCap.Round;
                    g.DrawEllipse(glow, ringRect);
                    g.DrawEllipse(outer, rect);
                    g.DrawEllipse(inner, RectangleF.Inflate(ringRect, -size * 0.05f, -size * 0.05f));
                    g.DrawArc(progress, RectangleF.Inflate(ringRect, -size * 0.04f, -size * 0.04f), 132, 238);
                }

                DrawIconClouds(g, rect, size);
                DrawIconRocket(g, size * 0.59f, size * 0.35f, size / 64.0f);

                using (Pen edge = new Pen(Color.FromArgb(178, 174, 211, 231), Math.Max(1.0f, size / 58.0f)))
                {
                    g.DrawEllipse(edge, rect);
                }

                IntPtr hIcon = bitmap.GetHicon();
                try
                {
                    Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
                    return icon;
                }
                finally
                {
                    NativeMethods.DestroyIcon(hIcon);
                }
            }
        }

        private static void DrawIconClouds(Graphics g, Rectangle rect, int size)
        {
            GraphicsState state = g.Save();
            using (GraphicsPath clipPath = new GraphicsPath())
            {
                clipPath.AddEllipse(rect);
                g.SetClip(clipPath, CombineMode.Intersect);

                float scale = size / 64.0f;
                float y = rect.Bottom - 18.0f * scale;
                using (Brush body = new SolidBrush(Color.FromArgb(248, 252, 255)))
                using (Brush shade = new SolidBrush(Color.FromArgb(220, 235, 244, 250)))
                {
                    g.FillEllipse(body, rect.X + 4.0f * scale, y + 5.0f * scale, 19.0f * scale, 15.0f * scale);
                    g.FillEllipse(body, rect.X + 17.0f * scale, y, 21.0f * scale, 19.0f * scale);
                    g.FillEllipse(body, rect.X + 34.0f * scale, y + 4.0f * scale, 24.0f * scale, 17.0f * scale);
                    g.FillRectangle(body, rect.X + 4.0f * scale, y + 11.0f * scale, rect.Width - 8.0f * scale, 19.0f * scale);
                    g.FillEllipse(shade, rect.X + 36.0f * scale, y + 12.0f * scale, 23.0f * scale, 11.0f * scale);
                }
            }

            g.Restore(state);
        }

        private static void DrawIconRocket(Graphics g, float x, float y, float scale)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(-34.0f);
            g.ScaleTransform(scale, scale);

            using (GraphicsPath flame = new GraphicsPath())
            using (Brush orange = new SolidBrush(Color.FromArgb(236, 255, 151, 54)))
            {
                flame.AddPolygon(new[]
                {
                    new PointF(-10.0f, -3.0f),
                    new PointF(-17.0f, 0.0f),
                    new PointF(-10.0f, 3.0f)
                });
                g.FillPath(orange, flame);
            }

            using (GraphicsPath body = new GraphicsPath())
            using (LinearGradientBrush bodyBrush = new LinearGradientBrush(
                       new RectangleF(-9.0f, -6.0f, 19.0f, 12.0f),
                       Color.White,
                       Color.FromArgb(202, 232, 255),
                       LinearGradientMode.ForwardDiagonal))
            using (Pen edge = new Pen(Color.FromArgb(192, 70, 142, 220), 1.0f))
            using (Brush fin = new SolidBrush(Color.FromArgb(255, 44, 132, 224)))
            {
                g.FillPolygon(fin, new[]
                {
                    new PointF(-5.0f, -3.4f),
                    new PointF(-9.0f, -7.2f),
                    new PointF(-2.0f, -5.0f)
                });
                g.FillPolygon(fin, new[]
                {
                    new PointF(-5.0f, 3.4f),
                    new PointF(-9.0f, 7.2f),
                    new PointF(-2.0f, 5.0f)
                });

                body.AddBezier(-9.0f, -4.8f, -3.5f, -7.2f, 5.0f, -4.8f, 9.0f, 0.0f);
                body.AddBezier(9.0f, 0.0f, 5.0f, 4.8f, -3.5f, 7.2f, -9.0f, 4.8f);
                body.CloseFigure();
                g.FillPath(bodyBrush, body);
                g.DrawPath(edge, body);
            }

            using (Brush window = new SolidBrush(Color.FromArgb(255, 42, 137, 229)))
            {
                g.FillEllipse(window, 0.4f, -2.4f, 4.8f, 4.8f);
            }

            g.Restore(state);
        }
    }

    internal static class NativeMethods
    {
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const int AddressFamilyInet = 2;
        public const int AddressFamilyInet6 = 23;
        public const int TcpTableOwnerPidAll = 5;
        public const int TcpEstatsTypeData = 1;
        public const int TcpEstatsDataSize = 96;
        // TCP_ESTATS_DATA_RW_v0 只含一个 EnableCollection 开关，大小正好 1 字节。
        public const int TcpEstatsDataRwSize = 1;
        public const uint ErrorInsufficientBuffer = 122;
        public const uint ErrorNotFound = 1168;
        public const int HwndBroadcast = 0xFFFF;

        [StructLayout(LayoutKind.Sequential)]
        public struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;

            public long ToInt64()
            {
                return ((long)HighDateTime << 32) | LowDateTime;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysicalMemory;
            public ulong AvailablePhysicalMemory;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime,
            out FileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(byte[] tcpTable, ref int size, bool order,
            int addressFamily, int tableClass, int reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetPerTcpConnectionEStats(IntPtr row, int estatsType,
            IntPtr rw, int rwVersion, int rwSize,
            IntPtr ros, int rosVersion, int rosSize,
            IntPtr rod, int rodVersion, int rodSize);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetPerTcp6ConnectionEStats(IntPtr row, int estatsType,
            IntPtr rw, int rwVersion, int rwSize,
            IntPtr ros, int rosVersion, int rosSize,
            IntPtr rod, int rodVersion, int rodSize);

        // 采集默认是关闭的：只有先用它申请启用，后面的 Get...EStats 才会返回真实字节数。
        // 这一步需要管理员权限，普通权限会返回 ERROR_ACCESS_DENIED(5)。
        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint SetPerTcpConnectionEStats(IntPtr row, int estatsType,
            IntPtr rw, int rwVersion, int rwSize, int offset);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint SetPerTcp6ConnectionEStats(IntPtr row, int estatsType,
            IntPtr rw, int rwVersion, int rwSize, int offset);

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern bool IsUserAnAdmin();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}

