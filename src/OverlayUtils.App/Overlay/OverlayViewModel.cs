using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OverlayUtils.App.Games;
using OverlayUtils.Core;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Games;
using OverlayUtils.Core.Localization;
using OverlayUtils.Core.Overlay;

namespace OverlayUtils.App.Overlay;

/// <summary>
/// One page icon of the rail. <see cref="VisibleIndex"/> is the index into the navigator's visible tabs (what a
/// click passes to ClickTab); <see cref="IsCurrent"/> is lit only while that tab's page is shown (not on Settings).
/// </summary>
internal sealed record RailVm(string Glyph, string Title, bool IsCurrent, int VisibleIndex);

internal sealed record OptionVm(int Index, string Label, bool IsCursor, bool IsActive, bool IsPending);

internal sealed record FooterVm(string Keys, string Label, OverlayCommand? Command = null)
{
    public bool IsClickable => Command is not null;
}

/// <summary>
/// One visible row. <see cref="Index"/> is the navigator index, so clicks map back to it. <see cref="IsOn"/> is the
/// switch for Toggle/Expander, the mute for Slider and "playing" for Media; <see cref="Level"/> is the Slider's 0..1;
/// <see cref="IsHeld"/> marks a picked-up Reorder row; <see cref="Child"/> an indented row (an app under its channel).
/// On a tiles page (Home) <see cref="Caption"/> is the small line at the top of the tile.
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

    /// <summary>On a tiles page the row spans both columns (volume, now playing).</summary>
    public bool IsWide => Kind is ItemKind.Slider or ItemKind.Media;

    /// <summary>
    /// Top line of the tile: the output device for the volume, the section for media, nothing for an action (its
    /// label is the tile's text), else the label. Empty off a tiles page.
    /// </summary>
    public string Caption { get; init; } = "";

    /// <summary>Album art of a Media row (the Home tile shows it), null when there is none.</summary>
    public ImageSource? Art { get; init; }
}

/// <summary>What the stage's context card shows for the selected row.</summary>
internal enum ContextKind { Text, Monitors, Mixer, Media, Volume }

/// <summary>One monitor profile of the Monitors card; <see cref="Active"/> is the profile in use (lit).</summary>
internal sealed record MonitorVm(string Name, bool Active);

/// <summary>One vertical bar of the Mixer card (a channel or the master): level 0..1, the selected one outlined.</summary>
internal sealed record BarVm(string Name, double Level, bool Muted, bool Selected);

/// <summary>
/// Stage context of the selected row. <see cref="Title"/> and <see cref="Text"/> are the card's heading and body;
/// <see cref="Value"/> the row's value line; <see cref="Glyph"/> the device icon (Volume); <see cref="Apps"/> the
/// apps of the selected channel (Mixer); <see cref="Art"/> the album art (Media). Equal contexts compare equal, so a
/// refresh that changes nothing keeps the card as it is.
/// </summary>
internal sealed record ContextVm(ContextKind Kind, string Title, string Text)
{
    public string Value { get; init; } = "";
    public string Glyph { get; init; } = "";
    public IReadOnlyList<MonitorVm> Monitors { get; init; } = [];
    public IReadOnlyList<BarVm> Bars { get; init; } = [];
    public string Apps { get; init; } = "";
    public ImageSource? Art { get; init; }

    public bool Equals(ContextVm? other) =>
        other is not null && Kind == other.Kind && Title == other.Title && Text == other.Text && Value == other.Value
        && Glyph == other.Glyph && Apps == other.Apps && ReferenceEquals(Art, other.Art)
        && Monitors.SequenceEqual(other.Monitors) && Bars.SequenceEqual(other.Bars);

    public override int GetHashCode() => HashCode.Combine(Kind, Title, Text, Value, Apps);
}

/// <summary>
/// What the stage context reads besides the navigator (set once by the controller): the cap of the default output,
/// the monitor profiles with the active one (for the many-profiles Choice shape), the album art.
/// </summary>
internal sealed record StageSources(Func<int?> OutputCap, Func<IReadOnlyList<MonitorVm>> Monitors, Func<ImageSource?> Art)
{
    public static readonly StageSources None = new(() => null, () => [], () => null);

