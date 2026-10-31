# Overlay Shell (Stage 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A WPF overlay window driven by keyboard and mouse, with a Controls tab, Settings and Info pages, opened with Shift+F1 or a tray click.

**Architecture:** Everything without WPF lives in Core so the self-test reaches it: the row model, the navigator (selection, segments, confirm, pages, footer), the content builders (which rows exist and what they do), the size and placement maths, and the Win32 focus/monitor helpers. The App adds one WPF window that renders a view model built from the navigator, an `OverlayController` that shows, hides, places and feeds commands to it, and wiring in `TrayController`.

**Tech Stack:** C# / .NET 10, WPF (App), no NuGet packages, the dependency-free self-test in `tests/OverlayUtils.SelfTest/Program.cs`.

**Spec:** `docs/superpowers/specs/2026-10-31-overlay-shell-design.md` (read it first; this plan does not repeat its reasoning). Python reference: `reference/python/app.py`, section `OVERLAY GAMEPAD`; line numbers in `docs/PYTHON-CODEMAP.md`.

## Global Constraints

- Follow `CLAUDE.md`: UI work on the UI thread, hotkey callbacks arrive on the hotkey thread, blocking work in `Task.Run`, `Log.Error(context, e)` instead of throwing from any handler or action, Win32 declarations only in `src/OverlayUtils.Core/Interop/Native.cs` (`LibraryImport`, blittable structs).
- Every user-facing string through `Loc.T("Key")` **in C#**, with the key in both `Strings.resx` and `Strings.it.resx`. XAML binds to view-model strings and contains no keys, so the self-test's key scan (literal keys in `src/**/*.cs`, prefixes `Overlay.` and `Settings.` already included) covers everything.
- Config changes only through `ConfigStore.Update`; new keys `overlay_size` (`normal|large|auto`, default `auto`) and `overlay_side` (`left|right`, default `left`).
- `hidden_tabs` stores the Python tab names; the Controls tab id is `"Controlli"` and it is always visible.
- Python measurements at 96 dpi: width 420, height 720, margin 36, confirm timeout 3 s, action delay 150 ms, focus check 150 ms, clock tick 30 s, at most 3 monitor pills.
- Large factor 1.35 (on top of DPI scaling).
- Palette fallback (Python): bg `#202020`, surface `#2D2D2D`, selected `#32373F`, border `#3E3E3E`, text `#FFFFFF`, muted `#A5A5A5`, subtle `#737373`, accent fallback `#4CC2FF`, danger `#DC4646`, segment bg `#1A1A1A`, segment active `#444444`.
- Icons: glyphs from "Segoe Fluent Icons" with fallback "Segoe MDL2 Assets"; reuse the code points of Python's `ICON_*` constants.
- Checks after every task: `dotnet build OverlayUtils.sln` with 0 warnings, and `dotnet run --project tests/OverlayUtils.SelfTest` printing `All N checks passed.`
- Commit messages in English, ending with the attribution line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Opened from a tray click or over the desktop:** the foreground window is the taskbar or the desktop. "Close window" must then do nothing (Alt+F4 on the desktop opens the Windows shut-down dialog), focus is not "restored" to the taskbar, and the overlay opens on the monitor under the cursor. Test in Task 3 (`ShellWindows`) and Task 4 (`Close window` with no target).
2. **Large size on a 1080p screen:** 720 × 1.35 = 972 px is taller than the work area minus margins. The panel must be clamped to the work area and its content must scroll with the selection kept in view. Test in Task 1 (`Place` clamp); manual check in Task 7.
3. **Monitor profiles appearing or disappearing while the overlay is open** (MonitorSwitcher saves a new one, or the folder is emptied): the selection must move to the nearest selectable row, never land on a hidden one, and the description must follow it. Test in Task 2 (`Refresh` after visibility change).
4. **The confirm window running out:** the first press of A on Power arms "Confirm?". Moving to another option, moving to another row or waiting 3 s must disarm it, so a later single A does not shut down the PC. Test in Task 2.
5. **Language switched from the overlay's Settings:** every visible string, including the tab title, the section headers and the footer, must change at once, without reopening. Test in Task 4 (labels are re-read via `Func<string>` after `Loc.SetLanguage`); manual check in Task 7.

---

### Task 1: Config keys and layout maths

**Files:**
- Modify: `src/OverlayUtils.Core/Config/AppConfig.cs`
- Create: `src/OverlayUtils.Core/Overlay/OverlayLayout.cs`
- Test: `tests/OverlayUtils.SelfTest/Program.cs` (new section `// --- Overlay layout ---` before the final cleanup)

**Interfaces:**
- Produces:
  - `AppConfig.OverlaySize` (`[JsonPropertyName("overlay_size")]`, default `OverlaySizes.Auto`) and `AppConfig.OverlaySide` (`"overlay_side"`, default `OverlaySides.Left`).
  - `public static class OverlaySizes { Normal = "normal", Large = "large", Auto = "auto" }` and `public static class OverlaySides { Left = "left", Right = "right" }` (const strings, in `AppConfig.cs` next to `HomeKeyModes`).
  - `namespace OverlayUtils.Core.Overlay`: `public readonly record struct RectI(int Left, int Top, int Right, int Bottom)` with `Width`/`Height` properties.
  - `public static class OverlayLayout` with consts `Width = 420`, `Height = 720`, `Margin = 36`, `LargeFactor = 1.35`, and:
    - `public static double SizeFactor(string sizeSetting, string currentProfile, string tvProfile)`
    - `public static RectI Place(RectI work, double dpiScale, double sizeFactor, string side)` (physical pixels)

