using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BtBattery;

public sealed class BatteryService
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    const string AddressKey = "System.Devices.Aep.DeviceAddress";
    const string CodMajorKey = "System.Devices.Aep.Bluetooth.Cod.Major";
    const string LeCategoryKey = "System.Devices.Aep.Bluetooth.Le.Appearance.Category";

    static readonly Regex Hex12 = new("(?<![0-9A-F])[0-9A-F]{12}(?![0-9A-F])", RegexOptions.Compiled);

    public async Task<List<DeviceBattery>> ScanAsync()
    {
        var byAddress = new Dictionary<string, DeviceBattery>();
        await AddEndpointsAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), false, byAddress);
        await AddEndpointsAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), true, byAddress);

        var levels = await ReadDevnodeBatteryAsync(byAddress.Keys.ToHashSet());
        foreach (var d in byAddress.Values)
            if (levels.TryGetValue(d.Address, out var lvl)) d.Battery = lvl;

        foreach (var d in byAddress.Values.Where(d => d.IsLowEnergy && d.IsConnected && d.Battery == null))
            d.Battery = await ReadGattBatteryAsync(d.Id);

        return byAddress.Values.ToList();
    }

    static async Task AddEndpointsAsync(string selector, bool le, Dictionary<string, DeviceBattery> into)
    {
        DeviceInformationCollection list;
        try
        {
            list = await DeviceInformation.FindAllAsync(selector,
                new[] { IsConnectedKey, AddressKey, CodMajorKey, LeCategoryKey }, DeviceInformationKind.AssociationEndpoint);
        }
        catch
        {
            list = await DeviceInformation.FindAllAsync(selector,
                new[] { IsConnectedKey, AddressKey }, DeviceInformationKind.AssociationEndpoint);
        }

        foreach (var info in list)
        {
            var address = Normalize(Prop(info, AddressKey) as string) ?? Normalize(info.Id.Split('-').LastOrDefault());
            if (address == null) continue;
            bool connected = Prop(info, IsConnectedKey) is bool b && b;
            var name = string.IsNullOrWhiteSpace(info.Name) ? address : info.Name.Trim();

            if (into.TryGetValue(address, out var existing))
            {
                existing.IsConnected |= connected;
                continue;
            }

            into[address] = new DeviceBattery
            {
                Id = info.Id,
                Name = name,
                Address = address,
                IsLowEnergy = le,
                IsConnected = connected,
                Kind = Classify(name, ToInt(Prop(info, CodMajorKey)), ToInt(Prop(info, LeCategoryKey))),
            };
        }
    }

    static async Task<Dictionary<string, int>> ReadDevnodeBatteryAsync(HashSet<string> addresses)
    {
        var result = new Dictionary<string, int>();
        if (addresses.Count == 0) return result;

        DeviceInformationCollection nodes;
        try
        {
            nodes = await DeviceInformation.FindAllAsync("System.Devices.DeviceInstanceId:~<\"BTH\"",
                new[] { BatteryKey }, DeviceInformationKind.Device);
        }
        catch
        {
            nodes = await DeviceInformation.FindAllAsync("", new[] { BatteryKey }, DeviceInformationKind.Device);
        }

        foreach (var node in nodes)
        {
            if (ToInt(Prop(node, BatteryKey)) is not int level || level < 0 || level > 100) continue;
            foreach (Match m in Hex12.Matches(node.Id.ToUpperInvariant()))
                if (addresses.Contains(m.Value) && !result.ContainsKey(m.Value))
                    result[m.Value] = level;
        }
        return result;
    }

    static async Task<int?> ReadGattBatteryAsync(string id)
    {
        var timeout = TimeSpan.FromSeconds(6);
        try
        {
            using var dev = await BluetoothLEDevice.FromIdAsync(id).AsTask().WaitAsync(timeout);
            if (dev == null) return null;
            var svcs = await dev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached)
                .AsTask().WaitAsync(timeout);
            if (svcs.Status != GattCommunicationStatus.Success) return null;
            try
            {
                foreach (var svc in svcs.Services)
                {
                    var chars = await svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel,
                        BluetoothCacheMode.Cached).AsTask().WaitAsync(timeout);
                    if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) continue;
                    var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached)
                        .AsTask().WaitAsync(timeout);
                    if (read.Status == GattCommunicationStatus.Success && read.Value.Length > 0)
                    {
                        int v = DataReader.FromBuffer(read.Value).ReadByte();
                        if (v <= 100) return v;
                    }
                }
            }
            finally
            {
                foreach (var svc in svcs.Services) svc.Dispose();
            }
        }
        catch (Exception ex) { Log.Write($"GATT read {id}: {ex.Message}"); }
        return null;
    }

    static DeviceKind Classify(string name, int? codMajor, int? leCategory)
    {
        var n = name.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(n.Contains);

        if (Has("mouse", "mx master", "mx anywhere", "trackball", "trackpad")) return DeviceKind.Mouse;
        if (Has("keyboard", "keychron", "k380", "mx keys", "keys-to-go")) return DeviceKind.Keyboard;
        if (Has("controller", "gamepad", "xbox", "dualsense", "dualshock", "joy-con")) return DeviceKind.Gamepad;
        if (Regex.IsMatch(n, @"\b(pen|stylus)\b")) return DeviceKind.Pen;
        if (Has("watch", " band", "fitbit")) return DeviceKind.Watch;
        if (Has("speaker", "srs-", "boom", "soundlink", "jbl flip", "jbl charge", "jbl go", "soundcore motion")) return DeviceKind.Speaker;
        if (Has("buds", "pods", "headphone", "headset", "earphone", "earbud", "wh-", "wf-", "freebuds", "tws", "beats"))
            return DeviceKind.Headphones;

        switch (leCategory)
        {
            case 1: return DeviceKind.Phone;
            case 2: return DeviceKind.Computer;
            case 3: return DeviceKind.Watch;
        }
        switch (codMajor)
        {
            case 1: return DeviceKind.Computer;
            case 2: return DeviceKind.Phone;
            case 4: return DeviceKind.Headphones;
            case 5: return DeviceKind.Keyboard;
        }
        return DeviceKind.Other;
    }

    static object? Prop(DeviceInformation info, string key) =>
        info.Properties.TryGetValue(key, out var v) ? v : null;

    static int? ToInt(object? o)
    {
        try { return o is IConvertible c ? c.ToInt32(null) : null; }
        catch { return null; }
    }

    static string? Normalize(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var hex = new string(address.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 12 ? hex : null;
    }
}
