# Overlay Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the 420x720 side panel into the full-screen "Focus" overlay (rail, column, reacting stage), add the Home page, pad battery percent and the current game.

**Architecture:** Core keeps all logic and gets the testable parts (Home tab, install folders, current-game matching, battery percent, frame maths). The App's `OverlayWindow` is rebuilt as a full-monitor window with three zones over the DWM backdrop; `OverlayNavigator` stays the only owner of selection, and the view only draws its state and animates between states.

**Tech Stack:** .NET 10, WPF, DWM system backdrop, SDL3 (existing), self-test console app.

**Spec:** `docs/superpowers/specs/2026-11-04-overlay-redesign-design.md` (read it first; this plan argues from it).

## Global Constraints

- User-facing text only through `Loc.T("Key")`, every key in **both** `Strings.resx` and `Strings.it.resx` (the self-test fails otherwise).
- Settings only through `AppConfig` (snake_case JSON name) and `ConfigStore.Update`.
- UI work on the UI thread (`OnUi`); process lookup and file work in `Task.Run`, never blocking the open.
- Win32 declarations in `src/OverlayUtils.Core/Interop/Native.cs` (`LibraryImport`, blittable structs).
- `Log.Error(context, exception)` for problems; no handler, menu action or hotkey path may throw.
- Every animation under 250 ms; none when `SystemParameters.ClientAreaAnimation` is false; a new command completes or cancels a running animation at once (input never waits).
- No layered full-screen window (`AllowsTransparency` stays false).
- Agents never run, stop or touch the owner's running OverlayUtils, never change audio devices/volumes, Windows settings, or send input, never launch games. App changes are verified by `dotnet build OverlayUtils.sln` with 0 warnings; the owner checks the look at the marked checkpoints.
- Checks after every task: `dotnet build OverlayUtils.sln` → `0 Warning(s)`, `0 Error(s)`; `dotnet run --project tests/OverlayUtils.SelfTest` → `All N checks passed.`
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push or merge.

## Review Focus

1. **Current game false positives:** a library entry whose install folder is a drive root, empty, or a prefix of another folder by name only (`C:\Games\Foo` vs `C:\Games\Foobar`) must not match. Owner expectation: nothing shown rather than a wrong title. (Tests in Task 3.)
2. **Home hidden, or all its tiles invisible** (no media, no monitor profiles): the overlay opens on Controls; with Home shown but empty of optional tiles it still has Volume / Mic / Close window and never selects an invisible tile. (Tests in Task 5.)
3. **Monitor change while open** (profile switch from the Screen tile moves the game to another monitor or changes DPI): the frame re-lays out on the monitor it is on, cover columns recomputed, grid selection kept by id. (Test in Task 6 for the maths; Task 7 wires `Relayout`.)
4. **Rapid input during animations** (holding Down, LT/RT spam): selection and page follow every command; no stale highlight left between two rows, no page stuck half-faded. (Task 12 rule, owner checkpoint.)
5. **Pads without battery reporting / charging / fully charged:** wired shows nothing, charging shows the percent, charged shows 100 %. (Tests in Task 4.)

---

### Task 1: Backdrop spike (throwaway, owner gate)

**Files:**
- Create (scratchpad, NOT in the repo): `<scratchpad>/backdropspike/BackdropSpike.csproj`, `MainWindow.xaml(.cs)`, `App.xaml(.cs)`

- [ ] **Step 1:** WPF `net10.0-windows` exe with one window: `WindowStyle=None`, `Topmost`, `AllowsTransparency=false`, placed over the full bounds of the monitor under the cursor, `WS_EX_TOOLWINDOW`. Apply the backdrop exactly as `WindowEffects.TryApplyAcrylic` does (extend frame `-1`, `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW`, composition background transparent), then draw a WPF `LinearGradientBrush` (left→right `#F0080A10` @0, `#DB080A10` @0.38, `#80080A10` @0.72, `#4D080A10` @1) and a sample column of text. Space toggles hide/show (re-applying the backdrop on show, through `DWMSBT_NONE` first, as the existing code does); Esc exits. Copy the needed P/Invokes locally; nothing from the repo is referenced.
- [ ] **Step 2:** Build it. Expected: builds with 0 errors.
- [ ] **Step 3 (owner):** Stop and ask the owner to run the spike exe over a borderless-fullscreen game and the desktop, toggle it ~10 times, and report: blur visible behind the gradient? flicker or one solid frame on show? frame-rate drop while hidden? Record the answer in `docs/WORKLOG.md` (Decisions) in Task 13.
- [ ] **Step 4:** If the owner reports the backdrop works: go on. If not: the fallback in the spec applies (near-opaque `#F2080A10` solid backdrop); note it and go on. No commit (scratch only).

