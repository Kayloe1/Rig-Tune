using System.Diagnostics;
using System.Text.Json;
using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Services.AppxWinget;
using RigTune.Core.Storage;

namespace RigTune.Core.Services.AppOptimizer;

/// <summary>
/// App Optimizer (SPEC §5.4): detects installed apps, measures and cleans their caches, applies/reverts
/// reversible toggles through the tweak engine (so every change is snapshotted), persists per-app
/// "While You Play" rules to <c>gamemode.json</c>, and manages <c>custom_apps.json</c>.
/// </summary>
public sealed class AppOptimizerService
{
    private static readonly (RegistryHiveName Hive, RegistryViewName View)[] UninstallRoots =
    {
        (RegistryHiveName.HKLM, RegistryViewName.Registry64),
        (RegistryHiveName.HKLM, RegistryViewName.Registry32),
        (RegistryHiveName.HKCU, RegistryViewName.Registry64),
    };

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly TweakEngine _engine;
    private readonly IRegistryAccess _registry;
    private readonly AuditLogService _audit;
    private readonly string _bundledPath;
    private readonly string _customPath;
    private readonly JsonDocumentStore<GameModeDocument> _gameMode;
    private readonly Func<string, string?> _env;
    private readonly SemaphoreSlim _customLock = new(1, 1);
    private List<AppProfile>? _apps;

    public AppOptimizerService(TweakEngine engine, IRegistryAccess registry, AuditLogService audit, RigTunePaths paths)
        : this(engine, registry, audit, RigTunePaths.BundledAppsFile, paths.CustomAppsFile,
            new JsonDocumentStore<GameModeDocument>(paths.GameModeFile))
    {
    }

    public AppOptimizerService(TweakEngine engine, IRegistryAccess registry, AuditLogService audit, string bundledPath,
        string customPath, JsonDocumentStore<GameModeDocument> gameMode, Func<string, string?>? environment = null)
    {
        _engine = engine;
        _registry = registry;
        _audit = audit;
        _bundledPath = bundledPath;
        _customPath = customPath;
        _gameMode = gameMode;
        _env = environment ?? Environment.GetEnvironmentVariable;
    }

    public IReadOnlyList<string> LoadErrors { get; private set; } = Array.Empty<string>();

