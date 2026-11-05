using OverlayUtils.Core.Audio;
using OverlayUtils.Core.Games;
using OverlayUtils.Core.Localization;

namespace OverlayUtils.Core.Overlay;

/// <summary>
/// Selection and paging state machine of the overlay (Python _handle/_move/_confirm). It only reads item
/// state; every side effect is returned as <see cref="NavResult.Action"/> for the view to run.
/// </summary>
public sealed class OverlayNavigator
{
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(3);

    const string ControlsTabId = "Controlli";

    static readonly NavResult None = new(NavEffect.None);
    static readonly NavResult Redraw = new(NavEffect.Redraw);

    readonly IReadOnlyList<OverlayTab> _tabs;
    readonly Func<IReadOnlyList<OverlayItem>> _settings;
    readonly Func<IReadOnlyList<OverlayItem>> _info;
    readonly TimeProvider _time;
    readonly IMediaControl? _media;

    IReadOnlyCollection<string> _hidden = [];
    int _tabIndex;
    int _savedIndex;
    int _savedSegment;
    (int Index, int Segment, DateTimeOffset Deadline)? _pending;
    (int Index, DateTimeOffset Deadline)? _removePending;
    string? _selectedKey;
    bool _holding;
    Action? _holdCancel;
    OverlayItem? _subItem;
    OverlayPage _subOrigin;
    int _subIndex;
    int _subSegment;
    int _tile;          // Grid row: the selected tile
    string? _tileId;    // its id, so a refresh keeps the same game selected
    // Grid key -> selected game id, so coming back from Settings or another tab finds the same tile (cleared on open).
    readonly Dictionary<string, string> _gridTiles = [];

    public OverlayNavigator(IReadOnlyList<OverlayTab> tabs, Func<IReadOnlyList<OverlayItem>> settings,
        Func<IReadOnlyList<OverlayItem>> info, TimeProvider? time = null, IMediaControl? media = null)
    {
        _tabs = tabs;
        _settings = settings;
        _info = info;
        _time = time ?? TimeProvider.System;
        _media = media;
    }

    public OverlayPage Page { get; private set; }
    public int Index { get; private set; }
    public int Segment { get; private set; }

    /// <summary>A Reorder row is picked up: Up/Down move it, A drops it, B puts it back.</summary>
    public bool Holding => _holding;

    /// <summary>Title of the sub-page (the label of the row that opened it).</summary>
    public string SubTitle => _subItem?.Label() ?? "";

    /// <summary>The page a sub-page was opened from (meaningful while <see cref="Page"/> is Sub).</summary>
    public OverlayPage SubOrigin => _subOrigin;

    public IReadOnlyList<OverlayTab> VisibleTabs =>
        _tabs.Where(t => t.Id == ControlsTabId || !_hidden.Contains(t.Id)).ToList();

    public OverlayTab CurrentTab
    {
        get
        {
            var tabs = VisibleTabs;
            return tabs[Math.Clamp(_tabIndex, 0, tabs.Count - 1)];
        }
    }

    public IReadOnlyList<OverlayItem> Items => Page switch
    {
        OverlayPage.Settings => _settings(),
        OverlayPage.Info => _info(),
        OverlayPage.Sub => _subItem?.SubPage?.Invoke() ?? [],
        _ => CurrentTab.Items(),
    };

    public OverlayItem? Selected
    {
        get
        {
            var items = Items;
            return Index >= 0 && Index < items.Count && IsSelectable(items[Index]) ? items[Index] : null;
        }
    }

    /// <summary>The selected tile of the selected Grid row (null when it is not a grid or it is empty).</summary>
    public GameEntry? SelectedTile
    {
        get
        {
            var tiles = Tiles();
            return tiles.Count == 0 ? null : tiles[Math.Clamp(_tile, 0, tiles.Count - 1)];
        }
    }

    /// <summary>Index of <see cref="SelectedTile"/> in the grid's tiles (0 when there is none).</summary>
    public int SelectedTileIndex => Math.Clamp(_tile, 0, Math.Max(Tiles().Count - 1, 0));

    public bool IsPending(int index, int segment) => _pending is { } p && p.Index == index && p.Segment == segment;

