# CLAUDE.md - working guide for this repo

Read this first. It is the entry point for anyone (human or agent) picking up
work here; the deeper docs live in `docs/`.

## What this is

**OverlayUtils** - a Windows tray utility with a gamepad-driven overlay that
can sit on top of fullscreen games: monitor profile switching, Home key
remapping, global gamepad combos, and an overlay with system controls, the
games library (Steam, Epic, GOG, Xbox, apps) and media controls.

It is being **ported from Python (the "AppUtili" app) to C# / .NET 10 + WPF**,
in stages. The Python app in `reference/python/` is the behaviour spec for
everything not ported yet. Repo: `github.com/Nuxlum/OverlayUtils`.

## Where to look

| Need | Go to |
|---|---|
| What is ported, what is next, stage checklist | `docs/PORTING.md` |
| Projects, two modes, threads, config, localization | `docs/ARCHITECTURE.md` |
| Decisions, open questions, session log | `docs/WORKLOG.md` |
| Find Python code for a feature (line numbers) | `docs/PYTHON-CODEMAP.md` |
| End-user instructions (EN + IT) | `README.txt` |

## Layout

```
src/OverlayUtils.Core/      no-UI logic, no NuGet, builds on any OS
src/OverlayUtils.App/       WPF/WinForms app (AppHost, Tray/, ModeSwitcher)
src/OverlayUtils/           launcher OverlayUtils.exe (standard mode)
src/OverlayUtils.UIAccess/  launcher OverlayUtils.UIAccess.exe (full mode)
tests/OverlayUtils.SelfTest dependency-free checks for Core
scripts/                    certificate.ps1, build.ps1
reference/python/           frozen Python app (spec for the port)
tools/codemap.py            regenerates docs/PYTHON-CODEMAP.md
```

## Hard constraints

- **Windows only at runtime.** Core targets plain `net10.0` and builds
  anywhere; App and the launchers need the Windows Desktop SDK (Visual Studio
  or `dotnet build` on Windows).
- **Two exes, one codebase.** UIAccess is fixed by the manifest. Never add
  `uiAccess="true"` to the standard launcher or start the UIAccess exe with
  `UseShellExecute = false` (Windows refuses UIAccess to CreateProcess).
- **Nothing runs elevated** except `OverlayUtils.exe --helper ...`. Keep
  admin-only work in `FullModeInstaller` / `LegacyInstall`, per-user work in
  the app.
- **Never trust a certificate the user did not opt into.** `FullModeInstaller`
  only adds the self-signed code-signing cert that signed our own exe, and
  records it so disabling removes exactly that one.
- **Secrets never in the repo.** Tokens and config live in
  `%APPDATA%\OverlayUtils\`; `.gitignore` blocks `*.pfx/*.cer/*.key` too.
- **No single-file publish** (see ARCHITECTURE, Build).

## Conventions

- English for code, comments, docs and commit messages. User-facing text only
  through `Loc.T("Key")` with the key in **both** `Strings.resx` and
  `Strings.it.resx`.
- Config keys: add a property to `AppConfig` with the snake_case JSON name;
  change settings only through `ConfigStore.Update`.
- UI work on the UI thread (`OnUi`); hotkey callbacks arrive on the hotkey
  thread; blocking work (process starts, UAC waits, file searches) in `Task.Run`.
- Log problems with `Log.Error(context, exception)`; never let a menu action
  or hotkey handler throw.
- Win32 declarations go in `Core/Interop/Native.cs` (`LibraryImport`,
  blittable structs).
- When porting a feature, read the Python section first (via
  PYTHON-CODEMAP), keep behaviour unless WORKLOG records a decision to change
  it, and tick it off in PORTING.md.

## Checks

```
dotnet build src/OverlayUtils.Core
dotnet run --project tests/OverlayUtils.SelfTest      # must print "All N checks passed."
```
On Windows also: `dotnet build OverlayUtils.sln`, then `scripts\build.ps1`
and the manual test list at the end of `docs/PORTING.md`.

After a session: tick PORTING.md, add a dated line to WORKLOG.md.
