# Stage 2 design: overlay shell

Date: 2026-10-31. Status: draft for approval.

## Intent

Stage 2 of the port (`docs/PORTING.md`): the overlay window itself, driven
by keyboard and mouse. The gamepad comes in stage 3, so everything here
must work without one, and must be built so stage 3 can feed the same
commands in.

The longer-term goal is to hand the app to friends who are not technical.
They will mostly use it from the couch on a TV with a pad, so the overlay
must be readable at a distance, always show where the cursor is, and never
show something meaningless (raw shortcuts, settings for features that do
not exist yet).

Behaviour follows the Python overlay (`reference/python/app.py`, section
`OVERLAY GAMEPAD`, see `docs/PYTHON-CODEMAP.md`) except for the changes
listed under "Changes from Python", which the owner approved.

## What the Python overlay is today

A 420 px wide (at 96 dpi) dark panel, Windows 11 style, at the top-left of
the work area of the monitor holding the foreground window, 36 px from the
edges. Header: app name, gear (Settings), clock, pad battery, monitor
profile; then a tab strip (Controls, Games, Spotify, Discord; LT/RT chips).
Body: sectioned rows with an icon, a label and a control on the right
(segmented pills, `‹ value ›` choice, slider, toggle switch, keycap hint).
Bottom: a description of the selected row, then a footer of pad-button
hints. Settings and Info are pages that replace the tabs (back arrow,
"B Back"). The whole panel is one Pillow image; the height is set by the
Controls tab (minimum 720 px) and shared by every tab.

Renders of the current screens were shown to the owner during design.
To reproduce them: a venv with `keyboard pystray Pillow`, `APPDATA` pointed
at a scratch folder, `app._Overlay().render()` per tab/page.

## Goals

1. Overlay window: borderless, topmost, per-monitor DPI aware, shown on the
   monitor of the foreground window, hidden from the taskbar and Alt+Tab.
2. Focus handling as in Python: remember the previous foreground window,
   force our window to the foreground, give focus back on close, close by
   itself when another window takes the foreground (only after we had it),
   run actions after focus has returned.
3. Toggle with Shift+F1 (hotkey id 2, already reserved in `TrayController`)
   and with a left click on the tray icon.
4. Controls tab with the items ported so far (see "Content").
5. Settings and Info pages (see "Content").
6. Keyboard and mouse navigation over one command model that stage 3's pad
   will also drive.
7. The approved changes below.

## Changes from Python (approved)

1. **Size setting.** "Overlay size": Normal / Large / Auto. Large scales
   the whole panel up (suggested 1.35x on top of DPI scaling; builder may
   tune). Auto = Large while the active monitor profile is the one
   assigned to the TV role, Normal otherwise. Default: Auto. *(Default is
   my call.)* *(My calls:)* "Active profile" is `MonitorProfiles.Current`,
   the same source as the tray icon, so it can be stale if displays are
   switched outside the app; accepted. The TV role counts whether chosen
   or guessed by `MonitorProfiles.ForRole`, consistent with Ctrl+Alt+Up;
   Normal only if there is no TV profile at all.
2. **Always-visible selection.** The selected row always has an accent
   highlight (background tint plus a bar on the leading edge, as Python's
   Settings page already does). The selected option inside a segmented
   row is also marked. There is no state in which nothing looks selected.
   The footer hints describe what A/Enter does on the selected row.