    /// <summary>A Remove on a <see cref="OverlayItem.ConfirmRemove"/> row is armed: the next one within 3 s deletes.</summary>
    public bool IsRemovePending(int index) => _removePending is { } p && p.Index == index;

    /// <summary>
    /// Back to the first tab. Returns the Cancel of a picked-up Reorder row (or null); the caller runs it
    /// so the list is restored.
    /// </summary>
    public Action? Reset(IReadOnlyCollection<string> hiddenTabIds)
    {
        var cancel = TakeHold();
        _hidden = hiddenTabIds;
        Page = OverlayPage.Tabs;
        _tabIndex = 0;
        _savedIndex = 0;
        _savedSegment = 0;
        _subItem = null;
        _tile = 0;
        _tileId = null;
        _gridTiles.Clear();
        SelectFirst();
        return cancel;
    }

    /// <summary>
    /// The hidden tabs changed while open (Settings → Tabs): the tab strip follows at once, page and selection stay.
    /// If the current tab was hidden, the nearest visible one becomes current (its first row when the tabs are
    /// shown, the first row on return from Settings). Returns the Cancel of a picked-up Reorder row that was on the
    /// hidden tab (or null), for the caller to run.
    /// </summary>
    public Action? SetHiddenTabs(IReadOnlyCollection<string> hiddenTabIds)
    {
        var currentId = CurrentTab.Id;
        _hidden = hiddenTabIds;
        var tabs = VisibleTabs;
        var index = tabs.ToList().FindIndex(t => t.Id == currentId);
        if (index >= 0)
        {
            _tabIndex = index;
            return null;
        }
        _tabIndex = Math.Clamp(_tabIndex, 0, tabs.Count - 1);
        _savedIndex = 0;
        _savedSegment = 0;
        if (Page != OverlayPage.Tabs) return null;
        var cancel = TakeHold();
        SelectFirst();
        return cancel;
    }

    /// <summary>
    /// Re-validates the selection after the lists changed. If the picked-up Reorder entry is gone the hold is
    /// dropped and its Cancel is returned for the caller to run (null otherwise).
    /// </summary>
    public Action? Refresh()
    {
        var cancel = RefreshRows();
        SyncTile();
        return cancel;
    }

