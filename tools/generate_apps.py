#!/usr/bin/env python3
"""Generates src/RigTune.Core/Catalogs/apps.json — the 38 non-AMD App Optimizer profiles of SPEC.md §7.

SPEC action counting: all cache items of an app = 1 "cache" action, + each toggle, + the While-You-Play rule.
Edit this file, then run:  python3 tools/generate_apps.py
"""
import json, os

APPS = []
L, R, PD, PF, PF86 = "%LOCALAPPDATA%", "%APPDATA%", "%PROGRAMDATA%", "%PROGRAMFILES%", "%PROGRAMFILES(X86)%"

def cache(id, name, paths, checked=True):
    return {"id": id, "name": name, "paths": paths, "defaultChecked": checked}

def chromium_caches(user_data, cache_root=None):
    """Web cache + cookies + history for a Chromium profile directory (cookies/history unchecked)."""
    c = cache_root or user_data
    return [
        cache("web_cache", "Cached images, code & GPU/shader caches",
              [c + r"\*\Cache", c + r"\*\Code Cache", c + r"\*\GPUCache", c + r"\*\DawnCache", c + r"\*\DawnGraphiteCache"]),
        cache("cookies", "Cookies & site data (signs you out)", [user_data + r"\*\Network\Cookies*"], False),
        cache("history", "Browsing history (signs you out)", [user_data + r"\*\History*"], False),
    ]

def opera_caches(roaming_profile, local_profile):
    return [
        cache("web_cache", "Cached images, code & GPU/shader caches",
              [local_profile + r"\Cache", roaming_profile + r"\Code Cache", roaming_profile + r"\GPUCache"]),
        cache("cookies", "Cookies & site data (signs you out)", [roaming_profile + r"\Network\Cookies*"], False),
        cache("history", "Browsing history (signs you out)", [roaming_profile + r"\History*"], False),
    ]

def policy(id, name, hive_path, value_name, value, desc=None, warning=None):
    t = {"id": id, "name": name, "action": {"type": "registry", "hive": "HKLM", "view": "Registry64", "path": hive_path,
                                            "valueName": value_name, "valueKind": "DWord", "applyValue": value}}
    if desc: t["description"] = desc
    if warning: t["warning"] = warning
    return t

def flag(local_state, process):
    return {"id": "parallel_dl", "name": "Enable parallel downloading",
            "description": "Turns on the chrome://flags parallel-downloading experiment (splits large downloads into several connections). Close the browser first.",
            "action": {"type": "chromium_flag", "localStatePath": local_state, "flag": "enable-parallel-downloading",
                       "applyState": "Enabled", "processName": process}}

def autostart(match=None, family=None, warning=None, name="Turn off auto-start"):
    a = {"type": "startup"}
    if match: a["match"] = match
    if family: a["packageFamily"] = family
    t = {"id": "auto_start", "name": name,
         "description": "Disables the app's Windows startup entry the same way Task Manager does (the entry stays visible and can be re-enabled).",
         "action": a}
    if warning: t["warning"] = warning
    return t

def service(id, name, svc, desc):
    return {"id": id, "name": name, "description": desc,
            "action": {"type": "service", "serviceName": svc, "startType": "Disabled", "stopService": True}}

ECO = {"enabled": True, "mode": "EcoQoS", "cpuPriority": "BelowNormal", "ioPriority": "Low"}
SLOW = {"enabled": True, "mode": "EcoQoS", "cpuPriority": "BelowNormal", "ioPriority": "Low"}
LOWER = {"enabled": True, "mode": "LowerPriority", "cpuPriority": "BelowNormal", "ioPriority": "Normal"}

HW_WARN = "Lowers GPU contention while a game runs, but video playback and scrolling then use the CPU."

def app(id, name, category, detect, processes, caches=None, toggles=None, while_playing=None, uninstall=None):
    a = {"id": id, "name": name, "category": category, "detectPaths": detect}
    if uninstall: a["uninstallNames"] = uninstall
    a["processes"] = processes
    a["caches"] = caches or []
    a["toggles"] = toggles or []
    if while_playing: a["whilePlaying"] = dict(while_playing)
    APPS.append(a)

