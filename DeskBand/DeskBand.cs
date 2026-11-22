using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CSDeskBand;
using CSDeskBand.ContextMenu;

[assembly: AssemblyTitle("Net Summary Bar DeskBand")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: ComVisible(false)]

namespace NetSummaryBar
{
    /// <summary>
    /// Taskbar deskband version of NetSummaryBar. Explorer hosts this class directly inside the taskbar,
    /// so there is no overlay window, no z-order handling and no Show Desktop flicker.
    /// </summary>
    [ComVisible(true)]
    [Guid("8E3B7C54-5C0A-4B63-9D0B-6A1F2C7D41E9")]
    [CSDeskBandRegistration(Name = "Net Summary Bar", ShowDeskBand = true)]
    public class NetSummaryDeskBand : CSDeskBandWin
    {
        private readonly BarControl _control;

        public NetSummaryDeskBand()
        {
            StartupManager.Disabled = true;
            SettingsForm.DeskBandMode = true;
            Log.Init(null);
            Log.Write("DeskBand: constructing in process " + System.Diagnostics.Process.GetCurrentProcess().ProcessName +
                      ", CLR " + Environment.Version);
            try
            {
                _control = new BarControl(this);
                ApplySize();
                try { Log.Write("DeskBand: forcing handle, h=0x" + _control.Handle.ToInt64().ToString("X")); }
                catch (Exception ex2) { Log.Error("DeskBand handle creation", ex2); }
            }
            catch (Exception ex) { Log.Error("DeskBand ctor", ex); throw; }

            DeskBandMenuAction settings = new DeskBandMenuAction("Net Summary Bar settings...");
            settings.Clicked += delegate { _control.RequestSettings(); };
            Options.ContextMenuItems = new List<DeskBandMenuItem> { settings };
        }

        protected override Control Control { get { return _control; } }

        internal void ApplySize()
        {
            AppSettings s = _control.Settings;
            float scale = _control.Scale;
            int w = (int)Math.Round(Math.Max(40, s.BarWidth) * scale);
            Options.MinHorizontalSize = new DeskBandSize(w, (int)Math.Round(24 * scale));
            Options.HorizontalSize = new DeskBandSize(w, CSDeskBandOptions.TaskbarHorizontalHeightLarge);
            Options.MinVerticalSize = new DeskBandSize((int)Math.Round(40 * scale), (int)Math.Round(40 * scale));
        }

        protected override void DeskbandOnClosed()
        {
            _control.Dispose();
        }
    }

    internal sealed class BarControl : Control
    {
        private readonly NetSummaryDeskBand _owner;
        private AppSettings _settings;
        private readonly NetworkMonitor _monitor;
        private readonly Timer _timer;
        private bool _settingsOpen;
        private ToolTip _tip;
        private Timer _tipDelay;
        private bool _hover, _tipShown;
        private bool _loggedTick;
        private float _scale = 1f;

        private Pen _upPen, _downPen, _borderPen, _winBorderPen;
        private SolidBrush _upFillMirror, _downFillMirror, _upFillSingle, _downFillSingle, _upText, _downText, _graphBg;
        private Font _rateFont, _iconFont;
        private float _fontPx, _iconW, _textW;
        private bool _fontsDirty = true;
        private StringFormat _sf;
        private PointF[] _line = new PointF[0], _poly = new PointF[0];

        public AppSettings Settings { get { return _settings; } }
        public float Scale { get { return _scale; } }

        public BarControl(NetSummaryDeskBand owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) _scale = Math.Max(1f, g.DpiX / 96f);
            _sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            _sf.FormatFlags = (_sf.FormatFlags & ~StringFormatFlags.NoClip) | StringFormatFlags.NoWrap;
            _sf.Trimming = StringTrimming.None;

            _settings = AppSettings.Load();
            _monitor = new NetworkMonitor(_settings.HistoryPoints);
            RebuildGdi();

            _tip = new ToolTip();
            _tip.UseFading = false;
            _tip.UseAnimation = false;
            _tipDelay = new Timer();
            _tipDelay.Interval = 400;
            _tipDelay.Tick += delegate { _tipDelay.Stop(); if (_hover) ShowTip(); };

            _timer = new Timer();
            _timer.Interval = Math.Max(250, _settings.UpdateIntervalMs);
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private Surface _surface;

        // Layered mode (per-pixel alpha, so DWM composites us over the taskbar's acrylic) is switched on AFTER the
        // shell has re-parented the control into the taskbar: creating a layered window up front fails.
        // If Windows refuses it, we silently stay in normal (classic transparent) painting.
        private bool _layered, _layeredTried;
        private int _renderCalls;

        private void Render()
        {
            if (!IsHandleCreated || IsDisposed) return;
            if (!_layered)
            {
                if (_layeredTried || !TryEnableLayered()) { Invalidate(); return; }
            }
            RenderLayered();
        }