    Action? RefreshRows()
    {
        var items = Items;
        if (_selectedKey != null)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Key != _selectedKey || !IsSelectable(items[i])) continue;
                if (i != Index) Select(i);
                return null;
            }
        }
        var cancel = _selectedKey != null ? TakeHold() : null; // keyed entry is gone
        if (Index >= 0 && Index < items.Count && IsSelectable(items[Index]))
        {
            _selectedKey = items[Index].Key; // drop a stale key so later Refreshes do not misread it
            return cancel;
        }

        var from = Math.Clamp(Index, 0, Math.Max(items.Count - 1, 0));
        for (var i = from; i >= 0; i--)
            if (i < items.Count && IsSelectable(items[i])) { Select(i); return cancel; }
        for (var i = from + 1; i < items.Count; i++)
            if (IsSelectable(items[i])) { Select(i); return cancel; }
        Select(0);
        return cancel;
    }

    public NavResult Handle(OverlayCommand command)
    {
        if (command != OverlayCommand.Remove) _removePending = null; // any other command disarms a pending delete
        if (_holding) return HandleHolding(command);
        if (Selected?.Kind == ItemKind.Grid && GridCommand(command) is { } grid) return grid;
        switch (command)
        {
            case OverlayCommand.Up: return Move(-1);
            case OverlayCommand.Down: return Move(1);
            case OverlayCommand.WheelUp: return Page == OverlayPage.Tabs ? None : Move(-1);
            case OverlayCommand.WheelDown: return Page == OverlayPage.Tabs ? None : Move(1);
            case OverlayCommand.Left: return Horizontal(-1);
            case OverlayCommand.Right: return Horizontal(1);
            case OverlayCommand.A: return Activate();
            case OverlayCommand.B: return Back();
            case OverlayCommand.Settings:
                if (Page == OverlayPage.Tabs) OpenPage(OverlayPage.Settings);
                else ReturnToTabs();
                return Redraw;
            case OverlayCommand.PrevTab: return ChangeTab(-1);
            case OverlayCommand.NextTab: return ChangeTab(1);
            case OverlayCommand.LeftRepeat:
                return Selected?.Kind == ItemKind.Slider ? Horizontal(-1) : None;
            case OverlayCommand.RightRepeat:
                return Selected?.Kind == ItemKind.Slider ? Horizontal(1) : None;
            case OverlayCommand.PrevTrack: return Media(_media is { } prev ? prev.Previous : null);
            case OverlayCommand.NextTrack: return Media(_media is { } next ? next.Next : null);
            case OverlayCommand.PlayPause: return Media(_media is { } play ? play.PlayPause : null);
            case OverlayCommand.Remove: return Remove();
            default: return None;
        }
    }

    /// <summary>Runs the row's RemoveEntry; a <see cref="OverlayItem.ConfirmRemove"/> row needs a second press within 3 s.</summary>
    NavResult Remove()
    {
        if (Selected?.AltAction is { } alt) return ApplyFilter(alt);
        if (Selected is not { RemoveEntry: { } remove } item) return None;
        if (item.ConfirmRemove)
        {
            var now = _time.GetUtcNow();
            if (!(_removePending is { } p && p.Index == Index && now <= p.Deadline))
            {
                _removePending = (Index, now + ConfirmTimeout);
                return Redraw;
            }
            _removePending = null;
        }
        return new NavResult(NavEffect.RunHere, remove);
    }

    /// <summary>
    /// Runs a change of the grid's filter (Y, or a source pill), then keeps the selected game if it is still shown, else
    /// starts on the first tile. Nothing while a list entry is held.
    /// </summary>
    public NavResult ApplyFilter(Action change)
    {
        if (_holding) return None;
        return new NavResult(NavEffect.RunHere, () =>
        {
            change();
            var tiles = Tiles();
            if (tiles.Count == 0) return;
            var index = IndexOfTile(_tileId);
            SetTile(index >= 0 ? index : 0, tiles);
        });
    }

    static NavResult Media(Action? action) => action == null ? None : new NavResult(NavEffect.RunHere, action);

    /// <summary>Commands while a Reorder row is picked up: move, drop, cancel; everything else is ignored.</summary>
    NavResult HandleHolding(OverlayCommand command)
    {
        switch (command)
        {
            case OverlayCommand.Up: return MoveHeld(-1);
            case OverlayCommand.Down: return MoveHeld(1);
            case OverlayCommand.A:
            {
                var commit = Selected?.Commit;
                _holding = false;
                _holdCancel = null;
                return commit == null ? Redraw : new NavResult(NavEffect.RunHere, commit);
            }
            case OverlayCommand.B:
                return Cancelled(TakeHold());
            case OverlayCommand.Settings:
            {
                var cancel = TakeHold();
                if (Page == OverlayPage.Tabs) OpenPage(OverlayPage.Settings);
                else ReturnToTabs();
                return Cancelled(cancel);
            }
            case OverlayCommand.PrevTab:
            case OverlayCommand.NextTab:
            {
                if (Page != OverlayPage.Tabs) return None;
                var cancel = TakeHold();
                ChangeTab(command == OverlayCommand.PrevTab ? -1 : 1);
                return Cancelled(cancel);
            }
            default: return None;
        }
    }

    static NavResult Cancelled(Action? cancel) => cancel == null ? Redraw : new NavResult(NavEffect.RunHere, cancel);

    Action? TakeHold()
    {
        if (!_holding) return null;
        var cancel = _holdCancel;
        _holding = false;
        _holdCancel = null;
        return cancel;
    }

    NavResult MoveHeld(int direction)
    {
        var items = Items;
        var target = Index + direction;
        if (target < 0 || target >= items.Count || items[target].Kind != ItemKind.Reorder || !items[target].Visible()) return None;
        if (Selected?.Move is not { } move) return None;
        Index = target; // the entry moves with the selection; _selectedKey still names it
        return new NavResult(NavEffect.RunHere, () => move(direction));
    }

    public NavResult Click(int index, int? segment = null)
    {
        if (_holding) return None;
        _removePending = null;
        var items = Items;
        if (index < 0 || index >= items.Count || !IsSelectable(items[index])) return None;
        // Re-selecting the current row would clear a pending confirm, so a second click could never fire it.
        if (index != Index) Select(index);
        if (segment is { } s && items[index].Kind == ItemKind.Segmented)
        {
            s = Math.Clamp(s, 0, Math.Max(items[index].Options().Count - 1, 0));
            if (s != Segment)
            {
                Segment = s;
                _pending = null;
            }
        }
        return Activate();
    }

    // --- grid -------------------------------------------------------------------

    IReadOnlyList<GameEntry> Tiles() => Selected is { Kind: ItemKind.Grid, Tiles: { } tiles } ? tiles() : [];

    /// <summary>Python _CoverGrid.move: Left/Right step through the tiles (wrapping to the next/previous row),
    /// Up/Down keep the column (clamped on a short last row); the edges stay.</summary>
    NavResult? GridCommand(OverlayCommand command) => command switch
    {
        OverlayCommand.Left or OverlayCommand.LeftRepeat => MoveTile(-1, 0),
        OverlayCommand.Right or OverlayCommand.RightRepeat => MoveTile(1, 0),
        OverlayCommand.Up or OverlayCommand.WheelUp => MoveTile(0, -1),
        OverlayCommand.Down or OverlayCommand.WheelDown => MoveTile(0, 1),
        _ => null,
    };

    NavResult MoveTile(int dx, int dy)
    {
        var tiles = Tiles();
        if (tiles.Count == 0) return None;
        var columns = Math.Max(1, Selected!.Columns);
        var current = Math.Clamp(_tile, 0, tiles.Count - 1);
        int target;
        if (dx != 0)
        {
            target = Math.Clamp(current + dx, 0, tiles.Count - 1);
        }
        else
        {
            var row = current / columns + dy;
            if (row < 0 || row > (tiles.Count - 1) / columns) return None;
            target = Math.Min(row * columns + current % columns, tiles.Count - 1);
        }
        if (target == current) return None;
        SetTile(target, tiles);
        return Redraw;
    }

    void SetTile(int index, IReadOnlyList<GameEntry> tiles)
    {
        _tile = index;
        _tileId = tiles[index].Id;
    }

    /// <summary>Index of the game with this id in the selected grid's tiles, -1 if it is not there.</summary>
    public int IndexOfTile(string? id)
    {
        if (id is null) return -1;
        var tiles = Tiles();
        for (var i = 0; i < tiles.Count; i++)
            if (tiles[i].Id == id) return i;
        return -1;
    }

    /// <summary>After the tiles changed: the same game stays selected; if it is gone, the tile now at its place.</summary>
    void SyncTile()
    {
        var tiles = Tiles();
        if (tiles.Count == 0) return; // keep the id: the game comes back selected if the list refills
        var index = IndexOfTile(_tileId);
        SetTile(index >= 0 ? index : Math.Clamp(_tile, 0, tiles.Count - 1), tiles);
    }

    NavResult LaunchTile()
    {
        SyncTile(); // the list may have changed since the last redraw: launch the game that was shown, by id
        if (Selected?.Launch is not { } launch || SelectedTile is not { } tile) return None;
        return new NavResult(NavEffect.HideAndRun, () => launch(tile));
    }

    /// <summary>Selects the page's grid row if it is not selected (changed: the row selection moved).</summary>
    bool SelectGrid(out bool changed)
    {
        changed = false;
        if (Selected?.Kind == ItemKind.Grid) return true;
        var items = Items;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Kind != ItemKind.Grid || !IsSelectable(items[i])) continue;
            Select(i);
            changed = true;
            return true;
        }
        return false;
    }

    /// <summary>Mouse over a tile: it becomes the selection (true if that changed).</summary>
    public bool HoverTile(int tile)
    {
        if (_holding || !SelectGrid(out var changed)) return false;
        var tiles = Tiles();
        if (tile < 0 || tile >= tiles.Count) return changed;
        if (tile == _tile && _tileId == tiles[tile].Id) return changed;
        SetTile(tile, tiles);
        return true;
    }

    /// <summary>Click on a tile: select it and launch it.</summary>
    public NavResult ClickTile(int tile)
    {
        if (_holding || !SelectGrid(out _)) return None;
        var tiles = Tiles();
        if (tile < 0 || tile >= tiles.Count) return None;
        _removePending = null;
        SetTile(tile, tiles);
        return LaunchTile();
    }

    // --- mouse ------------------------------------------------------------------

    /// <summary>Mouse over a row: it becomes the selection (true if that changed). Ignored while a row is held.</summary>
    public bool Hover(int index)
    {
        var items = Items;
        if (_holding || index == Index || index < 0 || index >= items.Count || !IsSelectable(items[index])) return false;
        Select(index);
        return true;
    }

    /// <summary>Click on a tab header (index into <see cref="VisibleTabs"/>); from a page it goes back to that tab.</summary>
    public NavResult ClickTab(int visibleIndex)
    {
        if (_holding || visibleIndex < 0 || visibleIndex >= VisibleTabs.Count) return None;
        if (Page != OverlayPage.Tabs) Page = OverlayPage.Tabs;
        else if (visibleIndex == _tabIndex) return None;
        _tabIndex = visibleIndex;
        SelectFirst();
        return Redraw;
    }

    /// <summary>Click on a picker arrow: select the row, then as Left (-1) or Right (+1).</summary>
    public NavResult ClickStep(int index, int direction) =>
        SelectForClick(index) ? Horizontal(Math.Sign(direction)) : None;

    /// <summary>Click or drag on a slider bar at <paramref name="fraction"/> of its width.</summary>
    public NavResult ClickLevel(int index, double fraction)
    {
        if (!SelectForClick(index) || Items[index].SetLevel is not { } set) return None;
        var level = Math.Clamp(fraction, 0.0, 1.0);
        return new NavResult(NavEffect.RunHere, () => set(level));
    }

    /// <summary>Click on a media button: -1 previous, 0 play/pause, +1 next.</summary>
    public NavResult ClickMedia(int index, int button) =>
        !SelectForClick(index) || Items[index].Kind != ItemKind.Media ? None
        : Media(_media is { } m ? button < 0 ? m.Previous : button > 0 ? m.Next : m.PlayPause : null);

    bool SelectForClick(int index)
    {
        var items = Items;
        if (_holding || index < 0 || index >= items.Count || !IsSelectable(items[index])) return false;
        _removePending = null;
        if (index != Index) Select(index);
        return true;
    }

    /// <summary>Drops a segment confirm or a pending delete whose 3 s are over (true if one was dropped).</summary>
    public bool ExpirePending()
    {
        var now = _time.GetUtcNow();
        var expired = false;
        if (_pending is { } p && now > p.Deadline)
        {
            _pending = null;
            expired = true;
        }
        if (_removePending is { } r && now > r.Deadline)
        {
            _removePending = null;
            expired = true;
        }
        return expired;
    }

    /// <summary>A segment confirm or a delete is waiting for its second press.</summary>
    public bool HasPending => _pending is not null || _removePending is not null;

    public string Description() => Selected?.Description() ?? "";

    /// <summary>Key hints for the selected row; <paramref name="pad"/> shows pad buttons instead of keys.</summary>
    public IReadOnlyList<FooterHint> Footer(bool pad = false)
    {
        var hints = new List<FooterHint>();
        if (_holding)
        {
            hints.Add(new FooterHint(pad ? "A" : "Enter", Loc.T("Overlay.Footer.Drop"), OverlayCommand.A));
            hints.Add(new FooterHint("↑ ↓", Loc.T("Overlay.Footer.Move")));
            hints.Add(new FooterHint(pad ? "B" : "Esc", Loc.T("Overlay.Footer.Cancel"), OverlayCommand.B));
            return hints;
        }
        var item = Selected;
        if (item is not null)
        {
            hints.Add(new FooterHint(pad ? "A" : "Enter", item.ActionLabel?.Invoke() ?? Loc.T(item.Kind switch
            {
                ItemKind.Choice when item.Apply == null && item.Toggle != null => "Overlay.Footer.Toggle",
                ItemKind.Choice when item.Apply == null => "Overlay.Footer.Change",
                ItemKind.Slider => "Overlay.Footer.Mute",
                ItemKind.Media => "Overlay.Footer.PlayPause",
                ItemKind.Reorder => "Overlay.Footer.PickUp",
                ItemKind.Toggle => "Overlay.Footer.Toggle",
                ItemKind.Expander => "Overlay.Footer.Open",
                ItemKind.Info => "Overlay.Footer.Select",
                ItemKind.Grid => "Overlay.Footer.Play",
                _ => "Overlay.Footer.Confirm",
            }), OverlayCommand.A));
            if (item.Kind is ItemKind.Segmented or ItemKind.Choice)
                hints.Add(new FooterHint("← →", Loc.T("Overlay.Footer.Change")));
            else if (item.Kind == ItemKind.Slider)
                hints.Add(new FooterHint("← →", item.StepLabel?.Invoke() ?? Loc.T("Overlay.Footer.Volume")));
            else if (item.Kind == ItemKind.Media)
                hints.Add(new FooterHint("← →", Loc.T("Overlay.Footer.Track")));
            else if (item.Kind == ItemKind.Reorder && item.Step != null)
                hints.Add(new FooterHint("← →", Loc.T("Overlay.Footer.Icon")));
            else if (item.Kind == ItemKind.Expander)
                hints.Add(new FooterHint("← →", Loc.T("Overlay.Footer.OpenClose")));
            if (item.AltAction != null)
                hints.Add(new FooterHint(pad ? "Y" : "Del", item.AltLabel?.Invoke() ?? "", OverlayCommand.Remove));
            if (IsRemovePending(Index))
                hints.Add(new FooterHint(pad ? "Y" : "Del", Loc.T("Overlay.Footer.ConfirmDelete"), OverlayCommand.Remove));
        }
        if (Page == OverlayPage.Tabs)
        {
            hints.Add(new FooterHint(pad ? "B" : "Esc", Loc.T("Overlay.Footer.Close"), OverlayCommand.B));
            hints.Add(pad
                ? new FooterHint("Back", Loc.T("Overlay.Footer.SettingsHold"), OverlayCommand.Settings)
                : new FooterHint("F2", Loc.T("Overlay.Footer.Settings"), OverlayCommand.Settings));
        }
        else
        {
            hints.Add(new FooterHint(pad ? "B" : "Esc", Loc.T("Overlay.Footer.Back"), OverlayCommand.B));
        }
        return hints;
    }

    static bool IsSelectable(OverlayItem item) =>
        item.Visible() && !(item.Kind == ItemKind.Info && item.Action == null);

    static NavResult Run(Action action, bool stay) =>
        new(stay ? NavEffect.RunHere : NavEffect.HideAndRun, action);

    /// <summary>Select a row: clears the pending confirm, puts the segment cursor on the active option.</summary>
    void Select(int index)
    {
        var items = Items;
        var key = index >= 0 && index < items.Count ? items[index].Key : null;
        if (key == null || key != _selectedKey)
        {
            if (_selectedKey != null && _tileId != null) _gridTiles[_selectedKey] = _tileId; // leaving a grid
            _tile = 0; // another row: a grid starts on its first tile, or on the one it had before
            _tileId = key != null && _gridTiles.TryGetValue(key, out var remembered) ? remembered : null;
        }
        Index = index;
        _pending = null;
        _removePending = null;
        Segment = 0;
        _selectedKey = index >= 0 && index < items.Count ? items[index].Key : null;
        if (index >= 0 && index < items.Count && items[index].Kind == ItemKind.Grid) SyncTile();
        if (index < 0 || index >= items.Count || items[index].Kind != ItemKind.Segmented) return;
        var options = items[index].Options();
        for (var i = 0; i < options.Count; i++)
            if (options[i].Active?.Invoke() == true) { Segment = i; return; }
    }

    void SelectFirst()
    {
        var items = Items;
        for (var i = 0; i < items.Count; i++)
            if (IsSelectable(items[i])) { Select(i); return; }
        Select(0);
    }

    NavResult Move(int direction)
    {
        var items = Items;
        var count = items.Count;
        for (var step = 1; step <= count; step++)
        {
            var i = (((Index + direction * step) % count) + count) % count;
            if (!IsSelectable(items[i])) continue;
            Select(i);
            return Redraw;
        }
        return None;
    }

    NavResult Horizontal(int direction)
    {
        var item = Selected;
        if (item is null) return None;
        switch (item.Kind)
        {
            case ItemKind.Segmented:
                Segment = Math.Clamp(Segment + direction, 0, Math.Max(item.Options().Count - 1, 0));
                _pending = null;
                return Redraw;
            case ItemKind.Choice:
            case ItemKind.Slider:
                return item.Step is { } step ? new NavResult(NavEffect.RunHere, () => step(direction)) : None;
            case ItemKind.Media:
                return Media(_media is { } m ? (direction < 0 ? m.Previous : m.Next) : null);
            case ItemKind.Reorder:
                // Not while held (HandleHolding ignores Left/Right): e.g. the icon of a channel.
                return item.Step is { } change ? new NavResult(NavEffect.RunHere, () => change(direction)) : None;
            case ItemKind.Toggle:
                return item.Toggle is { } toggle ? new NavResult(NavEffect.RunHere, toggle) : None;
            case ItemKind.Expander:
                // Left closes an open section, Right opens a closed one.
                if (item.Toggle is { } expand && item.IsOn() == (direction < 0))
                    return new NavResult(NavEffect.RunHere, expand);
                return None;
            default:
                return None;
        }
    }

    NavResult Activate()
    {
        var item = Selected;
        if (item is null) return None;
        switch (item.Kind)
        {
            case ItemKind.Choice:
                if (item.Apply is { } apply) return new NavResult(NavEffect.HideAndRun, apply);
                if (item.Toggle is { } choiceToggle) return new NavResult(NavEffect.RunHere, choiceToggle);
                return Horizontal(1);
            case ItemKind.Slider:
                return item.Toggle is { } mute ? new NavResult(NavEffect.RunHere, mute) : None;
            case ItemKind.Media:
                return Media(_media is { } m ? m.PlayPause : null);
            case ItemKind.Reorder:
                _holding = true;
                _holdCancel = item.Cancel;
                return Redraw;
            case ItemKind.Action when item.SubPage != null:
                if (Page == OverlayPage.Sub) return None;
                _subItem = item;
                _subOrigin = Page;
                _subIndex = Index;
                _subSegment = Segment;
                OpenPage(OverlayPage.Sub);
                return Redraw;
            case ItemKind.Toggle:
            case ItemKind.Expander:
                return item.Toggle is { } toggle ? new NavResult(NavEffect.RunHere, toggle) : None;
            case ItemKind.Segmented:
                return ActivateSegment(item);
            case ItemKind.Grid:
                return LaunchTile();
            case ItemKind.Action when item.OpensInfo:
                OpenPage(OverlayPage.Info);
                return Redraw;
            default:
                return item.Action is { } action ? Run(action, item.Stay) : None;
        }
    }

    NavResult ActivateSegment(OverlayItem item)
    {
        var options = item.Options();
        if (Segment < 0 || Segment >= options.Count) return None;
        var option = options[Segment];
        if (item.Confirm)
        {
            var now = _time.GetUtcNow();
            if (!(_pending is { } p && p.Index == Index && p.Segment == Segment && now <= p.Deadline))
            {
                _pending = (Index, Segment, now + ConfirmTimeout);
                return Redraw;
            }
            _pending = null;
        }
        return Run(option.Action, option.Stay);
    }

    NavResult Back()
    {
        switch (Page)
        {
            case OverlayPage.Info:
                OpenPage(OverlayPage.Settings);
                return Redraw;
            case OverlayPage.Settings:
                ReturnToTabs();
                return Redraw;
            case OverlayPage.Sub:
                Page = _subOrigin;
                Select(_subIndex);
                Refresh();
                if (Index == _subIndex) Segment = _subSegment;
                return Redraw;
            default:
                return new NavResult(NavEffect.Hide);
        }
    }

    NavResult ChangeTab(int direction)
    {
        if (Page != OverlayPage.Tabs) return None;
        var count = VisibleTabs.Count;
        _tabIndex = (((_tabIndex + direction) % count) + count) % count;
        SelectFirst();
        return Redraw;
    }

    void OpenPage(OverlayPage page)
    {
        if (Page == OverlayPage.Tabs)
        {
            _savedIndex = Index;
            _savedSegment = Segment;
        }
        Page = page;
        SelectFirst();
    }

    void ReturnToTabs()
    {
        Page = OverlayPage.Tabs;
        Select(_savedIndex);
        Refresh();
        if (Index == _savedIndex) Segment = _savedSegment;
    }
}
