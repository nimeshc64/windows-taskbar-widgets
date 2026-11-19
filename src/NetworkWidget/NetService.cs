using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using BtBattery;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;

namespace NetTools;

public sealed record SpeedResult(DateTime When, double DownMbps, double UpMbps, double PingMs);
sealed record WifiNet(string Ssid, int Bars, bool Secure, bool Connected, WiFiAvailableNetwork Raw);
sealed record VpnInfo(string Name, string Detail, bool Connected, string? RasEntry);
sealed record AppUsage(string Name, ulong Down, ulong Up);

sealed class NetService
{
    public const string AppName = "NetworkWidget";
    const string CloudflareSpeed = "https://speed.cloudflare.com";

    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.None,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    static NetService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("NetworkWidget/1.0");

    public bool Connected { get; private set; }
    public string Name { get; private set; } = "";
    public string Kind { get; private set; } = "";
    public int? Bars { get; private set; }
    public long LinkBps { get; private set; }
    public string LocalIp { get; private set; } = "";
    public string Gateway { get; private set; } = "";
    public string Dns { get; private set; } = "";
    public bool Internet { get; private set; }
    public double? PingMs { get; private set; }
    public List<double?> PingHistory { get; } = new();

    public string? PublicIp { get; private set; }
    public string? Location { get; private set; }
    public string? Isp { get; private set; }
    DateTime _ipTime;
    string _ipKey = "";

    public List<VpnInfo> Vpns { get; private set; } = new();
    public bool VpnActive => Vpns.Any(v => v.Connected);

    public bool HasWifi { get; private set; }
    public bool WifiOn { get; private set; }
    public bool Scanning { get; private set; }
    public List<WifiNet> Networks { get; private set; } = new();
    public string? WifiMessage { get; private set; }
    public bool WifiNeedsLocation { get; private set; }
    WiFiAdapter? _adapter;

    public (ulong Down, ulong Up)? Today { get; private set; }
    public (ulong Down, ulong Up)? Month { get; private set; }
    public List<AppUsage> Apps { get; private set; } = new();
    public string? UsageMessage { get; private set; }

    public bool Testing { get; private set; }
    public string TestPhase { get; private set; } = "";
    public double? TestPing { get; private set; }
    public double? TestDown { get; private set; }
    public double? TestUp { get; private set; }
    CancellationTokenSource? _testCts;

    static readonly TimeSpan WinRtTimeout = TimeSpan.FromSeconds(10);

    public event Action? Changed;
    void OnChanged() => Changed?.Invoke();

    public async Task RefreshAsync(bool full, bool forcePublicIp = false)
    {
        try { ReadConnection(); } catch (Exception ex) { Log("Connection: " + ex.Message); }
        await PingAsync();
        if (full)
        {
            try { ReadVpns(); } catch (Exception ex) { Log("VPN: " + ex.Message); }
            await UsageAsync();
        }
        await PublicIpAsync(forcePublicIp);
        OnChanged();
    }

