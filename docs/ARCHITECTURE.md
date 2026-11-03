# Architecture

How the .NET OverlayUtils is put together. What is ported so far is in
`docs/PORTING.md`; for features not ported yet, the Python reference and
`docs/PYTHON-CODEMAP.md` describe the intended behaviour.

## Projects

```
OverlayUtils.sln
├─ src/OverlayUtils.Core       net10.0, no NuGet. Config, localization, Win32 interop,
│                              services. Builds on any OS; runs on Windows.
├─ src/OverlayUtils.App        net10.0-windows, WPF + WinForms. AppHost (startup),
│                              tray, mode switching; overlay from stage 2.
├─ src/OverlayUtils            Launcher: OverlayUtils.exe          (uiAccess=false)
├─ src/OverlayUtils.UIAccess   Launcher: OverlayUtils.UIAccess.exe (uiAccess=true)
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

Rule: anything that touches UI goes through `TrayController.OnUi`. Hotkey callbacks arrive on the hotkey thread.

## Overlay

Split like the rest: everything that can run without WPF is in Core and
covered by the self-test.

| Core (`Overlay/`, `Platform/`) | App (`Overlay/`) |
|---|---|
| `OverlayContent`: tabs and rows (Controls, Settings, Info) | `OverlayController`: open / close, focus, placement, runs effects |
| `OverlayNavigator`: selection, pages, expanders, Power confirm | `OverlayWindow` + `OverlayViewModel`: drawing, key and mouse input |
| `OverlayLayout`: size factor and `Place` (physical px, clamped) | `OverlayTheme.xaml`, `WindowEffects`: acrylic, border, rounded corners |
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

The overlay itself lives on the UI thread. The view runs a 250 ms timer that
calls `ExpirePending`, so a Power option armed with Enter or a click disarms
after 3 s.

### Focus

On open the controller remembers the foreground window (`_prev`), skipping
shell windows (taskbar, desktop, Start...: `WindowFocus.ShellWindows`) and its
own, then takes foreground with `WindowFocus.Force`. It checks focus every
150 ms and hides when it is lost. Esc gives focus back to `_prev` if it is
still alive. If the overlay never gets focus it stays open until toggled (as in
Python). Close window sends Alt+F4 to the previous app and does nothing when
there is none (`HasPreviousWindow`: opened over the taskbar or desktop).

### Placement and size

The window opens on the monitor of the foreground window (`DisplayArea`, cursor
as fallback), at the chosen side of its work area. `OverlayLayout.SizeFactor`
maps `overlay_size` (Auto looks at the active monitor profile) to a factor;
`Place` returns the physical rectangle clamped to the work area. The content
scales by the factor and scrolls when clamped. The XAML size is only the
initial one. The window is per-monitor DPI aware and follows `WM_DPICHANGED`.

### Look

- Backdrop: acrylic on Windows 11 build 22621 or newer; older builds (Windows 10,
  11 21H2) get a solid background. It is re-applied on every show, because DWM
  drops it otherwise. Rounded corners and border colour (as in Python) go
  through DWM too.
- Accent: read from the Windows accent palette (`AccentPalette`). It is used
  only if its contrast against the panel (#202020) is at least 4.5:1, else the
  lightest palette entry, else #4CC2FF. The selection must always be visible.
- Transparency effects off in Windows -> solid; animation effects off -> no slide.

### Hotkey

Hotkey id 2 (reserved in stage 1) is now Shift+F1 via `RegisterHotKey`; a left
click on the tray icon does the same. Both call `OverlayController.Toggle`.

## Configuration

`ConfigStore` owns `%APPDATA%\OverlayUtils\config.json`:
- property names match the Python file, so migrated configs load unchanged;
- unknown keys are kept (`[JsonExtensionData]`) - Spotify/Discord settings
  survive until their stage is ported;
- overlay keys: `overlay_size` (`normal` / `large` / `auto`; auto picks by the
  active monitor profile) and `overlay_side` (`left` / `right`);
- `Update(change)` saves atomically (temp file + move) and raises `Changed`;
- a corrupt file is backed up as `config.json.bad` and defaults are used.

## Localization

`Strings.resx` (English, neutral) and `Strings.it.resx`. Keys are dotted:
`Menu.*`, `Notify.*`, `Dialog.*`, `Tooltip.*`, `Language.*` `Overlay.*` (rows,
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
| `%APPDATA%\OverlayUtils\spotify_token.json` etc. | migrated now, used from stages 6-7 |
| `%ProgramFiles%\OverlayUtils\` | full-mode install + `full-mode.json` |
| `%LOCALAPPDATA%\Programs\OverlayUtils\` | where the app moves when leaving full mode |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\OverlayUtils` | start-up entry |

## Build

`scripts\build.ps1`: self-test, then `dotnet publish` both launchers
self-contained (win-x64) into `dist\OverlayUtils`, sign both exes with the
certificate from `scripts\certificate.ps1`, zip with `README.txt`. No
single-file publish: it extracts native libraries to a user-writable temp
folder at every start, which a UIAccess process must not load from.
