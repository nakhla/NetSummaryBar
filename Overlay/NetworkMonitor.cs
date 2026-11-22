using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NetSummaryBar
{
    public class AdapterInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public NetworkInterfaceType InterfaceType { get; set; }
        public OperationalStatus Status { get; set; }

        public override string ToString()
        {
            return Name + " (" + Description + ")";
        }
    }

    /// <summary>
    /// Samples interface byte counters.
    ///  - Deltas are computed PER ADAPTER, so an adapter appearing/disappearing never produces a fake spike.
    ///  - Default mode follows the interface that currently holds the route to the internet,
    ///    which avoids double counting VPN/tunnel + physical traffic.
    ///  - The NetworkInterface list is cached and refreshed only on network-change events
    ///    (plus a slow fallback), instead of being rebuilt every tick.
    /// </summary>
    public class NetworkMonitor : IDisposable
    {
        /// <summary>SelectedAdapterId value meaning "active internet connection".</summary>
        public const string AdapterAuto = "";
        /// <summary>SelectedAdapterId value meaning "sum of all physical adapters".</summary>
        public const string AdapterAll = "*ALL";

        [DllImport("iphlpapi.dll")]
        private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

        public double CurrentDownloadSpeed { get; private set; } // Bytes/sec
        public double CurrentUploadSpeed { get; private set; }   // Bytes/sec
        public double PeakDownloadSpeed { get; private set; }
        public double PeakUploadSpeed { get; private set; }
        public long TotalSessionReceived { get; private set; }
        public long TotalSessionSent { get; private set; }

        /// <summary>True when the last sample changed what the histogram/text would show.</summary>
        public bool NeedsRedraw { get; private set; }

        // ---- ring buffers (always full, zero-filled) ----
        private double[] _down;
        private double[] _up;
        private int _cap;
        private int _head;      // index of the next write == oldest sample
        private int _nonZero;   // number of slots holding a non-zero sample

        // ---- adapter tracking ----
        private NetworkInterface[] _selected = new NetworkInterface[0];
        private readonly Dictionary<string, long[]> _prev = new Dictionary<string, long[]>();
        private volatile bool _listDirty = true;
        private string _selectedId = "";
        private long _lastRefreshMs;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _prevTicks = -1;
        private bool _disposed;

        public NetworkMonitor(int historyPoints)
        {
            SetCapacity(historyPoints);
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        }

        private void OnNetworkChanged(object sender, EventArgs e) { _listDirty = true; }
        private void OnAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e) { _listDirty = true; }

        // ------------------------------------------------------------------ history

        public int HistoryCapacity { get { return _cap; } }

        /// <summary>i = 0 is the oldest sample, HistoryCapacity-1 the newest.</summary>
        public double DownAt(int i) { return _down[(_head + i) % _cap]; }
        public double UpAt(int i) { return _up[(_head + i) % _cap]; }

        public void SetCapacity(int n)
        {
            n = Math.Max(20, Math.Min(300, n));
            if (n == _cap) return;

            double[] nd = new double[n];
            double[] nu = new double[n];
            if (_down != null)
            {
                int keep = Math.Min(n, _cap);
                for (int i = 0; i < keep; i++)
                {
                    // copy the newest 'keep' samples to the END of the new buffers
                    nd[n - keep + i] = DownAt(_cap - keep + i);
                    nu[n - keep + i] = UpAt(_cap - keep + i);
                }
            }
            _down = nd;
            _up = nu;
            _cap = n;
            _head = 0;

            _nonZero = 0;
            for (int i = 0; i < n; i++)
                if (_down[i] > 0 || _up[i] > 0) _nonZero++;
            NeedsRedraw = true;
        }

        private void Push(double down, double up)
        {
            int slot = _head;
            bool droppedNonZero = _down[slot] > 0 || _up[slot] > 0;
            bool newNonZero = down > 0 || up > 0;

            _down[slot] = down;
            _up[slot] = up;
            _head = (_head + 1) % _cap;

            if (droppedNonZero) _nonZero--;
            if (newNonZero) _nonZero++;

            NeedsRedraw = _nonZero > 0 || droppedNonZero;
        }

        /// <summary>Highest sample (either direction) currently in the history window.</summary>
        public double HistoryMax()
        {
            double m = 0;
            for (int i = 0; i < _cap; i++)
            {
                if (_down[i] > m) m = _down[i];
                if (_up[i] > m) m = _up[i];
            }
            return m;
        }

        // ------------------------------------------------------------------ adapters

        public static List<AdapterInfo> GetAvailableAdapters()
        {
            var list = new List<AdapterInfo>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    list.Add(new AdapterInfo
                    {
                        Id = ni.Id,
                        Name = ni.Name,
                        Description = ni.Description,
                        InterfaceType = ni.NetworkInterfaceType,
                        Status = ni.OperationalStatus
                    });
                }
            }
            catch { }
            return list;
        }

        private static bool IsVirtualOrTunnel(NetworkInterface ni)
        {
            switch (ni.NetworkInterfaceType)
            {
                case NetworkInterfaceType.Loopback:
                case NetworkInterfaceType.Tunnel:
                case NetworkInterfaceType.Ppp:
                    return true;
            }
            string d = (ni.Description ?? "") + " " + (ni.Name ?? "");
            string[] markers = { "Hyper-V", "vEthernet", "VMware", "VirtualBox", "Virtual Adapter", "Pseudo",
                                 "Npcap", "WAN Miniport", "TAP-Windows", "Wintun", "WireGuard", "Bluetooth" };
            for (int i = 0; i < markers.Length; i++)
            {
                if (d.IndexOf(markers[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static int GetBestRouteIndex()
        {
            try
            {
                uint idx;
                // 8.8.8.8 - any public address works; we only ask which interface the route table would use.
                uint dest = BitConverter.ToUInt32(new byte[] { 8, 8, 8, 8 }, 0);
                if (GetBestInterface(dest, out idx) == 0) return (int)idx;
            }
            catch { }
            return -1;
        }

        private static int GetIfIndex(NetworkInterface ni)
        {
            try
            {
                IPInterfaceProperties p = ni.GetIPProperties();
                try { return p.GetIPv4Properties().Index; }
                catch { return p.GetIPv6Properties().Index; }
            }
            catch { return -1; }
        }

        private void RefreshList()
        {
            _listDirty = false;
            _lastRefreshMs = _clock.ElapsedMilliseconds;

            var sel = new List<NetworkInterface>();
            try
            {
                NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();

                if (_selectedId == AdapterAll)
                {
                    foreach (var ni in all)
                        if (ni.OperationalStatus == OperationalStatus.Up && !IsVirtualOrTunnel(ni)) sel.Add(ni);
                }
                else if (_selectedId.Length > 0)
                {
                    foreach (var ni in all)
                    {
                        if (ni.OperationalStatus == OperationalStatus.Up &&
                            ni.Id.Equals(_selectedId, StringComparison.OrdinalIgnoreCase))
                        {
                            sel.Add(ni);
                            break;
                        }
                    }
                }
                else
                {
                    int best = GetBestRouteIndex();
                    if (best >= 0)
                    {
                        foreach (var ni in all)
                        {
                            if (ni.OperationalStatus != OperationalStatus.Up) continue;
                            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                            if (GetIfIndex(ni) == best) { sel.Add(ni); break; }
                        }
                    }
                    if (sel.Count == 0)
                    {
                        // offline or route lookup failed: fall back to physical adapters
                        foreach (var ni in all)
                            if (ni.OperationalStatus == OperationalStatus.Up && !IsVirtualOrTunnel(ni)) sel.Add(ni);
                    }
                }
            }
            catch (Exception ex) { Log.Error("RefreshList", ex); }

            _selected = sel.ToArray();

            // forget baselines of adapters that are no longer tracked
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _selected.Length; i++) keep.Add(_selected[i].Id);
            var stale = new List<string>();
            foreach (string k in _prev.Keys) if (!keep.Contains(k)) stale.Add(k);
            for (int i = 0; i < stale.Count; i++) _prev.Remove(stale[i]);
        }

        /// <summary>Drops all counter baselines; the next tick reports 0 instead of a bogus delta.</summary>
        public void Reset()
        {
            _prev.Clear();
            _listDirty = true;
        }

        private static long Delta(long prev, long cur)
        {
            if (cur >= prev) return cur - prev;
            // 32-bit counter wrap (older drivers) vs. interface reset
            if (prev > 0xE0000000L && prev <= 0xFFFFFFFFL && cur < 0x20000000L)
                return cur + 0x100000000L - prev;
            return 0;
        }

        // ------------------------------------------------------------------ sampling

        public void Update(string selectedAdapterId)
        {
            string wanted = selectedAdapterId ?? "";
            if (!string.Equals(wanted, _selectedId, StringComparison.Ordinal))
            {
                _selectedId = wanted;
                _prev.Clear();
                _listDirty = true;
            }

            if (_listDirty || _clock.ElapsedMilliseconds - _lastRefreshMs > 60000)
                RefreshList();

            long ticks = _clock.ElapsedTicks;
            double dt = _prevTicks < 0 ? 0 : (ticks - _prevTicks) / (double)Stopwatch.Frequency;
            _prevTicks = ticks;

            long sumRecv = 0, sumSent = 0;
            for (int i = 0; i < _selected.Length; i++)
            {
                NetworkInterface ni = _selected[i];
                try
                {
                    IPInterfaceStatistics st = ni.GetIPStatistics();
                    long r = st.BytesReceived;
                    long s = st.BytesSent;
                    long[] p;
                    if (_prev.TryGetValue(ni.Id, out p))
                    {
                        sumRecv += Delta(p[0], r);
                        sumSent += Delta(p[1], s);
                    }
                    else
                    {
                        p = new long[2];
                        _prev[ni.Id] = p;
                    }
                    p[0] = r;
                    p[1] = s;
                }
                catch
                {
                    _listDirty = true; // interface vanished; rebuild next tick
                }
            }

            if (dt > 0.05)
            {
                CurrentDownloadSpeed = sumRecv / dt;
                CurrentUploadSpeed = sumSent / dt;
                TotalSessionReceived += sumRecv;
                TotalSessionSent += sumSent;
                if (CurrentDownloadSpeed > PeakDownloadSpeed) PeakDownloadSpeed = CurrentDownloadSpeed;
                if (CurrentUploadSpeed > PeakUploadSpeed) PeakUploadSpeed = CurrentUploadSpeed;
            }
            else if (dt == 0)
            {
                CurrentDownloadSpeed = 0;
                CurrentUploadSpeed = 0;
            }

            Push(CurrentDownloadSpeed, CurrentUploadSpeed);
        }

        // ------------------------------------------------------------------ formatting

        private static readonly string[] Formats = { "0", "0.0", "0.00", "0.000" };

        public static string FormatSpeed(double bytesPerSec, string unit, int decimalPlaces)
        {
            string fmt = Formats[Math.Max(0, Math.Min(3, decimalPlaces))];
            CultureInfo inv = CultureInfo.InvariantCulture;

            if (string.Equals(unit, "KB/s", StringComparison.OrdinalIgnoreCase))
                return (bytesPerSec / 1024.0).ToString(fmt, inv) + " KB/s";
            if (string.Equals(unit, "MB/s", StringComparison.OrdinalIgnoreCase))
                return (bytesPerSec / (1024.0 * 1024.0)).ToString(fmt, inv) + " MB/s";
            if (string.Equals(unit, "Mbps", StringComparison.OrdinalIgnoreCase))
                return (bytesPerSec * 8.0 / 1000000.0).ToString(fmt, inv) + " Mbps";
            if (string.Equals(unit, "Kbps", StringComparison.OrdinalIgnoreCase))
                return (bytesPerSec * 8.0 / 1000.0).ToString(fmt, inv) + " Kbps";

            // Auto
            if (bytesPerSec >= 1024.0 * 1024.0 * 1024.0)
                return (bytesPerSec / (1024.0 * 1024.0 * 1024.0)).ToString(fmt, inv) + " GB/s";
            if (bytesPerSec >= 1024.0 * 1024.0)
                return (bytesPerSec / (1024.0 * 1024.0)).ToString(fmt, inv) + " MB/s";
            if (bytesPerSec >= 1024.0)
                return (bytesPerSec / 1024.0).ToString(fmt, inv) + " KB/s";
            return Math.Round(bytesPerSec).ToString("0", inv) + " B/s";
        }

        public static string FormatBytesTotal(long bytes)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            if (bytes >= 1024L * 1024 * 1024 * 1024)
                return (bytes / (1024.0 * 1024 * 1024 * 1024)).ToString("0.00", inv) + " TB";
            if (bytes >= 1024L * 1024 * 1024)
                return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", inv) + " GB";
            if (bytes >= 1024L * 1024)
                return (bytes / (1024.0 * 1024)).ToString("0.00", inv) + " MB";
            if (bytes >= 1024L)
                return (bytes / 1024.0).ToString("0.0", inv) + " KB";
            return bytes.ToString(inv) + " B";
        }
    }
}
