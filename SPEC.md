# Personal Windows 11 PC Optimizer — Technical Specification (`SPEC.md`)

> **Purpose:** Implementation-ready engineering specification for a personal-use, 100% local, offline Windows 11 optimization, maintenance, disk analysis, and gaming utility tailored to one specific desktop PC.
> **Working Name:** `RigTune` (rename freely).

---

## 1. Target Machine & Environment Profile

Build specifically for this verified host configuration (no cross-OS or irrelevant vendor bloat):

| Attribute | Verified Value | Design Implication |
| :--- | :--- | :--- |
| **OS** | Windows 11 Pro 64-bit (`10.0.26300`, Build 26300) | Windows 11 APIs only; drop Windows 10 branching. |
| **Form Factor** | Desktop (no battery; S0 Low Power Idle; Hibernation currently off) | Drop laptop battery >= 30% guards & S3/laptop standby scripts. |
| **CPU** | Intel Core i7-14700F (20 cores: **8 P-cores + 12 E-cores**, 28 logical threads) | Hybrid-aware: use Windows **EcoQoS** so Thread Director moves throttled background apps to E-cores; warn & display P-core vs. E-core indices in interrupt-affinity UI. |
| **GPU** | NVIDIA GeForce RTX 4070 Ti SUPER (16 GB, Ada Lovelace) | **NVIDIA-only.** Drop all AMD Radeon tweaks & AMD Software profiles. HAGS & Resizable BAR are supported. |
| **RAM** | 64 GB (`63.8 GB`) | Drop 8–40 GB `SvcHostSplitThreshold` memory profile tweak. |
| **Storage** | 2x NVMe Gen4 SSDs (`WD_BLACK` 2 TB + 1 TB; **0 HDDs**) | Drop HDD Defrag; optimize Disk Analyzer for fast multi-threaded NVMe traversal. |
| **Virtualization** | `HypervisorPresent: True`, WSL installed | Keep Hyper-V / VBS / HVCI tweaks strictly inside **Danger Zone** with clear warnings. |
| **Installed Toolchains** | `.NET 8.0 SDK` (`8.0.417`), `Git` | Ready to build & publish with `dotnet` immediately — zero extra SDK installs. |

### Intentionally Out of Scope (Do NOT Implement)
- No accounts, sign-in, licensing, cloud sync, telemetry, or online leaderboards.
- No AI / LLM features (100% deterministic, offline, local rules).
- No Driver Manager / GPU driver installer.
- No Hardware / DirectX 11 Benchmark suite.
- No AMD Radeon tweaks (4), AMD Software app actions (5), HDD Defrag (1), or 8–40 GB RAM `SvcHostSplitThreshold` tweak (1).
- No game file patching, game config editing, or code injection into game/anti-cheat processes.

---

## 2. Tech Stack & Project Structure

- **Language & Runtime:** C# 12 / `.NET 8.0` (`net8.0-windows10.0.22621.0`)
- **UI Framework:** WPF with **WPF-UI** (`Wpf.Ui` + `Wpf.Ui.Tray`, Fluent dark theme, Mica/acrylic backdrop optional with clean fallback, neutral dark-gray cards with subtle thin borders)
- **MVVM & DI:** `CommunityToolkit.Mvvm` + `Microsoft.Extensions.DependencyInjection`
- **Local Persistence:** JSON files in `%LOCALAPPDATA%\RigTune\` (atomic write via temp file + replace):
  - `settings.json` — UI preferences, Danger Zone toggle, Start-with-Windows state, per-monitor target refresh rates
  - `snapshots.json` — Exact pre-change original values per tweak/app toggle for true rollback & drift detection
  - `history.json` — Timestamped audit log of every applied, reverted, drifted/re-applied, cleaned, or repaired action
  - `gamemode.json` — Custom game `.exe` allowlist, discovered library games, per-app throttling overrides, active session crash-recovery state
  - `disk_locks.json` — User-locked "Important" files/folders + per-drive cached scan summaries
- **Bundled Data Catalogs** (copied to output & user-extensible via `%LOCALAPPDATA%\RigTune\custom_*.json`):
  - `Catalogs\tweaks.json` — All 185 tailored Windows tweaks
  - `Catalogs\apps.json` — All 38 non-AMD app optimizer profiles
- **Elevation:** `app.manifest` with `<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />`.
- **Start with Windows (Elevated):** Because Windows ignores `HKCU\...\Run` for elevated apps, implement Start-with-Windows using a **Windows Task Scheduler logon trigger with `RunLevel = Highest`** (e.g., task name `\RigTune\AutoStart`, launching with `--minimized` to start cleanly in the system tray).

### Recommended Solution Layout
```text
RigTune.sln
├── src/
│   ├── RigTune.Core/
│   │   ├── Models/            # TweakDefinition, SnapshotEntry, DriftItem, AppProfile, AppxEntry, WingetUpdate, DisplayTarget, DiskNode, GameSession
│   │   ├── Catalogs/          # tweaks.json, apps.json
│   │   ├── Native/            # P/Invoke: PowrProf, Kernel32 (EcoQoS/Priority), SrClient, SetupApi, NvApi (DRS), User32 (DisplaySettings), Wasapi, Shell32
│   │   ├── Runners/           # RegistryRunner, ServiceRunner, PowerRunner, NvApiRunner, DismRunner, TaskSchedulerRunner, PciRunner, CleanupRunner
│   │   ├── Services/          # SnapshotService, DriftDetectorService, RestorePointService, TweakEngine, SystemCareService, AppxWingetService, AppOptimizerService, GameModeService, RefreshRateWatchdogService, DiskAnalyzerService, SpaceAdvisorService, AuditLogService
│   │   └── Hardware/          # HostProfileDetector (P-core/E-core topology + Intel microcode revision)
│   └── RigTune.App/
│       ├── Views/             # DashboardView, TweaksView, SystemCareView, AppOptimizerView, GameModeView, DiskAnalyzerView, HistoryView
│       ├── ViewModels/
│       ├── Controls/          # TweakDetailDrawer, ConfirmationDialogs
│       └── Tray/              # SystemTrayManager
└── tests/
    └── RigTune.Core.Tests/    # xUnit tests for snapshot/rollback, drift detection, JSON schema validation, Steam VDF parser, path protection