### Task 2: Install folder on every game

**Files:**
- Modify: `src/OverlayUtils.Core/Games/GameEntry.cs`, `SteamLibrary.cs`, `EpicLibrary.cs`, `GogLibrary.cs`, `EmulatorLibrary.cs`, `ManualApps.cs`, `src/OverlayUtils.App/Games/XboxLibrary.cs`
- Test: `tests/OverlayUtils.SelfTest/Program.cs` (the existing games reader sections)

**Interfaces:**
- Produces: `GameEntry(..., string? Arguments = null, string? InstallDir = null)` (new last positional parameter).

- [ ] **Step 1: failing checks** next to each reader's existing tests (they already build fake trees / registry views):
  - Steam manifest with `"installdir" "Ember Hollow"` in library `L` → `InstallDir == Path.Combine(L, "steamapps", "common", "Ember Hollow")`; manifest without `installdir` → `InstallDir == null`.
  - Steam shortcut with exe `"C:\Tools\x\run.exe"` (quoted as Steam stores it) → `InstallDir == @"C:\Tools\x"`.
  - Epic manifest `InstallLocation = D:\Epic\Game` → `InstallDir == @"D:\Epic\Game"`.
  - GOG key with `path = E:\GOG\Game` → that; without `path` → the exe's folder.
  - Emulator and manual app → the exe's folder.
- [ ] **Step 2:** Run self-test. Expected: the new checks FAIL.
- [ ] **Step 3:** Add the parameter and fill it in each reader (Xbox: `package.InstalledPath`). Full paths via `Path.GetFullPath`; unreadable/missing → `null`.
- [ ] **Step 4:** Build + self-test. Expected: 0 warnings, all checks pass.
- [ ] **Step 5:** Commit `Games: install folder per entry`.

### Task 3: Current game matching and play time text

**Files:**
- Create: `src/OverlayUtils.Core/Games/CurrentGame.cs`
- Create: `src/OverlayUtils.Core/Platform/ForegroundProcess.cs`
- Modify: `src/OverlayUtils.Core/Interop/Native.cs` (add `GetProcessTimes`, `FILETIME` if missing), both `Strings*.resx`
- Test: `tests/OverlayUtils.SelfTest/Program.cs` (new section "Current game")

**Interfaces:**
- Produces:
  - `static GameEntry? CurrentGame.Match(string exePath, IEnumerable<GameEntry> games)`
  - `static string CurrentGame.PlayTime(TimeSpan running)` (localized)
  - `static (string ExePath, DateTime StartUtc)? ForegroundProcess.Describe(nint hwnd)`
  - Strings: `Overlay.Playing.Minutes` = "{0} min" / "{0} min"; `Overlay.Playing.Hours` = "{0} h {1} min" / "{0} h {1} min"; `Overlay.Eyebrow.Game` = "{0} · {1}" / "{0} · {1}".

- [ ] **Step 1: failing checks**
  ```csharp
  var a = Game("a", installDir: @"C:\Games\Foo");
  var b = Game("b", installDir: @"C:\Games\Foobar");
  var launcher = Game("l", installDir: @"C:\Launcher");
  var inner = Game("i", installDir: @"C:\Launcher\games\Inner");
  var root = Game("r", installDir: @"D:\");
  Check(CurrentGame.Match(@"C:\Games\Foo\bin\foo.exe", [a, b])?.Id == "a", "exe under the folder matches");
  Check(CurrentGame.Match(@"c:\games\FOOBAR\x.exe", [a, b])?.Id == "b", "case-insensitive, no prefix-by-name match");
  Check(CurrentGame.Match(@"C:\Launcher\games\Inner\i.exe", [launcher, inner])?.Id == "i", "longest folder wins");
  Check(CurrentGame.Match(@"D:\anything.exe", [root]) is null, "a drive root never matches");
  Check(CurrentGame.Match(@"C:\Other\x.exe", [a]) is null, "no match");
  Check(CurrentGame.Match(@"C:\Games\Foo\x.exe", [Game("n", installDir: null), Game("e", installDir: "")]) is null, "missing folders are skipped");
  Check(CurrentGame.PlayTime(TimeSpan.FromSeconds(20)) == "0 min", "under a minute");
  Check(CurrentGame.PlayTime(TimeSpan.FromMinutes(72)) == "1 h 12 min", "hours and minutes");
  ```
  (`Game(...)` is a small local helper building a `GameEntry` with the given id and `InstallDir`.)
