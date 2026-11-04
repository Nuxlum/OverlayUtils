# Worklog

Living notes: decisions, open questions, backlog and a dated session log.
Newest entries first. Link to code by symbol name (line numbers go stale).
The stage checklist lives in `PORTING.md`.

## Decisions

- 2026-10-31 - **Port to C# / .NET 10 + WPF.** Main reason: the owner works
  in .NET. Performance alone would not justify it (the app is mostly idle);
  the real gains are GPU-drawn overlay, native Windows APIs without Python
  wrappers, and an easy way to ship two launchers (see Two modes).
- 2026-10-31 - **WPF over WinUI 3**: the overlay is custom-drawn, needs a
  borderless topmost window and must work with UIAccess; WPF is the proven
  choice for that.
- 2026-10-31 - **Product name OverlayUtils** (was AppUtili). Settings are
  migrated automatically; the old install is removed when enabling full mode
  or from the tray menu.
- 2026-10-31 - **English + Italian**, following the Windows display
  language, overridable in the tray menu (stage 2: also in Settings).
- 2026-10-31 - **Two modes.** Standard needs no certificate; full mode
  (overlay above fullscreen games) is opt-in from inside the app, which
  trusts the signer certificate and installs to Program Files, and can undo
  both. Neither mode runs as admin.
- 2026-10-31 - **Start-up via HKCU Run** instead of an elevated scheduled
  task (no admin needed any more).
- 2026-10-31 - **Ctrl+Alt+Up/Down via RegisterHotKey**: unlike the Python
  `keyboard` hook, the keys no longer reach the foreground app. Revisit if
  that bothers anyone.
- 2026-10-31 - English names for scripts and docs; the Python files keep
  their original names inside `reference/python/` (frozen reference).
- 2026-11-03 - **Overlay Settings key is F2.**
- 2026-11-03 - **The overlay's full-mode switch has no dialogs** (A twice is
  the confirmation) and always notifies failures, even with notifications off.
- 2026-11-03 - **Close window does nothing** unless the previous app window
  is alive and back in the foreground when the action runs (overlay opened
  over the taskbar or desktop, or another window took the foreground).
- 2026-11-03 - **Info-row actions** (open folder / site) close the overlay
  first; otherwise the new window would open under the topmost panel.
- 2026-11-03 - **Accent contrast floor 4.5:1** against #202020, because the
  selected icon glyph uses the accent; the palette's light variant is tried
  first, then the lightest entry, each needing 4.5:1, else #4CC2FF. Users
  with a dark accent see a lighter tone instead.
- 2026-11-03 - **Acrylic needs Windows 11 build 22621+**; earlier builds
  (Windows 10, 11 21H2) get the solid background. The backdrop is re-applied
  on every show because DWM drops it.
- 2026-11-03 - **Monitor row says "Confirm"** instead of the pad's "A" until
  stage 3 adds the gamepad.
- 2026-11-03 - **If the overlay never gets focus it stays open** until
  toggled (same as Python).

- 2026-11-03 - **Gamepad via our own SDL3 wrapper** over the official
  SDL3.dll (3.4.18, vendored in `third_party/SDL3`, loaded from the app folder
  only). No binding package; nothing for the user to install.
- 2026-11-03 - **Pad combos reduced** to Back+RB (overlay) and Back+D-pad
  Up/Down (monitor). Python's Back+L3+R3, Back+Start+LB+RB+LT+RT and
  Back+LB+Left/Right are dropped: those actions live in the overlay, and the
  six-button chord contained Moonlight's quit-stream chord.
- 2026-11-03 - **Input bleed accepted:** a game still sees the pad while the
  overlay is open; no driver-free fix known (research item in Backlog).

- 2026-11-03 - **Duplicate pads:** the same action from another pad within
  50 ms is dropped, and a second overlay toggle within 250 ms too, because
  DS4Windows/ViGEm/reWASD show one physical pad twice.
- 2026-11-03 - **Monitor combos fire only on the D-pad press** (Back held
  first), not when Back is pressed while the D-pad is held: holding the D-pad
  in a game and then pressing Back must not switch monitors.
- 2026-11-03 - **Footer shows pad buttons instead of keys** while a pad is
  connected (Enter/Esc/F2 still work); pad shortcuts are listed in the Info
  Shortcuts panel, marked "(pad)", not in a separate section.
- 2026-11-03 - **Stage 4a target framework:** App and both launchers now
  target `net10.0-windows10.0.19041.0` (needed for the WinRT media session
  API); the debug output path changed accordingly. Core stays `net10.0`.