- [ ] **Step 1: Write the failing checks**

```csharp
// --- Overlay layout ---------------------------------------------------------
{
    Check(new AppConfig().OverlaySize == OverlaySizes.Auto && new AppConfig().OverlaySide == OverlaySides.Left,
        "overlay defaults");
    var json = JsonSerializer.Serialize(new AppConfig(), ConfigStore.JsonOptions);
    Check(json.Contains("\"overlay_size\"") && json.Contains("\"overlay_side\""), "overlay keys are snake_case");

    Check(OverlayLayout.SizeFactor("normal", "TV", "TV") == 1.0, "normal stays normal on TV");
    Check(OverlayLayout.SizeFactor("large", "Desk", "TV") == 1.35, "large is large everywhere");
    Check(OverlayLayout.SizeFactor("auto", "TV", "TV") == 1.35, "auto is large on the TV profile");
    Check(OverlayLayout.SizeFactor("auto", "Desk", "TV") == 1.0, "auto is normal elsewhere");
    Check(OverlayLayout.SizeFactor("auto", "", "") == 1.0, "auto without a TV profile is normal");
    Check(OverlayLayout.SizeFactor("bogus", "TV", "TV") == 1.35, "unknown setting behaves as auto");

    var hd = new RectI(0, 0, 1920, 1040);
    Check(OverlayLayout.Place(hd, 1.0, 1.0, "left") == new RectI(36, 36, 456, 756), "left placement");
    Check(OverlayLayout.Place(hd, 1.0, 1.0, "right") == new RectI(1464, 36, 1884, 756), "right placement");
    Check(OverlayLayout.Place(new RectI(0, 0, 3840, 2100), 1.5, 1.35, "right") == new RectI(2935, 54, 3786, 1512),
        "dpi and large factor multiply, rounded away from zero");
    Check(OverlayLayout.Place(new RectI(0, 0, 1920, 1032), 1.0, 1.35, "left").Height == 960,
        "height clamped to the work area minus both margins");
    Check(OverlayLayout.Place(new RectI(-1920, 0, 0, 1040), 1.0, 1.0, "left").Left == -1884,
        "monitors left of the primary have negative coordinates");
}
```

- [ ] **Step 2: Run the self-test and see it fail to compile** — `dotnet run --project tests/OverlayUtils.SelfTest`, expected: errors for `OverlaySizes`, `OverlayLayout`, `RectI`.

- [ ] **Step 3: Add the config properties and `OverlayLayout`.** `SizeFactor`: Large → `LargeFactor`; Normal → 1; anything else (Auto) → `LargeFactor` only when `tvProfile != ""` and `currentProfile == tvProfile`. `Place`: `scale = dpiScale * sizeFactor`, `w = round(Width * scale)`, `h = min(round(Height * scale), work.Height - 2 * round(Margin * dpiScale))`, `m = round(Margin * dpiScale)`; left side `x = work.Left + m`, right side `x = work.Right - m - w`; `y = work.Top + m`. Round with `MidpointRounding.AwayFromZero`. The margin is scaled by DPI only, not by the size factor.

- [ ] **Step 4: Run the self-test** — expected `All N checks passed.`

- [ ] **Step 5: Commit** — `git add src/OverlayUtils.Core tests && git commit -m "Add overlay size/side settings and layout maths"`

---

### Task 2: Row model and navigator

**Files:**
- Create: `src/OverlayUtils.Core/Overlay/OverlayItem.cs` (item, option, tab, enums, result, footer types)
- Create: `src/OverlayUtils.Core/Overlay/OverlayNavigator.cs`
- Modify: `src/OverlayUtils.Core/Localization/Strings.resx`, `Strings.it.resx` (footer keys)
- Test: `tests/OverlayUtils.SelfTest/Program.cs` (section `// --- Overlay navigator ---`)

**Interfaces:**
- Produces (namespace `OverlayUtils.Core.Overlay`):