- [ ] **Step 2:** Run self-test. Expected: FAIL (type missing).
- [ ] **Step 3:** Implement `Match` by comparing `Path.GetFullPath` forms with a trailing separator, `StringComparison.OrdinalIgnoreCase`, skipping null/empty dirs and dirs equal to `Path.GetPathRoot(dir)`; pick the longest. `PlayTime`: under 60 min → `Minutes`, else `Hours`. `ForegroundProcess.Describe`: `GetWindowThreadProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` → `QueryFullProcessImageName` (as `AudioService.Sessions.cs:290` does) + `GetProcessTimes` creation time; any failure → `null`; handle always closed.
- [ ] **Step 4:** Build + self-test. Expected: pass.
- [ ] **Step 5:** Commit `Current game matching`.

### Task 4: Pad battery percent

**Files:**
- Modify: `src/OverlayUtils.Core/Input/PadInterpreter.cs`, `src/OverlayUtils.Core/Input/PadService.cs`, `src/OverlayUtils.App/Tray/TrayController.cs:274`, `src/OverlayUtils.App/Overlay/OverlayController.cs` (`SetPadState`)
- Test: self-test, next to the existing `BatteryLevel` checks

**Interfaces:**
- Produces: `static int? PadInterpreter.BatteryPercent(int sdlPowerState, int percent)`; `int? PadService.BatteryPercent`; `OverlayController.SetPadState(bool padConnected, int? batteryLevel, int? batteryPercent)`.

- [ ] **Step 1: failing checks:** `BatteryPercent(1, 84) == 84`; `BatteryPercent(3, 40) == 40` (charging); `BatteryPercent(4, -1) == 100` (charged); `BatteryPercent(2, -1) == null` (no battery / wired); `BatteryPercent(1, -1) == null`; `BatteryPercent(0, 50) == null`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement; `PadService.UpdateBattery` stores both level and percent (same lock); tray passes both to the overlay.
- [ ] **Step 4:** Build + self-test. Expected: pass.
- [ ] **Step 5:** Commit `Pad battery percent`.

### Task 5: Home tab

**Files:**
- Modify: `src/OverlayUtils.Core/Overlay/OverlayItem.cs` (`OverlayTab`), `OverlayContent.cs`, both `Strings*.resx`
- Test: self-test (new section "Home"; fix existing tab tests)

**Interfaces:**
- Produces:
  - `OverlayContent.HomeTabId = "Home"`; `Tabs` = Home, Controls, Audio, Games (this order).
  - `public sealed record OverlayTab(string Id, Func<string> Title, Func<IReadOnlyList<OverlayItem>> Items, bool Tiles = false)`; Home has `Tiles: true`.
  - `IReadOnlyList<OverlayItem> OverlayContent.Home()` returns, in order, the items with keys `audio.volume`, `audio.media`, `audio.input`, `controls.monitor`, `controls.closewindow`, `controls.bigpicture`, taken from `Audio()` / `Controls()` (same instances, so same callbacks). Add those three `controls.*` keys to `MonitorRow()` (both shapes) and the two Controls items.
  - Strings: `Overlay.Tab.Home` = "Home" / "Home".
  - Settings → Tabs: `TabRow(HomeTabId, "Overlay.Tab.Home", "Overlay.Section.Tabs")` first; Controls row loses the section; Controls stays never-hidden; Home is hideable.
