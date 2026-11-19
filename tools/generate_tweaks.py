#!/usr/bin/env python3
"""Generates src/RigTune.Core/Catalogs/tweaks.json — the 185 tailored tweaks of SPEC.md §6.

Edit this file, then run:  python3 tools/generate_tweaks.py
"""
import json, os

TWEAKS = []

# ----------------------------------------------------------------------------- helpers
def reg(hive, path, name, kind=None, value=None, op=None, mask=None, key=None, view=None):
    a = {"type": "registry", "hive": hive, "view": view or "Registry64", "path": path, "valueName": name}
    if op: a["operation"] = op
    if kind: a["valueKind"] = kind
    if value is not None: a["applyValue"] = value
    if mask is not None: a["bitMask"] = mask
    if key: a["key"] = key
    return a

def dword(hive, path, name, value, **kw): return reg(hive, path, name, "DWord", value, **kw)
def sz(hive, path, name, value, **kw): return reg(hive, path, name, "String", value, **kw)

def svc(name, start="Disabled", stop=True):
    return {"type": "service", "serviceName": name, "startType": start, "stopService": stop}

def pwr(sub, setting, ac=None, dc=None, key=None, op=None, rng=None):
    a = {"type": "power", "subgroup": sub, "setting": setting}
    if op: a["operation"] = op
    if ac is not None: a["acValue"] = ac
    if dc is not None: a["dcValue"] = dc
    if key: a["key"] = key
    if rng: a["range"] = rng
    return a

def nv(setting_id, value=None, key=None, op=None):
    a = {"type": "nvapi_drs", "settingId": setting_id}
    if op: a["operation"] = op
    if value is not None: a["applyValue"] = value
    if key: a["key"] = key
    return a

def feature(name, state="Disabled"):
    return {"type": "dism", "operation": "feature", "feature": name, "applyState": state}

def task(path, state="Disabled"):
    return {"type": "schtask", "taskPath": path, "applyState": state}

def summarize(actions):
    parts = []
    for a in actions:
        t = a["type"]
        if t == "registry":
            parts.append(f'{a["hive"]}\\{a["path"]}\\{a["valueName"] or "(Default)"}')
        elif t == "service":
            parts.append(f'Service {a["serviceName"]} → {a["startType"]}')
        elif t == "power":
            parts.append(f'Power {a["subgroup"][:8]}…/{a["setting"]}' + (" (unhide)" if a.get("operation") == "unhide" else ""))
        elif t == "nvapi_drs":
            parts.append(f'NVAPI DRS global {a["settingId"]}')
        elif t == "dism":
            parts.append(f'DISM {a["operation"]}' + (f' {a.get("feature") or a.get("packageName") or a.get("setting") or ""}'.rstrip()))
        elif t == "schtask":
            parts.append(f'Task {a["taskPath"]}')
        elif t == "ntfs":
            parts.append(f'NTFS {a["setting"]}')
        else:
            parts.append(f'{t}{(":" + a["operation"]) if a.get("operation") else ""}')
    s = "; ".join(dict.fromkeys(parts))
    return s if len(s) < 400 else s[:397] + "…"

def T(id, name, cat, sub, desc, actions=None, impact="Low", risk="Standard", rec=False, restart=False,
      undo="Toggle", target=None, warning=None, preset=None, interactive=None, options=None, default=None):
    actions = actions or []
    t = {"id": id, "name": name, "category": cat, "subcategory": sub, "description": desc, "impact": impact,
         "riskTier": risk, "inRecommendedPreset": rec, "requiresRestart": restart, "undoType": undo,
         "targetSummary": target or (f'Applies the recommended {preset["category"]}' + (f' › {preset["subcategory"]}' if preset.get("subcategory") else "") + ' tweaks' if preset else summarize(actions))}
    if warning: t["warning"] = warning
    if preset: t["presetScope"] = preset
    if interactive: t["interactive"] = interactive
    if options: t["options"] = options
    if default: t["defaultOption"] = default
    t["actions"] = actions
    TWEAKS.append(t)

def DZ(**kw):
    kw["risk"] = "DangerZone"; kw["rec"] = False
    T(**kw)

ONCE, PERM, RO = "RunOnce", "Permanent", "ReadOnly"
HKLM, HKCU, HKU = "HKLM", "HKCU", "HKU"

# Policy paths
POL = r"SOFTWARE\Policies\Microsoft\Windows"
CDM = r"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"
ADV = r"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"
EDGE = r"SOFTWARE\Policies\Microsoft\Edge"
CHROME = r"SOFTWARE\Policies\Google\Chrome"
FIREFOX = r"SOFTWARE\Policies\Mozilla\Firefox"
SYSPROFILE = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"
MM = r"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"
WU = POL + r"\WindowsUpdate"
TCPIP6 = r"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters"

# Power GUIDs
SUB_PROCESSOR = "54533251-82be-4824-96c1-47b60b740d00"
HETEROPOLICY = "7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5"
SCHEDPOLICY = "93b8b6dc-0698-4d1c-9ee4-06ed4e92f5eb"
SHORTSCHEDPOLICY = "bae08b81-2d5e-4688-ad6a-132433566544"
CPMINCORES = "0cc5b647-c1df-4637-891a-dec35c318583"
PERFBOOSTMODE = "be337238-0d82-4146-a960-4f3749d470c7"
PERFEPP = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863"
PROCTHROTTLEMIN = "893dee8e-2bef-41e0-89c6-b55d0929964c"
PROCTHROTTLEMAX = "bc5038f7-23e0-4960-96da-33abaf5935ec"
SUB_SLEEP = "238c9fa8-0aad-41ed-83f4-97be242c8f20"
STANDBYIDLE = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da"
SUB_DISK = "0012ee47-9041-4b5d-9b77-535fba8b1442"
DISKIDLE = "6738e2c4-e8a5-4a42-b16a-e040e769756e"
SUB_VIDEO = "7516b95f-f776-4464-8c53-06167f40cc99"
VIDEOIDLE = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"
SUB_PCIE = "501a4d13-42af-4429-9fd1-a8218c268e20"
ASPM = "ee12f906-d277-404b-b6da-e5fa1a576df5"
SUB_USB = "2a737441-1930-4402-8d77-b2bebba308a3"
USBSS = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226"

# ============================================================================ A. PRIVACY (50)
P = "Privacy"
S = "Windows Privacy"
T("privacy.apply_recommended", "Apply Recommended Privacy Tweaks", P, S,
  "Applies every Privacy tweak marked Recommended in one batch (never Danger Zone or permanent items). A restore point is offered first.",
  undo=ONCE, impact="Medium", preset={"category": P})
