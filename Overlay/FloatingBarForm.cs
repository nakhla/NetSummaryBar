using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSummaryBar
{
    /// <summary>
    /// Borderless per-pixel-alpha (layered) window that floats next to the taskbar tray.
    ///
    /// No global mouse hook is used. The "empty" background is painted at alpha = 1, which is invisible but
    /// still hit-testable, so the normal window messages (right-click menu, drag, hover) work over the whole
    /// bar. Rendering goes into a persistent DIB section and is pushed with UpdateLayeredWindow only when the
    /// picture actually changes.
    /// </summary>
    public class FloatingBarForm : Form
    {
        // ------------------------------------------------------------------ off-screen surface
        private sealed class Surface : IDisposable
        {
            public readonly int Width;
            public readonly int Height;
            public readonly Graphics G;
            private readonly Bitmap _bmp;
            private readonly IntPtr _memDc;
            private readonly IntPtr _hBitmap;
            private readonly IntPtr _oldObj;

            public Surface(int w, int h)
            {
                Width = w;
                Height = h;

                IntPtr screenDc = Native.GetDC(IntPtr.Zero);
                try
                {
                    _memDc = Native.CreateCompatibleDC(screenDc);
                    Native.BITMAPINFOHEADER bi = new Native.BITMAPINFOHEADER();
                    bi.biSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
                    bi.biWidth = w;
                    bi.biHeight = -h; // top-down
                    bi.biPlanes = 1;
                    bi.biBitCount = 32;
                    bi.biCompression = 0; // BI_RGB
                    IntPtr bits;
                    _hBitmap = Native.CreateDIBSection(screenDc, ref bi, 0, out bits, IntPtr.Zero, 0);
                    if (_hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
                        throw new InvalidOperationException("CreateDIBSection failed");
                    _oldObj = Native.SelectObject(_memDc, _hBitmap);
                    _bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
                    G = Graphics.FromImage(_bmp);
                }
                finally
                {
                    Native.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }

            public void Present(IntPtr hwnd)
            {
                Native.GdiFlush();
                Native.SIZE size = new Native.SIZE();
                size.cx = Width;
                size.cy = Height;
                Native.POINT src = new Native.POINT();
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;
                Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, IntPtr.Zero, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }

            public void Dispose()
            {
                if (G != null) G.Dispose();
                if (_bmp != null) _bmp.Dispose();
                if (_memDc != IntPtr.Zero)
                {
                    if (_oldObj != IntPtr.Zero) Native.SelectObject(_memDc, _oldObj);
                    Native.DeleteDC(_memDc);
                }
                if (_hBitmap != IntPtr.Zero) Native.DeleteObject(_hBitmap);
            }
        }

        // ------------------------------------------------------------------ state
        private readonly AppSettings _settings;
        private readonly NetworkMonitor _monitor;
        private Timer _updateTimer;
        private Timer _dockTimer;

        // Show-Desktop / z-order resilience
        private Timer _zTimer;                 // 1 s safety net: visibility + "is the taskbar above us?"
        private Timer _zCheckTimer;            // one-shot, coalesces event notifications into a single check
        private bool _zCheckPending;
        private string _zReason = "";
        private Native.WinEventProc _fgProc;   // delegates must stay referenced while hooked
        private Native.WinEventProc _reorderProc;
        private Native.WinEventProc _dbgProc;
        private IntPtr _fgHook = IntPtr.Zero;
        private IntPtr _reorderHook = IntPtr.Zero;
        private IntPtr _desktopHwnd = IntPtr.Zero;
        private IntPtr _dockTaskbarHwnd = IntPtr.Zero;   // taskbar window found by the last ComputeDock()
        private int _zFollowUps;
        private readonly int[] _reseatTicks = new int[12];
        private int _reseatIdx;
        private IntPtr _dbgShowHook = IntPtr.Zero;
        private IntPtr _dbgCloakHook = IntPtr.Zero;
        private readonly System.Text.StringBuilder _cls = new System.Text.StringBuilder(64);
        private bool _closing;
        private bool _shownOnce;
        private bool _busyObserved;
        private int _busySinceTick;
        private int _lastLoggedUns = -1;
        private Timer _trimTimer;

        private NotifyIcon _trayIcon;
        private ContextMenuStrip _contextMenu;
        private ToolTip _hoverToolTip;
        private ToolStripMenuItem _miLock, _miTransparent, _miToggleBar;
        private Timer _trayClickTimer;
        private bool _userHidden;         // bar hidden by the user via tray click / menu (this session only)
        private ToolStripMenuItem[] _miUnits;
        private ToolStripMenuItem[] _miStyles;
        private Font _menuBoldFont;

        private static readonly string[] UnitNames = { "Auto", "KB/s", "MB/s", "Mbps", "Kbps" };
        private static readonly string[] StyleNames = { "Mirror", "Stacked", "Overlay" };

        private Surface _surface;
        private float _scale = 1f;
        private int _taskbarCreatedMsg;

        private bool _hovering;
        private bool _moving;
        private Point _moveStart;
        private bool _settingsOpen;
        private bool _onScreen = true;     // visibility policy result (fullscreen / hidden taskbar)
        private bool _taskbarHidden;       // docked taskbar currently auto-hidden / invisible
        private bool _forceRedraw = true;
        private bool _fontsDirty = true;

        // cached GDI+ objects (rebuilt only when settings change)
        private Font _rateFont, _iconFont;
        private float _fontPx;
        private float _iconW, _textW;
        private StringFormat _sf;
        private Pen _upPen, _downPen, _borderPen;
        private SolidBrush _upFillMirror, _downFillMirror, _upFillSingle, _downFillSingle;
        private SolidBrush _upText, _downText, _graphBgBrush;
        private Pen _winBorderPen;
        private PointF[] _line = new PointF[0];
        private PointF[] _poly = new PointF[0];

        public bool FirstRun;

        public FloatingBarForm(AppSettings settings)
        {
            _settings = settings;

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                _scale = Math.Max(1f, g.DpiX / 96f);
            }

            _sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            _sf.FormatFlags = (_sf.FormatFlags & ~StringFormatFlags.NoClip) | StringFormatFlags.NoWrap;
            _sf.Trimming = StringTrimming.None;

            _monitor = new NetworkMonitor(_settings.HistoryPoints);
            _taskbarCreatedMsg = (int)Native.RegisterWindowMessage("TaskbarCreated");

            InitializeComponent();

            _updateTimer = new Timer();
            _updateTimer.Tick += UpdateTimer_Tick;
            _dockTimer = new Timer();
            _dockTimer.Interval = 2000;
            _dockTimer.Tick += DockTimer_Tick;

            _zTimer = new Timer();
            _zTimer.Interval = 1000;
            _zTimer.Tick += ZTimer_Tick;
            _zCheckTimer = new Timer();
            _zCheckTimer.Tick += ZCheckTimer_Tick;
            _desktopHwnd = Native.GetDesktopWindow();

            ApplySettings();

            _updateTimer.Start();
            _dockTimer.Start();

            _monitor.Update(_settings.SelectedAdapterId);

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            // one-shot: trim the working set a few seconds after startup
            _trimTimer = new Timer();
            _trimTimer.Interval = 5000;
            _trimTimer.Tick += delegate { _trimTimer.Stop(); Native.TrimMemory(); };
            _trimTimer.Start();
        }

        // ------------------------------------------------------------------ window plumbing
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                if (_settings == null || _settings.AlwaysOnTop)
                    cp.ExStyle |= Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { /* content comes from UpdateLayeredWindow */ }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Keep the bar visible while Aero Peek fades every other window (hovering the Show Desktop corner).
            try
            {
                int exclude = 1;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_EXCLUDED_FROM_PEEK, ref exclude, sizeof(int));
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            base.OnFormClosing(e);
        }

        /// <summary>True once the user/app asked to close (as opposed to Windows destroying our window).</summary>
        public bool IsClosing { get { return _closing; } }

        /// <summary>Raised when the native window was destroyed underneath us (see OnHandleDestroyed).</summary>
        public event EventHandler HandleLost;

        /// <summary>
        /// The bar is an OWNED window of the taskbar. Windows destroys every window a taskbar owns when that
        /// taskbar is destroyed (Explorer restart/crash, monitor unplugged, "show taskbar on all displays" off).
        /// That is not a close request, so tell the controller to build a fresh bar instead of letting the app die.
        /// </summary>
        protected override void OnHandleDestroyed(EventArgs e)
        {
            base.OnHandleDestroyed(e);
            if (!_closing && !RecreatingHandle && !IsDisposed && !Disposing && _shownOnce)
            {
                Log.Write("Bar window destroyed externally (its taskbar owner went away); rebuilding.");
                EventHandler h = HandleLost;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _shownOnce = true;
            EnsureTaskbarOwner();
            ApplyZOrder();
            UpdateVisibility();
            _forceRedraw = true;
            Redraw();
            InstallWinEventHooks();
            _zTimer.Start();

            if (FirstRun)
            {
                BeginInvoke(new MethodInvoker(OpenSettingsDialog));
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)Native.MA_NOACTIVATE;
                    return;

                case Native.WM_LBUTTONDOWN:
                    if (!_settings.Locked)
                    {
                        // hand the drag to the system move loop (no hook, no manual tracking)
                        Native.ReleaseCapture();
                        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
                        return;
                    }
                    break;

                case Native.WM_SYSCOMMAND:
                    // Never let anything minimize the bar (it has no taskbar button to restore it from).
                    if (((int)m.WParam & 0xFFF0) == Native.SC_MINIMIZE)
                    {
                        Log.Debug("SC_MINIMIZE swallowed");
                        return;
                    }
                    break;

                case Native.WM_SHOWWINDOW:
                    // wParam == 0 && lParam == 0: somebody called ShowWindow(SW_HIDE) on us. Our own policy hides set
                    // _onScreen = false first, so reaching here with _onScreen == true means another process did it.
                    if (m.WParam == IntPtr.Zero && m.LParam == IntPtr.Zero && _onScreen && !_closing && _shownOnce)
                    {
                        Log.Debug("hidden externally via ShowWindow -> re-showing");
                        try
                        {
                            BeginInvoke(new MethodInvoker(delegate { EnsureVisibleAndOnTop("wm_showwindow"); }));
                        }
                        catch { }
                    }
                    break;

                case Native.WM_ENTERSIZEMOVE:
                    _moving = true;
                    _moveStart = Location;
                    break;

                case Native.WM_EXITSIZEMOVE:
                    _moving = false;
                    if (Location != _moveStart)
                    {
                        _settings.AutoDockToTaskbar = false;
                        _settings.WindowX = Location.X;
                        _settings.WindowY = Location.Y;
                        _settings.Save();
                    }
                    break;

                default:
                    if (_taskbarCreatedMsg != 0 && m.Msg == _taskbarCreatedMsg)
                    {
                        // Explorer restarted
                        PositionWindow();
                        UpdateVisibility();
                        _forceRedraw = true;
                        Redraw();
                    }
                    break;
            }
            base.WndProc(ref m);
        }

        private void InitializeComponent()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.Manual;

            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(iconPath)) this.Icon = new Icon(iconPath);
            }
            catch { }

            _menuBoldFont = new Font(SystemFonts.MenuFont, FontStyle.Bold);
            _contextMenu = new ContextMenuStrip();
            BuildContextMenu();
            this.ContextMenuStrip = _contextMenu;

            _hoverToolTip = new ToolTip();
            _hoverToolTip.InitialDelay = 400;
            _hoverToolTip.ReshowDelay = 200;

            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "NetSummaryBar";
            _trayIcon.Icon = this.Icon != null ? this.Icon : SystemIcons.Application;
            _trayIcon.Visible = true;
            _trayIcon.ContextMenuStrip = _contextMenu;
            // Single left click toggles the bar; double click opens settings. A click is held back for one
            // double-click interval so a double click does not toggle first.
            _trayClickTimer = new Timer();
            _trayClickTimer.Interval = SystemInformation.DoubleClickTime;
            _trayClickTimer.Tick += delegate { _trayClickTimer.Stop(); ToggleBarVisibility(); };
            _trayIcon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { _trayClickTimer.Stop(); _trayClickTimer.Start(); }
            };
            _trayIcon.DoubleClick += delegate { _trayClickTimer.Stop(); OpenSettingsDialog(); };
            _trayIcon.MouseMove += delegate { UpdateTrayText(); };

            this.MouseEnter += delegate
            {
                _hovering = true;
                _hoverToolTip.SetToolTip(this, _settings.ShowTooltip ? BuildInfoText() : null);
            };
            this.MouseLeave += delegate { _hovering = false; };
        }

        // ------------------------------------------------------------------ context menu (built once)
        private void BuildContextMenu()
        {
            var itemSettings = new ToolStripMenuItem("⚙ Settings...", null, delegate { OpenSettingsDialog(); });
            itemSettings.Font = _menuBoldFont;
            _contextMenu.Items.Add(itemSettings);
            _miToggleBar = new ToolStripMenuItem("Hide Bar", null, delegate { ToggleBarVisibility(); });
            _contextMenu.Items.Add(_miToggleBar);
            _contextMenu.Opening += delegate { _miToggleBar.Text = _userHidden ? "Show Bar" : "Hide Bar"; };
            _contextMenu.Items.Add(new ToolStripSeparator());

            _miLock = new ToolStripMenuItem("Lock Position");
            _miLock.Click += delegate
            {
                _settings.Locked = !_settings.Locked;
                _settings.Save();
            };
            _contextMenu.Items.Add(_miLock);

            _contextMenu.Items.Add(new ToolStripMenuItem("Dock to Taskbar", null, delegate
            {
                _settings.AutoDockToTaskbar = true;
                _settings.Save();
                ApplySettings();
            }));

            _miTransparent = new ToolStripMenuItem("Transparent Background");
            _miTransparent.Click += delegate
            {
                _settings.TransparentBackground = !_settings.TransparentBackground;
                _settings.Save();
                ApplySettings();
            };
            _contextMenu.Items.Add(_miTransparent);

            var units = new ToolStripMenuItem("Speed Units");
            _miUnits = new ToolStripMenuItem[UnitNames.Length];
            for (int i = 0; i < UnitNames.Length; i++)
            {
                string name = UnitNames[i];
                var sub = new ToolStripMenuItem(name);
                sub.Click += delegate
                {
                    _settings.SpeedUnit = name;
                    _settings.Save();
                    _fontsDirty = true;
                    _forceRedraw = true;
                    Redraw();
                };
                _miUnits[i] = sub;
                units.DropDownItems.Add(sub);
            }
            _contextMenu.Items.Add(units);

            var styles = new ToolStripMenuItem("Graph Style");
            _miStyles = new ToolStripMenuItem[StyleNames.Length];
            for (int i = 0; i < StyleNames.Length; i++)
            {
                string name = StyleNames[i];
                var sub = new ToolStripMenuItem(name);
                sub.Click += delegate
                {
                    _settings.GraphStyle = name;
                    _settings.Save();
                    _forceRedraw = true;
                    Redraw();
                };
                _miStyles[i] = sub;
                styles.DropDownItems.Add(sub);
            }
            _contextMenu.Items.Add(styles);

            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(new ToolStripMenuItem("Exit", null, delegate
            {
                _trayIcon.Visible = false;
                Application.Exit();
            }));

            // refresh check marks lazily each time the menu opens (no rebuild, no leaked items/fonts)
            _contextMenu.Opening += delegate
            {
                _miLock.Checked = _settings.Locked;
                _miTransparent.Checked = _settings.TransparentBackground;
                for (int i = 0; i < _miUnits.Length; i++)
                    _miUnits[i].Checked = string.Equals(_settings.SpeedUnit, UnitNames[i], StringComparison.OrdinalIgnoreCase);
                for (int i = 0; i < _miStyles.Length; i++)
                    _miStyles[i].Checked = string.Equals(_settings.GraphStyle, StyleNames[i], StringComparison.OrdinalIgnoreCase);
            };
        }

        // ------------------------------------------------------------------ settings -> window
        private int Scaled(int v)
        {
            return (int)Math.Round(v * _scale);
        }

        public void ApplySettings()
        {
            _monitor.SetCapacity(_settings.HistoryPoints);
            _updateTimer.Interval = Math.Max(250, _settings.UpdateIntervalMs);
            RebuildGdi();
            _fontsDirty = true;

            PositionWindow();
            ApplyZOrder();
            UpdateVisibility();
            _forceRedraw = true;
            Redraw();
        }

        private void DisposeGdi()
        {
            if (_upPen != null) { _upPen.Dispose(); _upPen = null; }
            if (_downPen != null) { _downPen.Dispose(); _downPen = null; }
            if (_borderPen != null) { _borderPen.Dispose(); _borderPen = null; }
            if (_winBorderPen != null) { _winBorderPen.Dispose(); _winBorderPen = null; }
            if (_upFillMirror != null) { _upFillMirror.Dispose(); _upFillMirror = null; }
            if (_downFillMirror != null) { _downFillMirror.Dispose(); _downFillMirror = null; }
            if (_upFillSingle != null) { _upFillSingle.Dispose(); _upFillSingle = null; }
            if (_downFillSingle != null) { _downFillSingle.Dispose(); _downFillSingle = null; }
            if (_upText != null) { _upText.Dispose(); _upText = null; }
            if (_downText != null) { _downText.Dispose(); _downText = null; }
            if (_graphBgBrush != null) { _graphBgBrush.Dispose(); _graphBgBrush = null; }
        }

        private void RebuildGdi()
        {
            DisposeGdi();
            float pw = Math.Max(1f, 1.2f * _scale);
            Color up = _settings.UploadColor;
            Color down = _settings.DownloadColor;

            _upPen = new Pen(up, pw);
            _downPen = new Pen(down, pw);
            _upPen.LineJoin = LineJoin.Round;
            _downPen.LineJoin = LineJoin.Round;
            _borderPen = new Pen(_settings.HistogramBorderColor, 1f);
            _winBorderPen = new Pen(Color.FromArgb(45, 255, 255, 255), 1f);

            _upFillMirror = new SolidBrush(Color.FromArgb(120, up));
            _downFillMirror = new SolidBrush(Color.FromArgb(120, down));
            _upFillSingle = new SolidBrush(Color.FromArgb(90, up));
            _downFillSingle = new SolidBrush(Color.FromArgb(90, down));

            Color upTxt = _settings.TextMatchesGraphColor ? up : _settings.CustomTextColor;
            Color downTxt = _settings.TextMatchesGraphColor ? down : _settings.CustomTextColor;
            _upText = new SolidBrush(upTxt);
            _downText = new SolidBrush(downTxt);
            _graphBgBrush = new SolidBrush(Color.FromArgb(20, 0, 0, 0));
        }

        private Screen GetDockScreen()
        {
            string dev = _settings.DockScreenDevice;
            if (!string.IsNullOrEmpty(dev))
            {
                foreach (Screen s in Screen.AllScreens)
                    if (string.Equals(s.DeviceName, dev, StringComparison.OrdinalIgnoreCase)) return s;
            }
            return Screen.PrimaryScreen;
        }

        private static bool Overlaps(Native.RECT r, Rectangle b)
        {
            Rectangle i = Rectangle.Intersect(new Rectangle(r.Left, r.Top, r.Width, r.Height), b);
            return i.Width > 0 && i.Height > 0;
        }

        private bool TryFindTaskbar(Screen scr, out Native.RECT tb, out Native.RECT tray, out bool hasTray, out IntPtr hwnd)
        {
            tb = new Native.RECT();
            tray = new Native.RECT();
            hasTray = false;
            hwnd = IntPtr.Zero;

            IntPtr primary = Native.FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero && Native.GetWindowRect(primary, out tb) && Overlaps(tb, scr.Bounds))
            {
                hwnd = primary;
                IntPtr trayWnd = Native.FindWindowEx(primary, IntPtr.Zero, "TrayNotifyWnd", null);
                hasTray = trayWnd != IntPtr.Zero && Native.GetWindowRect(trayWnd, out tray) && tray.Width > 0;
                return true;
            }

            IntPtr sec = IntPtr.Zero;
            while ((sec = Native.FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                if (Native.GetWindowRect(sec, out tb) && Overlaps(tb, scr.Bounds))
                {
                    hwnd = sec;
                    hasTray = false; // secondary taskbars have no TrayNotifyWnd; anchor to the right edge instead
                    return true;
                }
            }
            return false;
        }

        private Rectangle ComputeDock()
        {
            Screen scr = GetDockScreen();
            Rectangle sb = scr.Bounds;
            int w = Scaled(_settings.BarWidth);
            int h = Scaled(_settings.BarHeight);
            int pad = Scaled(6);
            int x = sb.Right - w - Scaled(240);
            int y = sb.Bottom - h - 1;
            _taskbarHidden = false;
            _dockTaskbarHwnd = IntPtr.Zero;

            Native.RECT tb, tray;
            bool hasTray;
            IntPtr tbWnd;
            if (TryFindTaskbar(scr, out tb, out tray, out hasTray, out tbWnd))
            {
                _dockTaskbarHwnd = tbWnd;
                // auto-hidden taskbars slide mostly off-screen: don't float on its sliver
                Rectangle vis = Rectangle.Intersect(new Rectangle(tb.Left, tb.Top, tb.Width, tb.Height), sb);
                long full = (long)tb.Width * tb.Height;
                _taskbarHidden = !Native.IsWindowVisible(tbWnd) || full <= 0 ||
                                 ((long)vis.Width * vis.Height) < full * 9 / 10;

                if (tb.Width > tb.Height)
                {
                    h = Math.Min(h, Math.Max(Scaled(24), tb.Height));
                    y = tb.Top + (tb.Height - h) / 2;
                    if (hasTray && tray.Left > tb.Left) x = tray.Left - w - pad;
                    else x = tb.Right - w - Scaled(hasTray ? 220 : 170);
                }
                else
                {
                    w = Math.Min(w, Math.Max(Scaled(50), tb.Width));
                    x = tb.Left + (tb.Width - w) / 2;
                    if (hasTray && tray.Top > tb.Top) y = tray.Top - h - pad;
                    else y = tb.Bottom - h - Scaled(hasTray ? 180 : 130);
                }
            }
            else
            {
                Rectangle wa = scr.WorkingArea;
                if (wa.Bottom < sb.Bottom)
                {
                    int tbH = sb.Bottom - wa.Bottom;
                    h = Math.Min(h, Math.Max(Scaled(24), tbH));
                    y = wa.Bottom + (tbH - h) / 2;
                    x = wa.Right - w - Scaled(240);
                }
                else if (wa.Top > sb.Top)
                {
                    int tbH = wa.Top - sb.Top;
                    h = Math.Min(h, Math.Max(Scaled(24), tbH));
                    y = sb.Top + (tbH - h) / 2;
                    x = wa.Right - w - Scaled(240);
                }
            }

            if (x + w > sb.Right) x = sb.Right - w - 10;
            if (x < sb.Left) x = sb.Left + 10;
            if (y + h > sb.Bottom) y = sb.Bottom - h;
            if (y < sb.Top) y = sb.Top;
            return new Rectangle(x, y, w, h);
        }

        private Rectangle ComputeManual()
        {
            int w = Scaled(_settings.BarWidth);
            int h = Scaled(_settings.BarHeight);
            _dockTaskbarHwnd = IntPtr.Zero;
            if (_settings.WindowX == -1 && _settings.WindowY == -1)
                return ComputeDock();

            Rectangle r = new Rectangle(_settings.WindowX, _settings.WindowY, w, h);
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle i = Rectangle.Intersect(r, s.Bounds);
                if (i.Width >= Math.Min(w, 20) && i.Height >= Math.Min(h, 10))
                {
                    _taskbarHidden = false;
                    return r;
                }
            }
            return ComputeDock(); // saved position is on a monitor that no longer exists
        }

        private void PositionWindow()
        {
            Rectangle target = _settings.AutoDockToTaskbar ? ComputeDock() : ComputeManual();
            if (!_settings.AutoDockToTaskbar) _taskbarHidden = false;
            if (target != this.Bounds)
            {
                this.SetBounds(target.X, target.Y, target.Width, target.Height);
                _forceRedraw = true;
            }
            EnsureTaskbarOwner(); // cheap compare; only acts when the owner link is missing or stale
        }

        private void ApplyZOrder()
        {
            if (!IsHandleCreated) return;
            Native.SetWindowPos(Handle, _settings.AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
                0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        private void ToggleBarVisibility()
        {
            if (_closing || IsDisposed) return;
            _userHidden = !_userHidden;
            Log.Debug("user toggled bar: " + (_userHidden ? "hidden" : "shown"));
            UpdateVisibility();
        }

        /// <summary>Hides the bar over fullscreen apps / lock screen and when the docked taskbar is auto-hidden.</summary>
        private void UpdateVisibility()
        {
            if (!IsHandleCreated) return;

            bool show = true;
            if (_settings.HideInFullscreen)
            {
                int state;
                bool busy = false;
                if (Native.SHQueryUserNotificationState(out state) == 0)
                {
                    // 1 = not present (locked/screensaver), 2 = busy (fullscreen), 3 = D3D fullscreen, 4 = presentation
                    busy = state >= 1 && state <= 4;
                    if (state != _lastLoggedUns)
                    {
                        _lastLoggedUns = state;
                        Log.Debug("SHQueryUserNotificationState = " + state);
                    }
                }

                // Showing the desktop makes the shell's own WorkerW/Progman window the foreground window.
                // That is not a fullscreen app, so it must never hide the bar.
                if (busy && IsForegroundShellWindow()) busy = false;

                // Hide only if the condition persists; a momentary flip must not make the bar blink.
                if (busy)
                {
                    if (!_busyObserved) { _busyObserved = true; _busySinceTick = Environment.TickCount; }
                    if (unchecked(Environment.TickCount - _busySinceTick) >= 1000) show = false;
                }
                else
                {
                    _busyObserved = false;
                }
            }
            if (show && _settings.AutoDockToTaskbar && _taskbarHidden) show = false;
            if (_userHidden) show = false;

            if (show != _onScreen)
            {
                Log.Debug("visibility policy: " + (show ? "show" : "hide"));
                _onScreen = show;
                Native.ShowWindow(Handle, show ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
                if (show)
                {
                    ApplyZOrder();
                    _forceRedraw = true;
                    Redraw();
                }
            }
        }

        private string ClassOf(IntPtr hwnd)
        {
            _cls.Length = 0;
            if (hwnd == IntPtr.Zero || Native.GetClassName(hwnd, _cls, _cls.Capacity) <= 0) return "";
            return _cls.ToString();
        }

        /// <summary>
        /// True when the foreground window belongs to the shell (explorer.exe: desktop, taskbar, Start, flyouts) or
        /// there is momentarily no foreground window. Show Desktop passes through exactly these states, and the
        /// desktop window covers the whole screen, so SHQueryUserNotificationState reports "busy" for it.
        /// </summary>
        private bool IsForegroundShellWindow()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return true;
            string c = ClassOf(fg);
            if (c == "WorkerW" || c == "Progman" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" ||
                c == "SHELLDLL_DefView" || c == "SysListView32" || c == "Windows.UI.Core.CoreWindow" ||
                c == "XamlExplorerHostIslandWindow" || c == "TopLevelWindowForOverflowXamlIsland")
                return true;
            uint fgPid, shellPid;
            Native.GetWindowThreadProcessId(fg, out fgPid);
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(tray, out shellPid);
            return fgPid != 0 && fgPid == shellPid;
        }

        private static bool IsShellClass(string c)
        {
            return c == "WorkerW" || c == "Progman" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" ||
                   c == "SHELLDLL_DefView" || c == "SysListView32";
        }

        /// <summary>A taskbar window (primary or secondary) above us in z-order that overlaps the bar, or Zero.</summary>
        private IntPtr FindTaskbarAboveUs()
        {
            Native.RECT mine;
            if (!Native.GetWindowRect(Handle, out mine)) return IntPtr.Zero;

            IntPtr h = Native.GetWindow(Handle, Native.GW_HWNDPREV);
            for (int guard = 0; h != IntPtr.Zero && guard < 256; guard++)
            {
                if (Native.IsWindowVisible(h))
                {
                    string c = ClassOf(h);
                    if (c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd")
                    {
                        Native.RECT r;
                        if (Native.GetWindowRect(h, out r) &&
                            r.Left < mine.Right && r.Right > mine.Left && r.Top < mine.Bottom && r.Bottom > mine.Top)
                            return h;
                    }
                }
                h = Native.GetWindow(h, Native.GW_HWNDPREV);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Makes the bar an OWNED window of the taskbar (GWLP_HWNDPARENT). Windows always keeps an owned window
        /// above its owner, so clicking the taskbar, Show Desktop, Start, etc. can no longer raise the taskbar over
        /// the bar. This is a structural guarantee instead of a timer race.
        ///
        /// In steady state this is a single compare and touches nothing: re-asserting HWND_TOPMOST on an owned
        /// window drags the taskbar's whole owner cluster above any open shell menu, so it is only done once,
        /// when the relationship is (re)established.
        /// </summary>
        private void EnsureTaskbarOwner()
        {
            if (!IsHandleCreated || _closing) return;

            IntPtr want = (_settings.AutoDockToTaskbar && _settings.AlwaysOnTop) ? _dockTaskbarHwnd : IntPtr.Zero;
            if (want != IntPtr.Zero && !Native.IsWindow(want)) want = IntPtr.Zero;

            IntPtr cur = Native.GetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT);
            if (cur != IntPtr.Zero && !Native.IsWindow(cur))
            {
                Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, IntPtr.Zero); // stale owner (taskbar replaced)
                cur = IntPtr.Zero;
            }
            if (cur == want) return;

            Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, want);
            Log.Debug("owner taskbar: 0x" + cur.ToInt64().ToString("X") + " -> 0x" + want.ToInt64().ToString("X"));
            ApplyZOrder(); // the single topmost seat, now that the relationship changed
        }

        /// <summary>
        /// Re-inserts the bar directly above the given taskbar without moving it, activating it, or dragging the
        /// taskbar's owner cluster (SWP_NOOWNERZORDER). Everything already above the taskbar stays above the bar,
        /// so open menus and flyouts are never covered.
        /// </summary>
        private bool ReseatAboveTaskbar(IntPtr tb)
        {
            if (tb == IntPtr.Zero || !Native.IsWindow(tb)) return false;
            // A fullscreen app takes the taskbar out of the topmost band; following it down would put us under it.
            if ((Native.GetWindowLongPtr(tb, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOPMOST) == 0) return false;

            IntPtr prev = Native.GetWindow(tb, Native.GW_HWNDPREV);
            if (prev == Handle) return false;
            IntPtr after = prev != IntPtr.Zero ? prev : Native.HWND_TOPMOST;
            const uint F = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
            if (Native.SetWindowPos(Handle, after, 0, 0, 0, 0, F)) return true;

            // Inserting after a window of another band/process can be refused (access denied). Walk up from the
            // taskbar to the first window that is accepted, and as a last resort use the topmost band directly.
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log.Debug("SetWindowPos(after 0x" + after.ToInt64().ToString("X") + " '" + ClassOf(after) + "') failed, error " + err);
            IntPtr h = prev;
            for (int guard = 0; h != IntPtr.Zero && guard < 64; guard++)
            {
                h = Native.GetWindow(h, Native.GW_HWNDPREV);
                if (h == IntPtr.Zero || h == Handle) break;
                if (Native.SetWindowPos(Handle, h, 0, 0, 0, 0, F)) return true;
            }
            if (Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, F)) return true;
            Log.Debug("fallback HWND_TOPMOST failed, error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return false;
        }

        // A taskbar that keeps burying the bar is a fight we should not escalate: at most 12 event-driven
        // re-seats per 10 s. The 1 s safety tick is always allowed, so recovery is never starved.
        private bool AllowReseat(bool fromPoll)
        {
            if (fromPoll) return true;
            // Switching to/from the desktop or taskbar (Show Desktop, taskbar click) is a deliberate user action,
            // not a fight with another app: never throttle it.
            if (IsShellClass(ClassOf(Native.GetForegroundWindow()))) return true;
            int now = Environment.TickCount, recent = 0;
            for (int i = 0; i < _reseatTicks.Length; i++)
                if (_reseatTicks[i] != 0 && unchecked(now - _reseatTicks[i]) < 10000) recent++;
            return recent < _reseatTicks.Length;
        }

        private void NoteReseat()
        {
            _reseatTicks[_reseatIdx] = Environment.TickCount | 1;
            _reseatIdx = (_reseatIdx + 1) % _reseatTicks.Length;
        }

        /// <summary>
        /// Safety net used by the event handlers and the 1 s tick: re-show if another process hid us (unless our
        /// own owner taskbar is hidden), and if the taskbar still ended up above us, re-seat just above it.
        /// With the owner link in place the second part should almost never fire.
        /// </summary>
        private void EnsureVisibleAndOnTop(string reason)
        {
            if (!_shownOnce || _closing || IsDisposed || !IsHandleCreated || _moving) return;

            IntPtr owner = Native.GetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT);
            bool ownerHidden = owner != IntPtr.Zero && !Native.IsWindowVisible(owner);

            if (_onScreen && !ownerHidden && !Native.IsWindowVisible(Handle))
            {
                Log.Debug("[" + reason + "] window not visible although it should be -> ShowWindow");
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                _forceRedraw = true;
                Redraw();
            }

            if (_onScreen && _settings.AlwaysOnTop && !ownerHidden)
            {
                IntPtr tb = FindTaskbarAboveUs();
                if (tb != IntPtr.Zero && AllowReseat(reason == "poll"))
                {
                    Log.Debug("[" + reason + "] taskbar 0x" + tb.ToInt64().ToString("X") + " is above the bar -> re-seating");
                    bool ok;
                    if (owner != IntPtr.Zero) ok = ReseatAboveTaskbar(tb);
                    else { ApplyZOrder(); ok = true; } // free-floating bar: no owner cluster to disturb
                    if (ok) { if (!IsShellClass(ClassOf(Native.GetForegroundWindow()))) NoteReseat(); }
                    else Log.Debug("[" + reason + "] re-seat failed");
                }
            }
        }

        /// <summary>Coalesces bursts of shell notifications into one check ~30 ms later (after the shell finished re-ordering).</summary>
        private void ScheduleZCheck(string reason)
        {
            _zReason = reason;
            if (reason == "foreground") _zFollowUps = 2; // grace: the shell may re-raise the taskbar once more
            if (_zCheckPending || _zCheckTimer == null) return;
            _zCheckPending = true;
            _zCheckTimer.Interval = 30;
            _zCheckTimer.Start();
        }

        private void ZCheckTimer_Tick(object sender, EventArgs e)
        {
            _zCheckTimer.Stop();
            _zCheckPending = false;
            try
            {
                EnsureVisibleAndOnTop(_zReason);
                // After a switch to the desktop/taskbar (Show Desktop) the shell can re-order again a moment later.
                // Two bounded follow-up checks per switch, only while the shell is in front; no polling.
                if (_zFollowUps > 0 && IsShellClass(ClassOf(Native.GetForegroundWindow())))
                {
                    _zFollowUps--;
                    _zReason = "followup";
                    _zCheckPending = true;
                    _zCheckTimer.Interval = 120;
                    _zCheckTimer.Start();
                }
            }
            catch (Exception ex) { Log.Error("ZCheckTimer_Tick", ex); }
        }

        private void ZTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_moving) return;
                EnsureVisibleAndOnTop("poll");
                UpdateVisibility();
            }
            catch (Exception ex) { Log.Error("ZTimer_Tick", ex); }
        }

        // WinEvent hooks are asynchronous accessibility notifications delivered to our message loop.
        // They are NOT input hooks: they never sit in the mouse/keyboard path and cannot stall the system.
        private void InstallWinEventHooks()
        {
            try
            {
                uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;

                _fgProc = new Native.WinEventProc(OnForegroundChanged);
                _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, _fgProc, 0, 0, flags);

                // The shell fires REORDER on the desktop window right before it raises the taskbar.
                _reorderProc = new Native.WinEventProc(OnReorder);
                _reorderHook = Native.SetWinEventHook(Native.EVENT_OBJECT_REORDER, Native.EVENT_OBJECT_REORDER,
                    IntPtr.Zero, _reorderProc, 0, 0, flags);

                if (_fgHook == IntPtr.Zero || _reorderHook == IntPtr.Zero)
                    Log.Write("SetWinEventHook failed; relying on the 1 s safety tick");

                if (Log.DebugEnabled)
                {
                    _dbgProc = new Native.WinEventProc(OnDebugWindowEvent);
                    _dbgShowHook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_HIDE,
                        IntPtr.Zero, _dbgProc, 0, 0, flags);
                    _dbgCloakHook = Native.SetWinEventHook(Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_UNCLOAKED,
                        IntPtr.Zero, _dbgProc, 0, 0, flags);
                }
            }
            catch (Exception ex) { Log.Error("InstallWinEventHooks", ex); }
        }

        private void RemoveWinEventHooks()
        {
            if (_fgHook != IntPtr.Zero) { Native.UnhookWinEvent(_fgHook); _fgHook = IntPtr.Zero; }
            if (_reorderHook != IntPtr.Zero) { Native.UnhookWinEvent(_reorderHook); _reorderHook = IntPtr.Zero; }
            if (_dbgShowHook != IntPtr.Zero) { Native.UnhookWinEvent(_dbgShowHook); _dbgShowHook = IntPtr.Zero; }
            if (_dbgCloakHook != IntPtr.Zero) { Native.UnhookWinEvent(_dbgCloakHook); _dbgCloakHook = IntPtr.Zero; }
        }

        private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild,
                                         uint thread, uint time)
        {
            try
            {
                if (_closing || IsDisposed || !IsHandleCreated) return;
                if (Log.DebugEnabled) Log.Debug("foreground -> '" + ClassOf(hwnd) + "'");
                ScheduleZCheck("foreground");
            }
            catch (Exception ex) { Log.Error("OnForegroundChanged", ex); }
        }

        private void OnReorder(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild,
                               uint thread, uint time)
        {
            // Cheap filter first: only the desktop's child list changing (top-level z-order) matters.
            if (hwnd != _desktopHwnd || idObject != Native.OBJID_WINDOW) return;
            if (_closing || IsDisposed || !IsHandleCreated) return;
            ScheduleZCheck("reorder");
        }

        private void OnDebugWindowEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild,
                                        uint thread, uint time)
        {
            if (hwnd != IntPtr.Zero && IsHandleCreated && hwnd == Handle && idObject == 0)
            {
                string name = evt == Native.EVENT_OBJECT_SHOW ? "SHOW" : evt == Native.EVENT_OBJECT_HIDE ? "HIDE" :
                              evt == Native.EVENT_OBJECT_CLOAKED ? "CLOAKED" : "UNCLOAKED";
                Log.Debug("OUR WINDOW event from another process: " + name);
            }
        }

        // ------------------------------------------------------------------ timers / events
        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                _monitor.Update(_settings.SelectedAdapterId);

                if (_onScreen && (_forceRedraw || _monitor.NeedsRedraw))
                    Redraw();

                if (_hovering)
                    _hoverToolTip.SetToolTip(this, _settings.ShowTooltip ? BuildInfoText() : null);
            }
            catch (Exception ex) { Log.Error("UpdateTimer_Tick", ex); }
        }

        private void DockTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_moving) return;
                Rectangle before = this.Bounds;
                PositionWindow(); // cheap; only moves the window if the target actually changed
                // Visibility and z-order are handled by the faster ZTimer / WinEvent paths. We no longer re-assert
                // topmost blindly here, which is what used to make recovery take up to a full timer interval.
                if (_onScreen && before != this.Bounds) Redraw();
            }
            catch (Exception ex) { Log.Error("DockTimer_Tick", ex); }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new MethodInvoker(delegate
            {
                PositionWindow();
                UpdateVisibility();
                _forceRedraw = true;
                Redraw();
            }));
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon ||
                e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    PositionWindow();
                    UpdateVisibility();
                    _forceRedraw = true;
                    Redraw();
                }));
            }
        }

        private string BuildInfoText()
        {
            string u = _settings.SpeedUnit;
            int dp = _settings.DecimalPlaces;
            return string.Format("Download: {0} (Peak: {1})\n" +
                                 "Upload:   {2} (Peak: {3})\n" +
                                 "Session Recv: {4}\n" +
                                 "Session Sent: {5}\n" +
                                 "Right-click for Settings / Lock",
                NetworkMonitor.FormatSpeed(_monitor.CurrentDownloadSpeed, u, dp),
                NetworkMonitor.FormatSpeed(_monitor.PeakDownloadSpeed, u, 1),
                NetworkMonitor.FormatSpeed(_monitor.CurrentUploadSpeed, u, dp),
                NetworkMonitor.FormatSpeed(_monitor.PeakUploadSpeed, u, 1),
                NetworkMonitor.FormatBytesTotal(_monitor.TotalSessionReceived),
                NetworkMonitor.FormatBytesTotal(_monitor.TotalSessionSent));
        }

        private void UpdateTrayText()
        {
            try
            {
                string tip = string.Format("▲ {0}\n▼ {1}",
                    NetworkMonitor.FormatSpeed(_monitor.CurrentUploadSpeed, _settings.SpeedUnit, 1),
                    NetworkMonitor.FormatSpeed(_monitor.CurrentDownloadSpeed, _settings.SpeedUnit, 1));
                if (tip.Length > 63) tip = tip.Substring(0, 63);
                _trayIcon.Text = tip;
            }
            catch { }
        }

        // ------------------------------------------------------------------ rendering
        private void Redraw()
        {
            if (!IsHandleCreated || IsDisposed || !_onScreen) return;
            int w = this.Width, h = this.Height;
            if (w <= 0 || h <= 0) return;

            try
            {
                if (_surface == null || _surface.Width != w || _surface.Height != h)
                {
                    if (_surface != null) _surface.Dispose();
                    _surface = new Surface(w, h);
                }
                PaintBar(_surface.G, w, h);
                _surface.Present(Handle);
                _forceRedraw = false;
            }
            catch (Exception ex) { Log.Error("Redraw", ex); }
        }

        private string SampleText()
        {
            int dp = _settings.DecimalPlaces;
            switch ((_settings.SpeedUnit ?? "Auto"))
            {
                case "KB/s": return NetworkMonitor.FormatSpeed(99999.0 * 1024, "KB/s", dp);
                case "MB/s": return NetworkMonitor.FormatSpeed(999.9 * 1048576, "MB/s", dp);
                case "Mbps": return NetworkMonitor.FormatSpeed(9999.9 * 125000, "Mbps", dp);
                case "Kbps": return NetworkMonitor.FormatSpeed(999999.0 * 125, "Kbps", dp);
                default: return NetworkMonitor.FormatSpeed(1023.9 * 1048576, "Auto", dp);
            }
        }

        private void EnsureFonts(Graphics g, int barHeight)
        {
            float rowH = (barHeight - 2 * Scaled(3)) / 2f;
            float px = Math.Max(7f, Math.Min(11f * _scale, rowH * 0.9f));

            if (_rateFont != null && !_fontsDirty && Math.Abs(px - _fontPx) < 0.01f) return;

            if (_rateFont != null) _rateFont.Dispose();
            if (_iconFont != null) _iconFont.Dispose();
            _rateFont = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
            _iconFont = new Font("Segoe UI", px * 0.85f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontPx = px;

            _iconW = g.MeasureString("▲", _iconFont, PointF.Empty, _sf).Width + px * 0.45f;
            _textW = _iconW + g.MeasureString(SampleText(), _rateFont, PointF.Empty, _sf).Width;
            _fontsDirty = false;
        }

        private void PaintBar(Graphics g, int w, int h)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // grayscale AA: ClearType is wrong on alpha surfaces
            g.CompositingMode = CompositingMode.SourceOver;

            EnsureFonts(g, h);

            // Background. Transparent mode paints alpha=1: invisible, but the window still receives mouse input.
            if (_settings.TransparentBackground)
            {
                g.Clear(Color.FromArgb(1, 0, 0, 0));
            }
            else
            {
                int a = (int)Math.Round(255 * Math.Max(0.1, Math.Min(1.0, _settings.BackgroundOpacity)));
                g.Clear(Color.FromArgb(a, _settings.BackgroundColor));
                g.DrawRectangle(_winBorderPen, 0.5f, 0.5f, w - 1, h - 1);
            }

            int padding = Scaled(3);
            int spacing = Math.Max(0, Math.Min(60, Scaled(_settings.ComponentSpacing)));
            Rectangle textRect = Rectangle.Empty;
            Rectangle graphRect = Rectangle.Empty;

            if (_settings.DisplayMode == "TextOnly")
            {
                textRect = new Rectangle(padding, padding, w - padding * 2, h - padding * 2);
            }
            else if (_settings.DisplayMode == "GraphOnly")
            {
                graphRect = new Rectangle(padding, padding, w - padding * 2, h - padding * 2);
            }
            else
            {
                int textWidth = (int)Math.Ceiling(Math.Min(_textW + Scaled(4), w * 0.7f));
                int available = w - padding * 2 - spacing;
                int gw = Math.Max(Scaled(20), available - textWidth);

                if (_settings.TextPlacement == "Right")
                {
                    graphRect = new Rectangle(padding, padding, gw, h - padding * 2);
                    textRect = new Rectangle(graphRect.Right + spacing, padding, textWidth, h - padding * 2);
                }
                else
                {
                    textRect = new Rectangle(padding, padding, textWidth, h - padding * 2);
                    graphRect = new Rectangle(textRect.Right + spacing, padding, gw, h - padding * 2);
                }
            }

            if (!textRect.IsEmpty) RenderSpeedText(g, textRect);
            if (!graphRect.IsEmpty && graphRect.Width > 10 && graphRect.Height > 10) RenderHistogram(g, graphRect);
        }

        private void RenderSpeedText(Graphics g, Rectangle rect)
        {
            string upStr = NetworkMonitor.FormatSpeed(_monitor.CurrentUploadSpeed, _settings.SpeedUnit, _settings.DecimalPlaces);
            string downStr = NetworkMonitor.FormatSpeed(_monitor.CurrentDownloadSpeed, _settings.SpeedUnit, _settings.DecimalPlaces);

            int rowH = rect.Height / 2;
            g.SetClip(rect);
            DrawRateRow(g, rect.Left, rect.Top, rowH, rect.Right, "▲", upStr, _upText);
            DrawRateRow(g, rect.Left, rect.Top + rowH, rowH, rect.Right, "▼", downStr, _downText);
            g.ResetClip();
        }

        private void DrawRateRow(Graphics g, int x, int top, int rowH, int right, string icon, string text, Brush brush)
        {
            float ty = top + (rowH - _rateFont.Height) / 2f;
            float iy = top + (rowH - _iconFont.Height) / 2f;
            g.DrawString(icon, _iconFont, brush, new RectangleF(x, iy, _iconW, _iconFont.Height + 2), _sf);
            g.DrawString(text, _rateFont, brush, new RectangleF(x + _iconW, ty, Math.Max(1, right - x - _iconW), _rateFont.Height + 2), _sf);
        }

        private void RenderHistogram(Graphics g, Rectangle rect)
        {
            if (!_settings.TransparentBackground)
                g.FillRectangle(_graphBgBrush, rect);

            if (_settings.ShowHistogramBorder)
                g.DrawRectangle(_borderPen, rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);

            int count = _monitor.HistoryCapacity;
            if (count < 2) return;

            double maxSpeed = 10240; // minimum baseline 10 KB/s
            if (_settings.ScaleMode == "Fixed")
                maxSpeed = Math.Max(1024, _settings.FixedMaxBytesPerSec);
            else
                maxSpeed = Math.Max(maxSpeed, _monitor.HistoryMax());

            if (_line.Length != count)
            {
                _line = new PointF[count];
                _poly = new PointF[count + 2];
            }

            if (_settings.GraphStyle == "Mirror")
            {
                float midY = rect.Top + rect.Height / 2f;
                float amp = rect.Height / 2f - 1;
                if (_settings.ShowHistogramBorder) g.DrawLine(_borderPen, rect.Left, midY, rect.Right, midY);
                DrawSeries(g, rect, true, maxSpeed, midY, -1f, amp, _upFillMirror, _upPen);
                DrawSeries(g, rect, false, maxSpeed, midY, 1f, amp, _downFillMirror, _downPen);
            }
            else if (_settings.GraphStyle == "Stacked")
            {
                int midY = rect.Top + rect.Height / 2;
                Rectangle top = new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height / 2);
                Rectangle bottom = new Rectangle(rect.Left, midY, rect.Width, rect.Bottom - midY);
                DrawSeries(g, top, true, maxSpeed, top.Bottom, -1f, top.Height - 1, _upFillSingle, _upPen);
                DrawSeries(g, bottom, false, maxSpeed, bottom.Bottom, -1f, bottom.Height - 1, _downFillSingle, _downPen);
                if (_settings.ShowHistogramBorder) g.DrawLine(_borderPen, rect.Left, midY, rect.Right, midY);
            }
            else // Overlay
            {
                DrawSeries(g, rect, false, maxSpeed, rect.Bottom, -1f, rect.Height - 1, _downFillSingle, _downPen);
                DrawSeries(g, rect, true, maxSpeed, rect.Bottom, -1f, rect.Height - 1, _upFillSingle, _upPen);
            }
        }

        /// <summary>Builds the polyline (and fill polygon) for one series from the ring buffer and draws it.</summary>
        private void DrawSeries(Graphics g, Rectangle rect, bool upload, double maxSpeed,
                                float baseY, float dir, float amp, Brush fill, Pen pen)
        {
            int count = _monitor.HistoryCapacity;
            float stepX = (float)rect.Width / (count - 1);

            _poly[0] = new PointF(rect.Left, baseY);
            for (int i = 0; i < count; i++)
            {
                double v = upload ? _monitor.UpAt(i) : _monitor.DownAt(i);
                float f = (float)(v / maxSpeed);
                if (f > 1f) f = 1f;
                PointF p = new PointF(rect.Left + i * stepX, baseY + dir * f * amp);
                _line[i] = p;
                _poly[i + 1] = p;
            }
            _poly[count + 1] = new PointF(rect.Left + (count - 1) * stepX, baseY);

            if (fill != null) g.FillPolygon(fill, _poly);
            g.DrawLines(pen, _line);
        }

        // ------------------------------------------------------------------ settings dialog
        public void OpenSettingsDialog()
        {
            if (_settingsOpen || IsDisposed) return;
            _settingsOpen = true;
            try
            {
                using (SettingsForm form = new SettingsForm(_settings))
                {
                    form.SettingsApplied += delegate { ApplySettings(); };
                    form.TopMost = true;
                    form.ShowDialog(this);
                }
            }
            catch (Exception ex) { Log.Error("OpenSettingsDialog", ex); }
            finally
            {
                _settingsOpen = false;
                Native.TrimMemory();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;

                if (_updateTimer != null) _updateTimer.Dispose();
                RemoveWinEventHooks();
                if (_dockTimer != null) _dockTimer.Dispose();
                if (_zTimer != null) _zTimer.Dispose();
                if (_zCheckTimer != null) _zCheckTimer.Dispose();
                if (_trimTimer != null) _trimTimer.Dispose();
                if (_trayClickTimer != null) _trayClickTimer.Dispose();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                if (_hoverToolTip != null) _hoverToolTip.Dispose();
                if (_contextMenu != null) _contextMenu.Dispose();
                if (_menuBoldFont != null) _menuBoldFont.Dispose();
                if (_surface != null) { _surface.Dispose(); _surface = null; }
                if (_rateFont != null) _rateFont.Dispose();
                if (_iconFont != null) _iconFont.Dispose();
                if (_sf != null) _sf.Dispose();
                DisposeGdi();
                if (_monitor != null) _monitor.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