# ============================================================================ Browsers (7)
B = "Browsers"
CHROME_POL = r"SOFTWARE\Policies\Google\Chrome"
app("chrome", "Google Chrome", B, [L + r"\Google\Chrome\User Data", PF + r"\Google\Chrome\Application\chrome.exe"], ["chrome.exe"],
    chromium_caches(L + r"\Google\Chrome\User Data"),
    [policy("bg_apps", "Turn off background apps", CHROME_POL, "BackgroundModeEnabled", 0, "Chrome no longer keeps running in the tray after you close it."),
     policy("preload", "Turn off page preloading", CHROME_POL, "NetworkPredictionOptions", 2, "Stops Chrome prefetching pages you might open."),
     policy("hw_accel", "Turn off hardware acceleration", CHROME_POL, "HardwareAccelerationModeEnabled", 0, warning=HW_WARN),
     flag(L + r"\Google\Chrome\User Data\Local State", "chrome.exe")], ECO)

EDGE_POL = r"SOFTWARE\Policies\Microsoft\Edge"
app("edge", "Microsoft Edge", B, [L + r"\Microsoft\Edge\User Data", PF86 + r"\Microsoft\Edge\Application\msedge.exe"], ["msedge.exe"],
    chromium_caches(L + r"\Microsoft\Edge\User Data"),
    [policy("bg_apps", "Turn off background apps", EDGE_POL, "BackgroundModeEnabled", 0, "Edge no longer keeps running after you close it."),
     policy("startup_boost", "Turn off Startup Boost", EDGE_POL, "StartupBoostEnabled", 0, "Stops Edge pre-launching processes at sign-in."),
     policy("preload", "Turn off page preloading", EDGE_POL, "NetworkPredictionOptions", 2),
     policy("hw_accel", "Turn off hardware acceleration", EDGE_POL, "HardwareAccelerationModeEnabled", 0, warning=HW_WARN),
     flag(L + r"\Microsoft\Edge\User Data\Local State", "msedge.exe"),
     autostart(["MicrosoftEdgeAutoLaunch*"])], ECO)

FF_POL = r"SOFTWARE\Policies\Mozilla\Firefox"
app("firefox", "Mozilla Firefox", B, [PF + r"\Mozilla Firefox\firefox.exe", R + r"\Mozilla\Firefox\Profiles"], ["firefox.exe"],
    [cache("web_cache", "Cached images, scripts & startup cache",
           [L + r"\Mozilla\Firefox\Profiles\*\cache2", L + r"\Mozilla\Firefox\Profiles\*\startupCache", L + r"\Mozilla\Firefox\Profiles\*\thumbnails"]),
     cache("cookies", "Cookies & site data (signs you out)", [R + r"\Mozilla\Firefox\Profiles\*\cookies.sqlite*"], False)],
    [policy("bg_updates", "Turn off background updates", FF_POL, "BackgroundAppUpdate", 0, "Firefox only updates while it is open."),
     policy("telemetry", "Turn off telemetry", FF_POL, "DisableTelemetry", 1),
     policy("hw_accel", "Turn off hardware acceleration", FF_POL, "HardwareAcceleration", 0, warning=HW_WARN)], ECO)

BRAVE_POL = r"SOFTWARE\Policies\BraveSoftware\Brave"
app("brave", "Brave", B, [L + r"\BraveSoftware\Brave-Browser\User Data"], ["brave.exe"],
    chromium_caches(L + r"\BraveSoftware\Brave-Browser\User Data"),
    [policy("bg_apps", "Turn off background apps", BRAVE_POL, "BackgroundModeEnabled", 0),
     policy("preload", "Turn off page preloading", BRAVE_POL, "NetworkPredictionOptions", 2),
     policy("hw_accel", "Turn off hardware acceleration", BRAVE_POL, "HardwareAccelerationModeEnabled", 0, warning=HW_WARN),
     flag(L + r"\BraveSoftware\Brave-Browser\User Data\Local State", "brave.exe")], ECO)

app("opera", "Opera", B, [R + r"\Opera Software\Opera Stable", L + r"\Programs\Opera\opera.exe"], ["opera.exe"],
    opera_caches(R + r"\Opera Software\Opera Stable", L + r"\Opera Software\Opera Stable"),
    [flag(R + r"\Opera Software\Opera Stable\Local State", "opera.exe"),
     autostart(["Opera Browser Assistant*", "Opera Stable*", "Opera Browser*"])], ECO)