```csharp
public enum ItemKind { Action, Segmented, Choice, Toggle, Expander, Info }
public enum OverlayPage { Tabs, Settings, Info }
public enum OverlayCommand { Up, Down, Left, Right, LeftRepeat, RightRepeat, A, B, PrevTab, NextTab, Settings, WheelUp, WheelDown }
public enum NavEffect { None, Redraw, Hide, RunHere, HideAndRun }
public readonly record struct NavResult(NavEffect Effect, Action? Action = null);
public sealed record OverlayOption(Func<string> Label, Action Action, bool Stay = false, Func<bool>? Active = null);
public sealed record OverlayTab(string Id, Func<string> Title, Func<IReadOnlyList<OverlayItem>> Items);
public sealed record FooterHint(string Keys, string Label);

public sealed class OverlayItem   // all init-only properties
{
    public required ItemKind Kind { get; init; }
    public required Func<string> Label { get; init; }
    public string Icon { get; init; } = "";
    public Func<string>? Section { get; init; }          // header drawn above this row
    public Func<string> Description { get; init; } = () => "";
    public Func<bool> Visible { get; init; } = () => true;
    public Func<IReadOnlyList<OverlayOption>> Options { get; init; } = () => [];  // Segmented
    public Func<string> Value { get; init; } = () => "";                         // Choice, Info
    public Action<int>? Step { get; init; }              // Choice
    public Action? Apply { get; init; }                  // Choice: A runs this after hiding
    public Func<bool> IsOn { get; init; } = () => false; // Toggle, Expander
    public Action? Toggle { get; init; }                 // Toggle, Expander
    public Action? Action { get; init; }                 // Action, Info (Info without Action is not selectable)
    public bool Stay { get; init; }                      // Action/Info: run with the overlay open
    public bool Confirm { get; init; }                   // Segmented: second A within 3 s
    public bool OpensInfo { get; init; }                 // Action: A opens the Info page
    public Func<string>? Hint { get; init; }             // keycap or button text on the right
}

public sealed class OverlayNavigator
{
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(3);
    public OverlayNavigator(IReadOnlyList<OverlayTab> tabs, Func<IReadOnlyList<OverlayItem>> settings,
        Func<IReadOnlyList<OverlayItem>> info, TimeProvider? time = null);
    public OverlayPage Page { get; }
    public IReadOnlyList<OverlayTab> VisibleTabs { get; }
    public OverlayTab CurrentTab { get; }
    public IReadOnlyList<OverlayItem> Items { get; }     // of the current page or tab
    public int Index { get; }
    public int Segment { get; }
    public OverlayItem? Selected { get; }                // null only if nothing is selectable
    public bool IsPending(int index, int segment);
    public void Reset(IReadOnlyCollection<string> hiddenTabIds); // on every open
    public void Refresh();                               // after any change: re-select if the row vanished
    public NavResult Handle(OverlayCommand command);
    public NavResult Click(int index, int? segment = null);      // mouse: select, then as A
    public bool ExpirePending();                         // true if a pending confirm was cleared
    public string Description();                         // of Selected, "" if none
    public IReadOnlyList<FooterHint> Footer();
}
```

Behaviour to implement (Python `_handle`, `_controls_command`, `_move`, `_step`, `_confirm`, `_ensure_selectable`, `_select`, `_open_page`): the navigator **never invokes item callbacks itself** except the read-only ones (`Visible`, `Options`, `Active`, `IsOn`, `Label`); every side effect comes back as `NavResult.Action`.
- Selectable = `Visible()` and not (`Kind == Info && Action == null`).
- Up/Down: next selectable row with wrap-around. WheelUp/WheelDown = Up/Down on Settings/Info, `None` on Tabs. Up/Down skip if nothing is selectable.
- Selecting a row clears the pending confirm and puts the segment cursor on the first option whose `Active()` is true, else 0.
- Left/Right: Segmented → move the cursor, clamped, clear pending, `Redraw`; Choice → `RunHere(() => Step(±1))`; Toggle → `RunHere(Toggle)`; Expander → Left `RunHere(Toggle)` only if `IsOn()`, Right only if not; otherwise `None`. `LeftRepeat`/`RightRepeat` → `None` (only sliders use them; none exist yet).
- A: Choice with `Apply` → `HideAndRun(Apply)`; Choice without → as Right; Toggle/Expander → `RunHere(Toggle)`; Segmented with `Confirm` → first press arms `(index, segment, now + 3 s)` and returns `Redraw`, second press on the same pair before the deadline runs the option; Segmented option run → `Stay ? RunHere : HideAndRun`; Action/Info → `Stay ? RunHere : HideAndRun`. A on a row with nothing to run → `None`.
- A on an Action row with `OpensInfo` → open the Info page, `Redraw`.
- B: Info → Settings; Settings → Tabs (restoring the Tabs `Index`/`Segment` saved when Settings was opened); Tabs → `Hide`.
- Settings command: from Tabs open Settings (save Tabs position); from Settings or Info go back to Tabs.
- PrevTab/NextTab: Tabs page only, cycle over `VisibleTabs`, reset selection to the first selectable row.
- Opening a page selects its first selectable row. `Reset` = Tabs page, first visible tab, first selectable row, nothing pending.
- `VisibleTabs` = tabs whose `Id` is not in the hidden set, with `"Controlli"` always included.
- Footer: Tabs → `Enter` + label for the selected kind, `Esc` + "Close", `F2` + "Settings"; Settings and Info → `Enter` + kind label, `Esc` + "Back"; add `← →` + "Change" when the selected row is Segmented or Choice, `← →` + "Close / open" for an Expander. Kind labels: Action/Segmented/Choice-with-Apply → "Confirm", Choice → "Change", Toggle → "Turn on/off", Expander → "Open", Info → "Select". New keys: `Overlay.Footer.Confirm`, `.Change`, `.Toggle`, `.Open`, `.OpenClose`, `.Select`, `.Close`, `.Back`, `.Settings` (IT: Conferma, Cambia, Attiva/disattiva, Apri, Chiudi / apri, Seleziona, Chiudi, Indietro, Impostazioni).

