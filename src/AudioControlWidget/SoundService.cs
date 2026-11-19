using System.Runtime.InteropServices;
using AudioCtl.Interop;

namespace AudioCtl;

public sealed class AudioDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Interface { get; init; }
    public int FormFactor { get; init; }
    public EDataFlow Flow { get; init; }
    public bool IsDefault { get; init; }
}

public sealed class SoundService : IDisposable
{
    internal static Guid EventContext = Guid.NewGuid();

    readonly IMMDeviceEnumerator _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
    readonly SynchronizationContext _ui;
    readonly NotificationClient _client;
    int _volumePosted;

    public Endpoint Output { get; }
    public Endpoint Input { get; }

    public event Action? DevicesChanged;
    public event Action? VolumeChanged;

    public SoundService()
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Create on the UI thread.");
        Output = new Endpoint(this, EDataFlow.Render);
        Input = new Endpoint(this, EDataFlow.Capture);
        Output.Bind();
        Input.Bind();
        _client = new NotificationClient(this);
        try { _enumerator.RegisterEndpointNotificationCallback(_client); }
        catch (Exception ex) { SLog.Write("Register notifications: " + ex.Message); }
    }

    public Endpoint this[EDataFlow flow] => flow == EDataFlow.Capture ? Input : Output;

    public List<AudioDevice> GetDevices(EDataFlow flow)
    {
        var list = new List<AudioDevice>();
        var defaultId = this[flow].DeviceId;
        try
        {
            var col = _enumerator.EnumAudioEndpoints(flow, 1 );
            uint n = col.GetCount();
            for (uint i = 0; i < n; i++)
            {
                try
                {
                    var dev = col.Item(i);
                    var id = dev.GetId();
                    var store = dev.OpenPropertyStore(0 );
                    var desc = GetString(store, PKey.DeviceDesc);
                    var iface = GetString(store, PKey.InterfaceName) ?? "";
                    var friendly = GetString(store, PKey.FriendlyName);
                    list.Add(new AudioDevice
                    {
                        Id = id,
                        Name = desc ?? friendly ?? "Audio device",
                        Interface = iface,
                        FormFactor = (int)(GetUInt(store, PKey.FormFactor) ?? 10),
                        Flow = flow,
                        IsDefault = id == defaultId,
                    });
                }
                catch (Exception ex) { SLog.Write("Read device: " + ex.Message); }
            }
        }
        catch (Exception ex) { SLog.Write("Enumerate: " + ex.Message); }
        return list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ThenBy(d => d.Interface).ToList();
    }

    public bool SetDefault(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            int hr = 0;
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                hr |= policy.SetDefaultEndpoint(deviceId, role);
            Marshal.ReleaseComObject(policy);
            return hr == 0;
        }
        catch (Exception ex) { SLog.Write("Set default: " + ex.Message); return false; }
    }

    internal IMMDevice? DefaultDevice(EDataFlow flow)
    {
        try { return _enumerator.GetDefaultAudioEndpoint(flow, ERole.Console); }
        catch { return null; }
    }

    internal void Post(Action a) => _ui.Post(_ => { try { a(); } catch (Exception ex) { SLog.Write("Event: " + ex); } }, null);

    internal void PostVolumeChanged()
    {
        if (Interlocked.Exchange(ref _volumePosted, 1) == 1) return;
        Post(() => { Interlocked.Exchange(ref _volumePosted, 0); VolumeChanged?.Invoke(); });
    }

    internal void OnDefaultChanged(EDataFlow flow)
    {
        this[flow].Bind();
        DevicesChanged?.Invoke();
        VolumeChanged?.Invoke();
    }

    internal void OnDevicesChanged()
    {
        if (Output.DeviceId != DefaultDevice(EDataFlow.Render)?.GetId()) Output.Bind();
        if (Input.DeviceId != DefaultDevice(EDataFlow.Capture)?.GetId()) Input.Bind();
        DevicesChanged?.Invoke();
        VolumeChanged?.Invoke();
    }

    static string? GetString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.vt == 31  ? Marshal.PtrToStringUni(pv.ptr) : null; }
        finally { Ole.PropVariantClear(ref pv); }
    }

    static uint? GetUInt(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.vt == 19  ? pv.uintVal : null; }
        finally { Ole.PropVariantClear(ref pv); }
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_client); } catch { }
        Output.Unbind();
        Input.Unbind();
    }

    public sealed class Endpoint
    {
        readonly SoundService _svc;
        readonly VolumeCallback _callback;
        IAudioEndpointVolume? _volume;

        public EDataFlow Flow { get; }
        public string? DeviceId { get; private set; }
        public string DeviceName { get; private set; } = "";
        public bool Available => _volume != null;

        internal Endpoint(SoundService svc, EDataFlow flow)
        {
            _svc = svc;
            Flow = flow;
            _callback = new VolumeCallback(svc);
        }

        internal void Bind()
        {
            Unbind();
            var dev = _svc.DefaultDevice(Flow);
            if (dev == null) { DeviceId = null; DeviceName = ""; return; }
            try
            {
                DeviceId = dev.GetId();
                var store = dev.OpenPropertyStore(0);
                DeviceName = GetString(store, PKey.FriendlyName) ?? GetString(store, PKey.DeviceDesc) ?? "";
                var iid = typeof(IAudioEndpointVolume).GUID;
                _volume = (IAudioEndpointVolume)dev.Activate(ref iid, 23 , IntPtr.Zero);
                _volume.RegisterControlChangeNotify(_callback);
            }
            catch (Exception ex)
            {
                SLog.Write($"Bind {Flow}: {ex.Message}");
                _volume = null;
            }
        }

        internal void Unbind()
        {
            if (_volume == null) return;
            try { _volume.UnregisterControlChangeNotify(_callback); } catch { }
            try { Marshal.ReleaseComObject(_volume); } catch { }
            _volume = null;
        }

        public float Volume
        {
            get { try { return _volume?.GetMasterVolumeLevelScalar() ?? 0; } catch { Recover(); return 0; } }
            set
            {
                try { _volume?.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref EventContext); }
                catch { Recover(); }
            }
        }

        public bool Muted
        {
            get { try { return _volume?.GetMute() ?? false; } catch { Recover(); return false; } }
            set
            {
                try { _volume?.SetMute(value, ref EventContext); }
                catch { Recover(); }
            }
        }

        public int Percent => (int)Math.Round(Volume * 100);

        public void Step(int points)
        {
            if (!Available) return;
            int target = Math.Clamp((int)Math.Round(Volume * 100) + points, 0, 100);
            Volume = target / 100f;
            if (points > 0 && Muted) Muted = false;
        }

        void Recover() => _svc.Post(() => _svc.OnDevicesChanged());
    }

    [ComVisible(true)]
    sealed class VolumeCallback(SoundService svc) : IAudioEndpointVolumeCallback
    {
        public void OnNotify(IntPtr notifyData) => svc.PostVolumeChanged();
    }

    [ComVisible(true)]
    sealed class NotificationClient(SoundService svc) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string id, uint newState) => svc.Post(svc.OnDevicesChanged);
        public void OnDeviceAdded(string id) => svc.Post(svc.OnDevicesChanged);
        public void OnDeviceRemoved(string id) => svc.Post(svc.OnDevicesChanged);
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id)
        {
            if (role == ERole.Console && flow != EDataFlow.All) svc.Post(() => svc.OnDefaultChanged(flow));
        }
        public void OnPropertyValueChanged(string id, PropertyKey key) { }
    }
}