T("privacy.widgets", "Turn Off Windows Widgets", P, S,
  "Removes the Widgets board and its news feed (and the background process that keeps it updated) via policy.",
  [dword(HKLM, r"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)], rec=True, impact="Medium")
T("privacy.activity_history", "Disable Activity History Tracking", P, S,
  "Stops Windows from collecting and uploading the history of apps, files and sites you open (Timeline / activity feed).",
  [dword(HKLM, POL + r"\System", n, 0) for n in ("EnableActivityFeed", "PublishUserActivities", "UploadUserActivities")], rec=True)
T("privacy.location", "Disable Location Tracking", P, S,
  "Denies location access for all apps (system-wide consent switch). Weather and Maps will no longer auto-detect your location.",
  [sz(HKLM, r"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny")])
T("privacy.storage_sense", "Configure Storage Sense / Cleanup Rules", P, S,
  "Turns on Storage Sense weekly: deletes temporary app files and Recycle Bin items older than 30 days; never touches Downloads.",
  [dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", n, v)
   for n, v in (("01", 1), ("2048", 7), ("04", 1), ("08", 1), ("256", 30), ("32", 0))], undo=PERM)
T("privacy.background_apps", "Block Apps Running in Background", P, S,
  "Prevents Store/UWP apps from running in the background. Desktop (Win32) apps are unaffected.",
  [dword(HKLM, POL + r"\AppPrivacy", "LetAppsRunInBackground", 2)], impact="Medium",
  warning="Store apps such as Phone Link or Xbox may stop delivering background notifications.")
T("privacy.store_in_search", "Remove Microsoft Store from Search Results", P, S,
  "Stops Windows offering \"Look for an app in the Microsoft Store\" for unknown file types and Store results.",
  [dword(HKLM, POL + r"\Explorer", "NoUseStoreOpenWith", 1)], undo=PERM)
T("privacy.telemetry", "Block Microsoft Telemetry Data Collection", P, S,
  "Sets diagnostic data to the lowest level Windows 11 Pro honours (Required/Security) via both policy locations.",
  [dword(HKLM, POL + r"\DataCollection", "AllowTelemetry", 0),
   dword(HKLM, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0)], rec=True, impact="Medium")
T("privacy.error_reporting", "Disable Windows Error Reporting", P, S,
  "Stops crash reports being collected and sent to Microsoft.",
  [dword(HKLM, r"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1),
   dword(HKLM, POL + r"\Windows Error Reporting", "Disabled", 1)], rec=True)
T("privacy.copilot", "Turn Off Microsoft Copilot AI", P, S,
  "Disables the Windows Copilot integration for this user and machine-wide.",
  [dword(HKCU, r"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
   dword(HKLM, POL + r"\WindowsCopilot", "TurnOffWindowsCopilot", 1)], rec=True)
T("privacy.xbox_services", "Disable Xbox Background Services", P, S,
  "Disables the Xbox authentication, cloud save, networking and accessory services (XblAuthManager, XblGameSave, XboxNetApiSvc, XboxGipSvc).",
  [svc("XblAuthManager"), svc("XblGameSave"), svc("XboxNetApiSvc"), svc("XboxGipSvc")], impact="Medium",
  warning="Breaks Xbox app / Game Pass sign-in, cloud saves and Xbox controller accessory updates. The Xbox app is installed on this PC.")
T("privacy.game_dvr", "Turn Off Game DVR and Xbox Bar", P, S,
  "Disables background game recording (Game DVR) and capture, which can cost frame time while gaming.",
  [dword(HKCU, r"System\GameConfigStore", "GameDVR_Enabled", 0),
   dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
   dword(HKLM, POL + r"\GameDVR", "AllowGameDVR", 0)], rec=True, impact="Medium")
T("privacy.handwriting_typing", "Disable Handwriting & Typing Data Collection", P, S,
  "Stops Windows learning from your inking and typing to build a personal dictionary in the cloud.",
  [dword(HKCU, r"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
   dword(HKCU, r"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
   dword(HKCU, r"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0)], rec=True)
T("privacy.input_personalization", "Disable Input Personalization", P, S,
  "Turns off online speech/ink/typing personalization via policy.",
  [dword(HKLM, r"SOFTWARE\Policies\Microsoft\InputPersonalization", "AllowInputPersonalization", 0)], rec=True)
T("privacy.tailored_experiences", "Disable Tailored Experiences", P, S,
  "Stops Microsoft using diagnostic data to personalise tips, ads and recommendations.",
  [dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
   dword(HKCU, r"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1)], rec=True)
T("privacy.app_launch_tracking", "Disable App Launch Tracking", P, S,
  "Stops Windows tracking which apps you launch to personalise Start and search.",
  [dword(HKCU, ADV, "Start_TrackProgs", 0)], rec=True)
T("privacy.feedback_prompts", "Stop Windows Feedback Prompts", P, S,
  "Never ask for feedback (SIUF) and hide feedback notifications.",
  [dword(HKCU, r"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
   dword(HKLM, POL + r"\DataCollection", "DoNotShowFeedbackNotifications", 1)], rec=True)
T("privacy.delivery_optimization", "Block Delivery Optimization Peer Sharing (Policy)", P, S,
  "Updates download only from Microsoft (HTTP), never uploading to or from other PCs.",
  [dword(HKLM, POL + r"\DeliveryOptimization", "DODownloadMode", 0)], rec=True)

S = "Ads & Suggestions"
T("privacy.advertising_id", "Disable Advertising ID", P, S,
  "Turns off the per-user advertising identifier apps use for targeted ads.",
  [dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
   dword(HKLM, POL + r"\AdvertisingInfo", "DisabledByGroupPolicy", 1)], rec=True)
T("privacy.settings_suggestions", "Block Suggested Content in Settings", P, S,
  "Hides suggested content and promotions inside the Settings app.",
  [dword(HKCU, CDM, n, 0) for n in ("SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled")], rec=True)
T("privacy.lock_screen_spotlight", "Disable Lock Screen Spotlight", P, S,
  "Stops rotating Spotlight images, fun facts and tips on the lock screen.",
  [dword(HKCU, CDM, n, 0) for n in ("RotatingLockScreenEnabled", "RotatingLockScreenOverlayEnabled", "SubscribedContent-338387Enabled")], rec=True)
T("privacy.silent_app_installs", "Block Silently Installed Apps", P, S,
  "Prevents Windows from silently installing suggested Store apps.",
  [dword(HKCU, CDM, "SilentInstalledAppsEnabled", 0)], rec=True)
T("privacy.preinstalled_suggestions", "Disable Pre-installed App Suggestions", P, S,
  "Stops OEM and Microsoft pre-installed app suggestions and Start suggestions.",
  [dword(HKCU, CDM, n, 0) for n in ("PreInstalledAppsEnabled", "OemPreInstalledAppsEnabled", "SystemPaneSuggestionsEnabled", "SubscribedContent-338388Enabled")], rec=True)

S = "Search & Cortana"
T("privacy.bing_search", "Disable Bing Web Results in Search", P, S,
  "Start/taskbar search shows only local results — no Bing web suggestions.",
  [dword(HKCU, r"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
   dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0)], rec=True, impact="Medium")
T("privacy.cortana", "Disable Cortana", P, S, "Disables Cortana via policy.",
  [dword(HKLM, POL + r"\Windows Search", "AllowCortana", 0)], rec=True)
T("privacy.search_highlights", "Disable Search Highlights", P, S,
  "Removes trending/\"today in history\" highlights from the search box and search home.",
  [dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0),
   dword(HKLM, POL + r"\Windows Search", "EnableDynamicContentInWSB", 0)], rec=True)
T("privacy.store_search_suggestions", "Disable Microsoft Store Search Suggestions", P, S,
  "Stops search from querying online/cloud sources (Store and Microsoft account content) as you type.",
  [dword(HKLM, POL + r"\Windows Search", "ConnectedSearchUseWeb", 0),
   dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsMSACloudSearchEnabled", 0)], rec=True)

S = "Sync & Clipboard"
T("privacy.clipboard_cloud_sync", "Disable Clipboard Cloud Sync", P, S,
  "Prevents clipboard content being synced to your other devices through the cloud.",
  [dword(HKLM, POL + r"\System", "AllowCrossDeviceClipboard", 0)], rec=True)
T("privacy.clipboard_history", "Disable Clipboard History", P, S,
  "Turns off Win+V clipboard history (items are no longer stored).",
  [dword(HKLM, POL + r"\System", "AllowClipboardHistory", 0)])
T("privacy.settings_sync", "Disable Windows Settings Sync", P, S,
  "Stops syncing themes, passwords and preferences across devices with your Microsoft account.",
  [dword(HKLM, POL + r"\SettingSync", "DisableSettingSync", 2), dword(HKLM, POL + r"\SettingSync", "DisableSettingSyncUserOverride", 1)])
T("privacy.find_my_device", "Disable Find My Device", P, S,
  "Turns off device location reporting for Find My Device (not useful on a desktop).",
  [dword(HKLM, r"SOFTWARE\Policies\Microsoft\FindMyDevice", "AllowFindMyDevice", 0)], rec=True)

S = "AI Features"
T("privacy.recall", "Disable Windows Recall", P, S,
  "Disables Recall snapshots and blocks Recall from being enabled.",
  [dword(HKLM, POL + r"\WindowsAI", "DisableAIDataAnalysis", 1), dword(HKLM, POL + r"\WindowsAI", "AllowRecallEnablement", 0),
   dword(HKCU, r"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1)], rec=True)
T("privacy.click_to_do", "Disable Click To Do", P, S, "Disables Click to Do screen analysis.",
  [dword(HKLM, POL + r"\WindowsAI", "DisableClickToDo", 1), dword(HKCU, r"Software\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1)], rec=True)
T("privacy.edge_compose", "Disable Edge Compose AI", P, S, "Disables Compose (AI writing) in Microsoft Edge.",
  [dword(HKLM, EDGE, "ComposeInlineEnabled", 0)], rec=True)
T("privacy.paint_ai", "Disable Paint AI Features (Cocreator)", P, S, "Disables Cocreator, Generative Fill and Image Creator in Paint.",
  [dword(HKLM, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", n, 1) for n in ("DisableCocreator", "DisableGenerativeFill", "DisableImageCreator")], rec=True)
T("privacy.notepad_ai", "Disable Notepad AI Features", P, S, "Disables Rewrite/Summarize AI features in Notepad.",
  [dword(HKLM, r"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1)], rec=True)
T("privacy.ai_service", "Disable AI Service Auto-Start (AiHost)", P, S,
  "Sets the Windows AI host service to manual start so it only runs when an AI feature asks for it. Skipped if the service is not present.",
  [svc("AiHost", "Manual", False)], rec=True)

S = "Microsoft Edge"
for i, n, d in (("edge_usage_data", "MetricsReportingEnabled", "Disable Edge Usage Data Reporting|Stops Edge sending usage and crash data."),
                ("edge_site_info", "SendSiteInfoToImproveServices", "Disable Edge Site Info Collection|Stops Edge sending info about the sites you visit."),
                ("edge_personalization", "PersonalizationReportingEnabled", "Disable Edge Personalization Reporting|Stops browsing activity being used to personalise ads and news."),
                ("edge_copilot_access", "CopilotCDPPageContext", "Block Copilot Page Access in Edge|Prevents Copilot reading the page you are on."),
                ("edge_copilot_context", "CopilotPageContext", "Block Copilot Page Context in Edge|Prevents Edge passing page context to Copilot."),
                ("edge_discover", "DiscoverPageContextEnabled", "Disable Edge Discover Page Scanning|Stops the Discover pane scanning page content."),
                ("edge_sidebar", "HubsSidebarEnabled", "Hide the Edge Sidebar|Removes the Edge sidebar."),
                ("edge_shopping", "EdgeShoppingAssistantEnabled", "Disable Edge Shopping Assistant|Disables price comparison and coupon pop-ups.")):
    name, desc = d.split("|")
    T(f"privacy.{i}", name, P, S, desc + " (Edge policy)", [dword(HKLM, EDGE, n, 0)], rec=True)

S = "Google Chrome"
T("privacy.chrome_usage_stats", "Disable Chrome Usage Statistics", P, S, "Stops Chrome sending usage statistics and crash reports.", [dword(HKLM, CHROME, "MetricsReportingEnabled", 0)], rec=True)
T("privacy.chrome_feedback", "Disable Chrome Feedback Reports", P, S, "Disables the Chrome feedback reporter.", [dword(HKLM, CHROME, "UserFeedbackAllowed", 0)], rec=True)
T("privacy.chrome_extended_reporting", "Disable Chrome Extended Safe Browsing Reporting", P, S,
  "Stops sending extra page data to Google for Safe Browsing. Standard Safe Browsing protection stays ON.",
  [dword(HKLM, CHROME, "SafeBrowsingExtendedReportingEnabled", 0)], rec=True)

S = "Mozilla Firefox"
T("privacy.firefox_telemetry", "Disable Firefox Telemetry", P, S, "Disables Firefox telemetry via enterprise policy.", [dword(HKLM, FIREFOX, "DisableTelemetry", 1)], rec=True)
T("privacy.firefox_browser_agent", "Disable Firefox Default Browser Agent", P, S,
  "Stops the scheduled agent that reports your default-browser choice to Mozilla.", [dword(HKLM, FIREFOX, "DisableDefaultBrowserAgent", 1)], rec=True)

# ============================================================================ B. DEBLOAT (13)
C = "Debloat"
S = "Optional Windows Features"
T("debloat.features_refresh", "Refresh Optional Features Status", C, S,
  "Re-reads the live install state of every Optional Windows Feature (read-only).",
  [{"type": "dism", "operation": "query"}], undo=RO)
for i, name, feat, desc in (
        ("print_to_pdf", "Toggle Microsoft Print to PDF", "Printing-PrintToPDFServices-Features", "Removes the Microsoft Print to PDF virtual printer."),
        ("media_player_legacy", "Toggle Legacy Windows Media Player", "WindowsMediaPlayer", "Removes legacy Windows Media Player (the modern Media Player app is unaffected)."),
        ("work_folders", "Toggle Work Folders Sync Client", "WorkFolders-Client", "Removes the Work Folders corporate sync client."),
        ("ie11", "Toggle Internet Explorer 11", "Internet-Explorer-Optional-amd64", "Removes the Internet Explorer 11 engine (also used by Edge IE mode)."),
        ("xps", "Toggle XPS Document Support", "Printing-XPSServices-Features", "Removes the XPS document printer and services."),
        ("telnet", "Toggle Telnet Client", "TelnetClient", "Ensures the insecure Telnet client is turned off.")):
    T(f"debloat.{i}", name, C, S, desc + " Revert re-enables it.", [feature(feat)], restart=False)
DZ(id="debloat.smb1", name="Toggle SMB 1.0 File Sharing", cat=C, sub=S,
   desc="Keeps the obsolete, wormable SMB 1.0 protocol turned off. Changing SMB1 is Danger Zone because re-enabling it on revert exposes the PC.",
   actions=[feature("SMB1Protocol")], warning="Old NAS boxes and very old network devices that only speak SMB1 will stop working.")
S = "Microsoft Store & Edge"
DZ(id="debloat.remove_store", name="Remove or Restore Microsoft Store", cat=C, sub=S,
   desc="Removes the Microsoft Store for all users and de-provisions it. Use \"Reinstall Microsoft Store\" to get it back.",
   actions=[{"type": "dism", "operation": "appx_remove", "packageName": "Microsoft.WindowsStore"}], undo=ONCE,
   warning="Store-delivered app updates (Xbox app, NVIDIA App components, Windows apps) stop until the Store is reinstalled.")
DZ(id="debloat.uninstall_onedrive", name="Uninstall OneDrive", cat=C, sub=S,
   desc="Runs Microsoft's OneDrive uninstaller. Files already in your OneDrive cloud are kept online; local sync stops.",
   actions=[{"type": "dism", "operation": "onedrive_uninstall"}], undo=ONCE, warning="OneDrive is installed and in use on this PC.")
DZ(id="debloat.uninstall_edge", name="Uninstall Microsoft Edge", cat=C, sub=S,
   desc="Runs Edge's own system-level uninstaller. WebView2 (used by many apps) is NOT removed.",
   actions=[{"type": "dism", "operation": "edge_uninstall"}], undo=ONCE,
   warning="Windows may block or later reinstall Edge outside the EEA. Some Windows links will open nothing until another browser is default.")
DZ(id="debloat.block_edge_reinstall", name="Stop Edge from Reinstalling Itself", cat=C, sub=S,
   desc="Disables the Edge update services (edgeupdate, edgeupdatem) and blocks automatic Chromium Edge redeployment.",
   actions=[svc("edgeupdate"), svc("edgeupdatem"), dword(HKLM, r"SOFTWARE\Microsoft\EdgeUpdate", "DoNotUpdateToEdgeWithChromium", 1)],
   warning="Edge (and WebView2 delivered by Edge Update) will no longer receive security updates.")
T("debloat.reinstall_store", "Reinstall Microsoft Store", C, S,
  "Reinstalls / repairs the Microsoft Store (wsreset -i).",
  [{"type": "dism", "operation": "appx_restore", "packageName": "Microsoft.WindowsStore"}], undo=ONCE)

# ============================================================================ C. SERVICES (24)
C = "Services"
S = "Service Groups"
T("services.apply_recommended", "Apply Recommended Services Tweaks", C, S,
  "Applies all Recommended service changes (telemetry & safe-to-disable services only).", undo=ONCE, preset={"category": C})
T("services.group_telemetry", "Disable Telemetry Service Group", C, S,
  "Disables Connected User Experiences & Telemetry and the WAP push routing service.",
  [svc("DiagTrack"), svc("dmwappushservice")], rec=True)
T("services.group_search", "Disable Search Indexing Service Group", C, S,
  "Disables Windows Search indexing and SysMain (Superfetch). On fast NVMe SSDs the benefit of both is small.",
  [svc("WSearch"), svc("SysMain")], impact="Medium", warning="Start-menu and File Explorer search become slower (no index).")
DZ(id="services.group_windows_update", name="Disable Windows Update Service Group", cat=C, sub=S,
   desc="Disables Windows Update, Update Orchestrator, Update Medic, BITS and Delivery Optimization services.",
   actions=[svc("wuauserv"), svc("UsoSvc"), svc("WaaSMedicSvc"), svc("bits"), svc("DoSvc")], restart=True, impact="High",
   warning="No security updates, Store updates or Defender platform updates until reverted. This PC runs an Insider build (26300).")
T("services.group_diagnostics", "Disable Diagnostics Service Group", C, S,
  "Disables the Diagnostic Policy Service and its service/system hosts (troubleshooters stop working).",
  [svc("DPS"), svc("WdiServiceHost"), svc("WdiSystemHost")])
S = "Individual Services"
for name, desc, rec, start, warn in (
        ("DiagTrack", "Connected User Experiences and Telemetry.", True, "Disabled", None),
        ("dmwappushservice", "Device Management WAP Push message routing (telemetry helper).", True, "Disabled", None),
        ("PcaSvc", "Program Compatibility Assistant (compatibility pop-ups for old apps).", False, "Disabled", None),
        ("SysMain", "SysMain / Superfetch memory prefetching.", False, "Disabled", None),
        ("WSearch", "Windows Search indexer.", False, "Disabled", "Search becomes slower without an index."),
        ("DPS", "Diagnostic Policy Service.", False, "Disabled", None),
        ("WdiServiceHost", "Diagnostic Service Host.", False, "Disabled", None),
        ("WdiSystemHost", "Diagnostic System Host.", False, "Disabled", None),
        ("MapsBroker", "Downloaded Maps Manager.", True, "Disabled", None),
        ("Fax", "Fax service.", True, "Disabled", None),
        ("PrintNotify", "Printer Extensions and Notifications.", False, "Disabled", "Printer pop-ups and some printer apps stop working."),
        ("TabletInputService", "Touch Keyboard and Handwriting Panel (set to Manual).", False, "Manual", "Also powers the emoji panel and some IMEs; left on Manual rather than Disabled."),
        ("QWAVE", "Quality Windows Audio Video Experience.", False, "Disabled", None),
        ("SharedAccess", "Internet Connection Sharing (ICS).", False, "Disabled", "Mobile Hotspot stops working."),
        ("SSDPSRV", "SSDP Discovery (UPnP device discovery).", False, "Disabled", "Media devices and some network discovery stop working."),
        ("upnphost", "UPnP Device Host.", False, "Disabled", None),
        ("wisvc", "Windows Insider Service.", False, "Disabled", "This PC runs an Insider build (26300) — disabling wisvc breaks Insider flighting.")):
    T(f"services.{name.lower()}", f"Disable {name}" if start == "Disabled" else f"Set {name} to Manual", C, S, desc,
      [svc(name, start, start == "Disabled")], rec=rec, warning=warn)
    if name == "WSearch":
        DZ(id="services.dosvc", name="Disable DoSvc", cat=C, sub=S, desc="Delivery Optimization service.",
           actions=[svc("DoSvc")], restart=True, warning="Windows Update and Store downloads may fail or slow down.")
        DZ(id="services.waasmedicsvc", name="Disable WaaSMedicSvc", cat=C, sub=S,
           desc="Windows Update Medic Service (repairs Windows Update components). Protected service: written via its service key.",
           actions=[svc("WaaSMedicSvc")], restart=True, warning="Windows will no longer self-heal Windows Update.")

# ============================================================================ D. PERFORMANCE (64)
C = "Performance"
S = "Power Plans"
T("perf.power.configure_plan", "Configure Power Plan", C, S,
  "Tune the active plan with sliders: sleep, disk and display timeouts and min/max processor state. The expert view lists every processor setting (incl. hidden ones) with live AC/DC values. Desktop checks: no battery, S0 Low Power Idle, hibernation state.",
  [pwr(SUB_SLEEP, STANDBYIDLE, ac=0, key="sleep", rng={"min": 0, "max": 18000, "step": 60, "unit": "seconds (0 = never)"}),
   pwr(SUB_DISK, DISKIDLE, ac=0, key="disk", rng={"min": 0, "max": 18000, "step": 60, "unit": "seconds (0 = never)"}),
   pwr(SUB_VIDEO, VIDEOIDLE, ac=900, key="display", rng={"min": 0, "max": 18000, "step": 60, "unit": "seconds (0 = never)"}),
   pwr(SUB_PROCESSOR, PROCTHROTTLEMIN, ac=5, key="min_cpu", rng={"min": 0, "max": 100, "step": 5, "unit": "%"}),
   pwr(SUB_PROCESSOR, PROCTHROTTLEMAX, ac=100, key="max_cpu", rng={"min": 5, "max": 100, "step": 5, "unit": "%"})],
  undo=ONCE, interactive="power_plan", impact="Medium")
T("perf.power.pcie_aspm", "Check/Toggle PCIe Link Power Saving", C, S,
  "Turns PCIe Active State Power Management off (AC and DC) so the RTX 4070 Ti SUPER and NVMe links never drop into low-power states mid-game.",
  [pwr(SUB_PCIE, ASPM, ac=0, dc=0)], rec=True, impact="Medium")
T("perf.power.expose_hidden", "Expose Hidden Advanced Power Settings", C, S,
  "Unhides the Intel hybrid scheduler policies, core parking, boost mode and EPP in Control Panel › Power Options (Attributes = 2).",
  [pwr(SUB_PROCESSOR, g, op="unhide") for g in (HETEROPOLICY, SCHEDPOLICY, SHORTSCHEDPOLICY, CPMINCORES, PERFBOOSTMODE, PERFEPP)])

S = "CPU Scheduling"
T("perf.cpu.apply_recommended", "Apply Recommended CPU Tweaks", C, S, "Applies the Recommended CPU scheduling tweaks.",
  undo=ONCE, preset={"category": C, "subcategory": S})
T("perf.cpu.priority_separation", "Prioritize Foreground App CPU Time", C, S,
  "Sets Win32PrioritySeparation to 0x26 (short, variable quanta with foreground boost) so the game in focus gets more CPU time.",
  [dword(HKLM, r"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 38)], rec=True, impact="Medium")
T("perf.cpu.system_responsiveness", "Set System Responsiveness to Zero", C, S,
  "MMCSS reserves no CPU for background tasks (SystemResponsiveness = 0).",
  [dword(HKLM, SYSPROFILE, "SystemResponsiveness", 0)], rec=True)
T("perf.cpu.unpark_cores", "Unpark All CPU Cores", C, S,
  "Sets minimum unparked cores to 100% on AC so all 20 cores stay available.",
  [pwr(SUB_PROCESSOR, CPMINCORES, ac=100)], rec=True, impact="Medium")
T("perf.cpu.power_throttling", "Disable CPU Power Throttling", C, S,
  "Turns off Windows power throttling for all processes.",
  [dword(HKLM, r"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1)],
  warning="This also disables EcoQoS, which RigTune's Game Mode uses to push background apps onto the E-cores. Leave off unless you do not use Game Mode.")
DZ(id="perf.cpu.disable_paging_executive", name="Keep Kernel in RAM / Disable Paging", cat=C, sub=S,
   desc="Keeps kernel-mode drivers and system code in physical RAM (DisablePagingExecutive = 1). With 64 GB there is headroom.",
   actions=[dword(HKLM, MM, "DisablePagingExecutive", 1)], restart=True, warning="Can make kernel crash dumps less useful.")
T("perf.cpu.hybrid_scheduling", "Configure Intel Hybrid Thread Scheduling Policy", C, S,
  "Chooses how Windows 11 places threads on the i7-14700F's 8 P-cores and 12 E-cores (SCHEDPOLICY for long-running and SHORTSCHEDPOLICY for short-running threads under SUB_PROCESSOR). HETEROPOLICY is shown read-only in the expert view. Background work keeps landing on E-cores via EcoQoS.",
  [pwr(SUB_PROCESSOR, SCHEDPOLICY, ac=2, key="sched"), pwr(SUB_PROCESSOR, SHORTSCHEDPOLICY, ac=2, key="short")],
  options=[{"id": "automatic", "name": "Windows default (Automatic)", "values": {"sched": 5, "short": 5}},
           {"id": "prefer_p", "name": "Prefer P-cores for foreground & short threads", "description": "Recommended for gaming on Raptor Lake.", "values": {"sched": 2, "short": 2}},
           {"id": "short_p", "name": "Automatic, short threads prefer P-cores", "values": {"sched": 5, "short": 2}}],
  default="prefer_p", interactive="power_plan", impact="Medium")
T("perf.cpu.microcode_topology", "Inspect Intel 14th-Gen Microcode & Core Topology", C, S,
  "Read-only: shows the active Intel microcode revision (0x12B or newer recommended) and the P-core / E-core logical processor layout.",
  [{"type": "cpu_microcode_inspect"}], undo=RO, target=r"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\Update Revision; GetLogicalProcessorInformationEx")

S = "Graphics Pipeline"
GD = r"SYSTEM\CurrentControlSet\Control\GraphicsDrivers"
T("perf.gpu.apply_recommended", "Apply Recommended Graphics Tweaks", C, S, "Applies the Recommended graphics tweaks.",
  undo=ONCE, preset={"category": C, "subcategory": S})
T("perf.gpu.disable_fso", "Disable Fullscreen Optimizations", C, S,
  "Forces true exclusive fullscreen behaviour for DirectX games globally.",
  [dword(HKCU, r"System\GameConfigStore", n, v) for n, v in (("GameDVR_FSEBehaviorMode", 2), ("GameDVR_HonorUserFSEBehaviorMode", 1), ("GameDVR_FSEBehavior", 2), ("GameDVR_DXGIHonorFSEWindowsCompatible", 1))],
  restart=True, impact="Medium")
T("perf.gpu.rebar_check", "Check Resizable BAR Support", C, S,
  "Read-only: shows the NVIDIA driver's global ReBAR flag. Resizable BAR also needs Above 4G Decoding + Re-Size BAR enabled and CSM disabled in the motherboard BIOS.",
  [nv("0x000F00BA", op="inspect")], undo=RO)
T("perf.gpu.dwm_transparency", "Disable DWM Transparency", C, S, "Turns off acrylic/transparency effects in the shell.",
  [dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0)])
T("perf.gpu.disable_mpo", "Disable Multi-Plane Overlay (MPO)", C, S,
  "Disables MPO, a common fix for flicker and stutter with NVIDIA GPUs on multi-monitor / mixed refresh setups.",
  [dword(HKLM, r"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5), dword(HKLM, GD, "DisableOverlays", 1)], restart=True)
T("perf.gpu.hags", "Enable Hardware GPU Scheduling (HAGS)", C, S,
  "Lets the RTX 4070 Ti SUPER manage its own VRAM scheduling (required for DLSS Frame Generation).",
  [dword(HKLM, GD, "HwSchMode", 2)], rec=True, restart=True, impact="Medium")
T("perf.gpu.windowed_optimizations", "Disable DirectX Maximized Windowed Mode", C, S,
  "Turns off \"Optimizations for windowed games\" (flip-model upgrade of borderless games).",
  [sz(HKCU, r"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "SwapEffectUpgradeEnable=0;")], undo=ONCE)
T("perf.gpu.timer_interrupt", "Disable GPU Timer Interrupt Overhead", C, S,
  "Lowers the graphics kernel's monitor latency tolerance so the GPU is not woken by coarse timer coalescing.",
  [dword(HKLM, GD + r"\Power", "MonitorLatencyTolerance", 1), dword(HKLM, GD + r"\Power", "MonitorRefreshLatencyTolerance", 1)],
  undo=ONCE, restart=True)

S = "NVIDIA Driver"
T("perf.nvidia.hdcp", "Disable HDCP Overhead", C, S,
  "Sets RMHdcpKeyglobZero on the NVIDIA display adapter to skip HDCP key handling.",
  [dword(HKLM, "{NVIDIA_DISPLAY_KEY}", "RMHdcpKeyglobZero", 1)], restart=True,
  warning="Protected content (Netflix 4K, Blu-ray players) will refuse to play until reverted.")
T("perf.nvidia.telemetry", "Disable NVIDIA Telemetry Container", C, S,
  "Disables NvTelemetryContainer (skipped if the current driver no longer ships it).", [svc("NvTelemetryContainer")], rec=True)
T("perf.nvidia.network_service", "Disable NVIDIA Network Service", C, S,
  "Disables NvNetworkService (skipped if not present).", [svc("NvNetworkService")], rec=True)
T("perf.nvidia.shader_cache", "Configure NVIDIA Global Shader Cache Size", C, S,
  "Sets the driver's global shader cache size via nvapi64.dll DRS (0x00A06946). A larger cache reduces shader-compile stutter on your 2 TB NVMe.",
  [nv("0x00A06946", 102400, key="size")],
  options=[{"id": "default", "name": "Driver Default", "values": {"size": "delete"}},
           {"id": "10gb", "name": "10 GB", "values": {"size": 10240}},
           {"id": "100gb", "name": "100 GB", "values": {"size": 102400}},
           {"id": "unlimited", "name": "Unlimited", "values": {"size": "0xFFFFFFFF"}}],
  default="100gb", impact="Medium")
T("perf.nvidia.power_mode", "Configure NVIDIA Power Management Mode", C, S,
  "Sets the global Power Management Mode via nvapi64.dll DRS (0x1057EB71).",
  [nv("0x1057EB71", 1, key="mode")],
  options=[{"id": "normal", "name": "Normal", "values": {"mode": 5}},
           {"id": "adaptive", "name": "Adaptive", "values": {"mode": 0}},
           {"id": "max", "name": "Prefer Maximum Performance", "description": "Keeps clocks up; higher idle power.", "values": {"mode": 1}}],
  default="normal", impact="Medium")
T("perf.nvidia.rebar", "Inspect & Toggle NVIDIA Resizable BAR DRS Profile", C, S,
  "Experimental: forces the driver's global ReBAR feature flags (0x000F00BA, 0x000F00BB) for every game instead of NVIDIA's per-game allow list.",
  [nv("0x000F00BA", 1), nv("0x000F00BB", 1)],
  warning="Some games run slower with ReBAR forced on. Requires ReBAR enabled in the BIOS.")

S = "Input & Device Pipeline"
T("perf.input.apply_recommended", "Apply Recommended Input Tweaks", C, S, "Applies the Recommended input tweaks.",
  undo=ONCE, preset={"category": C, "subcategory": S})
DZ(id="perf.input.msi_mode", name="Review/Configure MSI Mode for PCI Devices", cat=C, sub=S,
   desc="Lists PCI devices with their MSI (message-signaled interrupt) state and lets you enable MSI mode and device priority for the ones you select.",
   actions=[{"type": "pci_msi", "operation": "msi"}], restart=True, undo=ONCE, interactive="pci_msi",
   warning="A device whose driver does not support MSI may fail to start. Revert restores the exact previous values.")
DZ(id="perf.input.gpu_affinity", name="Choose CPU Cores for GPU Interrupts", cat=C, sub=S,
   desc="Experimental: pins the RTX 4070 Ti SUPER's interrupts to chosen logical processors. P-cores are 0–15, E-cores 16–27 on the i7-14700F.",
   actions=[{"type": "interrupt_affinity", "operation": "affinity", "deviceClass": "Display"}], restart=True, undo=ONCE,
   interactive="interrupt_affinity", warning="Pinning to E-cores or too few cores can increase latency.")
DZ(id="perf.input.io_affinity", name="Choose CPU Cores for USB/Network/Audio Interrupts", cat=C, sub=S,
   desc="Experimental: pins USB controller, network adapter and audio controller interrupts to chosen cores (P-cores 0–15, E-cores 16–27).",
   actions=[{"type": "interrupt_affinity", "operation": "affinity", "deviceClass": "USB,Net,Audio"}], restart=True, undo=ONCE,
   interactive="interrupt_affinity", warning="Wrong choices can cause audio crackle or input lag; revert restores Windows defaults.")
T("perf.input.usb_selective_suspend", "Check/Toggle USB Selective Suspend", C, S,
  "Stops Windows suspending idle USB devices (mouse, keyboard, headset, DAC).",
  [pwr(SUB_USB, USBSS, ac=0, dc=0)], rec=True)
T("perf.input.audio_latency", "Check Audio Latency Settings", C, S,
  "Read-only Core Audio / WASAPI inspection: endpoints, shared-mode format and engine periods.",
  [{"type": "wasapi_inspect"}], undo=RO, target="Core Audio (MMDevice API / IAudioClient)")
DZ(id="perf.input.xhci_imod", name="Disable xHCI USB Interrupt Moderation", cat=C, sub=S,
   desc="Shows your xHCI USB controllers and their MSI state. Interrupter moderation itself lives in controller MMIO registers and needs a kernel driver to change, so RigTune reports it instead of writing it.",
   actions=[{"type": "pci_msi", "operation": "xhci_imod"}], restart=True, undo=RO)
T("perf.input.raw_input_queue", "Optimize Raw Input Message Queue", C, S,
  "Reduces mouse/keyboard class driver queue sizes from 100 to 32 events (applies after the next restart).",
  [dword(HKLM, r"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", 32),
   dword(HKLM, r"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", 32)],
  warning="Very small queues can drop input on high-polling-rate mice; 32 is a conservative value.")
T("perf.input.restore_devices", "Restore All Optional Devices", C, S,
  "Re-enables every device RigTune disabled, restoring their exact previous state.",
  [{"type": "device_manager", "operation": "restore_all"}], undo=ONCE)
T("perf.input.disable_unused_devices", "Disable Unused Devices", C, S,
  "Lists rarely-needed system devices rated Safe / Moderate / Risky; disable only the ones you select.",
  [{"type": "device_manager", "operation": "disable_candidates", "devices": [
      {"match": "Microsoft Kernel Debug Network Adapter", "rating": "Safe"},
      {"match": "Remote Desktop Device Redirector Bus", "rating": "Safe", "note": "Breaks redirecting local devices into Remote Desktop sessions."},
      {"match": "Microsoft GS Wavetable Synth", "rating": "Safe", "note": "Only used by legacy MIDI playback."},
      {"match": "System Speaker", "rating": "Safe"},
      {"match": "Composite Bus Enumerator", "rating": "Moderate", "note": "Used by some virtual devices (e.g. Xbox wireless adapters)."},
      {"match": "NDIS Virtual Network Adapter Enumerator", "rating": "Moderate"},
      {"match": "Microsoft Virtual Drive Enumerator", "rating": "Moderate", "note": "Needed to mount VHD/ISO files."},
      {"match": "UMBus Root Bus Enumerator", "rating": "Moderate"},
      {"match": "WAN Miniport", "rating": "Moderate", "note": "Needed for VPNs."},
      {"match": "High precision event timer", "rating": "Risky", "note": "Benefit is disputed; can change timer behaviour."},
      {"match": "Management Engine Interface", "rating": "Risky", "note": "Intel ME; some firmware and power features depend on it."}]}],
  undo=ONCE, interactive="devices")
T("perf.input.mouse_acceleration", "Disable Mouse Pointer Acceleration", C, S,
  "Turns off \"Enhance pointer precision\" for 1:1 mouse movement.",
  [sz(HKCU, r"Control Panel\Mouse", n, "0") for n in ("MouseSpeed", "MouseThreshold1", "MouseThreshold2")], undo=ONCE, rec=True)
T("perf.input.raw_mouse_throttle", "Optimize Raw Mouse Throttle", C, S,
  "Sets RawMouseThrottleDuration to 1 ms (1000 Hz raw-input listener rate) instead of the Windows 11 default of 8 ms.",
  [dword(HKCU, r"Control Panel\Mouse", "RawMouseThrottleDuration", 1)], rec=True, impact="Medium")
T("perf.input.keyboard_repeat", "Optimize Keyboard Repeat Rate & Delay", C, S,
  "Shortest repeat delay and fastest repeat rate.",
  [sz(HKCU, r"Control Panel\Keyboard", "KeyboardDelay", "0"), sz(HKCU, r"Control Panel\Keyboard", "KeyboardSpeed", "31")], undo=ONCE, rec=True)
T("perf.input.accessibility_hotkeys", "Disable Sticky Keys, Filter Keys & Toggle Keys Popups", C, S,
  "Clears the hotkey-active bit (0x4) in the StickyKeys, Keyboard Response (Filter Keys) and ToggleKeys flags, so pressing Shift 5× or holding Shift no longer interrupts a game. The features themselves stay available in Settings.",
  [reg(HKCU, "Control Panel\\Accessibility\\" + k, "Flags", op="clearBits", mask=4) for k in ("StickyKeys", "Keyboard Response", "ToggleKeys")],
  rec=True)
T("perf.input.ssd_trim", "Run SSD TRIM", C, S,
  "Sends a ReTrim to both WD_BLACK NVMe volumes so the SSD controllers know which blocks are free.",
  [{"type": "ntfs", "setting": "retrim"}], undo=PERM, target="ReTrim on every fixed NTFS volume")
T("perf.input.ntfs_tweaks", "Apply NTFS Performance Tweaks", C, S,
  "Disables 8.3 short-name creation and last-access timestamp updates on NTFS volumes.",
  [{"type": "ntfs", "setting": "disable8dot3", "applyValue": 1}, {"type": "ntfs", "setting": "disableLastAccess", "applyValue": "0x80000001"}],
  restart=True, warning="Very old 16-bit era installers may rely on 8.3 names.")
T("perf.input.ntfs_memory", "Set NTFS Memory Usage to Maximum", C, S,
  "Lets NTFS use more paged-pool memory for its caches (NtfsMemoryUsage = 2); sensible with 64 GB RAM.",
  [{"type": "ntfs", "setting": "memoryUsage", "applyValue": 2}], restart=True)

S = "Network Tuning"
TCPP = r"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters"
T("perf.net.apply_recommended", "Apply Recommended Network Tweaks", C, S, "Applies the Recommended network tweaks.",
  undo=ONCE, preset={"category": C, "subcategory": S})
T("perf.net.interrupt_moderation", "Inspect & Test Network Interrupt Moderation", C, S,
  "Shows Interrupt Moderation per physical adapter and lets you switch it per adapter (lower latency vs. CPU load). The adapter is briefly restarted.",
  [{"type": "tcp_profile", "operation": "adapter_property", "keyword": "*InterruptModeration", "applyState": "0"}], undo=ONCE, interactive="network_adapters")
T("perf.net.rss", "Configure Receive Side Scaling (RSS) per Adapter", C, S,
  "Shows RSS per physical adapter and lets you enable/disable it so receive processing spreads across cores.",
  [{"type": "tcp_profile", "operation": "adapter_property", "keyword": "*RSS", "applyState": "1"}], undo=ONCE, interactive="network_adapters")
T("perf.net.qos_dscp", "Enable Network QoS / DSCP Traffic Tagging", C, S,
  "Lets local QoS policies set DSCP tags on a non-domain PC (\"Do not use NLA\").",
  [sz(HKLM, r"SYSTEM\CurrentControlSet\Services\Tcpip\QoS", "Do not use NLA", "1")])
T("perf.net.throttling_index", "Disable Network Throttling Index", C, S,
  "Removes MMCSS network throttling during multimedia playback (NetworkThrottlingIndex = 0xFFFFFFFF).",
  [dword(HKLM, SYSPROFILE, "NetworkThrottlingIndex", "0xFFFFFFFF")], rec=True)
T("perf.net.nagle", "Disable Nagle Algorithm", C, S,
  "Sets TcpAckFrequency = 1 and TCPNoDelay = 1 on every physical adapter's TCP/IP interface.",
  [{"type": "tcp_profile", "operation": "nagle"}])
T("perf.net.tcp_safe_profile", "Apply TCP Optimizer Safe Profile", C, S,
  "Conservative global TCP settings: receive-window auto-tuning Normal, window scaling heuristics off, RSS on.",
  [{"type": "tcp_profile", "operation": "global", "setting": "autotuninglevel", "applyState": "normal"},
   {"type": "tcp_profile", "operation": "global", "setting": "heuristics", "applyState": "disabled"},
   {"type": "tcp_profile", "operation": "global", "setting": "rss", "applyState": "enabled"}], undo=ONCE, rec=True)
T("perf.net.prefer_ipv4", "Prefer IPv4 Over IPv6", C, S,
  "Keeps IPv6 enabled but prefers IPv4 in the prefix policy (DisabledComponents = 0x20).",
  [dword(HKLM, TCPIP6, "DisabledComponents", 32)])
T("perf.net.teredo", "Disable Teredo IPv6 Tunneling", C, S,
  "Disables the Teredo tunnelling adapter via policy.",
  [sz(HKLM, POL + r"\TCPIP\v6Transition", "Teredo_State", "Disabled")],
  warning="Xbox party chat and some peer-to-peer game networking use Teredo.")
DZ(id="perf.net.disable_ipv6", name="Disable IPv6 Protocol Stack", cat=C, sub=S,
   desc="Disables all IPv6 components except loopback (DisabledComponents = 0xFF).",
   actions=[dword(HKLM, TCPIP6, "DisabledComponents", 255)],
   warning="Microsoft does not support disabling IPv6; HomeGroup-style features, some VPNs and Xbox networking can break.")
T("perf.net.timer_resolution", "Set High-Precision Timer Resolution", C, S,
  "Requests a 0.5 ms timer for RigTune's own process only and measures Sleep(1) before and after. Released on Reset or exit.",
  [{"type": "timer_resolution", "targetMs": 0.5}], undo=ONCE, target="NtSetTimerResolution (RigTune process only)")
T("perf.net.media_sense", "Ignore Brief Network Link Drops", C, S,
  "Keeps the IP configuration when the cable/link drops for a moment (DisableDHCPMediaSense = 1).",
  [dword(HKLM, TCPP, "DisableDHCPMediaSense", 1)])

S = "Registry & Policy"
T("perf.registry.apply_recommended", "Apply Recommended Registry Tweaks", C, S, "Applies the Recommended registry & policy tweaks.",
  undo=ONCE, preset={"category": C, "subcategory": S})
T("perf.registry.maintenance_tasks", "Optimize Windows Task Scheduler Maintenance Tasks", C, S,
  "Disables telemetry and data-collection maintenance tasks (compatibility appraiser, CEIP, disk diagnostics, feedback, error queue, maps updates).",
  [task(p) for p in (r"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
                     r"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
                     r"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
                     r"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
                     r"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
                     r"\Microsoft\Windows\Feedback\Siuf\DmClient",
                     r"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
                     r"\Microsoft\Windows\Windows Error Reporting\QueueReporting",
                     r"\Microsoft\Windows\Maps\MapsUpdateTask")], undo=ONCE, rec=True)
T("perf.registry.mmcss", "Optimize MMCSS Multimedia Scheduler", C, S,
  "Raises the MMCSS \"Games\" task: GPU Priority 8, Priority 6, Scheduling Category High, SFIO Priority High.",
  [dword(HKLM, SYSPROFILE + r"\Tasks\Games", "GPU Priority", 8), dword(HKLM, SYSPROFILE + r"\Tasks\Games", "Priority", 6),
   sz(HKLM, SYSPROFILE + r"\Tasks\Games", "Scheduling Category", "High"), sz(HKLM, SYSPROFILE + r"\Tasks\Games", "SFIO Priority", "High")], rec=True)
DZ(id="perf.registry.wu_auto_download", name="Disable Windows Update Auto-Download (wuauserv)", cat=C, sub=S,
   desc="Windows Update notifies before downloading (AUOptions = 2) instead of downloading automatically.",
   actions=[dword(HKLM, WU + r"\AU", "AUOptions", 2), dword(HKLM, WU + r"\AU", "NoAutoUpdate", 0)],
   warning="Security updates wait until you install them manually.")
T("perf.registry.block_wu_drivers", "Block Windows Update from Installing Drivers", C, S,
  "Stops Windows Update replacing your NVIDIA and chipset drivers with older WHQL versions.",
  [dword(HKLM, WU, "ExcludeWUDriversInQualityUpdate", 1)], rec=True, impact="Medium")
T("perf.registry.no_lock_screen", "Disable Lock Screen Policy", C, S, "Goes straight to the sign-in screen.",
  [dword(HKLM, POL + r"\Personalization", "NoLockScreen", 1)])
T("perf.registry.no_auto_reboot", "Disable Auto-Reboot While Logged In", C, S,
  "Windows Update will not restart the PC while you are signed in (e.g. during an overnight download or game).",
  [dword(HKLM, WU + r"\AU", "NoAutoRebootWithLoggedOnUsers", 1)], rec=True)
T("perf.registry.multiselect_limit", "Remove File Explorer Multi-Select Limit", C, S,
  "Raises the 15-item limit for context-menu actions (Open/Print) on multiple selected files to 100.",
  [dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\Explorer", "MultipleInvokePromptMinimum", 100)], undo=PERM)
T("perf.registry.menu_show_delay", "Set Menu Animation Delay to Zero", C, S, "Menus open instantly (MenuShowDelay = 0).",
  [sz(HKCU, r"Control Panel\Desktop", "MenuShowDelay", "0")], rec=True)
T("perf.registry.consumer_features", "Disable Windows Consumer Features", C, S,
  "Stops Windows auto-installing promoted apps and games (Candy Crush, etc.).",
  [dword(HKLM, POL + r"\CloudContent", "DisableWindowsConsumerFeatures", 1)], rec=True)

# ============================================================================ E. SYSTEM (27)
C = "System"
S = "Boot & Recovery"
T("system.fast_startup", "Disable Fast Startup (Hybrid Boot)", C, S,
  "Shutdown becomes a full shutdown, so driver and Windows Update state is always clean (hibernation is already off on this PC).",
  [dword(HKLM, r"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0)], rec=True)
DZ(id="system.spectre_meltdown", name="Toggle Spectre / Meltdown CPU Mitigations", cat=C, sub=S,
   desc="Disables the Spectre v2 / Meltdown kernel mitigations (FeatureSettingsOverride = 3).",
   actions=[dword(HKLM, MM, "FeatureSettingsOverride", 3), dword(HKLM, MM, "FeatureSettingsOverrideMask", 3)], restart=True,
   warning="Removes protection against speculative-execution attacks from malicious code. Gains on a 14700F are small.")
DZ(id="system.vbs_hvci", name="Disable VBS and HVCI Security Layer", cat=C, sub=S,
   desc="Turns off Virtualization-Based Security and Memory Integrity (HVCI).",
   actions=[dword(HKLM, r"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0),
            dword(HKLM, r"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0)],
   restart=True, impact="Medium", warning="Removes kernel exploit protection; some anti-cheat titles require Memory Integrity ON.")
DZ(id="system.hyperv_hypervisor", name="Disable Hyper-V Hypervisor", cat=C, sub=S,
   desc="Sets the boot hypervisor launch type to Off.",
   actions=[{"type": "dism", "operation": "bcd", "setting": "hypervisorlaunchtype", "applyState": "Off"}], restart=True,
   warning="Breaks WSL2 (installed on this PC), Docker, Windows Sandbox and VBS.")
DZ(id="system.winre", name="Toggle Windows Recovery Environment (WinRE)", cat=C, sub=S,
   desc="Disables WinRE (reagentc /disable). Revert re-enables it.",
   actions=[{"type": "dism", "operation": "winre", "applyState": "Disabled"}], undo=ONCE,
   warning="Without WinRE, automatic repair and \"Reset this PC\" from boot are unavailable.")
T("system.boot_menu", "Switch Classic F8 vs. Modern Boot Menu", C, S,
  "Switches to the classic text boot menu (F8 for Safe Mode). Revert restores the modern menu.",
  [{"type": "dism", "operation": "bcd", "setting": "bootmenupolicy", "applyState": "Legacy"}], undo=ONCE)
T("system.boot_log", "Toggle Boot Logging (ntbtlog.txt)", C, S,
  "Writes the driver load log to C:\\Windows\\ntbtlog.txt on every boot.",
  [{"type": "dism", "operation": "bcd", "setting": "bootlog", "applyState": "Yes"}], undo=ONCE)

S = "Windows UI Tweaks"
T("system.ui.apply_recommended", "Apply Recommended Windows UI Tweaks", C, S, "Applies the Recommended Windows UI tweaks.",
  undo=ONCE, preset={"category": C, "subcategory": S})
DZ(id="system.ui.hibernation", name="Disable Hibernation File", cat=C, sub=S,
   desc="Status-aware: hibernation is already off on this PC, so this normally shows as applied. Turning it off frees hiberfil.sys.",
   actions=[dword(HKLM, r"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", 0)],
   warning="Also disables Fast Startup and hibernate.")
T("system.ui.visual_effects", "Optimize Visual Effects for Performance", C, S,
  "Selects \"Adjust for best performance\" (animations, shadows and smoothing off).",
  [dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2)])
T("system.ui.end_task", "Add End Task to Taskbar Right-Click Menu", C, S, "Adds End task to taskbar app menus.",
  [dword(HKCU, ADV + r"\TaskbarDeveloperSettings", "TaskbarEndTask", 1)], rec=True)
T("system.ui.classic_context_menu", "Restore Classic Windows 11 Right-Click Context Menu", C, S,
  "Shows the full classic context menu without \"Show more options\" (takes effect after Explorer restarts).",
  [sz(HKCU, r"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "")])
T("system.ui.explorer_this_pc", "Set File Explorer to Open This PC", C, S, "File Explorer opens to This PC instead of Home.",
  [dword(HKCU, ADV, "LaunchTo", 1)], rec=True)
T("system.ui.notifications", "Disable All Windows Notifications", C, S, "Turns off toast notifications for every app.",
  [dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0)],
  warning="You will also miss RigTune's own tray alerts (e.g. refresh-rate watchdog).")
T("system.ui.bluetooth_services", "Disable Bluetooth Background Services", C, S,
  "Disables the Bluetooth support and audio gateway services (bthserv, BTAGService).",
  [svc("bthserv"), svc("BTAGService")], warning="All Bluetooth devices stop working.")
T("system.ui.driver_autoinstall", "Stop Windows from Auto-Installing Drivers", C, S,
  "Stops device metadata and driver downloads when new hardware is connected.",
  [dword(HKLM, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Metadata", "PreventDeviceMetadataFromNetwork", 1),
   dword(HKLM, r"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig", 0)])
T("system.ui.search_indexing", "Disable Windows Search File Indexing (WSearch)", C, S, "Disables the Windows Search indexing service.",
  [svc("WSearch")], warning="Start-menu and Explorer search become slower.")

S = "Usability & Convenience"
T("system.usability.file_extensions", "Show File Extensions in Explorer", C, S, "Always shows file extensions (safer against fake .pdf.exe files).",
  [dword(HKCU, ADV, "HideFileExt", 0)], rec=True)
T("system.usability.hidden_files", "Show Hidden Files and Folders", C, S, "Shows hidden files and folders in Explorer.",
  [dword(HKCU, ADV, "Hidden", 1)])
T("system.usability.numlock", "Enable NumLock on Startup", C, S, "NumLock is on at the sign-in screen and after sign-in.",
  [sz(HKU, r".DEFAULT\Control Panel\Keyboard", "InitialKeyboardIndicators", "2"), sz(HKCU, r"Control Panel\Keyboard", "InitialKeyboardIndicators", "2")], rec=True)
T("system.usability.snap_hover", "Disable Snap Layouts on Hover", C, S, "Hovering the maximize button no longer pops up Snap layouts.",
  [dword(HKCU, ADV, "EnableSnapAssistFlyout", 0)])
T("system.usability.taskbar_chat", "Disable Taskbar Chat", C, S, "Removes the Chat/Teams button from the taskbar.",
  [dword(HKCU, ADV, "TaskbarMn", 0), dword(HKLM, POL + r"\Windows Chat", "ChatIcon", 3)], rec=True)
T("system.usability.tips", "Disable Windows Tips & Suggestions", C, S, "Stops tips, tricks and \"finish setting up\" nags.",
  [dword(HKCU, CDM, "SubscribedContent-338389Enabled", 0),
   dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0)], rec=True)
T("system.usability.wallpaper_quality", "Enable Full Wallpaper Quality", C, S, "Stops Windows recompressing JPEG wallpapers (JPEGImportQuality = 100).",
  [dword(HKCU, r"Control Panel\Desktop", "JPEGImportQuality", 100)], rec=True)
T("system.usability.clipboard_suggestions", "Remove Clipboard Suggested Actions", C, S,
  "Stops suggested actions (call, add to calendar) popping up when you copy text.",
  [dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\SmartActionPlatform\SmartClipboard", "Disabled", 1)], undo=PERM)
T("system.usability.start_recommendations", "Disable Start Menu Recommendations", C, S,
  "Hides recommended files and app promotions in Start.",
  [dword(HKCU, ADV, "Start_IrisRecommendations", 0), dword(HKCU, ADV, "Start_TrackDocs", 0)], rec=True)
T("system.usability.safe_search", "Disable Safe Search in Windows Search", C, S, "Sets Windows Search SafeSearch to Off.",
  [dword(HKCU, r"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "SafeSearchMode", 0)])

# ============================================================================ F. SYSTEM CARE (7)
C = "System Care"
S = "System Cleanup"
for i, name, op, undo, desc in (
        ("temp_system_files", "Clean Up Temporary System Files", "temp_system_files", PERM, "Windows Update leftovers, delivery optimization cache and old logs."),
        ("temp_files", "Delete Windows & User Temp Files", "temp_files", PERM, "%TEMP% and C:\\Windows\\Temp files not in use."),
        ("prefetch", "Clear Windows Prefetch Cache", "prefetch", ONCE, "C:\\Windows\\Prefetch (rebuilt automatically)."),
        ("dns_cache", "Flush DNS Cache", "dns_cache", ONCE, "Clears the DNS resolver cache."),
        ("gpu_shader_cache", "Clear GPU Shader Cache", "gpu_shader_cache", ONCE, "NVIDIA DXCache/GLCache and the DirectX shader cache (rebuilt by games)."),
        ("thumbnails", "Delete Thumbnail Cache", "thumbnails", PERM, "Explorer thumbnail databases (rebuilt on demand)."),
        ("ram_standby", "Clean RAM Working Sets & Standby Cache", "ram_standby", PERM, "Trims working sets and purges the standby list.")):
    T(f"care.{i}", name, C, S, desc + " Runs from System Care with a size preview.",
      [{"type": "cleanup", "operation": op}], undo=undo)

# ----------------------------------------------------------------------------- write
ids = [t["id"] for t in TWEAKS]
assert len(ids) == len(set(ids)), "duplicate ids"
counts = {}
for t in TWEAKS:
    counts[t["category"]] = counts.get(t["category"], 0) + 1
expected = {"Privacy": 50, "Debloat": 13, "Services": 24, "Performance": 64, "System": 27, "System Care": 7}
assert counts == expected, counts
for t in TWEAKS:
    if t["riskTier"] == "DangerZone":
        assert not t["inRecommendedPreset"]
out = os.path.join(os.path.dirname(__file__), "..", "src", "RigTune.Core", "Catalogs", "tweaks.json")
with open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump({"schemaVersion": 1, "tweaks": TWEAKS}, f, indent=2, ensure_ascii=False)
    f.write("\n")
print(f"wrote {len(TWEAKS)} tweaks: {counts}; DZ={sum(t['riskTier']=='DangerZone' for t in TWEAKS)}")
