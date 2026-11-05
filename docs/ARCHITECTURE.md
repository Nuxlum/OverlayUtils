# Architecture

How the .NET OverlayUtils is put together. What is ported so far is in
`docs/PORTING.md`; for features not ported yet, the Python reference and
`docs/PYTHON-CODEMAP.md` describe the intended behaviour.

## Projects

```
OverlayUtils.sln
├─ src/OverlayUtils.Core       net10.0, no NuGet. Config, localization, Win32 interop,
│                              services. Builds on any OS; runs on Windows.
├─ src/OverlayUtils.App        net10.0-windows10.0.19041.0, WPF + WinForms. AppHost
│                              (startup), tray, mode switching, overlay, media session.
├─ src/OverlayUtils            Launcher: OverlayUtils.exe          (uiAccess=false)
├─ src/OverlayUtils.UIAccess   Launcher: OverlayUtils.UIAccess.exe (uiAccess=true)
│                              (both launchers also target net10.0-windows10.0.19041.0)
└─ tests/OverlayUtils.SelfTest Console checks for Core, no test framework
```

Rule of thumb: if it can be written without WPF/WinForms, it goes in Core,
where the self-test can reach it.

## Two modes

Windows reads `uiAccess` from the exe manifest before any code runs, and
refuses to start a `uiAccess="true"` exe unless it is (1) signed with a
trusted certificate, (2) in a protected folder such as Program Files and
(3) started through ShellExecute. So there are two launchers over the same
code; `AppHost.Run(args, LauncherKind)` is the shared entry point.

| | Standard | Full |
|---|---|---|
| Exe | `OverlayUtils.exe` | `OverlayUtils.UIAccess.exe` |
| Location | anywhere | `%ProgramFiles%\OverlayUtils` |
| Certificate | none | trusted on the PC (added by the app) |
| Overlay over fullscreen games | no | yes |
| Input to elevated windows | no (UIPI) | yes |
| Admin at runtime | no | no |

`TrayController.InFullMode` = launched as UIAccess **and** the token really
has UIAccess (`ProcessInfo.HasUIAccess`).

### Switching (`ModeSwitcher` + `FullModeInstaller`)

Admin-only work runs in a short-lived elevated copy of the standard exe
(`OverlayUtils.exe --helper <verb>`), which exits with a `HelperResult` code.
Per-user work (Run key, launching) stays in the unelevated app so it applies
to the right account even when a different admin account approves UAC.

```
Enable (standard instance)                     Disable (full instance)
  runas: --helper enable-full-mode --source      copy app -> %LOCALAPPDATA%\Programs\OverlayUtils
     trust signer cert (only if untrusted,       start that copy with
       self-signed and code-signing)               --after-switch <pid> --finish-disable-full-mode
     copy app -> Program Files (staged swap)     exit
     remove old AppUtili install               new standard instance:
  re-point Run key to the UIAccess exe           wait for <pid>, re-point Run key
  ShellExecute UIAccess exe --after-switch <pid> runas: --helper disable-full-mode
  exit                                              remove cert we added + Program Files folder
```

`full-mode.json` in the install folder remembers which certificate we added,
so disabling never removes one we did not add.

## Startup sequence (`AppHost.Run`)

1. Parse args. `--helper` -> run the elevated step and exit.
2. `--after-switch <pid>` -> wait for the previous instance to exit.
3. Single-instance mutex (`Local\OverlayUtils.SingleInstance`) shared by both exes.
4. Migrate `%APPDATA%\AppUtili` if needed, load config, apply language.
5. WPF `Application` (explicit shutdown) + `TrayController.Start`.

## Threads

| Thread | Owner | Notes |
|---|---|---|
| UI (STA) | WPF `Dispatcher` | tray, menus, dialogs, the overlay window |
| Hotkeys | `HotkeyService` | own message loop; `RegisterHotKey` must be called on it, so requests are queued and the thread is woken with `PostThreadMessage` |
| Workers | `Task.Run` | MonitorSwitcher search, mode switching (waits on UAC), overlay actions after hide |
| Audio | `AudioService` | own MTA thread; every Core Audio COM call runs there (see Audio) |
| Pads | `PadService` | all SDL3 calls; actions go to the overlay via `OverlayController.Command`/`Toggle`, which marshal to the UI thread |

