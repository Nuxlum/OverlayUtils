using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverlayUtils.Core.Audio;
using OverlayUtils.Core.Diagnostics;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OverlayUtils.App.Audio;

/// <summary>
/// System media session (WinRT GlobalSystemMediaTransportControls). Events may be raised on any thread.
/// The track's thumbnail is decoded on a worker into <see cref="Art"/>.
/// </summary>
public sealed class MediaSession : IMediaControl, IDisposable
{
    // Same short names as the Python MEDIA_APP_NAMES.
    private static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "Edge",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["zunemusic"] = "Lettore multimediale",
        ["vlc"] = "VLC",
    };

    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private NowPlaying? _current;
    private int _refreshing;
    private bool _again;
    private bool _disposed;
    // Album art: the track it belongs to, whether its thumbnail was read already (once per track, so a broken
    // thumbnail is not retried or logged again), and the decoded image.
    private string? _artTrack;
    private bool _artRead;
    private ImageSource? _art;

    /// <summary>Width the album art is decoded at (the stage shows it large, the Home tile small).</summary>
    private const int ArtWidth = 300;

    public MediaSession()
    {
        _ = InitAsync();
    }

    public NowPlaying? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Album art of the current track (frozen, any thread), null when the session has none or it failed.</summary>
    public ImageSource? Art
    {
        get { lock (_gate) return _art; }
    }

    /// <summary>Raised when <see cref="Current"/> or <see cref="Art"/> changes.</summary>
    public event Action? Changed;

    public void Previous() => Command(s => s.TrySkipPreviousAsync().AsTask(), "Previous");
    public void Next() => Command(s => s.TrySkipNextAsync().AsTask(), "Next");
    public void PlayPause() => Command(s => s.TryTogglePlayPauseAsync().AsTask(), "PlayPause");

    public void Dispose()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            manager = _manager;
            _manager = null;
        }
        try
        {
            if (manager != null) manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            Attach(null);
        }
        catch (Exception ex) { Log.Error("MediaSession.Dispose", ex); }
    }

    private async Task InitAsync()
    {
        try
        {
            var manager = await Task.Run(async () =>
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            lock (_gate)
            {
                if (_disposed) return;
                _manager = manager;
            }
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            Attach(manager.GetCurrentSession());
            await RefreshAsync();
        }
        catch (Exception ex) { Log.Error("MediaSession.Init", ex); }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, object? args)
    {
        try
        {
            Attach(sender.GetCurrentSession());
            _ = RefreshAsync();
        }
        catch (Exception ex) { Log.Error("MediaSession.CurrentSessionChanged", ex); }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        GlobalSystemMediaTransportControlsSession? old;
        lock (_gate)
        {
            if (_disposed) session = null;
            old = _session;
            _session = session;
        }
        if (ReferenceEquals(old, session)) return;
        if (old != null)
        {
            old.MediaPropertiesChanged -= OnSessionChanged;
            old.PlaybackInfoChanged -= OnSessionChanged;
        }
        if (session != null)
        {
            session.MediaPropertiesChanged += OnSessionChanged;
            session.PlaybackInfoChanged += OnSessionChanged;
        }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object? args)
        => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        // Coalesce overlapping refreshes: one runs, a flag asks it to go round again.
        lock (_gate)
        {
            if (_disposed) return;
            if (_refreshing != 0) { _again = true; return; }
            _refreshing = 1;
        }
        try
        {
            while (true)
            {
                NowPlaying? now = null;
                IRandomAccessStreamReference? thumbnail = null;
                try
                {
                    GlobalSystemMediaTransportControlsSession? session;
                    lock (_gate) session = _session;
                    if (session != null)
                    {
                        var props = await session.TryGetMediaPropertiesAsync();
                        thumbnail = props?.Thumbnail;
                        var playing = session.GetPlaybackInfo()?.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                        now = new NowPlaying(props?.Title ?? "", props?.Artist ?? "",
                            AppName(session.SourceAppUserModelId), playing);
                    }
                }
                catch (Exception ex) { Log.Error("MediaSession.Refresh", ex); }

                bool changed;
                lock (_gate)
                {
                    changed = !Equals(_current, now);
                    _current = now;
                }
                if (changed) RaiseChanged();
                await UpdateArtAsync(now, thumbnail);

                lock (_gate)
                {
                    if (_again && !_disposed) { _again = false; continue; }
                    _refreshing = 0;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate) { _refreshing = 0; _again = false; }
            Log.Error("MediaSession.Refresh", ex);
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("MediaSession.Changed", ex); }
    }

    /// <summary>
    /// A new track drops the old art at once; its thumbnail (which may arrive with a later properties change) is
    /// read and decoded once on a worker. Runs inside the serialized refresh, so loads never overlap.
    /// </summary>
    private async Task UpdateArtAsync(NowPlaying? now, IRandomAccessStreamReference? thumbnail)
    {
        var track = now is null ? null : now.Title + "\n" + now.Artist + "\n" + now.App;
        bool cleared, load;
        lock (_gate)
        {
            cleared = false;
            if (track != _artTrack)
            {
                _artTrack = track;
                _artRead = false;
                cleared = _art is not null;
                _art = null;
            }
            load = track is not null && thumbnail is not null && !_artRead;
            if (load) _artRead = true;
        }
        if (cleared) RaiseChanged();
        if (!load) return;

        var art = await Task.Run(async () =>
        {
            try
            {
                using var stream = await thumbnail!.OpenReadAsync();
                using var input = stream.AsStreamForRead();
                using var bytes = new MemoryStream();
                await input.CopyToAsync(bytes);
                bytes.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad; // the stream is not kept
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = ArtWidth;
                image.StreamSource = bytes;
                image.EndInit();
                image.Freeze();
                return (ImageSource?)image;
            }
            catch (Exception ex)
            {
                Log.Error("MediaSession.Art", ex); // at most once per track (_artRead)
                return null;
            }
        });
        if (art is null) return;
        lock (_gate)
        {
            if (_disposed || _artTrack != track) return; // the track changed meanwhile
            _art = art;
        }
        RaiseChanged();
    }

    private void Command(Func<GlobalSystemMediaTransportControlsSession, Task> action, string name)
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate) session = _session;
        if (session == null) return;
        _ = Task.Run(async () =>
        {
            try { await action(session); }
            catch (Exception ex) { Log.Error("MediaSession." + name, ex); }
        });
    }

    /// <summary>"SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" -> "Spotify", "chrome.exe" -> "Chrome".</summary>
    internal static string AppName(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return "";
        var name = appId[(appId.LastIndexOf('!') + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name[(name.LastIndexOf('.') + 1)..];
        if (name.Length == 0) return "";
        return AppNames.TryGetValue(name, out var friendly) ? friendly : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
