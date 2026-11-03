# Final fix report (stage 4a whole-branch review)
1. AudioService.ApplyDefault: on success sets _defaultOutput/_defaultInput under _lock and RaiseChanged() (COM, no self-test possible).
2. AudioService.UpdateLists: _config.Update wrapped in try/catch, Log.Error, continues with in-memory lists.
3. ConfigStore.Load: AudioOutputOrder/AudioInputOrder/AudioVolumeCaps ??= []. RED: new check "null lists load as empty" threw NullReferenceException at Program.cs:89; GREEN after fix.
4. TrayController.OnConfigChanged: re-applies Home key (notifyIfBusy) only when HomeKeyMode differs from _lastHomeKeyMode (seeded at startup).
5. PadInterpreter.Press: closed-overlay Back+Left/Right/Y require state.Held.Count == 2. New checks: Back+LB+Y no MicMute, Back+LB+Left no VolumeDown (monitor combos unchanged).
Verification: `dotnet run --project tests/OverlayUtils.SelfTest` -> "All 825 checks passed."; `dotnet build OverlayUtils.sln` -> 0 warnings, 0 errors.
