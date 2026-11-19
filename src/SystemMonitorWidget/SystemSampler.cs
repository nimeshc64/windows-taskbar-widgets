using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SysMon;

sealed class History(int capacity = 60)
{
    readonly Queue<double> _q = new();
    public void Add(double v) { _q.Enqueue(v); while (_q.Count > capacity) _q.Dequeue(); }
    public IReadOnlyList<double> Values => _q.ToArray();
    public int Capacity => capacity;
    public double Max => _q.Count == 0 ? 0 : _q.Max();
}

sealed class SystemSampler
{
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    sealed class MemoryStatusEx
    {
        public uint dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx m);

    long _idle, _kernel, _user;
    readonly Dictionary<string, (long rx, long tx)> _net = new();
    DateTime _netTime;
    DateTime _lastInternetCheck = DateTime.MinValue;
    object? _nlm;

    public double Cpu { get; private set; }
    public double MemoryLoad { get; private set; }
    public ulong MemoryTotal { get; private set; }
    public ulong MemoryAvailable { get; private set; }
    public ulong MemoryUsed => MemoryTotal - MemoryAvailable;
    public double Down { get; private set; }
    public double Up { get; private set; }
    public string AdapterName { get; private set; } = "";
    public string AdapterKind { get; private set; } = "";
    public string LocalIp { get; private set; } = "";
    public bool HasNetwork { get; private set; }
    public bool? Internet { get; private set; }

    public History CpuHistory { get; } = new();
    public History MemoryHistory { get; } = new();
    public History DownHistory { get; } = new();
    public History UpHistory { get; } = new();

    public string CpuName { get; } = ReadCpuName();
    public int LogicalProcessors { get; } = Environment.ProcessorCount;

    public void Sample()
    {
        SampleCpu();
        SampleMemory();
        SampleNetwork();
        if ((DateTime.Now - _lastInternetCheck).TotalSeconds >= 5) CheckInternet();
        CpuHistory.Add(Cpu);
        MemoryHistory.Add(MemoryLoad);
        DownHistory.Add(Down);
        UpHistory.Add(Up);
    }

    void SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;
        long dIdle = idle - _idle, dTotal = (kernel - _kernel) + (user - _user);
        if (_kernel != 0 && dTotal > 0) Cpu = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
        _idle = idle; _kernel = kernel; _user = user;
    }

    void SampleMemory()
    {
        var m = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(m)) return;
        MemoryTotal = m.ullTotalPhys;
        MemoryAvailable = m.ullAvailPhys;
        MemoryLoad = MemoryTotal == 0 ? 0 : 100.0 * (MemoryTotal - MemoryAvailable) / MemoryTotal;
    }

    static bool IsVirtual(NetworkInterface n)
    {
        var d = (n.Description + " " + n.Name).ToLowerInvariant();
        return d.Contains("virtual") || d.Contains("hyper-v") || d.Contains("vethernet") || d.Contains("vmware") ||
               d.Contains("virtualbox") || d.Contains("loopback") || d.Contains("pseudo") || d.Contains("wsl");
    }

    void SampleNetwork()
    {
        var now = DateTime.UtcNow;
        double secs = _netTime == default ? 0 : (now - _netTime).TotalSeconds;
        _netTime = now;
        long dRx = 0, dTx = 0;
        var seen = new HashSet<string>();
        NetworkInterface? primary = null;

        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (IsVirtual(n)) continue;
                IPInterfaceProperties props;
                try { props = n.GetIPProperties(); } catch { continue; }
                bool hasGateway = props.GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) &&
                                                                   !g.Address.Equals(System.Net.IPAddress.IPv6Any));
                if (!hasGateway) continue;

                var stats = n.GetIPStatistics();
                long rx = stats.BytesReceived, tx = stats.BytesSent;
                seen.Add(n.Id);
                if (_net.TryGetValue(n.Id, out var prev) && rx >= prev.rx && tx >= prev.tx)
                {
                    dRx += rx - prev.rx;
                    dTx += tx - prev.tx;
                }
                _net[n.Id] = (rx, tx);
                if (primary == null || n.Speed > primary.Speed) primary = n;
            }
        }
        catch (Exception ex) { SmLog.Write("Network sample: " + ex.Message); }

        foreach (var gone in _net.Keys.Where(k => !seen.Contains(k)).ToList()) _net.Remove(gone);

        HasNetwork = primary != null;
        Down = secs > 0 ? dRx / secs : 0;
        Up = secs > 0 ? dTx / secs : 0;
        if (primary != null)
        {
            AdapterName = primary.Name;
            AdapterKind = primary.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi"
                        : primary.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet ? "Ethernet"
                        : primary.NetworkInterfaceType.ToString();
            try
            {
                LocalIp = primary.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
            }
            catch { LocalIp = ""; }
        }
        else { AdapterName = AdapterKind = LocalIp = ""; }
    }

    void CheckInternet()
    {
        _lastInternetCheck = DateTime.Now;
        try
        {
            _nlm ??= Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!);
            Internet = (bool)((dynamic)_nlm!).IsConnectedToInternet;
        }
        catch
        {
            Internet = HasNetwork ? NetworkInterface.GetIsNetworkAvailable() : false;
        }
    }

    static string ReadCpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Processor";
        }
        catch { return "Processor"; }
    }

    public static string Speed(double bytesPerSec, bool bits)
    {
        if (bits)
        {
            double b = bytesPerSec * 8 / 1000;
            if (b < 1000) return $"{Num(b)} Kbps";
            b /= 1000; if (b < 1000) return $"{Num(b)} Mbps";
            return $"{Num(b / 1000)} Gbps";
        }
        double k = bytesPerSec / 1024;
        if (k < 1000) return $"{Num(k)} KB/s";
        k /= 1024; if (k < 1000) return $"{Num(k)} MB/s";
        return $"{Num(k / 1024)} GB/s";
    }

    static string Num(double v) => v < 10 ? v.ToString("0.0") : v.ToString("0");

    public static string Gb(ulong bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("0.0") + " GB";

    public static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{t.Hours} h {t.Minutes} min" : $"{t.Minutes} min";
    }

    public static Color LoadColor(double pct) => pct >= 90 ? BtBattery.Theme.Red : pct >= 70 ? BtBattery.Theme.Amber : BtBattery.Theme.Green;
}
