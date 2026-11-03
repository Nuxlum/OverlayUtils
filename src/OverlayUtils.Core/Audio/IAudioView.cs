using OverlayUtils.Core.Config;

namespace OverlayUtils.Core.Audio;

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

    void ToggleMute();

    /// <summary>Mute of the default input.</summary>
    bool MicMuted { get; }

    void ToggleMicMute();
}
