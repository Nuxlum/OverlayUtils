# Audio, part 4a (devices) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An Audio tab with output/input pickers, mic mute, master volume and now playing; priority lists that auto-switch the Windows default output and input; media buttons on the pad; a pad volume/mic shortcut with an on-screen indicator; a volume cap per output device.

**Architecture:** Pure Core logic (priority rule, list editing, volume stepping with caps, new navigator row kinds and sub-pages, pad combos) is self-tested. A Core `AudioService` wraps Windows Core Audio through source-generated COM and runs for the whole session, applying the auto-switch rule on device events. The WinRT media session lives in the App behind a Core interface. The App wires both into the overlay, a small volume indicator window and the tray.

**Tech Stack:** C# / .NET 10, `GeneratedComInterface` COM (MMDevice API, IAudioEndpointVolume, IPolicyConfig), WinRT `Windows.Media.Control` (App), WPF, the dependency-free self-test.

**Spec:** `docs/superpowers/specs/2026-11-03-audio-design.md` — part **4a** only (see its "Build in two parts"). Python reference: `reference/python/app.py` sections `VOLUME…`, `USCITA AUDIO`, `MEDIA IN RIPRODUZIONE`.

## Global Constraints

- Nothing for the user to install; no NuGet packages. COM via `[GeneratedComInterface]`/`[GeneratedComClass]`, declared in Core (new file `src/OverlayUtils.Core/Interop/CoreAudio.cs`); Core stays plain `net10.0` and the self-test must run without Windows audio.
- WinRT media session only in the App. The App and both launchers move to `net10.0-windows10.0.19041.0` (needed for `Windows.Media.Control`); nothing else changes in them.
- Volume step 5 % on exact multiples (Python `VOLUME_STEP = 0.05`); raising unmutes. Caps per output device default 100 %, stepped in 5 % from 20 % to 100 %.
- Auto-switch re-evaluates only on device added / removed / state changed in that direction; never on default-device-changed. Default set for all three roles (console, multimedia, communications). Lists seeded with the current default first; new devices appended; auto-switch on by default.
- Pad: LB / RB / X = previous / next / play-pause while the overlay is open (any tab). Overlay closed and Pad combos on: Back+D-pad Left/Right = volume down/up with repeat while held (0.4 s, then 0.12 s); Back+Y = mic mute toggle.
- Audio events arrive on COM/worker threads: marshal UI work to the UI thread; never block the UI thread on audio calls; `Log.Error`, never throw.
- New config keys (snake_case, via `ConfigStore.Update`): `audio_output_order`, `audio_input_order` (lists of `{ "id", "name" }`), `audio_auto_output`, `audio_auto_input` (bool, default true), `audio_volume_caps` (map id → percent).
- New tab id `"Audio"` (stored in `hidden_tabs` like the others), after Controls. Text via `Loc.T`, keys in both `.resx`.
- Manual-testing rules: never change the user's audio devices, volumes or Windows settings from a script; never touch the user's running OverlayUtils; no synthetic input. Read-only smoke checks only; anything that switches devices is in the owner's test list.
- Checks after every task: `dotnet build OverlayUtils.sln` 0 warnings; self-test prints `All N checks passed.` Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Headset turned on/off repeatedly or Bluetooth flapping:** each event re-evaluates once; never a switch loop, never a switch when the top available device is already default. Test in Task 1 (`Target` returns null when already default).
2. **Every ranked device unavailable** (all unplugged): no switch, no crash. Test in Task 1.
3. **Cap lower than the current volume when a device becomes default:** volume drops to the cap; raising stops at the cap; lowering still works. Test in Task 1.
4. **Reorder interrupted** (B, leaving the page, overlay hidden while a device is held): order unchanged, nothing saved. Test in Task 2.
5. **Media session appearing/disappearing while the Now playing row is selected:** selection moves to a neighbour, no action on the wrong row. Test in Task 2 (`Refresh` with the media row hidden).

---

### Task 1: Audio rules (pure)

**Files:**
- Create: `src/OverlayUtils.Core/Audio/AudioRules.cs`
- Modify: `src/OverlayUtils.Core/Config/AppConfig.cs` (keys above; `public sealed record AudioDeviceRef(string Id, string Name)`)
- Test: `tests/OverlayUtils.SelfTest/Program.cs` (section `// --- Audio rules ---`)

**Interfaces — Produces** (namespace `OverlayUtils.Core.Audio`):

