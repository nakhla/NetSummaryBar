using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Collections.Generic;

namespace NetSummaryBar
{
    public class SettingsForm : Form
    {
        public event EventHandler SettingsApplied;

        private readonly AppSettings _settings;
        private readonly AppSettings _original;   // snapshot used to revert if the dialog is cancelled after Apply
        private bool _applied;

        // Color Controls
        private Panel _pnlDownColor;
        private Panel _pnlUpColor;
        private CheckBox _chkTransparent;
        private Label _lblBg;
        private Panel _pnlBgColor;
        private Button _btnPickBg;
        private Label _lblOpacity;
        private TrackBar _tbOpacity;
        private Label _lblOpacityVal;
        private CheckBox _chkTextMatch;

        // Speed Units
        private ComboBox _cbUnit;
        private NumericUpDown _nudDecimals;

        // Graph
        private ComboBox _cbGraphStyle;
        private ComboBox _cbScaleMode;
        private NumericUpDown _nudFixedSpeedMbps;
        private CheckBox _chkShowBorder;
        private Panel _pnlBorderColor;
        private Button _btnPickBorder;

        // Layout
        private ComboBox _cbDisplayMode;
        private ComboBox _cbTextPlacement;
        /// <summary>Set by the deskband host: hides every option that only makes sense for the floating bar.</summary>
        public static bool DeskBandMode;
        private CheckBox _chkTooltip;
        private NumericUpDown _nudSpacing;

        // Window & Taskbar
        private NumericUpDown _nudWidth;
        private NumericUpDown _nudHeight;
        private CheckBox _chkAlwaysOnTop;
        private CheckBox _chkAutoDock;
        private CheckBox _chkLocked;
        private CheckBox _chkStartup;
        private CheckBox _chkHideFullscreen;
        private ComboBox _cbMonitor;
        private System.Windows.Forms.Screen[] _screens;

        // Adapter
        private ComboBox _cbAdapters;
        private List<AdapterInfo> _adapterList;

        // Buttons
        private Button _btnApply;
        private Button _btnSave;
        private Button _btnCancel;
        private Button _btnDockNow;
        private Button _btnResetDefaults;
        private Timer _applyFeedbackTimer;

        public SettingsForm(AppSettings settings)
        {
            _settings = settings;
            _original = settings.Clone();
            InitializeComponent();
            LoadFrom(_settings);
            SmoothText(this);
        }

        /// <summary>Renders labels, checkboxes and buttons with GDI+ anti-aliased text (smoother than plain GDI).</summary>
        private static void SmoothText(Control root)
        {
            foreach (Control c in root.Controls)
            {
                ButtonBase b = c as ButtonBase;
                if (b != null) b.UseCompatibleTextRendering = true;
                Label l = c as Label;
                if (l != null) l.UseCompatibleTextRendering = true;
                if (c.HasChildren) SmoothText(c);
            }
        }

        private void InitializeComponent()
        {
            this.AutoScaleDimensions = new SizeF(96F, 96F);
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.Text = "NetSummaryBar - Settings & Customization";
            this.Size = new Size(520, 640);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = true;
            this.ShowInTaskbar = true;
            this.TopMost = true;
            this.Font = new Font("Segoe UI", 9f);
            this.BackColor = Color.FromArgb(245, 245, 248);

            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
            }
            catch { }

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Top;
            tabs.Height = 525;

            // Tab 1: Colors & Appearance
            TabPage tabColors = new TabPage("🎨 Colors");
            tabColors.BackColor = Color.White;
            tabColors.Padding = new Padding(15);
            BuildColorsAndUnitsTab(tabColors);
            tabs.TabPages.Add(tabColors);

            // Tab 2: Graph & Layout
            TabPage tabGraph = new TabPage("📊 Display");
            tabGraph.BackColor = Color.White;
            tabGraph.Padding = new Padding(15);
            BuildGraphAndLayoutTab(tabGraph);
            tabs.TabPages.Add(tabGraph);

            // Tab 3: Taskbar & Behavior
            TabPage tabTaskbar = new TabPage(DeskBandMode ? "⚙ Band" : "⚙ Taskbar");
            tabTaskbar.BackColor = Color.White;
            tabTaskbar.Padding = new Padding(15);
            BuildTaskbarTab(tabTaskbar);
            tabs.TabPages.Add(tabTaskbar);

            // Tab 4: About
            TabPage tabAbout = new TabPage("ℹ About");
            tabAbout.BackColor = Color.White;
            tabAbout.Padding = new Padding(15);
            BuildAboutTab(tabAbout);
            tabs.TabPages.Add(tabAbout);