- [ ] **Step 1: Write the failing checks.** Build a fixture in the section: a manual `TimeProvider` subclass (`sealed class TestClock : TimeProvider { public DateTimeOffset Now; public override DateTimeOffset GetUtcNow() => Now; }`, declared at the end of `Program.cs`), one tab `"Controlli"` with rows `[hidden action (Visible=false), action "Desktop", segmented "Power" (Confirm, options Sleep/Restart/Off), choice "Pick" (Step records direction)]`, a second tab `"Giochi"` with one action, settings `[toggle, expander]`, info `[info row without action, info row with action]`. Record side effects in local lists. Checks (each one line, `Check(cond, "what")`):
  - after `Reset([])`: `Page == Tabs`, `Index == 1` (hidden row skipped), `Description()` is the Desktop row's;
  - `Reset(["Controlli","Giochi"])` keeps Controlli in `VisibleTabs` and drops Giochi;
  - Down from Desktop selects Power; Down from Pick wraps to Desktop (skipping the hidden row);
  - on Power, Right twice → `Segment == 2`, a third Right stays 2;
  - A on Power/Off → `Redraw` and `IsPending(2, 2)`; Left → not pending; A, then clock + 2.9 s, A → `HideAndRun` and running the action records "Off";
  - A, clock + 3.1 s, A → `Redraw` again (expired, re-armed), not `HideAndRun`;
  - A, then Down → not pending; `ExpirePending()` returns true only after the deadline passes;
  - on Pick: Left → `RunHere` whose action calls `Step(-1)`; A (no Apply) → calls `Step(+1)`;
  - Settings command → `Page == Settings`, Index 0; B → back on Tabs at the same row; B on Tabs → `Hide`;
  - WheelDown on Tabs → `None`; on Settings → moves like Down;
  - Info page: the info row without an action is never selected (`Index` lands on the one with an action);
  - NextTab → `CurrentTab.Id == "Giochi"`, NextTab again wraps to Controlli; on Settings, NextTab → `None`;
  - visibility change: make the selected row hidden (flip a captured bool), call `Refresh()` → selection moves to the nearest selectable row and `Description()` follows it (spec bug fix);
  - `Click(2, 1)` selects Power with `Segment == 1` and behaves as A (arms confirm);
  - `Footer()` on Tabs with Power selected contains `"Esc"` with `Loc.T("Overlay.Footer.Close")` and `"← →"` with `Loc.T("Overlay.Footer.Change")`.

- [ ] **Step 2: Run the self-test and see it fail to compile.**

- [ ] **Step 3: Implement the types in `OverlayItem.cs` and `OverlayNavigator` as specified above, and add the footer keys to both `.resx` files.**

- [ ] **Step 4: Run the self-test** — all checks pass.

- [ ] **Step 5: Commit** — `git commit -m "Add overlay row model and navigator"`

---

### Task 3: Win32 helpers: focus, monitors, power, shell

**Files:**
- Modify: `src/OverlayUtils.Core/Interop/Native.cs`
- Create: `src/OverlayUtils.Core/Platform/WindowFocus.cs`, `src/OverlayUtils.Core/Platform/DisplayArea.cs`, `src/OverlayUtils.Core/Platform/PowerActions.cs`, `src/OverlayUtils.Core/Platform/ShellOpen.cs`, `src/OverlayUtils.Core/Platform/AccentPalette.cs`
- Test: `tests/OverlayUtils.SelfTest/Program.cs` (section `// --- Overlay platform ---`, pure functions only so it still runs off Windows)