```csharp
public static class AudioRules
{
    public const double Step = 0.05;
    /// <summary>Device to make default, or null if the current default is already the right one or none is available.</summary>
    public static string? Target(IReadOnlyList<AudioDeviceRef> ranked, IReadOnlySet<string> available, string? currentDefault);
    /// <summary>Appends devices not yet in the list (keeps order and names; refreshes the name of known ids).</summary>
    public static List<AudioDeviceRef> Merge(IReadOnlyList<AudioDeviceRef> list, IEnumerable<AudioDeviceRef> seen);
    /// <summary>Seed for a first run: current default first, then the others in the given order.</summary>
    public static List<AudioDeviceRef> Seed(IEnumerable<AudioDeviceRef> devices, string? currentDefault);
    /// <summary>One step up (+1) or down (-1) on exact 5 % multiples, clamped to [0, cap].</summary>
    public static double StepVolume(double current, int direction, double cap);
    public static double CapFor(IReadOnlyDictionary<string, int> capsPercent, string deviceId); // 1.0 if none
}
```

- [ ] **Step 1: Failing checks:** `Target` — ranked [A,B,C], available {B,C}, default C → "B"; default B → null; available {} → null; ranked empty → null; available {D} only (unranked) → null. `Merge` — [A,B] + seen [B',C] → [A,B',C] (B renamed). `Seed` — devices [X,Y,Z], default Y → [Y,X,Z]. `StepVolume(0.52, +1, 1)` → 0.55; `(0.52, -1, 1)` → 0.50; `(0.98, +1, 1)` → 1.0; `(0.03, -1, 1)` → 0.0; `(0.70, +1, 0.6)` → 0.6; `(0.70, -1, 0.6)` → 0.6 (one step down, then clamped to the cap); `CapFor({"A":60}, "A")` → 0.6, `"B"` → 1.0. Config: defaults (`AudioAutoOutput == true`, empty lists) and snake_case names in JSON.
- [ ] **Step 2: Run — fails to compile.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run — passes.**
- [ ] **Step 5: Commit** `Add audio rules`

---

### Task 2: Navigator: slider, media, sub-pages, reorder; pad media and volume combos

**Files:**
- Modify: `src/OverlayUtils.Core/Overlay/OverlayItem.cs`, `OverlayNavigator.cs`, `src/OverlayUtils.Core/Input/PadInterpreter.cs`, both `.resx` (footer keys)
- Test: `tests/OverlayUtils.SelfTest/Program.cs`

**Interfaces — Produces:**
- `ItemKind` gains `Slider`, `Media`, `Reorder`. `OverlayPage` gains `Sub`.
- `OverlayCommand` gains `PrevTrack`, `NextTrack`, `PlayPause`, `Remove`.
- `OverlayItem` gains `Func<IReadOnlyList<OverlayItem>>? SubPage` (Action row: A opens it as `Page == Sub`, title = row label; B returns to the page it came from), and for `Reorder` rows `Action<int>? Move` (move this entry by ±1 in its list) plus `Action? Commit`, `Action? Cancel`; `Action? RemoveEntry` (Remove command; only if set).
- `OverlayNavigator`: `public bool Holding { get; }` (a Reorder row is picked up). Global `OverlayCommand.PrevTrack/NextTrack/PlayPause` return `RunHere(media action)` through a constructor-supplied `IMediaControl? media`, on any page. Create `src/OverlayUtils.Core/Audio/IMediaControl.cs` here: `public sealed record NowPlaying(string Title, string Artist, string App, bool Playing)` and `public interface IMediaControl { NowPlaying? Current { get; } event Action? Changed; void Previous(); void Next(); void PlayPause(); }`.
- `PadActionKind` gains `VolumeDown`, `VolumeUp`, `MicMute`.

Rules:
- **Slider:** Left/Right and `LeftRepeat`/`RightRepeat` → `RunHere(() => Step(±1))`; A → `RunHere(Toggle)` (mute). **Choice with `Toggle`:** A → `RunHere(Toggle)` instead of stepping (Input row: A = mic mute). **Media:** Left/Right → `RunHere(Previous/Next)`, A → `RunHere(PlayPause)`.
- **Reorder:** A picks up (`Holding = true`); while holding, Up/Down → `RunHere(() => Move(∓1))` and the selection follows the moved entry; A → `RunHere(Commit)`, `Holding = false`; B, page change, tab change, `Reset` → `RunHere(Cancel)` (or the controller calls it), `Holding = false`; every other command is ignored while holding. `Remove` (not holding) → `RunHere(RemoveEntry)` if set.
- **Selection identity:** items may carry `string? Key`; `Refresh` keeps the selection on the item with the same `Key` when lists change, else nearest by index (existing rule).
- **Pad:** while open, LB/RB/X → `Command(PrevTrack/NextTrack/PlayPause)` (not when part of Back+RB); Y → `Command(Remove)`. Closed, combos on, Back held: DpadLeft/DpadRight → `VolumeDown/VolumeUp` with repeat while both held (stops when Back or the D-pad is released); Y → `MicMute`. Duplicate-pad filtering applies as for other actions.