app("opera_gx", "Opera GX", B, [R + r"\Opera Software\Opera GX Stable", L + r"\Programs\Opera GX\opera.exe"], ["opera.exe"],
    opera_caches(R + r"\Opera Software\Opera GX Stable", L + r"\Opera Software\Opera GX Stable"),
    [flag(R + r"\Opera Software\Opera GX Stable\Local State", "opera.exe"),
     autostart(["Opera GX*"])], ECO)

app("vivaldi", "Vivaldi", B, [L + r"\Vivaldi\User Data", L + r"\Vivaldi\Application\vivaldi.exe"], ["vivaldi.exe"],
    chromium_caches(L + r"\Vivaldi\User Data"),
    [flag(L + r"\Vivaldi\User Data\Local State", "vivaldi.exe")], ECO)

# ============================================================================ Chat & Conferencing (6)
C = "Chat & Conferencing"
def electron_cache(root):
    return [cache("cache", "App cache (images, code & GPU caches)", [root + r"\Cache", root + r"\Code Cache", root + r"\GPUCache"])]

app("discord", "Discord", C, [L + r"\Discord\Update.exe", R + r"\discord"], ["Discord.exe"],
    electron_cache(R + r"\discord"), [autostart(["Discord"])], LOWER)
app("telegram", "Telegram", C, [R + r"\Telegram Desktop\Telegram.exe", L + r"\Packages\TelegramMessengerLLP.TelegramDesktop_*"], ["Telegram.exe"],
    [cache("cache", "Media & file cache", [R + r"\Telegram Desktop\tdata\user_data\cache", R + r"\Telegram Desktop\tdata\user_data\media_cache"])],
    [autostart(["Telegram Desktop*"], "TelegramMessengerLLP.TelegramDesktop_t4vj0pshhgkwm")], LOWER)
app("whatsapp", "WhatsApp", C, [L + r"\Packages\5319275A.WhatsAppDesktop_cv1g1gvanyjgm"], ["WhatsApp.exe", "WhatsApp.Root.exe"],
    [cache("cache", "App web cache", [L + r"\Packages\5319275A.WhatsAppDesktop_cv1g1gvanyjgm\LocalState\EBWebView\Default\Cache",
                                       L + r"\Packages\5319275A.WhatsAppDesktop_cv1g1gvanyjgm\LocalState\EBWebView\Default\Code Cache",
                                       L + r"\Packages\5319275A.WhatsAppDesktop_cv1g1gvanyjgm\LocalCache\Roaming\WhatsApp\Cache"])],
    [autostart(family="5319275A.WhatsAppDesktop_cv1g1gvanyjgm")], LOWER)
app("slack", "Slack", C, [L + r"\slack\slack.exe", R + r"\Slack"], ["slack.exe"],
    [cache("cache", "App cache (images, code & GPU caches)", [R + r"\Slack\Cache", R + r"\Slack\Code Cache", R + r"\Slack\GPUCache", R + r"\Slack\Service Worker\CacheStorage"])],
    [autostart(["com.squirrel.slack.slack", "Slack*"])], LOWER)
app("teams", "Microsoft Teams", C, [L + r"\Packages\MSTeams_8wekyb3d8bbwe", L + r"\Microsoft\Teams\Update.exe"], ["ms-teams.exe", "Teams.exe"],
    [cache("cache", "App web cache", [L + r"\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\Default\Cache",
                                       L + r"\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\Default\Code Cache",
                                       R + r"\Microsoft\Teams\Cache"])],
    [autostart(["com.squirrel.Teams.Teams"], "MSTeams_8wekyb3d8bbwe")], LOWER)
app("zoom", "Zoom", C, [R + r"\Zoom\bin\Zoom.exe", PF + r"\Zoom\bin\Zoom.exe"], ["Zoom.exe"],
    [cache("logs", "Logs", [R + r"\Zoom\logs"])], [autostart(["Zoom", "ZoomWorkplace*"])], LOWER)

# ============================================================================ Cloud Sync (4)
S = "Cloud Sync"
SYNC_WARN = "Files stop syncing until you open the app yourself."
app("onedrive", "OneDrive", S, [L + r"\Microsoft\OneDrive\OneDrive.exe", PF + r"\Microsoft OneDrive\OneDrive.exe"], ["OneDrive.exe"],
    [cache("logs", "Logs", [L + r"\Microsoft\OneDrive\logs"])], [autostart(["OneDrive"], warning=SYNC_WARN)], SLOW)
