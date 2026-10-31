# OverlayUtils

*[Leggi in italiano](README.it.md)*

A Windows tray utility with an overlay you drive from a gamepad, keyboard or
mouse, and that can sit on top of fullscreen games:

- **Monitor setups** - switch between saved layouts (e.g. desk / TV) with
  [Monitor Profile Switcher](https://monitorprofileswitcher.com/),
  `Ctrl+Alt+Down` / `Ctrl+Alt+Up` or the tray menu
- **Home key remapping** - make the keyboard's "Browser Home" key act as the
  Windows key or Alt+Tab
- **Overlay** *(being ported)* - system controls, Steam library, Spotify and
  Discord, navigable with any SDL-supported controller
- **English and Italian**, following the Windows language or chosen in the menu

> **Status:** OverlayUtils is being ported from Python to .NET in stages.
> Stage 1 (tray, hotkeys, monitor setups, the two modes) is written; the
> overlay and the rest follow. See [docs/PORTING.md](docs/PORTING.md). The
> complete Python version is kept in `reference/python/` until then.

## Two modes

| | Standard | Full |
|---|---|---|
| Setup | unzip and run `OverlayUtils.exe` | one click in the tray menu, one admin prompt |
| Overlay over desktop, windows, borderless games, Big Picture | yes | yes |
| Overlay over **fullscreen** games | no | yes |
| Certificate installed on the PC | no | yes (removed again when you turn it off) |

Full mode needs the Windows **UIAccess** permission, which Windows only grants
to signed programs installed in Program Files. OverlayUtils handles both when
you choose *Overlay over fullscreen games -> Enable...*, and undoes them with
*Disable...*. Trusting a certificate means Windows accepts anything signed
with it, so only enable full mode for builds from someone you trust.

## Using it

See [README.txt](README.txt) (the same file ships in the zip).

Coming from **AppUtili**? Settings, Spotify and Discord logins are copied
automatically on first start, and the tray menu offers to remove the old
install.

## Building

Requirements: Windows 10/11, .NET 10 SDK (or Visual Studio 2026 with the
.NET desktop development workload).

```powershell
# once: create your signing key (no admin needed)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\certificate.ps1

# build, sign and zip -> dist\OverlayUtils and dist\OverlayUtils-<version>.zip
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
```

`-NoSign` builds without a certificate (standard mode only). For day-to-day
work open `OverlayUtils.sln` and run the `OverlayUtils` project; checks for
the core library run anywhere:

```
dotnet run --project tests/OverlayUtils.SelfTest
```

Each person who builds signs with their own key, so nobody has to trust
someone else's certificate. A build given to others as a zip asks them to
trust *your* certificate when they enable full mode.

## Repository guide

[CLAUDE.md](CLAUDE.md) is the starting point for contributors (and coding
agents): layout, conventions and where each topic is documented.