    public static AppCatalog LoadFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AppCatalog>(stream, AtomicJsonFile.DefaultOptions)
               ?? throw new InvalidDataException($"{path} is empty.");
    }

    public IReadOnlyList<AppProfile> Apps => _apps ??= LoadAll();

    public AppProfile? Find(string id) => Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    private List<AppProfile> LoadAll()
    {
        List<AppProfile> apps = LoadFile(_bundledPath).Apps;
        var errors = new List<string>();
        if (File.Exists(_customPath))
        {
            try
            {
                AppCatalog custom = LoadFile(_customPath);
                IReadOnlyList<string> customErrors = AppCatalogValidator.Validate(custom, builtIn: false);
                errors.AddRange(customErrors.Select(e => $"custom_apps.json: {e}"));
                if (customErrors.Count == 0)
                {
                    foreach (AppProfile app in custom.Apps)
                    {
                        app.IsCustom = true;
                        apps.RemoveAll(a => string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase));
                        apps.Add(app);
                    }
                }
            }
            catch (JsonException ex)
            {
                errors.Add($"custom_apps.json: {ex.Message}");
            }
        }

        LoadErrors = errors;
        return apps;
    }

    // ------------------------------------------------------------------ detection

    public Task<IReadOnlyList<AppDetection>> DetectAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        IReadOnlyList<string> uninstallNames = ReadUninstallDisplayNames();
        return (IReadOnlyList<AppDetection>)Apps.Select(app => Detect(app, uninstallNames)).ToList();
    }, cancellationToken);

    public AppDetection Detect(AppProfile app, IReadOnlyList<string> uninstallDisplayNames)
    {
        foreach (string pattern in app.DetectPaths)
        {
            IReadOnlyList<string> found = AppPathExpander.Expand(pattern, _env);
            if (found.Count > 0)
            {
                return new AppDetection(app, true, found[0]);
            }
        }

        foreach (string pattern in app.UninstallNames ?? new List<string>())
        {
            string? match = uninstallDisplayNames.FirstOrDefault(n => AppxClassifier.IsMatch(n, pattern));
            if (match is not null)
            {
                return new AppDetection(app, true, $"Installed program: {match}");
            }
        }

        return new AppDetection(app, false, null);
    }

    public IReadOnlyList<string> ReadUninstallDisplayNames()
    {
        var names = new List<string>();
        foreach ((RegistryHiveName hive, RegistryViewName view) in UninstallRoots)
        {
            foreach (string sub in _registry.GetSubKeyNames(hive, view, UninstallKey))
            {
                if (_registry.ReadValue(new RegistryTarget(hive, view, $@"{UninstallKey}\{sub}", "DisplayName"))?.String is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    // ------------------------------------------------------------------ caches

    public Task<IReadOnlyList<AppCacheEstimate>> ScanCachesAsync(AppProfile app, CancellationToken cancellationToken = default) =>
        Task.Run(() => (IReadOnlyList<AppCacheEstimate>)app.Caches.Select(c =>
        {
            List<string> paths = ExpandCache(c);
            (long bytes, int files) = SafeFileCleaner.Measure(paths);
            return new AppCacheEstimate(c.Id, bytes, files, paths);
        }).ToList(), cancellationToken);

    public async Task<AppCacheCleanResult> CleanCachesAsync(AppProfile app, IEnumerable<string> cacheIds,
        CancellationToken cancellationToken = default)
    {
        var selected = app.Caches.Where(c => cacheIds.Contains(c.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        var messages = new List<string>();
        long freed = 0;
        int deleted = 0, skipped = 0;

        string? running = app.Processes.FirstOrDefault(IsRunning);
        foreach (AppCache cache in selected)
        {
            if (cache.IsSensitive && running is not null)
            {
                messages.Add($"{cache.Name}: skipped — close {running} first (the browser keeps these files open).");
                continue;
            }

            (long bytes, int d, int s) = await Task.Run(() => SafeFileCleaner.Delete(ExpandCache(cache)), cancellationToken).ConfigureAwait(false);
            freed += bytes;
            deleted += d;
            skipped += s;
            messages.Add($"{cache.Name}: {CleanupFormat(bytes)} freed{(s > 0 ? $", {s} file(s) in use skipped" : string.Empty)}.");
        }

        if (running is not null && skipped > 0)
        {
            messages.Add($"{running} is running — close it to clean the remaining files.");
        }

        var result = new AppCacheCleanResult(app.Id, freed, deleted, skipped, messages);
        await _audit.LogAsync(AuditAction.Cleaned, $"Cleaned {app.Name} caches ({CleanupFormat(freed)})", $"app.{app.Id}.cache",
            string.Join("; ", selected.Select(c => c.Id)), string.Join(" ", messages), cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
        return result;
    }

    private List<string> ExpandCache(AppCache cache) =>
        cache.Paths.SelectMany(p => AppPathExpander.Expand(p, _env)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsRunning(string processName)
    {
        try
        {
            Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName));
            foreach (Process p in processes)
            {
                p.Dispose();
            }

            return processes.Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string CleanupFormat(long bytes) => SystemCare.CleanupEngine.FormatBytes(bytes);

    // ------------------------------------------------------------------ toggles

    /// <summary>The synthetic tweak an app toggle runs as (id <c>app.{appId}.{toggleId}</c>).</summary>
    public static TweakDefinition ToTweak(AppProfile app, AppToggle toggle) => new()
    {
        Id = $"app.{app.Id}.{toggle.Id}",
        Name = $"{app.Name}: {toggle.Name}",
        Category = "App Optimizer",
        Subcategory = app.Category,
        Description = toggle.Description ?? toggle.Name,
        Warning = toggle.Warning,
        RequiresRestart = toggle.RequiresRestart,
        TargetSummary = app.Name,
        Actions = toggle.AllActions.ToList(),
    };

    public Task<TweakStatusReport> GetToggleStatusAsync(AppProfile app, AppToggle toggle, CancellationToken cancellationToken = default) =>
        _engine.GetStatusAsync(ToTweak(app, toggle), null, cancellationToken);

    public async Task<TweakApplyResult> ApplyToggleAsync(AppProfile app, AppToggle toggle, CancellationToken cancellationToken = default)
    {
        TweakApplyResult result = await _engine.ApplyAsync(ToTweak(app, toggle), null, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(result.Success ? AuditAction.Applied : AuditAction.ApplyFailed, $"{app.Name}: {toggle.Name}",
            $"app.{app.Id}.{toggle.Id}", app.Name, result.Message, result.Success, cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
        return result;
    }

    public Task<TweakApplyResult> RevertToggleAsync(AppProfile app, AppToggle toggle, CancellationToken cancellationToken = default) =>
        _engine.RevertAsync(ToTweak(app, toggle), cancellationToken);

    // ------------------------------------------------------------------ While You Play

    /// <summary>The effective rule: the user's override from gamemode.json, else the catalog default.</summary>
    public async Task<WhilePlayingRule?> GetWhilePlayingAsync(AppProfile app, CancellationToken cancellationToken = default)
    {
        GameModeDocument doc = await _gameMode.ReadAsync(cancellationToken).ConfigureAwait(false);
        return doc.AppRules.TryGetValue(app.Id, out WhilePlayingRule? rule) ? rule : app.WhilePlaying?.Clone();
    }

    public async Task SetWhilePlayingAsync(AppProfile app, WhilePlayingRule rule, CancellationToken cancellationToken = default)
    {
        if (!AppCatalogValidator.WhilePlayingModes.Contains(rule.Mode))
        {
            throw new ArgumentException($"Unknown While-You-Play mode '{rule.Mode}'.", nameof(rule));
        }

        await _gameMode.UpdateAsync(doc => doc.AppRules[app.Id] = rule.Clone(), cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(AuditAction.Info,
            $"{app.Name}: While You Play {(rule.Enabled ? $"on ({rule.Mode}, CPU {rule.CpuPriority}, I/O {rule.IoPriority})" : "off")}",
            $"app.{app.Id}.while_playing", cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, WhilePlayingRule>> GetEnabledRulesAsync(CancellationToken cancellationToken = default)
    {
        var rules = new Dictionary<string, WhilePlayingRule>(StringComparer.OrdinalIgnoreCase);
        foreach (AppProfile app in Apps)
        {
            if (await GetWhilePlayingAsync(app, cancellationToken).ConfigureAwait(false) is { Enabled: true } rule)
            {
                rules[app.Id] = rule;
            }
        }

        return rules;
    }

    // ------------------------------------------------------------------ custom apps

    public async Task<IReadOnlyList<string>> SaveCustomAppAsync(AppProfile app, CancellationToken cancellationToken = default)
    {
        AppCatalog single = new() { Apps = { app } };
        IReadOnlyList<string> errors = AppCatalogValidator.Validate(single, builtIn: false);
        if (errors.Count > 0)
        {
            return errors;
        }

        if (!app.IsCustom && LoadFile(_bundledPath).Apps.Any(a => string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return new[] { $"'{app.Id}' is a built-in profile id; choose another." };
        }

        await _customLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppCatalog custom = File.Exists(_customPath) ? LoadFile(_customPath) : new AppCatalog();
            custom.Apps.RemoveAll(a => string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase));
            custom.Apps.Add(app);
            await AtomicJsonFile.WriteAsync(_customPath, custom, cancellationToken: cancellationToken).ConfigureAwait(false);
            _apps = null;
        }
        finally
        {
            _customLock.Release();
        }

        await _audit.LogAsync(AuditAction.Info, $"Saved custom app profile \"{app.Name}\"", $"app.{app.Id}",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return Array.Empty<string>();
    }

    public async Task DeleteCustomAppAsync(string id, CancellationToken cancellationToken = default)
    {
        await _customLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_customPath))
            {
                return;
            }

            AppCatalog custom = LoadFile(_customPath);
            custom.Apps.RemoveAll(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            await AtomicJsonFile.WriteAsync(_customPath, custom, cancellationToken: cancellationToken).ConfigureAwait(false);
            _apps = null;
        }
        finally
        {
            _customLock.Release();
        }

        await _audit.LogAsync(AuditAction.Info, $"Deleted custom app profile \"{id}\"", $"app.{id}",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Builds a custom profile from simple form inputs.</summary>
    public static AppProfile BuildCustomApp(string name, string category, string executablePath, IEnumerable<string> cachePaths,
        string? startupMatch, WhilePlayingRule? whilePlaying)
    {
        string id = "custom_" + new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var app = new AppProfile
        {
            Id = id,
            Name = name.Trim(),
            Category = category,
            DetectPaths = { executablePath.Trim() },
            Processes = { AppPathExpander.FileName(executablePath.Trim()) },
            WhilePlaying = whilePlaying,
            IsCustom = true,
        };

        List<string> caches = cachePaths.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (caches.Count > 0)
        {
            app.Caches.Add(new AppCache { Id = "cache", Name = "Cache", Paths = caches, DefaultChecked = true });
        }

        if (!string.IsNullOrWhiteSpace(startupMatch))
        {
            app.Toggles.Add(new AppToggle
            {
                Id = "auto_start",
                Name = "Turn off auto-start",
                Action = new TweakAction { Type = AppToggleTypes.Startup, Match = new List<string> { startupMatch.Trim() } },
            });
        }

        return app;
    }
}