- 2026-11-03 - **IPolicyConfig** (IID F8679F50-850A-41CF-9C72-430F290290C8,
  CLSID 870AF99C-...) sets the default device, on all three roles (console,
  multimedia, communications). If it fails, the error is logged and the
  default is left as it was.
- 2026-11-03 - **Pad audio buttons:** Back+D-pad Left/Right change the volume
  and Back+Y toggles the microphone mute (overlay closed, Pad combos on).
  With the overlay open LB / RB / X are previous / next / play-pause, and Y
  removes a disconnected device from a priority list.
- 2026-11-03 - **Volume cap:** applied from the overlay and pad and when the
  output changes, not at startup and not to Windows' own volume keys. A new
  cap applies at the next step or device change.
- 2026-11-03 - **Re-enabling auto-switch** waits for the next device event;
  it does not switch at once.
- 2026-11-03 - **RB before Back:** with the overlay open, pressing RB before
  Back skips a track (same as Python), then closes the overlay.
- 2026-11-03 - **Media app name "Lettore multimediale"** comes from the
  Python mapping and is not localized yet.

- 2026-11-04 - **Mouse as a first-class input:** the row under the pointer
  becomes the selection (Steam Big Picture style), but only when the mouse
  really moves, so opening the overlay or scrolling never moves it; pad and
  keyboard take the selection back until the next move. Every visible
  control is clickable, clickables get a hand cursor and a hover highlight.

- 2026-11-04 - **Mix not applied at startup:** apps already playing when the
  app starts keep their level; the stored mix applies to sessions created
  later, on the next overlay change and on a default output change (same
  stance as 4a's volume cap; "changes made elsewhere stay").
- 2026-11-04 - **Muting sets only the session mute flag:** the session volume
  stays at channel x app, so unmuting (here or in the Windows mixer) brings
  back the same level.
- 2026-11-04 - **Default channel names follow the UI language** until
  renamed: the four defaults are stored with their English names and shown
  translated while the stored name is still the default; accepting the
  suggested name in Rename keeps it that way.
- 2026-11-04 - **"Save preset..." hidden when no channel row is visible**
  (nothing playing, nothing to look at); the Preset row stays whenever
  presets exist.
- 2026-11-04 - **Channel and app steps don't change the mute** (unlike the
  master Volume row, which unmutes above 0): a muted channel stays muted
  while its level is adjusted.
- 2026-11-04 - **Auto-switch circuit breaker:** at most 3 automatic default
  switches per direction within 10 s; then auto-switch for that direction
  pauses until a device event after 10 s of quiet, with one `Log.Info`.
  Guards against switch loops with Sonar or flaky devices.
- 2026-11-04 - **Name dialog is modeless and owned by the overlay** (no
  nested message loop inside a navigator action), titled with what it names
  (New channel / Rename channel / Save preset). While it is open the pad
  drives it: A accepts the name shown (the suggestion unless typed over), B
  cancels, other pad input is swallowed (`DialogPad.Route`); with a pad
  connected the dialog shows "A OK · B Cancel". A new request replaces an
  open prompt (the old one is cancelled). Hiding the overlay cancels it.
- 2026-11-04 - **A row can name its A action** (`OverlayItem.ActionLabel`):
  "Apply" on Presets, "Rename" on Rename, instead of the kind's "Turn on/off".
- 2026-11-04 - **No stored level means hands off** (review fix): a session is
  written only when its app or its channel has an entry in the device's mix
  (`MixRules.ShouldWrite`); a new device's mix has no channel entries. So
  untouched programs keep the per-app volume and mute Windows has for them
  instead of being reset to 100 % unmuted. Edits, presets and copies to a new
  device create entries, so anything the user set (even back to 100 %) is
  written; not "skip if equal to the default", which would lose an unmute.
  A session written once stays written while it lives (a program moved to an
  untouched channel follows it).
- 2026-11-04 - **Deleting a custom channel takes two presses** (Y / Delete
  within 3 s, `OverlayItem.ConfirmRemove`), like Power: it loses the
  channel's assignments and levels. The row shows "Delete? Press again" and
  the footer "Confirm delete". Removing a disconnected device from a priority
  list stays one press.
- 2026-11-04 - **Games library without hard-coded paths:** every launcher is
  found from the registry (Steam path, Epic AppDataPath, GOG game keys,
  uninstall entries for emulators) and its own files; nothing assumes a drive
  or folder. Xbox / Game Pass games are the user's packages that carry a
  MicrosoftGame.config, launched by AUMID.