3. **Look.**
   - Translucent backdrop on Windows 11 (system acrylic, the "transient
     window" backdrop type), falling back to the solid Python palette on
     Windows 10, when Windows "Transparency effects" is off, or if the
     backdrop call fails.
   - Accent colour taken from the Windows accent (a light variant suited
     to a dark background), replacing the fixed blue `OV_ACCENT`; updated
     if the user changes it while the app runs.
   - A short slide-in + fade when opening (about 150 ms), skipped when
     Windows "Animation effects" is off. Closing is instant: focus must go
     back to the game at once.
   - Icons from Segoe Fluent Icons (Segoe MDL2 Assets on Windows 10), as
     Python does, drawn as vectors so they stay sharp at any DPI.
   - Theme: dark only, like Python. *(My call: a light overlay over games
     is not useful.)*
4. **Side setting.** "Overlay position": Left / Right. The panel sits at
   the top of that side of the work area, same 36 px margin. Default: Left
   (as Python).
5. **Bug fixes while porting.**
   - The description area always describes the selected row. (Python
     shows the Monitor description even when the Monitor row is hidden
     because there are no profiles.)
   - The footer is pinned to the bottom of the panel on every tab and page.
     (Python lets it float mid-panel on Controls.)
   - For stage 4, recorded here so it is not forgotten: the audio output
     row must never show raw shortcuts such as "Ctrl+è / Ctrl++"; without
     SteelSeries Sonar it shows the Windows output device name.

## Content in stage 2

Only features that work in the .NET app are shown. Rows and tabs for later
stages appear when their stage lands. *(My call: friends should never see
a dead control.)*

### Tabs

Only **Controls** exists in stage 2. The tab strip is still drawn (with one
tab) so the layout does not change when Games/Spotify/Discord arrive.
Tab cycling (Tab/Shift+Tab, PgUp/PgDn) is implemented over the visible
tabs and skips hidden ones, as Python's `_cycle_tab`.

`hidden_tabs` in `config.json` stores the Python (Italian) tab names
("Controlli", "Giochi", "Spotify", "Discord"); keep those strings as the
stored identifiers so migrated configs keep working, and show localized
names in the UI.

### Controls tab (Python `OVERLAY_ITEMS`)

| Section | Row | Kind | Behaviour |
|---|---|---|---|
| Screen | Monitor | segmented if 3 profiles or fewer, otherwise `‹ name ›` + A to apply (as Python `_monitor_kind`) | Hidden when there are no profiles. Cursor starts on the active profile. Uses `MonitorProfiles`. |
| Steam | Big Picture | segmented: Open / Exit | `steam://open/bigpicture` and the close URL, as Python `open_bigpicture` / `close_bigpicture` |
| Windows | Show desktop | action | Win+D (`KeySender.ShowDesktop`) |
| Windows | Close window | action, keycap hint "Alt+F4" | Alt+F4 to the previous foreground window (`KeySender.AltF4`) |
| System | Power | segmented: Sleep / Restart / Shut down, **confirm** | Second A within 3 s confirms (danger-coloured "Confirm?" pill), as Python `CONFIRM_TIMEOUT` |

Audio output, volume and the now-playing card are stage 4 and are not shown.

Actions that target the previous window (Show desktop, Close window, the
monitor switch, power, Big Picture) close the overlay first, give focus
back, wait `OVERLAY_ACTION_DELAY` (150 ms), then run on a worker thread, as
Python `_run` / `_run_overlay_action`.

### Settings page (Python `SETTINGS_ITEMS`)

| Section | Row | Notes |
|---|---|---|
| Tabs | Controls | Toggle, at least one tab stays visible (only Controls exists now, so it cannot be turned off; the row is still shown). |
| Monitor | Desk, TV | Choice rows over the profiles plus "None"; hidden without profiles. |
| Monitor | MonitorSwitcher | Status (searching / found / not found) + "Choose…" (file picker, as the tray menu). Hidden without profiles. |
| General | Notifications | Toggle. |
| General | Start with Windows | Toggle (`StartupRegistration`). |
| General | Home key | Segmented: Windows / Alt+Tab / None. |
| General | Language | Choice: Automatic / English / Italiano (the tray menu has this; ARCHITECTURE planned it for Settings). |
| Overlay | Size | Segmented: Normal / Large / Auto. New config key. |
| Overlay | Position | Segmented: Left / Right. New config key. |
| Overlay | Over fullscreen games | Shows Full mode on/off; Enable / Disable with confirm (second A within 3 s, like Power), no dialog boxes. Then the overlay closes and the same `ModeSwitcher` steps run (UAC still appears). Failures are reported as a Windows notification, not a dialog. The tray keeps its dialogs. *(Row is my call: this is the step friends most need to find. The no-dialog flow was the owner's decision.)* |
| Other | Information | Opens the Info page. |
| Other | Exit OverlayUtils | Segmented "Exit" with confirm. |

Omitted until their stage: Services (Spotify, Discord), Pad combos.

### Info page (Python `_info_panels`)

Expandable panels, one open at a time, as Python:
- **Shortcuts:** keyboard only for now (Shift+F1, Ctrl+Alt+Down, Ctrl+Alt+Up
  with the role names), plus the in-overlay keyboard keys. Pad sections
  arrive in stage 3.
- **Monitor:** MonitorSwitcher (Open folder / Download), profiles, profiles
  folder (Open), Desk · TV.
- **System:** version and install folder, mode (Standard / Full, i.e.
  UIAccess yes/no), configuration folder (Open).

Services, audio and controller rows arrive with their stages.

## Input model

One set of commands, matching Python `_handle`: `up`, `down`, `left`,
`right` (plus `*_repeat` variants for held keys, which only sliders use),
`a`, `b`, `prev_tab`, `next_tab`, `settings`, `wheel_up`, `wheel_down`.
Keyboard and mouse translate to these; stage 3's pad will too.

- Keyboard: arrows; Enter = a; Esc = b; Tab / Shift+Tab and PgDn / PgUp =
  tabs. **Settings must be reachable without a mouse or pad**: add a key
  for it (builder's choice, shown in the footer and in Info).
- Mouse: click a row selects it and activates it (as A); click a segment
  picks that option; click the gear opens Settings; click the back arrow is
  b; right click = b. Wheel = up/down (moves the selection) on Settings and
  Info, and does nothing on Controls, as Python `_wheel`. *(Check Python
  `_click`, `_right_click`, `_hover` for exact behaviour and keep it.)*
- *(My calls:)* Controls is always visible even if a migrated
  `hidden_tabs` hides "Controlli". When remembering the previous
  foreground window (also the Alt+F4 / Win+D target), ignore the taskbar
  and other shell windows, since a tray click makes the taskbar the
  foreground window. Pages taller than the work area (Settings at Large)
  scroll, as Python `settings_scroll`.
- b: Info -> Settings -> tabs -> close, as Python.
- On every open: Controls (or the first visible tab), first selectable row,
  page reset, as Python `show`.

The navigation state (selected index, skipping hidden rows, segment cursor,
pending confirmation with timeout, tab cycling, page stack) has no WPF
dependency and goes in Core so the self-test can cover it, per the
ARCHITECTURE rule of thumb.

## Constraints

- Everything in `CLAUDE.md`: UI thread via the dispatcher, hotkey
  callbacks on the hotkey thread, blocking work in `Task.Run`,
  `Log.Error` instead of throwing from handlers, Win32 declarations in
  `Core/Interop/Native.cs`, every user-facing string through `Loc.T` /
  the XAML `Loc` binding with keys in both `.resx` files.
- New config keys via `AppConfig` with snake_case names (suggested
  `overlay_size`: `normal|large|auto`, `overlay_side`: `left|right`),
  changed only through `ConfigStore.Update`. Unknown keys still preserved.
- Must work in both modes. In full mode the overlay must appear above
  fullscreen games; PORTING asks to verify that Python's `_repair_frame`
  UIAccess z-order problem does not affect WPF.
- The window is created once and shown/hidden, not recreated per open
  (it must appear instantly).
- Settings changes made in the overlay and in the tray menu stay in sync
  (both go through `ConfigStore`, which raises `Changed`).

## Non-goals

- Gamepad input, Select-hold, pad battery (stage 3).
- Audio, volume, now playing (stage 4); Games (5); Spotify (6); Discord (7).
- First-run setup for friends (full mode + Discord): planned after the
  port reaches Discord; tracked in WORKLOG backlog.
- A light theme.
- Python's temporary diagnostics (`_diag_windows`) are not ported.

## Anti-goals

- The overlay keeping focus or swallowing input after it closes, or Alt+F4
  / Win+D landing on the overlay instead of the game.
- Any visible state where the user cannot tell what is selected.
- Rows or tabs that do nothing yet.
- A second, separate settings logic: overlay and tray change the same
  config the same way.

## Left to the builder

- WPF structure (one window with templated rows vs. custom drawing),
  styling, exact sizes and spacing (start from Python's measurements).
- How the acrylic backdrop and accent colour are obtained (DWM attributes,
  `UISettings`, registry), and how the fallback is detected.
- The keyboard key for Settings.
- Large-scale factor.
- Whether the tray-click toggle needs to distinguish single and double
  click.
