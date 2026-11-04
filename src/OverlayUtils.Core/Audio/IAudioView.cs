using OverlayUtils.Core.Config;

namespace OverlayUtils.Core.Audio;

/// <summary>A program with audio sessions on the default output: full exe path and display name.</summary>
public sealed record AudioApp(string Exe, string Name);

/// <summary>
/// What the overlay reads and does with the audio devices (implemented over <see cref="AudioService"/> in the App,
/// faked in the self-test). Members are non-blocking and safe from the UI thread.
/// </summary>
public interface IAudioView
{
    /// <summary>Connected devices of that direction.</summary>
    IReadOnlyList<AudioDeviceRef> Available(AudioFlow flow);

    AudioDeviceRef? Default(AudioFlow flow);

    /// <summary>Makes the next (+1) or previous (-1) available device the default.</summary>
    void Cycle(AudioFlow flow, int direction);

    /// <summary>Master volume of the default output, 0..1.</summary>
    double Volume { get; }

    bool Muted { get; }

    void StepVolume(int direction);

    /// <summary>Sets the master volume directly (0..1), within the device cap; above 0 unmutes.</summary>
    void SetVolume(double level);

    void ToggleMute();

    /// <summary>Mute of the default input.</summary>
    bool MicMuted { get; }

    void ToggleMicMute();

    /// <summary>
    /// Programs with a live session (active or inactive, process still running) on the default output,
    /// one per exe, in order of first appearance; our own process and system sounds excluded.
    /// </summary>
    IReadOnlyList<AudioApp> Apps { get; }

    /// <summary>Re-applies the stored mix of the default output to every live session.</summary>
    void ApplyMix();
}
