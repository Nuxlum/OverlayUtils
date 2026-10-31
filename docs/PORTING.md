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

### Stage 2 - overlay shell  `[ ]`
- [ ] WPF overlay window: borderless, topmost, per-monitor DPI, on the foreground window's monitor  *(OVERLAY GAMEPAD: `_Overlay.show`, `_monitor_of`)*
- [ ] Focus handling: take foreground, remember previous window, auto-hide on focus loss, run actions after focus returns
- [ ] Shift+F1 and tray click toggle (hotkey id 2 is reserved)
- [ ] Keyboard and mouse navigation; Settings and Info pages
- [ ] Controls tab: monitor, Big Picture, power, show desktop, close window  *(OVERLAY_ITEMS)*
- [ ] Verify the UIAccess z-order quirk (`_repair_frame` in Python) does not apply to WPF

### Stage 3 - gamepad  `[ ]`
- [ ] SDL3 GameController input, hot-plug  *(CONTROLLER)*
- [ ] Global combos (monitor, Win+D, Alt+F4, audio prev/next), overlay toggle on Back+RB release
- [ ] Overlay navigation with repeat, Select-hold for settings
- [ ] Pad battery (XInput)  *(VOLUME, BATTERIA DEL PAD...)*

### Stage 4 - audio and media  `[ ]`
- [ ] Volume and mute, Windows output switching (Core Audio)  *(VOLUME..., USCITA AUDIO)*
- [ ] SteelSeries Sonar detection and output name
- [ ] Now playing card (GlobalSystemMediaTransportControls)  *(MEDIA IN RIPRODUZIONE)*

### Stage 5 - Steam library  `[ ]`
- [ ] Installed games, non-Steam shortcuts (binary VDF), artwork, launch  *(LIBRERIA GIOCHI)*

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