    static readonly string[] VirtualWords = { "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "loopback", "pseudo", "wsl" };

    static readonly (string Key, string Name)[] VpnBrands =
    {
        ("surfshark", "Surfshark"), ("nordlynx", "NordVPN"), ("nordvpn", "NordVPN"), ("proton", "Proton VPN"),
        ("tailscale", "Tailscale"), ("mullvad", "Mullvad"), ("expressvpn", "ExpressVPN"), ("cloudflare", "Cloudflare WARP"),
        ("zerotier", "ZeroTier"), ("anyconnect", "Cisco AnyConnect"), ("cisco", "Cisco VPN"), ("forti", "FortiClient"),
        ("pangp", "GlobalProtect"), ("globalprotect", "GlobalProtect"), ("windscribe", "Windscribe"), ("hotspot shield", "Hotspot Shield"),
        ("pia", "Private Internet Access"), ("cyberghost", "CyberGhost"), ("wireguard", "WireGuard"), ("wintun", "WireGuard"),
        ("openvpn", "OpenVPN"), ("tap-windows", "OpenVPN"), ("vpn", "VPN"),
    };

    static string? VpnBrand(NetworkInterface n)
    {
        var d = (n.Name + " " + n.Description).ToLowerInvariant();
        foreach (var (key, name) in VpnBrands)
            if (key == "pia" ? d.Contains("private internet access") : d.Contains(key)) return name;
        return null;
    }

    static bool IsVirtual(NetworkInterface n)
    {
        var d = (n.Description + " " + n.Name).ToLowerInvariant();
        return VirtualWords.Any(d.Contains);
    }

    static string Ipv4(NetworkInterface n)
    {
        try
        {
            return n.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
        }
        catch { return ""; }
    }

    void ReadConnection()
    {
        NetworkInterface? best = null;
        IPInterfaceProperties? bestProps = null;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) continue;
            if (IsVirtual(n) || VpnBrand(n) != null) continue;
            IPInterfaceProperties props;
            try { props = n.GetIPProperties(); } catch { continue; }
            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                  !g.Address.Equals(IPAddress.Any))) continue;
            bool better = best == null
                || (IsWired(n) && !IsWired(best))
                || (IsWired(n) == IsWired(best) && n.Speed > best.Speed);
            if (better) { best = n; bestProps = props; }
        }

        Connected = best != null;
        if (best == null || bestProps == null)
        {
            Name = Kind = LocalIp = Gateway = Dns = "";
            Bars = null; LinkBps = 0; Internet = false;
            return;
        }

        Kind = best.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Cellular",
            _ => "Ethernet",
        };
        LinkBps = best.Speed;
        LocalIp = Ipv4(best);
        Gateway = bestProps.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
        Dns = string.Join(", ", bestProps.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(2));
        Name = best.Name;
        Bars = null;

        try
        {
            var internet = NetworkInformation.GetInternetConnectionProfile();
            Internet = internet?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            if (Kind == "Wi-Fi")
            {
                var wlan = NetworkInformation.GetConnectionProfiles()
                    .FirstOrDefault(p => p.IsWlanConnectionProfile && p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);
                if (wlan != null)
                {
                    var ssid = wlan.WlanConnectionProfileDetails.GetConnectedSsid();
                    if (!string.IsNullOrEmpty(ssid)) Name = ssid;
                    Bars = wlan.GetSignalBars();
                }
            }
            else if (internet != null && !internet.IsWlanConnectionProfile && Kind == "Ethernet" && !string.IsNullOrEmpty(internet.ProfileName)
                     && !internet.ProfileName.StartsWith("Network", StringComparison.OrdinalIgnoreCase))
                Name = internet.ProfileName;
        }
        catch { Internet = NetworkInterface.GetIsNetworkAvailable(); }
    }

    static bool IsWired(NetworkInterface n) =>
        n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT;

    async Task PingAsync()
    {
        double? ms = null;
        if (Connected)
        {
            try
            {
                using var p = new Ping();
                var r = await p.SendPingAsync("1.1.1.1", 1500);
                if (r.Status == IPStatus.Success) ms = r.RoundtripTime;
            }
            catch { }
        }
        PingMs = ms;
        PingHistory.Add(ms);
        while (PingHistory.Count > 60) PingHistory.RemoveAt(0);
    }

    async Task PublicIpAsync(bool force)
    {
        var key = LocalIp + "|" + Internet + "|" + string.Join(",", Vpns.Where(v => v.Connected).Select(v => v.Name));
        if (!force && key == _ipKey && (DateTime.Now - _ipTime).TotalMinutes < 15) return;
        _ipKey = key;
        _ipTime = DateTime.Now;
        if (!Internet) { PublicIp = Location = Isp = null; return; }
        try
        {
            using var cts = new CancellationTokenSource(8000);
            var json = await Http.GetStringAsync("https://ipinfo.io/json", cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            PublicIp = Str("ip");
            var org = Str("org");
            if (org != null && org.StartsWith("AS") && org.IndexOf(' ') is int sp and > 0) org = org[(sp + 1)..];
            Isp = org;
            Location = string.Join(", ", new[] { Str("city"), Str("country") }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (PublicIp != null) return;
        }
        catch (Exception ex) { Log("Public IP (ipinfo): " + ex.Message); }

        try
        {
            using var cts = new CancellationTokenSource(8000);
            var text = await Http.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", cts.Token);
            var kv = text.Split('\n').Select(l => l.Split('=', 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1].Trim());
            PublicIp = kv.GetValueOrDefault("ip");
            Location = kv.GetValueOrDefault("loc");
            Isp = null;
        }
        catch (Exception ex)
        {
            Log("Public IP: " + ex.Message);
            _ipTime = DateTime.Now.AddMinutes(-14);
        }
    }

    static string? RoutableIp(NetworkInterface n)
    {
        try
        {
            var a = n.GetIPProperties().UnicastAddresses.Select(u => u.Address)
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !ip.ToString().StartsWith("169.254."))
                ?? n.GetIPProperties().UnicastAddresses.Select(u => u.Address)
                    .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6 && !ip.IsIPv6LinkLocal);
            return a?.ToString();
        }
        catch { return null; }
    }

    void ReadVpns()
    {
        var list = new List<VpnInfo>();
        var ifaces = NetworkInterface.GetAllNetworkInterfaces();

        foreach (var entry in RasEntries())
        {
            var n = ifaces.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Ppp &&
                                               i.OperationalStatus == OperationalStatus.Up &&
                                               string.Equals(i.Name, entry, StringComparison.OrdinalIgnoreCase));
            list.Add(new VpnInfo(entry, n != null ? $"Windows VPN · {Ipv4(n)}" : "Windows VPN", n != null, entry));
        }

        foreach (var g in ifaces.Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Ppp && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                                .Select(i => (Iface: i, Brand: VpnBrand(i)))
                                .Where(x => x.Brand != null)
                                .GroupBy(x => x.Brand!))
        {
            var pick = g.OrderByDescending(x => RoutableIp(x.Iface) != null).ThenByDescending(x => x.Iface.OperationalStatus == OperationalStatus.Up).First().Iface;
            var ip = pick.OperationalStatus == OperationalStatus.Up ? RoutableIp(pick) : null;
            list.Add(new VpnInfo(g.Key, ip != null ? $"{ip} · {pick.Description}" : pick.Description, ip != null, null));
        }
        Vpns = list.OrderByDescending(v => v.Connected).ThenBy(v => v.Name).ToList();
    }

    static IEnumerable<string> RasEntries()
    {
        var files = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Network\Connections\Pbk\rasphone.pbk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Network\Connections\Pbk\rasphone.pbk"),
        };
        var names = new List<string>();
        foreach (var f in files)
        {
            try
            {
                if (!File.Exists(f)) continue;
                string? section = null;
                foreach (var raw in File.ReadLines(f))
                {
                    var line = raw.Trim();
                    if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1];
                    else if (section != null && line.StartsWith("VpnStrategy=", StringComparison.OrdinalIgnoreCase) &&
                             !names.Contains(section, StringComparer.OrdinalIgnoreCase))
                        names.Add(section);
                }
            }
            catch { }
        }
        return names;
    }

    public async Task<string?> SetRasAsync(string entry, bool connect)
    {
        try
        {
            var psi = new ProcessStartInfo("rasdial.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.ArgumentList.Add(entry);
            if (!connect) psi.ArgumentList.Add("/disconnect");
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            ReadVpns();
            OnChanged();
            if (p.ExitCode == 0) return null;
            var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase));
            return line ?? $"rasdial error {p.ExitCode}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    async Task<Radio?> WifiRadioAsync()
    {
        try { return (await Radio.GetRadiosAsync().AsTask().WaitAsync(WinRtTimeout)).FirstOrDefault(r => r.Kind == RadioKind.WiFi); }
        catch { return null; }
    }

    public async Task ScanWifiAsync(bool scan = true)
    {
        WifiMessage = null;
        WifiNeedsLocation = false;
        try
        {
            var radio = await WifiRadioAsync();
            HasWifi = radio != null;
            WifiOn = radio?.State == RadioState.On;
            if (!HasWifi || !WifiOn) { Networks = new(); return; }

            if (_adapter == null)
            {
                var access = await WiFiAdapter.RequestAccessAsync();
                if (access != WiFiAccessStatus.Allowed)
                {
                    WifiMessage = "Wi-Fi list is blocked — turn on location access for desktop apps";
                    WifiNeedsLocation = true;
                    return;
                }
                _adapter = (await WiFiAdapter.FindAllAdaptersAsync()).FirstOrDefault();
                if (_adapter == null) { HasWifi = false; return; }
            }

            if (scan)
            {
                Scanning = true;
                OnChanged();
                await _adapter.ScanAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            }

            string? connected = null;
            try
            {
                var prof = await _adapter.NetworkAdapter.GetConnectedProfileAsync().AsTask().WaitAsync(WinRtTimeout);
                if (prof?.IsWlanConnectionProfile == true) connected = prof.WlanConnectionProfileDetails.GetConnectedSsid();
            }
            catch { }

            Networks = _adapter.NetworkReport.AvailableNetworks
                .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
                .GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(n => n.NetworkRssiInDecibelMilliwatts).First())
                .Select(n => new WifiNet(n.Ssid, n.SignalBars,
                    n.SecuritySettings.NetworkAuthenticationType is not (NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None),
                    n.Ssid == connected, n))
                .OrderByDescending(w => w.Connected).ThenByDescending(w => w.Raw.NetworkRssiInDecibelMilliwatts)
                .ToList();
        }
        catch (Exception ex) when ((uint)ex.HResult == 0x80070005)
        {
            WifiMessage = "Wi-Fi list is blocked — turn on location access for desktop apps";
            WifiNeedsLocation = true;
        }
        catch (Exception ex)
        {
            WifiMessage = "Couldn't scan for networks";
            Log("Wi-Fi scan: " + ex.Message);
        }
        finally
        {
            Scanning = false;
            OnChanged();
        }
    }

    public async Task<bool> SetWifiAsync(bool on)
    {
        try
        {
            await Radio.RequestAccessAsync();
            var radio = await WifiRadioAsync();
            if (radio == null) return false;
            var res = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            WifiOn = on && res == RadioAccessStatus.Allowed;
            if (!on) Networks = new();
            OnChanged();
            return res == RadioAccessStatus.Allowed;
        }
        catch (Exception ex) { Log("Wi-Fi radio: " + ex.Message); return false; }
    }

    public async Task<string?> ConnectWifiAsync(WifiNet net)
    {
        if (_adapter == null) return "No Wi-Fi adapter";
        try
        {
            var r = await _adapter.ConnectAsync(net.Raw, WiFiReconnectionKind.Automatic);
            await Task.Delay(1500);
            await ScanWifiAsync(false);
            await RefreshAsync(false);
            return r.ConnectionStatus == WiFiConnectionStatus.Success ? null : r.ConnectionStatus.ToString();
        }
        catch (Exception ex) { return ex.Message; }
    }

    public async Task DisconnectWifiAsync()
    {
        _adapter?.Disconnect();
        await Task.Delay(800);
        await ScanWifiAsync(false);
        await RefreshAsync(false);
    }

    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint ver, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    static extern int WlanGetProfile(IntPtr handle, ref Guid iface, string name, IntPtr reserved, out IntPtr xml, ref uint flags, out uint access);

    public static (string? Key, string Auth) SavedWifiKey(string ssid)
    {
        IntPtr h = IntPtr.Zero, list = IntPtr.Zero, xml = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out h) != 0) return (null, "");
            if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return (null, "");
            if (Marshal.ReadInt32(list) == 0) return (null, "");
            var guid = Marshal.PtrToStructure<Guid>(list + 8);
            uint flags = 4;
            if (WlanGetProfile(h, ref guid, ssid, IntPtr.Zero, out xml, ref flags, out _) != 0) return (null, "");
            var doc = System.Xml.Linq.XDocument.Parse(Marshal.PtrToStringUni(xml) ?? "");
            string? Get(string local) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value;
            var auth = Get("authentication") ?? "";
            if (Get("protected") == "true") return (null, auth);
            return (Get("keyMaterial"), auth);
        }
        catch { return (null, ""); }
        finally
        {
            if (xml != IntPtr.Zero) WlanFreeMemory(xml);
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
        }
    }

    public static string WifiQrText(string ssid, string? key, string auth)
    {
        static string Esc(string s) => s.Replace(@"\", @"\\").Replace(";", @"\;").Replace(",", @"\,").Replace(":", @"\:").Replace("\"", "\\\"");
        string type = key == null ? "nopass" : auth.Contains("WEP", StringComparison.OrdinalIgnoreCase) ? "WEP" : "WPA";
        return key == null ? $"WIFI:T:nopass;S:{Esc(ssid)};;" : $"WIFI:T:{type};S:{Esc(ssid)};P:{Esc(key)};;";
    }

    async Task UsageAsync()
    {
        try
        {
            var prof = NetworkInformation.GetInternetConnectionProfile();
            if (prof == null) { Today = Month = null; Apps = new(); UsageMessage = "Not connected"; return; }
            var states = new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare };
            var now = DateTimeOffset.Now;
            var day = new DateTimeOffset(now.Date, now.Offset);
            var month = new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset);

            static (ulong, ulong) Sum(IReadOnlyList<NetworkUsage> l) =>
                ((ulong)l.Sum(u => (decimal)u.BytesReceived), (ulong)l.Sum(u => (decimal)u.BytesSent));

            Today = Sum(await prof.GetNetworkUsageAsync(day, now, DataUsageGranularity.Total, states).AsTask().WaitAsync(WinRtTimeout));
            Month = Sum(await prof.GetNetworkUsageAsync(month, now, DataUsageGranularity.Total, states).AsTask().WaitAsync(WinRtTimeout));
            UsageMessage = null;

            try
            {
                var apps = await prof.GetAttributedNetworkUsageAsync(day, now, states).AsTask().WaitAsync(WinRtTimeout);
                Apps = apps.Select(a => new AppUsage(AppLabel(a.AttributionName, a.AttributionId), a.BytesReceived, a.BytesSent))
                    .GroupBy(a => a.Name)
                    .Select(g => new AppUsage(g.Key, (ulong)g.Sum(a => (decimal)a.Down), (ulong)g.Sum(a => (decimal)a.Up)))
                    .OrderByDescending(a => a.Down + a.Up)
                    .Take(5).ToList();
            }
            catch (Exception ex) { Apps = new(); Log("App usage: " + ex.Message); }
        }
        catch (Exception ex)
        {
            Today = Month = null;
            UsageMessage = "Data usage isn't available";
            Log("Usage: " + ex.Message);
        }
    }

    static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Google Chrome", ["msedge"] = "Microsoft Edge", ["firefox"] = "Firefox", ["brave"] = "Brave",
        ["code"] = "VS Code", ["studio64"] = "Android Studio", ["svchost"] = "Windows services", ["system"] = "System",
        ["spotify"] = "Spotify", ["teams"] = "Teams", ["ms-teams"] = "Teams", ["onedrive"] = "OneDrive", ["discord"] = "Discord",
        ["steam"] = "Steam", ["steamwebhelper"] = "Steam", ["java"] = "Java (Gradle)", ["dart"] = "Dart", ["node"] = "Node.js",
        ["git-remote-https"] = "Git", ["dotnet"] = ".NET", ["explorer"] = "File Explorer", ["whatsapp"] = "WhatsApp",
        ["mpengine"] = "Defender", ["msmpeng"] = "Defender", ["wuauclt"] = "Windows Update", ["telegram"] = "Telegram",
    };

    static string AppLabel(string? name, string? id)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name;
        if (string.IsNullOrWhiteSpace(id)) return "System";
        var file = id;
        int bang = file.IndexOf('!');
        if (bang > 0) file = file[..bang];
        file = Path.GetFileNameWithoutExtension(file.TrimEnd('\\', '/'));
        int us = file.IndexOf('_');
        if (us > 0 && id!.Contains('!')) file = file[..us];
        if (KnownApps.TryGetValue(file, out var known)) return known;
        var dot = file.LastIndexOf('.');
        if (dot > 0 && dot < file.Length - 1 && id!.Contains('!')) file = file[(dot + 1)..];
        return file.Length > 0 ? char.ToUpper(file[0]) + file[1..] : "System";
    }

    public void CancelSpeedTest() => _testCts?.Cancel();

    public async Task<SpeedResult?> SpeedTestAsync()
    {
        if (Testing) return null;
        Testing = true;
        TestPing = TestDown = TestUp = null;
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;
        try
        {
            TestPhase = "Measuring latency…";
            OnChanged();
            var pings = new List<double>();
            for (int i = 0; i < 7; i++)
            {
                var sw = Stopwatch.StartNew();
                using var r = await Http.GetAsync($"{CloudflareSpeed}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();
                if (i > 0) pings.Add(sw.Elapsed.TotalMilliseconds);
            }
            pings.Sort();
            TestPing = pings[pings.Count / 2];

            TestPhase = "Download";
            TestDown = await MeasureAsync(true, TimeSpan.FromSeconds(8), ct);
            TestPhase = "Upload";
            TestUp = await MeasureAsync(false, TimeSpan.FromSeconds(6), ct);
            TestPhase = "";
            return new SpeedResult(DateTime.Now, TestDown.Value, TestUp.Value, TestPing.Value);
        }
        catch (OperationCanceledException) { TestPhase = "Cancelled"; return null; }
        catch (Exception ex)
        {
            TestPhase = Internet ? "Speed test failed" : "No internet connection";
            Log("Speed test: " + ex.Message);
            return null;
        }
        finally
        {
            Testing = false;
            _testCts.Dispose();
            _testCts = null;
            OnChanged();
        }
    }

    static byte[]? _payload;

    async Task<double> MeasureAsync(bool download, TimeSpan duration, CancellationToken outer)
    {
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(duration);
        var token = cts.Token;
        if (!download && _payload == null) { _payload = new byte[2 * 1024 * 1024]; Random.Shared.NextBytes(_payload); }

        async Task Worker()
        {
            var buf = new byte[64 * 1024];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (download)
                    {
                        using var resp = await Http.GetAsync($"{CloudflareSpeed}/__down?bytes=50000000", HttpCompletionOption.ResponseHeadersRead, token);
                        await using var s = await resp.Content.ReadAsStreamAsync(token);
                        int n;
                        while ((n = await s.ReadAsync(buf, token)) > 0) Interlocked.Add(ref bytes, n);
                    }
                    else
                    {
                        using var content = new ProgressContent(_payload!, n => Interlocked.Add(ref bytes, n));
                        using var resp = await Http.PostAsync($"{CloudflareSpeed}/__up", content, token);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) when (!token.IsCancellationRequested) { await Task.Delay(250, CancellationToken.None); }
                catch { break; }
            }
        }

        var workers = Enumerable.Range(0, download ? 4 : 3).Select(_ => Task.Run(Worker)).ToArray();
        while (!workers.All(t => t.IsCompleted))
        {
            await Task.Delay(250, CancellationToken.None);
            double live = Mbps(Interlocked.Read(ref bytes), sw.Elapsed);
            if (download) TestDown = live; else TestUp = live;
            OnChanged();
        }
        outer.ThrowIfCancellationRequested();
        var total = Interlocked.Read(ref bytes);
        if (total == 0) throw new IOException("no data transferred");
        return Mbps(total, sw.Elapsed);
    }

    static double Mbps(long bytes, TimeSpan t) => t.TotalSeconds <= 0 ? 0 : bytes * 8 / t.TotalSeconds / 1_000_000;

    sealed class ProgressContent(byte[] data, Action<int> progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (int o = 0; o < data.Length; o += 65536)
            {
                int n = Math.Min(65536, data.Length - o);
                await stream.WriteAsync(data.AsMemory(o, n));
                progress(n);
            }
        }
        protected override bool TryComputeLength(out long length) { length = data.Length; return true; }
    }

    public static string Bytes(ulong b)
    {
        double v = b;
        if (v < 1024) return $"{v:0} B";
        v /= 1024; if (v < 1024) return $"{v:0} KB";
        v /= 1024; if (v < 1024) return v < 10 ? $"{v:0.0} MB" : $"{v:0} MB";
        v /= 1024; return v < 100 ? $"{v:0.0} GB" : $"{v:0} GB";
    }

    public static string LinkSpeed(long bps) =>
        bps <= 0 ? "" : bps >= 1_000_000_000 ? $"{bps / 1e9:0.#} Gbps" : $"{bps / 1e6:0} Mbps";

    public static string Mb(double? mbps) => mbps is not double v ? "—" : v >= 100 ? v.ToString("0") : v.ToString("0.0");

    public static void Log(string msg) => AppFiles.Log(AppName, msg);
}
