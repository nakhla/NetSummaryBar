using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSummaryBar
{
    /// <summary>Tiny file logger (replaces modal error dialogs). Capped in size, de-duplicates repeats.</summary>
    internal static class Log
    {
        private const long MaxBytes = 256 * 1024;
        private static string _last = "";
        private static DateTime _lastTime = DateTime.MinValue;
        private static readonly object _lock = new object();

        /// <summary>
        /// Verbose diagnostics (window show/hide/z-order events). Off by default.
        /// Enable by starting the exe with --debug, or by creating an empty file named debug.flag
        /// next to settings.json. Output goes to the same error.log.
        /// </summary>
        public static bool DebugEnabled { get; private set; }

        public static void Init(string[] args)
        {
            try
            {
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                        if (string.Equals(args[i], "--debug", StringComparison.OrdinalIgnoreCase)) DebugEnabled = true;
                }
                if (File.Exists(Path.Combine(Path.GetDirectoryName(AppSettings.GetConfigPath()), "debug.flag")))
                    DebugEnabled = true;
            }
            catch { }
        }

        public static string GetLogPath()
        {
            return Path.Combine(Path.GetDirectoryName(AppSettings.GetConfigPath()), "error.log");
        }

        public static void Error(string context, Exception ex)
        {
            Write(context + ": " + (ex == null ? "(null)" : ex.ToString()));
        }

        public static void Write(string message)
        {
            Append(message, true);
        }

        /// <summary>Millisecond-stamped trace line; no-op unless debugging is enabled.</summary>
        public static void Debug(string message)
        {
            if (DebugEnabled) Append("[dbg] " + message, false);
        }

        private static void Append(string message, bool dedupe)
        {
            try
            {
                lock (_lock)
                {
                    DateTime now = DateTime.Now;
                    if (dedupe)
                    {
                        if (message == _last && (now - _lastTime).TotalSeconds < 30) return;
                        _last = message;
                        _lastTime = now;
                    }

                    string path = GetLogPath();
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        File.Delete(path);
                    }
                    File.AppendAllText(path, now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    /// <summary>Start-with-Windows via HKCU\...\Run (no COM / dynamic / DLR needed).</summary>
    internal static class StartupManager
    {
        /// <summary>Set by the deskband build: the host process is explorer.exe, so there is nothing to autostart.</summary>
        public static bool Disabled;

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "NetSummaryBar";

        private static string LegacyShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "NetSummaryBar.lnk");
        }

        private static string ExpectedValue()
        {
            return "\"" + Application.ExecutablePath + "\"";
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (k != null && k.GetValue(ValueName) != null) return true;
                }
                return File.Exists(LegacyShortcutPath());
            }
            catch { return false; }
        }

        /// <summary>Migrates the old .lnk and repairs a stale path if the exe was moved.</summary>
        public static void Refresh()
        {
            try
            {
                if (!IsEnabled()) return;
                bool legacy = File.Exists(LegacyShortcutPath());
                string current = null;
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (k != null) current = k.GetValue(ValueName) as string;
                }
                if (legacy || !string.Equals(current, ExpectedValue(), StringComparison.OrdinalIgnoreCase))
                {
                    Set(true);
                }
            }
            catch (Exception ex) { Log.Error("StartupManager.Refresh", ex); }
        }

        public static void Set(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (k != null)
                    {
                        if (enable) k.SetValue(ValueName, ExpectedValue());
                        else k.DeleteValue(ValueName, false);
                    }
                }
                string lnk = LegacyShortcutPath();
                if (File.Exists(lnk)) File.Delete(lnk);
            }
            catch (Exception ex) { Log.Error("StartupManager.Set", ex); }
        }
    }
}
