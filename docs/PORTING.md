# Porting plan: Python AppUtili -> .NET OverlayUtils

The Python app in `reference/python/` is the behaviour spec until the .NET
version matches it. It is frozen: fix bugs only in .NET. Use
`docs/PYTHON-CODEMAP.md` to find the Python code for a feature (section
names below match the banners in `app.py`).

## Decisions

| Topic | Decision |
|---|---|
| Stack | C# / .NET 10, WPF for the overlay, WinForms `NotifyIcon` for the tray |
| Name | OverlayUtils everywhere (exe, `%APPDATA%`, Program Files, cert subject) |
| Languages | English + Italian via `.resx`; follows Windows, switchable in the tray menu |
| Modes | Standard (`OverlayUtils.exe`, no cert) and Full (`OverlayUtils.UIAccess.exe`, signed, Program Files), switched from inside the app |
| Admin | Neither mode runs elevated; only the short-lived setup helper does |
| Start-up | `HKCU\...\Run` (UIAccess exe via `ShellExec_RunDLL`) instead of an elevated scheduled task |
| Migration | `%APPDATA%\AppUtili` copied on first run; old install removed when enabling full mode or from the tray menu |
| Dependencies | Core has no NuGet packages; App will need a few (SDL3 bindings, maybe NAudio) in later stages |

## Stages

Status: `[x]` done, `[~]` written but not yet tested on Windows, `[ ]` to do.

### Stage 1 - tray, hotkeys, modes  `[~]`
Builds on Windows with 0 warnings and the self-test passes; the manual
tests below are still to do.
- [~] Solution layout, two launchers, self-contained build, signing script
- [~] Config compatible with the Python `config.json` (unknown keys preserved)
- [~] Migration from `%APPDATA%\AppUtili`; detection and removal of the old install
- [~] Localization EN/IT, auto from Windows, switch in the tray menu
- [~] Tray icon (profile colour + label), dark/light menu, notifications
- [~] Monitor profiles: list, roles (guessed or chosen), switch, MonitorSwitcher discovery  *(Python: MONITOR SETUP)*
- [~] Home key remap (Windows / Alt+Tab / none)  *(MAPPA TASTO HOME)*
- [~] Ctrl+Alt+Down/Up hotkeys (now RegisterHotKey, so the keys are no longer passed to the foreground app)
- [~] Start with Windows  *(AVVIO CON WINDOWS)*
- [~] Enable / disable full mode from the tray

### Stage 2 - overlay shell  `[~]`
Written in 8 tasks; builds with 0 warnings and the self-test passes. `[x]` = checked by hand on Windows, `[~]` = written, rest of
the manual tests below still to do.
- [~] WPF overlay window: borderless, topmost, per-monitor DPI, on the foreground window's monitor, size Normal / Large / Auto and side left / right, acrylic + Windows accent  *(OVERLAY GAMEPAD: `_Overlay.show`, `_monitor_of`)*
- [~] Focus handling: take foreground, remember previous window, auto-hide on focus loss, run actions after focus returns
- [~] Shift+F1 and tray click toggle (hotkey id 2)
- [~] Keyboard and mouse navigation; Settings and Info pages
- [~] Controls tab: monitor, Big Picture, power, show desktop, close window  *(OVERLAY_ITEMS)*
- [~] Verify the UIAccess z-order quirk (`_repair_frame` in Python) does not apply to WPF
- [~] Checked by a scripted test run on Windows, still needs the owner's check: Shift+F1 opens / closes on the foreground window's monitor; Esc gives focus back; Close window reaches the previous app and does nothing over the desktop; the Power confirm expires after 3 s; F2 / Esc page navigation; Size and Position settings; Exit from the overlay

