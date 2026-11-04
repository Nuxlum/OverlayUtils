using OverlayUtils.Core.Audio;
using OverlayUtils.Core.Config;

namespace OverlayUtils.App.Audio;

/// <summary>The overlay's view of <see cref="AudioService"/>: cached reads and queued commands, never blocking.</summary>
internal sealed class AudioServiceView(AudioService service) : IAudioView
{
    public IReadOnlyList<AudioDeviceRef> Available(AudioFlow flow) => service.Available(flow);
    public AudioDeviceRef? Default(AudioFlow flow) => service.Default(flow);
    public void Cycle(AudioFlow flow, int direction) => service.Cycle(flow, direction);
    public double Volume => service.Volume;
    public bool Muted => service.Muted;
    public void StepVolume(int direction) => service.StepVolume(direction);
    public void SetVolume(double level) => service.SetVolume(level);
    public void ToggleMute() => service.ToggleMute();
    public bool MicMuted => service.MicMuted;
    public void ToggleMicMute() => service.ToggleMicMute();
    public IReadOnlyList<AudioApp> Apps => service.Apps;
    public void ApplyMix() => service.ApplyMix();
}
