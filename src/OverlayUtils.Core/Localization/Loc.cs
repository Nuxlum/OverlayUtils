using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace OverlayUtils.Core.Localization;

/// <summary>
/// English / Italian strings from Strings.resx and Strings.it.resx.
///
/// Code:  <c>Loc.T("Tray.Exit")</c>, <c>Loc.T("Monitor.Switched", name)</c>
/// XAML:  <c>{Binding [Tray.Exit], Source={x:Static loc:Loc.Instance}}</c>
///        (bindings refresh by themselves when the language changes)
///
/// The language follows the Windows display language unless the user picks
/// one ("language" in config.json: "auto", "en" or "it").
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static readonly string[] Supported = ["en", "it"];
    public const string Auto = "auto";

    private static readonly ResourceManager Resources =
        new("OverlayUtils.Core.Localization.Strings", typeof(Loc).Assembly);

    /// <summary>Windows display language, captured before we change any thread culture.</summary>
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;

    public static Loc Instance { get; } = new();

    private Loc() { }

    public static CultureInfo Culture { get; private set; } = Resolve(Auto);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changes (any thread).</summary>
    public static event Action? LanguageChanged;

    /// <summary>Indexer used by XAML bindings.</summary>
    public string this[string key] => T(key);

    /// <summary>Two-letter code actually in use ("en" or "it").</summary>
    public static string CurrentCode => Culture.TwoLetterISOLanguageName;

    /// <summary>Language Windows would give us in "auto" mode.</summary>
    public static string SystemCode => Resolve(Auto).TwoLetterISOLanguageName;

    public static string T(string key)
    {
        var value = Resources.GetString(key, Culture);
        return string.IsNullOrEmpty(value) ? $"[{key}]" : value; // visible, never crashes
    }

    public static string T(string key, params object?[] args) =>
        string.Format(Culture, T(key), args);

    /// <summary>Apply a "language" setting value: "auto", "en" or "it".</summary>
    public static void SetLanguage(string setting)
    {
        var culture = Resolve(setting);
        if (Equals(culture, Culture)) return;
        Culture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }

    internal static CultureInfo Resolve(string setting)
    {
        var code = setting is "en" or "it" ? setting : SystemCulture.TwoLetterISOLanguageName;
        return CultureInfo.GetCultureInfo(code == "it" ? "it" : "en");
    }

    /// <summary>For tests: every key in the neutral (English) resources.</summary>
    internal static IReadOnlyDictionary<string, string> AllStrings(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        var result = new Dictionary<string, string>();
        if (set is null) return result;
        foreach (System.Collections.DictionaryEntry entry in set)
            result[(string)entry.Key] = entry.Value as string ?? "";
        return result;
    }
}
