using OverlayUtils.Core.Audio;
using OverlayUtils.Core.Diagnostics;
using Windows.Media.Control;

namespace OverlayUtils.App.Audio;

/// <summary>
/// System media session (WinRT GlobalSystemMediaTransportControls). Events may be raised on any thread.
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

    public MediaSession()
    {
        _ = InitAsync();
    }

    public NowPlaying? Current
    {
        get { lock (_gate) return _current; }
    }

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
                try
                {
                    GlobalSystemMediaTransportControlsSession? session;
                    lock (_gate) session = _session;
                    if (session != null)
                    {
                        var props = await session.TryGetMediaPropertiesAsync();
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
                if (changed)
                {
                    try { Changed?.Invoke(); }
                    catch (Exception ex) { Log.Error("MediaSession.Changed", ex); }
                }

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
