# SPEC §8 — Verification & Acceptance Checklist

Status legend: ✅ verified automatically in CI-equivalent runs (`dotnet build` / `dotnet test` / `dotnet publish`) ·
🖥️ must be run on the target Windows 11 PC (Build 26300) — exact steps given.

> Development builds ran on Linux with `EnableWindowsTargeting`. Windows-only tests (`[WindowsFact]`) are **skipped**
> there and must be run once on the PC: `dotnet test` (as Administrator) should report **0 skipped**.

## 1. Zero build errors — ✅

| Check | Result |
| --- | --- |
| `dotnet build --no-incremental` | 0 errors, **0 warnings** |
| Warnings as errors | `TreatWarningsAsErrors=true` in `RigTune.Core` (all safety-critical services) **and** `RigTune.App` |
| `dotnet test` | 375 passed, 0 failed, 5 skipped (Windows-only, see §2) |

## 2. Snapshot round-trip tests — ✅ (🖥️ for the real-registry variants)

| Requirement | Tests |
| --- | --- |
| Registry value that **pre-existed** restores exact kind + value | `SnapshotServiceTests.Revert_restores_exact_kind_and_value_of_preexisting_value` (6 kinds) · 🖥️ `WindowsRegistrySnapshotTests.Preexisting_values_of_every_kind_round_trip_exactly` |
| Registry value that was **missing** is deleted (and created keys removed) | `Revert_deletes_value_that_did_not_exist_and_removes_created_keys` · 🖥️ `Previously_missing_value_and_created_keys_are_deleted_on_revert` |
| Service start type + running state | `Service_round_trip_restores_start_type_and_running_state`, `Service_that_was_stopped_is_not_started_on_revert` |
| JSON snapshot survives restart & round-trips | `Snapshots_survive_restart_and_can_be_reverted_by_a_new_instance`, `Snapshot_json_round_trips_polymorphically_with_exact_values` · 🖥️ `Revert_survives_app_restart_on_real_registry` |
| Power / NVAPI DRS / generic state | `Power_round_trip_…`, `NvApi_setting_that_existed_is_restored_…`, drift tests for state runners |
| Drift detection & re-apply (isolated `HKCU\Software\RigTuneTest\{guid}`) | `DriftDetectorTests` (all types) · 🖥️ `WindowsRegistryDriftTests` |

All real-registry tests write only under `HKCU\Software\RigTuneTest\{guid}`, assert that prefix before every write,
and delete the key afterwards.

## 3. Non-destructive manual smoke tests — 🖥️

| # | Smoke test | How to run it in RigTune | Supporting automated coverage |
| --- | --- | --- | --- |
| 1 | Create & enumerate a restore point; cooldown bypass twice in 2 minutes | System Care › Restore Points › *Create Restore Point* twice within 2 min; both appear with origin "Modify Settings" | `RestorePointServiceTests` (cooldown snapshotted once, both created) |
| 2 | Safe registry tweak (`MenuShowDelay`) → live status → regedit → drift → re-apply → revert | Tweaks › *Set Menu Animation Delay to Zero* › Apply (status **Applied**) › *Open in regedit* and set `MenuShowDelay` back to `400` › Backups, Drift & History › *Check Now* shows Expected `"0"` / Now `"400"` › *Re-apply* › Tweaks › *Revert* restores `400` | `DriftDetectorTests.Reapply_restores_the_applied_value_…_keeps_the_true_original`, 🖥️ `WindowsRegistryDriftTests.Detects_a_manual_regedit_change_and_reapplies_it` |
| 3 | NVAPI DRS reads Shader Cache Size / Power Management Mode; snapshot before change; microcode + P/E topology | Tweaks › NVIDIA Driver › open each tweak's inspector (live value shown) · Dashboard Processor card (microcode `0x12B`+, P 0–15 / E 16–27) | `TweakEngineTests.Nvidia_shader_cache_options_…`, `HardwareParsingTests` (14700F topology, microcode) |
| 4 | Danger Zone hidden/locked until enabled; restore-point prompt | Tweaks › *Danger Zone* filter: items show a lock and Apply is disabled; enable the switch (confirmation) → Apply shows *Create Restore Point & Continue / Continue Without / Cancel* | `TweakEngineTests.Danger_zone_tweaks_are_refused_until_danger_zone_is_enabled`, `TweakCatalogTests.No_danger_zone_tweak_is_in_a_recommended_preset` |
| 5 | Appx manager blocks System-Critical; winget lists updates | System Care › Appx & winget: tick *Show protected packages* (locked) · *Check for updates* | `AppxWingetTests.Critical_packages_are_always_protected`, `…_hard_blocked_unless_danger_zone_is_on`, winget parser tests |
| 6 | App Optimizer detects the 13 installed apps, hides others; cookies/history unchecked | App Optimizer (summary "13 of 38 profiles detected") · expand Chrome/Edge/Firefox | `AppCatalogTests.Cookies_and_history_are_never_checked_by_default…`, `AppOptimizerServiceTests.Installed_apps_are_detected…` |
| 7 | Game Mode with `notepad.exe`: priority, EcoQoS on a browser, full restore | Game Mode › allowlist `notepad.exe` · App Optimizer › Chrome › While You Play **on** · open Chrome, then Notepad → session shows Notepad **High**, chrome.exe **EcoQoS** · close Notepad → all restored | `GameModeSessionTests.Session_applies_high_priority_ecoqos_and_restores_exact_originals`, crash-recovery tests |
| 8 | Refresh Rate Watchdog: pin, drop to 60 Hz, detect, 1-click restore | Game Mode › pin current Hz · Windows Settings › Display › Advanced › 60 Hz → tray balloon "Refresh rate dropped" → click it (or Dashboard *Restore*) | `RefreshRateWatchdogTests` (drift, restore, auto-restore) |
| 9 | Disk Analyzer: system folders undeletable, locked test folder blocked, dummy file → Recycle Bin | Disk Analyzer › Scan C: › right-click `Windows` → Delete (refused) · create `D:\RigTuneTest\keep`, *Mark as Important*, Delete (refused) · create `D:\RigTuneTest\dummy.txt` → Delete → appears in Recycle Bin | `PathGuardTests`, `RecycleBinGuardrailTests` |

## 4. Publishable single executable — ✅

```bash
dotnet publish src/RigTune.App -c Release -r win-x64 --self-contained false
```

Output: `src/RigTune.App/bin/Release/net8.0-windows10.0.22621.0/win-x64/publish/` containing `RigTune.exe`
(embedded `requireAdministrator` manifest), `Catalogs\tweaks.json`, `Catalogs\apps.json` and dependencies. Requires the
.NET 8 Desktop Runtime on the PC (the installed .NET 8 SDK includes it). Start-with-Windows registers that
`RigTune.exe` as `\RigTune\AutoStart` (RunLevel Highest, `--minimized`).

## Shutdown & cleanup audit

| Situation | Behaviour |
| --- | --- |
| Normal exit (tray *Exit*) | `RestoreTransientState`: active Game Mode session stopped and every priority / EcoQoS / I/O priority / power plan restored; app-scoped timer resolution released; then DI disposal (watchers, WMI, SystemEvents) |
| Window closed with minimize-to-tray | Hidden to tray; nothing torn down |
| Windows sign-out / shutdown | `OnSessionEnding` marks the tray as exiting (close is never cancelled) and runs the same restore |
| Crash / power loss | Session originals were persisted to `gamemode.json` before any change; restored automatically on next start. Timer resolution is process-scoped and released by Windows when the process ends |
