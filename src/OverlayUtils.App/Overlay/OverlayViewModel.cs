using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using OverlayUtils.Core;
using OverlayUtils.Core.Localization;
using OverlayUtils.Core.Overlay;

namespace OverlayUtils.App.Overlay;

internal sealed record TabVm(string Title, bool IsCurrent);

internal sealed record OptionVm(int Index, string Label, bool IsCursor, bool IsActive, bool IsPending);

internal sealed record FooterVm(string Keys, string Label, OverlayCommand? Command = null)
{
    public bool IsClickable => Command is not null;
}

/// <summary>
/// One visible row. <see cref="Index"/> is the navigator index, so clicks map back to it. <see cref="IsOn"/> is the
/// switch for Toggle/Expander, the mute for Slider and "playing" for Media; <see cref="Level"/> is the Slider's 0..1;
/// <see cref="IsHeld"/> marks a picked-up Reorder row; <see cref="Child"/> an indented row (an app under its channel).
/// </summary>
internal sealed record RowVm(
    int Index, string? Section, string Icon, string Label, ItemKind Kind, string Value, bool IsOn, string Hint,
    bool HasChevron, bool IsSelected, IReadOnlyList<OptionVm> Options, double Level = 0, bool IsHeld = false,
    bool Child = false)
{
    public const double SliderWidth = 120; // Python _render_slider bar length
    const double KnobRadius = 7;

    public double SliderFill => Level * SliderWidth;
    public Thickness KnobMargin => new(SliderFill - KnobRadius, 0, 0, 0);
}

/// <summary>
/// What the overlay window shows, rebuilt from the navigator by <see cref="Refresh"/> after every command.
/// All text is resolved here (Loc), so the XAML has no keys.
/// </summary>
internal sealed class OverlayViewModel : INotifyPropertyChanged
{
    /// <summary>Python OV_ACCENT, used until the Windows accent is applied.</summary>
    public static readonly Color FallbackAccent = Color.FromRgb(0x4C, 0xC2, 0xFF);

    string _clock = "";
    string _profileName = "";
    Brush? _profileBrush;
    IReadOnlyList<TabVm> _tabs = [];
    string _pageTitle = "";
    bool _isOnPage;
    IReadOnlyList<RowVm> _rows = [];
    string _description = "";
    IReadOnlyList<FooterVm> _footer = [];
    string _batteryGlyph = "";
    Brush _accentBrush = Frozen(FallbackAccent);
    double _sizeFactor = 1;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => AppPaths.AppName;
    public string Clock { get => _clock; private set => Set(ref _clock, value); }
    /// <summary>Pad battery level glyph, empty when no pad reports one.</summary>
    public string BatteryGlyph { get => _batteryGlyph; private set => Set(ref _batteryGlyph, value); }
    public string ProfileName { get => _profileName; private set => Set(ref _profileName, value); }
    public Brush? ProfileBrush { get => _profileBrush; private set => Set(ref _profileBrush, value); }
    public IReadOnlyList<TabVm> Tabs { get => _tabs; private set => Set(ref _tabs, value); }
    public string PageTitle { get => _pageTitle; private set => Set(ref _pageTitle, value); }
    public bool IsOnPage { get => _isOnPage; private set => Set(ref _isOnPage, value); }
    public IReadOnlyList<RowVm> Rows { get => _rows; private set => Set(ref _rows, value); }
    public string Description { get => _description; private set => Set(ref _description, value); }
    public IReadOnlyList<FooterVm> Footer { get => _footer; private set => Set(ref _footer, value); }

    /// <summary>Selection, switches, cursor outline. Replaced when the Windows accent is known.</summary>
    public Brush AccentBrush { get => _accentBrush; set => Set(ref _accentBrush, value); }

    /// <summary>Scale of the whole panel (Large size); set by the controller.</summary>
    public double SizeFactor { get => _sizeFactor; set => Set(ref _sizeFactor, value); }

    public void Refresh(OverlayNavigator nav, string clock, (byte R, byte G, byte B)? profileColor, string profileName,
        bool pad = false, int? battery = null)
    {
        Clock = clock;
        BatteryGlyph = battery is { } level ? Glyphs.Battery[Math.Clamp(level, 0, 3)] : "";
        ProfileName = profileName;
        ProfileBrush = profileColor is { } c ? Frozen(Color.FromRgb(c.R, c.G, c.B)) : null;

        var current = nav.CurrentTab.Id;
        Tabs = nav.VisibleTabs.Select(t => new TabVm(t.Title(), t.Id == current)).ToList();
        PageTitle = nav.Page switch
        {
            OverlayPage.Settings => Loc.T("Overlay.Page.Settings"),
            OverlayPage.Info => Loc.T("Overlay.Page.Info"),
            OverlayPage.Sub => nav.SubTitle,
            _ => "",
        };
        IsOnPage = nav.Page != OverlayPage.Tabs;

        var rows = new List<RowVm>();
        var items = nav.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!item.Visible()) continue;
            var selected = i == nav.Index;
            var options = new List<OptionVm>();
            if (item.Kind == ItemKind.Segmented)
            {
                var source = item.Options();
                for (var j = 0; j < source.Count; j++)
                {
                    var pending = nav.IsPending(i, j);
                    options.Add(new OptionVm(j, pending ? Loc.T("Overlay.ConfirmPending") : source[j].Label(),
                        selected && j == nav.Segment, source[j].Active?.Invoke() == true, pending));
                }
            }
            var hint = item.Hint?.Invoke() ?? "";
            var section = item.Section?.Invoke();
            var value = item.Kind is ItemKind.Choice or ItemKind.Info or ItemKind.Slider or ItemKind.Media or ItemKind.Reorder
                ? item.Value()
                : "";
            var isOn = item.Kind is ItemKind.Toggle or ItemKind.Expander or ItemKind.Slider or ItemKind.Media && item.IsOn();
            var sliderLevel = item.Kind == ItemKind.Slider ? SliderLevel(value) : 0;
            if (item.Kind == ItemKind.Slider && isOn) value = Loc.T("Overlay.Audio.Muted");
            rows.Add(new RowVm(i, string.IsNullOrEmpty(section) ? null : section.ToUpper(Loc.Culture), item.Icon,
                item.Label(), item.Kind, value, isOn, hint,
                item.Kind == ItemKind.Action && hint.Length == 0 && (item.Stay || item.OpensInfo || item.SubPage != null),
                selected, options, sliderLevel, selected && nav.Holding, item.Child));
        }
        Rows = rows;
        Description = nav.Description();
        Footer = nav.Footer(pad).Select(h => new FooterVm(h.Keys, h.Label, h.Command)).ToList();
    }

    /// <summary>A Slider's Value is its percent ("45%").</summary>
    static double SliderLevel(string percent) =>
        double.TryParse(percent.TrimEnd('%', ' '), NumberStyles.Number, CultureInfo.InvariantCulture, out var p)
            ? Math.Clamp(p / 100, 0, 1)
            : 0;

    static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