Rule: anything that touches UI goes through `TrayController.OnUi`. Hotkey callbacks arrive on the hotkey thread.

## Overlay

Split like the rest: everything that can run without WPF is in Core and
covered by the self-test.

| Core (`Overlay/`, `Platform/`) | App (`Overlay/`) |
|---|---|
| `OverlayContent`: tabs and rows (Home, Controls, Audio, Games, Settings, Info) | `OverlayController`: open / close, focus, placement, runs effects |
| `OverlayNavigator`: selection, pages (incl. Home tiles and Games), expanders, Power confirm | `OverlayWindow` + `OverlayViewModel`: drawing, key and mouse input |
| `OverlayLayout`: size factor and `Frame` (rail, column, gutter, cover columns) | `OverlayTheme.xaml`: full-screen frame, frosted backdrop, motion; `WindowEffects`: tool window, dark mode, square corners |
| `IOverlayHost` (implemented by `TrayController`) | |
| `WindowFocus`, `DisplayArea`, `PowerActions`, `ShellOpen`, `AccentPalette` | |

### Effect model

The navigator never runs callbacks. Each command (Up, Enter, Click...) returns
a `NavResult`: what to redraw, plus an optional effect.
- `RunHere`: run now on the UI thread (settings changes, language switch).
- `HideAndRun`: the controller hides the overlay, gives focus back to the
  previous window, waits 150 ms on a worker (`Task.Run`) and then runs the
  action (Big Picture, Show desktop, Close window, Power, full-mode switch,
  Exit, opening a folder or site from Info).

The overlay itself lives on the UI thread. `OverlayController` runs a 250 ms
timer that calls `ExpirePending`, so a Power option armed with Enter or a click
disarms after 3 s. The second press of a double-click is ignored, so it cannot
fire a confirm the first press armed.

### Focus

On open the controller remembers the foreground window (`_prev`, from
`WindowFocus.ForegroundApp`): the root of the foreground window, or none when
that is a shell window (taskbar, desktop, tray overflow: the class names in
`ShellWindows`). Windows of our own process are not skipped. It then takes
foreground with `WindowFocus.Force`, checks focus every 150 ms and hides when
it is lost. Esc gives focus back to `_prev` if it is still alive. If the
overlay never gets focus it stays open until toggled (as in Python). Close
window sends Alt+F4 only when, after the 150 ms delay, `_prev` is alive and is
the foreground app again (`PreviousWindowIsForeground`); otherwise it does
nothing (opened over the taskbar or desktop, or another window took over).

The tray icon decides open or close on the mouse press: the press makes the
taskbar foreground and the focus watcher hides the overlay before the release,
so a toggle on the release would open it again.

### Frame (full screen)

The window covers the whole monitor (monitor bounds, taskbar included) and is
opaque, not layered. Three zones: the **rail** on the left (one icon per page,
Settings as a gear at the bottom, outside the LT / RT page cycle), the
**column** (eyebrow, page title, rows, footer hints) and the **stage** on the
right (status corner: output device, pad battery as a percent, clock; and the
context card). `OverlayLayout.SizeFactor` maps `overlay_size` (Auto looks at
the active monitor profile) to a factor; `OverlayLayout.Frame` turns the
monitor width (DIPs) and the factor into rail, column, gutter and cover
columns. Per-monitor DPI aware, follows `WM_DPICHANGED` and, for a resolution
change while open, `WM_DISPLAYCHANGE`. The window covers the monitor of the
foreground window (`DisplayArea.MonitorForWindow`, `MonitorForCursor` as
fallback). When a setting changes while it is open
(size, Appearance sliders, monitor profile) the controller recomputes layout
and, for the sliders, re-processes the backdrop. The old left / right side
setting is gone; an old `overlay_side` just lands in `Extra`.