app("google_drive", "Google Drive", S, [PF + r"\Google\Drive File Stream", L + r"\Google\DriveFS"], ["GoogleDriveFS.exe"],
    [cache("logs", "Logs", [L + r"\Google\DriveFS\Logs"])], [autostart(["GoogleDriveFS*"], warning=SYNC_WARN)], SLOW)
app("dropbox", "Dropbox", S, [PF86 + r"\Dropbox\Client\Dropbox.exe", PF + r"\Dropbox\Client\Dropbox.exe", R + r"\Dropbox"], ["Dropbox.exe"],
    [cache("logs", "Logs", [R + r"\Dropbox\logs"])], [autostart(["Dropbox"], warning=SYNC_WARN)], SLOW)
app("icloud", "iCloud", S, [L + r"\Packages\AppleInc.iCloud_nzyj5cx40ttqa", PF + r"\Common Files\Apple\Internet Services"], ["iCloudDrive.exe", "iCloudServices.exe"],
    None, [autostart(["iCloud*"], "AppleInc.iCloud_nzyj5cx40ttqa", warning=SYNC_WARN)], SLOW)

# ============================================================================ Game Launchers (12)
G = "Game Launchers"
LAUNCH_WARN = "Games that need the launcher running will start it when you launch them."
app("steam", "Steam", G, [PF86 + r"\Steam\steam.exe"], ["steam.exe", "steamwebhelper.exe"],
    [cache("cache", "Web & HTTP cache (not games)", [L + r"\Steam\htmlcache", PF86 + r"\Steam\appcache\httpcache"])],
    [autostart(["Steam"], warning=LAUNCH_WARN)], LOWER)
app("xbox", "Xbox app", G, [L + r"\Packages\Microsoft.GamingApp_8wekyb3d8bbwe"], ["XboxPcApp.exe", "XboxPcAppFT.exe"],
    [cache("cache", "Web cache", [L + r"\Packages\Microsoft.GamingApp_8wekyb3d8bbwe\AC\INetCache"])],
    [autostart(family="Microsoft.GamingApp_8wekyb3d8bbwe", warning=LAUNCH_WARN)], LOWER)
app("ea_app", "EA app", G, [PF + r"\Electronic Arts\EA Desktop"], ["EADesktop.exe"],
    [cache("cache", "Launcher & browser cache", [L + r"\Electronic Arts\EA Desktop\cache", L + r"\Electronic Arts\EA Desktop\CEF\BrowserCache"])],
    [autostart(["EADM", "EA Desktop*", "EADesktop*"], warning=LAUNCH_WARN)], LOWER)
app("battlenet", "Battle.net", G, [PF86 + r"\Battle.net\Battle.net.exe", PD + r"\Battle.net"], ["Battle.net.exe"],
    [cache("cache", "Launcher & browser cache", [L + r"\Battle.net\Cache", L + r"\Battle.net\BrowserCache", PD + r"\Battle.net\Agent\data\cache"])],
    [autostart(["Battle.net*"], warning=LAUNCH_WARN)], LOWER)
app("epic", "Epic Games Launcher", G, [PF86 + r"\Epic Games\Launcher", PF + r"\Epic Games\Launcher"], ["EpicGamesLauncher.exe"],
    [cache("cache", "Launcher web cache", [L + r"\EpicGamesLauncher\Saved\webcache*"])],
    [autostart(["EpicGamesLauncher*"], warning=LAUNCH_WARN)], LOWER)
app("ubisoft", "Ubisoft Connect", G, [PF86 + r"\Ubisoft\Ubisoft Game Launcher"], ["UbisoftConnect.exe", "upc.exe"],
    [cache("cache", "Launcher HTTP & asset cache", [PF86 + r"\Ubisoft\Ubisoft Game Launcher\cache\http", PF86 + r"\Ubisoft\Ubisoft Game Launcher\cache\assets"])],
    [autostart(["Ubisoft*", "UbisoftConnect*"], warning=LAUNCH_WARN)], LOWER)
