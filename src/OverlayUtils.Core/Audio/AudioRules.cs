using OverlayUtils.Core.Config;

namespace OverlayUtils.Core.Audio;

/// <summary>Pure audio decisions: device priority and volume stepping.</summary>
public static class AudioRules
{
    public const double Step = 0.05;

    /// <summary>Device to make default, or null if the current default is already the right one or none is available.</summary>
    public static string? Target(IReadOnlyList<AudioDeviceRef> ranked, IReadOnlySet<string> available, string? currentDefault)
    {
        foreach (var device in ranked)
        {
            if (!available.Contains(device.Id)) continue;
            return device.Id == currentDefault ? null : device.Id;
        }
        return null;
    }

    /// <summary>Appends devices not yet in the list (keeps order and names; refreshes the name of known ids).</summary>
    public static List<AudioDeviceRef> Merge(IReadOnlyList<AudioDeviceRef> list, IEnumerable<AudioDeviceRef> seen)
    {
        var result = new List<AudioDeviceRef>(list);
        foreach (var device in seen)
        {
            var index = result.FindIndex(d => d.Id == device.Id);
            if (index >= 0) result[index] = device;
            else result.Add(device);
        }
        return result;
    }

    /// <summary>Seed for a first run: current default first, then the others in the given order.</summary>
    public static List<AudioDeviceRef> Seed(IEnumerable<AudioDeviceRef> devices, string? currentDefault)
    {
        var all = devices.ToList();
        var first = all.Where(d => d.Id == currentDefault);
        return [.. first, .. all.Where(d => d.Id != currentDefault)];
    }

    /// <summary>One step up (+1) or down (-1) on exact 5 % multiples, clamped to [0, cap].</summary>
    public static double StepVolume(double current, int direction, double cap)
    {
        var steps = (int)Math.Round(current / Step, MidpointRounding.AwayFromZero);
        var onGrid = Math.Round(current / Step) * Step;
        if (Math.Abs(onGrid - current) > 1e-9)
            steps = direction > 0 ? (int)Math.Floor(current / Step) + 1 : (int)Math.Ceiling(current / Step) - 1;
        else
            steps += direction > 0 ? 1 : -1;
        var value = steps * 5 / 100.0;
        return Math.Clamp(value, 0.0, cap);
    }

    public static double CapFor(IReadOnlyDictionary<string, int> capsPercent, string deviceId) =>
        capsPercent.TryGetValue(deviceId, out var percent) ? Math.Clamp(percent, 0, 100) / 100.0 : 1.0;
}
