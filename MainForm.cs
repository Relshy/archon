// this form paints the whole ui itself instead of using child controls, so
// WM_NCHITTEST still reaches it and dragging/snapping/resize stay native.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Archon
{
    static class Native
    {
        public const int WM_NCCALCSIZE = 0x0083;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_NCMOUSELEAVE = 0x02A2;

        public const int HTCLIENT = 1;
        public const int HTCAPTION = 2;
        public const int HTLEFT = 10;
        public const int HTRIGHT = 11;
        public const int HTTOP = 12;
        public const int HTTOPLEFT = 13;
        public const int HTTOPRIGHT = 14;
        public const int HTBOTTOM = 15;
        public const int HTBOTTOMLEFT = 16;
        public const int HTBOTTOMRIGHT = 17;

        public const int SW_RESTORE = 9;

        // win11 only, older builds return an error
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);
    }

    class InstanceRow
    {
        public int Pid;
        public string Uptime;
        public int Cpu;
        public float MemoryGb;
        public bool HasWindow;

        // never kill anything off this - MainWindowHandle lies during window recreation
        public bool Closed;

        public string Label;

        // shared with _cpuTrend, not a copy
        public List<int> Trend;
    }

    class PendingLaunch
    {
        public HashSet<int> BaselinePids;
        public string Label;
        public DateTime StartedAt;
    }

    class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Skin.Card; } }
        public override Color MenuBorder { get { return Skin.Line; } }
        public override Color MenuItemBorder { get { return Skin.Line; } }
        public override Color MenuItemSelected { get { return Skin.CardLit; } }
        public override Color MenuItemSelectedGradientBegin { get { return Skin.CardLit; } }
        public override Color MenuItemSelectedGradientEnd { get { return Skin.CardLit; } }
        public override Color ImageMarginGradientBegin { get { return Skin.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Skin.Card; } }
        public override Color ImageMarginGradientEnd { get { return Skin.Card; } }
    }

    class MainForm : Form
    {
        // Windows10Universal is the microsoft store build
        private static readonly string[] ProcessNames =
            new string[] { "RobloxPlayerBeta", "Windows10Universal" };

        private const string TikTokUrl = "https://www.tiktok.com/@sillyrelshy";
        private const string GitHubUrl = "https://github.com/Relshy/archon";

        // logical 96dpi units, scaled once
        private const float TitleH = 46f;
        private const float PadX = 32f;
        private const float CardH = 322f;
        private const float PanelH = 100f;
        private const float Gap = 18f;
        private const float CardMinW = 236f;

        private float _scale = 1f;

        private readonly Motion _motion = new Motion();
        private readonly List<KeyValuePair<string, RectangleF>> _spots =
            new List<KeyValuePair<string, RectangleF>>();
        private string _hot = "";
        private string _pressed = "";

        private int _tab;
        private bool _showCredits;

        // pid not index - the list gets re-sorted every poll
        private int _selInstancePid = -1;
        private int _selAccount = -1;
        private bool _busy;
        private bool _bootstrapped;

        private float _instanceScroll, _instanceViewH, _instanceContentH;
        private float _accountScroll, _accountViewH, _accountContentH;

        private readonly SingletonManager _singleton = new SingletonManager();
        private InstanceRow[] _instances = new InstanceRow[0];
        private int _windowlessCount;

        private struct CpuSample { public TimeSpan Cpu; public DateTime At; }
        private readonly Dictionary<int, CpuSample> _cpuHistory = new Dictionary<int, CpuSample>();

        private const int TrendMaxSamples = 40;
        private readonly Dictionary<int, List<int>> _cpuTrend = new Dictionary<int, List<int>>();

        private readonly Dictionary<int, string> _instanceLabels = new Dictionary<int, string>();
        private readonly List<PendingLaunch> _pendingLaunches = new List<PendingLaunch>();

        private readonly HashSet<int> _everHadWindow = new HashSet<int>();
        private readonly Dictionary<int, DateTime> _lostWindowAt = new Dictionary<int, DateTime>();

        private readonly List<RobloxAccount> _accounts = new List<RobloxAccount>();

        // accounts.dat is there but couldn't be read. nothing gets saved until
        // a read goes through, or the empty list would replace the whole file.
        private bool _accountsUnread;

        private readonly PerformanceSettings _perf = PerformanceSettings.Load();

        private static readonly int[] FpsPresets = { 0, 30, 60, 120, 144, 240, PerformanceSettings.Unlimited };
        private static readonly ProcessPriorityClass[] PriorityPresets =
        {
            ProcessPriorityClass.BelowNormal,
            ProcessPriorityClass.Normal,
            ProcessPriorityClass.AboveNormal,
            ProcessPriorityClass.High
        };

        private TextBox _placeInput;
        private TextBox _serverInput;
        private TextBox _fpsInput;
        private System.Windows.Forms.Timer _frames;
        private System.Windows.Forms.Timer _poll;
        private readonly List<FileSystemWatcher> _settingsWatchers = new List<FileSystemWatcher>();
        private Icon _appIcon;
        private Icon _trayIconImage;
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;

        public MainForm()
        {
            Text = "Archon";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Skin.Base;
            DoubleBuffered = true;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _appIcon = AppMark.MakeIcon(64);
            _trayIconImage = AppMark.MakeIcon(32);
            Icon = _appIcon;

            // LoadAccounts is in OnShown, not here - it can call Warn, and
            // MessageBox.Show(this,...) touches Handle before this ctor finishes
            RobloxTuning.ApplyFpsCap(_perf.TargetFps);
        }

        public void HaltTimers()
        {
            try { if (_frames != null) _frames.Stop(); } catch { }
            try { if (_poll != null) _poll.Stop(); } catch { }
        }

        public void ResumeTimers()
        {
            try { if (_frames != null) _frames.Start(); } catch { }
            try { if (_poll != null) _poll.Start(); } catch { }
        }


        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            using (Graphics g = CreateGraphics()) _scale = g.DpiX / 96f;

            // Size not ClientSize - the client area already covers the whole window.
            // capped to the work area: at 150% on a 1080p screen the design height
            // is taller than the screen, and centring that pushed the title bar
            // and its buttons off the top
            Rectangle work = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Dev(1060f), work.Width), Math.Min(Dev(740f), work.Height));
            MinimumSize = new Size(Math.Min(Dev(1000f), work.Width), Math.Min(Dev(720f), work.Height));

            // CenterScreen already ran against the old size, redo it
            Location = new Point(work.X + (work.Width - Width) / 2,
                                 work.Y + (work.Height - Height) / 2);

            ApplyWindowChrome();
            BuildPlaceInput();
            BuildTray();

            _frames = new System.Windows.Forms.Timer();
            _frames.Interval = 16;
            _frames.Tick += delegate { if (_motion.Tick()) Invalidate(); };
            _frames.Start();

            _poll = new System.Windows.Forms.Timer();
            _poll.Interval = 1500;
            _poll.Tick += delegate
            {
                RefreshInstances();

                // roblox wipes ClientAppSettings.json while starting a client
                RobloxTuning.ApplyFpsCap(_perf.TargetFps);
            };
            _poll.Start();

            WatchClientSettings();
        }

        // the poll alone tends to lose the race with the bootstrapper, which
        // resets ClientAppSettings.json just before the client reads it - and
        // with the window in the tray, where it sits while you play, the poll
        // only runs every 10s. this puts the cap back as soon as the file
        // changes. the poll stays as the fallback, eg for a bootstrapper
        // installed after startup.
        private void WatchClientSettings()
        {
            foreach (string root in RobloxTuning.VersionRoots())
            {
                try
                {
                    FileSystemWatcher w = new FileSystemWatcher(root, "ClientAppSettings.json");
                    w.IncludeSubdirectories = true;
                    w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;

                    // raised on an io thread
                    FileSystemEventHandler touched = delegate { Post(new MethodInvoker(ReapplyFpsCap)); };
                    w.Changed += touched;
                    w.Created += touched;
                    w.Renamed += delegate { Post(new MethodInvoker(ReapplyFpsCap)); };

                    w.EnableRaisingEvents = true;
                    _settingsWatchers.Add(w);
                }
                catch { /* the poll still covers this install */ }
            }
        }

        private void ReapplyFpsCap()
        {
            RobloxTuning.ForgetVersionFolders();
            RobloxTuning.ApplyFpsCap(_perf.TargetFps);
        }

        private void ApplyWindowChrome()
        {
            try
            {
                int on = 1;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);

                int round = Native.DWMWCP_ROUND;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);

                // COLORREF is BGR, not RGB like everything else
                int border = 0x2A << 16 | 0x26 << 8 | 0x22;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private void BuildPlaceInput()
        {
            _placeInput = new TextBox();
            _placeInput.BorderStyle = BorderStyle.None;
            _placeInput.BackColor = Skin.Sunken;
            _placeInput.ForeColor = Skin.TextHi;
            _placeInput.Font = new Font(Skin.Body.FontFamily, 13f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            // room for a whole game or private server link, not just the id -
            // MaxLength cuts a paste short too, and a cut link doesn't parse
            _placeInput.MaxLength = 400;
            _placeInput.Text = LoadLastPlaceId();
            _placeInput.Visible = false;
            // Launch's enabled state is worked out at paint time from these boxes
            _placeInput.TextChanged += delegate { Invalidate(); };
            Controls.Add(_placeInput);

            _serverInput = new TextBox();
            _serverInput.BorderStyle = BorderStyle.None;
            _serverInput.BackColor = Skin.Sunken;
            _serverInput.ForeColor = Skin.TextHi;
            _serverInput.Font = new Font(Skin.Body.FontFamily, 13f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _serverInput.MaxLength = 400;
            _serverInput.Visible = false;
            _serverInput.TextChanged += delegate { Invalidate(); };
            Controls.Add(_serverInput);

            _fpsInput = new TextBox();
            _fpsInput.BorderStyle = BorderStyle.None;
            _fpsInput.BackColor = Skin.Sunken;
            _fpsInput.ForeColor = Skin.TextHi;
            _fpsInput.Font = new Font(Skin.Body.FontFamily, 13f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fpsInput.MaxLength = 5;
            _fpsInput.Text = _perf.TargetFps > 0 ? _perf.TargetFps.ToString(CultureInfo.InvariantCulture) : "";
            _fpsInput.Visible = false;
            _fpsInput.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                ApplyCustomFps();
            };
            Controls.Add(_fpsInput);
        }

        private void BuildTray()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            _trayMenu.BackColor = Skin.Card;
            _trayMenu.ForeColor = Skin.TextHi;
            _trayMenu.Items.Add("Show window", null, delegate { RestoreWindow(); });
            _trayMenu.Items.Add("-");
            _trayMenu.Items.Add("Toggle multi-instance", null, delegate { Toggle(); });
            _trayMenu.Items.Add("-");
            _trayMenu.Items.Add("Exit", null, delegate { ExitApplication(); });

            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "Archon";
            _trayIcon.Icon = _trayIconImage;
            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += delegate { RestoreWindow(); };
        }

        private int Dev(float logical) { return (int)Math.Round(logical * _scale); }
        private float LogicalW { get { return ClientSize.Width / _scale; } }
        private float LogicalH { get { return ClientSize.Height / _scale; } }

        // free handles first, then acquire - the other order fails if roblox is running

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_bootstrapped) return;
            _bootstrapped = true;

            LoadAccounts();

            if (CountRoblox() > 0) BeginFreeHandles(true);
            else AcquireAndUpdate();

            RefreshInstances();
        }

        private void AcquireAndUpdate()
        {
            _singleton.Acquire();
            Invalidate();
        }

        private static HashSet<int> CurrentRobloxPids()
        {
            HashSet<int> pids = new HashSet<int>();
            for (int i = 0; i < ProcessNames.Length; i++)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(ProcessNames[i]); }
                catch { continue; }

                foreach (Process p in procs) { pids.Add(p.Id); p.Dispose(); }
            }
            return pids;
        }

        private static int CountRoblox()
        {
            return CurrentRobloxPids().Count;
        }

        // WM_NCCALCSIZE just moves the client area over the real frame, so the
        // shadow, aero snap and win11 rounded corners stay

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                m.Result = IntPtr.Zero;
                return;
            }

            if (m.Msg == Native.WM_NCHITTEST)
            {
                long lp = m.LParam.ToInt64();
                Point screen = new Point(unchecked((short)lp), unchecked((short)(lp >> 16)));
                m.Result = (IntPtr)HitTest(PointToClient(screen));
                return;
            }

            if (m.Msg == Native.WM_NCMOUSELEAVE && _hot.StartsWith("win."))
            {
                _hot = "";
                Invalidate();
            }

            base.WndProc(ref m);
        }

        private int HitTest(Point p)
        {
            int grip = Dev(6f);
            bool left = p.X <= grip;
            bool right = p.X >= ClientSize.Width - grip;
            bool top = p.Y <= grip;
            bool bottom = p.Y >= ClientSize.Height - grip;

            if (top && left) return Native.HTTOPLEFT;
            if (top && right) return Native.HTTOPRIGHT;
            if (bottom && left) return Native.HTBOTTOMLEFT;
            if (bottom && right) return Native.HTBOTTOMRIGHT;
            if (left) return Native.HTLEFT;
            if (right) return Native.HTRIGHT;
            if (top) return Native.HTTOP;
            if (bottom) return Native.HTBOTTOM;

            PointF l = new PointF(p.X / _scale, p.Y / _scale);
            if (l.Y < TitleH)
            {
                if (CloseRect.Contains(l) || MinRect.Contains(l) || InfoRect.Contains(l)) return Native.HTCLIENT;
                return Native.HTCAPTION;
            }
            return Native.HTCLIENT;
        }

        private RectangleF CloseRect
        {
            get { return new RectangleF(LogicalW - 14f - 34f, (TitleH - 26f) / 2f, 34f, 26f); }
        }

        private RectangleF MinRect
        {
            get { return new RectangleF(LogicalW - 14f - 72f, (TitleH - 26f) / 2f, 34f, 26f); }
        }

        // extra gap left of MinRect so this doesn't read as a third window control
        private RectangleF InfoRect
        {
            get { return new RectangleF(LogicalW - 14f - 72f - 14f - 26f, (TitleH - 26f) / 2f, 26f, 26f); }
        }

        // set while painting a scrolling list so spots get clipped to it
        private RectangleF _spotClip = RectangleF.Empty;

        private void Spot(string id, RectangleF r)
        {
            // without this a half-scrolled card's offscreen Focus button beats the tab strip
            if (!_spotClip.IsEmpty)
            {
                r = RectangleF.Intersect(r, _spotClip);
                if (r.Width <= 0.5f || r.Height <= 0.5f) return;
            }

            _spots.Add(new KeyValuePair<string, RectangleF>(id, r));
        }

        private float Hover(string id)
        {
            return _motion.Of("h." + id, _hot == id ? 1f : 0f, 0.30f);
        }

        private string SpotAt(Point device)
        {
            PointF l = new PointF(device.X / _scale, device.Y / _scale);
            for (int i = _spots.Count - 1; i >= 0; i--)
                if (_spots[i].Value.Contains(l)) return _spots[i].Key;
            return "";
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string now = SpotAt(e.Location);
            if (now == _hot) return;

            _hot = now;
            Cursor = now.Length > 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot.Length == 0) return;
            _hot = "";
            Cursor = Cursors.Default;
            Invalidate();
        }

        // left button only, like real buttons. otherwise a right or middle
        // click on the switch turns multi-instance off with no prompt
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _pressed = SpotAt(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            string id = SpotAt(e.Location);
            string was = _pressed;
            _pressed = "";
            if (id.Length > 0 && id == was) Activate(id);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float delta = -e.Delta / 3f;

            // only the two tabs with a list - not else, or Performance scrolls the accounts behind it
            if (_tab == 0)
                _instanceScroll = ClampScroll(_instanceScroll + delta, _instanceContentH, _instanceViewH);
            else if (_tab == 1)
                _accountScroll = ClampScroll(_accountScroll + delta, _accountContentH, _accountViewH);
            else
                return;

            Invalidate();
        }

        private void PaintScrollHint(Graphics g, RectangleF view, float contentH, float viewH, float scroll)
        {
            if (contentH <= viewH + 1f || viewH <= 0f) return;

            float w = 3f;
            float x = view.Right - w;
            float minThumb = 24f;

            float thumbH = Math.Max(minThumb, viewH * (viewH / contentH));
            float travel = viewH - thumbH;
            float max = Math.Max(1f, contentH - viewH);
            float y = view.Y + travel * (scroll / max);

            using (SolidBrush track = new SolidBrush(Color.FromArgb(40, Skin.TextLo)))
                g.FillRectangle(track, x, view.Y, w, viewH);

            using (SolidBrush thumb = new SolidBrush(Color.FromArgb(150, Skin.TextLo)))
                g.FillRectangle(thumb, x, y, w, thumbH);
        }

        private static float ClampScroll(float scroll, float contentH, float viewH)
        {
            float max = Math.Max(0f, contentH - viewH);
            if (scroll < 0f) return 0f;
            if (scroll > max) return max;
            return scroll;
        }

        private void Activate(string id)
        {
            if (id == "win.close") { Close(); return; }
            if (id == "win.min") { WindowState = FormWindowState.Minimized; return; }
            if (id == "info") { _showCredits = !_showCredits; return; }
            if (id == "credits.dismiss") { _showCredits = false; return; }
            if (id == "credits.tiktok") { _showCredits = false; OpenUrl(TikTokUrl); return; }
            if (id == "credits.github") { _showCredits = false; OpenUrl(GitHubUrl); return; }
            if (id == "switch") { Toggle(); return; }
            if (id == "tab.0") { _tab = 0; return; }
            if (id == "tab.1") { _tab = 1; return; }
            if (id == "tab.2") { _tab = 2; return; }
            if (id == "free") { FreeHandlesClicked(); return; }
            if (id == "cleanup") { CleanupOrphans(); return; }
            if (id == "launch") { LaunchSelected(); return; }
            if (id == "acct.add") { AddAccount(); return; }
            if (id == "acct.remove") { RemoveAccount(); return; }
            if (id == "perf.cores.minus") { AdjustCoreLimit(-1); return; }
            if (id == "perf.cores.plus") { AdjustCoreLimit(1); return; }
            if (id == "perf.fps.custom") { ApplyCustomFps(); return; }

            // pid, not index - a re-sort can land between paint and click
            if (id.StartsWith("card.")) { _selInstancePid = ParseIndex(id, 5); return; }
            if (id.StartsWith("focus."))
            {
                int pid = ParseIndex(id, 6);
                _selInstancePid = pid;
                FocusInstance(pid);
                return;
            }
            if (id.StartsWith("more.")) { ShowInstanceMenu(ParseIndex(id, 5)); return; }

            if (id.StartsWith("perf.fps."))
            {
                int fps;
                if (int.TryParse(id.Substring(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out fps))
                    SetFpsCap(fps);
                return;
            }
            if (id.StartsWith("perf.pri."))
            {
                int idx = ParseIndex(id, 9);
                if (idx >= 0 && idx < PriorityPresets.Length) SetPriority(PriorityPresets[idx]);
                return;
            }

            if (id.StartsWith("acct."))
            {
                int idx;
                if (int.TryParse(id.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                    _selAccount = idx;
            }
        }

        private void SetFpsCap(int fps)
        {
            _perf.TargetFps = fps;
            _perf.Save();
            RobloxTuning.ApplyFpsCap(fps);
            _fpsInput.Text = fps > 0 ? fps.ToString(CultureInfo.InvariantCulture) : "";
            Invalidate();
        }

        private void ApplyCustomFps()
        {
            int fps;
            if (!int.TryParse(_fpsInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out fps))
                return;
            if (fps < 1) return;
            if (fps > PerformanceSettings.Unlimited) fps = PerformanceSettings.Unlimited;

            SetFpsCap(fps);
        }

        private void SetPriority(ProcessPriorityClass priority)
        {
            _perf.Priority = priority;
            _perf.Save();
            Invalidate();
        }

        private void AdjustCoreLimit(int delta)
        {
            int totalCores = Environment.ProcessorCount;
            int current = _perf.CoreLimit > 0 && _perf.CoreLimit < totalCores ? _perf.CoreLimit : totalCores;

            int next = current + delta;
            if (next < 1) next = 1;
            if (next > totalCores) next = totalCores;

            _perf.CoreLimit = next >= totalCores ? 0 : next;
            _perf.Save();
            Invalidate();
        }

        private static int ParseIndex(string id, int skip)
        {
            int v;
            int.TryParse(id.Substring(skip), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
            return v;
        }

        private void FocusInstance(int pid)
        {
            if (pid <= 0) return;

            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    IntPtr h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) return;
                    if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
                    Native.SetForegroundWindow(h);
                }
            }
            catch (ArgumentException) { /* already closed */ }
            catch (InvalidOperationException) { }
        }

        private void ShowInstanceMenu(int pid)
        {
            if (pid <= 0) return;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            menu.BackColor = Skin.Card;
            menu.ForeColor = Skin.TextHi;

            ToolStripMenuItem rename = new ToolStripMenuItem("Rename this client");
            rename.Click += delegate { RenameInstance(pid); };
            menu.Items.Add(rename);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem close = new ToolStripMenuItem("Close this client");
            close.Click += delegate { CloseInstance(pid); };
            menu.Items.Add(close);

            menu.Show(this, PointToClient(Cursor.Position));
        }

        // for clients started outside the launcher, no way to know the account otherwise
        private void RenameInstance(int pid)
        {
            string current;
            if (!_instanceLabels.TryGetValue(pid, out current)) current = "";

            string value = TextPrompt.Show(this, "Rename client",
                "Name for the client running as PID " +
                pid.ToString(CultureInfo.InvariantCulture) + ":", current);
            if (value == null) return;

            value = value.Trim();
            if (value.Length == 0) _instanceLabels.Remove(pid);
            else _instanceLabels[pid] = value;

            RefreshInstances();
        }

        private void CloseInstance(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    DateTime startedAt = StartTimeOf(p);

                    DialogResult answer = MessageBox.Show(this,
                        "Close this Roblox client (PID " + pid.ToString(CultureInfo.InvariantCulture) + ")?",
                        "Close client", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes) return;

                    // gone or replaced while the prompt was up, nothing to close
                    if (StillSameProcess(p, startedAt)) p.Kill();
                }
            }
            catch (ArgumentException)
            {
            }
            catch (Exception ex)
            {
                Warn("Could not close that client", ex.Message);
            }
            RefreshInstances();
        }

        // a confirm prompt can sit open for minutes, and Kill() goes by pid. if
        // the client exits meanwhile windows can hand that pid to a brand new
        // process, which would get killed instead. same pid and same start
        // time means it's still the process the user said yes to.
        private static DateTime StartTimeOf(Process p)
        {
            try { return p.StartTime; }
            catch { return DateTime.MinValue; }
        }

        private static bool StillSameProcess(Process p, DateTime startedAt)
        {
            // unreadable before the prompt too, so nothing to compare against
            if (startedAt == DateTime.MinValue) return true;

            try { return !p.HasExited && p.StartTime == startedAt; }
            catch { return false; }   // exited
        }


        private void Toggle()
        {
            if (_busy) return;

            if (_singleton.AnyOwned)
            {
                _singleton.Release();
                Invalidate();
            }
            else if (CountRoblox() > 0)
            {
                BeginFreeHandles(true);
            }
            else
            {
                AcquireAndUpdate();
            }
        }

        private void FreeHandlesClicked()
        {
            if (_busy) return;
            if (CountRoblox() == 0)
            {
                Info("No Roblox processes are running, so there is nothing to free.");
                return;
            }
            BeginFreeHandles(!_singleton.AnyOwned);
        }

        // must not run on the ui thread, walks the whole handle table
        private void BeginFreeHandles(bool acquireAfter)
        {
            if (_busy) return;
            _busy = true;
            Invalidate();

            ThreadPool.QueueUserWorkItem(delegate
            {
                HandleCloseResult result;
                try
                {
                    result = RobloxHandleCloser.Close(ProcessNames);
                }
                catch (Exception ex)
                {
                    result = new HandleCloseResult();
                    result.Error = ex.Message;
                }

                Post(new Action<HandleCloseResult, bool>(OnHandlesFreed), result, acquireAfter);
            });
        }

        private void OnHandlesFreed(HandleCloseResult result, bool acquireAfter)
        {
            _busy = false;

            if (!string.IsNullOrEmpty(result.Error))
            {
                Warn("Handle scan failed", result.Error);
            }
            else if (result.OpenFailures > 0)
            {
                // any process we couldn't open counts, even if others worked
                Warn(result.Closed > 0 ? "Some clients could not be freed" : "Access denied",
                     "Could not open " + result.OpenFailures + " Roblox process(es)" +
                     (result.Closed > 0
                        ? ", though " + result.Closed + " handle(s) were closed on the rest."
                        : ".") +
                     "\r\n\r\nRestart this app as Administrator and try again.");
            }

            if (acquireAfter) AcquireAndUpdate();
            else Invalidate();
        }

        // always list the pids and ask first - a starting client looks the same as a leftover
        private void CleanupOrphans()
        {
            if (_busy) return;

            List<Process> all = new List<Process>();
            try
            {
                for (int i = 0; i < ProcessNames.Length; i++)
                    all.AddRange(Process.GetProcessesByName(ProcessNames[i]));

                List<Process> orphans = new List<Process>();
                foreach (Process p in all)
                {
                    bool hasWindow;
                    try { hasWindow = p.MainWindowHandle != IntPtr.Zero; }
                    catch { hasWindow = false; }
                    if (!hasWindow) orphans.Add(p);
                }

                if (orphans.Count == 0)
                {
                    Info("No windowless Roblox processes to clean up.");
                    RefreshInstances();
                    return;
                }

                // read now, compared after the prompt - see StartTimeOf
                DateTime[] startedAt = new DateTime[orphans.Count];
                for (int i = 0; i < orphans.Count; i++) startedAt[i] = StartTimeOf(orphans[i]);

                System.Text.StringBuilder detail = new System.Text.StringBuilder();
                foreach (Process p in orphans)
                {
                    detail.Append("    PID ").Append(p.Id);
                    try
                    {
                        detail.Append("   ")
                              .Append((p.WorkingSet64 / 1024 / 1024).ToString(CultureInfo.InvariantCulture))
                              .Append(" MB   ")
                              .Append(p.TotalProcessorTime.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                              .Append("s CPU used");
                    }
                    catch { }
                    detail.AppendLine();
                }

                DialogResult answer = MessageBox.Show(this,
                    "End these Roblox processes?\r\n\r\n" + detail +
                    "\r\nThey have no window, so no game is showing for them.\r\n\r\n" +
                    "If you just clicked Launch, a client that is still starting up also has " +
                    "no window yet - wait a few seconds before doing this.",
                    "Close leftover processes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return;

                int ended = 0;
                int failed = 0;
                for (int i = 0; i < orphans.Count; i++)
                {
                    Process p = orphans[i];
                    try
                    {
                        // leave anything that exited or got replaced under the
                        // same pid while the prompt was up, and any client that
                        // was only starting and has its window by now
                        p.Refresh();
                        if (!StillSameProcess(p, startedAt[i]) || p.MainWindowHandle != IntPtr.Zero)
                            continue;

                        p.Kill();
                        ended++;
                    }
                    catch { failed++; }
                }

                if (failed > 0)
                    Warn("Some processes could not be ended",
                         "Ended " + ended + ", failed on " + failed + ".\r\n" +
                         "Those may need Administrator.");
            }
            catch (Exception ex)
            {
                Warn("Cleanup failed", ex.Message);
            }
            finally
            {
                foreach (Process p in all) p.Dispose();
                RefreshInstances();
            }
        }

        // rebuilt from a live process query every tick, never patched in place

        private void RefreshInstances()
        {
            List<Process> all = new List<Process>();
            try
            {
                for (int i = 0; i < ProcessNames.Length; i++)
                    all.AddRange(Process.GetProcessesByName(ProcessNames[i]));
            }
            catch
            {
                foreach (Process p in all) p.Dispose();
                return;
            }

            DateTime now = DateTime.Now;
            HashSet<int> seen = new HashSet<int>();
            foreach (Process p in all) seen.Add(p.Id);

            ResolvePendingLaunches(seen);

            List<InstanceRow> rows = new List<InstanceRow>(all.Count);
            int windowless = 0;

            try
            {
                foreach (Process p in all)
                {
                    bool hasWindow;
                    try { hasWindow = p.MainWindowHandle != IntPtr.Zero; }
                    catch { hasWindow = false; }

                    bool closed = false;

                    if (hasWindow)
                    {
                        _everHadWindow.Add(p.Id);
                        _lostWindowAt.Remove(p.Id);
                    }
                    else if (_everHadWindow.Contains(p.Id))
                    {
                        // only sets a label - do NOT kill here, MainWindowHandle
                        // reads zero during window recreation too. CleanupOrphans
                        // does the actual killing, with a prompt.
                        DateTime lostAt;
                        if (!_lostWindowAt.TryGetValue(p.Id, out lostAt))
                            _lostWindowAt[p.Id] = DateTime.Now;
                        else if ((DateTime.Now - lostAt).TotalSeconds >= 3.0)
                            closed = true;
                    }

                    RobloxTuning.ApplyResourceLimits(p, _perf.Priority, _perf.CoreLimit);

                    InstanceRow row = new InstanceRow();
                    row.Pid = p.Id;
                    row.HasWindow = hasWindow;
                    row.Closed = closed;

                    string label;
                    row.Label = _instanceLabels.TryGetValue(p.Id, out label) ? label : null;
                    if (!row.HasWindow) windowless++;

                    try
                    {
                        row.MemoryGb = (float)(p.WorkingSet64 / 1024.0 / 1024.0 / 1024.0);

                        TimeSpan up = DateTime.Now - p.StartTime;
                        row.Uptime = up.TotalHours >= 1
                            ? string.Format(CultureInfo.InvariantCulture, "{0}h {1}m", (int)up.TotalHours, up.Minutes)
                            : string.Format(CultureInfo.InvariantCulture, "{0}m {1}s", up.Minutes, up.Seconds);

                        row.Cpu = ComputeCpuPercent(p, now);
                        row.Trend = RecordTrend(p.Id, row.Cpu);
                    }
                    catch
                    {
                        row.Uptime = "-";
                    }

                    rows.Add(row);
                }
            }
            finally
            {
                // each Process holds an os handle
                foreach (Process p in all) p.Dispose();
            }

            DropStale(_everHadWindow, seen);
            RobloxTuning.ForgetExited(seen);
            DropStale(_lostWindowAt, seen);
            DropStale(_cpuHistory, seen);
            DropStale(_cpuTrend, seen);
            DropStale(_instanceLabels, seen);

            rows.Sort(delegate(InstanceRow a, InstanceRow b) { return a.Pid.CompareTo(b.Pid); });

            _instances = rows.ToArray();
            _windowlessCount = windowless;

            bool stillPresent = false;
            for (int i = 0; i < _instances.Length; i++)
                if (_instances[i].Pid == _selInstancePid) { stillPresent = true; break; }

            if (_instances.Length == 0) _selInstancePid = -1;
            else if (!stillPresent) _selInstancePid = _instances[0].Pid;

            Invalidate();
        }

        private int ComputeCpuPercent(Process p, DateTime now)
        {
            TimeSpan cpu = p.TotalProcessorTime;
            int percent = 0;

            CpuSample prev;
            if (_cpuHistory.TryGetValue(p.Id, out prev))
            {
                double elapsedMs = (now - prev.At).TotalMilliseconds;
                if (elapsedMs > 200.0)
                {
                    double cpuMs = (cpu - prev.Cpu).TotalMilliseconds;
                    double pct = cpuMs / (elapsedMs * Environment.ProcessorCount) * 100.0;
                    if (pct < 0) pct = 0;
                    if (pct > 100) pct = 100;
                    percent = (int)Math.Round(pct);
                }
            }

            CpuSample sample;
            sample.Cpu = cpu;
            sample.At = now;
            _cpuHistory[p.Id] = sample;

            return percent;
        }

        private List<int> RecordTrend(int pid, int cpuPercent)
        {
            List<int> trend;
            if (!_cpuTrend.TryGetValue(pid, out trend))
            {
                trend = new List<int>();
                _cpuTrend[pid] = trend;
            }

            trend.Add(cpuPercent);

            if (trend.Count > TrendMaxSamples)
                trend.RemoveRange(0, trend.Count - TrendMaxSamples);
            return trend;
        }

        // only counts if exactly one new unclaimed pid appeared - more than one
        // means the list hasn't settled, wait rather than guess. gives up after 25s.
        private void ResolvePendingLaunches(HashSet<int> seen)
        {
            if (_pendingLaunches.Count == 0) return;

            List<PendingLaunch> stillPending = new List<PendingLaunch>();
            foreach (PendingLaunch pending in _pendingLaunches)
            {
                if ((DateTime.Now - pending.StartedAt).TotalSeconds > 25) continue;

                int match = -1;
                int matchCount = 0;
                foreach (int pid in seen)
                {
                    if (pending.BaselinePids.Contains(pid) || _instanceLabels.ContainsKey(pid)) continue;
                    matchCount++;
                    match = pid;
                }

                if (matchCount == 1) _instanceLabels[match] = pending.Label;
                else stillPending.Add(pending);
            }

            _pendingLaunches.Clear();
            _pendingLaunches.AddRange(stillPending);
        }

        private static void DropStale<T>(Dictionary<int, T> dict, HashSet<int> seen)
        {
            if (dict.Count == 0) return;

            List<int> stale = null;
            foreach (int pid in dict.Keys)
            {
                if (seen.Contains(pid)) continue;
                if (stale == null) stale = new List<int>();
                stale.Add(pid);
            }
            if (stale == null) return;

            foreach (int pid in stale) dict.Remove(pid);
        }

        private static void DropStale(HashSet<int> set, HashSet<int> seen)
        {
            if (set.Count == 0) return;

            List<int> stale = null;
            foreach (int pid in set)
            {
                if (seen.Contains(pid)) continue;
                if (stale == null) stale = new List<int>();
                stale.Add(pid);
            }
            if (stale == null) return;

            foreach (int pid in stale) set.Remove(pid);
        }

        private void LoadAccounts()
        {
            _accounts.Clear();
            _accountsUnread = false;
            try { _accounts.AddRange(AccountStore.Load()); }
            catch (Exception ex)
            {
                _accountsUnread = true;
                Warn("Could not read saved accounts",
                     ex.Message + "\r\n\r\nAccounts can't be added until the file can be read, " +
                     "so the ones already saved aren't overwritten.");
            }

            if (_selAccount >= _accounts.Count) _selAccount = _accounts.Count - 1;
            if (_selAccount < 0 && _accounts.Count > 0) _selAccount = 0;
        }

        private void AddAccount()
        {
            // the lock that failed the startup read may be gone by now
            if (_accountsUnread)
            {
                LoadAccounts();
                if (_accountsUnread) return;
            }

            using (AddAccountForm dialog = new AddAccountForm())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result == null) return;

                RobloxAccount added = dialog.Result;

                for (int i = 0; i < _accounts.Count; i++)
                {
                    if (_accounts[i].UserId == added.UserId)
                    {
                        _accounts[i] = added;
                        SaveAccounts();
                        _selAccount = i;
                        Invalidate();
                        return;
                    }
                }

                _accounts.Add(added);
                SaveAccounts();
                _selAccount = _accounts.Count - 1;
                Invalidate();
            }
        }

        private void RemoveAccount()
        {
            if (_selAccount < 0 || _selAccount >= _accounts.Count) return;
            RobloxAccount a = _accounts[_selAccount];

            DialogResult answer = MessageBox.Show(this,
                "Remove " + a.Label + " from this launcher?\r\n\r\n" +
                "The saved session is deleted from this PC. The Roblox account itself is untouched.",
                "Remove account", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            _accounts.RemoveAt(_selAccount);
            SaveAccounts();

            _selAccount = -1;
            Invalidate();
        }

        private void SaveAccounts()
        {
            if (_accountsUnread) return;   // see _accountsUnread
            try { AccountStore.Save(_accounts); }
            catch (Exception ex) { Warn("Could not save accounts", ex.Message); }
        }

        private bool TryResolvePlaceId(out long placeId)
        {
            string code;
            bool isLinkCode;
            return TryResolveTarget(out placeId, out code, out isLinkCode);
        }

        // either box may hold a full private server link, not just the server box
        private bool TryResolveTarget(out long placeId, out string code, out bool isLinkCode)
        {
            placeId = 0;
            code = null;
            isLinkCode = false;

            string placeText = _placeInput.Text.Trim();
            string serverText = _serverInput.Text.Trim();

            if (!long.TryParse(placeText, NumberStyles.Integer,
                               CultureInfo.InvariantCulture, out placeId) || placeId <= 0)
            {
                placeId = 0;
                if (placeText.Length > 0)
                {
                    long linkedPlace;
                    string linkedCode;
                    bool linkedIsLink;
                    if (RobloxWeb.TryParsePrivateServerLink(placeText, out linkedPlace,
                                                            out linkedCode, out linkedIsLink))
                    {
                        if (linkedPlace > 0) placeId = linkedPlace;
                        code = linkedCode;
                        isLinkCode = linkedIsLink;
                    }
                }
            }

            if (serverText.Length > 0)
            {
                long serverPlace;
                string serverCode;
                bool serverIsLink;
                if (RobloxWeb.TryParsePrivateServerLink(serverText, out serverPlace,
                                                        out serverCode, out serverIsLink))
                {
                    if (placeId <= 0 && serverPlace > 0) placeId = serverPlace;
                    if (!string.IsNullOrEmpty(serverCode))
                    {
                        code = serverCode;
                        isLinkCode = serverIsLink;
                    }
                }
            }

            return placeId > 0;
        }

        private void LaunchSelected()
        {
            if (_busy) return;
            if (_selAccount < 0 || _selAccount >= _accounts.Count) return;
            RobloxAccount account = _accounts[_selAccount];

            long placeId;
            string accessCode;
            bool isLinkCode;
            if (!TryResolveTarget(out placeId, out accessCode, out isLinkCode))
            {
                Info("Enter the place ID of the game you want to join.\r\n\r\n" +
                     "It is the number in the game's URL:\r\n" +
                     "roblox.com/games/<place id>/name\r\n\r\n" +
                     "Pasting a private server link into that box works too.");
                return;
            }

            if (!_singleton.AnyOwned && CountRoblox() > 0)
            {
                DialogResult answer = MessageBox.Show(this,
                    "Multi-instance is not active, and Roblox is already running.\r\n\r\n" +
                    "The new client will most likely be blocked. Launch anyway?",
                    "Multi-instance is off", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return;
            }

            SaveLastPlaceId(placeId);

            _busy = true;
            Invalidate();

            // snapshotted before the new client exists, for ResolvePendingLaunches
            HashSet<int> baselinePids = CurrentRobloxPids();

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                string url = null;
                try
                {
                    string cookie = AccountStore.Unprotect(account.ProtectedCookie);
                    string ticket = RobloxWeb.GetAuthTicket(cookie, out error);
                    if (!string.IsNullOrEmpty(ticket))
                        url = RobloxWeb.BuildLaunchUrl(ticket, placeId, accessCode, isLinkCode);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                Post(new Action<string, string, string, HashSet<int>>(OnTicketReady),
                     url, error, account.Label, baselinePids);
            });
        }

        private void OnTicketReady(string launchUrl, string error, string label, HashSet<int> baselinePids)
        {
            _busy = false;

            if (string.IsNullOrEmpty(launchUrl))
            {
                Warn("Could not launch " + label,
                     string.IsNullOrEmpty(error) ? "Roblox did not return an authentication ticket." : error);
                Invalidate();
                return;
            }

            try
            {
                RobloxWeb.Launch(launchUrl);

                PendingLaunch pending = new PendingLaunch();
                pending.BaselinePids = baselinePids;
                pending.Label = label;
                pending.StartedAt = DateTime.Now;
                _pendingLaunches.Add(pending);
            }
            catch (Exception ex)
            {
                Warn("Could not start the Roblox client", ex.Message);
            }

            Invalidate();
        }

        private static string SettingsPath
        {
            get { return Path.Combine(AccountStore.Directory, "lastplace.txt"); }
        }

        private static string LoadLastPlaceId()
        {
            try
            {
                if (File.Exists(SettingsPath)) return File.ReadAllText(SettingsPath).Trim();
            }
            catch { }
            return "";
        }

        private static void SaveLastPlaceId(long placeId)
        {
            try
            {
                Directory.CreateDirectory(AccountStore.Directory);
                File.WriteAllText(SettingsPath, placeId.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }


        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.ScaleTransform(_scale, _scale);

            _spots.Clear();

            float w = LogicalW;
            float h = LogicalH;

            g.FillRectangle(Skin.Br(Skin.Base), 0f, 0f, w, h);
            PaintTitleBar(g, w);

            float y = TitleH + 22f;
            PaintHero(g, w, y);

            y += string.IsNullOrEmpty(HeroSub()) ? 58f : 78f;
            Skin.Divider(g, PadX, (float)Math.Floor(y), w - PadX * 2f, Skin.Line);

            y += 22f;
            PaintTabRow(g, w, y);

            y += 34f + 22f;
            float footerTop = h - 28f - 36f;
            RectangleF body = new RectangleF(PadX, y, w - PadX * 2f, footerTop - 24f - y);

            if (_tab == 0) PaintInstances(g, body);
            else if (_tab == 1) PaintAccounts(g, body);
            else PaintPerformance(g, body);

            PaintFooter(g, w, footerTop);

            if (_showCredits) PaintCredits(g, w, h);

            QueueInputLayout();
        }

        // recorded while painting, applied afterwards - setting Bounds/Visible
        // from inside OnPaint re-enters layout during a paint
        private Rectangle _placeBounds, _serverBounds, _fpsBounds;
        private bool _layoutQueued;

        private void QueueInputLayout()
        {
            if (_layoutQueued || !IsHandleCreated || IsDisposed) return;
            _layoutQueued = true;

            try { BeginInvoke(new MethodInvoker(ApplyInputLayout)); }
            catch (ObjectDisposedException) { _layoutQueued = false; }
            catch (InvalidOperationException) { _layoutQueued = false; }
        }

        private void ApplyInputLayout()
        {
            _layoutQueued = false;
            PlaceBox(_placeInput, _placeBounds, _tab == 1);
            PlaceBox(_serverInput, _serverBounds, _tab == 1);
            PlaceBox(_fpsInput, _fpsBounds, _tab == 2);
        }

        private static void PlaceBox(TextBox box, Rectangle bounds, bool visible)
        {
            if (box == null) return;
            if (bounds.Width > 0 && box.Bounds != bounds) box.Bounds = bounds;
            if (box.Visible != visible) box.Visible = visible;
        }

        private void PaintTitleBar(Graphics g, float w)
        {
            AppMark.Draw(g, new RectangleF(PadX, (TitleH - 23f) / 2f, 23f, 23f));

            Skin.Text(g, "Archon", Skin.SmallMed, Skin.TextMid,
                      new RectangleF(PadX + 33f, 0f, 300f, TitleH), Skin.Left);

            Spot("info", InfoRect);
            Spot("win.min", MinRect);
            Spot("win.close", CloseRect);
            Widgets.InfoButton(g, InfoRect, Hover("info"));
            Widgets.WindowButton(g, MinRect, false, Hover("win.min"));
            Widgets.WindowButton(g, CloseRect, true, Hover("win.close"));
        }

        private void PaintCredits(Graphics g, float w, float h)
        {
            // registered before the panel's own spots, so a click anywhere
            // outside the panel closes it instead of hitting whatever is behind
            Spot("credits.dismiss", new RectangleF(0f, 0f, w, h));

            const float panelW = 214f;
            const float rowH = 34f;
            const float pad = 14f;
            float panelH = pad * 2f + rowH * 3f;

            RectangleF panel = new RectangleF(w - 14f - panelW, TitleH + 6f, panelW, panelH);
            Skin.Surface(g, panel, 12f, Skin.Card, Skin.Line);

            PaintCreditRow(g, panel, pad, 0, "TikTok", "@sillyrelshy", "credits.tiktok");
            PaintCreditRow(g, panel, pad, 1, "Discord", "@sillyrelshy", null);
            PaintCreditRow(g, panel, pad, 2, "GitHub", "Relshy/archon", "credits.github");
        }

        private void PaintCreditRow(Graphics g, RectangleF panel, float pad, int index,
                                    string label, string value, string spotId)
        {
            const float rowH = 34f;
            RectangleF row = new RectangleF(panel.X, panel.Y + pad + rowH * index, panel.Width, rowH);

            if (spotId != null)
            {
                Spot(spotId, row);
                float hover = Hover(spotId);
                if (hover > 0.01f) Skin.Fill(g, row, 8f, Skin.A(Color.White, (int)(12 * hover)));
            }

            Color valueColor = spotId != null ? Skin.Mix(Skin.TextLo, Skin.TextHi, Hover(spotId)) : Skin.TextLo;

            Skin.Text(g, label, Skin.SmallMed, Skin.TextHi,
                      new RectangleF(row.X + pad, row.Y, row.Width - pad * 2f, row.Height), Skin.Left);
            Skin.Text(g, value, Skin.Small, valueColor,
                      new RectangleF(row.X + pad, row.Y, row.Width - pad * 2f, row.Height), Skin.Right);
        }

        private string HeroSub()
        {
            bool allOwned = _singleton.AllOwned;
            bool anyOwned = _singleton.AnyOwned;

            if (anyOwned && !allOwned) return _singleton.FirstError ?? "Only some of the singleton names could be held.";
            if (!anyOwned && _singleton.Attempted) return _singleton.FirstError;
            return null;
        }

        private void PaintHero(Graphics g, float w, float y)
        {
            bool allOwned = _singleton.AllOwned;
            bool anyOwned = _singleton.AnyOwned;
            float on = _motion.Of("switch", anyOwned ? 1f : 0f, 0.20f);

            string title = allOwned ? "Multi-instance is on"
                          : anyOwned ? "Multi-instance is partly on"
                          : "Multi-instance is off";
            string sub = HeroSub();

            Skin.Text(g, title, Skin.H1, Skin.TextHi,
                      new RectangleF(PadX, y - 4f, w - PadX * 2f - 200f, 38f), Skin.Left);

            if (!string.IsNullOrEmpty(sub))
                Skin.Text(g, sub, Skin.Body, Skin.TextMid,
                          new RectangleF(PadX, y + 38f, Math.Min(560f, w - PadX * 2f - 200f), 20f), Skin.Left);

            RectangleF sw = new RectangleF(w - PadX - 52f, y + 8f, 52f, 30f);
            string state = allOwned ? "ON" : anyOwned ? "PARTIAL" : "OFF";
            float labelW = Skin.TrackedWidth(g, state, Skin.Micro, 1.4f);
            Skin.Tracked(g, state, Skin.Micro, Skin.Mix(Skin.TextLo, Skin.TextHi, on),
                         sw.X - 12f - labelW, sw.Y + 9f, 1.4f);

            Spot("switch", RectangleF.Inflate(sw, 6f, 6f));
            Widgets.Switch(g, sw, on);
        }

        private void PaintTabRow(Graphics g, float w, float y)
        {
            RectangleF seg = new RectangleF(PadX, y, 315f, 34f);
            float pos = _motion.Of("tab", _tab, 0.26f);
            float segw = seg.Width / 3f;

            int hovered = -1;
            if (_hot == "tab.0") hovered = 0;
            if (_hot == "tab.1") hovered = 1;
            if (_hot == "tab.2") hovered = 2;

            // must match Widgets.Segmented's own cell maths exactly (insets by 3)
            float cellW = (seg.Width - 6f) / 3f;
            for (int t = 0; t < 3; t++)
                Spot("tab." + t.ToString(CultureInfo.InvariantCulture),
                     new RectangleF(seg.X + 3f + cellW * t, seg.Y, cellW, seg.Height));

            Widgets.Segmented(g, seg, new string[] { "Instances", "Accounts", "Performance" }, pos, hovered, 10f);

            string count;
            if (_tab == 0) count = _instances.Length.ToString(CultureInfo.InvariantCulture) + " RUNNING";
            else if (_tab == 1) count = _accounts.Count.ToString(CultureInfo.InvariantCulture) + " SAVED";
            else count = (_perf.TargetFps > 0
                ? _perf.TargetFps.ToString(CultureInfo.InvariantCulture) + " FPS"
                : "DEFAULT FPS");

            float cw = Skin.TrackedWidth(g, count, Skin.Micro, 1.6f);
            Skin.Tracked(g, count, Skin.Micro, Skin.TextLo, w - PadX - cw, y + 11f, 1.6f);
        }


        private void PaintInstances(Graphics g, RectangleF body)
        {
            _instanceViewH = body.Height;

            if (_instances.Length == 0)
            {
                Skin.Text(g, "No Roblox instances running.", Skin.Body, Skin.TextLo, body, Skin.Centre);
                _instanceContentH = 0f;
                return;
            }

            int columns = Math.Max(1, (int)((body.Width + Gap) / (CardMinW + Gap)));
            columns = Math.Min(columns, _instances.Length);
            float cw = (body.Width - Gap * (columns - 1)) / columns;
            int rows = (int)Math.Ceiling(_instances.Length / (float)columns);

            _instanceContentH = rows * CardH + (rows - 1) * Gap;
            _instanceScroll = ClampScroll(_instanceScroll, _instanceContentH, _instanceViewH);

            GraphicsState state = g.Save();
            g.SetClip(body, CombineMode.Intersect);
            _spotClip = body;

            for (int i = 0; i < _instances.Length; i++)
            {
                int col = i % columns;
                int row = i / columns;
                RectangleF r = new RectangleF(
                    body.X + (cw + Gap) * col,
                    body.Y + (CardH + Gap) * row - _instanceScroll,
                    cw, CardH);

                if (r.Bottom < body.Y - 4f || r.Y > body.Bottom + 4f) continue;

                Spot("card." + _instances[i].Pid, r);
                PaintInstanceCard(g, r, _instances[i]);
            }

            _spotClip = RectangleF.Empty;
            g.Restore(state);

            PaintScrollHint(g, body, _instanceContentH, _instanceViewH, _instanceScroll);
        }

        private void PaintInstanceCard(Graphics g, RectangleF r, InstanceRow inst)
        {
            bool selected = inst.Pid == _selInstancePid;
            float hover = Hover("card." + inst.Pid);
            float sel = _motion.Of("sel." + inst.Pid, selected ? 1f : 0f, 0.22f);

            Skin.Surface(g, r, 16f,
                         Skin.Mix(Skin.Card, Skin.CardLit, hover * 0.9f),
                         Skin.Mix(Skin.Line, Skin.TextHi, sel * 0.6f));

            float pad = 15f;
            float x = r.X + pad;
            float inner = r.Width - pad * 2f;
            float y = r.Y + pad;

            Skin.Dot(g, x + 4f, y + 10f, 7f, inst.HasWindow ? Skin.TextHi : Skin.TextFaint);

            // a client started from the roblox website carries nothing naming the signed-in user
            bool named = !string.IsNullOrEmpty(inst.Label);
            string displayName = named ? inst.Label : "Unknown account";
            Skin.Text(g, displayName, Skin.BodyMed, named ? Skin.TextHi : Skin.TextMid,
                      new RectangleF(x + 16f, y, inner - 100f, 20f), Skin.Left);

            string pill = inst.HasWindow ? "Running" : (inst.Closed ? "Leftover" : "Starting");
            float pw = Skin.Width(g, pill, Skin.Micro) + 20f;
            Widgets.Pill(g, new RectangleF(r.Right - pad - pw, y + 1f, pw, 19f), pill, inst.HasWindow);

            y += 22f;

            string id = "PID " + inst.Pid.ToString(CultureInfo.InvariantCulture);
            float sx = x + 16f;
            Skin.Text(g, id, Skin.Mono, Skin.TextFaint, new RectangleF(sx, y, 100f, 14f), Skin.Left);
            sx += Skin.Width(g, id, Skin.Mono) + 7f;
            g.FillEllipse(Skin.Br(Skin.TextFaint), sx, y + 6.5f, 2.5f, 2.5f);
            Skin.Text(g, inst.Uptime + " up", Skin.Mono, Skin.TextFaint,
                      new RectangleF(sx + 10f, y, 100f, 14f), Skin.Left);

            y += 22f;

            RectangleF panel = new RectangleF(x, y, inner, PanelH);
            Skin.Trend(g, panel, 11f, inst.Trend, inst.HasWindow);
            Skin.Tracked(g, "CPU · LAST 60S", Skin.Micro, Skin.TextFaint,
                         panel.X + 10f, panel.Y + 8f, 1.2f);
            y += PanelH + 20f;

            Metric(g, x, y, inner, "CPU",
                   inst.Cpu.ToString(CultureInfo.InvariantCulture) + "%", inst.Cpu / 100f);
            y += 42f;
            Metric(g, x, y, inner, "Memory",
                   inst.MemoryGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB", inst.MemoryGb / 3f);

            float bh = 38f;
            float by = r.Bottom - pad - bh;
            float mw = 44f;
            RectangleF focus = new RectangleF(x, by, inner - mw - 9f, bh);
            RectangleF more = new RectangleF(focus.Right + 9f, by, mw, bh);

            Spot("focus." + inst.Pid, focus);
            Spot("more." + inst.Pid, more);

            Widgets.Button(g, focus, "Focus", selected ? Btn.Primary : Btn.Secondary,
                           Hover("focus." + inst.Pid), inst.HasWindow, 10f);
            Widgets.More(g, more, Hover("more." + inst.Pid), 10f);
        }

        private void Metric(Graphics g, float x, float y, float w, string label, string value, float amount)
        {
            Skin.Text(g, label, Skin.Small, Skin.TextMid, new RectangleF(x, y, w * 0.6f, 16f), Skin.Left);
            Skin.Text(g, value, Skin.Num, Skin.TextHi,
                      new RectangleF(x, y - 1f, w, 16f), Skin.Right);

            Skin.Bar(g, new RectangleF(x, y + 23f, w, 3f), amount, Skin.Rgb(0xC8, 0xC8, 0xC8));
        }

        private void PaintAccounts(Graphics g, RectangleF body)
        {
            float detailW = 340f;
            RectangleF list = new RectangleF(body.X, body.Y, body.Width - detailW - 20f, body.Height);
            RectangleF detail = new RectangleF(list.Right + 20f, body.Y, detailW, body.Height);

            Skin.Surface(g, list, 16f, Skin.Card, Skin.Line);

            float rowH = 62f;
            _accountViewH = list.Height - 10f;
            _accountContentH = _accounts.Count * rowH;
            _accountScroll = ClampScroll(_accountScroll, _accountContentH, _accountViewH);

            if (_accounts.Count == 0)
            {
                Skin.Text(g, "No saved accounts yet. Add one to get started.",
                          Skin.Body, Skin.TextLo, list, Skin.Centre);
            }
            else
            {
                GraphicsState state = g.Save();
                RectangleF clip = Skin.Inset(list, 1f);
                g.SetClip(clip, CombineMode.Intersect);
                _spotClip = clip;

                for (int i = 0; i < _accounts.Count; i++)
                {
                    RectangleF row = new RectangleF(
                        list.X + 1f, list.Y + 10f + rowH * i - _accountScroll, list.Width - 2f, rowH);
                    if (row.Bottom < list.Y || row.Y > list.Bottom) continue;

                    Spot("acct." + i, row);
                    PaintAccountRow(g, row, _accounts[i], i);
                }

                _spotClip = RectangleF.Empty;
                g.Restore(state);

                PaintScrollHint(g, new RectangleF(list.X, list.Y + 10f, list.Width, _accountViewH),
                                _accountContentH, _accountViewH, _accountScroll);
            }

            PaintAccountDetail(g, detail);
        }

        private static string Initial(RobloxAccount a)
        {
            return string.IsNullOrEmpty(a.Label) ? "?" : a.Label.Substring(0, 1).ToUpperInvariant();
        }

        private Image AvatarFor(long userId)
        {
            Image cached = AvatarCache.Get(userId);
            if (cached == null) AvatarCache.BeginFetch(userId, delegate { Post(new MethodInvoker(Invalidate)); });
            return cached;
        }

        private void PaintAccountRow(Graphics g, RectangleF r, RobloxAccount a, int index)
        {
            bool selected = index == _selAccount;
            float hover = Hover("acct." + index);
            float sel = _motion.Of("asel." + index, selected ? 1f : 0f, 0.24f);

            RectangleF inner = new RectangleF(r.X + 8f, r.Y + 3f, r.Width - 16f, r.Height - 6f);

            if (hover > 0.01f || sel > 0.01f)
            {
                Skin.Fill(g, inner, 11f,
                          Skin.Mix(Skin.A(Color.White, (int)(12f * hover)), Skin.A(Skin.Accent, 26), sel));
            }
            if (sel > 0.01f)
            {
                Skin.Stroke(g, inner, 11f, Skin.A(Skin.Accent, (int)(120f * sel)), 1f);
                Skin.Fill(g, new RectangleF(inner.X + 1f, inner.Y + 13f, 2.5f, inner.Height - 26f),
                          1.25f, Skin.A(Skin.Accent, (int)(255f * sel)));
            }

            float av = 34f;
            Widgets.Avatar(g, new RectangleF(inner.X + 14f, inner.Y + (inner.Height - av) / 2f, av, av),
                           Initial(a), sel, AvatarFor(a.UserId));

            float tx = inner.X + 14f + av + 13f;
            Skin.Text(g, a.Label, Skin.BodyMed, Skin.TextHi,
                      new RectangleF(tx, inner.Y + 12f, inner.Width - 200f, 18f), Skin.Left);
            Skin.Text(g, "@" + a.Username, Skin.Small, Skin.TextLo,
                      new RectangleF(tx, inner.Y + 30f, inner.Width - 200f, 16f), Skin.Left);

            Skin.Text(g, a.UserId.ToString(CultureInfo.InvariantCulture), Skin.Mono, Skin.TextFaint,
                      new RectangleF(inner.Right - 130f, inner.Y, 116f, inner.Height), Skin.Right);
        }

        private void PaintAccountDetail(Graphics g, RectangleF r)
        {
            Skin.Surface(g, r, 16f, Skin.Card, Skin.Line);

            float pad = 22f;
            float x = r.X + pad;
            float w = r.Width - pad * 2f;

            bool hasSelection = _selAccount >= 0 && _selAccount < _accounts.Count;
            RobloxAccount a = hasSelection ? _accounts[_selAccount] : null;

            if (hasSelection)
            {
                float y = r.Y + 22f;
                float av = 48f;
                Widgets.Avatar(g, new RectangleF(x, y, av, av), Initial(a), 1f, AvatarFor(a.UserId));

                Skin.Text(g, a.Label, Skin.H3, Skin.TextHi,
                          new RectangleF(x + av + 14f, y + 7f, w - av - 14f, 22f), Skin.Left);
                Skin.Text(g, "@" + a.Username, Skin.Small, Skin.TextLo,
                          new RectangleF(x + av + 14f, y + 27f, w - av - 14f, 16f), Skin.Left);

                y += av + 18f;
                Skin.Divider(g, x, (float)Math.Floor(y), w, Skin.LineSoft);
                y += 15f;

                MetaRow(g, x, y, w, "USER ID", a.UserId.ToString(CultureInfo.InvariantCulture));
                y += 26f;
                MetaRow(g, x, y, w, "SESSION", "Signed in");
                y += 24f;

                Skin.Divider(g, x, (float)Math.Floor(y), w, Skin.LineSoft);
            }
            else
            {
                Skin.Text(g, "Select an account to launch it.", Skin.Body, Skin.TextLo,
                          new RectangleF(x, r.Y + 30f, w, 40f), Skin.Para);
            }

            float by = r.Bottom - pad;

            RectangleF add = new RectangleF(x, by - 34f, w / 2f - 5f, 34f);
            RectangleF remove = new RectangleF(x + w / 2f + 5f, by - 34f, w / 2f - 5f, 34f);
            Spot("acct.add", add);
            Spot("acct.remove", remove);
            Widgets.Button(g, add, "Add account", Btn.Secondary, Hover("acct.add"), !_busy, 9f);
            Widgets.Button(g, remove, "Remove", Btn.Secondary, Hover("acct.remove"), hasSelection && !_busy, 9f);

            long placeId;
            bool validPlace = TryResolvePlaceId(out placeId);
            bool canLaunch = hasSelection && validPlace && !_busy;

            RectangleF launch = new RectangleF(x, add.Y - 10f - 40f, w, 40f);
            Spot("launch", launch);
            Widgets.Button(g, launch, _busy ? "Getting ticket…" : "Launch account",
                           Btn.Primary, Hover("launch"), canLaunch, 10f);

            Skin.Text(g, "Place ID from the URL. Private server link works too.",
                      Skin.Small, Skin.TextFaint, new RectangleF(x, launch.Y - 12f - 14f, w, 14f), Skin.Left);

            RectangleF serverField = new RectangleF(x, launch.Y - 12f - 14f - 6f - 34f, w, 34f);
            Skin.Fill(g, serverField, 9f, Skin.Sunken);
            Skin.Stroke(g, serverField, 9f, Skin.Line, 1f);

            // PreferredHeight is how a single-line TextBox reports its own font-driven height
            int sfh = _serverInput.PreferredHeight;
            _serverBounds = new Rectangle(
                Dev(serverField.X + 12f), Dev(serverField.Y) + (Dev(serverField.Height) - sfh) / 2,
                Dev(serverField.Width - 24f), sfh);

            Skin.Tracked(g, "PRIVATE SERVER (OPTIONAL)", Skin.Micro, Skin.TextFaint,
                        x, serverField.Y - 16f, 1.4f);

            RectangleF field = new RectangleF(x, serverField.Y - 16f - 6f - 36f, w, 36f);
            Skin.Fill(g, field, 9f, Skin.Sunken);
            Skin.Stroke(g, field, 9f, Skin.Line, 1f);

            int fh = _placeInput.PreferredHeight;
            _placeBounds = new Rectangle(
                Dev(field.X + 12f), Dev(field.Y) + (Dev(field.Height) - fh) / 2,
                Dev(field.Width - 24f), fh);

            Skin.Tracked(g, "PLACE ID", Skin.Micro, Skin.TextFaint, x, field.Y - 18f, 1.6f);
        }

        private void MetaRow(Graphics g, float x, float y, float w, string label, string value)
        {
            Skin.Tracked(g, label, Skin.Micro, Skin.TextFaint, x, y, 1.6f);
            Skin.Text(g, value, Skin.Mono, Skin.TextMid, new RectangleF(x, y - 3f, w, 16f), Skin.Right);
        }


        private void PaintPerformance(Graphics g, RectangleF body)
        {
            Skin.Surface(g, body, 16f, Skin.Card, Skin.Line);

            float pad = 24f;
            float x = body.X + pad;
            float w = body.Width - pad * 2f;
            float y = body.Y + 22f;

            Skin.Tracked(g, "FRAME RATE", Skin.Micro, Skin.TextFaint, x, y, 1.6f);
            y += 24f;

            float cx = x;
            for (int i = 0; i < FpsPresets.Length; i++)
            {
                int fps = FpsPresets[i];
                string label = fps == 0 ? "Default"
                              : fps == PerformanceSettings.Unlimited ? "Unlimited"
                              : fps.ToString(CultureInfo.InvariantCulture);
                float chipW = fps == 0 || fps == PerformanceSettings.Unlimited ? 92f : 64f;

                RectangleF chip = new RectangleF(cx, y, chipW, 34f);
                string id = "perf.fps." + fps.ToString(CultureInfo.InvariantCulture);
                Spot(id, chip);
                Widgets.Button(g, chip, label, _perf.TargetFps == fps ? Btn.Primary : Btn.Secondary,
                               Hover(id), true, 9f);
                cx += chipW + 10f;
            }

            cx += 10f;
            RectangleF fpsBox = new RectangleF(cx, y, 80f, 34f);
            Skin.Fill(g, fpsBox, 9f, Skin.Sunken);
            Skin.Stroke(g, fpsBox, 9f, Skin.Line, 1f);

            int ifh = _fpsInput.PreferredHeight;
            _fpsBounds = new Rectangle(
                Dev(fpsBox.X + 10f), Dev(fpsBox.Y) + (Dev(fpsBox.Height) - ifh) / 2,
                Dev(fpsBox.Width - 20f), ifh);

            RectangleF setBtn = new RectangleF(fpsBox.Right + 8f, y, 52f, 34f);
            Spot("perf.fps.custom", setBtn);
            Widgets.Button(g, setBtn, "Set", Btn.Secondary, Hover("perf.fps.custom"), true, 9f);

            y += 34f + 34f;

            Skin.Tracked(g, "CPU PRIORITY", Skin.Micro, Skin.TextFaint, x, y, 1.6f);
            y += 24f;

            string[] priorityLabels = { "Below normal", "Normal", "Above normal", "High" };
            cx = x;
            for (int i = 0; i < PriorityPresets.Length; i++)
            {
                RectangleF chip = new RectangleF(cx, y, 128f, 34f);
                string id = "perf.pri." + i.ToString(CultureInfo.InvariantCulture);
                Spot(id, chip);
                Widgets.Button(g, chip, priorityLabels[i],
                               _perf.Priority == PriorityPresets[i] ? Btn.Primary : Btn.Secondary,
                               Hover(id), true, 9f);
                cx += 128f + 10f;
            }
            y += 34f + 34f;

            Skin.Tracked(g, "CPU CORES PER CLIENT", Skin.Micro, Skin.TextFaint, x, y, 1.6f);
            y += 24f;

            int totalCores = Environment.ProcessorCount;
            int coreValue = _perf.CoreLimit > 0 && _perf.CoreLimit < totalCores ? _perf.CoreLimit : totalCores;
            string coreLabel = coreValue >= totalCores
                ? "All (" + totalCores.ToString(CultureInfo.InvariantCulture) + ")"
                : coreValue.ToString(CultureInfo.InvariantCulture);

            RectangleF minus = new RectangleF(x, y, 34f, 34f);
            RectangleF valueBox = new RectangleF(minus.Right, y, 92f, 34f);
            RectangleF plus = new RectangleF(valueBox.Right, y, 34f, 34f);

            Spot("perf.cores.minus", minus);
            Spot("perf.cores.plus", plus);
            Widgets.Button(g, minus, "-", Btn.Secondary, Hover("perf.cores.minus"), coreValue > 1, 9f);
            Skin.Surface(g, valueBox, 9f, Skin.Sunken, Skin.Line);
            Skin.Text(g, coreLabel, Skin.SmallMed, Skin.TextHi, valueBox, Skin.Centre);
            Widgets.Button(g, plus, "+", Btn.Secondary, Hover("perf.cores.plus"), coreValue < totalCores, 9f);

            y += 34f + 20f;
            Skin.Divider(g, x, (float)Math.Floor(y), w, Skin.LineSoft);
            y += 16f;

            Skin.Text(g, "Applies live to every client this app is running, including ones already open.\r\n" +
                         "Frame rate takes effect the next time a client starts.",
                      Skin.Small, Skin.TextFaint, new RectangleF(x, y, w, 32f), Skin.Para);
        }


        private void PaintFooter(Graphics g, float w, float y)
        {
            string hint;
            if (_tab == 0)
                hint = "Keep this window open while you play. Closing it hands the names back to Roblox.\r\n" +
                       "Leftover means a closed game whose process is still up. Nothing here is ended without asking.";
            else if (_tab == 1)
                hint = "Saved sessions go straight into a game without a login.\r\n" +
                       "Turn multi-instance on first, then start each account in turn.";
            else
                hint = "Frame rate is set through Roblox's own client settings file, not by patching anything.\r\n" +
                       "Priority and core limits apply to every Roblox client this app can see.";

            Skin.Text(g, hint, Skin.Small, Skin.TextLo,
                      new RectangleF(PadX, y + 1f, w * 0.52f, 40f), Skin.Para);

            if (_tab != 0) return;

            string cleanupLabel = _windowlessCount == 0
                ? "No leftover processes"
                : string.Format(CultureInfo.InvariantCulture, "Close {0} leftover process{1}",
                                _windowlessCount, _windowlessCount == 1 ? "" : "es");

            RectangleF cleanup = new RectangleF(w - PadX - 200f, y, 200f, 36f);
            RectangleF free = new RectangleF(cleanup.X - 10f - 178f, y, 178f, 36f);

            Spot("free", free);
            Spot("cleanup", cleanup);

            Widgets.Button(g, free, "Free handles from Roblox", Btn.Secondary, Hover("free"), !_busy, 9f);
            Widgets.Button(g, cleanup, cleanupLabel, Btn.Secondary, Hover("cleanup"),
                           _windowlessCount > 0 && !_busy, 9f);
        }


        private void Info(string message)
        {
            MessageBox.Show(this, message, "Archon",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(url); } catch { }
        }

        private void Warn(string title, string message)
        {
            if (_trayIcon != null && _trayIcon.Visible && !Visible)
                _trayIcon.ShowBalloonTip(4000, title, message, ToolTipIcon.Warning);
            else
                MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void Post(Delegate method, params object[] args)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(method, args);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _singleton.Release();
            if (_trayIcon != null) _trayIcon.Visible = false;
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (_trayIcon != null)
                    _trayIcon.ShowBalloonTip(2000, "Still running",
                        "Archon is in the system tray and still holding the names.",
                        ToolTipIcon.Info);
                base.OnFormClosing(e);
                return;
            }

            if (_frames != null) _frames.Stop();
            if (_poll != null) _poll.Stop();
            _singleton.Release();
            if (_trayIcon != null) _trayIcon.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized) Hide();
            ApplyTimerRates();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            ApplyTimerRates();
        }

        // nothing is drawn while the window is in the tray
        private void ApplyTimerRates()
        {
            if (_frames == null || _poll == null) return;

            bool onScreen = Visible && WindowState != FormWindowState.Minimized;

            if (onScreen)
            {
                _poll.Interval = 1500;
                if (!_frames.Enabled) _frames.Start();
            }
            else
            {
                _frames.Stop();
                _poll.Interval = 10000;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_showCredits && keyData == Keys.Escape)
            {
                _showCredits = false;
                Invalidate();
                return true;
            }

            bool typing = (_placeInput != null && _placeInput.Focused) ||
                          (_serverInput != null && _serverInput.Focused) ||
                          (_fpsInput != null && _fpsInput.Focused);

            if (typing)
            {
                if (keyData == Keys.Escape)
                {
                    ActiveControl = null;   // drop focus, keep the window up
                    Invalidate();
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            switch (keyData)
            {
                case Keys.Escape:
                    Close();
                    return true;

                case Keys.Space:
                    Toggle();
                    Invalidate();
                    return true;

                case Keys.Control | Keys.Tab:
                    _tab = (_tab + 1) % 3;
                    Invalidate();
                    return true;

                case Keys.Control | Keys.D1: _tab = 0; Invalidate(); return true;
                case Keys.Control | Keys.D2: _tab = 1; Invalidate(); return true;
                case Keys.Control | Keys.D3: _tab = 2; Invalidate(); return true;

                case Keys.Up:
                case Keys.Left:
                    MoveSelection(-1);
                    return true;

                case Keys.Down:
                case Keys.Right:
                    MoveSelection(1);
                    return true;

                case Keys.Enter:
                    if (_tab == 0 && _selInstancePid > 0) FocusInstance(_selInstancePid);
                    else if (_tab == 1) LaunchSelected();
                    return true;

                case Keys.Delete:
                    if (_tab == 1) RemoveAccount();
                    return true;

                case Keys.Home:
                case Keys.End:
                case Keys.PageUp:
                case Keys.PageDown:
                    ScrollByKey(keyData);
                    return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void MoveSelection(int delta)
        {
            if (_tab == 0)
            {
                if (_instances.Length == 0) return;

                int at = 0;
                for (int i = 0; i < _instances.Length; i++)
                    if (_instances[i].Pid == _selInstancePid) { at = i; break; }

                at += delta;
                if (at < 0) at = 0;
                if (at >= _instances.Length) at = _instances.Length - 1;
                _selInstancePid = _instances[at].Pid;
            }
            else if (_tab == 1)
            {
                if (_accounts.Count == 0) return;

                int at = _selAccount + delta;
                if (at < 0) at = 0;
                if (at >= _accounts.Count) at = _accounts.Count - 1;
                _selAccount = at;
            }

            Invalidate();
        }

        private void ScrollByKey(Keys key)
        {
            bool instances = _tab == 0;
            if (!instances && _tab != 1) return;

            float view = instances ? _instanceViewH : _accountViewH;
            float content = instances ? _instanceContentH : _accountContentH;
            float scroll = instances ? _instanceScroll : _accountScroll;

            if (key == Keys.Home) scroll = 0f;
            else if (key == Keys.End) scroll = content;
            else if (key == Keys.PageUp) scroll -= view;
            else scroll += view;

            scroll = ClampScroll(scroll, content, view);

            if (instances) _instanceScroll = scroll;
            else _accountScroll = scroll;

            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _singleton.Release();
                if (_frames != null) { _frames.Stop(); _frames.Dispose(); _frames = null; }
                if (_poll != null) { _poll.Stop(); _poll.Dispose(); _poll = null; }
                foreach (FileSystemWatcher w in _settingsWatchers) w.Dispose();
                _settingsWatchers.Clear();
                if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
                if (_trayMenu != null) { _trayMenu.Dispose(); _trayMenu = null; }
                if (_appIcon != null) { _appIcon.Dispose(); _appIcon = null; }
                if (_trayIconImage != null) { _trayIconImage.Dispose(); _trayIconImage = null; }
            }
            base.Dispose(disposing);
        }
    }
}