`OverlayNavigator` still owns selection and commands. **Home** is a tab whose
rows are the same `OverlayItem`s as the Controls / Audio rows they mirror
(Volume, Now playing, Microphone, Screen, Close window, Steam Big Picture),
drawn as tiles in a 2-column grid with wide tiles; an item that is not visible
is left out. **Stage context** per selected row: the description card by
default, the Desk / TV monitor picture, the audio mixer bars, large album art.
The **Games page** uses column and stage together: hero, source filter pills
(Y cycles; X is play / pause), cover shelf with lift on the selected cover, and a backdrop tint
from the selected art's dominant colour (taken when the cover is decoded).

### Frosted backdrop

On open the monitor is captured once (GDI `BitBlt`, in memory only, never
saved) on the UI thread in `Show`, while the window is still hidden (so the
image is of the game); only the processing runs on a worker. The window
shows at once with opaque #080A10 and the UI; `Frost` (Core, from `overlay_blur` / `overlay_frost` / `overlay_dim`) is
processed off the UI thread (1/4 downscale, 3-pass box blur, saturation 1.3,
brightness x1.08) and the image fades in over 180 ms when ready (measured at
2560x1440: capture ~25 ms, processing ~20 ms). Layers on top: white frost,
static grain, dark gradient. The UI thread only receives the finished bitmap.
Capture failed or black (exclusive fullscreen, protected content) keeps the
plain dark background. The Appearance sliders preview live. A click on the
backdrop (not on the rail, column, stage cards or pill gaps) closes the
overlay on mouse-up.

### Current game

On open, in `Task.Run`, the foreground window's exe path
(`QueryFullProcessImageName`, limited rights, read-only: no injection, no
hooks) is matched against the install folders of the games library
(`CurrentGame`; longest folder wins, case-insensitive). `GameEntry.InstallDir`
is filled per source: Steam `installdir` under its library folder, Epic
`InstallLocation`, GOG `path`, Xbox `InstalledPath`, emulator and manual-app
exe folders. A match shows "<Title> · <time running>" in Home's eyebrow
(session time from the process start); any failure shows the app name.

### Motion

Under 250 ms, all skipped when Windows Animation effects is off
(`SystemParameters.ClientAreaAnimation`). One highlight glides between
rows / tiles (drawn behind the list), open slides rail and column in, a page
change fades out in 90 ms then in over 140 ms, and a new command completes
the running animation at once. Closing fades the frame, not the backdrop.

### Look

