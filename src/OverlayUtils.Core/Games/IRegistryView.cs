using Microsoft.Win32;

namespace OverlayUtils.Core.Games;

/// <summary>Read-only registry access for the library readers; the self-test passes a fake.</summary>
public interface IRegistryView
{
    /// <summary>A value as text (numbers are converted), or null when the key or value is missing.</summary>
    string? GetString(RegistryHive hive, string key, string value, bool wow64 = false);

    /// <summary>Names of the direct subkeys, empty when the key is missing.</summary>
    IReadOnlyList<string> SubKeys(RegistryHive hive, string key, bool wow64 = false);
}

/// <summary>The real registry, opened read-only.</summary>
public sealed class WindowsRegistryView : IRegistryView
{
    public string? GetString(RegistryHive hive, string key, string value, bool wow64 = false)
    {
        try
        {
            using var sub = Open(hive, key, wow64);
            return sub?.GetValue(value)?.ToString();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> SubKeys(RegistryHive hive, string key, bool wow64 = false)
    {
        try
        {
            using var sub = Open(hive, key, wow64);
            return sub?.GetSubKeyNames() ?? [];
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    static RegistryKey? Open(RegistryHive hive, string key, bool wow64)
    {
        using var root = RegistryKey.OpenBaseKey(hive, wow64 ? RegistryView.Registry32 : RegistryView.Default);
        return root.OpenSubKey(key, writable: false);
    }
}