### Stage 3 - gamepad  `[~]`
Design: `docs/superpowers/specs/2026-11-03-gamepad-design.md`. Builds with 0
warnings, self-test passes; nothing tested with a real pad yet.
- [~] SDL3 gamepad input (vendored SDL3.dll, loaded from the app folder only), hot-plug  *(CONTROLLER)*
- [~] Back+RB toggles the overlay on release; Back+D-pad Up/Down switch TV/Desk profile (Python's other combos dropped, see WORKLOG)
- [~] Overlay navigation with repeat, LT/RT tabs, hold Back for Settings
- [~] Pad battery for any pad that reports it; Pad combos switch in Settings

### Stage 4 - audio and media  `[~]`
Design: `docs/superpowers/specs/2026-11-03-audio-design.md`, built in two
parts. Parts 4a and 4b build with 0 warnings and the self-test passes; nothing
tested with real devices yet.

#### 4a - devices  `[~]`
- [~] Audio tab: Output and Input pickers, Volume, Now playing  *(VOLUME..., USCITA AUDIO, MEDIA IN RIPRODUZIONE)*
- [~] Core Audio service: devices, default switching on all roles (IPolicyConfig), master volume and mute, microphone mute, live updates
- [~] Priority lists for outputs and inputs, auto-switch on device changes, reorder / remove from the pad, in Settings
- [~] Max volume per output device (Settings), applied from the overlay / pad and when the output changes
- [~] Pad: Back+D-pad Left/Right volume and Back+Y mic mute with the overlay closed, volume indicator; LB / RB / X media buttons with the overlay open
- [~] Now playing from the Windows media session (GlobalSystemMediaTransportControls), no Spotify-specific code
- [~] Overlay entry in the tray menu

#### 4b - mixing  `[~]`
Plan: `docs/superpowers/plans/2026-11-04-audio-mixing.md`.
- [~] Channels (Game, Chat, Media, Other) with per-app volumes, automatic assignment, Programs page (+ "Add program..." file picker)
- [~] Custom channels (add, rename, icon, order, delete), mix per output device, presets; name dialog owned by the overlay
- [~] Auto-switch circuit breaker (3 switches per direction in 10 s, then paused until a quiet device event)
- [~] Sonar: no integration planned; its virtual devices are ordinary entries in the priority lists

### Stage 5 - games library  `[~]`
Design: `docs/superpowers/specs/2026-11-04-games-design.md`, plan:
`docs/superpowers/plans/2026-11-04-games.md`. Builds with 0 warnings, the
self-test passes, and a read-only check found Steam, Epic, GOG and Xbox games
on the dev PC. No game has been launched from the overlay yet.
- [~] Steam: installed games in every library, non-Steam shortcuts (binary VDF), cover art, last played order  *(LIBRERIA GIOCHI)*
- [~] Epic, GOG, Xbox / PC Game Pass (MicrosoftGame.config packages), installed emulators (one tile each), manually added apps
- [~] Games tab: 3-column cover grid, Y / Delete (or the filter pill) cycles the source filter, mouse hover / click, empty state
- [~] Settings → Games: a switch per source, Manage apps page (Add app…, Y removes)

### Stage 6 - Spotify  `[ ]`
- [ ] PKCE login, token refresh (reuse migrated `spotify_token.json`), recent / playlists, play, shuffle  *(SPOTIFY)*

### Stage 7 - Discord  `[ ]`
- [ ] Named-pipe RPC client, OAuth, voice channels, mute/deafen, go-live hotkey, favourites  *(DISCORD)*

### After parity
- [ ] Delete `reference/python/`
- [ ] Consider a CA-issued signing certificate (removes the "trust this certificate" step)

## Testing on Windows (stage 1)

1. `scripts\certificate.ps1` once (no admin), then `scripts\build.ps1`.
2. Run `dist\OverlayUtils\OverlayUtils.exe` from the dist folder: standard mode.
   Check tray menu, language switch, Home key, Ctrl+Alt+Up/Down, start-up.
3. With an old AppUtili installed: settings imported, "Remove the old AppUtili" works.
4. Tray -> Overlay over fullscreen games -> Enable: UAC once, app restarts from
   Program Files, tooltip says "Full mode". Then Disable: back to
   `%LOCALAPPDATA%\Programs\OverlayUtils`, Program Files folder and certificate gone.
5. Logs: `%APPDATA%\OverlayUtils\errors.log`.

## Testing on Windows (mouse)

1. Open the overlay under a still mouse pointer: the first row stays
   selected. Move the mouse over the rows: the selection, description and
   footer follow it; rows, tabs, arrows and buttons show a hand cursor.
2. Use the pad or keyboard while the pointer rests on a row: the selection
   follows the pad/keyboard and stays there until the mouse moves again.
3. Click Power -> Shut down once ("Confirm?"), move the mouse to another row
   and back: it is no longer armed; a single click only arms it again
   (do not confirm unless you mean it). A double-click never confirms.
4. Click each tab; from Settings, click a tab: back on that tab.
5. Click the < > arrows of Output/Input, the segmented options, a toggle.
6. Volume bar: click a point, drag past both ends (stops at 0 and at the
   device's max volume), use the wheel over it.
7. Media buttons previous / play-pause / next; footer hints act like their
   key (Esc/B, Enter/A, F2/Back); right click goes back.
8. While moving a device in Output order, clicking tabs or hovering other
   rows does nothing; Enter/B still drop/cancel.

## Testing on Windows (stage 4a)

Needs a real headset, a second output and a pad. Auto-switch is on by default,
so the first tests change the default devices on purpose.
1. Headset on / off with Settings -> Audio auto-switch on: Windows' default
   output follows the highest-ranked connected device (headset first when it
   is ranked first). Repeat for the microphone.
2. With SteelSeries Sonar installed: its virtual devices ranked first, then
   last. First, Sonar's device wins while it runs; last, the real device wins.
3. Pick an output from the Audio tab, then plug in another device: the pick
   holds until that device change, then the ranking decides again. Turning
   auto-switch off and on again changes nothing until the next device event.
4. Settings -> Audio lists: select a device, A picks it up, Up/Down move it,
   A drops it, B cancels. A disconnected device is removed with Y on the pad
   (Delete on the keyboard).
5. Max volume on the headphones set to 40 %: switch to them from the Audio
   tab; the volume drops to 40 % and Right stops there. A new, lower cap
   applies at the next step or device change. Windows' own volume keys are
   not limited.
6. Overlay closed, a game in front: Back+D-pad Left/Right lowers / raises the
   volume (repeat while held) and the indicator shows over the game on the
   monitor of the foreground window for about 1.5 s.
7. Back+Y toggles the microphone mute with the indicator; the Input row in
   the Audio tab shows it muted.
8. Play YouTube Music in a browser: Now playing shows the title and app;
   with the overlay open LB / RB / X go back / forward / play-pause.
9. Open the overlay, change the volume from the Windows flyout: the Volume
   row updates at once. Hide the Audio tab in Settings -> Tabs while the
   overlay is open: it disappears at once.

## Testing on Windows (stage 5)

Launching starts real games, so pick ones you don't mind opening.
1. Open the overlay, go to Games: tiles of every installed launcher, most
   recently played Steam games first, then A-Z. Covers appear within a few
   seconds the first time and at once after that.
2. Pad / arrows: Right at the end of a row goes to the next row, Up / Down
   keep the column, Down onto a short last row lands on its last tile. The
   list scrolls with the selection. The mouse wheel moves by row.
3. Y (Delete on the keyboard, or click the filter pill) cycles All → Steam →
   Epic → … and back; only launchers that have games are offered.
4. A (or a click) on a Steam game, a non-Steam shortcut, an Epic game, a GOG
   game and an Xbox game: the overlay hides and the game starts.
5. Install or uninstall a game while the overlay is closed, then open it: the
   grid follows, and the selected tile stays on the same game if it is still
   there.
6. Settings → Games: turn Epic off, the Epic tiles go; when the filter was
   Epic it goes back to All. Turn it on again.
7. Settings → Games → Manage apps → Add app…: pick an exe (e.g. a portable
   emulator); it shows as a tile with its icon under Apps. Y on its row in
   Manage apps removes it.
8. With every source off: "No games found" and the hint to add apps.
9. A game title with accents or symbols shows correctly on its tile.
10. Scroll the whole grid quickly with the pad and the wheel: covers fill in
    as rows come into view and the overlay stays responsive.
11. Right after starting OverlayUtils the Games tab may say "Looking for
    games…" for a moment, never "No games found" when there are games.
12. A GOG classic that runs in DOSBox starts the game, not an empty DOSBox.

## Testing on Windows (stage 4b)

Needs the headset and the speakers (two outputs), Discord, a browser and a
game. Windows' per-app volumes and mutes change only for apps whose own row
or channel row was set in the overlay on the current output; every other app
keeps what Windows has for it.
1. Audio tab with Chrome playing in two tabs and Discord in a call: one row
   for Chrome and one for Discord (indented under Media / Chat), however many
   processes they run. Right on Chrome changes every Chrome tab at once.
2. Lower Media to 50 % and Chrome to 50 %: Chrome plays at 25 % in the
   Windows volume mixer; Spotify under Media keeps its balance with Chrome.
   A on Media mutes Chrome and Spotify; A again restores the same levels.
3. Per-device mix: set a mix on the speakers, switch to the headset (Audio
   tab or plug it in): the first time the headset starts from the speakers'
   mix; change it, switch back: each device keeps its own mix and every
   playing app follows at once.
4. Presets: "Save preset..." -> the "Save preset" dialog opens over the
   overlay with "Preset 1" selected; Enter saves it (Esc or Cancel saves
   nothing). With only the pad: the dialog shows "A OK · B Cancel", A saves
   the suggested name, B cancels, D-pad does nothing behind it. Change
   the channels, pick the preset on the Preset row and press A ("Apply" in
   the footer): the channel levels come back on the current device.
5. Settings -> Audio -> Channels: "Add channel..." (type a name), change its
   icon with Left/Right, move it with A / Up / Down / A; "Rename..." (footer
   says "Rename") on Game; the new name shows in the Audio tab. Move Discord
   to the new channel in Programs, then press Y on the channel: the row says
   "Delete? Press again" and the footer "Confirm delete"; wait 3 s and it
   goes back. Press Y twice: Discord is back under Other with its own volume
   unchanged.
6. Settings -> Audio -> Programs: move Chrome to Chat by hand; restart the
   app and play in Chrome: it stays in Chat. "Add program..." opens a file
   picker over the overlay; pick an .exe that never played, give it a
   channel, then start it: it plays in that channel.
7. A preset saved while a custom channel existed: delete the channel, apply
   the preset: the other channels are set, no error in errors.log.
8. Circuit breaker: with auto-switch on, make Windows flip outputs quickly
   (e.g. unplug / replug the headset several times within 10 s): after 3
   automatic switches the app stops switching that direction, errors.log has
   one "Audio auto-switch (Output) paused" line, and the next device event
   after 10 quiet seconds switches normally again.
9. Hide the overlay (a click outside, or Back+RB) while the name dialog is
   open: the dialog closes and nothing is saved. Open "Add channel..." and,
   with the prompt still open, click "Save preset...": the prompt becomes
   "Save preset" and only the preset is saved. Apps already playing when the app
   starts keep their level until their row or their channel's row is touched,
   or the output changes to a device with a level set for them.
10. Hands off untouched apps: on a fresh config, set Spotify to 40 % and mute
    VLC in the Windows mixer. Open the overlay, change only Game, switch the
    output and back: Spotify stays at 40 % and VLC muted. In the name dialog,
    Tab to Cancel and press Enter: nothing is saved. With one custom channel
    renamed "Channel 6", "Add channel..." suggests "Channel 7".

## Testing on Windows (stage 3)

Needs a person pressing buttons. With each pad (Xbox, 8BitDo Ultimate 2,
DualShock 4; DualSense and Switch Pro if available):
1. Back+RB opens the overlay (on release) and closes it; D-pad and left stick
   move (holding repeats), A confirms, B goes back, LT/RT change tab, holding
   Back opens Settings. The footer shows pad buttons while a pad is connected.
2. With the overlay closed, Back+D-pad Up/Down switch to the TV/Desk profile;
   with Settings -> Pad combos off they do nothing, Back+RB still works.
3. Plug and unplug a pad while the app runs: it is picked up / dropped without
   a restart; the battery icon appears for pads that report it.
4. A game in front: the pad still drives the overlay (the game may also see
   the presses: known limit).
5. Steam running: PlayStation / Switch pads through Steam Input still work.
6. Moonlight from the Retroid Pocket 6: Back+RB opens the overlay in the
   stream; Moonlight's own Start+Select+LB+RB still quits the stream.
7. Full mode (Program Files): SDL3.dll is loaded from there and pads work.
8. Rename SDL3.dll in a test copy: the app starts, logs one error, keyboard
   and tray keep working.

## Testing on Windows (stage 2)

1. Tray icon left click opens and closes the overlay; clicking outside it closes it.
2. Size Large on a 1080p monitor: the panel clamps to the work area and scrolls.
3. Two monitors at 100% and 150% DPI: open on each, the panel is sharp and sized correctly.
4. Settings row: switch language, the overlay and tray menu change at once.
5. Windows Settings -> Transparency effects off: the panel becomes solid. Animation effects off: no slide.
6. Change the Windows accent colour while running: the overlay follows on the next open. A dark or grey accent falls back to a lighter tone, then to blue.
7. Full mode: `scripts\build.ps1`, start `dist\OverlayUtils\OverlayUtils.exe`, enable full mode from the overlay's Settings (A twice, UAC once; the app restarts from Program Files).
8. In full mode, open the overlay over a fullscreen game (borderless and, if available, exclusive): it appears on top and takes keys and clicks. Confirm the `_repair_frame` problem does not occur (content detached from the frame, as in Python). If it does, stop and record it in WORKLOG.
9. Power options for real (once): Sleep, Restart, Shut down each ask for a second Confirm and run after it.
10. Windows 10, if available: the panel is solid, everything else works.