app("riot", "Riot Client", G, [L + r"\Riot Games\Riot Client"], ["RiotClientServices.exe", "Riot Client.exe"],
    [cache("cache", "Client web cache", [L + r"\Riot Games\Riot Client\Data\Cache", L + r"\Riot Games\Riot Client\Logs"])],
    [autostart(["RiotClient*", "Riot Client*"], warning=LAUNCH_WARN)], LOWER, uninstall=["Riot Client*"])
app("gog", "GOG Galaxy", G, [PF86 + r"\GOG Galaxy\GalaxyClient.exe"], ["GalaxyClient.exe"],
    [cache("cache", "Galaxy web cache", [PD + r"\GOG.com\Galaxy\webcache"])],
    [autostart(["GogGalaxy*", "GOG Galaxy*"], warning=LAUNCH_WARN)], LOWER)
app("rockstar", "Rockstar Games Launcher", G, [PF + r"\Rockstar Games\Launcher"], ["Launcher.exe", "RockstarService.exe"],
    [cache("cache", "Launcher web cache", [L + r"\Rockstar Games\Launcher\Cache", L + r"\Rockstar Games\Launcher\webcache"])],
    [autostart(["Rockstar*"], warning=LAUNCH_WARN)])
app("twok", "2K Launcher", G, [L + r"\2K Launcher", PF86 + r"\2K Launcher"], ["2KLauncher.exe"],
    [cache("cache", "Launcher web cache", [L + r"\2K Launcher\Cache", L + r"\2KLauncher\Cache"])],
    [autostart(["2K Launcher*", "2KLauncher*"], warning=LAUNCH_WARN)], uninstall=["2K Launcher*"])
app("paradox", "Paradox Launcher", G, [L + r"\Programs\Paradox Interactive\launcher-v2", R + r"\Paradox Interactive\launcher-v2"], ["Paradox Launcher.exe"],
    [cache("cache", "Launcher cache", [R + r"\Paradox Interactive\launcher-v2\Cache", R + r"\Paradox Interactive\launcher-v2\Code Cache",
                                        R + r"\Paradox Interactive\launcher-v2\GPUCache"])],
    [autostart(["Paradox*"], warning=LAUNCH_WARN)])
app("minecraft", "Minecraft Launcher", G, [PF86 + r"\Minecraft Launcher", L + r"\Packages\Microsoft.4297127D64EC6_8wekyb3d8bbwe"], ["MinecraftLauncher.exe"],
    [cache("cache", "Launcher web cache (worlds are never touched)", [R + r"\.minecraft\webcache2"])],
    [autostart(["Minecraft*"], "Microsoft.4297127D64EC6_8wekyb3d8bbwe", warning=LAUNCH_WARN)])

# ============================================================================ GPU & Capture (2)
V = "GPU & Capture"
app("nvidia_app", "NVIDIA App", V, [PF + r"\NVIDIA Corporation\NVIDIA App"], ["NVIDIA App.exe", "NVIDIA Overlay.exe"],
    [cache("shader_cache", "Driver shader cache (DXCache / GLCache — rebuilt by games)", [L + r"\NVIDIA\DXCache", L + r"\NVIDIA\GLCache"])],
    [service("telemetry", "Disable NVIDIA telemetry container", "NvTelemetryContainer", "Skipped automatically if this driver no longer ships it."),
     service("network_service", "Disable NVIDIA network service", "NvNetworkService", "Skipped automatically if not present."),
     {"id": "hdcp", "name": "Disable HDCP overhead", "description": "Sets RMHdcpKeyglobZero on the NVIDIA display adapter.",
      "warning": "Protected content (Netflix 4K, Blu-ray) will refuse to play until reverted.", "requiresRestart": True,
      "action": {"type": "registry", "hive": "HKLM", "view": "Registry64", "path": "{NVIDIA_DISPLAY_KEY}",
                 "valueName": "RMHdcpKeyglobZero", "valueKind": "DWord", "applyValue": 1}}],
    uninstall=["NVIDIA App*"])
app("overwolf", "Overwolf", V, [L + r"\Overwolf", PF86 + r"\Overwolf"], ["Overwolf.exe", "OverwolfBrowser.exe"],
    [cache("capture_cache", "Capture & browser caches", [L + r"\Overwolf\BrowserCache", L + r"\Overwolf\Log"])],
    [autostart(["Overwolf*"])])

