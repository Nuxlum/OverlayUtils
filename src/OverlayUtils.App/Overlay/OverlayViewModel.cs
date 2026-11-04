using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OverlayUtils.App.Games;
using OverlayUtils.Core;
using OverlayUtils.Core.Games;
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
/// One game of the Games tab. Tiles live as long as the list of games shown does not change, so moving the
/// selection only flips <see cref="IsSelected"/> and the art arrives later without rebuilding the grid.
/// </summary>
internal sealed class TileVm(int index, GameEntry game, string badge) : INotifyPropertyChanged
{
    ImageSource? _cover;
    ImageSource? _icon;
    bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; } = index;
    public GameEntry Game { get; } = game;
    public string Title => Game.Title;
    public string Badge { get; } = badge;
    public ImageSource? Cover { get => _cover; set { _cover = value; Changed(); Changed(nameof(HasArt)); } }
    public ImageSource? Icon { get => _icon; set { _icon = value; Changed(); Changed(nameof(HasArt)); } }
    /// <summary>A cover or an icon is shown, so the placeholder glyph is not.</summary>
    public bool HasArt => _cover is not null || _icon is not null;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Changed();
        }
    }

    void Changed([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
    IReadOnlyList<TileVm> _tiles = [];
    string _tilesCulture = "";
    bool _showTiles;
    int _selectedTile = -1;
    CancellationTokenSource? _artLoad;              // cancelled when the tiles are rebuilt
    readonly HashSet<TileVm> _withArt = [];         // tiles that hold their art now
    readonly HashSet<TileVm> _requested = [];       // tiles whose art a worker is decoding

    /// <summary>Columns of the Games grid (the window's UniformGrid).</summary>
    public const int TileColumns = 3;
    /// <summary>Tiles kept with their art beyond the requested range (four rows on each side).</summary>
    const int ArtKeep = 4 * TileColumns;

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

    /// <summary>The Games grid, drawn under the rows while the page has a visible Grid row.</summary>
    public IReadOnlyList<TileVm> Tiles { get => _tiles; private set => Set(ref _tiles, value); }
    public bool ShowTiles { get => _showTiles; private set => Set(ref _showTiles, value); }
    /// <summary>Index of the selected tile, -1 when the grid is not selected.</summary>
    public int SelectedTile { get => _selectedTile; private set => Set(ref _selectedTile, value); }

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
                : item.Kind == ItemKind.Grid ? (item.Tiles?.Invoke().Count ?? 0).ToString(Loc.Culture)
                : "";
            var isOn = item.Kind is ItemKind.Toggle or ItemKind.Expander or ItemKind.Slider or ItemKind.Media && item.IsOn();
            var sliderLevel = item.Kind == ItemKind.Slider ? SliderLevel(value) : 0;
            if (item.Kind == ItemKind.Slider && isOn) value = Loc.T("Overlay.Audio.Muted");
            if (nav.IsRemovePending(i)) value = Loc.T("Overlay.DeletePending");
            rows.Add(new RowVm(i, string.IsNullOrEmpty(section) ? null : section.ToUpper(Loc.Culture), item.Icon,
                item.Label(), item.Kind, value, isOn, hint,
                item.Kind == ItemKind.Action && hint.Length == 0 && (item.Stay || item.OpensInfo || item.SubPage != null),
                selected, options, sliderLevel, selected && nav.Holding, item.Child));
        }
        Rows = rows;
        RefreshTiles(nav, items);
        Description = nav.Description();
        Footer = nav.Footer(pad).Select(h => new FooterVm(h.Keys, h.Label, h.Command)).ToList();
    }

    void RefreshTiles(OverlayNavigator nav, IReadOnlyList<OverlayItem> items)
    {
        var grid = items.FirstOrDefault(i => i.Kind == ItemKind.Grid && i.Visible());
        ShowTiles = grid is not null;
        if (grid?.Tiles?.Invoke() is not { } games) return; // kept while hidden: coming back to the tab is instant
        // Whole records: a re-read that only found new art or a new title must show it.
        if (_tilesCulture != Loc.Culture.Name || games.Count != _tiles.Count
            || !games.SequenceEqual(_tiles.Select(t => t.Game)))
        {
            _tilesCulture = Loc.Culture.Name;
            _artLoad?.Cancel();
            _artLoad = null;
            _withArt.Clear();
            _requested.Clear();
            Tiles = games.Select((g, i) => new TileVm(i, g, Loc.T("Games.Source." + g.Source))).ToList();
        }
        var selected = nav.Selected?.Kind == ItemKind.Grid && _tiles.Count > 0 ? nav.SelectedTileIndex : -1;
        foreach (var tile in _tiles) tile.IsSelected = tile.Index == selected;
        SelectedTile = selected;
    }

    /// <summary>
    /// The tiles from <paramref name="first"/> to <paramref name="last"/> are on screen or next to it (window, UI
    /// thread). Their art is set at once when decoded already, else decoded on a worker; tiles further than
    /// <see cref="ArtKeep"/> away drop theirs, so a big library holds only a few rows of images.
    /// </summary>
    public void ShowArt(int first, int last)
    {
        foreach (var tile in _withArt.Where(t => t.Index < first - ArtKeep || t.Index > last + ArtKeep).ToList())
        {
            tile.Cover = null;
            tile.Icon = null;
            _withArt.Remove(tile);
        }
        var pending = new List<TileVm>();
        for (var i = Math.Max(0, first); i <= last && i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            if (_withArt.Contains(tile) || _requested.Contains(tile)) continue;
            if (GameArt.TryCached(tile.Game.ArtPath, tile.Game.IconPath, out var cover, out var icon))
            {
                tile.Cover = cover;
                tile.Icon = icon;
                _withArt.Add(tile);
            }
            else
            {
                pending.Add(tile);
                _requested.Add(tile);
            }
        }
        if (pending.Count == 0) return;
        var cancel = _artLoad ??= new CancellationTokenSource();
        var token = cancel.Token;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = Task.Run(() =>
        {
            foreach (var tile in pending)
            {
                if (token.IsCancellationRequested) return;
                var cover = tile.Game.ArtPath is { } art ? GameArt.Cover(art) : null;
                var icon = cover is null && tile.Game.IconPath is { } path ? GameArt.Icon(path) : null;
                dispatcher.BeginInvoke(() =>
                {
                    if (token.IsCancellationRequested) return; // the grid was rebuilt meanwhile
                    _requested.Remove(tile);
                    tile.Cover = cover;
                    tile.Icon = icon;
                    _withArt.Add(tile);
                });
            }
        }, token);
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