        private bool TryEnableLayered()
        {
            _renderCalls++;
            if (_renderCalls < 3) return false; // let the shell parent us first
            _layeredTried = true;
            try
            {
                IntPtr ex = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
                Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr(ex.ToInt64() | Native.WS_EX_LAYERED));
                _layered = true;
                if (RenderLayered()) { Log.Write("DeskBand: layered (per-pixel alpha) mode ON"); return true; }
                _layered = false;
                Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, ex);
                Log.Write("DeskBand: layered mode refused by Windows; using classic transparent painting");
            }
            catch (Exception e) { _layered = false; Log.Error("DeskBand layered", e); }
            Invalidate();
            return false;
        }

        private bool RenderLayered()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return false;
            try
            {
                if (_surface == null || _surface.Width != w || _surface.Height != h)
                {
                    if (_surface != null) _surface.Dispose();
                    _surface = new Surface(w, h);
                }
                Graphics g = _surface.G;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.FromArgb(1, 0, 0, 0)); // alpha 1: invisible, but still hit-testable (right-click menu)
                g.CompositingMode = CompositingMode.SourceOver;
                PaintBar(g, w, h);
                return _surface.Present(Handle);
            }
            catch (Exception ex) { Log.Error("DeskBand.RenderLayered", ex); return false; }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Render(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) Render(); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (_layered) return;
            if (_settings.TransparentBackground) base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_layered) return; // content is pushed by RenderLayered()
            PaintBar(e.Graphics, ClientSize.Width, ClientSize.Height);
        }

        private int Scaled(int v) { return (int)Math.Round(v * _scale); }

        private void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (!_loggedTick) { _loggedTick = true; Log.Write("DeskBand: first tick, handle=" + IsHandleCreated + " visible=" + Visible); }
                // Nothing to draw while the taskbar/band is hidden (auto-hide, locked screen): skip the work.
                if (!IsHandleCreated || !Visible) return;
                _monitor.Update(_settings.SelectedAdapterId);
                Render();
                if (_tipShown && _hover) ShowTip(); // keep the numbers live while hovering
            }
            catch (Exception ex) { Log.Error("DeskBand.Timer_Tick", ex); }
        }

        // ------------------------------------------------------------------ tooltip
        private string BuildInfoText()
        {
            string u = _settings.SpeedUnit;
            int dp = _settings.DecimalPlaces;
            return string.Format("Download: {0} (Peak: {1})\n" +
                                 "Upload:   {2} (Peak: {3})\n" +
                                 "Session Recv: {4}\n" +
                                 "Session Sent: {5}",
                NetworkMonitor.FormatSpeed(_monitor.CurrentDownloadSpeed, u, dp),
                NetworkMonitor.FormatSpeed(_monitor.PeakDownloadSpeed, u, 1),
                NetworkMonitor.FormatSpeed(_monitor.CurrentUploadSpeed, u, dp),
                NetworkMonitor.FormatSpeed(_monitor.PeakUploadSpeed, u, 1),
                NetworkMonitor.FormatBytesTotal(_monitor.TotalSessionReceived),
                NetworkMonitor.FormatBytesTotal(_monitor.TotalSessionSent));
        }

        private void ShowTip()
        {
            if (!_settings.ShowTooltip) return;
            try
            {
                // Place above the band when the taskbar is on the lower half of the screen, below it otherwise.
                Rectangle scr = Screen.FromControl(this).Bounds;
                Point onScreen = PointToScreen(Point.Empty);
                bool above = onScreen.Y > scr.Top + scr.Height / 2;
                int y = above ? -112 : Height + 4;
                _tip.Show(BuildInfoText(), this, Math.Min(Width / 4, 20), y);
                _tipShown = true;
            }
            catch (Exception ex) { Log.Error("DeskBand.ShowTip", ex); }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            _tipDelay.Stop();
            _tipDelay.Start();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _tipDelay.Stop();
            _tipShown = false;
            try { _tip.Hide(this); } catch { }
        }

        public void RequestSettings()
        {
            // The menu callback runs inside a COM call from the shell; open the dialog afterwards.
            if (!IsHandleCreated) return;
            BeginInvoke(new MethodInvoker(OpenSettings));
        }

        private void OpenSettings()
        {
            if (_settingsOpen || IsDisposed) return;
            _settingsOpen = true;
            try
            {
                using (SettingsForm form = new SettingsForm(_settings))
                {
                    form.SettingsApplied += delegate { ApplySettings(); };
                    form.TopMost = true;
                    form.ShowDialog();
                }
            }
            catch (Exception ex) { Log.Error("DeskBand.OpenSettings", ex); }
            finally { _settingsOpen = false; }
        }

        private void ApplySettings()
        {
            if (!_settings.ShowTooltip) { _tipShown = false; try { _tip.Hide(this); } catch { } }
            _monitor.SetCapacity(_settings.HistoryPoints);
            _timer.Interval = Math.Max(250, _settings.UpdateIntervalMs);
            RebuildGdi();
            _fontsDirty = true;
            _owner.ApplySize();
            Render();
        }

        // ------------------------------------------------------------------ painting
        private void DisposeGdi()
        {
            IDisposable[] all = { _upPen, _downPen, _borderPen, _winBorderPen, _upFillMirror, _downFillMirror,
                                  _upFillSingle, _downFillSingle, _upText, _downText, _graphBg };
            foreach (IDisposable d in all) if (d != null) d.Dispose();
            _upPen = _downPen = _borderPen = _winBorderPen = null;
            _upFillMirror = _downFillMirror = _upFillSingle = _downFillSingle = _upText = _downText = _graphBg = null;
        }

        private void RebuildGdi()
        {
            DisposeGdi();
            float pw = Math.Max(1f, 1.2f * _scale);
            Color up = _settings.UploadColor, down = _settings.DownloadColor;
            _upPen = new Pen(up, pw); _downPen = new Pen(down, pw);
            _upPen.LineJoin = LineJoin.Round; _downPen.LineJoin = LineJoin.Round;
            _borderPen = new Pen(_settings.HistogramBorderColor, 1f);
            _winBorderPen = new Pen(Color.FromArgb(45, 255, 255, 255), 1f);
            _upFillMirror = new SolidBrush(Color.FromArgb(120, up));
            _downFillMirror = new SolidBrush(Color.FromArgb(120, down));
            _upFillSingle = new SolidBrush(Color.FromArgb(90, up));
            _downFillSingle = new SolidBrush(Color.FromArgb(90, down));
            _upText = new SolidBrush(_settings.TextMatchesGraphColor ? up : _settings.CustomTextColor);
            _downText = new SolidBrush(_settings.TextMatchesGraphColor ? down : _settings.CustomTextColor);
            _graphBg = new SolidBrush(Color.FromArgb(20, 0, 0, 0));
        }

        private string SampleText()
        {
            int dp = _settings.DecimalPlaces;
            switch (_settings.SpeedUnit ?? "Auto")
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

        private bool _loggedPaint;
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Log.Write("DeskBand: control handle created, parent=0x" + (Parent != null ? Parent.Handle.ToInt64().ToString("X") : "none"));
        }

        private void PaintBar(Graphics g, int w, int h)
        {
            try
            {
                if (!_loggedPaint)
                {
                    _loggedPaint = true;
                    Log.Write("DeskBand: first paint, size " + ClientSize.Width + "x" + ClientSize.Height + ", visible=" + Visible);
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                EnsureFonts(g, h);

                if (!_settings.TransparentBackground)
                {
                    int a = (int)Math.Round(255 * Math.Max(0.1, Math.Min(1.0, _settings.BackgroundOpacity)));
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(a, _settings.BackgroundColor)))
                        g.FillRectangle(bg, 0, 0, w, h);
                    g.DrawRectangle(_winBorderPen, 0.5f, 0.5f, w - 1, h - 1);
                }

                int padding = Scaled(3);
                int spacing = Math.Max(0, Math.Min(60, Scaled(_settings.ComponentSpacing)));
                Rectangle textRect = Rectangle.Empty, graphRect = Rectangle.Empty;

                if (_settings.DisplayMode == "TextOnly")
                    textRect = new Rectangle(padding, padding, w - padding * 2, h - padding * 2);
                else if (_settings.DisplayMode == "GraphOnly")
                    graphRect = new Rectangle(padding, padding, w - padding * 2, h - padding * 2);
                else
                {
                    int textWidth = (int)Math.Ceiling(Math.Min(_textW + Scaled(4), w * 0.7f));
                    int gw = Math.Max(Scaled(20), w - padding * 2 - spacing - textWidth);
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
            catch (Exception ex) { Log.Error("DeskBand.PaintBar", ex); }
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
            if (!_settings.TransparentBackground) g.FillRectangle(_graphBg, rect);
            if (_settings.ShowHistogramBorder)
                g.DrawRectangle(_borderPen, rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);

            int count = _monitor.HistoryCapacity;
            if (count < 2) return;

            double maxSpeed = 10240;
            if (_settings.ScaleMode == "Fixed") maxSpeed = Math.Max(1024, _settings.FixedMaxBytesPerSec);
            else maxSpeed = Math.Max(maxSpeed, _monitor.HistoryMax());

            if (_line.Length != count) { _line = new PointF[count]; _poly = new PointF[count + 2]; }

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
            else
            {
                DrawSeries(g, rect, false, maxSpeed, rect.Bottom, -1f, rect.Height - 1, _downFillSingle, _downPen);
                DrawSeries(g, rect, true, maxSpeed, rect.Bottom, -1f, rect.Height - 1, _upFillSingle, _upPen);
            }
        }

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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                _tipDelay.Dispose();
                _tip.Dispose();
                _monitor.Dispose();
                DisposeGdi();
                if (_surface != null) _surface.Dispose();
                if (_rateFont != null) _rateFont.Dispose();
                if (_iconFont != null) _iconFont.Dispose();
                _sf.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
