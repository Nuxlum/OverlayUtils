# Audio, part 4b (mixing) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Channels (Game, Chat, Media, Other + custom ones with name and icon) with their own volume, per-app volumes inside them, automatic program-to-channel assignment the user can override, the mix remembered per output device, and mix presets.

**Architecture:** Pure Core rules (classification, effective volume = channel × app, per-device mix, presets, channel editing) are self-tested. The existing `AudioService` worker gains Windows audio sessions (enumerate, notifications, per-session volume) and applies the stored mix when a session appears or the user changes it. The overlay gets a Channels section and Settings pages; the App adds a small name-entry dialog.

**Tech Stack:** C# / .NET 10, `GeneratedComInterface` COM (IAudioSessionManager2 family), WPF, the dependency-free self-test.

**Spec:** `docs/superpowers/specs/2026-11-03-audio-design.md` — part **4b**: goal 1 (Channels section), goals 2, 3, 8, 9, and "Details settled" 3 (selection follows the app). Mockup approved by the owner: bold channel rows with their apps indented below, volume bar on every row, Left/Right = volume, A = mute, channels with no app playing hidden.

## Global Constraints

- Default channels, ids fixed: `game`, `chat`, `media`, `other` (names/icons editable, not deletable; `other` is the fallback). Custom channel ids `c1`, `c2`, …; deleting one moves its programs to `other`.
- Automatic assignment only when a program first plays sound, and only to the default channels (spec goal 2 lists the exact exe names and folders); a program's stored choice always wins; custom channels never get programs automatically.
- Effective session volume = channel volume × app volume; channel mute or app mute mutes the session. Volumes are applied when a session appears and when the user changes them in the overlay; changes made elsewhere are left alone until the next overlay change.
- Mix (channel and app volume/mute) stored **per output device id**; a device seen for the first time starts from the current device's mix. Presets store channel volumes/mutes only and apply to the current device.
- Programs are identified by full exe path, case-insensitive; our own process and the Windows "system sounds" session are excluded.
- Config keys (snake_case via `ConfigStore.Update`, unknown keys preserved, null collections tolerated like 4a): `audio_channels` (list of `{id, name, icon}` in display order), `audio_programs` (map exe → channel id), `audio_mix` (map device id → `{ "channels": {id: {volume, muted}}, "apps": {exe: {volume, muted}} }`), `audio_presets` (list of `{name, channels: {id: {volume, muted}}}`).
- Icon set for channels (Segoe Fluent glyphs, about 16): game, chat, music, video, browser, mic, headphones, speaker, star, heart, people, globe, bolt, home, code, other. Default icons: game → game, chat → chat, media → music, other → other.
- Text entry (add/rename channel, save preset) uses a small WPF dialog owned by the overlay; a pad user can accept the suggested name ("Channel 5", "Preset 2").
- Auto-switch circuit breaker carried from 4a: at most 3 automatic default switches per direction within 10 s; after that, auto-switch for that direction pauses until the next device event after a 10 s quiet period, with one `Log.Info`.
- Safety for agents: never run anything that changes this PC's audio devices, volumes or sessions; no synthetic input; read-only smoke checks with a scratch config only.
- Checks after every task: self-test `All N checks passed.`; build 0 warnings. Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **The same program with several sessions/processes** (browsers, Discord): one app row, one volume applied to all its sessions. Test in Task 1 (grouping) and Task 2 (apply to all).
2. **Switching output device while apps play:** the new device's mix is applied to every live session at once. Test in Task 1 (mix lookup/copy); Task 2 applies it on default change.
3. **Deleting a custom channel that holds programs:** they move to Other, and their app volumes are kept. Test in Task 1.
4. **A program the user moved by hand later matching an automatic rule** (e.g. a browser moved to Chat): it stays where the user put it. Test in Task 1.
5. **A preset saved before a custom channel was deleted:** applying it ignores the missing channel without error. Test in Task 1.

---

### Task 1: Mixing rules (pure)

**Files:** create `src/OverlayUtils.Core/Audio/MixRules.cs`; modify `src/OverlayUtils.Core/Config/AppConfig.cs` (+ `ConfigStore.Load` null tolerance); test section `// --- Mixing rules ---`.

**Produces** (namespace `OverlayUtils.Core.Audio`): records `AudioChannel(string Id, string Name, string Icon)`, `MixLevel(double Volume, bool Muted)`, `DeviceMix(Dictionary<string, MixLevel> Channels, Dictionary<string, MixLevel> Apps)`, `MixPreset(string Name, Dictionary<string, MixLevel> Channels)`; and

```csharp
public static class MixRules
{
    public static IReadOnlyList<AudioChannel> DefaultChannels { get; }          // game, chat, media, other
    public static string Classify(string exePath, bool fullscreenForeground);   // spec goal 2 rules → default channel id
    public static string ChannelOf(string exePath, IReadOnlyDictionary<string, string> programs, bool fullscreenForeground); // stored choice wins
    public static double Effective(MixLevel channel, MixLevel app);            // 0 when either is muted
    public static DeviceMix MixFor(IReadOnlyDictionary<string, DeviceMix> mixes, string deviceId, string? currentDeviceId); // copy current on first sight
    public static (List<AudioChannel> Channels, Dictionary<string, string> Programs) DeleteChannel(IReadOnlyList<AudioChannel> channels, IReadOnlyDictionary<string, string> programs, string id); // defaults not deletable
    public static string NextCustomId(IReadOnlyList<AudioChannel> channels);   // c1, c2, …
    public static DeviceMix ApplyPreset(DeviceMix mix, MixPreset preset, IReadOnlyList<AudioChannel> channels); // ignores unknown channel ids
    public static IReadOnlyList<(string Exe, IReadOnlyList<T> Sessions)> GroupByProgram<T>(IEnumerable<T> sessions, Func<T, string> exe); // case-insensitive
}
```

