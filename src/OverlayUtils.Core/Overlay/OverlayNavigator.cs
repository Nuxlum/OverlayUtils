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

    IReadOnlyCollection<string> _hidden = [];
    int _tabIndex;
    int _savedIndex;
    int _savedSegment;
    (int Index, int Segment, DateTimeOffset Deadline)? _pending;

    public OverlayNavigator(IReadOnlyList<OverlayTab> tabs, Func<IReadOnlyList<OverlayItem>> settings,
        Func<IReadOnlyList<OverlayItem>> info, TimeProvider? time = null)
    {
        _tabs = tabs;
        _settings = settings;
        _info = info;
        _time = time ?? TimeProvider.System;
    }

    public OverlayPage Page { get; private set; }
    public int Index { get; private set; }
    public int Segment { get; private set; }

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

    public bool IsPending(int index, int segment) => _pending is { } p && p.Index == index && p.Segment == segment;

    public void Reset(IReadOnlyCollection<string> hiddenTabIds)
    {
        _hidden = hiddenTabIds;
        Page = OverlayPage.Tabs;
        _tabIndex = 0;
        _savedIndex = 0;
        _savedSegment = 0;
        SelectFirst();
    }

    public void Refresh()
    {
        var items = Items;
        if (Index >= 0 && Index < items.Count && IsSelectable(items[Index])) return;

        var from = Math.Clamp(Index, 0, Math.Max(items.Count - 1, 0));
        for (var i = from; i >= 0; i--)
            if (i < items.Count && IsSelectable(items[i])) { Select(i); return; }
        for (var i = from + 1; i < items.Count; i++)
            if (IsSelectable(items[i])) { Select(i); return; }
        Select(0);
    }

    public NavResult Handle(OverlayCommand command)
    {
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
            default: return None; // LeftRepeat/RightRepeat: only sliders use them
        }
    }

    public NavResult Click(int index, int? segment = null)
    {
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

    public bool ExpirePending()
    {
        if (_pending is not { } p || _time.GetUtcNow() <= p.Deadline) return false;
        _pending = null;
        return true;
    }

    public string Description() => Selected?.Description() ?? "";

    /// <summary>Key hints for the selected row; <paramref name="pad"/> shows pad buttons instead of keys.</summary>
    public IReadOnlyList<FooterHint> Footer(bool pad = false)
    {
        var hints = new List<FooterHint>();
        var item = Selected;
        if (item is not null)
        {
            hints.Add(new FooterHint(pad ? "A" : "Enter", Loc.T(item.Kind switch
            {
                ItemKind.Choice when item.Apply == null => "Overlay.Footer.Change",
                ItemKind.Toggle => "Overlay.Footer.Toggle",
                ItemKind.Expander => "Overlay.Footer.Open",
                ItemKind.Info => "Overlay.Footer.Select",
                _ => "Overlay.Footer.Confirm",
            })));
            if (item.Kind is ItemKind.Segmented or ItemKind.Choice)
                hints.Add(new FooterHint("← →", Loc.T("Overlay.Footer.Change")));
            else if (item.Kind == ItemKind.Expander)
                hints.Add(new FooterHint("← →", Loc.T("Overlay.Footer.OpenClose")));
        }
        if (Page == OverlayPage.Tabs)
        {
            hints.Add(new FooterHint(pad ? "B" : "Esc", Loc.T("Overlay.Footer.Close")));
            hints.Add(pad
                ? new FooterHint("Back", Loc.T("Overlay.Footer.SettingsHold"))
                : new FooterHint("F2", Loc.T("Overlay.Footer.Settings")));
        }
        else
        {
            hints.Add(new FooterHint(pad ? "B" : "Esc", Loc.T("Overlay.Footer.Back")));
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
        Index = index;
        _pending = null;
        Segment = 0;
        var items = Items;
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
                return item.Step is { } step ? new NavResult(NavEffect.RunHere, () => step(direction)) : None;
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
                return Horizontal(1);
            case ItemKind.Toggle:
            case ItemKind.Expander:
                return item.Toggle is { } toggle ? new NavResult(NavEffect.RunHere, toggle) : None;
            case ItemKind.Segmented:
                return ActivateSegment(item);
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