            this.Controls.Add(tabs);

            // Bottom action panel
            Panel pnlBottom = new Panel();
            pnlBottom.Dock = DockStyle.Bottom;
            pnlBottom.Height = 65;
            pnlBottom.BackColor = Color.FromArgb(238, 238, 242);

            _btnResetDefaults = new Button();
            _btnResetDefaults.Text = "Reset Defaults";
            _btnResetDefaults.Location = new Point(15, 18);
            _btnResetDefaults.Size = new Size(105, 30);
            _btnResetDefaults.Click += (s, e) => ResetDefaults();
            pnlBottom.Controls.Add(_btnResetDefaults);

            // Apply Button (Applies live without closing)
            _btnApply = new Button();
            _btnApply.Text = "Apply";
            _btnApply.Location = new Point(190, 18);
            _btnApply.Size = new Size(90, 30);
            _btnApply.BackColor = Color.FromArgb(225, 230, 240);
            _btnApply.FlatStyle = FlatStyle.Flat;
            _btnApply.Click += BtnApply_Click;
            pnlBottom.Controls.Add(_btnApply);

            // Save & Close Button
            _btnSave = new Button();
            _btnSave.Text = "Save";
            _btnSave.Location = new Point(290, 18);
            _btnSave.Size = new Size(110, 30);
            _btnSave.BackColor = Color.FromArgb(0, 120, 215);
            _btnSave.ForeColor = Color.White;
            _btnSave.FlatStyle = FlatStyle.Flat;
            _btnSave.Click += BtnSave_Click;
            pnlBottom.Controls.Add(_btnSave);

            // Cancel Button
            _btnCancel = new Button();
            _btnCancel.Text = "Cancel";
            _btnCancel.Location = new Point(410, 18);
            _btnCancel.Size = new Size(85, 30);
            _btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };
            this.FormClosing += (s, e) =>
            {
                if (this.DialogResult != DialogResult.OK) RevertIfApplied();
            };
            pnlBottom.Controls.Add(_btnCancel);