    /// <summary>The Games page's source pills (All = null first) and the one shown now.</summary>
    public Func<IReadOnlyList<GameSource?>> GameFilters { get; init; } = () => [];
    public Func<GameSource?> GameFilter { get; init; } = () => null;
}

/// <summary>One source filter pill of the Games page (null: All); a click shows that source.</summary>
internal sealed record PillVm(GameSource? Source, string Label, bool IsCurrent);

/// <summary>
/// One game of the Games tab. Tiles live as long as the list of games shown does not change, so moving the
/// selection only flips <see cref="IsSelected"/> / <see cref="IsDimmed"/> and the art arrives later without
/// rebuilding the grid. <see cref="Ambient"/> is the cover's colour (the backdrop tint while it is selected).
/// </summary>
internal sealed class TileVm(int index, GameEntry game, string badge) : INotifyPropertyChanged
{
    ImageSource? _cover;
    ImageSource? _icon;
    Color? _ambient;
    bool _isSelected;
    bool _isDimmed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; } = index;
    public GameEntry Game { get; } = game;
    public string Title => Game.Title;
    public string Badge { get; } = badge;
    public ImageSource? Cover { get => _cover; set { _cover = value; Changed(); Changed(nameof(HasArt)); } }
    public ImageSource? Icon { get => _icon; set { _icon = value; Changed(); Changed(nameof(HasArt)); } }
    /// <summary>A cover or an icon is shown, so the placeholder glyph is not.</summary>
    public bool HasArt => _cover is not null || _icon is not null;
    public Color? Ambient
    {
        get => _ambient;
        set
        {
            if (_ambient == value) return;
            _ambient = value;
            Changed();
        }
    }
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
    /// <summary>The tile is not on the selected tile's row of the shelf (drawn fainter).</summary>
    public bool IsDimmed
    {
        get => _isDimmed;
        set
        {
            if (_isDimmed == value) return;
            _isDimmed = value;
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
    IReadOnlyList<RailVm> _railItems = [];
    bool _settingsCurrent;
    string _settingsTitle = "";
    string _eyebrow = AppPaths.AppName;
    string _pageTitle = "";
    bool _isOnPage;
    OverlayFrame _frame = OverlayLayout.Frame(1920, 1);
    string _outputDevice = "";
    string _outputGlyph = "";
    string _batteryText = "";
    IReadOnlyList<RowVm> _rows = [];
    string _description = "";
    ContextVm? _context;
    IReadOnlyList<FooterVm> _footer = [];
    string _batteryGlyph = "";
    Brush _accentBrush = Frozen(FallbackAccent);
    double _sizeFactor = 1;
    IReadOnlyList<TileVm> _tiles = [];
    string _tilesCulture = "";
    string _pillsCulture = "";
    bool _showTiles;
    bool _showHomeTiles;
    int _selectedTile = -1;
    TileVm? _hero;
    Color? _ambient;
    IReadOnlyList<PillVm> _pills = [];
    string _playKeys = "";
    string _playLabel = "";
    string _gameCount = "";
    CancellationTokenSource? _artLoad;              // cancelled when the tiles are rebuilt
    readonly HashSet<TileVm> _withArt = [];         // tiles that hold their art now
    readonly HashSet<TileVm> _requested = [];       // tiles whose art a worker is decoding

    /// <summary>Rows of tiles kept with their art beyond the requested range, on each side.</summary>
    const int ArtKeepRows = 4;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Clock of the status corner ("21:34").</summary>
    public string Clock { get => _clock; private set => Set(ref _clock, value); }
    /// <summary>Pad battery level glyph, empty when no pad reports one (wired).</summary>
    public string BatteryGlyph { get => _batteryGlyph; private set => Set(ref _batteryGlyph, value); }
    /// <summary>Pad battery as a percentage ("84%"), empty when SDL reports none.</summary>
    public string BatteryText { get => _batteryText; private set => Set(ref _batteryText, value); }
    /// <summary>Name of the default output device in the status corner, empty when there is none.</summary>
    public string OutputDevice { get => _outputDevice; private set => Set(ref _outputDevice, value); }
    /// <summary>Speaker or headphones glyph of <see cref="OutputDevice"/>, empty when there is none.</summary>
    public string OutputGlyph { get => _outputGlyph; private set => Set(ref _outputGlyph, value); }

    /// <summary>One icon per visible tab, in LT / RT order. The Settings gear is separate.</summary>
    public IReadOnlyList<RailVm> RailItems { get => _railItems; private set => Set(ref _railItems, value); }
    /// <summary>The gear is lit: Settings, Info, or a sub-page opened from them.</summary>
    public bool SettingsCurrent { get => _settingsCurrent; private set => Set(ref _settingsCurrent, value); }
    /// <summary>Tooltip of the gear.</summary>
    public string SettingsTitle { get => _settingsTitle; private set => Set(ref _settingsTitle, value); }

    /// <summary>Small accent line above the page title (drawn upper case). Not touched by <see cref="Refresh"/>.</summary>
    public string Eyebrow { get => _eyebrow; set => Set(ref _eyebrow, value); }
    /// <summary>Title of the page shown: the tab, Settings, Info or the sub-page.</summary>
    public string PageTitle { get => _pageTitle; private set => Set(ref _pageTitle, value); }
    /// <summary>Settings, Info or a sub-page: the title gets a back chevron (click = B).</summary>
    public bool IsOnPage { get => _isOnPage; private set => Set(ref _isOnPage, value); }

    /// <summary>Rail, column, gutter and cover columns of the monitor the overlay is on (DIPs); set by the controller.</summary>
    public OverlayFrame Frame
    {
        get => _frame;
        set
        {
            if (_frame == value) return;
            _frame = value;
            foreach (var name in new[] { nameof(Frame), nameof(RailWidth), nameof(ColumnWidth), nameof(ColumnPadding),
                         nameof(StagePadding), nameof(TileColumns) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            UpdateDimmed(); // the shelf's rows changed with the columns; the navigator keeps the game by id
        }
    }
    public GridLength RailWidth => new(_frame.RailWidth);
    public GridLength ColumnWidth => new(_frame.ColumnWidth);
    /// <summary>Inside the column: the gutter at the top and left, half of it on the right, two thirds at the bottom.</summary>
    public Thickness ColumnPadding => new(_frame.Gutter, _frame.Gutter, _frame.Gutter / 2, _frame.Gutter * 2 / 3);
    /// <summary>Inside the stage: status corner and context card keep the gutter from the screen edges.</summary>
    public Thickness StagePadding => new(_frame.Gutter);
    /// <summary>Columns of the Games grid (the frame's cover columns).</summary>
    public int TileColumns => Math.Max(1, _frame.CoverColumns);
    /// <summary>The page shows the Games grid: the column takes the stage too (no context card).</summary>
    public bool IsWide => _showTiles;
    public IReadOnlyList<RowVm> Rows { get => _rows; private set => Set(ref _rows, value); }
    public string Description { get => _description; private set => Set(ref _description, value); }
    /// <summary>Stage card of the selected row, null when there is nothing to show (the card is hidden).</summary>
    public ContextVm? Context { get => _context; private set => Set(ref _context, value); }
    /// <summary>Cap, monitor profiles and album art for the stage context; set by the controller.</summary>
    public StageSources Sources { get; set; } = StageSources.None;
    public IReadOnlyList<FooterVm> Footer { get => _footer; private set => Set(ref _footer, value); }

    /// <summary>The Games grid, drawn under the rows while the page has a visible Grid row.</summary>
    public IReadOnlyList<TileVm> Tiles { get => _tiles; private set => Set(ref _tiles, value); }
    public bool ShowTiles
    {
        get => _showTiles;
        private set
        {
            if (_showTiles == value) return;
            Set(ref _showTiles, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWide)));
        }
    }
    /// <summary>The page is a tiles tab (Home): <see cref="Rows"/> are drawn as shortcut tiles, without sections.</summary>
    public bool ShowHomeTiles => _showHomeTiles;
    /// <summary>Index of the selected tile, -1 when the grid is not selected.</summary>
    public int SelectedTile { get => _selectedTile; private set => Set(ref _selectedTile, value); }
    /// <summary>The selected game, drawn large in the Games hero (null when no tile is selected).</summary>
    public TileVm? Hero { get => _hero; private set => Set(ref _hero, value); }
    /// <summary>Backdrop tint of the Games page: the selected cover's colour (null: no tint, or not on Games).</summary>
    public Color? Ambient { get => _ambient; private set => Set(ref _ambient, value); }
    /// <summary>Source filter pills of the Games page (All first); reassigned only when they change.</summary>
    public IReadOnlyList<PillVm> Pills { get => _pills; private set => Set(ref _pills, value); }
    /// <summary>Key and label of the hero's Play button ("A" / "Enter", "Play").</summary>
    public string PlayKeys { get => _playKeys; private set => Set(ref _playKeys, value); }
    public string PlayLabel { get => _playLabel; private set => Set(ref _playLabel, value); }
    /// <summary>Number of games on the shelf (right end of the pills row).</summary>
    public string GameCount { get => _gameCount; private set => Set(ref _gameCount, value); }

    /// <summary>Selection, switches, cursor outline. Replaced when the Windows accent is known.</summary>
    public Brush AccentBrush { get => _accentBrush; set => Set(ref _accentBrush, value); }

    /// <summary>Scale of the frame's content (Large size); set by the controller.</summary>
    public double SizeFactor { get => _sizeFactor; set => Set(ref _sizeFactor, value); }

    /// <summary>
    /// Only the rail (lit page icon, gear): while the old page slides out, the rail already shows where the command
    /// went; the page itself is refreshed when it comes in.
    /// </summary>
    public void RefreshRail(OverlayNavigator nav)
    {
        var settings = nav.Page is OverlayPage.Settings or OverlayPage.Info
            || nav.Page == OverlayPage.Sub && nav.SubOrigin != OverlayPage.Tabs;
        SettingsCurrent = settings;
        SettingsTitle = Loc.T("Overlay.Rail.Settings");
        var current = nav.CurrentTab.Id;
        var rail = nav.VisibleTabs
            .Select((t, i) => new RailVm(RailGlyph(t.Id), t.Title(), !settings && t.Id == current, i))
            .ToList();
        if (!rail.SequenceEqual(_railItems)) RailItems = rail; // most commands only move the selection
    }

    public void Refresh(OverlayNavigator nav, string clock, string outputDevice, bool pad = false, int? battery = null,
        int? batteryPercent = null)
    {
        Clock = clock;
        BatteryGlyph = battery is { } level ? Glyphs.Battery[Math.Clamp(level, 0, 3)] : "";
        BatteryText = batteryPercent is { } percent ? Loc.T("Overlay.Status.Battery", percent) : "";
        OutputDevice = outputDevice;
        OutputGlyph = outputDevice.Length > 0 ? OverlayContent.OutputIcon(outputDevice) : "";

        RefreshRail(nav);
        PageTitle = nav.Page switch
        {
            OverlayPage.Settings => Loc.T("Overlay.Page.Settings"),
            OverlayPage.Info => Loc.T("Overlay.Page.Info"),
            OverlayPage.Sub => nav.SubTitle,
            _ => nav.CurrentTab.Title(),
        };
        IsOnPage = nav.Page != OverlayPage.Tabs;

        var tiles = nav.Page == OverlayPage.Tabs && nav.CurrentTab.Tiles;
        var art = Art();
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
            var label = item.Label();
            var caption = !tiles ? ""
                : item.Kind == ItemKind.Slider && outputDevice.Length > 0 ? outputDevice
                : item.Kind == ItemKind.Media && !string.IsNullOrEmpty(section) ? section
                : item.Kind == ItemKind.Action ? "" // the label is the tile's text already
                : label;
            rows.Add(new RowVm(i, string.IsNullOrEmpty(section) ? null : section.ToUpper(Loc.Culture), item.Icon,
                label, item.Kind, value, isOn, hint,
                item.Kind == ItemKind.Action && hint.Length == 0 && (item.Stay || item.OpensInfo || item.SubPage != null),
                selected, options, sliderLevel, selected && nav.Holding, item.Child)
            {
                Caption = caption,
                Art = item.Kind == ItemKind.Media ? art : null,
            });
        }
        SetRows(rows, tiles);
        RefreshTiles(nav, items, pad);
        Description = nav.Description();
        Context = BuildContext(nav, items, rows, outputDevice, art);
        Footer = nav.Footer(pad).Select(h => new FooterVm(h.Keys, h.Label, h.Command)).ToList();
    }

    /// <summary>
    /// Rows and the Home-tiles flag in one step: both fields first, then the notifications, so the list that shows
    /// them (RowList or HomeTileList) generates its containers once, for the new rows, on a Home / tab switch.
    /// </summary>
    void SetRows(IReadOnlyList<RowVm> rows, bool homeTiles)
    {
        var tilesChanged = _showHomeTiles != homeTiles;
        _showHomeTiles = homeTiles;
        _rows = rows;
        if (tilesChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowHomeTiles)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rows)));
    }

    /// <summary>The album art from <see cref="Sources"/>; a failure only loses the art.</summary>
    ImageSource? Art()
    {
        try { return Sources.Art(); }
        catch (Exception e)
        {
            Log.Error("Overlay album art", e);
            return null;
        }
    }

    /// <summary>
    /// The stage card for the selected row: the monitors for the Screen row, the mixer for the Audio page's volume
    /// and channel rows, the device and its cap for the Home volume tile, the album art for Now playing, else the
    /// row's label, value and description. Null when nothing is selected or there is nothing to say.
    /// </summary>
    ContextVm? BuildContext(OverlayNavigator nav, IReadOnlyList<OverlayItem> items, IReadOnlyList<RowVm> rows,
        string outputDevice, ImageSource? art)
    {
        try
        {
            if (nav.Index < 0 || nav.Index >= items.Count) return null;
            var item = items[nav.Index];
            var at = -1;
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].IsSelected) at = i;
            if (at < 0) return null;
            var row = rows[at];
            var description = item.Description();
            var key = item.Key ?? "";
            var tab = nav.Page == OverlayPage.Tabs ? nav.CurrentTab.Id : "";

            if (key == "controls.monitor")
            {
                IReadOnlyList<MonitorVm> monitors = item.Kind == ItemKind.Segmented
                    ? row.Options.Select(o => new MonitorVm(o.Label, o.IsActive)).ToList()
                    : Sources.Monitors(); // Choice (many profiles): the options are not rows
                return new ContextVm(ContextKind.Monitors, row.Label, description) { Monitors = monitors };
            }
            if (tab == OverlayContent.AudioTabId && (key == "audio.volume" || IsChannel(key)))
            {
                // One bar per channel row, the master last (mockup); app rows are not bars.
                var bars = new List<BarVm>();
                BarVm? master = null;
                foreach (var r in rows)
                {
                    var rowKey = KeyOf(items, r);
                    if (r.Kind != ItemKind.Slider || r.Child) continue;
                    var bar = new BarVm(r.Label, r.Level, r.IsOn, r.IsSelected);
                    if (rowKey == "audio.volume") master = bar;
                    else if (IsChannel(rowKey)) bars.Add(bar);
                }
                if (master is not null) bars.Add(master);
                var apps = new List<string>();
                if (IsChannel(key))
                    for (var i = at + 1; i < rows.Count && rows[i].Child; i++) apps.Add(rows[i].Label);
                return new ContextVm(ContextKind.Mixer, row.Label, description)
                {
                    Bars = bars, Apps = string.Join(", ", apps), Value = row.Value,
                };
            }
            if (tab == OverlayContent.HomeTabId && key == "audio.volume")
            {
                var cap = Sources.OutputCap();
                return new ContextVm(ContextKind.Volume, outputDevice.Length > 0 ? outputDevice : row.Label,
                    cap is { } percent ? Loc.T("Overlay.Context.Cap", percent) : description)
                {
                    Value = row.Value,
                    Glyph = outputDevice.Length > 0 ? OverlayContent.OutputIcon(outputDevice) : Glyphs.Volume,
                };
            }
            if (key == "audio.media")
                return new ContextVm(ContextKind.Media, row.Label, row.Value) { Art = art };

            if (description.Length == 0 && row.Value.Length == 0) return null;
            return new ContextVm(ContextKind.Text, row.Label, description) { Value = row.Value };
        }
        catch (Exception e)
        {
            Log.Error("Overlay stage context", e);
            return null;
        }
    }

    static bool IsChannel(string key) => key.StartsWith("ch.", StringComparison.Ordinal);

    static string KeyOf(IReadOnlyList<OverlayItem> items, RowVm row) =>
        row.Index >= 0 && row.Index < items.Count ? items[row.Index].Key ?? "" : "";

    /// <summary>Rail icon of a tab.</summary>
    static string RailGlyph(string tabId) => tabId switch
    {
        OverlayContent.HomeTabId => Glyphs.Home,
        OverlayContent.ControlsTabId => Glyphs.Controls,
        OverlayContent.AudioTabId => Glyphs.Volume,
        OverlayContent.GamesTabId => Glyphs.Game,
        _ => Glyphs.List,
    };

    void RefreshTiles(OverlayNavigator nav, IReadOnlyList<OverlayItem> items, bool pad)
    {
        var grid = items.FirstOrDefault(i => i.Kind == ItemKind.Grid && i.Visible());
        ShowTiles = grid is not null;
        if (grid?.Tiles?.Invoke() is not { } games)
        {
            UpdateAmbient(); // the tiles are kept while hidden (coming back to the tab is instant); the tint goes
            return;
        }
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
        Hero = selected >= 0 ? _tiles[selected] : null;
        UpdateDimmed();
        UpdateAmbient();
        RefreshPills();
        PlayKeys = pad ? "A" : "Enter";
        PlayLabel = Loc.T("Overlay.Footer.Play");
        GameCount = _tiles.Count.ToString(Loc.Culture);
    }

    /// <summary>The pills from <see cref="Sources"/>, reassigned only when a label, a source or the lit one changed.</summary>
    void RefreshPills()
    {
        try
        {
            var current = Sources.GameFilter();
            var filters = Sources.GameFilters();
            var same = filters.Count == _pills.Count;
            for (var i = 0; same && i < filters.Count; i++)
                same = _pills[i].Source == filters[i] && _pills[i].IsCurrent == (filters[i] == current);
            if (same && _pillsCulture == Loc.Culture.Name) return; // the usual case: a move on the shelf
            _pillsCulture = Loc.Culture.Name;
            Pills = filters.Select(s => new PillVm(s,
                s is { } source ? Loc.T("Games.Source." + source) : Loc.T("Overlay.Games.All"), s == current)).ToList();
        }
        catch (Exception e)
        {
            Log.Error("Overlay game filters", e);
        }
    }

    /// <summary>Tiles off the selected tile's shelf row are dimmed; none when no tile is selected.</summary>
    void UpdateDimmed()
    {
        var columns = TileColumns;
        var row = _selectedTile >= 0 ? _selectedTile / columns : -1;
        foreach (var tile in _tiles) tile.IsDimmed = row >= 0 && tile.Index / columns != row;
    }

    /// <summary>The tint follows the selected cover while the shelf is shown (its colour may arrive later).</summary>
    void UpdateAmbient() => Ambient = _showTiles ? _hero?.Ambient : null;

    /// <summary>
    /// The tiles from <paramref name="first"/> to <paramref name="last"/> are on screen or next to it (window, UI
    /// thread). Their art is set at once when decoded already, else decoded on a worker; tiles further than
    /// <see cref="ArtKeepRows"/> rows away drop theirs, so a big library holds only a few rows of images.
    /// </summary>
    public void ShowArt(int first, int last)
    {
        var keep = ArtKeepRows * TileColumns;
        foreach (var tile in _withArt.Where(t => t.Index < first - keep || t.Index > last + keep).ToList())
        {
            SetArt(tile, null, null);
            _withArt.Remove(tile);
        }
        var pending = new List<TileVm>();
        for (var i = Math.Max(0, first); i <= last && i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            if (_withArt.Contains(tile) || _requested.Contains(tile)) continue;
            if (GameArt.TryCached(tile.Game.ArtPath, tile.Game.IconPath, out var cover, out var icon))
            {
                SetArt(tile, cover, icon);
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
                    SetArt(tile, cover, icon);
                    _withArt.Add(tile);
                });
            }
        }, token);
    }

    /// <summary>Cover, ambient colour and icon of a tile; the backdrop tint follows when it is the selected one.</summary>
    void SetArt(TileVm tile, CoverArt? cover, ImageSource? icon)
    {
        tile.Cover = cover?.Image;
        tile.Ambient = cover?.Ambient;
        tile.Icon = icon;
        if (ReferenceEquals(tile, _hero)) UpdateAmbient();
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