**Interfaces:**
- Consumes: `RectI` (Task 1).
- Produces:
  - `public static class WindowFocus` (Python `_force_foreground`, `_monitor_of`): `nint Foreground()`; `nint ForegroundApp()` (foreground, or 0 if it is a shell window); `void Force(nint hwnd)` (AttachThreadInput to the foreground thread, BringWindowToTop + SetForegroundWindow, and if that failed an Alt tap via `KeySender.Chord(Native.VK_MENU)` then SetForegroundWindow again); `bool IsAlive(nint hwnd)`; `uint ProcessOf(nint hwnd)`; `string ClassName(nint hwnd)`.
  - `public static class ShellWindows { public static bool IsShellClass(string className); }`, the classes `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, `Progman`, `WorkerW`, `NotifyIconOverflowWindow`, `TopLevelWindowForOverflowXamlIsland` (in `WindowFocus.cs`).
  - `public static class DisplayArea { public static (RectI Work, double DpiScale) ForWindow(nint hwnd); public static (RectI Work, double DpiScale) ForCursor(); }` (MonitorFromWindow / MonitorFromPoint(GetCursorPos) with MONITOR_DEFAULTTONEAREST, GetMonitorInfoW work area, GetDpiForMonitor effective DPI / 96, 1.0 if that call fails).
  - `public static class PowerActions { Sleep(); Restart(); ShutDown(); }`: `SetSuspendState(false, false, false)` from powrprof; `shutdown /r /t 0` and `shutdown /s /t 0` with `CreateNoWindow`.
  - `public static class ShellOpen { public static void Open(string target); }`: `Process.Start` with `UseShellExecute = true`, logs and swallows failures.
  - `public static class AccentPalette { public static (byte R, byte G, byte B)? LightVariant(byte[]? palette); public static (byte R, byte G, byte B)? Read(); }`: `palette` is the `AccentPalette` REG_BINARY in `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent` (8 entries × RGBA); the light variant is entry 1 (bytes 4–6). Null if shorter than 8 bytes or unreadable.

- [ ] **Step 1: Write the failing checks**

```csharp
// --- Overlay platform -------------------------------------------------------
{
    foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW" })
        Check(ShellWindows.IsShellClass(cls), $"{cls} is a shell window");
    Check(!ShellWindows.IsShellClass("UnityWndClass") && !ShellWindows.IsShellClass(""), "games are not shell windows");

    byte[] palette = [0x99, 0xEB, 0xFF, 0, 0x4C, 0xC2, 0xFF, 0, 0, 0x78, 0xD4, 0];
    Check(AccentPalette.LightVariant(palette) == ((byte)0x4C, (byte)0xC2, (byte)0xFF), "light accent is entry 1");
    Check(AccentPalette.LightVariant([1, 2, 3]) is null && AccentPalette.LightVariant(null) is null, "short palette ignored");
}
```

- [ ] **Step 2: Run the self-test and see it fail to compile.**

- [ ] **Step 3: Add the declarations to `Native.cs`** (GetForegroundWindow, SetForegroundWindow, BringWindowToTop, AttachThreadInput, GetWindowThreadProcessId, IsWindow, GetAncestor, GetClassNameW, GetCursorPos, MonitorFromWindow, MonitorFromPoint, GetMonitorInfoW + `MONITORINFO`, shcore GetDpiForMonitor, powrprof SetSuspendState, plus the constants they need), **then the five helper classes.** `ForegroundApp` also maps the window through `GetAncestor(GA_ROOT)` before checking the class.

- [ ] **Step 4: Run the self-test and `dotnet build OverlayUtils.sln`** — all checks pass, 0 warnings.

- [ ] **Step 5: Commit** — `git commit -m "Add focus, monitor, power and accent helpers"`

---

### Task 4: Overlay content (Controls, Settings, Info)

**Files:**
- Create: `src/OverlayUtils.Core/Overlay/IOverlayHost.cs`, `src/OverlayUtils.Core/Overlay/OverlayContent.cs`, `src/OverlayUtils.Core/Overlay/Glyphs.cs`
- Modify: both `.resx` files (keys `Overlay.*`, `Settings.*`)
- Test: `tests/OverlayUtils.SelfTest/Program.cs` (section `// --- Overlay content ---`)

**Interfaces:**
- Consumes: Task 2 types; `MonitorProfiles`, `MonitorSwitcherLocator`, `ConfigStore`, `StartupRegistration.IsEnabled`, `KeySender`, `Loc`; Task 3 `PowerActions`, `ShellOpen`.
- Produces:

```csharp
public interface IOverlayHost
{
    bool InFullMode { get; }
    bool HasPreviousWindow { get; }      // false when opened over the taskbar/desktop
    void SwitchMonitor(string profile);  // switch + tray icon + notification, like the hotkeys
    void ToggleStartup();
    void ChooseMonitorSwitcher();        // file picker, as the tray menu
    void SwitchFullMode();               // overlay flow: no dialogs, failures as notifications
    void Exit();
}

public sealed class OverlayContent
{
    public const string ControlsTabId = "Controlli";
    public const int MonitorMaxPills = 3;
    public OverlayContent(ConfigStore config, MonitorProfiles monitors, MonitorSwitcherLocator locator,
        IOverlayHost host, Action? sendAltF4 = null);    // default KeySender.AltF4; tests pass a counter
    public IReadOnlyList<OverlayTab> Tabs { get; }   // stage 2: only Controls
    public IReadOnlyList<OverlayItem> Controls();
    public IReadOnlyList<OverlayItem> Settings();
    public IReadOnlyList<OverlayItem> Info();
    public void ResetTransient();                    // on open: monitor pick cleared, Info panel = Shortcuts
}
```

`Glyphs` holds the `ICON_*` code points used (monitor, game, desktop, close, power, bell, bell-off, list, pc, folder, home, info, keyboard, link, chevrons, settings) as `const string`.