- [ ] **Step 1: Failing checks:** slider steps with repeat and A mutes; Choice-with-Toggle A toggles; media row actions; sub-page opens from Settings and B returns to Settings at the same row; reorder pick/move/commit — and B / NextTab / Reset while holding cancel (Review Focus 4) with the Move calls observed; `Holding` blocks Down from leaving the row; `Remove` calls `RemoveEntry`; global media commands call `IMediaControl` from Tabs and Settings; `Refresh` keeps selection by `Key` when an item before it disappears, and moves to a neighbour when the selected media row hides (Review Focus 5); footer for Slider shows `← →` Volume and A Mute (new keys `Overlay.Footer.Volume`, `Overlay.Footer.Mute`, `Overlay.Footer.Move`, `Overlay.Footer.PickUp`, `Overlay.Footer.Drop`); pad: LB/RB/X open → media commands; Back+Left closed → `VolumeDown` then repeats after 0.4 s, stops on Back release; Back+Y → `MicMute`; combos off → nothing.
- [ ] **Step 2: Run — fails.** **Step 3: Implement.** **Step 4: Run — passes.**
- [ ] **Step 5: Commit** `Navigator and pad support for audio rows`

---

### Task 3: Core Audio service

**Files:**
- Create: `src/OverlayUtils.Core/Interop/CoreAudio.cs` (COM interfaces/structs: `IMMDeviceEnumerator`, `IMMDeviceCollection`, `IMMDevice`, `IPropertyStore` (friendly name), `IMMNotificationClient` (implemented, `[GeneratedComClass]`), `IAudioEndpointVolume` + callback, `IPolicyConfig`), `src/OverlayUtils.Core/Audio/AudioService.cs`

**Interfaces — Produces:**

```csharp
public enum AudioFlow { Output, Input }
public sealed class AudioService : IDisposable
{
    public AudioService(ConfigStore config);
    public void Start();                                    // own STA/MTA worker thread for COM; registers notifications
    public event Action? Changed;                           // devices, default, volume or mute changed (worker thread)
    public IReadOnlyList<AudioDeviceRef> Available(AudioFlow flow);
    public AudioDeviceRef? Default(AudioFlow flow);
    public void SetDefault(AudioFlow flow, string id);      // manual pick (all roles)
    public void Cycle(AudioFlow flow, int direction);       // next/previous available, for the pickers
    public double Volume { get; }  public bool Muted { get; }        // default output
    public void StepVolume(int direction);                  // AudioRules.StepVolume with the device cap; raising unmutes
    public void ToggleMute();
    public bool MicMuted { get; } public void ToggleMicMute();       // default input
}
```

Behaviour: on start, seed missing lists (`AudioRules.Seed`) and merge seen devices into config; on device added/removed/state changed, merge, then if `audio_auto_*` is on apply `AudioRules.Target` (no action on default-changed events); when the default output changes, rebind the endpoint volume and apply its cap if the volume is above it. All COM calls on the service thread (queue work to it); public members safe from any thread.

- [ ] **Step 1: Implement interop and service.** (No self-test for COM; the rules it uses are tested in Task 1.)
- [ ] **Step 2: Build** 0 warnings.
- [ ] **Step 3: Read-only smoke check** in a scratchpad console (not committed, config redirected to the scratchpad as in stage 3): start the service with auto-switch **off** in that scratch config, print available outputs/inputs, defaults, volume, mute for 3 s; confirm nothing changed on the system. Report the output.
- [ ] **Step 4: Commit** `Add Core Audio service`

---

### Task 4: Media session (App)