# ============================================================================ Peripherals (4)
P = "Peripherals"
PERIPH_WARN = "Lighting, macros and DPI profiles stored only in the software won't load until you open it (on-board profiles still work)."
app("razer_synapse", "Razer Synapse", P, [PF86 + r"\Razer\Synapse3", L + r"\Razer\RazerAppEngine", PF + r"\Razer"], ["Razer Synapse 3.exe", "RazerAppEngine.exe"],
    [cache("logs", "Logs", [L + r"\Razer\Synapse3\Log", PD + r"\Razer\Synapse3\Logs", L + r"\Razer\RazerAppEngine\User Data\Logs"])],
    [autostart(["Razer Synapse*", "RazerAppEngine*", "Razer Central*"], warning=PERIPH_WARN)])
app("ghub", "Logitech G HUB", P, [PF + r"\LGHUB"], ["lghub.exe", "lghub_agent.exe"],
    [cache("logs", "Logs", [L + r"\LGHUB\logs"])], [autostart(["LGHUB*"], warning=PERIPH_WARN)])
app("icue", "Corsair iCUE", P, [PF + r"\Corsair\Corsair iCUE5 Software", PF + r"\Corsair\CORSAIR iCUE 4 Software"], ["iCUE.exe"],
    [cache("logs", "Logs", [L + r"\Corsair\CUE5\logs", L + r"\Corsair\CUE4\logs"])], [autostart(["Corsair iCUE*", "iCUE*"], warning=PERIPH_WARN)])
app("steelseries_gg", "SteelSeries GG", P, [PF + r"\SteelSeries\GG"], ["SteelSeriesGG.exe", "SteelSeriesEngine.exe"],
    [cache("logs", "Logs", [PD + r"\SteelSeries\GG\logs"])], [autostart(["SteelSeriesGG*", "SteelSeries GG*"], warning=PERIPH_WARN)])

# ============================================================================ Media & Utilities (3)
M = "Media & Utilities"
app("spotify", "Spotify", M, [R + r"\Spotify\Spotify.exe", L + r"\Packages\SpotifyAB.SpotifyMusic_zpdnekdrzrea0"], ["Spotify.exe"],
    [cache("cache", "Streaming cache", [L + r"\Spotify\Data", L + r"\Packages\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\LocalCache\Spotify\Data"])],
    [autostart(["Spotify*"], "SpotifyAB.SpotifyMusic_zpdnekdrzrea0")], LOWER)
app("adobe_cc", "Adobe Creative Cloud", M, [PF + r"\Adobe\Adobe Creative Cloud", PF86 + r"\Adobe\Adobe Creative Cloud"], ["Creative Cloud.exe", "CCXProcess.exe"],
    [cache("media_cache", "Media cache (Premiere / After Effects; rebuilt on demand)", [R + r"\Adobe\Common\Media Cache Files", R + r"\Adobe\Common\Media Cache"])],
    [autostart(["Adobe Creative Cloud*", "AdobeGCInvoker*", "CCXProcess*", "Adobe CCXProcess*"])], LOWER)
app("wallpaper_engine", "Wallpaper Engine", M, [PF86 + r"\Steam\steamapps\common\wallpaper_engine"], ["wallpaper32.exe", "wallpaper64.exe"],
    None, [autostart(["Wallpaper Engine*", "WallpaperEngine*"])])

# ----------------------------------------------------------------------------- write
ids = [a["id"] for a in APPS]
assert len(ids) == len(set(ids)) == 38, len(ids)
def actions(a): return (1 if a["caches"] else 0) + len(a["toggles"]) + (1 if "whilePlaying" in a else 0)
total = sum(actions(a) for a in APPS)
assert total == 118, total
for a in APPS:
    for c in a["caches"]:
        if any(k in c["id"] for k in ("cookie", "history")):
            assert not c["defaultChecked"] and "(signs you out)" in c["name"], (a["id"], c)
out = os.path.join(os.path.dirname(__file__), "..", "src", "RigTune.Core", "Catalogs", "apps.json")
with open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump({"schemaVersion": 1, "apps": APPS}, f, indent=2, ensure_ascii=False)
    f.write("\n")
cats = {}
for a in APPS: cats[a["category"]] = cats.get(a["category"], 0) + 1
print(f"wrote {len(APPS)} apps / {total} actions: {cats}")