- Accent: read from the Windows accent palette (`AccentPalette`). It tries the
  light variant (palette entry 1), then the lightest entry (entry 0), each
  needing a contrast of at least 4.5:1 against the panel (#202020), else
  #4CC2FF. The selection must always be visible.
- Animation effects off in Windows -> no motion. Transparency effects do not
  matter any more (the backdrop is our own image, not DWM acrylic, which was
  tried and rejected).

### Hotkey

Hotkey id 2 (reserved in stage 1) is now Shift+F1 via `RegisterHotKey`; a left
click on the tray icon does the same. Both call `OverlayController.Toggle`.

## Input: pad

- `PadInterpreter` (Core, pure, self-tested): button/axis events plus a clock
  in, `PadAction`s out. Back+RB armed only when exactly those two are held,
  fires on release; Back+D-pad Up/Down (overlay closed, `controller_enabled`)
  switch profiles; while the overlay is open D-pad/stick navigate with repeat
  (0.4 s, then 0.12 s), A/B, LT/RT tabs, Back held alone 0.6 s opens Settings
  (that hold is consumed). Triggers and stick: pressed above 16000, released
  below 8000. Guide is ignored; nothing uses Start+Back+LB+RB (Moonlight).
- `PadService` (Core): SDL3 on its own thread (`SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS`
  so pads work while a game has focus), hot-plug, battery every 30 s.
- `Sdl` resolves `SDL3.dll` only from `AppContext.BaseDirectory`; missing DLL =
  one log line, pads off. The DLL is vendored in `third_party/SDL3` (version
  and hash in its README) and copied to the output by the App project.
- `TrayController` maps actions; `OverlayController.OpenChanged` keeps the
  interpreter's `OverlayOpen` in sync; `SetPadState` drives the pad footer and
  battery glyph.

## Audio

Stage 4a. Pure rules and COM in Core, the media session in App.

| Core (`Audio/`, `Interop/CoreAudio.cs`) | App (`Audio/`, `Overlay/`) |
|---|---|
| `AudioRules`: target device, merge / seed of the priority lists, volume step, cap | `AudioServiceView`: adapts `AudioService` to `IAudioView` for the overlay |
| `AudioService`: devices, default switching, volume, mute, mic mute | `MediaSession`: now playing and transport via GlobalSystemMediaTransportControls |
| `IAudioView`, `IMediaControl` / `NowPlaying`: what the overlay sees | `VolumeIndicator`: small non-activating topmost window |

- **Target framework.** The media session is a WinRT API, so App and both
  launchers target `net10.0-windows10.0.19041.0` (the Windows SDK projection;
  the debug output folder changed with it). Core stays plain `net10.0`; Core
  Audio is COM through `GeneratedComInterface` and `LibraryImport` (no NuGet),
  declarations in `Interop/CoreAudio.cs`.
- **Service thread.** `AudioService` runs one MTA thread with a command
  queue. Windows notifications (any thread) only queue work; bursts of device
  events are settled for 250 ms and handled once. Public members read a cached
  snapshot or queue a command, so they are non-blocking from any thread; errors
  are logged, never thrown. `Changed` is raised for any change, including ones
  made elsewhere (Windows flyout), and the tray refreshes the open overlay.
  It runs for the whole session, overlay open or not.
- **Default device.** Set through `IPolicyConfig` (undocumented but what every
  audio switcher uses), for all three roles (console, multimedia,
  communications).
- **Priority lists.** `audio_output_order` / `audio_input_order` hold every
  device seen (id and last known name), new ones at the bottom. When a device
  is added, removed, enabled or disabled, and `audio_auto_output` /
  `audio_auto_input` is on, the default becomes the highest-ranked available
  device (`AudioRules.Target`). A manual pick from the Audio tab holds until
  the next device event. Re-enabling auto-switch waits for that next event.
- **Volume cap.** `audio_volume_caps` (device id -> %, default 100). Applied
  when the volume is stepped from the overlay or pad and when the default
  output changes; not at startup and not to Windows' own volume keys. A new cap
  applies at the next step or device change.
- **Pad.** With the overlay closed and Pad combos on, Back+D-pad Left/Right
  step the volume (repeat) and Back+Y toggles the microphone mute, both via
  `PadInterpreter`; the tray shows `VolumeIndicator` on the foreground
  window's monitor for about 1.5 s. With the overlay open LB / RB / X are
  previous / next / play-pause. List reordering in Settings uses the `Reorder`
  item kind: A picks up, Up/Down move, A drops, B cancels;
  `OverlayCommand.Remove` (Delete, or Y on the pad) removes a disconnected
  entry.
- **Tabs.** Audio is a tab after Controls and can be hidden in Settings
  (`hidden_tabs`); the change applies to the open overlay at once.

### Mixing

Stage 4b. Channels group programs; every Windows audio session of a program
gets channel volume x app volume.

| Core | App |
|---|---|
| `MixRules` (pure): default channels, `Classify` / `ChannelOf` / `ChannelFor`, `Effective`, `MixFor`, `AppLevel`, `DeleteChannel`, `NextCustomId`, `ApplyPreset`, `GroupByProgram` | `TrayController.AskName`: modeless `NameDialog`, owned by the overlay |
| `MixController`: every mix / channel / preset / program edit (one `ConfigStore.Update`, then `IAudioView.ApplyMix`) | `TrayController.ChooseProgram`: `OpenFileDialog` for an .exe, owned by the overlay |
| `AudioService` (`AudioService.Sessions.cs`): sessions of the default output, apply rules | `OverlayWindow`: `Child` rows indented 24 DIP with a smaller label |
| `OverlayContent`: Channels / Presets rows in the Audio tab, Settings -> Audio -> Channels and Programs | |

- **Programs.** Identified by full exe path, case-insensitive. Sessions are
  grouped per program (`GroupByProgram`); our own process, pid 0 and the
  system sounds session are excluded. A program seen for the first time is
  stored in `audio_programs` with `MixRules.ChannelOf` (Discord & co. -> chat,
  browsers and players -> media, game library folders or fullscreen
  foreground -> game, else other); a stored choice always wins and custom
  channels never get programs automatically. A stored id that no longer
  exists counts as `other` (`ChannelFor`).
- **Apply rules.** The service writes a session's volume (`Effective`,
  clamped) and its mute flag (channel or app muted); muting never changes
  the stored or the session volume. Only sessions whose app or channel has a
  stored level on the current device are written (`MixRules.ShouldWrite`):
  no entry means hands off, so a program the user never set in the overlay
  keeps the volume and mute Windows has for it, on every device and after
  every edit of other rows. Any level the user sets is an entry (even back
  to 100 % unmuted), so undoing a mute is written. A session written once
  keeps being written while it lives (moving its program to an untouched
  channel resets it to that channel's 100 %). When it writes: to every
  session of a program when that program gets a new session; to all live
  sessions when the default output changes (that device's mix) and on
  `ApplyMix()` (after every overlay edit). Never at startup: apps already
  playing keep their level until their row or their channel's row is
  touched, or the output changes to a device with an entry for them.
  Changes made in the Windows mixer stay until then too.
- **Per device.** `audio_mix` is keyed by output device id. A device seen for
  the first time gets a copy of the previous device's entries (`MixFor`,
  stored by `EnsureMix`), or an empty mix when there is none: untouched
  channels and apps stay without entries (shown as 100 %, not written). Presets store channel levels only and apply to the current
  device; ids missing from the channel list are ignored.
- **Channels.** `game`, `chat`, `media`, `other` are fixed ids (renamable,
  re-iconable, not deletable); custom ones are `c1`, `c2`, ... Deleting one
  moves its programs to `other` and removes its id from every device mix and
  preset. A default channel whose stored name is still the English default
  is shown in the UI language (`OverlayContent.ChannelName`); accepting the
  suggested name in the dialog keeps it that way.
- **Threads.** `MixController` runs on the UI thread (overlay actions) and
  only edits config; `ApplyMix` queues the writes to the audio thread. Session
  notifications (COM threads) only queue a rescan. `AskName` / `ChooseProgram`
  are opened with `Dispatcher.BeginInvoke`, after the navigator action has
  returned; the name dialog is modeless (no nested loop) and its callback
  runs on the UI thread only for OK with a non-empty trimmed name. Each
  caller passes a title; a new request replaces an open prompt. While it is
  open, `TrayController.OnPadAction` routes pad commands to it on the UI
  thread (`DialogPad.Route`: A accept, B cancel, rest swallowed). Hiding the
  overlay closes an open name dialog.
- **Footer.** `OverlayItem.ActionLabel` names the A action when the kind's
  default does not fit ("Apply" on Presets, "Rename" on Rename...).
- **Auto-switch circuit breaker.** `SwitchBreaker` (pure, per direction):
  at most 3 automatic default switches within 10 s; the 4th trips it (one
  `Log.Info`), and auto-switch for that direction stays paused until a device
  event arrives after 10 s without events.
- **Config keys.** `audio_channels` (list of `{id, name, icon}` in display
  order), `audio_programs` (exe -> channel id), `audio_mix` (device id ->
  `{channels: {id: {volume, muted}}, apps: {exe: {volume, muted}}}`),
  `audio_presets` (list of `{name, channels}`). Null collections in the file
  load as empty.

## Games

Stage 5. Readers and rules in Core (`Games/`), Xbox and the service in App.

| Core | App (`Games/`, `Overlay/`) |
|---|---|
| `SteamLibrary` (libraryfolders, appmanifests, shortcuts.vdf via `Vdf`), `EpicLibrary`, `GogLibrary`, `EmulatorLibrary`, `ManualApps` | `XboxLibrary`: user packages with a MicrosoftGame.config (WinRT `PackageManager`) |
| `IRegistryView` (read-only; faked in the self-test) | `GameService : IGameView`: reads on a worker at start and on every overlay open |
| `GameLibrary`: merge (hidden sources, same id once), sort (last played, title), filter cycle | `GameArt`: covers / icons decoded small, frozen, cached by path |
| `GameEntry`, `IGameView` | `TileVm`, `TileList` in `OverlayWindow` |

- **Grid row.** `ItemKind.Grid` gives the navigator a tile cursor: Left/Right
  step through tiles across rows, Up/Down keep the column, A (or a click)
  returns `HideAndRun` with `Launch(tile)`. `Refresh` keeps the selected tile
  by game id. The Remove command runs the row's `AltAction` (the source
  filter) when it has one.
