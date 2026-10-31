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

## Open questions

- Signing: stay self-signed, or get a CA-issued certificate (Azure Artifact
  Signing needs an organisation in the EU; individuals only in US/Canada)?
  A CA certificate would remove the "trust this certificate" step.
- Does the Home key low-level behaviour need anything beyond RegisterHotKey
  now that the app is no longer elevated? (Test in stage 1.)
- Gamepad library for stage 3: SDL3 via a NuGet binding, or our own minimal
  P/Invoke over a bundled SDL3.dll?
- SteelSeries GG shortcuts use Italian-layout scan codes (`SCAN_*` in Python):
  make them configurable when porting stage 4?

## Backlog

- [ ] CI: GitHub Actions on windows-latest running build + self-test
- [ ] Python leftover to drop in the port: temporary diagnostics in
  `_Overlay.show()` (`_diag_windows`, "DIAGNOSTICA TEMPORANEA")

## Session log

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