            this.AcceptButton = _btnSave;
            this.CancelButton = _btnCancel;
            this.Controls.Add(pnlBottom);
        }

        private void BuildColorsAndUnitsTab(TabPage page)
        {
            int top = 15;

            // Group: Speed Rates & Units
            GroupBox grpUnits = new GroupBox();
            grpUnits.Text = "Download & Upload Speed Units";
            grpUnits.Location = new Point(15, top);
            grpUnits.Size = new Size(460, 110);

            Label lblUnit = new Label();
            lblUnit.Text = "Display Unit:";
            lblUnit.Location = new Point(15, 30);
            lblUnit.AutoSize = true;
            grpUnits.Controls.Add(lblUnit);

            _cbUnit = new ComboBox();
            _cbUnit.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbUnit.Items.AddRange(new object[] {
                "Auto (Dynamic B/s, KB/s, MB/s)",
                "KB/s (Kilobytes per second)",
                "MB/s (Megabytes per second)",
                "Mbps (Megabits per second)",
                "Kbps (Kilobits per second)"
            });
            _cbUnit.Location = new Point(160, 27);
            _cbUnit.Size = new Size(270, 24);
            grpUnits.Controls.Add(_cbUnit);

            Label lblDec = new Label();
            lblDec.Text = "Decimal Precision:";
            lblDec.Location = new Point(15, 68);
            lblDec.AutoSize = true;
            grpUnits.Controls.Add(lblDec);

            _nudDecimals = new NumericUpDown();
            _nudDecimals.Minimum = 0;
            _nudDecimals.Maximum = 3;
            _nudDecimals.Value = 1;
            _nudDecimals.Location = new Point(160, 66);
            _nudDecimals.Size = new Size(70, 24);
            grpUnits.Controls.Add(_nudDecimals);

            page.Controls.Add(grpUnits);
            top += 125;

            // Group: Colors & Transparency
            GroupBox grpColors = new GroupBox();
            grpColors.Text = "Custom Colors & Transparency";
            grpColors.Location = new Point(15, top);
            grpColors.Size = new Size(460, 260);

            // Download Color
            Label lblDown = new Label { Text = "Download Color (▼):", Location = new Point(15, 26), AutoSize = true };
            grpColors.Controls.Add(lblDown);
            _pnlDownColor = CreateColorSwatch(160, 24);
            Button btnPickDown = CreatePickButton(210, 22, _pnlDownColor);
            grpColors.Controls.Add(_pnlDownColor);
            grpColors.Controls.Add(btnPickDown);

            // Upload Color
            Label lblUp = new Label { Text = "Upload Color (▲):", Location = new Point(15, 62), AutoSize = true };
            grpColors.Controls.Add(lblUp);
            _pnlUpColor = CreateColorSwatch(160, 60);
            Button btnPickUp = CreatePickButton(210, 58, _pnlUpColor);
            grpColors.Controls.Add(_pnlUpColor);
            grpColors.Controls.Add(btnPickUp);

            // Transparent background checkbox
            _chkTransparent = new CheckBox();
            _chkTransparent.Text = "Transparent background";
            _chkTransparent.Location = new Point(15, 96);
            _chkTransparent.AutoSize = true;
            _chkTransparent.CheckedChanged += (s, e) => UpdateBackgroundControlsState();
            grpColors.Controls.Add(_chkTransparent);

            // Background Color
            _lblBg = new Label { Text = "Background Color:", Location = new Point(15, 130), AutoSize = true };
            grpColors.Controls.Add(_lblBg);
            _pnlBgColor = CreateColorSwatch(160, 128);
            _btnPickBg = CreatePickButton(210, 126, _pnlBgColor);
            grpColors.Controls.Add(_pnlBgColor);
            grpColors.Controls.Add(_btnPickBg);

            // Background Opacity
            _lblOpacity = new Label { Text = "Window Opacity:", Location = new Point(15, 172), AutoSize = true };
            grpColors.Controls.Add(_lblOpacity);
            _tbOpacity = new TrackBar();
            _tbOpacity.Minimum = 20;
            _tbOpacity.Maximum = 100;
            _tbOpacity.Value = 95;
            _tbOpacity.TickFrequency = 10;
            _tbOpacity.Location = new Point(155, 167);
            _tbOpacity.Size = new Size(200, 45);
            grpColors.Controls.Add(_tbOpacity);

            _lblOpacityVal = new Label { Text = "95%", Location = new Point(365, 172), AutoSize = true };
            grpColors.Controls.Add(_lblOpacityVal);
            _tbOpacity.Scroll += (s, e) => { _lblOpacityVal.Text = _tbOpacity.Value + "%"; };

            // Text color matches graph color checkbox
            _chkTextMatch = new CheckBox();
            _chkTextMatch.Text = "Text color matches download/upload rate colors";
            _chkTextMatch.Location = new Point(15, 222);
            _chkTextMatch.AutoSize = true;
            _chkTextMatch.Checked = true;
            grpColors.Controls.Add(_chkTextMatch);

            page.Controls.Add(grpColors);
        }

        private void BuildGraphAndLayoutTab(TabPage page)
        {
            int top = 15;

            GroupBox grpGraph = new GroupBox();
            grpGraph.Text = "Histogram & Graph Settings";
            grpGraph.Location = new Point(15, top);
            grpGraph.Size = new Size(460, 205);

            // Graph Style
            Label lblStyle = new Label { Text = "Histogram Style:", Location = new Point(15, 28), AutoSize = true };
            grpGraph.Controls.Add(lblStyle);
            _cbGraphStyle = new ComboBox();
            _cbGraphStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbGraphStyle.Items.AddRange(new object[] {
                "Mirror (Crener style: Upload Up, Download Down)",
                "Stacked (Upload Top Half, Download Bottom Half)",
                "Overlay (Both overlap full height)"
            });
            _cbGraphStyle.Location = new Point(150, 25);
            _cbGraphStyle.Size = new Size(280, 24);
            grpGraph.Controls.Add(_cbGraphStyle);

            // Scale Mode
            Label lblScale = new Label { Text = "Y-Axis Scale:", Location = new Point(15, 68), AutoSize = true };
            grpGraph.Controls.Add(lblScale);
            _cbScaleMode = new ComboBox();
            _cbScaleMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbScaleMode.Items.AddRange(new object[] {
                "Dynamic Auto-scale (adapts to peak traffic)",
                "Fixed Maximum Bandwidth"
            });
            _cbScaleMode.Location = new Point(150, 65);
            _cbScaleMode.Size = new Size(280, 24);
            grpGraph.Controls.Add(_cbScaleMode);

            // Fixed Speed (if selected)
            Label lblFixed = new Label { Text = "Fixed Max (Mbps):", Location = new Point(15, 108), AutoSize = true };
            grpGraph.Controls.Add(lblFixed);
            _nudFixedSpeedMbps = new NumericUpDown();
            _nudFixedSpeedMbps.Minimum = 1;
            _nudFixedSpeedMbps.Maximum = 10000;
            _nudFixedSpeedMbps.Value = 100;
            _nudFixedSpeedMbps.Location = new Point(150, 105);
            _nudFixedSpeedMbps.Size = new Size(100, 24);
            grpGraph.Controls.Add(_nudFixedSpeedMbps);

            // Histogram Border Settings
            _chkShowBorder = new CheckBox { Text = "Show Histogram Border & Baseline", Location = new Point(15, 148), AutoSize = true, Checked = true };
            _chkShowBorder.CheckedChanged += (s, e) =>
            {
                _pnlBorderColor.Enabled = _chkShowBorder.Checked;
                if (_btnPickBorder != null) _btnPickBorder.Enabled = _chkShowBorder.Checked;
            };
            grpGraph.Controls.Add(_chkShowBorder);

            Label lblBorder = new Label { Text = "Border Color:", Location = new Point(15, 175), AutoSize = true };
            grpGraph.Controls.Add(lblBorder);

            _pnlBorderColor = CreateColorSwatch(150, 172);
            _btnPickBorder = CreatePickButton(200, 170, _pnlBorderColor);
            grpGraph.Controls.Add(_pnlBorderColor);
            grpGraph.Controls.Add(_btnPickBorder);

            page.Controls.Add(grpGraph);
            top += 220;

            GroupBox grpLayout = new GroupBox();
            grpLayout.Text = "Display & Arrangement";
            grpLayout.Location = new Point(15, top);
            grpLayout.Size = new Size(460, 155);

            Label lblDisp = new Label { Text = "Components:", Location = new Point(15, 30), AutoSize = true };
            grpLayout.Controls.Add(lblDisp);
            _cbDisplayMode = new ComboBox();
            _cbDisplayMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbDisplayMode.Items.AddRange(new object[] {
                "Both Speed Text and Histogram",
                "Speed Text Only",
                "Histogram Only"
            });
            _cbDisplayMode.Location = new Point(165, 27);
            _cbDisplayMode.Size = new Size(265, 24);
            grpLayout.Controls.Add(_cbDisplayMode);

            Label lblPos = new Label { Text = "Text Position:", Location = new Point(15, 72), AutoSize = true };
            grpLayout.Controls.Add(lblPos);
            _cbTextPlacement = new ComboBox();
            _cbTextPlacement.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbTextPlacement.Items.AddRange(new object[] {
                "Text on Left, Histogram on Right",
                "Histogram on Left, Text on Right"
            });
            _cbTextPlacement.Location = new Point(165, 69);
            _cbTextPlacement.Size = new Size(265, 24);
            grpLayout.Controls.Add(_cbTextPlacement);

            // Spacing option between text and histogram
            Label lblSpace = new Label { Text = "Spacing:", Location = new Point(15, 114), AutoSize = true };
            grpLayout.Controls.Add(lblSpace);

            _nudSpacing = new NumericUpDown { Minimum = 0, Maximum = 60, Value = 8, Location = new Point(165, 112), Size = new Size(70, 24) };
            grpLayout.Controls.Add(_nudSpacing);

            Label lblSpacePx = new Label { Text = "px", Location = new Point(242, 114), AutoSize = true };
            grpLayout.Controls.Add(lblSpacePx);

            page.Controls.Add(grpLayout);
        }

        private void BuildTaskbarTab(TabPage page)
        {
            int top = 15;

            // Network Adapter
            GroupBox grpAdapter = new GroupBox();
            grpAdapter.Text = "Network Interface to Monitor";
            grpAdapter.Location = new Point(15, top);
            grpAdapter.Size = new Size(460, 80);

            _cbAdapters = new ComboBox();
            _cbAdapters.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbAdapters.Location = new Point(15, 30);
            _cbAdapters.Size = new Size(425, 24);
            grpAdapter.Controls.Add(_cbAdapters);

            page.Controls.Add(grpAdapter);
            top += 95;

            // Bar Dimensions & Docking
            GroupBox grpDock = new GroupBox();
            grpDock.Text = DeskBandMode ? "Band Size & Behavior" : "Size & Taskbar Integration";
            grpDock.Location = new Point(15, top);
            grpDock.Size = new Size(460, 300);

            Label lblW = new Label { Text = "Bar Width (px):", Location = new Point(15, 30), AutoSize = true };
            grpDock.Controls.Add(lblW);
            _nudWidth = new NumericUpDown { Minimum = 90, Maximum = 600, Location = new Point(150, 28), Size = new Size(90, 24) };
            grpDock.Controls.Add(_nudWidth);

            Label lblH = new Label { Text = "Bar Height (px):", Location = new Point(15, 65), AutoSize = true };
            grpDock.Controls.Add(lblH);
            _nudHeight = new NumericUpDown { Minimum = 24, Maximum = 80, Location = new Point(150, 63), Size = new Size(90, 24) };
            grpDock.Controls.Add(_nudHeight);

            Label lblMon = new Label { Text = "Dock on monitor:", Location = new Point(15, 102), AutoSize = true };
            grpDock.Controls.Add(lblMon);
            _cbMonitor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(150, 99), Size = new Size(290, 24) };
            grpDock.Controls.Add(_cbMonitor);

            _chkAutoDock = new CheckBox { Text = "Automatically dock next to Windows Taskbar Tray", Location = new Point(18, 137), AutoSize = true };
            grpDock.Controls.Add(_chkAutoDock);

            _chkAlwaysOnTop = new CheckBox { Text = "Keep Always on Top (Taskbar level)", Location = new Point(18, 162), AutoSize = true };
            grpDock.Controls.Add(_chkAlwaysOnTop);

            _chkHideFullscreen = new CheckBox { Text = "Hide during fullscreen / taskbar auto-hide", Location = new Point(18, 187), AutoSize = true };
            grpDock.Controls.Add(_chkHideFullscreen);

            _chkLocked = new CheckBox { Text = "Lock bar position (prevent mouse dragging)", Location = new Point(18, 212), AutoSize = true };
            grpDock.Controls.Add(_chkLocked);

            _chkStartup = new CheckBox { Text = "Start automatically on Windows boot", Location = new Point(18, 237), AutoSize = true };
            _chkStartup.Visible = !StartupManager.Disabled;
            grpDock.Controls.Add(_chkStartup);

            _chkTooltip = new CheckBox { Text = "Show tooltip with speeds and totals when hovering", Location = new Point(18, 262), AutoSize = true };
            grpDock.Controls.Add(_chkTooltip);

            if (DeskBandMode)
            {
                // Position, docking, z-order, fullscreen and autostart are handled by the taskbar itself.
                foreach (Control c in new Control[] { lblH, _nudHeight, lblMon, _cbMonitor, _chkAutoDock, _chkAlwaysOnTop,
                                                      _chkHideFullscreen, _chkLocked, _chkStartup })
                    c.Visible = false;
                _chkTooltip.Location = new Point(18, 68);
                grpDock.Size = new Size(460, 105);
            }

            page.Controls.Add(grpDock);
            top += DeskBandMode ? 120 : 315;

            _btnDockNow = new Button();
            _btnDockNow.Text = "📌 Dock to Taskbar Tray Now";
            _btnDockNow.Location = new Point(15, top);
            _btnDockNow.Size = new Size(220, 32);
            _btnDockNow.Click += (s, e) =>
            {
                _chkAutoDock.Checked = true;
                BtnApply_Click(null, EventArgs.Empty);
            };
            _btnDockNow.Visible = !DeskBandMode;
            page.Controls.Add(_btnDockNow);
        }

        private void BuildAboutTab(TabPage page)
        {
            Label title = new Label();
            title.Text = "NetSummaryBar";
            title.Font = new Font(this.Font.FontFamily, 18f, FontStyle.Bold);
            title.Location = new Point(20, 25);
            title.AutoSize = true;
            page.Controls.Add(title);

            Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string edition = DeskBandMode ? "Deskband edition" : "Overlay edition";
            if (v != null && v.Major > 0) edition += " ver. " + v.Major + "." + v.Minor;
            Label sub = new Label();
            sub.Text = edition;
            sub.ForeColor = Color.DimGray;
            sub.Location = new Point(23, 70);
            sub.AutoSize = true;
            page.Controls.Add(sub);

            Label desc = new Label();
            desc.Text = "A tiny taskbar network monitor: live download / upload speed\r\nand a histogram of recent activity.";
            desc.Location = new Point(23, 110);
            desc.AutoSize = true;
            page.Controls.Add(desc);

            Label by = new Label();
            by.Text = "Created by";
            by.ForeColor = Color.DimGray;
            by.Location = new Point(23, 175);
            by.AutoSize = true;
            page.Controls.Add(by);

            Label author = new Label();
            author.Text = "Magdi Nakhla";
            author.Font = new Font(this.Font.FontFamily, 12f, FontStyle.Bold);
            author.Location = new Point(22, 197);
            author.AutoSize = true;
            page.Controls.Add(author);

            LinkLabel link = new LinkLabel();
            link.Text = "https://github.com/nakhla";
            link.Location = new Point(23, 232);
            link.AutoSize = true;
            link.LinkClicked += delegate
            {
                try { System.Diagnostics.Process.Start("https://github.com/nakhla"); }
                catch (Exception ex) { Log.Error("About link", ex); }
            };
            page.Controls.Add(link);
        }

        private Panel CreateColorSwatch(int x, int y)
        {
            var pnl = new Panel();
            pnl.Location = new Point(x, y);
            pnl.Size = new Size(40, 24);
            pnl.BorderStyle = BorderStyle.FixedSingle;
            return pnl;
        }

        private Button CreatePickButton(int x, int y, Panel targetSwatch)
        {
            var btn = new Button();
            btn.Text = "Change...";
            btn.Location = new Point(x, y);
            btn.Size = new Size(80, 26);
            btn.Click += (s, e) =>
            {
                using (ColorDialog cd = new ColorDialog())
                {
                    cd.Color = targetSwatch.BackColor;
                    cd.FullOpen = true;
                    if (cd.ShowDialog() == DialogResult.OK)
                    {
                        targetSwatch.BackColor = cd.Color;
                    }
                }
            };
            return btn;
        }

        private void UpdateBackgroundControlsState()
        {
            bool customBg = !_chkTransparent.Checked;
            if (_lblBg != null) _lblBg.Enabled = customBg;
            if (_pnlBgColor != null) _pnlBgColor.Enabled = customBg;
            if (_btnPickBg != null) _btnPickBg.Enabled = customBg;
            if (_lblOpacity != null) _lblOpacity.Enabled = customBg;
            if (_tbOpacity != null) _tbOpacity.Enabled = customBg;
            if (_lblOpacityVal != null) _lblOpacityVal.Enabled = customBg;
        }

        private void LoadFrom(AppSettings src)
        {
            // Colors
            _pnlDownColor.BackColor = src.DownloadColor;
            _pnlUpColor.BackColor = src.UploadColor;
            _chkTransparent.Checked = src.TransparentBackground;
            UpdateBackgroundControlsState();
            _pnlBgColor.BackColor = src.BackgroundColor;
            int opInt = (int)Math.Round(src.BackgroundOpacity * 100);
            _tbOpacity.Value = Math.Max(20, Math.Min(100, opInt));
            _lblOpacityVal.Text = _tbOpacity.Value + "%";
            _chkTextMatch.Checked = src.TextMatchesGraphColor;

            // Units
            if (string.Equals(src.SpeedUnit, "KB/s", StringComparison.OrdinalIgnoreCase)) _cbUnit.SelectedIndex = 1;
            else if (string.Equals(src.SpeedUnit, "MB/s", StringComparison.OrdinalIgnoreCase)) _cbUnit.SelectedIndex = 2;
            else if (string.Equals(src.SpeedUnit, "Mbps", StringComparison.OrdinalIgnoreCase)) _cbUnit.SelectedIndex = 3;
            else if (string.Equals(src.SpeedUnit, "Kbps", StringComparison.OrdinalIgnoreCase)) _cbUnit.SelectedIndex = 4;
            else _cbUnit.SelectedIndex = 0; // Auto

            _nudDecimals.Value = Math.Max(0, Math.Min(3, src.DecimalPlaces));

            // Graph Style
            if (string.Equals(src.GraphStyle, "Stacked", StringComparison.OrdinalIgnoreCase)) _cbGraphStyle.SelectedIndex = 1;
            else if (string.Equals(src.GraphStyle, "Overlay", StringComparison.OrdinalIgnoreCase)) _cbGraphStyle.SelectedIndex = 2;
            else _cbGraphStyle.SelectedIndex = 0; // Mirror

            // Scale
            if (string.Equals(src.ScaleMode, "Fixed", StringComparison.OrdinalIgnoreCase)) _cbScaleMode.SelectedIndex = 1;
            else _cbScaleMode.SelectedIndex = 0;

            _nudFixedSpeedMbps.Value = Math.Max(1, src.FixedMaxBytesPerSec / 125000); // Bytes/s to Mbps approx

            // Border
            _chkShowBorder.Checked = src.ShowHistogramBorder;
            _pnlBorderColor.BackColor = src.HistogramBorderColor;
            _pnlBorderColor.Enabled = src.ShowHistogramBorder;
            if (_btnPickBorder != null) _btnPickBorder.Enabled = src.ShowHistogramBorder;

            // Display Mode
            if (string.Equals(src.DisplayMode, "TextOnly", StringComparison.OrdinalIgnoreCase)) _cbDisplayMode.SelectedIndex = 1;
            else if (string.Equals(src.DisplayMode, "GraphOnly", StringComparison.OrdinalIgnoreCase)) _cbDisplayMode.SelectedIndex = 2;
            else _cbDisplayMode.SelectedIndex = 0;

            // Text Placement
            if (string.Equals(src.TextPlacement, "Right", StringComparison.OrdinalIgnoreCase)) _cbTextPlacement.SelectedIndex = 1;
            else _cbTextPlacement.SelectedIndex = 0;

            // Spacing
            _nudSpacing.Value = Math.Max(0, Math.Min(60, src.ComponentSpacing));

            // Dimensions
            _nudWidth.Value = Math.Max(90, Math.Min(600, src.BarWidth));
            _nudHeight.Value = Math.Max(24, Math.Min(80, src.BarHeight));
            _chkAutoDock.Checked = src.AutoDockToTaskbar;
            _chkAlwaysOnTop.Checked = src.AlwaysOnTop;
            _chkLocked.Checked = src.Locked;
            _chkStartup.Checked = src.StartWithWindows;

            _chkHideFullscreen.Checked = src.HideInFullscreen;
            _chkTooltip.Checked = src.ShowTooltip;

            // Monitors
            _cbMonitor.Items.Clear();
            _screens = System.Windows.Forms.Screen.AllScreens;
            int monIdx = 0;
            for (int i = 0; i < _screens.Length; i++)
            {
                string label = "Monitor " + (i + 1) + (_screens[i].Primary ? " (primary)" : "") +
                               "  " + _screens[i].Bounds.Width + "x" + _screens[i].Bounds.Height;
                _cbMonitor.Items.Add(label);
                if (!string.IsNullOrEmpty(src.DockScreenDevice) &&
                    string.Equals(_screens[i].DeviceName, src.DockScreenDevice, StringComparison.OrdinalIgnoreCase))
                    monIdx = i;
                else if (string.IsNullOrEmpty(src.DockScreenDevice) && _screens[i].Primary)
                    monIdx = i;
            }
            _cbMonitor.SelectedIndex = monIdx;

            // Adapters
            _cbAdapters.Items.Clear();
            _cbAdapters.Items.Add("[Auto - active internet connection (recommended)]");
            _cbAdapters.Items.Add("[All physical adapters (summed)]");
            _adapterList = NetworkMonitor.GetAvailableAdapters();
            int selectedIdx = 0;
            if (src.SelectedAdapterId == NetworkMonitor.AdapterAll) selectedIdx = 1;
            for (int i = 0; i < _adapterList.Count; i++)
            {
                var ad = _adapterList[i];
                _cbAdapters.Items.Add(ad.ToString());
                if (!string.IsNullOrEmpty(src.SelectedAdapterId) && ad.Id.Equals(src.SelectedAdapterId, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIdx = i + 2;
                }
            }
            _cbAdapters.SelectedIndex = selectedIdx;
        }

        private void ApplySettingsToObject()
        {
            // Save colors & transparency
            _settings.DownloadColor = _pnlDownColor.BackColor;
            _settings.UploadColor = _pnlUpColor.BackColor;
            _settings.TransparentBackground = _chkTransparent.Checked;
            _settings.BackgroundColor = _pnlBgColor.BackColor;
            _settings.BackgroundOpacity = _tbOpacity.Value / 100.0;
            _settings.TextMatchesGraphColor = _chkTextMatch.Checked;

            // Save units
            switch (_cbUnit.SelectedIndex)
            {
                case 1: _settings.SpeedUnit = "KB/s"; break;
                case 2: _settings.SpeedUnit = "MB/s"; break;
                case 3: _settings.SpeedUnit = "Mbps"; break;
                case 4: _settings.SpeedUnit = "Kbps"; break;
                default: _settings.SpeedUnit = "Auto"; break;
            }
            _settings.DecimalPlaces = (int)_nudDecimals.Value;

            // Save graph style
            switch (_cbGraphStyle.SelectedIndex)
            {
                case 1: _settings.GraphStyle = "Stacked"; break;
                case 2: _settings.GraphStyle = "Overlay"; break;
                default: _settings.GraphStyle = "Mirror"; break;
            }

            // Save scale
            _settings.ScaleMode = (_cbScaleMode.SelectedIndex == 1) ? "Fixed" : "Dynamic";
            _settings.FixedMaxBytesPerSec = (long)(_nudFixedSpeedMbps.Value * 125000); // Mbps to Bytes/sec

            // Save border
            _settings.ShowHistogramBorder = _chkShowBorder.Checked;
            _settings.HistogramBorderColor = _pnlBorderColor.BackColor;

            // Layout
            switch (_cbDisplayMode.SelectedIndex)
            {
                case 1: _settings.DisplayMode = "TextOnly"; break;
                case 2: _settings.DisplayMode = "GraphOnly"; break;
                default: _settings.DisplayMode = "TextAndGraph"; break;
            }

            _settings.TextPlacement = (_cbTextPlacement.SelectedIndex == 1) ? "Right" : "Left";
            _settings.ComponentSpacing = (int)_nudSpacing.Value;

            // Dimensions
            _settings.BarWidth = (int)_nudWidth.Value;
            _settings.BarHeight = (int)_nudHeight.Value;
            _settings.AutoDockToTaskbar = _chkAutoDock.Checked;
            _settings.AlwaysOnTop = _chkAlwaysOnTop.Checked;
            _settings.Locked = _chkLocked.Checked;
            _settings.HideInFullscreen = _chkHideFullscreen.Checked;
            _settings.ShowTooltip = _chkTooltip.Checked;

            // Monitor
            if (_screens != null && _cbMonitor.SelectedIndex >= 0 && _cbMonitor.SelectedIndex < _screens.Length)
            {
                var sc = _screens[_cbMonitor.SelectedIndex];
                _settings.DockScreenDevice = sc.Primary ? "" : sc.DeviceName;
            }

            // Adapters: 0 = auto, 1 = all physical, 2.. = specific adapter
            if (_cbAdapters.SelectedIndex <= 0 || _adapterList == null)
            {
                _settings.SelectedAdapterId = NetworkMonitor.AdapterAuto;
            }
            else if (_cbAdapters.SelectedIndex == 1)
            {
                _settings.SelectedAdapterId = NetworkMonitor.AdapterAll;
            }
            else
            {
                int adIdx = _cbAdapters.SelectedIndex - 2;
                if (adIdx >= 0 && adIdx < _adapterList.Count)
                {
                    _settings.SelectedAdapterId = _adapterList[adIdx].Id;
                }
            }

            // Startup (registry Run key is the source of truth)
            if (!StartupManager.Disabled && _chkStartup.Checked != StartupManager.IsEnabled())
            {
                StartupManager.Set(_chkStartup.Checked);
            }
            _settings.StartWithWindows = _chkStartup.Checked;

            _settings.Save();
        }

        private void BtnApply_Click(object sender, EventArgs e)
        {
            ApplySettingsToObject();
            _applied = true;

            // Trigger live refresh in FloatingBarForm without closing this window
            if (SettingsApplied != null)
            {
                SettingsApplied(this, EventArgs.Empty);
            }

            // Visual feedback on Apply button
            _btnApply.Text = "✔ Applied!";
            _btnApply.BackColor = Color.FromArgb(200, 240, 200);

            if (_applyFeedbackTimer == null)
            {
                _applyFeedbackTimer = new Timer();
                _applyFeedbackTimer.Interval = 1200;
                _applyFeedbackTimer.Tick += (ts, te) =>
                {
                    _applyFeedbackTimer.Stop();
                    _btnApply.Text = "Apply";
                    _btnApply.BackColor = Color.FromArgb(225, 230, 240);
                };
            }
            _applyFeedbackTimer.Stop();
            _applyFeedbackTimer.Start();
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            ApplySettingsToObject();
            if (SettingsApplied != null) SettingsApplied(this, EventArgs.Empty);
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void ResetDefaults()
        {
            if (MessageBox.Show("Reset all settings to default values?", "Confirm Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                // Only reload the controls; the live settings object is untouched until Apply / Save.
                LoadFrom(new AppSettings());
            }
        }

        private void RevertIfApplied()
        {
            if (!_applied) return;
            _applied = false;
            _settings.CopyFrom(_original);
            _settings.Save();
            if (!StartupManager.Disabled && _original.StartWithWindows != StartupManager.IsEnabled())
                StartupManager.Set(_original.StartWithWindows);
            if (SettingsApplied != null) SettingsApplied(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_applyFeedbackTimer != null) _applyFeedbackTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