- **Drawing.** The grid row draws only its header (filter pill, count); the
  tiles are a separate `ItemsControl` under the rows, bound to tile objects
  that survive redraws while the game list is the same.
- **Art.** `OverlayWindow.UpdateArtRange` (on scroll and when the tiles
  change) asks `OverlayViewModel.ShowArt` for the rows in view ± one; covers
  are decoded on a worker at 200 px and kept in `GameArt`'s 120-entry
  most-recently-used cache; tiles four rows away release theirs.
- **Reads.** At start, on overlay open when the last read is older than 30 s
  (`GameService.RefreshIfStale`), at once on a Settings → Games change.
  `Loading` is true until the first read ends. Xbox ids are the package
  family name; the launch target is the AUMID.
- **Launch.** Through the shell (GOG `launchParam` as arguments): `steam://rungameid/`, the Epic launcher URI,
  `shell:AppsFolder\<AUMID>`, or the exe with its working folder.
- **Config keys.** `games_sources_hidden` (lowercase source names, not
  read at all when hidden), `games_manual_apps` (exe paths).

## Configuration

`ConfigStore` owns `%APPDATA%\OverlayUtils\config.json`:
- property names match the Python file, so migrated configs load unchanged;
- unknown keys are kept (`[JsonExtensionData]`), except `spotify_*` /
  `discord_*` (dropped features; the Discord client secret goes with them),
  removed on load by `ConfigStore.DropRemovedFeatures`;
