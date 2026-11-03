# Stage 4 design: audio and media

Date: 2026-11-03. Status: draft for approval.

## Intent

Let the owner control sound from the overlay (pad or keyboard) without
SteelSeries Sonar or the Windows volume mixer, on any output device. Two
everyday frustrations drive it:

1. **Balancing apps.** Today the owner balances game vs Discord with
   Sonar's mixer slider, which only works with the SteelSeries headset, and
   lowers the game to watch YouTube/Twitch on the second monitor through the
   Windows volume mixer, which is tedious.
2. **Switching devices.** Sonar switches headset/speakers automatically;
   the owner wants the same for any headphones, and for microphones, with an
   order they can change at any time, plus a manual picker.

The owner would like to move away from Sonar ("cool features but buggy"):
**built-in features come first**; Sonar integration is optional and later.

Python reference (`reference/python/app.py`, see `docs/PYTHON-CODEMAP.md`):
`VOLUME, BATTERIA DEL PAD, ALIMENTAZIONE` (master volume/mute, step),
`USCITA AUDIO` (Windows output listing/switching), `MEDIA IN RIPRODUZIONE`
(now playing via Windows media session, play/pause/next/previous).

## Goals

1. **Audio tab** in the overlay (new tab after Controls; owner chose this
   layout in a mockup), top to bottom:
   - **Output** row: current default output device; Left/Right switches to
     the previous/next available output immediately (manual pick).
   - **Input** row: same for the default microphone.
   - **Volume** row: master volume of the default output; Left/Right change
     it (held = repeat, as Python), A toggles mute.
   - **Apps** section: one row per app currently playing sound (Windows
     audio sessions on the default output, grouped per process, our own
     process excluded), labelled with the app's name. Left/Right change that
     app's volume, A mutes/unmutes it. Rows appear/disappear as apps start
     and stop playing (refreshed while the overlay is open). Each app is
     independent (no single balance slider; the owner chose this).
   - **Now playing** card: title/artist (and app) from the Windows media
     session (works for YouTube Music, browsers, Spotify…); Left/Right
     previous/next, A play/pause. Hidden when nothing is playing.
2. **Media buttons from any tab** (Python): LB / RB = previous / next track,
   X = play/pause, while the overlay is open (pad). These are added to
   `PadInterpreter` (they currently do nothing).
3. **Device priority lists**, one for outputs and one for inputs, in
   Settings → Audio:
   - Each list holds every device seen on this PC (connected or not), new
     devices added at the bottom, stored in config (device id + last known
     name).
   - Reordering from the pad: select a device, A picks it up, Up/Down move
     it, A drops it (B cancels). Disconnected devices are shown as such.
   - **Auto-switch rule:** whenever an output (input) device is added,
     removed, enabled or disabled, set the Windows default output (input)
     to the highest-ranked device currently available. A manual pick from
     the Audio tab holds until the next such device change.
   - A Settings toggle per list turns auto-switching off (manual only).
4. **Sonar coexistence:** Sonar's virtual devices are ordinary devices in
   the lists. Ranked above the real devices, Sonar is used while it runs;
   ranked below, our auto-switch wins. No Sonar-specific code in this stage.
5. Volume and mute changes are reflected live in the overlay if changed
   elsewhere (Windows flyout, keyboard keys) while it is open.

## Changes from Python

- The now-playing card moves from Controls to the Audio tab.
- Python's Sonar integration (reading Sonar's output name from its local
  API, switching outputs with GG keyboard shortcuts on Italian scan codes)
  is **not ported**. The audio output pad combo (Back+LB+Left/Right) stays
  dropped (stage 3 decision); the Audio tab replaces it.
- New: input devices, per-app volumes, priority-based auto-switch.

## Constraints

- **Nothing for the user to install.** Use Windows Core Audio (MMDevice
  API, endpoint volume, audio session manager) and the Windows media
  session API (GlobalSystemMediaTransportControls). Setting the default
  device uses the undocumented-but-standard `IPolicyConfig` COM interface
  (what every "audio switcher" tool uses); set it for all roles
  (console, multimedia, communications).
- No third-party packages. COM through .NET's source-generated COM
  (`GeneratedComInterface`), so logic stays in Core where possible; WinRT
  media session in the App (needs a Windows TFM) unless the builder finds a
  clean way to keep it in Core.
- Pure logic in Core and self-tested: the priority rule (given a ranked
  list and the available devices, which is the target), list editing
  (pick up / move / drop / cancel, new devices appended), session grouping
  per process, volume stepping. Interop is the thin layer around it.
