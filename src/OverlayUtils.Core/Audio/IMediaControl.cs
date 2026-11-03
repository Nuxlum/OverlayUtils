namespace OverlayUtils.Core.Audio;

/// <summary>The current media session (title, artist, source app) as the Audio tab shows it.</summary>
public sealed record NowPlaying(string Title, string Artist, string App, bool Playing);

/// <summary>Transport controls of the system media session. Implemented in the App (WinRT).</summary>
public interface IMediaControl
{
    NowPlaying? Current { get; }
    event Action? Changed;
    void Previous();
    void Next();
    void PlayPause();
}
