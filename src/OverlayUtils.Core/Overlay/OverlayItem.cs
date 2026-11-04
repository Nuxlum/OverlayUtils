using OverlayUtils.Core.Games;

namespace OverlayUtils.Core.Overlay;

public enum ItemKind { Action, Segmented, Choice, Toggle, Expander, Info, Slider, Media, Reorder, Grid }

public enum OverlayPage { Tabs, Settings, Info, Sub }

public enum OverlayCommand
{
    Up, Down, Left, Right, LeftRepeat, RightRepeat, A, B, PrevTab, NextTab, Settings, WheelUp, WheelDown,
    PrevTrack, NextTrack, PlayPause, Remove,
}

/// <summary>What the view must do after a command. The navigator never runs item callbacks itself.</summary>
public enum NavEffect { None, Redraw, Hide, RunHere, HideAndRun }

public readonly record struct NavResult(NavEffect Effect, Action? Action = null);

/// <summary>One button of a segmented row.</summary>
public sealed record OverlayOption(Func<string> Label, Action Action, bool Stay = false, Func<bool>? Active = null);

public sealed record OverlayTab(string Id, Func<string> Title, Func<IReadOnlyList<OverlayItem>> Items, bool Tiles = false);

/// <summary>A footer hint; <paramref name="Command"/> is what clicking it does (null: not clickable).</summary>
public sealed record FooterHint(string Keys, string Label, OverlayCommand? Command = null);

/// <summary>A row of the overlay. Which properties matter depends on <see cref="Kind"/>.</summary>
public sealed class OverlayItem
{
    public required ItemKind Kind { get; init; }
    public required Func<string> Label { get; init; }
    public string Icon { get; init; } = "";
    public Func<string>? Section { get; init; }          // header drawn above this row
    public Func<string> Description { get; init; } = () => "";
    public Func<bool> Visible { get; init; } = () => true;
    public Func<IReadOnlyList<OverlayOption>> Options { get; init; } = () => [];  // Segmented
    public Func<string> Value { get; init; } = () => "";                         // Choice, Info
    public Action<int>? Step { get; init; }              // Choice, Slider; Reorder: Left/Right when not held
    public Action<double>? SetLevel { get; init; }       // Slider: level 0..1 (click on the bar)
    public Action? Apply { get; init; }                  // Choice: A runs this after hiding
    public Func<bool> IsOn { get; init; } = () => false; // Toggle, Expander
    public Action? Toggle { get; init; }                 // Toggle, Expander
    public Action? Action { get; init; }                 // Action, Info (Info without Action is not selectable)
    public bool Stay { get; init; }                      // Action/Info: run with the overlay open
    public bool Confirm { get; init; }                   // Segmented: second A within 3 s
    public bool OpensInfo { get; init; }                 // Action: A opens the Info page
    public Func<string>? Hint { get; init; }             // keycap or button text on the right
    public string? Key { get; init; }                    // identity that survives list changes (Refresh)
    public Func<IReadOnlyList<OverlayItem>>? SubPage { get; init; } // Action: A opens these rows as OverlayPage.Sub
    public Action<int>? Move { get; init; }              // Reorder: move this entry by +-1 in its list
    public Action? Commit { get; init; }                 // Reorder: drop the picked-up entry
    public Action? Cancel { get; init; }                 // Reorder: put the picked-up entry back
    public Action? RemoveEntry { get; init; }            // any row: the Remove command (Y on the pad)
    public bool ConfirmRemove { get; init; }             // Remove: second Y / Delete within 3 s (destructive deletes)
    public bool Child { get; init; }                     // drawn indented under the row above (an app under its channel)
    public Func<string>? ActionLabel { get; init; }      // footer label of A when the kind's default does not fit
    public Func<IReadOnlyList<GameEntry>>? Tiles { get; init; } // Grid: the tiles, in display order
    public int Columns { get; init; } = 3;               // Grid
    public Action<GameEntry>? Launch { get; init; }      // Grid: A on a tile (runs after hiding)
    public Action? AltAction { get; init; }              // the Remove command (Y / Delete) does this instead, when set
    public Func<string>? AltLabel { get; init; }         // footer label of AltAction
}