```

---

## 3. Core Engineering & Safety Contracts

### 3.1 Snapshot & True Rollback Engine (`SnapshotService`)
Every reversible change **must** capture the machine's actual pre-existing state before writing:
- **Registry:** Record `(Hive, View [64/32], KeyPath, ValueName, Existed: bool, ValueKind, RawValueBase64OrTyped)`. On revert: if `Existed == false`, delete the value (never overwrite with a guessed default); otherwise restore exact `ValueKind` and value.
- **Services:** Record `(ServiceName, OriginalStartType, WasRunning: bool)`. On revert: restore exact `StartType` via `ChangeServiceConfig` and restart if `WasRunning`.
- **Power Settings:** Record `(ActiveSchemeGuid, SubgroupGuid, SettingGuid, OriginalAcValue, OriginalDcValue)` via `PowerReadACValueIndex` / `PowerReadDCValueIndex`.
- **NVIDIA Driver Settings (NVAPI DRS):** Record `(SettingId, ExistedInBaseProfile: bool, OriginalDwordValue)` before writing global DRS profile settings so revert restores the exact prior NVIDIA driver state.
- **Startup Entries:** Use `HKCU/HKLM\...\Explorer\StartupApproved\Run` binary state (`02 00 ...` enabled vs `03 00 ...` disabled) so entries stay visible and reversible in Windows Task Manager.
- **One-Click "Revert All":** Iterates snapshots in reverse chronological order, restores all active reversible tweaks, reports any items needing a reboot, and logs every action to `history.json`.
- **Windows Update Drift Detector (`DriftDetectorService`):** Compares every currently active snapshot in `snapshots.json` against live Windows/NVAPI state, surfaces any setting silently reverted by Windows/driver updates (showing expected vs. current value), and offers 1-click **"Re-apply All Drifted"** or per-item re-apply.

### 3.2 Danger Zone & Restore Point Prompts
- **Danger Zone Toggle:** Off by default (persistent setting in `settings.json`, or quick header toggle with warning banner). When off, `[DangerZone]` tweaks are hidden or locked behind a clear opt-in toggle.
- **Recommended Presets:** Area-level "Apply Recommended" bundles **never** include `[DangerZone]` or `(No undo)` destructive tweaks.
- **Restore Point Prompt:** Before applying any `[DangerZone]` tweak or any multi-tweak Recommended Preset batch, display a dialog offering **"Create Restore Point & Continue"** (recommended default), **"Continue Without Restore Point"**, or **"Cancel"**.

### 3.3 Key Win32 API Contracts
1. **Restore Points (Cooldown-Free):**
   - Set `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore\SystemRestorePointCreationFrequency` (`DWORD`) to `0` (snapshot original value first) so Windows' 24-hour cooldown is bypassed.
   - Create via `srclient.dll` `SRSetRestorePointW` (or WMI `root\default:SystemRestore.CreateRestorePoint`).
   - Enumerate existing restore points via WMI `SELECT * FROM SystemRestore`, mapping `RestorePointType` to human-readable origin tags (`0` = Application Install, `1` = Application Uninstall, `10` = Device Driver Install, `12` = Modify Settings, `13` = Cancelled Operation, `16` = System Checkpoint / Update).
2. **Game Mode Process Priority & EcoQoS (Intel 14700F Hybrid-Aware):**
   - Game process: `SetPriorityClass(hProcess, HIGH_PRIORITY_CLASS)` (save original priority class).
   - Throttled background apps: `SetPriorityClass(hProcess, BELOW_NORMAL_PRIORITY_CLASS)` + `SetProcessInformation(hProcess, ProcessPowerThrottling, PROCESS_POWER_THROTTLING_EXECUTION_SPEED)` (Windows EcoQoS — automatically directs Windows 11 Thread Director to schedule throttled apps onto the 12 E-cores) + optionally `PROCESS_MODE_BACKGROUND_BEGIN` / low I/O priority via `NtSetInformationProcess(ProcessIoPriority)`.
   - Memory Trim (Pressure-Only): Only trigger `EmptyWorkingSet` / `SetProcessWorkingSetSizeEx(-1, -1)` on selected background apps when system memory load (`GlobalMemoryStatusEx.dwMemoryLoad`) exceeds a configurable threshold (default **>= 80%**).
   - Crash Recovery: Persist active Game Mode session overrides (`pid`, `exe`, `originalPriority`, `originalPowerPlanGuid`) to `gamemode.json`. On app launch, if a stale active session exists, automatically restore the power plan and clean up state.
3. **Recycle Bin Deletions:** Always use `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile/DeleteDirectory(..., RecycleOption.SendToRecycleBin)` (or `IFileOperation` with `FOF_ALLOWUNDO`) — never `Directory.Delete` in Disk Analyzer.
4. **Intel 14th-Gen Hybrid Topology, Scheduler & Microcode:**
   - Query `GetLogicalProcessorInformationEx(RelationProcessorCore)` to identify `EfficiencyClass` (`1` = P-cores 0–15, `0` = E-cores 16–27 on i7-14700F) and clearly label P-cores vs. E-cores in the interrupt-affinity UI.
   - Expose and manage Windows 11's heterogeneous scheduler GUIDs under `SUB_PROCESSOR` (`54533251-82be-4824-96c1-47b60b740d00`) via `powrprof.dll`: `HETEROPOLICY` (`7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5`), `SCHEDPOLICY` (`93b8b6dc-0698-4d1c-9ee4-06ed4e92f5eb`), and `SHORTSCHEDPOLICY` (`bae08b81-2d5e-4688-ad6a-132433566544`) — allowing `Prefer Performant Processors` (P-cores) for foreground/short threads while routing background threads to E-cores.
   - Read active Intel CPU microcode revision from `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\Update Revision` (parse raw binary to hex, e.g. `0x12B`+) for read-only verification on the Dashboard and CPU panel.
5. **NVIDIA Driver Profile Tweaks (`nvapi64.dll` DRS):**
   - Load `nvapi64.dll` via `nvapi_QueryInterface` to open a DRS session (`NvAPI_DRS_CreateSession`, `NvAPI_DRS_LoadSettings`, `NvAPI_DRS_GetBaseProfile`, `NvAPI_DRS_GetSetting`, `NvAPI_DRS_SetSetting`, `NvAPI_DRS_SaveSettings`).
   - Support global Shader Cache Size (`0x00A06946`: Driver Default / 10 GB / 100 GB / Unlimited), Power Management Mode (`0x1057EB71`: Normal / Adaptive / Prefer Max Performance), and Resizable BAR DRS status inspection/toggle, snapshotting original values before any write.
6. **Refresh Rate Watchdog (`user32.dll`):**
   - Enumerate connected displays via `EnumDisplayDevicesW` + `EnumDisplaySettingsExW` (`ENUM_CURRENT_SETTINGS` + supported modes) and store the user's preferred target refresh rate per display in `settings.json`.
   - Listen to `Microsoft.Win32.SystemEvents.DisplaySettingsChanged` (plus check on app launch and Game Mode launch). If any monitor drops below its target refresh rate (e.g., reset to 60 Hz after a driver/Windows update), fire a tray notification with **1-click Restore** (via `ChangeDisplaySettingsExW` with `CDS_UPDATEREGISTRY`) or auto-restore if enabled.
7. **High-Precision Timer Resolution:** Scoped strictly to `RigTune`'s own process via `NtSetTimerResolution` / `timeBeginPeriod(1)`. Measure `Sleep(1)` via `Stopwatch` before and after to display real achieved resolution; always release on Reset or app exit.

---

## 4. Data-Driven JSON Schemas

### 4.1 `Catalogs\tweaks.json`
```json
{
  "schemaVersion": 1,
  "tweaks": [
    {
      "id": "perf.input.raw_mouse_throttle",
      "name": "Optimize Raw Mouse Throttle",
      "category": "Performance",
      "subcategory": "Input & Device Pipeline",
      "description": "Sets RawMouseThrottleDuration to 1 ms (1000 Hz raw-input listener rate) instead of the Windows 11 default of 8 ms.",
      "impact": "Medium",
      "riskTier": "Standard",
      "inRecommendedPreset": true,
      "requiresRestart": false,
      "undoType": "Toggle",
      "targetSummary": "HKCU\\Control Panel\\Mouse\\RawMouseThrottleDuration",
      "actions": [
        {
          "type": "registry",
          "hive": "HKCU",
          "view": "Registry64",
          "path": "Control Panel\\Mouse",
          "valueName": "RawMouseThrottleDuration",
          "valueKind": "DWord",
          "applyValue": 1
        }
      ]
    }
  ]
}
```
Supported `actions[].type` values: `registry`, `service`, `power`, `nvapi_drs`, `dism`, `schtask`, `cleanup`, `pci_msi`, `interrupt_affinity`, `device_manager`, `wasapi_inspect`, `cpu_microcode_inspect`, `timer_resolution`, `ntfs`, `tcp_profile`.

### 4.2 `Catalogs\apps.json`
```json
{
  "schemaVersion": 1,
  "apps": [
    {
      "id": "chrome",
      "name": "Google Chrome",
      "category": "Browsers",
      "detectPaths": ["%LOCALAPPDATA%\\Google\\Chrome\\User Data"],
      "processes": ["chrome.exe"],
      "caches": [
        { "id": "web_cache", "name": "Cached images, code & GPU/shader caches", "paths": ["%LOCALAPPDATA%\\Google\\Chrome\\User Data\\*\\Cache", "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\*\\Code Cache", "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\*\\GPUCache", "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\*\\DawnCache", "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\Crashpad\\reports"], "defaultChecked": true },
        { "id": "cookies", "name": "Cookies & site data (signs you out)", "paths": ["%LOCALAPPDATA%\\Google\\Chrome\\User Data\\*\\Network\\Cookies*"], "defaultChecked": false },
        { "id": "history", "name": "Browsing history", "paths": ["%LOCALAPPDATA%\\Google\\Chrome\\User Data\\*\\History*"], "defaultChecked": false }
      ],
      "toggles": [
        { "id": "bg_apps", "name": "Turn off background apps", "action": { "type": "registry", "hive": "HKLM", "path": "SOFTWARE\\Policies\\Google\\Chrome", "valueName": "BackgroundModeEnabled", "valueKind": "DWord", "applyValue": 0 } }
      ],
      "whilePlaying": { "enabled": true, "mode": "EcoQoS", "cpuPriority": "BelowNormal", "ioPriority": "Low" }
    }
  ]
}
```

---

## 5. Detailed Module Specifications (5 Modules + Dashboard & History)

### Navigation Sidebar
1. **Dashboard** — Quick system snapshot (Windows 11 Build 26300, i7-14700F + active Intel microcode revision, RTX 4070 Ti SUPER, 64 GB RAM, free space on both NVMe drives), monitor refresh-rate watchdog status badge (with 1-click Restore if drifted), active Game Mode status card, **Drift Detector** summary card (count of active tweaks + 1-click "Check for Drift"), 1-click "Create Restore Point", and recent change history.
2. **Tweaks** — Searchable catalog of all 185 tailored tweaks grouped by the 6 categories, filter pills (`All`, `Active`, `Needs Restart`, `Danger Zone`), Danger Zone opt-in toggle, area-level "Apply Recommended" preset buttons (prompting for a Restore Point), per-tweak inspector drawer (exact registry/service/power/NVAPI target, live status, copy path, open in regedit/services.msc), and global **"Revert All Tweaks"** button.
3. **System Care** — Guided 5-tab/section maintenance workflow:
   - *Restore Points:* Create cooldown-free restore point with custom label; view existing points with date & origin tag; open Windows `rstrui.exe` if needed.
   - *System Cleanup:* 7 individual user-confirmed cleanup actions with live estimated reclaimable size before cleaning (Temp System Files, User/Windows Temp, Prefetch, DNS Cache, GPU Shader Cache, Thumbnails, RAM/Standby Cache).
   - *Startup Apps:* Lists `HKCU`/`HKLM` Run + Startup folder entries with publisher/command; toggle via `StartupApproved` without deleting entries.
   - *Appx Bloatware & `winget`:*
     - **UWP / Appx Manager:** Enumerates installed Microsoft Store/UWP packages (`PackageManager` / PowerShell), categorizing them into **Safe to Remove** (Clipchamp, Bing News/Weather, Solitaire, Dev Home, Feedback Hub, People, Sound Recorder, etc.), **Optional** (Photos, Camera, Calculator, Paint, Xbox), and **System-Critical / Protected** (Store, ShellExperienceHost, WebView2, NVIDIA, Audio — hard-blocked unless Danger Zone is on) with 1-click remove (current user + optional deprovision).
     - **`winget` Updater:** Wraps native `winget upgrade --include-unknown` to list outdated desktop apps (current vs. available version) with selective or 1-click **"Upgrade Selected"** and live output log.
   - *Repair & Recovery:* Status-first scanner that inspects and repairs specific broken items: disabled devices (Bluetooth, audio, network, USB), stalled services (Windows Update, Print Spooler, BITS), and flipped policies (Search Indexing, Storage Sense, Hyper-V). Does *not* run disk/filesystem repair commands.
4. **App Optimizer** — Loads all 38 non-AMD app profiles from `apps.json`, **auto-hiding uninstalled apps** (with a "Show all 38 profiles" toggle and an **"Add Custom App"** modal saving to `custom_apps.json`). Per app: scan & show cache sizes (cookies/history unchecked by default), apply reversible toggles (auto-start, background mode, telemetry, etc.), and configure its "While You Play" Game Mode rule.
5. **Game Mode & Tray Watchdogs** —
   - Runs quietly in the **system tray** (minimize-to-tray on window close; tray menu with Start/Stop Game Mode, active game label, Refresh Rate status / Restore, Open, Exit; optional Start-with-Windows toggle via elevated Task Scheduler task).
   - **Refresh Rate Watchdog:** Lets you pin a target refresh rate per connected monitor; automatically watches `DisplaySettingsChanged`, app startup, and Game Mode start, firing a tray notification with 1-click Restore (or optional auto-restore) if Windows or a driver update drops a display back to 60 Hz.
   - **Hybrid Auto-Detection:** Scans installed game libraries automatically:
     - Steam: parses `steamapps\libraryfolders.vdf` + `appmanifest_*.acf` to map game install folders and main executables
     - EA App, Battle.net, Xbox/WindowsApps, Epic (if installed): scans known install roots & manifests
     - Plus a user-editable **Custom EXE Allowlist** and **Exclusion List** (ignore launchers/crash handlers)
     - Plus manual **Start / Stop** override
   - Watches process starts via lightweight WMI `Win32_ProcessStartTrace` (with a 2-second fallback poller if WMI fails).
   - On game launch: elevates game to `High` priority, applies EcoQoS + BelowNormal CPU + Low I/O priority to enabled background apps from App Optimizer, optionally trims background RAM only when RAM load >= 80%, optionally switches power plan (only if enabled by user), and **automatically restores every single change** when the game exits or Stop is clicked.
6. **Disk Analyzer** —
   - Multi-threaded parallel drive scanner (C: and second NVMe drive) with cancellation token, progress indicator, and per-drive scan caching.
   - Size-sorted collapsible folder tree (lazy UI child binding for speed) + top category breakdown bar (**Games, Media, Apps, System, Other**) + **"Hide small items"** toggle (e.g., < 50 MB / configurable).
   - **Hard-Protected Paths:** Permanently block deletion of `C:\Windows`, `C:\Program Files\WindowsApps`, `pagefile.sys`, `swapfile.sys`, `hiberfil.sys`, `System Volume Information`, `$Recycle.Bin`, and user profile roots.
   - **User Locks:** Right-click or button to **"Mark as Important"** on any file/folder (persisted in `disk_locks.json`), blocking deletion of it and anything inside it until unmarked.
   - **Safe Deletion:** Confirmation dialog showing path + size, always sending to **Recycle Bin**.
   - **Offline Deterministic "Space Advisor" Tab:** Scans without any LLM and groups actionable findings into **Safe to Remove**, **Check First**, and **Keep** with sizes, plain-English reasons, and per-item delete buttons:
     - *Safe to Remove:* Browser caches, GPU shader caches, `%TEMP%`, `Windows\Temp`, `SoftwareDistribution\Download`, crash dumps (`CrashDumps`, `Minidump`), old NVIDIA installer extracts (`C:\NVIDIA`), recycle bin size.
     - *Check First:* Large `Downloads` files (> 200 MB / older than 30 days), `Windows.old`, old `.iso`/`.zip`/`.exe` installers, and **dormant Steam games** (parses Steam `userdata\*\config\localconfig.vdf` `LastPlayed` Unix timestamps to flag large games not played in 60+ / 90+ days).
     - *Keep:* Active games played recently, user-marked Important folders, critical system directories.
7. **Backups, Drift & History** — Unified view of:
   - **Windows Update Drift Detector:** On-demand (and optional on-launch) scan comparing all active snapshots in `snapshots.json` against live Windows/NVAPI state, listing drifted tweaks with expected vs. current values and 1-click **"Re-apply All Drifted"** or per-item restore.
   - **Active Snapshots:** Per-item Revert and global **"Revert All"**.
   - **Audit Log & Profiles:** Chronological audit log (`history.json`) + JSON export/import of your personal tweak profile.

---

## 6. Complete Tailored Windows Tweaks Catalog (185 Tweaks)

> Tags: **`[DZ]`** = Danger Zone (opt-in, excluded from Recommended presets, prompts for Restore Point) | **`[R]`** = Requires Restart | **`(1x)`** = Runs once | **`(Perm)`** = Permanent / no automatic undo.

### A. Privacy (50 Tweaks)
**Windows Privacy (18):** Apply Recommended Privacy Tweaks `(1x)`; Turn Off Windows Widgets; Disable Activity History Tracking; Disable Location Tracking; Configure Storage Sense / Cleanup Rules `(Perm)`; Block Apps Running in Background; Remove Microsoft Store from Search Results `(Perm)`; Block Microsoft Telemetry Data Collection; Disable Windows Error Reporting; Turn Off Microsoft Copilot AI; Disable Xbox Background Services (`XblAuthManager`, `XblGameSave`, `XboxNetApiSvc`, `XboxGipSvc`); Turn Off Game DVR and Xbox Bar; Disable Handwriting & Typing Data Collection; Disable Input Personalization; Disable Tailored Experiences; Disable App Launch Tracking; Stop Windows Feedback Prompts; Block Delivery Optimization Peer Sharing (Policy).
**Ads & Suggestions (5):** Disable Advertising ID; Block Suggested Content in Settings; Disable Lock Screen Spotlight; Block Silently Installed Apps; Disable Pre-installed App Suggestions.
**Search & Cortana (4):** Disable Bing Web Results in Search; Disable Cortana; Disable Search Highlights; Disable Microsoft Store Search Suggestions.
**Sync & Clipboard (4):** Disable Clipboard Cloud Sync; Disable Clipboard History; Disable Windows Settings Sync; Disable Find My Device.
**AI Features (6):** Disable Windows Recall; Disable Click To Do; Disable Edge Compose AI; Disable Paint AI Features (Cocreator); Disable Notepad AI Features; Disable AI Service Auto-Start (`AiHost`).
**Microsoft Edge (8):** Disable Edge Usage Data Reporting; Disable Edge Site Info Collection; Disable Edge Personalization Reporting; Block Copilot Page Access in Edge; Block Copilot Page Context in Edge; Disable Edge Discover Page Scanning; Hide the Edge Sidebar; Disable Edge Shopping Assistant.
**Google Chrome (3):** Disable Chrome Usage Statistics; Disable Chrome Feedback Reports; Disable Chrome Extended Safe Browsing Reporting *(keeps standard Safe Browsing on)*.
**Mozilla Firefox (2):** Disable Firefox Telemetry; Disable Firefox Default Browser Agent.

### B. Debloat (13 Tweaks)
**Optional Windows Features (8):** Refresh Optional Features Status `(1x)`; Toggle Microsoft Print to PDF; Toggle Legacy Windows Media Player; Toggle Work Folders Sync Client; Toggle Internet Explorer 11; Toggle SMB 1.0 File Sharing `[DZ]`; Toggle XPS Document Support; Toggle Telnet Client.
**Microsoft Store & Edge (5):** Remove or Restore Microsoft Store `[DZ]` `(1x)`; Uninstall OneDrive *(keeps cloud files)* `[DZ]` `(1x)`; Uninstall Microsoft Edge `[DZ]` `(1x)`; Stop Edge from Reinstalling Itself (`edgeupdate`, `edgeupdate2`) `[DZ]`; Reinstall Microsoft Store `(1x)`.

### C. Services (24 Tweaks)
**Service Groups (5):** Apply Recommended Services Tweaks `(1x)`; Disable Telemetry Service Group (`DiagTrack`, `dmwappushservice`); Disable Search Indexing Service Group (`WSearch`, `SysMain`); Disable Windows Update Service Group (`wuauserv`, `UsoSvc`, `WaaSMedicSvc`, `bits`, `DoSvc`) `[DZ]` `[R]`; Disable Diagnostics Service Group (`DPS`, `WdiServiceHost`, `WdiSystemHost`).
**Individual Services (19):** `DiagTrack`; `dmwappushservice`; `PcaSvc`; `SysMain`; `WSearch`; `DoSvc` `[DZ]` `[R]`; `WaaSMedicSvc` `[DZ]` `[R]`; `DPS`; `WdiServiceHost`; `WdiSystemHost`; `MapsBroker`; `Fax`; `PrintNotify`; `TabletInputService`; `QWAVE`; `SharedAccess`; `SSDPSRV`; `upnphost`; `wisvc`.

### D. Performance (64 Tweaks — Tailored for Intel 14700F + RTX 4070 Ti SUPER + 64 GB + NVMe)
**Power Plans (3):** Configure Power Plan *(sliders + hidden expert view + hardware checks)* `(1x)`; Check/Toggle PCIe Link Power Saving *(PCIe ASPM AC/DC)*; Expose Hidden Advanced Power Settings.
**CPU Scheduling (8):** Apply Recommended CPU Tweaks `(1x)`; Prioritize Foreground App CPU Time (`Win32PrioritySeparation`); Set System Responsiveness to Zero; Unpark All CPU Cores; Disable CPU Power Throttling; Keep Kernel in RAM / Disable Paging `[DZ]` `[R]`; **Configure Intel Hybrid Thread Scheduling Policy** *(`HETEROPOLICY` / `SCHEDPOLICY` / `SHORTSCHEDPOLICY` — prefer P-cores for foreground/short threads, E-cores for background)*; **Inspect Intel 14th-Gen Microcode & Core Topology** *(read-only: shows active microcode revision e.g. `0x12B`+ & P-core/E-core layout)* `(1x)`.
**Graphics Pipeline (8):** Apply Recommended Graphics Tweaks `(1x)`; Disable Fullscreen Optimizations `[R]`; Check Resizable BAR Support *(BIOS + registry guide)* `(1x)`; Disable DWM Transparency; Disable Multi-Plane Overlay (MPO) `[R]`; Enable Hardware GPU Scheduling (HAGS) `[R]`; Disable DirectX Maximized Windowed Mode `(1x)`; Disable GPU Timer Interrupt Overhead `[R]` `(1x)`.
**NVIDIA Driver (6):** Disable HDCP Overhead; Disable NVIDIA Telemetry Container (`NvTelemetryContainer`); Disable NVIDIA Network Service (`NvNetworkService`); **Configure NVIDIA Global Shader Cache Size** *(`nvapi64.dll` DRS `0x00A06946`: Driver Default / 10 GB / 100 GB / Unlimited)*; **Configure NVIDIA Power Management Mode** *(`nvapi64.dll` DRS `0x1057EB71`: Normal / Adaptive / Prefer Max Performance)*; **Inspect & Toggle NVIDIA Resizable BAR DRS Profile** *(`nvapi64.dll` DRS global ReBAR flags)*.
**Input & Device Pipeline (17):** Apply Recommended Input Tweaks `(1x)`; Review/Configure MSI Mode for PCI Devices `[DZ]` `[R]` `(1x)`; Choose CPU Cores for GPU Interrupts *(label P-cores 0–15 vs. E-cores 16–27)* `[DZ]` `[R]` `(1x)`; Choose CPU Cores for USB/Network/Audio Interrupts `[DZ]` `[R]` `(1x)`; Check/Toggle USB Selective Suspend; Check Audio Latency Settings *(read-only Core Audio/WASAPI inspection)* `(1x)`; Disable xHCI USB Interrupt Moderation `[DZ]` `[R]`; Optimize Raw Input Message Queue; Restore All Optional Devices `(1x)`; Disable Unused Devices *(safe/moderate/risky ratings)* `(1x)`; Disable Mouse Pointer Acceleration `(1x)`; Optimize Raw Mouse Throttle (`RawMouseThrottleDuration` 1 ms vs. 8 ms); Optimize Keyboard Repeat Rate & Delay `(1x)`; **Disable Sticky Keys, Filter Keys & Toggle Keys Popups** *(clears hotkey popup bits in `Accessibility\StickyKeys`, `Keyboard Response`, `ToggleKeys`)*; Run SSD TRIM `(Perm)`; Apply NTFS Performance Tweaks *(8.3 names & LastAccess)* `[R]`; Set NTFS Memory Usage to Maximum (`MemoryUsage = 2`) `[R]`.
**Network Tuning (12):** Apply Recommended Network Tweaks `(1x)`; Inspect & Test Network Interrupt Moderation *(per adapter)* `(1x)`; Configure Receive Side Scaling (RSS) per Adapter `(1x)`; Enable Network QoS / DSCP Traffic Tagging; Disable Network Throttling Index; Disable Nagle Algorithm; Apply TCP Optimizer Safe Profile `(1x)`; Prefer IPv4 Over IPv6; Disable Teredo IPv6 Tunneling; Disable IPv6 Protocol Stack `[DZ]`; Set High-Precision Timer Resolution *(app-scoped; measures `Sleep(1)`)* `(1x)`; Ignore Brief Network Link Drops.
**Registry & Policy (10):** Apply Recommended Registry Tweaks `(1x)`; Optimize Windows Task Scheduler Maintenance Tasks `(1x)`; Optimize MMCSS Multimedia Scheduler; Disable Windows Update Auto-Download (`wuauserv`) `[DZ]`; Block Windows Update from Installing Drivers; Disable Lock Screen Policy; Disable Auto-Reboot While Logged In; Remove File Explorer Multi-Select Limit `(Perm)`; Set Menu Animation Delay to Zero (`MenuShowDelay = 0`); Disable Windows Consumer Features.

### E. System (27 Tweaks)
**Boot & Recovery (7):** Disable Fast Startup (Hybrid Boot); Toggle Spectre / Meltdown CPU Mitigations `[DZ]` `[R]`; Disable VBS and HVCI Security Layer `[DZ]` `[R]`; Disable Hyper-V Hypervisor *(warn: breaks WSL2/Docker/Sandbox)* `[DZ]` `[R]`; Toggle Windows Recovery Environment (WinRE) `[DZ]` `(1x)`; Switch Classic F8 vs. Modern Boot Menu `(1x)`; Toggle Boot Logging (`ntbtlog.txt`) `(1x)`.
**Windows UI Tweaks (10):** Apply Recommended Windows UI Tweaks `(1x)`; Disable Hibernation File *(note: currently off on this PC; keep status-aware toggle)* `[DZ]`; Optimize Visual Effects for Performance; Add End Task to Taskbar Right-Click Menu; Restore Classic Windows 11 Right-Click Context Menu; Set File Explorer to Open This PC; Disable All Windows Notifications; Disable Bluetooth Background Services (`bthserv`, `BTAGService`); Stop Windows from Auto-Installing Drivers; Disable Windows Search File Indexing (`WSearch`).
**Usability & Convenience (10):** Show File Extensions in Explorer; Show Hidden Files and Folders; Enable NumLock on Startup; Disable Snap Layouts on Hover; Disable Taskbar Chat; Disable Windows Tips & Suggestions; Enable Full Wallpaper Quality (`JPEGImportQuality = 100`); Remove Clipboard Suggested Actions `(Perm)`; Disable Start Menu Recommendations; Disable Safe Search in Windows Search.

### F. System Care Cleanup Actions (7)
Clean Up Temporary System Files `(Perm)`; Delete Windows & User Temp Files `(Perm)`; Clear Windows Prefetch Cache `(1x)`; Flush DNS Cache `(1x)`; Clear GPU Shader Cache `(1x)`; Delete Thumbnail Cache `(Perm)`; Clean RAM Working Sets & Standby Cache `(Perm)`.

---

## 7. Complete App Optimizer Catalog (38 Non-AMD Apps, 118 Actions)

*(Apps marked **`*`** are verified installed on this PC and should be prioritized for end-to-end verification; all others auto-hide until detected.)*

- **Browsers (7):** **Chrome\*** (6: cache, bg apps, preload, HW accel, parallel DL, EcoQoS); **Microsoft Edge\*** (8: cache, bg apps, Startup Boost, preload, HW accel, parallel DL, auto-start, EcoQoS); **Firefox\*** (5: cache, bg updates, telemetry, HW accel, EcoQoS); Brave (6: cache, bg apps, preload, HW accel, parallel DL, EcoQoS); Opera (4: cache, parallel DL, auto-start, EcoQoS); Opera GX (4: cache, parallel DL, auto-start, EcoQoS); Vivaldi (3: cache, parallel DL, EcoQoS).
- **Chat & Conferencing (6):** **Discord\***, Telegram, WhatsApp, Slack, Microsoft Teams (3 each: cache, auto-start, lower priority); Zoom (3: logs, auto-start, lower priority).
- **Cloud Sync (4):** **OneDrive\***, **Google Drive\***, Dropbox (3 each: logs, auto-start, slow while playing); iCloud (2: auto-start, slow while playing).
- **Game Launchers (12):** **Steam\***, **Xbox app\***, **EA app\***, **Battle.net\***, Epic Games Launcher, Ubisoft Connect, Riot Client, GOG Galaxy (3 each: cache, auto-start, lower priority); Rockstar Games Launcher, 2K Launcher, Paradox Launcher, Minecraft Launcher (2 each: cache, auto-start).
- **GPU & Capture (2):** **NVIDIA App\*** (4: shader cache, telemetry, network service, HDCP); Overwolf (2: capture caches, auto-start).
- **Peripherals (4):** **Razer Synapse\***, Logitech G HUB, Corsair iCUE, SteelSeries GG (2 each: logs, auto-start).
- **Media & Utilities (3):** Spotify (3: cache, auto-start, lower priority); **Adobe Creative Cloud\*** (3: media cache, auto-start, lower priority); Wallpaper Engine (1: auto-start).

---

## 8. Verification & Acceptance Criteria

1. **Zero Build Errors:** `dotnet build` and `dotnet test` pass cleanly on `.NET 8.0` with zero warnings treated as failures in core safety code.
2. **Snapshot Round-Trip Tests:** Unit tests verify that applying & reverting a Registry key (both pre-existing and previously missing), Service, and JSON snapshot restores the exact initial state.
3. **Non-Destructive Manual Smoke Tests:**
   - Create & enumerate a Windows Restore Point via UI; confirm cooldown bypass works twice in 2 minutes.
   - Apply 1 safe registry tweak (e.g., `MenuShowDelay`), verify live status changes, verify via `regedit`, manually change it back in `regedit` to confirm **Drift Detector** catches the drift and offers 1-click re-apply, then click Revert and verify original value is restored.
   - Verify NVIDIA NVAPI DRS reads current Shader Cache Size / Power Management Mode cleanly and snapshots before any change; verify Intel microcode revision & P-core/E-core topology display accurately.
   - Verify Danger Zone tweaks are hidden/locked until Danger Zone is enabled and prompt for a Restore Point before applying.
   - Verify Appx Bloatware Manager blocks System-Critical packages by default and `winget` lists available updates cleanly.
   - Verify App Optimizer detects the 13 installed apps and hides uninstalled ones; verify cookies/history stay unchecked by default.
   - Test Game Mode using a benign test process (e.g., `notepad.exe` added to custom allowlist): verify priority elevation, EcoQoS on a selected browser, and full restoration upon closing `notepad.exe`.
   - Test **Refresh Rate Watchdog**: pin current target Hz, temporarily drop to 60 Hz in Windows Display Settings, confirm watchdog detects it and 1-click restores target Hz.
   - Test Disk Analyzer on both NVMe drives: verify system folders cannot be deleted, verify locking a test folder blocks deletion, and verify deleting a dummy file moves it to the Recycle Bin.
4. **Publishable Single Executable:** `dotnet publish src/RigTune.App -c Release -r win-x64 --self-contained false` produces a clean local build ready to pin or auto-start via Task Scheduler.