- Decision: Home tiles are navigated in reading order with Up/Down (the navigator's normal list move); Left/Right/A act on the tile's item exactly as on its row. No navigator change.

- [ ] **Step 1: failing checks**
  - `content.Tabs.Select(t => t.Id)` equals `["Home", "Controlli", "Audio", "Giochi"]`; `content.Tabs[0].Tiles`.
  - `content.Home().Select(i => i.Key)` equals the six keys above.
  - New navigator after `Reset([])` → `CurrentTab.Id == "Home"`, `Selected.Key == "audio.volume"`.
  - `Reset(["Home"])` → `CurrentTab.Id == "Controlli"`.
  - With `FakeMedia` having no current track: `Down` from volume selects `audio.input` (media skipped).
  - On Home, `Right` on the volume tile runs `StepVolume(+1)` on `FakeAudio` and `A` runs `ToggleMute`; on the input tile `A` runs `ToggleMicMute`.
  - Settings → Tabs Home toggle adds/removes `"Home"` in `HiddenTabs`.
- [ ] **Step 2:** Run. Expected: new checks FAIL.
- [ ] **Step 3:** Implement. Then fix existing checks that assumed Controls is first or 3 tabs (e.g. "Controls is the first tab", tab counts, tests that read Controls rows right after `Reset`): pass `HiddenTabs` with `"Home"` in those tests' `Reset`, or step with `NextTab`, whichever keeps each test's intent.
- [ ] **Step 4:** Build + self-test. Expected: all pass.
- [ ] **Step 5:** Commit `Overlay: Home tab`.

### Task 6: Frame maths, monitor bounds, side setting removed

**Files:**
- Modify: `src/OverlayUtils.Core/Overlay/OverlayLayout.cs`, `src/OverlayUtils.Core/Platform/DisplayArea.cs`, `src/OverlayUtils.Core/Config/AppConfig.cs`, `OverlayContent.cs` (Settings rows; `Games()` columns), both `Strings*.resx` (remove `Settings.Position.*`)
- Test: self-test (replace the `Place` checks at `Program.cs:204-213`, the `OverlaySide` default at `:193`, the Position row checks at `:533-535`)

**Interfaces:**
- Produces:
  - `public readonly record struct OverlayFrame(double RailWidth, double ColumnWidth, double Gutter, int CoverColumns)` (DIPs).
  - `static OverlayFrame OverlayLayout.Frame(double widthDip, double sizeFactor)`: `RailWidth = 0.07 * w`; `ColumnWidth = (sizeFactor > 1 ? 0.40 : 0.30) * w`; `Gutter = 48 * sizeFactor`; `CoverColumns = Math.Clamp((int)((w - RailWidth - 2 * Gutter) / (CoverMinWidth * sizeFactor)), 4, 12)` with `CoverMinWidth = 150`.
  - `static (RectI Bounds, double DpiScale) DisplayArea.MonitorForWindow(nint hwnd)` and `MonitorForCursor()` (monitor rect, not work area).
  - `OverlayContent.GameColumns { get; set; } = 3` used as the Grid item's `Columns`.
  - Removed: `OverlayLayout.Place`, `Width`, `Height`, `Margin`; `AppConfig.OverlaySide`, `OverlaySides`; Settings Position row and its strings.

- [ ] **Step 1: failing checks:** `Frame(1920, 1.35)` → `CoverColumns == 8`, `ColumnWidth == 768`; `Frame(2048, 1.0)` → `CoverColumns == 12`, `ColumnWidth == 614.4`; `Frame(1280, 1.0).CoverColumns == 7`; `Frame(800, 1.35).CoverColumns == 4` (clamp); `content.GameColumns = 8` → the Games grid item's `Columns == 8`; Settings has no row labelled with the old Position text (check by absence of a Segmented row with two options Left/Right is enough: `!settings.Any(r => r.Icon == Glyphs.Desktop && r.Kind == ItemKind.Segmented)`); a config JSON with `"overlay_side": "right"` loads and keeps it in `Extra` (no crash).
- [ ] **Step 2:** Run. Expected: FAIL / compile errors on the removed members (fix those call sites in Step 3).
- [ ] **Step 3:** Implement. `OverlayController` temporarily keeps compiling by placing the window at the monitor bounds (full wiring in Task 7).
- [ ] **Step 4:** Build + self-test. Expected: pass.
- [ ] **Step 5:** Commit `Overlay: full-monitor frame maths, drop side setting`.

### Task 7: Full-screen frame (window, backdrop, rail, column, stage, status corner)

**Files:**
- Modify: `src/OverlayUtils.App/Overlay/OverlayWindow.xaml(.cs)`, `OverlayViewModel.cs`, `OverlayController.cs`, `OverlayTheme.xaml`, `WindowEffects.cs`, both `Strings*.resx`

**Interfaces:**
- Consumes: `OverlayLayout.Frame`, `DisplayArea.MonitorForWindow/ForCursor`, `OverlayContent.GameColumns`, `OverlayTab.Tiles`, battery percent (Task 4).
- Produces (view model, used by Tasks 8-12):
  - `RailItems: IReadOnlyList<RailVm>` (`RailVm(string Glyph, string Title, bool IsCurrent, int VisibleIndex)`; Settings gear is separate: `bool SettingsCurrent` true on Settings / Info / Sub-from-Settings).
  - `Eyebrow: string` (default `AppPaths.AppName`), `PageTitle`, `Frame: OverlayFrame`, `OutputDevice: string`, `BatteryText: string` ("84%" or empty; glyph stays as `BatteryGlyph` when no percent), `Clock`.
  - Window events: `RailClicked(int visibleIndex)`, `SettingsClicked()`, `BackdropClicked()`.

- [ ] **Step 1:** Controller: `Show` takes `MonitorForWindow(_prev)` (or `ForCursor`), sizes the window to the monitor bounds, sets `_vm.Frame = OverlayLayout.Frame(bounds.Width / dpi, sizeFactor)` and `_content.GameColumns = frame.CoverColumns`; `Relayout` does the same on the window's monitor. Remove `PrepareOpening(side)`'s side parameter. Drop `ApplyChrome`'s rounded corners/border for this window (keep tool-window + dark mode).
- [ ] **Step 1b (frosted backdrop, from Task 14):** no DWM backdrop for this window (do not call `TryApplyAcrylic`; window background opaque `#080A10`). In `Show`, before the window is shown, `ScreenCapture.Capture(bounds)` on the UI thread (~25 ms; must run while our window is hidden); then `Task.Run(() => Frost.Process(...))` with `FrostSettings.FromConfig(cfg)`; back on the UI thread (if still open) set the backdrop `Image.Source` (`BitmapSource.Create`, Bgr32, frozen; `Stretch=Fill`, `BitmapScalingMode=HighQuality`) and fade it in 150 ms (instant when motion is off). Layers, bottom to top: image, white frost rectangle at `FrostSettings.FrostOpacity`, grain (128x128 tiled grey noise generated once, `Random(12345)`, `TileMode=Tile`, absolute viewport, `NearestNeighbor`) at `FrostSettings.GrainOpacity`, gradient with `FrostSettings.GradientAlphas`. Keep the last capture while open; when the Appearance settings change (`ConfigStore.Changed`), re-process from it so the sliders preview live. Drop the capture on hide. Capture null → plain background.
- [ ] **Step 2:** XAML: root `Grid` = backdrop layers (Step 1b) + three columns bound to `Frame` (rail, column, stage). Rail: one glyph button per `RailItems`, accent bar on the current one, gear at the bottom. Column: eyebrow (accent, uppercase, small), page title (large), rows (existing templates, restyled, scaled by `SizeFactor`), footer at the bottom. Stage: status corner top-right (output device, battery, clock in light weight) and a context host at the bottom (Task 9 fills it; for now it shows `Description`). The old tab strip, header and description line go.
- [ ] **Step 3:** Mouse: rail click → `ClickTab`, gear → `Handle(Settings)`, click on the backdrop element itself (not on rail/column/cards; check `e.OriginalSource == BackdropElement`) → `Hide()`.
- [ ] **Step 4:** Strings for the gear tooltip if one is shown (`Overlay.Rail.Settings`). Build: 0 warnings; self-test passes.
- [ ] **Step 5:** Commit `Overlay: full-screen frame`.
- [ ] **Step 6 (owner checkpoint):** ask the owner to open the overlay on the desk and the TV and confirm the frame, backdrop and readability before Tasks 8-12.

### Task 8: Home tiles

**Files:**
- Modify: `OverlayWindow.xaml(.cs)`, `OverlayViewModel.cs`

**Interfaces:**
- Consumes: `OverlayTab.Tiles`, `RowVm` (existing).
- Produces: `RowVm.IsWide` (true for `Slider` and `Media` kinds) and `OverlayViewModel.ShowHomeTiles`.

- [ ] **Step 1:** When the current tab has `Tiles`, the rows are drawn by a tile `ItemsControl` in a 2-column layout (wide tiles span both; a pair whose partner is hidden makes the lone tile take its half, the grid closes up). Sections are not drawn. Tile templates per kind: Slider (label = output device name from `OutputDevice`, big percent, bar, muted state), Media (title, artist · app, ⏮ ⏯ ⏭ clickable via existing `MediaClicked`), Choice (input: device + muted state), Segmented (Desk/TV or Open/Exit pills, clickable via existing segment clicks), Action (label + hint). Selected tile: accent outline + glow. Mouse hover/click reuse `RowHovered` / `RowClicked` with the row index.
- [ ] **Step 2:** Build: 0 warnings; self-test passes.
- [ ] **Step 3:** Commit `Overlay: Home tiles`.

### Task 9: Stage context

**Files:**
- Modify: `OverlayViewModel.cs`, `OverlayWindow.xaml(.cs)`, `OverlayContent.cs` (cap helper), `src/OverlayUtils.App/Audio/MediaSession.cs` (thumbnail), both `Strings*.resx`
- Test: self-test for `OutputCapPercent`

**Interfaces:**
- Produces:
  - `int? OverlayContent.OutputCapPercent()` → cap of the default output device from `AudioVolumeCaps`, `null` when missing or 100.
  - `ContextVm` with `Kind` ∈ {`Text`, `Monitors`, `Mixer`, `Media`, `Volume`}, plus `Title`, `Text`, `Monitors: IReadOnlyList<(string Name, bool Active)>`, `Bars: IReadOnlyList<(string Name, double Level, bool Muted, bool Selected)>`, `Apps: string`, `Art: ImageSource?`.
  - `ImageSource? MediaSession.Art` (thumbnail decoded at 300 px on a worker, replaced on track change) and its change raising the existing `Changed`.
  - Strings: `Overlay.Context.Cap` = "Your cap for this device is {0}%." / "Il limite per questo dispositivo è {0}%." (the app list is joined with ", ", no string).

- [ ] **Step 1: failing checks:** caps `{id: 80}` with default output `id` → `80`; `{id: 100}` → `null`; no entry → `null`.
- [ ] **Step 2:** Run. Expected: FAIL. Implement; build + self-test pass.
- [ ] **Step 3:** VM builds `ContextVm` from the selected item: key `controls.monitor` → `Monitors` (from Options labels + Active, or the Choice profiles); keys `audio.volume` / `ch.*` on the Audio page → `Mixer` (bars: master from `audio.volume`, one per `ch.*` row, selected outlined; `Apps` = labels of the `Child` rows under the selected channel); `audio.volume` on Home → `Volume` (device name, `Cap` text when `OutputCapPercent` is set); `audio.media` → `Media` (art large); anything else → `Text` (`Label` as title, `Description`, `Value` if any). Cards cross-fade (Task 12 adds the animation; here they just swap).
- [ ] **Step 4:** Build: 0 warnings. Commit `Overlay: stage context`.

### Task 10: Games page

**Files:**
- Modify: `OverlayWindow.xaml(.cs)`, `OverlayViewModel.cs`, `src/OverlayUtils.App/Games/GameArt.cs`

**Interfaces:**
- Consumes: `OverlayFrame.CoverColumns`, `OverlayContent.GameColumns` (Task 6).
- Produces: `TileVm.Ambient: Color?` (dominant colour, cached with the cover in `GameArt`); `OverlayViewModel.TileColumns` becomes an instance property = `Frame.CoverColumns` (and `ArtKeep` = 4 rows of that).

- [ ] **Step 1:** `GameArt`: when a cover is decoded, average a downscaled copy (e.g. 8x8 via `TransformedBitmap`, `CopyPixels`) into a `Color`, darkened so white text stays readable; cached per path with the cover in the same MRU.
- [ ] **Step 2:** Games layout: column + stage merge (full width after the rail). Hero at the top (selected tile's title large, source badge, Play hint), source pills row (current lit; click = existing filter command; the old header pill goes), shelf `UniformGrid Columns={TileColumns}`. Selected cover scale ~1.1 + glow; other rows Opacity ~0.55. Backdrop switches to the top-to-bottom gradient and a radial tint of `Ambient` of the selected tile. Grid navigation, memory, launch by id and `ShowArt` range logic unchanged.
- [ ] **Step 3:** Build: 0 warnings; self-test passes. Commit `Overlay: Games page`.

### Task 11: Current game in the eyebrow

**Files:**
- Modify: `OverlayController.cs`, `OverlayViewModel.cs`

**Interfaces:**
- Consumes: `ForegroundProcess.Describe`, `CurrentGame.Match`, `CurrentGame.PlayTime`, `IGameView.Games` (Task 3), `Overlay.Eyebrow.Game`.

- [ ] **Step 1:** On `Show`, after capturing `_prev`: `Task.Run` → `Describe(_prev)` → `Match(path, games)`; back on UI, if the overlay is still open for the same `_prev`, set the Home eyebrow to `Loc.T("Overlay.Eyebrow.Game", title, PlayTime(now - start))`; the clock tick refreshes the time. Other pages and no match: `AppPaths.AppName`. Any exception → `Log.Error`, eyebrow unchanged.
- [ ] **Step 2:** Build: 0 warnings; self-test passes. Commit `Overlay: current game`.

### Task 12: Motion

**Files:**
- Modify: `OverlayWindow.xaml(.cs)`, `OverlayController.cs` (passes the page-change direction)

**Interfaces:**
- Produces: `OverlayWindow.AnimatePage(int direction)` (-1 LT / rail click to a lower index, +1 RT / higher), `OverlayWindow.MotionEnabled` (reads `SystemParameters.ClientAreaAnimation` at each open).

- [ ] **Step 1:** Open: backdrop opacity 0→1 in 180 ms; rail then column `TranslateTransform.X` from -40 DIP and opacity 0→1, 30 ms stagger, ease-out cubic, total ≤ 220 ms. Close: reverse in 120 ms, then `Hide`.
- [ ] **Step 2:** One highlight element (rows and Home tiles) on a canvas above the list, moved to the selected container's bounds with a 140 ms ease-out on X/Y/Width/Height; on page change it jumps (no glide across pages). Grid covers keep their own lift (scale 1→1.1 in 120 ms).
- [ ] **Step 3:** Page change: old content opacity→0 and X by `-24 * direction` in 90 ms, new content from `+24 * direction` in 140 ms. Stage cards and Games tint cross-fade 150 ms.
- [ ] **Step 4:** A new command during any animation: `BeginAnimation(..., null)` after setting the final value (or `HandoffBehavior.SnapshotAndReplace` toward the new target) so state is always current; nothing awaits an animation before handling input. `MotionEnabled == false` → set final values directly.
- [ ] **Step 5:** Build: 0 warnings; self-test passes. Commit `Overlay: motion`.

### Task 13: Docs and owner test list

**Files:**
- Modify: `docs/PORTING.md`, `docs/WORKLOG.md`, `docs/ARCHITECTURE.md`, `README.txt`

- [ ] **Step 1:** PORTING: new section "Overlay redesign" with the done items and the manual test list from the spec's Testing section (desk + TV, each page, Games tint/lift, backdrop click closes, Animation effects off, Transparency effects off, borderless and exclusive-fullscreen game behind, current game for Steam / Epic / manual app, battery wireless vs wired, monitor switch from the Screen tile while open).
- [ ] **Step 2:** WORKLOG: decisions (full-screen Focus layout, Home, side setting dropped, LT/RT unchanged, Settings gear outside the cycle, backdrop spike result from Task 1) and a dated session line with the self-test count. ARCHITECTURE: overlay section (frame, Home, current game, install folders). README.txt (EN + IT): Home page and the removed side option.
- [ ] **Step 3:** Build + self-test. Commit `Overlay redesign docs`.

### Task 14: Frosted backdrop core and Appearance settings (runs after Task 6, before Task 7)

Added after the backdrop spikes (owner decision 2026-11-04, see spec "How it is built"): the DWM backdrop is replaced by a frosted screenshot, and the owner asked for sliders under an **Appearance** section in Settings.

**Files:**
- Create: `src/OverlayUtils.Core/Overlay/Frost.cs`, `src/OverlayUtils.Core/Platform/ScreenCapture.cs`
- Modify: `src/OverlayUtils.Core/Interop/Native.cs` (GDI: `GetDC`, `ReleaseDC`, `CreateCompatibleDC`, `DeleteDC`, `CreateDIBSection`, `SelectObject`, `DeleteObject`, `BitBlt`, `BITMAPINFOHEADER`), `AppConfig.cs`, `ConfigStore.cs` (clamp on load), `OverlayContent.cs` (Settings), both `Strings*.resx`
- Test: self-test, new section "Frost"

**Interfaces:**
- Produces:
  - Config (percent ints, JSON names): `overlay_blur` (default 50), `overlay_frost` (default 35), `overlay_dim` (default 50); `ConfigStore.Load` clamps each to 0..100.
  - `public sealed record FrostSettings(int BlurRadius, double Saturation, double Brightness, double FrostOpacity, double GrainOpacity, IReadOnlyList<byte> GradientAlphas)`; `static FrostSettings FrostSettings.FromConfig(AppConfig c)`: `BlurRadius = round(c.OverlayBlur / 100.0 * 64)`; `Saturation = 1.3`; `Brightness = 1.08`; `FrostOpacity = c.OverlayFrost / 100.0 * 0.20`; `GrainOpacity = 0.05`; `GradientAlphas` = `{0xE0, 0xB3, 0x59, 0x33}` each multiplied by `dim = 0.5 + c.OverlayDim / 100.0` and clamped to 255.
  - `public sealed record FrostImage(byte[] Bgr32, int Width, int Height)`; `static FrostImage? Frost.Process(byte[] bgra, int width, int height, int stride, FrostSettings s)`: 4x4 average downscale (`Width = width / 4`, `Height = height / 4`; null when either is 0), then 3 box-blur passes per axis with radius `s.BlurRadius / 4` (edge-clamped), then per pixel `c' = clamp((luma + (c - luma) * Saturation) * Brightness)`, `luma = 0.2126R + 0.7152G + 0.0722B`; alpha byte 255.
  - `public sealed record CapturedScreen(byte[] Bgra, int Width, int Height, int Stride)`; `static CapturedScreen? ScreenCapture.Capture(RectI bounds)` (GDI BitBlt SRCCOPY of the screen DC into a 32bpp top-down DIB section; every handle released in `finally`; any failure → null; memory only, never written to disk).
  - Settings: the Size row's section becomes `Overlay.Section.Appearance` ("Appearance" / "Aspetto"); after it three `Slider` rows: `Settings.Blur` ("Blur" / "Sfocatura"), `Settings.Frost` ("Frost" / "Brina"), `Settings.Darkness` ("Darkness" / "Oscurità"), each with a `.Desc` string ("How blurred the game behind the overlay is." / "Quanto è sfocato il gioco dietro l'overlay."; "How milky the frosted glass looks." / "Quanto è lattiginoso il vetro smerigliato."; "How dark the background is behind the text." / "Quanto è scuro lo sfondo dietro il testo."). `Value` = "{n}%", `IsOn` false, `Step(±1)` = ±5 clamped 0..100, `SetLevel(f)` = round(f * 100), `Toggle` (A) resets to the default, `ActionLabel` = `Overlay.Footer.Reset` ("Reset" / "Ripristina"). All writes through `ConfigStore.Update`.

- [ ] **Step 1: failing checks**
  - `FromConfig(new AppConfig())` → `BlurRadius == 32`, `Math.Abs(FrostOpacity - 0.07) < 1e-9`, `GradientAlphas` equals `{0xE0, 0xB3, 0x59, 0x33}`; `OverlayDim = 100` → first alpha `255` (clamped); `OverlayBlur = 0` → `BlurRadius == 0`.
  - A config JSON with `"overlay_blur": 400, "overlay_frost": -3` loads as `100` and `0`.
  - `Frost.Process` on an 8x8 uniform grey (128,128,128) image with defaults → 2x2, every pixel grey (R == G == B) and `== clamp(128 * 1.08)` within ±1.
  - On a 16x4 image, left half pure red, right half pure blue, `BlurRadius = 0`, `Saturation = 1`, `Brightness = 1` → 4x1, pixel 0 red (R 255, B 0), pixel 3 blue.
  - Same image with `BlurRadius = 16` → pixels 1 and 2 both have R > 0 and B > 0 (mixed).
  - A muted colour (R 150, G 120, B 120), `Saturation = 1.3`, no blur → R - G larger than 30 (boosted).
  - `Process` on a 3x3 image → null.
  - Settings: rows labelled `Loc.T("Settings.Blur")`, `"Settings.Frost"`, `"Settings.Darkness"` exist, are `Slider`, and the Size row's `Section()` is `Loc.T("Overlay.Section.Appearance")`; `Step(1)` on Blur moves `overlay_blur` 50 → 55; `SetLevel(0.2)` → 20; `Toggle()` → back to 50; `Step(1)` at 100 stays 100.
- [ ] **Step 2:** Run. Expected: FAIL (types/keys missing).
- [ ] **Step 3:** Implement. `ScreenCapture` is not unit-tested (needs a screen); keep it small, never throwing.
- [ ] **Step 4:** Build + self-test. Expected: 0 warnings, all pass.
- [ ] **Step 5:** Commit `Overlay: frosted backdrop core, Appearance settings`.
