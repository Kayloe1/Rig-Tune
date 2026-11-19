using System.Text.Json.Serialization;

namespace RigTune.Core.Models;

/// <summary>Root of <c>Catalogs\apps.json</c> and <c>custom_apps.json</c> (SPEC §4.2).</summary>
public sealed class AppCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<AppProfile> Apps { get; set; } = new();
}

public static class AppCategories
{
    public const string Browsers = "Browsers";
    public const string Chat = "Chat & Conferencing";
    public const string CloudSync = "Cloud Sync";
    public const string GameLaunchers = "Game Launchers";
    public const string GpuCapture = "GPU & Capture";
    public const string Peripherals = "Peripherals";
    public const string Media = "Media & Utilities";

    public static readonly IReadOnlyList<string> All = new[] { Browsers, Chat, CloudSync, GameLaunchers, GpuCapture, Peripherals, Media };
}

public static class AppToggleTypes
{
    public const string Startup = "startup";
    public const string ChromiumFlag = "chromium_flag";

    /// <summary>Action types allowed in app toggles: tweak-engine registry/service plus app-specific ones.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        TweakActionTypes.Registry, TweakActionTypes.Service, Startup, ChromiumFlag,
    };
}

public sealed class AppProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    /// <summary>Paths (env vars and '*' wildcards allowed) whose existence means the app is installed.</summary>
    public List<string> DetectPaths { get; set; } = new();

    /// <summary>Uninstall-key DisplayName patterns ('*' wildcards) that also mean the app is installed.</summary>
    public List<string>? UninstallNames { get; set; }

    public List<string> Processes { get; set; } = new();
    public List<AppCache> Caches { get; set; } = new();
    public List<AppToggle> Toggles { get; set; } = new();
    public WhilePlayingRule? WhilePlaying { get; set; }

    /// <summary>True for entries loaded from custom_apps.json.</summary>
    [JsonIgnore]
    public bool IsCustom { get; set; }

    /// <summary>SPEC §7 action count: one "cache" action (all cache items) + each toggle + the While-You-Play rule.</summary>
    [JsonIgnore]
    public int ActionCount => (Caches.Count > 0 ? 1 : 0) + Toggles.Count + (WhilePlaying is null ? 0 : 1);
}

public sealed class AppCache
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Paths { get; set; } = new();
    public bool DefaultChecked { get; set; }

    /// <summary>Cookies / history and other data whose removal signs you out or loses user data.</summary>
    [JsonIgnore]
    public bool IsSensitive =>
        Id.Contains("cookie", StringComparison.OrdinalIgnoreCase) || Id.Contains("history", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("cookie", StringComparison.OrdinalIgnoreCase) || Name.Contains("history", StringComparison.OrdinalIgnoreCase);
}

public sealed class AppToggle
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Warning { get; set; }
    public bool RequiresRestart { get; set; }
    public TweakAction? Action { get; set; }
    public List<TweakAction>? Actions { get; set; }

    [JsonIgnore]
    public IReadOnlyList<TweakAction> AllActions =>
        (Actions ?? new List<TweakAction>()).Concat(Action is null ? Array.Empty<TweakAction>() : new[] { Action }).ToList();
}

/// <summary>How Game Mode treats the app while a game runs (SPEC §3.3.2).</summary>
public sealed class WhilePlayingRule
{
    public bool Enabled { get; set; }

    /// <summary>"EcoQoS" (efficiency mode → E-cores) or "LowerPriority".</summary>
    public string Mode { get; set; } = "EcoQoS";

    public string CpuPriority { get; set; } = "BelowNormal";
    public string IoPriority { get; set; } = "Low";

    public WhilePlayingRule Clone() => new() { Enabled = Enabled, Mode = Mode, CpuPriority = CpuPriority, IoPriority = IoPriority };
}

/// <summary>Persisted to <c>gamemode.json</c> (per-app overrides now; game lists and sessions in the Game Mode phase).</summary>
public sealed class GameModeDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, WhilePlayingRule> AppRules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CustomExeAllowlist { get; set; } = new();
    public List<string> ExclusionList { get; set; } = new();
}

public sealed record AppDetection(AppProfile Profile, bool IsInstalled, string? Evidence);

public sealed record AppCacheEstimate(string CacheId, long Bytes, int Files, IReadOnlyList<string> ExistingPaths);

public sealed record AppCacheCleanResult(string AppId, long BytesFreed, int FilesDeleted, int FilesSkipped,
    IReadOnlyList<string> Messages);