Rows, in order (labels, sections and descriptions are `Func<string>` over `Loc.T`, so a language change shows on the next redraw):
- **Controls** (spec table): Monitor (Visible = profiles exist; Segmented with one option per profile, `Active` = `monitors.Current`, action `host.SwitchMonitor(p)`, when ≤ 3 profiles; otherwise Choice whose `Value` is the pick or the current profile, `Step` moves the pick, `Apply` switches to it); Big Picture (Segmented Open/Exit → `ShellOpen.Open("steam://open/bigpicture")` / `"steam://close/bigpicture"`); Show desktop (Action → `KeySender.ShowDesktop`); Close window (Action, `Hint` "Alt+F4", action calls `sendAltF4` only if `host.HasPreviousWindow`); Power (Segmented Sleep/Restart/Shut down, `Confirm`).
- **Settings** (spec table): tab toggle Controls (always on, `Toggle` does nothing); Desk and TV (Choice over profiles + "None", via `monitors.SetRole`, visible with profiles); MonitorSwitcher (Segmented one option "Choose…", `Stay`, → `host.ChooseMonitorSwitcher`; label shows searching/found/not found from the locator; visible with profiles); Notifications (Toggle); Start with Windows (Toggle, `IsOn` = `StartupRegistration.IsEnabled`, `Toggle` = `host.ToggleStartup`); Home key (Segmented Windows/Alt+Tab/None, `Stay`, `Active` = current mode); Language (Choice cycling `auto → en → it`, `Value` "Automatic (English)" style via the existing `Menu.Language.Auto` and `Language.*` keys); Size (Segmented Normal/Large/Auto, `Stay`); Position (Segmented Left/Right, `Stay`); Over fullscreen games (Segmented, `Confirm`, one option Enable or Disable depending on `host.InFullMode`, → `host.SwitchFullMode`, label "… · On/Off"); Information (Action with `OpensInfo = true`); Exit (Segmented "Exit", `Confirm`, → `host.Exit`).
- **Info**: three Expanders (Shortcuts, Monitor, System; only one open, `IsOn` = open panel, `Toggle` opens it or closes it), each followed by its Info rows, which are `Visible` only while their panel is open. Shortcuts: Shift+F1, Ctrl+Alt+Down (desk name), Ctrl+Alt+Up (TV name), arrows, Enter, Esc, Tab / PgUp / PgDn, F2. Monitor: MonitorSwitcher (path or "not found"; action opens its folder or `MonitorSwitcherLocator.Website`), profiles (comma list or "none"), profiles folder (open), Desk · TV. System: version + `AppPaths.AppDir` (open), mode Standard/Full, configuration folder (`AppPaths.ConfigDir`, open).

- [ ] **Step 1: Write the failing checks.** Use a temp profiles dir and a `FakeHost : IOverlayHost` (declared at the end of `Program.cs`) recording calls, `InFullMode`/`HasPreviousWindow` settable.
  - no profiles: the Monitor row is not visible; with a navigator over the content, after `Reset([])` the selected row is Big Picture and `Description()` is Big Picture's (spec bug fix);
  - 3 profiles: Monitor is Segmented with 3 options and the cursor starts on `monitors.Current`; 4 profiles: Monitor is Choice, Right then A → `HideAndRun` whose action calls `host.SwitchMonitor` with the second profile;
  - Close window (content built with a counting `sendAltF4`): A → `HideAndRun`; running the action with `HasPreviousWindow == false` leaves the counter at 0, with `true` it becomes 1;
  - `Settings()` contains no row whose label is Spotify/Discord, contains Size and Position; running the Size "Large" option sets `config.Current.OverlaySize == "large"`; Position "Right" sets `"right"`;
  - Over fullscreen games: with `InFullMode = false` its single option label is `Loc.T("Settings.FullMode.Enable")`, with `true` it is `...Disable`; it has `Confirm`; running it calls `host.SwitchFullMode` once;
  - Language: Right on the row then running the action changes `config.Current.Language` from `"auto"` to `"en"`;
  - after `Loc.SetLanguage("it")` the Controls tab `Title()` and the Power row `Label()` return the Italian strings, then reset to `"en"`;
  - Info: after `ResetTransient()` only the Shortcuts rows are visible; toggling Monitor hides the Shortcuts rows and shows the Monitor rows;
  - Information row: A on it → `Page == Info`.

- [ ] **Step 2: Run the self-test and see it fail to compile.**

- [ ] **Step 3: Implement `IOverlayHost`, `Glyphs`, `OverlayContent` and the new keys in both `.resx` files** (English and Italian; Italian copy from Python where the row exists there, e.g. "Mostra desktop", "Chiudi finestra", "Sospendi", "Riavvia", "Spegni").

- [ ] **Step 4: Run the self-test** — all checks pass.

- [ ] **Step 5: Commit** — `git commit -m "Add overlay content: Controls, Settings, Info"`

---

### Task 5: Overlay window and view model

**Files:**
- Create: `src/OverlayUtils.App/Overlay/OverlayViewModel.cs`, `src/OverlayUtils.App/Overlay/OverlayWindow.xaml`, `src/OverlayUtils.App/Overlay/OverlayWindow.xaml.cs`, `src/OverlayUtils.App/Overlay/OverlayTheme.xaml`

**Interfaces:**
- Consumes: `OverlayNavigator`, `OverlayItem`, `FooterHint`, `MonitorProfiles.Style` (profile colour/label for the header).
- Produces:
  - `internal sealed class OverlayViewModel : INotifyPropertyChanged` with `void Refresh(OverlayNavigator nav, string clock, (byte R, byte G, byte B)? profileColor, string profileName)` and bindable `Title`, `Clock`, `ProfileName`, `ProfileBrush`, `Tabs` (`TabVm { Title, IsCurrent }`), `PageTitle` (empty on Tabs), `IsOnPage`, `Rows` (`RowVm { Index, Section, Icon, Label, Kind, Value, IsOn, Hint, IsSelected, Options }`, `OptionVm { Index, Label, IsCursor, IsActive, IsPending }`), `Description`, `Footer` (`FooterVm { Keys, Label }`), `AccentBrush`.
  - `internal sealed partial class OverlayWindow : Window` with events `event Action<OverlayCommand>? CommandIssued`, `event Action<int, int?>? RowClicked`, and `void Bind(OverlayViewModel vm)`.

