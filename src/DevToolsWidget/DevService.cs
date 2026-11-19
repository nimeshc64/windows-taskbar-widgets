using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using BtBattery;

namespace DevTools;

sealed class AndroidDevice
{
    public required string Serial { get; init; }
    public required string State { get; init; }
    public string? Model { get; init; }
    public bool IsEmulator => Serial.StartsWith("emulator-", StringComparison.Ordinal);
    public bool IsWireless => Serial.Contains(':') || Serial.Contains("._adb-tls-", StringComparison.Ordinal);
    public string? AvdName { get; set; }
    public string? Version { get; set; }
    public int? Battery { get; set; }
    public bool Online => State == "device";
    public string DisplayName => (AvdName ?? Model ?? Serial).Replace('_', ' ');
    public string Transport => IsEmulator ? "Emulator" : IsWireless ? "Wi-Fi" : "USB";
}

sealed class PortInfo
{
    public int Port { get; init; }
    public int Pid { get; init; }
    public required string Process { get; init; }
    public required string Address { get; init; }
    public string? Label => Labels.TryGetValue(Port, out var l) ? l : null;

    static readonly Dictionary<int, string> Labels = new()
    {
        [5037] = "ADB server", [3000] = "Dev server", [3001] = "Dev server", [4200] = "Angular",
        [5173] = "Vite", [5000] = "Dev server", [8000] = "HTTP", [8080] = "HTTP", [8081] = "Metro / HTTP",
        [9229] = "Node debugger", [5432] = "PostgreSQL", [3306] = "MySQL", [6379] = "Redis", [27017] = "MongoDB",
        [1433] = "SQL Server", [9100] = "DevTools",
    };
}

sealed class DevService
{
    public const string AppName = "DevToolsWidget";

    public string? Sdk { get; }
    public string? Adb { get; }
    public string? Emulator { get; }

    public bool AdbServerRunning { get; private set; }
    public List<AndroidDevice> Devices { get; private set; } = new();
    public List<string> Avds { get; private set; } = new();
    public List<PortInfo> Ports { get; private set; } = new();
    public HashSet<string> StartingAvds { get; } = new();

    readonly Dictionary<string, (string? avd, string? version)> _details = new();
    readonly Dictionary<string, (int? level, DateTime at)> _battery = new();
    readonly Dictionary<int, string> _procNames = new();
    bool _busy;

    static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    { "System", "Idle", "svchost", "lsass", "wininit", "services", "spoolsv", "smss", "csrss", "Registry", "winlogon", "MsMpEng" };

