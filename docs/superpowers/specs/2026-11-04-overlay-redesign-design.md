# Overlay redesign: full-screen "Focus" layout

Date: 2026-11-04. Status: draft for approval.

## Intent

Today the overlay is a 420x720 side panel (x1.35 on the TV) docked to the
left or right edge. The owner's point: while you move around the overlay the
game behind it does not matter, so a small window wastes the screen. The
redesign makes the overlay take the whole monitor, look clearly better, and
feel alive (motion), **without** filling the screen with more stuff than the
overlay actually has. Information density stays about the same; it gets
bigger, calmer and better arranged.

Direction agreed in the mockups (`.superpowers/brainstorm/…/home.html`,
not in git): **B "Focus"**, a left icon rail, a reading column, and a stage
on the right that reacts to the selected row, plus a new **Home** page of
shortcuts (taken from direction C). Direction A (centred column, tabs on top)
was rejected: it covers the middle of the screen and wastes both sides.

## Goals

1. **Full-screen frame** on the monitor the overlay opens on (same monitor
   choice as today). Three zones, left to right:
   - **Rail** (~7% of the width): one icon per page, the current one lit
     with the accent bar. Pages: Home, Controls, Audio, Games, and Settings as
     a gear at the bottom of the rail (see "Calls I made" for how the gear is
     reached).
   - **Column** (~38-42% on the TV, ~30% at the desk): page title (with a
     small accent "eyebrow" line above it), then the rows, then the footer
     hints, at the bottom of the column.
   - **Stage** (the rest): the status corner at the top right; a context area
     at the bottom that shows what is useful for the selected row (see 4).
2. **Backdrop**: the game shows through as frosted glass (a blurred,
   colour-boosted screenshot taken at open, see "How it is built"), with a
   dark gradient: dark behind the rail and column (about 88% → 70%),
   lighter towards the right edge (about 35% → 20%), so text is always
   readable and the stage "breathes". Games uses a top-to-bottom gradient
   instead (see 5).
3. **Home page** (new, first page, where the overlay opens): shortcut tiles
   in a 2-column grid inside the column; wide tiles span both columns.
   In this order:
   - **Volume** (wide): output device name, percentage, a bar. ← → change it
     and A mutes, exactly as the Audio page's volume row.
   - **Now playing** (wide): art, title, artist · app, prev / play-pause /
     next. Same commands as the Audio page's media row. Hidden when nothing
     is playing.
   - **Microphone**: the Audio page's input row as a tile: shows the device
     and on / muted; A toggles mute, ← → switch the input device, as on the
     row.
   - **Screen**: Desk / TV segmented, as the Controls monitor row. Hidden
     when that row is hidden (no profiles / roles).
   - **Close window**: Alt+F4 to the window behind, with the same guard as
     today (`PreviousWindowIsForeground`).
   - **Steam Big Picture**: Open / Exit segmented, as the Controls row.
   Each tile is the **same `OverlayItem`** as the row it mirrors (same
   callbacks, labels, visibility), only drawn as a tile. Nothing on Home is
   new behaviour. A tile whose item is not visible is left out and the grid
   closes up.
4. **Stage context** for the selected row:
   - default: a card with the row's existing `Description` (and value, if
     any), the text that the current panel shows in its description line;
   - **Monitor row / Screen tile**: a small picture of the Desk and TV
     monitors, the active one lit;
   - **Audio page channel and volume rows**: a visual mixer, one vertical bar
     per channel plus master, the selected one outlined; the card lists the
     apps in the selected channel;
   - **Now playing**: large album art;
   - **Volume tile**: device name and the cap for that device, if any.
   Cards cross-fade when the selection changes.
5. **Games page** uses column + stage together (full width after the rail):
   - a **hero** at the top: the selected game's title, large, its source
     badge, and a Play button hint;
   - the **source filter** as a row of pills (All, Steam, Epic, …) above the
     covers, the current one lit; Y still cycles it, as now (the filter pill
     in the grid header goes away);
   - the **cover shelf**: more columns than today (about 8 on the TV, more
     at the desk, the builder fits it to the width), the selected cover lifts
     (scale ~1.1) and glows; rows other than the selected one are slightly
     dimmed;
   - the backdrop **tints** with the selected game's art colour (a dominant
     colour taken when the cover is decoded, cached with it), cross-fading on
     selection change.
   Navigation, grid memory, launch by id and art loading for the visible
   range stay exactly as they are.
