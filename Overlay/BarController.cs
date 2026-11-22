using System;
using System.Windows.Forms;

namespace NetSummaryBar
{
    /// <summary>
    /// Owns the application lifetime (instead of the bar window being the "main form").
    ///
    /// The bar is an owned window of the taskbar, and Windows destroys a taskbar's owned windows when the taskbar
    /// itself is destroyed (Explorer restart or crash, monitor unplugged, "show taskbar on all displays" turned off).
    /// If the bar were the main form, that would terminate the whole app. Here the bar is just a disposable view:
    /// when it is lost, a fresh one is built shortly afterwards and re-docks to the new taskbar.
    /// </summary>
    internal sealed class BarController : ApplicationContext
    {
        private readonly AppSettings _settings;
        private FloatingBarForm _bar;
        private bool _firstRun;
        private bool _exiting;
        private readonly Timer _rebuildTimer;
        private int _windowStartTick;
        private int _rebuildsInWindow;

        public BarController(AppSettings settings, bool firstRun)
        {
            _settings = settings;
            _firstRun = firstRun;

            Application.ApplicationExit += delegate { _exiting = true; };

            _rebuildTimer = new Timer();
            _rebuildTimer.Tick += RebuildTimer_Tick;

            CreateBar();
        }

        private void CreateBar()
        {
            FloatingBarForm bar = new FloatingBarForm(_settings);
            bar.FirstRun = _firstRun;
            _firstRun = false;
            bar.HandleLost += OnBarLost;
            _bar = bar;
            bar.Show();
        }

        /// <summary>Called from the single-instance signal (any thread): open Settings on the current bar.</summary>
        public void OpenSettings()
        {
            FloatingBarForm b = _bar;
            try
            {
                if (b != null && !b.IsDisposed && b.IsHandleCreated)
                    b.BeginInvoke(new MethodInvoker(b.OpenSettingsDialog));
            }
            catch { }
        }

        private void OnBarLost(object sender, EventArgs e)
        {
            if (_exiting) return;

            // Give Explorer a moment to rebuild its taskbar, and never tear the bar down from inside its own
            // handle-destroyed callback. Back off if this keeps happening so we cannot spin.
            int now = Environment.TickCount;
            if (_rebuildsInWindow == 0 || unchecked(now - _windowStartTick) > 60000)
            {
                _windowStartTick = now;
                _rebuildsInWindow = 0;
            }
            _rebuildsInWindow++;

            _rebuildTimer.Stop();
            _rebuildTimer.Interval = _rebuildsInWindow > 5 ? 5000 : 500;
            _rebuildTimer.Start();
        }

        private void RebuildTimer_Tick(object sender, EventArgs e)
        {
            _rebuildTimer.Stop();
            if (_exiting) return;

            FloatingBarForm dead = _bar;
            _bar = null;
            try
            {
                if (dead != null) dead.Dispose();
            }
            catch (Exception ex) { Log.Error("disposing the lost bar", ex); }

            try
            {
                CreateBar();
                Log.Write("Bar rebuilt after its window was lost.");
            }
            catch (Exception ex)
            {
                Log.Error("rebuilding the bar", ex);
                _rebuildTimer.Interval = 5000; // try again shortly
                _rebuildTimer.Start();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _exiting = true;
                _rebuildTimer.Dispose();
                FloatingBarForm b = _bar;
                _bar = null;
                if (b != null && !b.IsDisposed) b.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
