# Stage 3 design: gamepad

Date: 2026-11-03. Status: draft for approval.

## Intent

Make the pad a comfortable way to control the PC outside games, so the
owner does not have to put it down. Two real situations drive it:

1. **Desk, two monitors.** Game on one screen, Discord on the other. Today
   any action outside the game means reaching for mouse or keyboard.
2. **Couch, streaming with Moonlight to a Retroid Pocket 6.** There is no
   keyboard or mouse at all; the pad (sent by Moonlight as a virtual Xbox
   pad) is the only input.

The owner has never used the Python app, so its combos are not a habit to
preserve. The design favours **one easy chord to open the overlay, and
everything else inside the overlay**, where the footer always shows what
each button does.

Python reference: `reference/python/app.py`, section `CONTROLLER`
(`docs/PYTHON-CODEMAP.md`), and the pad parts of `OVERLAY GAMEPAD`.

## Goals

1. Read pads through SDL3's gamepad API: Xbox (incl. Moonlight's virtual
   pad), 8BitDo Ultimate 2, DualShock 4 required; DualSense and Switch Pro
   should work too. Hot-plug: pads connected or removed while the app runs
   are picked up without a restart. Several pads at once are fine.
2. **Back+RB** toggles the overlay, firing on release and only when exactly
   those two buttons were held (Python `COMBO_OVERLAY`, `_overlay_armed`).
3. **Back+D-pad Up / Down** switch to the TV / Desk monitor profile (same
   as Ctrl+Alt+Up / Down), only while the overlay is closed. Each combo
   fires once per press, on the button that completes it (Python
   `_check_combos`).
4. While the overlay is open, the pad drives it through the existing
   `OverlayCommand`s:
   - D-pad and left stick: up/down/left/right, with repeat when held
     (first repeat after 0.4 s, then every 0.12 s; Python
     `NAV_REPEAT_DELAY` / `NAV_REPEAT_INTERVAL`); left/right repeats are
     the `*Repeat` commands.
   - A = a, B = b, LT / RT = previous / next tab.
   - Holding Back alone for 0.6 s opens Settings (Python
     `SETTINGS_HOLD_SECONDS`, `_back_hold`).
   - Buttons reserved for later stages (LB/RB/X tracks, L3/R3, Y, Start)
     do nothing yet.
5. Pad battery shown in the overlay header for any pad that reports it
   (SDL power info), as a level icon like Python's.
6. The footer, Info page and README show pad buttons alongside the
   keyboard keys (Info: a "Pad" section with Back+RB, Back+D-pad Up/Down,
   in-overlay buttons, hold Back for Settings).
7. Settings gets back the **Pad combos** toggle (`controller_enabled`,
   already in config): off disables Back+D-pad monitor combos. Opening the
   overlay with Back+RB always stays on.

## Changes from Python

- Python's other global combos are **dropped**: Back+L3+R3 (show desktop),
  Back+Start+LB+RB+LT+RT (close window), Back+LB+Left/Right (audio, comes
  back in stage 4 if wanted). Show desktop and Close window live in the
  overlay. The six-button close chord also contained Moonlight's quit
  chord.
- Monitor combos become Back+D-pad Up/Down (was Back+LB+Up/Down).
- Battery for all pads that report it, not only XInput ones.

## Constraints

- **Nothing extra for the end user to install.** SDL3 ships as `SDL3.dll`
  next to the exe (official release, x64), loaded from the app folder only
  (never from PATH or the current directory; it must work from Program
  Files in full mode). Self-contained publish, no single-file (as now).
- **How it is built (my call):** our own minimal P/Invoke wrapper over the
  SDL3 gamepad API (init, events, open/close, button/axis state, power
  info, name), declared like the rest (`LibraryImport`), no third-party
  binding package. Settles the WORKLOG open question.
- **Chords to avoid:** never use the Guide/Xbox button (Steam Big Picture,
  Xbox Game Bar), and never use Start+Select+LB+RB together (Moonlight's
  quit-stream chord).
- Pad thread: SDL events on a dedicated thread; overlay commands are
  marshalled to the UI thread; actions follow the existing rules
  (blocking work off the UI thread, errors logged, never thrown).
- The pad must work whether or not the overlay has focus, and whether or
  not a game is in front.
- Testable logic in Core (combo detection, Back+RB arming, hold-to-Settings,
  repeat timing, stick dead-zone to direction) as a pure state machine fed
  with button/axis events and a clock, so the self-test covers it. SDL
  interop is the thin layer around it.
- Keep `docs/PORTING.md`, `ARCHITECTURE.md`, `WORKLOG.md`, `README.txt`
  updated as in earlier stages.

## Known limits (accepted)

- **Input bleed:** a game still receives the pad while the overlay is open,
  and also sees Back+RB / Back+D-pad. Most games stop reading the pad when
  they lose focus (the overlay takes focus), some do not. There is no
  driver-free way to block it that we know of; drivers (HidHide-like) and
  injecting into games are ruled out (extra install; anti-cheat risk).
- **Steam Input** may hide PlayStation/Switch pads behind a virtual Xbox
  pad while Steam runs: buttons still work, battery may be missing.
- The Retroid/Moonlight setup needs full mode for the overlay to show over
  fullscreen games in the stream (same as at the desk).

## Non-goals

- Audio, media and Discord pad actions (stages 4 and 7).
- Remapping or configuring combos beyond the on/off toggle.
- Rumble, gyro, touchpad, LEDs.
- Moving the mouse cursor with the pad.

## Anti-goals

- Any chord that fires during normal play by accident (combos need Back
  held, fire once, and Back+RB only on release with nothing else held).
- Interfering with Moonlight's or Steam's own chords.
- Anything the end user has to install separately.

## Backlog notes produced by this design

- Research: a driver-free way to keep pad input away from the game while
  the overlay is open; if found, offered as an advanced option next to
  full mode.
- Stage 7: "watch a friend's stream" in Discord is wanted; probably not
  available through Discord's local RPC, may need a workaround.
- Stage 4: decide whether audio output switching gets a pad shortcut.

## Left to the builder

- Exact stick dead-zone and how stick and D-pad combine.
- Which pad's battery to show when several report one (suggest the one
  used last).
- Where SDL3.dll comes from in the repo/build (download step in
  `scripts/build.ps1` with a pinned version and hash, or vendored binary),
  as long as the end user installs nothing.
- Pad glyph style in the footer (text keycaps like "A", "LT" are fine).