6. **Status corner** (top right of the stage, every page): current output
   device, pad battery **as a percentage** when SDL reports one (glyph only
   otherwise, nothing for wired pads), and the clock (large, light weight).
7. **Current game** (new feature): when the overlay opens, the window that
   was in front is matched against the games library. On a match:
   - Home's eyebrow shows "<Title> · <time>", where the time is how long the
     game's process has been running ("1 h 12 min");
   - otherwise the eyebrow reads "Overlay".
   Matching: the foreground window's process exe path is under a library
   entry's install folder. This needs an **install folder on `GameEntry`**
   for every source (Steam `installdir` from the manifest under its library
   folder, Epic `InstallLocation`, GOG `path`, Xbox `InstalledPath`,
   emulator install dir, manual apps' exe folder). Matching is read-only:
   process path through `QueryFullProcessImageName` with limited query
   rights; no injection, no hooks. Any failure (access denied, no match,
   launcher process in front) shows nothing. Longest matching folder wins
   (so a game inside a launcher's folder beats the launcher).
8. **Motion**, all under 250 ms, so the pad never feels slow:
   - open: backdrop fades in (~180 ms) while rail and column slide in from
     the left with a small stagger; close: the reverse, faster;
   - **one** selection highlight that glides between rows / tiles
     (ease-out), instead of rows switching on and off;
   - page change: the old page slides a little and fades out, the new one
     comes in from the side of the button pressed (LT from the left, RT from
     the right; LT / RT switch pages as they switch tabs today, LB / RB stay
     previous / next track);
   - cover lift on selection; backdrop tint cross-fade on Games;
   - every animation is off when Windows "Animation effects" is off
     (`SystemParameters.ClientAreaAnimation`); things then just appear.
9. **Same layout at the desk and on the TV**, only scaled: the existing
   size setting (Normal / Large / Auto) keeps working, Auto = Large on the TV
   profile, as now. Normal makes text and rows smaller and the column
   narrower (~30%).
10. **Mouse**: hover and click work as today on rows, tiles, rail icons,
    filter pills and footer hints; the wheel scrolls; **a click on the
    backdrop outside the rail, column and stage cards closes the overlay**
    (like B).

## Non-goals

- No change to what the overlay can do on Controls, Audio, Games and
  Settings, nor to their rows, order or wording, except as listed here.
- No second, compact layout for the desk. A quick volume change while
  working stays on the pad combos and the tray menu.
- No total play time or "recently played" history (only the running
  session's time, from the process start).
- No new settings except the removal below.

## Anti-goals (fails even if it works)

- Any input lag: opening must feel at least as fast as today, and pad
  navigation must never wait for an animation (a new command cancels or
  completes the running animation at once).
- Full-screen rendering that stutters (big translucent layered window
  repainting every frame) or drops the game's frame rate noticeably while
  the overlay is closed. Closed = hidden, nothing animating.
- Touching the game: no injection, hooks or reading game memory for the
  current-game feature.
- A screen that looks busier than today. If in doubt, leave it out.
- Asking friends to set anything up.

## Constraints (from CLAUDE.md and earlier decisions)

- Text only through `Loc.T` with keys in both `Strings.resx` and
  `Strings.it.resx` (new: Home title, tile labels, eyebrow text, "playing
  for" time formats, battery percent, Settings rail tooltip if any).
- Settings through `AppConfig` / `ConfigStore.Update`.
- UI work on the UI thread; process lookup for the current game in
  `Task.Run`, never blocking the open.
- Win32 declarations in `Core/Interop/Native.cs`.
- `OverlayNavigator` stays the single owner of selection and commands; the
  view only draws its state. Core logic (Home tiles, tile navigation, game
  matching, layout maths) gets self-test checks.
- Build 0 warnings, self-test passes.

## Decisions taken in the conversation

- Full screen on every monitor, same layout, scaled (owner agreed).
- Rail on the left; Settings joins the rail as a gear at the bottom.
- Home page first, with the six tiles above (owner agreed).
- Pad battery percentage and current game added (owner asked).
- **Overlay side setting removed** (left / right): the rail is always on the
  left. The Settings row and its strings go; `overlay_side` stays in
  `AppConfig` only if removing it would lose other data, otherwise it is
  dropped and an old value simply lands in `Extra` and is ignored
  (builder's choice; my call: drop the property).

## Calls I made (owner can overrule)

- **Pad buttons unchanged:** LT / RT switch pages (as tabs today), LB / RB
  stay previous / next track, X / Y / Back as now. The rail is not reached
  with D-pad left, because ← → already change values on segmented and slider
  rows. LT / RT cycle Home, Controls, Audio, Games in rail order.
- **Settings gear is not in the LT / RT cycle:** Settings stays a separate
  page as today (Back-hold / F2, or a click on the gear). The gear is lit
  while on Settings, Info or their sub-pages; B goes back as now.
- **Home tiles behave exactly like their rows** (same item, same A and
  ← →); none opens another page. The Audio page is one LT / RT press away.
- **Hidden tabs:** Home can be hidden like the other pages (Settings →
  Tabs). The overlay opens on the first visible page. A Home tile is still
  shown even if its source page is hidden (it is just a shortcut).
- **Covering the taskbar:** the window takes the whole monitor (monitor
  bounds, not the work area), so the taskbar is under the backdrop too.
- **Fallback without the frosted image** (capture failed or black): the
  plain dark #080A10 background; everything else the same. Works the same
  on Windows 10 and with Transparency effects off.
- **Icons**: Segoe Fluent Icons / MDL2 glyphs as now (the mockups' emoji are
  stand-ins).
- **Settings and Info pages** use the same frame: column with the rows,
  stage with the description card. The current Back-hold / F2 shortcut to
  Settings stays.
- The volume indicator (the small popup on volume combos) is unchanged.

## How it is built (the parts that are decided)

- **Window**: the existing `OverlayWindow`, resized to the monitor instead
  of 420x720. `OverlayLayout.Place` returns the monitor rectangle; the
  column width and type scale come from the size factor.
- **Backdrop (decided after two spikes, owner-approved 2026-11-04):** the
  DWM acrylic backdrop was rejected (fixed, far too strong blur, grey wash,
  flicker on show). Instead a **frosted screenshot**: when the overlay
  opens, the monitor is captured once (GDI BitBlt, kept in memory only,
  never saved), processed on a worker and shown as the window's own
  background. Pipeline and the owner's chosen values: 1/4 downscale (4x4
  average) → 3-pass box blur with radius 32/4 = 8 at quarter scale →
  saturation 1.3 (`luma + (c - luma) * 1.3`) and brightness x1.08 → shown
  stretched with high-quality scaling; on top, a white frost at opacity
  0.07, a fine static grain (128 px tiled grey noise) at 0.05, then the
  dark gradient (#080A10 at alpha E0 / B3 / 59 / 33 at 0 / 0.38 / 0.72 /
  1). The window appears at once with an opaque #080A10 background and the
  UI; the frosted image fades in (150 ms) when ready (measured: capture
  ~25 ms, processing ~20 ms at 2560x1440). The window is opaque and not
  layered. Text on the stage (status, cards) sits on dark glass cards,
  since the right side can be bright. If the capture fails or comes back
  black (exclusive fullscreen, protected content), the plain #080A10
  background stays.
- **Exclusive-fullscreen games** behave as today (a topmost window over
  them makes Windows leave exclusive mode or not show; no new handling).

## Left to the builder

- Exact sizes, spacing, type ramp, colours (start from `OverlayTheme.xaml`
  keys and the Windows accent; add keys rather than hard-coding).
- How the gliding highlight is built (one adorner / overlay element moved
  by the view from the selected container's position is the expected way).
- How many cover columns fit at each scale and width.
- Dominant-colour method for the Games tint (a cheap average of a
  downscaled cover is fine).
- Ordering of the work into steps; the spike comes first.

## Testing

- Self-test: Home items and their visibility rules; tile navigation in the
  2-column layout with wide tiles; current-game matching (longest folder,
  case-insensitive paths, no match, launcher vs game inside it); install
  folder parsing per source; layout maths for both sizes.
- Manual (owner, on the TV and at the desk): open/close feel, each page,
  Games tint and lift, mouse click on backdrop, Animation effects off,
  Transparency effects off, a borderless game and an exclusive-fullscreen
  game behind, current game shown for a Steam, an Epic and a manual app,
  battery on a wireless pad and nothing on a wired one.