- [ ] **Step 1: Build the view model.** `Refresh` rebuilds `Rows` from the visible items only (hidden rows are skipped, `Index` keeps the navigator index so clicks map back), puts `Section` on a row when it has one, and marks `IsSelected`, `IsCursor` (segment cursor on the selected row only), `IsActive`, `IsPending` (`nav.IsPending`). Pending options show `Loc.T("Overlay.ConfirmPending")` ("Confirm?" / "Conferma?") instead of their label.

- [ ] **Step 2: Build the window.** `WindowStyle=None`, `ResizeMode=NoResize`, `ShowInTaskbar=False`, `Topmost=True`, `ShowActivated=True`, fixed `Width` 420 / `Height` 720 in DIPs, root content under a `LayoutTransform` `ScaleTransform` bound to the size factor (set by the controller). Layout top to bottom: header (title, gear button, clock, profile pill), tab strip, page title row with back chevron (only on Settings/Info), a `ScrollViewer` with the rows (`ItemsControl` with one `DataTemplate` per `ItemKind`, selector on `RowVm.Kind`), the description, the footer pinned to the bottom (`DockPanel` with the footer docked bottom, so it never floats). Selected row: accent-tinted background plus a 3 px accent bar on the leading edge; selected option inside a segmented row: outlined in accent; pending option: danger background. Controls have `Focusable=False`; the window handles `PreviewKeyDown` and marks every handled key `Handled` so WPF focus navigation never sees arrows or Tab. Keep the selected row in view (`BringIntoView` after each refresh).

- [ ] **Step 3: Map input to commands** in `OverlayWindow.xaml.cs`: Up/Down → Up/Down; Left/Right → Left/Right, or `LeftRepeat`/`RightRepeat` when `e.IsRepeat`; Enter → A; Escape → B; Tab → NextTab, Shift+Tab → PrevTab; PageDown → NextTab, PageUp → PrevTab; F2 → Settings. Mouse: left click on a row or option → `RowClicked(index, optionIndex or null)`; gear → Settings; back chevron → B; right click anywhere → B; wheel → WheelUp/WheelDown.

- [ ] **Step 4: Build** — `dotnet build OverlayUtils.sln`, 0 warnings; self-test still passes (it scans the new `.cs` files for keys).

- [ ] **Step 5: Commit** — `git commit -m "Add overlay window and view model"`

---

### Task 6: Overlay controller and tray wiring

**Files:**
- Create: `src/OverlayUtils.App/Overlay/OverlayController.cs`
- Modify: `src/OverlayUtils.App/Tray/TrayController.cs`
- Modify: both `.resx` files (`Notify.*` keys if new ones are needed)

**Interfaces:**
- Consumes: everything above; `ModeSwitcher`.
- Produces:
  - `internal sealed class OverlayController : IDisposable` — `OverlayController(Application app, ConfigStore config, MonitorProfiles monitors, OverlayContent content)`; `void Toggle()` (callable from any thread, marshals to the UI thread); `bool IsOpen`; `bool HasPreviousWindow`; `void Hide(bool restoreFocus = true)`.
  - `TrayController` implements `IOverlayHost`; `private static string FailureMessage(ModeSwitcher.Result result)` (extracted from `ShowFailure`, which now calls it); `private void Notify(string message, bool force = false)` (`force` ignores the notifications setting, used for failures).

- [ ] **Step 1: Write `OverlayController`.** Window created once (hidden, then shown/hidden). **Show** (Python `show`): `prev = WindowFocus.ForegroundApp()`; area = `prev != 0 ? DisplayArea.ForWindow(prev) : DisplayArea.ForCursor()`; `content.ResetTransient()`; `navigator.Reset(config.Current.HiddenTabs)`; size factor from `OverlayLayout.SizeFactor(config.Current.OverlaySize, monitors.Current, monitors.ForRole(MonitorRole.Tv))`; refresh the view model; place with `OverlayLayout.Place(...)` via `SetWindowPos` in physical pixels **before** showing (so a DPI change on the target monitor is applied while hidden); show, `WindowFocus.Force(own hwnd)`; start the 150 ms focus timer and the 30 s clock timer. **Hide** (Python `hide`): if `restoreFocus` and `WindowFocus.IsAlive(prev)` → `WindowFocus.Force(prev)` **before** hiding; hide; stop timers; clear pending. **Focus timer** (Python `_watch_focus`): foreground process == ours → `hadFocus = true`; else if `hadFocus` → `Hide(restoreFocus: false)`. **Commands**: `navigator.Handle` / `Click`, then by effect: `Redraw` → refresh; `Hide` → `Hide()`; `RunHere` → run guarded, `navigator.Refresh()`, refresh; `HideAndRun` → `Hide()`, then `Task.Run` awaiting 150 ms, then the action, guarded with `Log.Error("Overlay action", e)`. A 250 ms timer while a confirm is pending calls `ExpirePending()` and refreshes when it returns true. Re-render on `ConfigStore.Changed` and `Loc.LanguageChanged` while open.