- overlay keys: `overlay_size` (`normal` / `large` / `auto`; auto picks by the
  active monitor profile) `overlay_blur` / `overlay_frost` / `overlay_dim` (Appearance sliders,
  0-100, defaults 50 / 35 / 50);
- `Update(change)` saves atomically (temp file + move) and raises `Changed`;
- a corrupt file is backed up as `config.json.bad` and defaults are used.

## Localization

`Strings.resx` (English, neutral) and `Strings.it.resx`. Keys are dotted:
`Menu.*`, `Notify.*`, `Dialog.*`, `Tooltip.*`, `Language.*`, `Overlay.*` (rows,
footer, descriptions) and `Settings.*`.
- Code: `Loc.T("Menu.Exit")`, `Loc.T("Tooltip.Monitor", name)`.
- XAML: `{Binding [Menu.Exit], Source={x:Static loc:Loc.Instance}}`;
  bindings refresh when `Loc.SetLanguage` runs.
- The self-test fails if a key is missing in either language, if placeholders
  differ, or if source code uses a key that does not exist.

## Files at runtime

| Path | Content |
|---|---|
| `%APPDATA%\OverlayUtils\config.json` | settings |
| `%APPDATA%\OverlayUtils\errors.log` | `Log.Error` / `Log.Info`, restarts past 256 KB |
| `%ProgramFiles%\OverlayUtils\` | full-mode install + `full-mode.json` |
| `%LOCALAPPDATA%\Programs\OverlayUtils\` | where the app moves when leaving full mode |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\OverlayUtils` | start-up entry |

## Build

`scripts\build.ps1`: self-test, then `dotnet publish` both launchers
self-contained (win-x64) into `dist\OverlayUtils`, sign both exes with the
certificate from `scripts\certificate.ps1`, zip with `README.txt`. No
single-file publish: it extracts native libraries to a user-writable temp
folder at every start, which a UIAccess process must not load from.
