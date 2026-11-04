# Stage 5 design: games library

Date: 2026-11-04. Status: draft for approval.

## Intent

One Games tab that lists everything the owner plays, from the couch or the
desk, and launches it with A. The Python app only knew Steam; the owner also
uses Epic, GOG, Xbox / Game Pass, emulators, and two launchers (Quiver,
Recomp launcher) that are covered for now by adding apps by hand.

Python reference: `reference/python/app.py` section `LIBRERIA GIOCHI`
(`docs/PYTHON-CODEMAP.md`): Steam installed games from `steamapps` manifests
across `libraryfolders.vdf`, non-Steam shortcuts from the binary
`userdata/<user>/config/shortcuts.vdf`, artwork from
`appcache/librarycache`, launch via `steam://rungameid/`, sort by
`LastPlayed`; the overlay's `_CoverGrid` (3 columns) and `_render_games`.

## Goals

1. **Sources**, each discovered only through what the launcher itself
   registers (registry keys, its own library/manifest files found from
   those) — **no hard-coded install paths** (owner requirement):
   - **Steam:** installed apps across all library folders + non-Steam
     shortcuts (as Python), with `LastPlayed`; launch `steam://rungameid/…`.
   - **Epic Games:** installed games from the launcher's manifest folder
     (located via its registry / ProgramData setting, not a fixed path);
     launch with the launcher's `com.epicgames.launcher://apps/<id>?action=launch` URL.
   - **GOG Galaxy:** installed games from GOG's registry entries; launch the
     game's registered exe (or Galaxy's launch command when registered).
   - **Xbox / Game Pass:** installed game packages (Windows package
     catalog: packages that are games, e.g. with a MicrosoftGame.config);
     launch through `shell:AppsFolder\<AUMID>`.
   - **Emulators:** common emulators detected through their installers'
     uninstall registry entries (e.g. RetroArch, Dolphin, PCSX2,
     DuckStation, PPSSPP, RPCS3, Cemu, xemu, Ryujinx-family); each is one
     tile that opens the emulator. (Portable emulators: "Add app…".)
   - **Apps added by hand:** "Add app…" (file picker on an exe) in
     Settings → Games; removable there. Covers Quiver, Recomp launcher,
     portable emulators and anything else.
2. **Games tab** (Python layout): 3-column grid of cover tiles, title under
   each; most recently played first (only Steam reports it; the rest follow
   alphabetically); D-pad/stick moves in the grid, A launches (overlay
   closes, focus goes to the game — existing HideAndRun path), LT/RT tabs.
3. **Source filter:** Y cycles All / Steam / Epic / GOG / Xbox / Emulators /
   Apps (only sources that have entries); the current filter is shown above
   the grid. Each tile has a small source badge.
4. **Artwork:** Steam's cached covers (as Python); Epic/GOG/Xbox artwork
   where the launcher stores it locally; otherwise the exe/package icon on a
   plain tile. No online artwork, no API keys or accounts.
5. **Refresh:** the library is read in the background when the overlay
   opens (Python `refresh_games`), never blocking the UI; the previous list
   shows meanwhile.
6. Settings → Games: per-source on/off (hide a source entirely), the
   manual apps list (add / remove), and the existing tab toggle for Games.

## Constraints

- No hard-coded paths, no network, nothing to install; read-only access to
  launcher files and the registry.
- Pure logic in Core and self-tested: binary/text VDF parsing (Steam),
  Epic manifest parsing, sorting and filtering, emulator matching from
  uninstall entries, grid navigation (rows/columns, wrap rules as Python
  `_CoverGrid.move`). Readers take a root path/registry abstraction so tests
  use fixture files in the temp folder.
- Launching goes through the existing overlay action path (hide, give focus
  back, 150 ms, run on a worker); failures are logged and notified.
- Follows earlier stages: config keys snake_case via `ConfigStore.Update`
  (`games_sources_hidden`, `games_manual_apps`), text via `Loc.T` in both
  languages, pad/mouse/keyboard all work (hover selection, click a tile =
  launch, wheel scrolls the grid).

## Non-goals

- ROMs as individual games; emulator front-end integration.
- Quiver launcher and Recomp launcher libraries (later; "Add app…" now).
- Online artwork (SteamGridDB etc.), achievements, playtime, store pages.
- Uninstalling or installing games.

## Anti-goals

- Anything that writes to a launcher's files or registry.
- A slow or frozen overlay while a large library loads.
- Launching the wrong game (tile identity must survive refreshes).

## Details settled before planning (my calls)

- The Games tab is a new grid row kind in the navigator (D-pad/stick move in
  2-D, A launches, mouse hover/click/wheel as elsewhere).
- On the Games tab, pad Y cycles the source filter (Remove is not used
  there); Back+Y stays the global mic mute when the overlay is closed.
- Steam tools and redistributables are filtered as in Python
  (`STEAM_EXCLUDED_APPIDS`, shortcut flags).
- Tile identity = source + launcher id (Steam appid / shortcut id, Epic
  app name, GOG product id, Xbox AUMID, exe path for apps and emulators),
  so selection and launch survive refreshes. The same game in two stores
  shows twice, once per source.
- Hand-added apps use the exe's file description (else file name) and icon.
- Empty state: a short message with "Settings → Games → Add app…".
- The Games tab is on by default (Python default).

## Left to the builder

- Exact Epic/GOG/Xbox artwork locations and which image sizes to use.
- How Xbox game packages are told apart from other apps.
- The emulator list and its matching rules (display names / exe names in
  uninstall entries).
- Tile size within the 420-DIP panel (Python: 3 columns).