- [ ] **Step 2: Wire `TrayController`.** Create `OverlayContent` + `OverlayController` in `Start`; register hotkey id 2 = `MOD_SHIFT`, `VK_F1` (notify `Notify.HotkeyBusy` with "Shift+F1" if busy) and route it in `OnHotkey` to `_overlay.Toggle()`; replace the left-click `ShowContextMenu` reflection with `_overlay.Toggle()` (right click keeps the menu); implement `IOverlayHost` (`SwitchMonitor(name)` reuses the existing `SwitchMonitor(Func<SwitchResult>)` with `() => _monitors.Switch(name)`; `HasPreviousWindow` forwards to the controller; `SwitchFullMode` runs `ModeSwitcher.EnableFullMode` or `BeginDisableFullMode` on `Task.Run` with `_busy` set, exits on `Ok`, otherwise `Notify(FailureMessage(result), force: true)`; no MessageBox anywhere on this path); dispose the controller.

- [ ] **Step 3: Build and self-test** — 0 warnings, all checks pass.

- [ ] **Step 4: Run the app and check by hand** (debug `OverlayUtils.exe`): Shift+F1 opens on the monitor of the foreground window and closes again; tray left click opens it on the monitor under the cursor; Esc closes and focus is back in the previous app; clicking another window closes it; Notepad in front → Close window closes Notepad (and asks to save if dirty), not the overlay; over the bare desktop Close window does nothing; Power arms "Confirm?" and disarms after 3 s; F2 opens Settings, Esc returns to the same row; changing Home key, Size and Position from Settings is reflected in the tray menu and on the next open. `errors.log` has no new errors.

- [ ] **Step 5: Commit** — `git commit -m "Open the overlay with Shift+F1 and the tray icon"`

---

### Task 7: Look: acrylic, accent, corners, animation

**Files:**
- Create: `src/OverlayUtils.App/Overlay/WindowEffects.cs`
- Modify: `src/OverlayUtils.Core/Interop/Native.cs` (DWM declarations), `OverlayWindow.xaml(.cs)`, `OverlayController.cs`, `OverlayTheme.xaml`

**Interfaces:**
- Consumes: `AccentPalette.Read()` (Task 3).
- Produces: `internal static class WindowEffects` — `static void ApplyChrome(Window w)` (WS_EX_TOOLWINDOW without WS_EX_APPWINDOW, rounded corners `DWMWA_WINDOW_CORNER_PREFERENCE = 33` → `DWMWCP_ROUND = 2`, dark mode `DWMWA_USE_IMMERSIVE_DARK_MODE = 20`); `static bool TryApplyAcrylic(Window w)` (`DWMWA_SYSTEMBACKDROP_TYPE = 38` → `DWMSBT_TRANSIENTWINDOW = 3`, frame extended into the whole client area, window background transparent; returns false on Windows 10, when `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\EnableTransparency` is 0, or if a call fails, and the caller keeps the solid `#202020` background); `static Color Accent()` (`AccentPalette.Read()` or `#4CC2FF`).

- [ ] **Step 1: Implement `WindowEffects` and apply it in `OverlayWindow.SourceInitialized`.** Translucent background: on acrylic, the panel background is `#202020` at about 70 % opacity so text keeps its contrast over bright games; rows and surfaces keep their colours.

- [ ] **Step 2: Accent colour.** Bind `AccentBrush` from `WindowEffects.Accent()`; refresh it on `SystemEvents.UserPreferenceChanged` (category `General` or `Color`).

- [ ] **Step 3: Opening animation.** On show, when `SystemParameters.ClientAreaAnimation` is true: slide in from the panel's side (24 DIPs) and fade 0 → 1 over 150 ms; no animation on hide.

- [ ] **Step 4: Build, self-test, run and check by hand:** translucent over a bright window, solid with Windows "Transparency effects" off; accent follows Settings → Personalisation → Colours while running; animation present, absent with "Animation effects" off; Large on a 1080p monitor clamps to the screen and scrolls with the selection visible (Review Focus 2); switching language in Settings changes every visible string at once (Review Focus 5); move the overlay between a 100 % and a 150 % monitor (open it with each in front): size and position right on both.

- [ ] **Step 5: Commit** — `git commit -m "Overlay look: acrylic, Windows accent, rounded corners, animation"`

---

### Task 8: Full mode check and docs

**Files:**
- Modify: `docs/PORTING.md`, `docs/ARCHITECTURE.md`, `docs/WORKLOG.md`, `README.txt`

- [ ] **Step 1: Full-mode check.** Run `scripts\build.ps1`, start `dist\OverlayUtils\OverlayUtils.exe`, enable full mode from the overlay's Settings (A twice, UAC once, restart in full mode), then open the overlay over a fullscreen game (borderless and, if available, exclusive fullscreen): it appears on top and takes clicks. Confirm Python's `_repair_frame` problem (content detached from the frame) does not happen; if it does, stop and report.

- [ ] **Step 2: Docs.** PORTING: stage 2 items `[~]` (or `[x]` for what was checked by hand), stage 2 manual test list (the checks from Tasks 6–8). ARCHITECTURE: an "Overlay" section (Core navigator/content vs App window/controller, the effect model, threads: overlay on the UI thread, actions after hide on `Task.Run`), new config keys, `Overlay.*`/`Settings.*` keys. README.txt (EN + IT): Shift+F1 / tray click, keys inside the overlay, Settings with F2. WORKLOG: dated session line, plus decisions: Settings key F2, failures of the overlay's full-mode switch are always notified even with notifications off, Close window does nothing without a previous window.

- [ ] **Step 3: Self-test and build one last time, then commit** — `git commit -m "Stage 2 docs and checks"`
