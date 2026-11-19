using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;

namespace MacNotify;

static class AppSounds
{
    public const string Auto = "auto", None = "none";

    static string Media(string file) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", file);

    public static readonly (string Name, string File)[] Builtin =
    {
        ("Windows default", "Windows Notify System Generic.wav"),
        ("Message", "Windows Notify Messaging.wav"),
        ("Email", "Windows Notify Email.wav"),
        ("Reminder", "Windows Notify Calendar.wav"),
        ("Notify", "Windows Notify.wav"),
        ("Ding", "Windows Ding.wav"),
        ("Chimes", "chimes.wav"),
    };

    static readonly string[] Chat = { "teams", "whatsapp", "slack", "discord", "telegram", "signal", "messenger", "skype", "zoom", "chat", "message" };
    static readonly string[] Mail = { "outlook", "mail", "thunderbird", "gmail" };
    static readonly string[] Calendar = { "calendar", "reminder", "clock", "alarm", "todo", "to do" };

    static string DefaultFor(string app)
    {
        var a = app.ToLowerInvariant();
        if (Mail.Any(a.Contains)) return Media("Windows Notify Email.wav");
        if (Calendar.Any(a.Contains)) return Media("Windows Notify Calendar.wav");
        if (Chat.Any(a.Contains)) return Media("Windows Notify Messaging.wav");
        return Media("Windows Notify System Generic.wav");
    }

    public static void Play(string app, string? choice)
    {
        try
        {
            choice ??= Auto;
            if (choice == None) return;
            string file;
            if (choice == Auto)
            {
                if (AppIsPlayingAudio(app)) return;
                file = DefaultFor(app);
            }
            else file = File.Exists(choice) ? choice : Media(choice);
            if (File.Exists(file)) new SoundPlayer(file).Play();
            else SystemSounds.Asterisk.Play();
        }
        catch (Exception ex) { NotifyApp.Log("Sound: " + ex.Message); }
    }

    public static void Preview(string? choice, string app) =>
        Task.Run(() =>
        {
            var file = choice is null or Auto ? DefaultFor(app) : File.Exists(choice) ? choice : Media(choice);
            if (choice != None && File.Exists(file)) new SoundPlayer(file).Play();
        });

    static bool AppIsPlayingAudio(string app)
    {
        var words = app.ToLowerInvariant().Replace("microsoft", "").Split(' ', '-', '.', '(', ')')
            .Where(w => w.Length >= 3).ToList();
        if (words.Count == 0) return false;
        foreach (var pid in ActiveAudioProcesses())
        {
            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { continue; }
            if (words.Any(w => name.Contains(w))) return true;
        }
        return false;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr control);
        int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(IntPtr name, IntPtr ctx);
        int GetIconPath(out IntPtr path);
        int SetIconPath(IntPtr path, IntPtr ctx);
        int GetGroupingParam(out Guid g);
        int SetGroupingParam(ref Guid g, IntPtr ctx);
        int RegisterAudioSessionNotification(IntPtr n);
        int UnregisterAudioSessionNotification(IntPtr n);
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out uint pid);
    }

    public static List<uint> ActiveAudioProcesses()
    {
        var result = new List<uint>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0) return result;
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, 1, IntPtr.Zero, out var o);
            var manager = (IAudioSessionManager2)o;
            manager.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                if (sessions.GetSession(i, out var s) != 0) continue;
                if (s.GetState(out int state) == 0 && state == 1 && s.GetProcessId(out uint pid) == 0 && pid != 0) result.Add(pid);
            }
        }
        catch (Exception ex) { NotifyApp.Log("Audio sessions: " + ex.Message); }
        return result;
    }
}
