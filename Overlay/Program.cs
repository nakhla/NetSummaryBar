using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.0.0")]

namespace NetSummaryBar
{
    static class Program
    {
        private const string MutexName = "Local\\NetSummaryBar_SingleInstance_Mutex_98a72b";
        private const string ShowEventName = "Local\\NetSummaryBar_ShowSettings_Event_98a72b";

        private static Mutex _mutex;
        private static EventWaitHandle _showEvent;
        private static RegisteredWaitHandle _showWait;
        private static BarController _controller;
        private static bool _errorShown;

        [STAThread]
        static void Main(string[] args)
        {
            Log.Init(args);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Log.Error("ThreadException", e.Exception);
                ShowErrorOnce(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                Log.Error("UnhandledException", ex);
                ShowErrorOnce(ex);
            };

            bool createdNew = false;
            try
            {
                _mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (AbandonedMutexException) { createdNew = true; }
            catch { createdNew = true; }

            if (!createdNew)
            {
                // Second launch: ask the running instance to open its settings instead of showing a dialog.
                try
                {
                    EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
                    existing.Set();
                    existing.Close();
                }
                catch { }
                return;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool isFirstRun = !File.Exists(AppSettings.GetConfigPath());
                AppSettings settings = AppSettings.Load();
                _controller = new BarController(settings, isFirstRun);

                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, delegate(object state, bool timedOut)
                {
                    BarController c = _controller;
                    if (c != null) c.OpenSettings();
                }, null, -1, false);

                Application.Run(_controller);
            }
            catch (Exception ex)
            {
                Log.Error("Startup", ex);
                MessageBox.Show("NetSummaryBar failed to start.\n\n" + ex.Message + "\n\nDetails: " + Log.GetLogPath(),
                    "NetSummaryBar", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (_showWait != null && _showEvent != null) { try { _showWait.Unregister(_showEvent); } catch { } }
                if (_showEvent != null) { try { _showEvent.Close(); } catch { } }
                if (_mutex != null && createdNew)
                {
                    try { _mutex.ReleaseMutex(); } catch { }
                    try { _mutex.Dispose(); } catch { }
                }
            }
        }

        // A recurring error must never become a dialog every second: log everything, show at most one box.
        private static void ShowErrorOnce(Exception ex)
        {
            if (_errorShown) return;
            _errorShown = true;
            try
            {
                MessageBox.Show("NetSummaryBar hit an unexpected error:\n\n" + (ex != null ? ex.Message : "unknown") +
                                "\n\nFurther errors are written to:\n" + Log.GetLogPath(),
                    "NetSummaryBar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }
    }
}
