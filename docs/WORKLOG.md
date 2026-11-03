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
- [ ] First-run setup for friends: guide a new user through full mode and
  connecting Discord without reading docs. After stage 7 (Discord).
- [ ] Research: a driver-free way to keep pad input away from the game
  while the overlay is open (nothing for the user to install). If found,
  offer it as an advanced option next to full mode.
- [ ] Stage 7: "watch a friend's stream" in Discord is wanted; probably not
  in Discord's local RPC, may need a workaround.
- [ ] Optional Sonar integration after the built-in audio features (show
  the device Sonar routes to, ChatMix).
- [ ] Stage 6: YouTube Music integration in addition to Spotify (keep both).

## Session log

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
