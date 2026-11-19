using System.Drawing.Drawing2D;
using BtBattery;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace NowPlaying;

sealed class OtherSession
{
    public required Session Session { get; init; }
    public required string App { get; init; }
    public required string Title { get; init; }
    public bool Playing { get; init; }
}

sealed class MediaService
{
    public const string AppName = "NowPlayingWidget";

    readonly SynchronizationContext _ui;
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    Session? _session;
    string? _pinnedApp;
    int _queued, _fullPending;

    TimeSpan _pos;
    DateTimeOffset _posAt;
    double _rate = 1;
    TimeSpan _start;

    public event Action? Changed;

    public bool HasSession => _session != null;
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public string App { get; private set; } = "";
    public Image? Art { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool CanPrevious { get; private set; }
    public bool CanNext { get; private set; }
    public bool CanPlayPause { get; private set; }
    public bool CanSeek { get; private set; }
    public TimeSpan Duration { get; private set; }
    public List<OtherSession> Others { get; private set; } = new();

    public MediaService()
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Create on the UI thread.");
    }

    public async Task StartAsync()
    {
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mgr.SessionsChanged += (_, _) => Queue(true);
        _mgr.CurrentSessionChanged += (_, _) => { _pinnedApp = null; Queue(true); };
        await ReloadAsync(true);
    }

    public TimeSpan Position
    {
        get
        {
            var p = _pos;
            if (IsPlaying && _posAt.Year > 2000)
                p += TimeSpan.FromTicks((long)((DateTimeOffset.Now - _posAt).Ticks * _rate));
            return p < TimeSpan.Zero ? TimeSpan.Zero : Duration > TimeSpan.Zero && p > Duration ? Duration : p;
        }
    }

    public async void PlayPause() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch (Exception ex) { Log(ex); } }
    public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch (Exception ex) { Log(ex); } }
    public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch (Exception ex) { Log(ex); } }

    public async void Seek(TimeSpan to)
    {
        try
        {
            if (_session == null || !CanSeek) return;
            await _session.TryChangePlaybackPositionAsync((_start + to).Ticks);
            _pos = to; _posAt = DateTimeOffset.Now;
            Changed?.Invoke();
        }
        catch (Exception ex) { Log(ex); }
    }

    public void Select(OtherSession s)
    {
        _pinnedApp = s.Session.SourceAppUserModelId;
        Queue(true);
    }

    void Queue(bool full)
    {
        if (full) Interlocked.Exchange(ref _fullPending, 1);
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        _ui.Post(async _ =>
        {
            Interlocked.Exchange(ref _queued, 0);
            bool f = Interlocked.Exchange(ref _fullPending, 0) == 1;
            try { await ReloadAsync(f); } catch (Exception ex) { Log(ex); }
        }, null);
    }

    void OnMediaChanged(Session s, MediaPropertiesChangedEventArgs e) => Queue(true);
    void OnPlaybackChanged(Session s, PlaybackInfoChangedEventArgs e) => Queue(false);
    void OnTimelineChanged(Session s, TimelinePropertiesChangedEventArgs e) => Queue(false);

    async Task ReloadAsync(bool full)
    {
        if (_mgr == null) return;
        var sessions = _mgr.GetSessions().ToList();
        var pick = (_pinnedApp != null ? sessions.FirstOrDefault(s => s.SourceAppUserModelId == _pinnedApp) : null)
                   ?? _mgr.GetCurrentSession() ?? sessions.FirstOrDefault();

        if (pick?.SourceAppUserModelId != _session?.SourceAppUserModelId || (pick == null) != (_session == null))
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            _session = pick;
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaChanged;
                _session.PlaybackInfoChanged += OnPlaybackChanged;
                _session.TimelinePropertiesChanged += OnTimelineChanged;
            }
            full = true;
        }

        if (_session == null)
        {
            Title = Artist = Album = App = "";
            SetArt(null);
            IsPlaying = CanNext = CanPrevious = CanPlayPause = CanSeek = false;
            Duration = TimeSpan.Zero;
            Others = new();
            Changed?.Invoke();
            return;
        }

        var pb = _session.GetPlaybackInfo();
        IsPlaying = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var c = pb.Controls;
        CanPrevious = c.IsPreviousEnabled;
        CanNext = c.IsNextEnabled;
        CanPlayPause = c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled;
        CanSeek = c.IsPlaybackPositionEnabled;
        _rate = pb.PlaybackRate is double r && r > 0 ? r : 1;

        var tl = _session.GetTimelineProperties();
        _start = tl.StartTime;
        Duration = tl.EndTime - tl.StartTime;
        _pos = tl.Position - tl.StartTime;
        _posAt = tl.LastUpdatedTime;

        if (full)
        {
            App = FriendlyApp(_session.SourceAppUserModelId);
            try
            {
                var props = await _session.TryGetMediaPropertiesAsync();
                Title = props.Title ?? "";
                Artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
                Album = props.AlbumTitle ?? "";
                SetArt(await LoadArtAsync(props.Thumbnail));
            }
            catch (Exception ex) { Log(ex); }

            var others = new List<OtherSession>();
            foreach (var s in sessions.Where(s => s.SourceAppUserModelId != _session.SourceAppUserModelId))
            {
                string title = "";
                try { title = (await s.TryGetMediaPropertiesAsync()).Title ?? ""; } catch { }
                others.Add(new OtherSession
                {
                    Session = s,
                    App = FriendlyApp(s.SourceAppUserModelId),
                    Title = title,
                    Playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                });
            }
            Others = others;
        }
        Changed?.Invoke();
    }

    void SetArt(Image? img)
    {
        var old = Art;
        Art = img;
        old?.Dispose();
    }

    static async Task<Image?> LoadArtAsync(IRandomAccessStreamReference? thumb)
    {
        if (thumb == null) return null;
        try
        {
            using var ras = await thumb.OpenReadAsync();
            using var s = ras.AsStreamForRead();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            ms.Position = 0;
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    static string FriendlyApp(string aumid)
    {
        var s = aumid;
        int bang = s.IndexOf('!');
        if (bang >= 0) s = s[(bang + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        var lower = s.ToLowerInvariant();
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("chrome")) return "Chrome";
        if (lower.Contains("msedge") || lower == "microsoftedge") return "Edge";
        if (lower.Contains("firefox") || lower == "308046b0af4a39cb") return "Firefox";
        if (lower.Contains("zunemusic")) return "Media Player";
        if (lower.Contains("vlc")) return "VLC";
        if (lower.Contains("brave")) return "Brave";
        if (lower.Contains("opera")) return "Opera";
        if (s.Length == 0) return "Media";
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    public static string Time(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public static void DrawArt(Graphics g, Image? art, Rectangle r, float radius, Color tile, Color glyphColor, float glyphPx)
    {
        using var path = Theme.Rounded(r, radius);
        if (art == null)
        {
            using var b = new SolidBrush(tile);
            g.FillPath(b, path);
            using var f = new Font(Theme.IconFont, glyphPx, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "", f, r, glyphColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }
        float side = Math.Min(art.Width, art.Height);
        var src = new RectangleF((art.Width - side) / 2f, (art.Height - side) / 2f, side, side);
        var state = g.Save();
        g.SetClip(path);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(art, r, src, GraphicsUnit.Pixel);
        g.Restore(state);
    }

    static void Log(Exception ex) => AppFiles.Log(AppName, ex.Message);
}