- Config: new keys via `AppConfig` (snake_case), changed only through
  `ConfigStore.Update`; unknown keys still preserved.
- Device notifications arrive on COM threads: marshal to the right thread;
  never block the UI thread on audio calls; errors logged, never thrown.
- User-facing text via `Loc.T` in both languages.
- Follows stages 2-3 patterns: rows are `OverlayItem`s built in
  `OverlayContent`, navigation in `OverlayNavigator` (new row kinds such as
  a slider or a reorderable list are added there with self-tests).

## Non-goals

- Equaliser, virtual surround, mic noise suppression (need audio drivers).
  The owner sometimes uses Sonar's equaliser: that stays Sonar's job. With
  Sonar's devices ranked first in the priority lists, audio goes through
  Sonar (and its EQ) whenever it runs.
- A game/chat balance slider (owner chose per-app volumes).
- Sonar integration (backlog: show Sonar's real routed device, ChatMix).
- Per-app output routing (sending one app to a different device).
- YouTube Music / Spotify library integration (stage 6).

## Anti-goals

- Fighting the user: auto-switch must never flip devices when nothing
  changed (no polling-driven switches, no switch loops with Sonar).
- Changing volumes or devices by accident while navigating (only
  Left/Right/A on a selected row act).
- Anything the user must install.

## Known limits (accepted)

- Wired headphones sharing a jack with speakers (many Realtek setups) often
  are not a separate Windows device, so they cannot be auto-detected.
- Per-app volume is per Windows audio session: an app that plays through a
  different device than the default may not appear in the list.

## Backlog notes produced by this design

- Optional Sonar integration (routed device name, ChatMix) after the
  built-in features.
- Stage 6: YouTube Music integration **in addition to** Spotify.

## Details settled before planning (my calls)

1. **Media buttons:** LB/RB/X become new `OverlayCommand`s (PrevTrack,
   NextTrack, PlayPause) handled by the navigator from any tab, sent by
   `PadInterpreter` while the overlay is open. No keyboard keys inside the
   overlay (the keyboard's own media keys already work in Windows).
2. **Reordering:** each priority list is its own page opened from
   Settings → Audio ("Output order", "Input order"), with a new
   reorderable row kind in the navigator. A picks up / drops; while holding,
   Up/Down move the held device and everything else (B aside) is ignored;
   B cancels the move and restores the order; hiding the overlay or leaving
   the page cancels it too. The new order is saved on drop.
3. **Dynamic rows keep their identity:** the selection follows the app
   (process) or device, not the index; if the selected app disappears, the
   selection moves to the nearest row (existing `Refresh` rule). A Left/Right
   or A always acts on the row the user sees selected.
4. **Always on:** an audio service starts with the app (like the pad
   service) and runs whether or not the overlay is open, in both modes.
   Auto-switch is **on by default**; the lists are seeded with the current
   Windows default device first, then the others, so nothing switches on
   first launch. New devices are appended at the bottom.
5. **What re-evaluates the rule:** only a device being added, removed,
   enabled or disabled in that direction (outputs for the output list,
   inputs for the input list). Default-device-changed events (ours, Sonar's,
   other apps') never trigger a switch, so no loops. Any such device event in
   that direction ends a manual pick.
6. **Rebinding:** Volume, mute and the Apps rows always follow the current
   default output; on a default change they rebind (and re-subscribe).
7. **Now playing:** shown whenever a media session exists, paused included;
   it is a selectable row of its own kind (Left/Right previous/next, A
   play/pause), like Python's "media" item.
8. **Split:** COM declarations (`GeneratedComInterface`) live in Core next to
   `Native.cs`; the WinRT media session goes in the App behind a small Core
   interface. Core must keep building on plain `net10.0`.
9. **Stored devices:** matched by Windows device id, with the last known
   name for display; a disconnected device can be removed from a list on its
   page (Y on the pad, Delete on the keyboard). A reinstalled driver with a
   new id shows up as a new entry.
10. **Roles:** the default is set for all roles (console, multimedia,
    communications), for outputs and inputs alike. A separate
    "communications device" setup is not preserved.

## Left to the builder

- Volume step (Python's `VOLUME_STEP`) and repeat behaviour for sliders.
- How app names/icons are resolved (process description vs exe name).
- Refresh mechanism for sessions (notifications vs polling while open).
- Exact Settings layout for the two lists and their toggles.
