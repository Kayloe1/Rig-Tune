# RigTune

Personal, 100% local Windows 11 optimizer tailored to one PC (i7-14700F, RTX 4070 Ti SUPER, 64 GB, 2× NVMe).
The full design lives in [`SPEC.md`](SPEC.md).

## Build & test

```bash
dotnet build
dotnet test
dotnet publish src/RigTune.App -c Release -r win-x64 --self-contained false
```

The app requires administrator rights (`app.manifest` → `requireAdministrator`). All state is stored in
`%LOCALAPPDATA%\RigTune\` (`settings.json`, `snapshots.json`, `history.json`) using atomic temp-file + replace writes.

## Layout

| Project | Purpose |
| --- | --- |
| `src/RigTune.Core` | Persistence, snapshot/rollback engine, audit log, restore points, host detection, Win32/NVAPI interop |
| `src/RigTune.App` | WPF-UI (Fluent dark) shell, MVVM view models, tray icon |
| `tests/RigTune.Core.Tests` | xUnit tests: snapshot round trips, atomic JSON storage, hardware parsers |

Registry round-trip tests that touch the real registry run only on Windows and only under an isolated
`HKCU\Software\RigTuneTest\{guid}` key, which is deleted afterwards. Platform-independent tests use in-memory fakes.

## Status

- **Phase 1 (done):** solution, UI shell + navigation, atomic JSON persistence, `SnapshotService` (registry / service /
  power / NVAPI DRS, true revert, Revert All), `AuditLogService`, `RestorePointService` (cooldown bypass, SR/WMI
  create, WMI enumerate), `HostProfileDetector`, live Dashboard.
- Later phases: tweak catalog & engine, drift detector, System Care, App Optimizer, Game Mode & watchdogs, Disk Analyzer.
