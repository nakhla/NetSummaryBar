using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace NetSummaryBar
{
    public class AppSettings
    {
        // Colors
        public string DownloadColorHex { get; set; }
        public string UploadColorHex { get; set; }
        public bool TransparentBackground { get; set; }
        public string BackgroundColorHex { get; set; }
        public double BackgroundOpacity { get; set; }
        public bool TextMatchesGraphColor { get; set; }
        public string CustomTextColorHex { get; set; }

        // Units and Number Formatting: "Auto", "KB/s", "MB/s", "Mbps", "Kbps"
        public string SpeedUnit { get; set; }
        public int DecimalPlaces { get; set; }

        // Graph: "Mirror", "Stacked", "Overlay" / "Dynamic", "Fixed"
        public string GraphStyle { get; set; }
        public string ScaleMode { get; set; }
        public long FixedMaxBytesPerSec { get; set; }
        public string HistogramBorderColorHex { get; set; }
        public bool ShowHistogramBorder { get; set; }

        // Layout: "TextAndGraph", "TextOnly", "GraphOnly" / "Left", "Right"
        public string DisplayMode { get; set; }
        public string TextPlacement { get; set; }
        public int ComponentSpacing { get; set; }

        // Dimensions and Position (BarWidth/BarHeight are in 96-dpi units, scaled at runtime)
        public int BarWidth { get; set; }
        public int BarHeight { get; set; }
        public int WindowX { get; set; }
        public int WindowY { get; set; }
        public bool AutoDockToTaskbar { get; set; }
        public bool Locked { get; set; }
        public bool AlwaysOnTop { get; set; }
        public bool HideInFullscreen { get; set; }
        public bool ShowTooltip { get; set; }
        /// <summary>Screen.DeviceName of the monitor whose taskbar to dock to; empty = primary.</summary>
        public string DockScreenDevice { get; set; }

        // Network Interface: "" = active internet connection, "*ALL" = all physical adapters, else adapter Id
        public string SelectedAdapterId { get; set; }

        // Refresh Rate
        public int UpdateIntervalMs { get; set; }
        public int HistoryPoints { get; set; }
        public bool StartWithWindows { get; set; }

        private static readonly string[] Units = { "Auto", "KB/s", "MB/s", "Mbps", "Kbps" };
        private static readonly string[] Styles = { "Mirror", "Stacked", "Overlay" };
        private static readonly string[] Scales = { "Dynamic", "Fixed" };
        private static readonly string[] Modes = { "TextAndGraph", "TextOnly", "GraphOnly" };
        private static readonly string[] Placements = { "Left", "Right" };

        public AppSettings()
        {
            DownloadColorHex = "#00E5FF";
            UploadColorHex = "#FF9100";
            TransparentBackground = true;
            BackgroundColorHex = "#181818";
            BackgroundOpacity = 0.92;
            TextMatchesGraphColor = true;
            CustomTextColorHex = "#FFFFFF";

            SpeedUnit = "Auto";
            DecimalPlaces = 1;

            GraphStyle = "Mirror";
            ScaleMode = "Dynamic";
            FixedMaxBytesPerSec = 12500000; // 100 Mbps
            HistogramBorderColorHex = "#555555";
            ShowHistogramBorder = true;

            DisplayMode = "TextAndGraph";
            TextPlacement = "Left";
            ComponentSpacing = 8;

            BarWidth = 200;
            BarHeight = 38;
            WindowX = -1;
            WindowY = -1;
            AutoDockToTaskbar = true;
            Locked = false;
            AlwaysOnTop = true;
            HideInFullscreen = true;
            ShowTooltip = true;
            DockScreenDevice = "";

            SelectedAdapterId = NetworkMonitor.AdapterAuto;
            UpdateIntervalMs = 1000;
            HistoryPoints = 60;
            StartWithWindows = false;
        }

        public Color DownloadColor
        {
            get { return HexToColor(DownloadColorHex, Color.FromArgb(0, 229, 255)); }
            set { DownloadColorHex = ColorToHex(value); }
        }

        public Color UploadColor
        {
            get { return HexToColor(UploadColorHex, Color.FromArgb(255, 145, 0)); }
            set { UploadColorHex = ColorToHex(value); }
        }

        public Color BackgroundColor
        {
            get { return HexToColor(BackgroundColorHex, Color.FromArgb(24, 24, 24)); }
            set { BackgroundColorHex = ColorToHex(value); }
        }

        public Color CustomTextColor
        {
            get { return HexToColor(CustomTextColorHex, Color.White); }
            set { CustomTextColorHex = ColorToHex(value); }
        }

        public Color HistogramBorderColor
        {
            get { return HexToColor(HistogramBorderColorHex, Color.FromArgb(85, 85, 85)); }
            set { HistogramBorderColorHex = ColorToHex(value); }
        }

        public AppSettings Clone()
        {
            return (AppSettings)MemberwiseClone();
        }

        /// <summary>Copies every persisted value from another instance (keeps this object's identity).</summary>
        public void CopyFrom(AppSettings other)
        {
            foreach (PropertyInfo p in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite || p.PropertyType == typeof(Color)) continue;
                p.SetValue(this, p.GetValue(other, null), null);
            }
        }

        public static string GetConfigPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "NetSummaryBar");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, "settings.json");
        }

        private static string Q(string s)
        {
            return "\"" + EscapeJson(s) + "\"";
        }

        private static string B(bool b)
        {
            return b ? "true" : "false";
        }

        public void Save()
        {
            try
            {
                CultureInfo inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"DownloadColorHex\": " + Q(DownloadColorHex) + ",");
                sb.AppendLine("  \"UploadColorHex\": " + Q(UploadColorHex) + ",");
                sb.AppendLine("  \"TransparentBackground\": " + B(TransparentBackground) + ",");
                sb.AppendLine("  \"BackgroundColorHex\": " + Q(BackgroundColorHex) + ",");
                sb.AppendLine("  \"BackgroundOpacity\": " + BackgroundOpacity.ToString("0.00", inv) + ",");
                sb.AppendLine("  \"TextMatchesGraphColor\": " + B(TextMatchesGraphColor) + ",");
                sb.AppendLine("  \"CustomTextColorHex\": " + Q(CustomTextColorHex) + ",");
                sb.AppendLine("  \"SpeedUnit\": " + Q(SpeedUnit) + ",");
                sb.AppendLine("  \"DecimalPlaces\": " + DecimalPlaces.ToString(inv) + ",");
                sb.AppendLine("  \"GraphStyle\": " + Q(GraphStyle) + ",");
                sb.AppendLine("  \"ScaleMode\": " + Q(ScaleMode) + ",");
                sb.AppendLine("  \"FixedMaxBytesPerSec\": " + FixedMaxBytesPerSec.ToString(inv) + ",");
                sb.AppendLine("  \"HistogramBorderColorHex\": " + Q(HistogramBorderColorHex) + ",");
                sb.AppendLine("  \"ShowHistogramBorder\": " + B(ShowHistogramBorder) + ",");
                sb.AppendLine("  \"DisplayMode\": " + Q(DisplayMode) + ",");
                sb.AppendLine("  \"TextPlacement\": " + Q(TextPlacement) + ",");
                sb.AppendLine("  \"ComponentSpacing\": " + ComponentSpacing.ToString(inv) + ",");
                sb.AppendLine("  \"BarWidth\": " + BarWidth.ToString(inv) + ",");
                sb.AppendLine("  \"BarHeight\": " + BarHeight.ToString(inv) + ",");
                sb.AppendLine("  \"WindowX\": " + WindowX.ToString(inv) + ",");
                sb.AppendLine("  \"WindowY\": " + WindowY.ToString(inv) + ",");
                sb.AppendLine("  \"AutoDockToTaskbar\": " + B(AutoDockToTaskbar) + ",");
                sb.AppendLine("  \"Locked\": " + B(Locked) + ",");
                sb.AppendLine("  \"AlwaysOnTop\": " + B(AlwaysOnTop) + ",");
                sb.AppendLine("  \"HideInFullscreen\": " + B(HideInFullscreen) + ",");
                sb.AppendLine("  \"ShowTooltip\": " + B(ShowTooltip) + ",");
                sb.AppendLine("  \"DockScreenDevice\": " + Q(DockScreenDevice) + ",");
                sb.AppendLine("  \"SelectedAdapterId\": " + Q(SelectedAdapterId) + ",");
                sb.AppendLine("  \"UpdateIntervalMs\": " + UpdateIntervalMs.ToString(inv) + ",");
                sb.AppendLine("  \"HistoryPoints\": " + HistoryPoints.ToString(inv));
                // StartWithWindows is NOT persisted: the registry Run key is the single source of truth.
                sb.AppendLine("}");

                // Atomic write: temp file, then replace. A crash can no longer leave a truncated settings file.
                string path = GetConfigPath();
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex) { Log.Error("Settings.Save", ex); }
        }

        private static bool GetBool(Dictionary<string, string> d, string key, bool def)
        {
            string v;
            if (d.TryGetValue(key, out v)) return v.Equals("true", StringComparison.OrdinalIgnoreCase);
            return def;
        }

        private static int GetInt(Dictionary<string, string> d, string key, int def, int min, int max)
        {
            string v; int n;
            if (d.TryGetValue(key, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                return Math.Max(min, Math.Min(max, n));
            return def;
        }

        private static string GetStr(Dictionary<string, string> d, string key, string def)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : def;
        }

        private static string Pick(string value, string[] allowed, string def)
        {
            for (int i = 0; i < allowed.Length; i++)
                if (string.Equals(value, allowed[i], StringComparison.OrdinalIgnoreCase)) return allowed[i];
            return def;
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                string path = GetConfigPath();
                if (File.Exists(path))
                {
                    var d = ParseSimpleJson(File.ReadAllText(path));

                    s.DownloadColorHex = GetStr(d, "DownloadColorHex", s.DownloadColorHex);
                    s.UploadColorHex = GetStr(d, "UploadColorHex", s.UploadColorHex);
                    s.TransparentBackground = GetBool(d, "TransparentBackground", s.TransparentBackground);
                    s.BackgroundColorHex = GetStr(d, "BackgroundColorHex", s.BackgroundColorHex);
                    string op;
                    double opv;
                    if (d.TryGetValue("BackgroundOpacity", out op) &&
                        double.TryParse(op, NumberStyles.Any, CultureInfo.InvariantCulture, out opv))
                        s.BackgroundOpacity = Math.Max(0.1, Math.Min(1.0, opv));
                    s.TextMatchesGraphColor = GetBool(d, "TextMatchesGraphColor", s.TextMatchesGraphColor);
                    s.CustomTextColorHex = GetStr(d, "CustomTextColorHex", s.CustomTextColorHex);
                    s.SpeedUnit = Pick(GetStr(d, "SpeedUnit", ""), Units, "Auto");
                    s.DecimalPlaces = GetInt(d, "DecimalPlaces", s.DecimalPlaces, 0, 3);
                    s.GraphStyle = Pick(GetStr(d, "GraphStyle", ""), Styles, "Mirror");
                    s.ScaleMode = Pick(GetStr(d, "ScaleMode", ""), Scales, "Dynamic");
                    string fm; long fmv;
                    if (d.TryGetValue("FixedMaxBytesPerSec", out fm) &&
                        long.TryParse(fm, NumberStyles.Integer, CultureInfo.InvariantCulture, out fmv))
                        s.FixedMaxBytesPerSec = Math.Max(125000L, fmv);
                    s.HistogramBorderColorHex = GetStr(d, "HistogramBorderColorHex", s.HistogramBorderColorHex);
                    s.ShowHistogramBorder = GetBool(d, "ShowHistogramBorder", s.ShowHistogramBorder);
                    s.DisplayMode = Pick(GetStr(d, "DisplayMode", ""), Modes, "TextAndGraph");
                    s.TextPlacement = Pick(GetStr(d, "TextPlacement", ""), Placements, "Left");
                    s.ComponentSpacing = GetInt(d, "ComponentSpacing", s.ComponentSpacing, 0, 60);
                    s.BarWidth = GetInt(d, "BarWidth", s.BarWidth, 80, 600);
                    s.BarHeight = GetInt(d, "BarHeight", s.BarHeight, 24, 100);
                    s.WindowX = GetInt(d, "WindowX", s.WindowX, -32000, 32000);
                    s.WindowY = GetInt(d, "WindowY", s.WindowY, -32000, 32000);
                    s.AutoDockToTaskbar = GetBool(d, "AutoDockToTaskbar", s.AutoDockToTaskbar);
                    s.Locked = GetBool(d, "Locked", s.Locked);
                    s.AlwaysOnTop = GetBool(d, "AlwaysOnTop", s.AlwaysOnTop);
                    s.HideInFullscreen = GetBool(d, "HideInFullscreen", s.HideInFullscreen);
                    s.ShowTooltip = GetBool(d, "ShowTooltip", s.ShowTooltip);
                    s.DockScreenDevice = GetStr(d, "DockScreenDevice", s.DockScreenDevice);
                    s.SelectedAdapterId = GetStr(d, "SelectedAdapterId", s.SelectedAdapterId);
                    s.UpdateIntervalMs = GetInt(d, "UpdateIntervalMs", s.UpdateIntervalMs, 250, 5000);
                    s.HistoryPoints = GetInt(d, "HistoryPoints", s.HistoryPoints, 20, 300);
                }
            }
            catch (Exception ex) { Log.Error("Settings.Load", ex); }

            StartupManager.Refresh();
            s.StartWithWindows = StartupManager.IsEnabled();
            return s;
        }

        public static string ColorToHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        public static Color HexToColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            try
            {
                hex = hex.Trim().TrimStart(new char[] { '#' });
                if (hex.Length == 6)
                {
                    int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                    int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                    int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                    return Color.FromArgb(r, g, b);
                }
            }
            catch { }
            return fallback;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string UnescapeJson(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    sb.Append(s[i]);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static Dictionary<string, string> ParseSimpleJson(string json)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = json.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                string tr = line.Trim();
                if (tr.EndsWith(",")) tr = tr.Substring(0, tr.Length - 1);
                int colon = tr.IndexOf(':');
                if (colon <= 0) continue;

                string key = tr.Substring(0, colon).Trim().Trim(new char[] { '"' });
                string val = tr.Substring(colon + 1).Trim();
                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
                    val = UnescapeJson(val.Substring(1, val.Length - 2));
                dict[key] = val;
            }
            return dict;
        }
    }
}