- 2026-11-04 - **Emulators open by themselves:** one tile per installed
  emulator, no ROM scanning (there are too many formats and front ends to
  support well). Portable emulators, Quiver Launcher, recomp launchers and
  anything else go in through Settings → Games → Manage apps → Add app….
- 2026-11-04 - **Duplicates across stores are kept:** a game owned on Steam
  and Epic shows twice (each tile launches its own store's copy); only the
  same id is merged.
- 2026-11-04 - **Y / Delete is the Games filter:** on the Games tab the Remove
  command cycles All → each launcher that has games (`OverlayItem.AltAction`);
  a filter whose launcher empties falls back to All. The grid keeps the
  selected game by id across refreshes.
- 2026-11-04 - **Discord dropped (stage 7):** reading voice channels and
  mute / deafen need Discord's `rpc.voice.*` scopes, "only available to
  approved partners"; outside that, only accounts added by hand as app
  testers work, or each user registers their own app (the Python way, with a
  client secret). The owner wants no manual setup for friends, so it goes;
  "watch a friend's stream" was never in local RPC anyway. The migration
  skips `discord_token.json`, `discord_recent.json`, `discord_cache`; the
  config drops every `discord_*` / `spotify_*` key on load, so the next save
  no longer holds the client secret. Files an earlier build already copied
  are left alone (the user's files).
- 2026-11-04 - **Spotify and YouTube Music dropped (stage 6):** since
  February 2026 a Spotify app in development mode allows 5 users, all with
  Premium, and more needs a business with 250k monthly users, so it can't
  ship to friends; YouTube Music has no official API. The owner doesn't use
  music while gaming. The generic Windows media session (stage 4a) already
  shows and controls any player. The migration skips the Spotify token and
  cache; an already-migrated `spotify_token.json` in %APPDATA%\OverlayUtils
  is left alone (the user's file) and unused.
- 2026-11-04 - **Art only near the view** (review fix): the grid asks for the
  covers of the rows on screen plus one on each side, tiles four rows away
  drop theirs, and decoded covers sit in a 120-entry most-recently-used cache
  (about 30 MB), so a 1,000-game library does not hold 300 MB of images.
- 2026-11-04 - **Grid remembers its tile** across Settings and other tabs
  (Python resets it only on show; so do we, in `Reset`). A filter change keeps
  the selected game when the new filter has it, else starts on the first
  tile; a filter that fell back to All stays All.
- 2026-11-04 - **Launcher re-read throttled:** opening the overlay reads the
  launchers again only if the last read is over 30 s old; Settings → Games
  changes read at once. A launch that fails is logged and notified (shown
  even with notifications off).
- 2026-11-04 - **Tiles are drawn outside the row list:** the view model keeps
  the tile objects while the list of games is unchanged, so moving the
  selection only flips `IsSelected` and art decoded on a worker arrives
  without rebuilding the grid.

## Open questions

- Signing: stay self-signed, or get a CA-issued certificate (Azure Artifact
  Signing needs an organisation in the EU; individuals only in US/Canada)?
  A CA certificate would remove the "trust this certificate" step.
- Does the Home key low-level behaviour need anything beyond RegisterHotKey
  now that the app is no longer elevated? (Test in stage 1.)
- SteelSeries GG shortcuts use Italian-layout scan codes (`SCAN_*` in Python):
  make them configurable when porting stage 4?

## Backlog

- [ ] CI: GitHub Actions on windows-latest running build + self-test
- [ ] Branding (last stage, before going public): "OverlayUtils" and the repo
  description are placeholders. Candidates checked 2026-11-04 (web search, not
  a formal USPTO/EUIPO check): CouchPilot (crowded, same niche), OmniWrench
  (Ratchet & Clank item, trademark worry), Omnitool (no live trademark, very
  crowded), Omnideck (live trademark + Omnifinity VR treadmill), Omnidock
  (Bardex application possibly covering software). Untested: OmniPad. A rename
  touches exe names, %APPDATA%/Program Files folders (migrate settings), Run
  key, certificate subject (re-enable full mode), README, repo name.
- [ ] First-run setup for friends: guide a new user through full mode
  without reading docs.
- [ ] Research: a driver-free way to keep pad input away from the game
  while the overlay is open (nothing for the user to install). If found,
  offer it as an advanced option next to full mode.
- [ ] Optional Sonar integration after the built-in audio features (show
  the device Sonar routes to, ChatMix).

## Session log

- 2026-11-04 - Stage 7 dropped (Discord, see Decisions); migration skips
  Discord files, config drops `spotify_*` / `discord_*` keys. Self-test
  1263/1263.
- 2026-11-04 - Stage 6 dropped (Spotify / YouTube Music, see Decisions);
  migration no longer copies Spotify's token and cache. Self-test 1263/1263.
- 2026-11-04 - Stage 5 final review fixes: GOG `launchParam` passed as
  arguments, emulator `DisplayIcon` used only when it names the emulator's
  exe (not an uninstaller), launch and click by game id, grid tile kept
  across pages, filter fixes, "Looking for games…" before the first read,
  bounded art loading, stale-tile rebuild on any game change, Xbox via
  `InstalledPath` (473 → 81 ms) with the store logo as fallback, shortcut exe
  icons, Manage apps rows remove on A too. Self-test 1263/1263.
- 2026-11-04 - Stage 5 (games library) written in 5 tasks: Steam reader with
  text / binary VDF and shortcuts, Epic / GOG / emulator / manual-app
  readers, merge / sort / filter, Grid row kind and Games tab, Settings →
  Games, Xbox reader, GameService, cover grid with art loading. 0 warnings,
  self-test 1247/1247; a read-only check on the dev PC found Steam 79, Epic
  1, GOG 7, Xbox 1, emulators 0. Nothing launched yet: stage 5 list in
  PORTING.
- 2026-11-04 - Stage 4b review fixes: untouched mix levels no longer
  override Windows' per-app volumes (`ShouldWrite`, empty default mix),
  press-twice delete of custom channels, Enter on Cancel in the name dialog
  cancels, "Add channel..." skips names in use. Self-test green, 0 warnings;
  still not tested with real devices.
- 2026-11-04 - Stage 4b (mixing) written in 4 tasks: mixing rules
  (self-tested), audio sessions with per-app mix and the auto-switch circuit
  breaker, channels / presets / Channels and Programs pages in the overlay,
  name dialog, program picker, indented app rows, footer action labels,
  docs. Builds with 0 warnings, self-test green; nothing tested with real
  devices yet (stage 4b list in PORTING).
- 2026-11-04 - Mouse: hover selection, clickable tabs/arrows/volume bar/media
  buttons/footer hints, hover highlights; self-tested navigator entry points;
  not yet tried by hand.
- 2026-11-03 - Stage 4a (audio devices) written: audio rules (self-tested),
  Core Audio service, media session, Audio tab with Output / Input / Volume /
  Now playing, priority lists and auto-switch in Settings, pad volume and mic
  mute with a volume indicator, tray overlay entry, docs. Builds with 0
  warnings; nothing tested with real devices yet (stage 4a list in PORTING).
  Stage 4b (channels, per-app volumes, presets) is next.
- 2026-11-03 - Stage 3 (gamepad) written: pad interpreter (self-tested),
  SDL3 pad service, overlay/tray wiring, docs. Builds with 0 warnings; SDL3
  loads and fails cleanly without the DLL; not yet tested with a real pad.
- 2026-11-03 - Stage 2 review fixes: double-click no longer fires an armed
  confirm; Close window needs the previous window back in the foreground; a
  tray click closes an open overlay instead of reopening it; Size, Position
  and monitor profile changes re-place the open overlay; the selection is
  refreshed on every render; shutdown.exe starts from System32.
- 2026-11-03 - Stage 2 written in 8 tasks: overlay model and
  navigator, layout maths, focus / monitor / power / accent helpers, window and
  view model, Shift+F1 and tray toggle, acrylic / accent / animation, docs.
  Checked by a scripted test run on Windows (still needs the owner's check):
  open / close on the right monitor, Esc focus return, Close window, Power
  confirm timeout, F2 / Esc pages, Size and Position, Exit.
  Remains: the stage 2 manual list in PORTING (DPI pair, Large on 1080p,
  effects off, accent change, full mode over a fullscreen game with the
  `_repair_frame` check, real Power actions, Windows 10).
- 2026-10-31 - Stage 1 written: solution, Core (config, migration,
  localization, hotkeys, monitor profiles, start-up, full-mode installer,
  legacy cleanup), App (tray, mode switching), scripts, docs. Core self-test
  passes (Linux); App not yet compiled - needs a Windows build.
- 2026-10-31 - Took over the fork; mapped the Python codebase, wrote the
  orientation docs and the code-map generator.
- 2026-10-31 - First Windows build (.NET SDK 10.0.403): the only errors
  were missing `using System.IO;` in ModeSwitcher and TrayController (WPF
  projects leave System.IO out of the implicit usings). 0 warnings, self-test
  159/159, app starts in standard mode with nothing logged but the start line.