    public DevService()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
        };
        Sdk = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && Directory.Exists(c));
        if (Sdk != null)
        {
            var adb = Path.Combine(Sdk, "platform-tools", "adb.exe");
            var emu = Path.Combine(Sdk, "emulator", "emulator.exe");
            Adb = File.Exists(adb) ? adb : null;
            Emulator = File.Exists(emu) ? emu : null;
        }
        Adb ??= FindOnPath("adb.exe");
    }

    static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
            .Where(d => d.Length > 0).Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);

    public async Task RefreshAsync(bool full)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var result = await Task.Run(async () =>
            {
                var ports = ReadPorts();
                bool adbUp = ports.Any(p => p.Port == 5037);
                var devices = adbUp && Adb != null ? await ReadDevicesAsync() : new List<AndroidDevice>();
                List<string>? avds = null;
                if (full && Emulator != null)
                {
                    var (_, output) = await RunAsync(Emulator, "-list-avds", 10000);
                    avds = output.Split('\n').Select(l => l.Trim())
                        .Where(l => l.Length > 0 && !l.StartsWith("INFO") && !l.StartsWith("WARNING") && !l.Contains(' '))
                        .ToList();
                }
                return (ports, adbUp, devices, avds);
            });
            Ports = result.ports;
            AdbServerRunning = result.adbUp;
            Devices = result.devices;
            if (result.avds != null) Avds = result.avds;
            StartingAvds.RemoveWhere(a => Devices.Any(d => d.AvdName == a));
        }
        catch (Exception ex) { AppFiles.Log(AppName, "Refresh: " + ex.Message); }
        finally { _busy = false; }
    }

    async Task<List<AndroidDevice>> ReadDevicesAsync()
    {
        var (code, output) = await RunAsync(Adb!, "devices -l", 8000);
        var list = new List<AndroidDevice>();
        if (code != 0) return list;
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var model = parts.FirstOrDefault(p => p.StartsWith("model:"))?[6..];
            var d = new AndroidDevice { Serial = parts[0], State = parts[1], Model = model };

            if (d.Online)
            {
                if (!_details.TryGetValue(d.Serial, out var det))
                {
                    var ver = (await RunAsync(Adb!, $"-s {d.Serial} shell getprop ro.build.version.release", 5000)).Output.Trim();
                    string? avd = null;
                    if (d.IsEmulator)
                    {
                        var r = await RunAsync(Adb!, $"-s {d.Serial} emu avd name", 5000);
                        avd = r.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l != "OK");
                    }
                    det = (avd, ver.Length > 0 && ver.Length < 12 ? ver : null);
                    _details[d.Serial] = det;
                }
                d.AvdName = det.avd;
                d.Version = det.version;

                if (!d.IsEmulator)
                {
                    if (!_battery.TryGetValue(d.Serial, out var b) || (DateTime.Now - b.at).TotalSeconds > 60)
                    {
                        var r = await RunAsync(Adb!, $"-s {d.Serial} shell dumpsys battery", 6000);
                        var levelLine = r.Output.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("level:"));
                        b = (int.TryParse(levelLine?.Split(':')[1].Trim(), out var lvl) ? lvl : null, DateTime.Now);
                        _battery[d.Serial] = b;
                    }
                    d.Battery = b.level;
                }
            }
            list.Add(d);
        }
        foreach (var gone in _details.Keys.Where(k => list.All(d => d.Serial != k)).ToList()) _details.Remove(gone);
        return list;
    }

    public static string ScreenshotFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Android Screenshots");

    public async Task<string?> ScreenshotAsync(AndroidDevice d)
    {
        var png = await Task.Run(() => RunBinaryAsync(Adb!, $"-s {d.Serial} exec-out screencap -p", 20000));
        if (png == null || png.Length < 8 || png[0] != 0x89 || png[1] != 0x50) return null;
        Directory.CreateDirectory(ScreenshotFolder);
        var safe = string.Concat(d.DisplayName.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        var path = Path.Combine(ScreenshotFolder, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        await File.WriteAllBytesAsync(path, png);
        return path;
    }

    public async Task<string> InstallAsync(AndroidDevice d, string apk)
    {
        var (code, output) = await Task.Run(() => RunAsync(Adb!, $"-s {d.Serial} install -r \"{apk}\"", 300000));
        var last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        return code == 0 && last.Contains("Success") ? "Success" : (last.Length > 0 ? last : $"adb exited with {code}");
    }

    public void OpenShell(AndroidDevice d) => OpenConsole($"\"{Adb}\" -s {d.Serial} shell");
    public void OpenLogcat(AndroidDevice d) => OpenConsole($"\"{Adb}\" -s {d.Serial} logcat -v time");

    static void OpenConsole(string command)
    {
        try { Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"{command}\"") { UseShellExecute = true }); }
        catch (Exception ex) { AppFiles.Log(AppName, "Console: " + ex.Message); }
    }

    public void StartAvd(string name)
    {
        if (Emulator == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(Emulator, $"-avd \"{name}\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Emulator)! });
            StartingAvds.Add(name);
        }
        catch (Exception ex) { AppFiles.Log(AppName, "Start AVD: " + ex.Message); }
    }

    public async Task StartAdbServerAsync()
    {
        if (Adb != null) await Task.Run(() => RunAsync(Adb, "start-server", 15000));
    }

    public static string? KillProcess(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.Kill();
            p.WaitForExit(3000);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    static async Task<(int Code, string Output)> RunAsync(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "Timed out"); }
            return (p.ExitCode, (await stdout) + (await stderr));
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    static async Task<byte[]?> RunBinaryAsync(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            using var ms = new MemoryStream();
            using var cts = new CancellationTokenSource(timeoutMs);
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, cts.Token);
            _ = p.StandardError.ReadToEndAsync();
            await copy;
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch { return null; }
    }

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

    List<PortInfo> ReadPorts()
    {
        var seen = new HashSet<(int, int)>();
        var list = new List<PortInfo>();
        foreach (var af in new[] { 2, 23 })
        {
            foreach (var (port, pid, addr) in Listeners(af))
            {
                if (pid <= 4 || !seen.Add((port, pid))) continue;
                var name = ProcessName(pid);
                if (name == null || SystemProcesses.Contains(name)) continue;
                list.Add(new PortInfo { Port = port, Pid = pid, Process = name, Address = addr });
            }
        }
        foreach (var dead in _procNames.Keys.Where(pid => list.All(p => p.Pid != pid)).ToList()) _procNames.Remove(dead);
        return list.OrderBy(p => p.Port).ToList();
    }

    string? ProcessName(int pid)
    {
        if (_procNames.TryGetValue(pid, out var n)) return n;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); n = p.ProcessName; }
        catch { n = null; }
        if (n != null) _procNames[pid] = n;
        return n;
    }

    static List<(int port, int pid, string addr)> Listeners(int af)
    {
        var result = new List<(int, int, string)>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, af, 3 , 0);
        if (size <= 0) return result;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, true, af, 3, 0) != 0) return result;
            int n = Marshal.ReadInt32(buf);
            int rowSize = af == 2 ? 24 : 56;
            for (int i = 0; i < n; i++)
            {
                var row = buf + 4 + i * rowSize;
                int rawPort, pid;
                string addr;
                if (af == 2)
                {
                    addr = new IPAddress((uint)Marshal.ReadInt32(row, 4)).ToString();
                    rawPort = Marshal.ReadInt32(row, 8);
                    pid = Marshal.ReadInt32(row, 20);
                }
                else
                {
                    var bytes = new byte[16];
                    Marshal.Copy(row, bytes, 0, 16);
                    addr = new IPAddress(bytes).ToString();
                    rawPort = Marshal.ReadInt32(row, 20);
                    pid = Marshal.ReadInt32(row, 52);
                }
                int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                result.Add((port, pid, addr));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }
}