**Files:**
- Modify: `src/OverlayUtils.App/OverlayUtils.App.csproj`, `src/OverlayUtils/OverlayUtils.csproj`, `src/OverlayUtils.UIAccess/OverlayUtils.UIAccess.csproj` (TFM `net10.0-windows10.0.19041.0`)
- Create: `src/OverlayUtils.App/Audio/MediaSession.cs` implementing `IMediaControl` (Task 2) (`GlobalSystemMediaTransportControlsSessionManager`, current session; Previous/Next/PlayPause via the session; app name from the session's source app id, Python `MEDIA_APP_NAMES` mapping for common ones)

- [ ] **Step 1: Implement; Step 2: build** 0 warnings and confirm `dotnet publish` of the standard launcher still works (scratchpad output). **Step 3: Commit** `Add media session`

---

### Task 5: Audio tab, Settings → Audio, indicator, wiring

**Files:**
- Modify: `src/OverlayUtils.Core/Overlay/OverlayContent.cs` (Audio tab + Settings section + sub-pages), `Glyphs.cs` (speaker, volume levels, mute, mic, mic off, play, pause, prev, next, music — Python `ICON_*`), both `.resx`
- Create: `src/OverlayUtils.App/Overlay/VolumeIndicator.xaml(.cs)` (non-activating, topmost, click-through, ~1.5 s, on the foreground window's monitor, bar + icon + percent)
- Modify: `src/OverlayUtils.App/Tray/TrayController.cs` (start `AudioService` and `MediaSession`, map `VolumeDown/Up/MicMute` pad actions to the service + indicator, re-render the overlay on `Changed`), `OverlayController.cs` if needed
- Test: `tests/OverlayUtils.SelfTest/Program.cs`

`OverlayContent` gains a constructor parameter `IAudioView audio` (Core interface implemented by an adapter over `AudioService`, so the self-test can fake it: `Available(flow)`, `Default(flow)`, `Cycle(flow, dir)`, `Volume`, `Muted`, `StepVolume(dir)`, `ToggleMute()`, `MicMuted`, `ToggleMicMute()`) and `IMediaControl? media`.

Audio tab rows: Output (Choice: value = default output name, Left/Right `Cycle`), Input (Choice: value = default input name + "muted" when muted, Left/Right `Cycle`, A `ToggleMicMute`), Volume (Slider: value %, icon by level/mute, A mute), Now playing (Media: visible when `media.Current` is not null; label title, value artist · app; play/pause glyph). Settings → **Audio** section: "Output order" and "Input order" (Action with `SubPage`: Reorder rows over the config list, connected/disconnected shown in the value, `RemoveEntry` only for disconnected), "Auto-switch output", "Auto-switch input" (Toggles), "Max volume" (Choice for the current default output, 20 %–100 % in 5 %). Info → Shortcuts: Back + ← / → volume, Back + Y mic, LB / RB / X media.

- [ ] **Step 1: Failing checks** with a fake `IAudioView` and fake `IMediaControl`: Audio tab exists after Controls and its rows are in order; Volume row Right calls `StepVolume(+1)`, A calls `ToggleMute`; Input A calls `ToggleMicMute`; Output Right calls `Cycle(Output, +1)`; Now playing hidden with no media, visible with one, A calls `PlayPause`; Output order page lists the config entries, moving down then committing saves the new order in config, cancelling leaves config unchanged; Remove deletes a disconnected entry and is absent for connected ones; Max volume row stores `audio_volume_caps[id]` in 5 % steps within 20–100; hiding the Audio tab via Settings works like other tabs.
- [ ] **Step 2: Run — fails. Step 3: Implement** (Core first, then the App indicator and wiring). **Step 4: Run — passes; build 0 warnings.**
- [ ] **Step 5: Commit** `Audio tab, settings and volume indicator`

---

### Task 6: Docs and owner test list

- [ ] PORTING stage 4: split into 4a `[~]` (items above) and 4b `[ ]` (channels, per-app, custom channels, per-device mix, presets). "Testing on Windows (stage 4a)": headset on/off with auto-switch (and with Sonar's devices ranked first/last), manual pick then plugging a device, reorder and remove from the pad, Max volume on headphones, Back+D-pad volume with the indicator over a game, Back+Y mic mute, LB/RB/X with YouTube Music in a browser, volume changed from the Windows flyout updates the open overlay.
- [ ] ARCHITECTURE: "Audio" section (rules, service thread, COM in Core, media session in App, TFM change). README EN + IT: Audio tab, pad shortcuts, priority lists. WORKLOG: session line; decisions (TFM bump, IPolicyConfig, button choices Back+D-pad/Back+Y, caps not applied to Windows' own keys).
- [ ] Self-test + build; commit `Stage 4a docs`.
