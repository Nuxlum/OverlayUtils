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
| UI (STA) | WPF `Dispatcher` | tray, menus, dialogs; later the overlay |
| Hotkeys | `HotkeyService` | own message loop; `RegisterHotKey` must be called on it, so requests are queued and the thread is woken with `PostThreadMessage` |
| Workers | `Task.Run` | MonitorSwitcher search, mode switching (waits on UAC) |

Rule: anything that touches UI goes through `TrayController.OnUi` (later a
shared dispatcher helper). Hotkey callbacks arrive on the hotkey thread.

## Configuration

`ConfigStore` owns `%APPDATA%\OverlayUtils\config.json`:
- property names match the Python file, so migrated configs load unchanged;
- unknown keys are kept (`[JsonExtensionData]`) - Spotify/Discord settings
  survive until their stage is ported;
- `Update(change)` saves atomically (temp file + move) and raises `Changed`;
- a corrupt file is backed up as `config.json.bad` and defaults are used.

## Localization

`Strings.resx` (English, neutral) and `Strings.it.resx`. Keys are dotted:
`Menu.*`, `Notify.*`, `Dialog.*`, `Tooltip.*`, `Language.*` (later
`Overlay.*`, `Settings.*`).
- Code: `Loc.T("Menu.Exit")`, `Loc.T("Tooltip.Monitor", name)`.
- XAML (stage 2): `{Binding [Menu.Exit], Source={x:Static loc:Loc.Instance}}`;
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