- [ ] **Step 1: Failing checks** — Classify: `C:\…\Discord.exe` → chat; `chrome.exe`, `Spotify.exe`, `vlc.exe` → media; `D:\SteamLibrary\steamapps\common\X\x.exe` → game; `C:\Program Files\Epic Games\Y\y.exe` → game; unknown exe with fullscreen true → game, false → other. ChannelOf: browser stored as chat stays chat (Review Focus 4). Effective: (0.5, 0.8) → 0.4; channel muted → 0; app muted → 0. MixFor: unknown device copies the current device's mix; known device returns its own; no current → defaults (1.0, unmuted). DeleteChannel: custom with two programs → both now `other` (Review Focus 3); deleting `game` → unchanged. NextCustomId after c1, c3 → c4. ApplyPreset with a missing channel id → ignored, others applied (Review Focus 5). GroupByProgram: three sessions of `CHROME.EXE`/`chrome.exe` → one group of 3 (Review Focus 1). Config: new keys snake_case, defaults empty, null in JSON → empty.
- [ ] **Step 2–5:** RED → implement → GREEN + build → commit `Add mixing rules`.

### Task 2: Audio sessions in the service

**Files:** `src/OverlayUtils.Core/Interop/CoreAudio.cs` (IAudioSessionManager2, IAudioSessionEnumerator, IAudioSessionControl/IAudioSessionControl2, ISimpleAudioVolume, IAudioSessionNotification, IAudioSessionEvents), `Native.cs` (OpenProcess, QueryFullProcessImageNameW, CloseHandle if missing), `src/OverlayUtils.Core/Audio/AudioService.cs`, `IAudioView.cs`, App `AudioServiceView.cs`.

**Produces:** `public sealed record AudioApp(string Exe, string Name)` (Name = file description or exe name); `IAudioView` gains `IReadOnlyList<AudioApp> Apps { get; }` (programs with an active or inactive-but-alive session on the default output, grouped, own process and system sounds excluded) and `void ApplyMix()` (re-apply the stored mix to all live sessions); `AudioService` applies `MixRules.Effective` to every session of a program **when the session is created** and on `ApplyMix()`; on default output change it calls `ApplyMix()` for the new device; it records newly seen programs in `audio_programs` via `MixRules.ChannelOf` (fullscreen flag from the foreground window at creation time). All COM on the existing worker; session notifications only post work. The circuit breaker (Global Constraints) wraps `AutoSwitch`.

- [ ] **Step 1:** Implement. **Step 2:** build 0 warnings; self-test green (add a check for the breaker if it is extracted as a pure helper `SwitchBreaker` — recommended). **Step 3:** read-only smoke in the scratchpad: list `Apps` for 3 s with a scratch config; no volume writes (do not call ApplyMix). **Step 4:** commit `Audio sessions and per-app mix`.

### Task 3: Channels in the overlay

**Files:** `src/OverlayUtils.Core/Overlay/OverlayItem.cs` (`bool Child` for indented rows), `OverlayContent.cs`, `Glyphs.cs` (channel icon set), both `.resx`; `IAudioView` additions if needed (`SetChannelLevel`, `SetAppLevel`, mute toggles — or a Core `MixController` over config + `ApplyMix`, recommended, so the self-test drives it with a fake view); tests.

Audio tab, after Volume: section "Channels"; per channel with at least one app: Slider row (Key `ch.<id>`, channel icon, Left/Right ±5 %, A mute, `SetLevel` for the bar), then its apps as `Child` Slider rows (Key `app.<exe>`). Then a "Presets" Choice row (value = preset name, Left/Right cycles, A applies; hidden when no presets) and a "Save preset…" action (host name dialog, suggested "Preset N"). Settings → Audio gains "Channels" (sub-page: Reorder rows of channels, value = icon name; Left/Right on the selected channel cycles its icon; A picks up; Y/Delete deletes a custom channel; "Add channel…" and "Rename…" via host dialog) and "Programs" (sub-page: one Choice row per known program, value = channel name, Left/Right cycles channels; "Add program…" file picker via host). `IOverlayHost` gains `void AskName(string suggested, Action<string> done)` and `void ChooseProgram(Action<string> done)`.

- [ ] **Step 1: Failing checks** with fakes: channel rows appear only with apps; Right on a channel raises its stored volume 5 % for the current device and calls ApplyMix; A on an app mutes it; Programs page moves an app to Media and the Audio tab shows it under Media; deleting a custom channel from the Channels page moves its program to Other; saving then applying a preset restores channel volumes; selection follows the app by Key when another app appears above it.
- [ ] **Step 2–5:** RED → implement → GREEN + build → commit `Channels in the overlay`.

### Task 4: App wiring, dialog, indent template, docs

**Files:** `src/OverlayUtils.App/Overlay/NameDialog.xaml(.cs)` (topmost, owned by the overlay, text box + OK/Cancel, Enter/Esc, pad A/B via the existing command path is not required), `OverlayWindow.xaml` (Child rows indented 24 DIP, smaller label), `TrayController.cs` (host methods), docs (README EN+IT Channels/Presets; PORTING 4b `[~]` + owner test list from Review Focus 1–5 plus per-device mix with headset/speakers and the circuit breaker; ARCHITECTURE "Mixing"; WORKLOG decisions).

- [ ] Implement, build 0 warnings, self-test green, commit `Mixing: dialog, wiring and docs`.
