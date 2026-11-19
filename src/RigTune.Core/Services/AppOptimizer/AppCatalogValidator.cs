using System.Text.RegularExpressions;
using RigTune.Core.Models;

namespace RigTune.Core.Services.AppOptimizer;

/// <summary>Schema and safety validation for app catalogs (SPEC §4.2, §7).</summary>
public static class AppCatalogValidator
{
    public const int ExpectedAppCount = 38;
    public const int ExpectedActionCount = 118;

    public static readonly IReadOnlyDictionary<string, int> ExpectedCategoryCounts = new Dictionary<string, int>
    {
        [AppCategories.Browsers] = 7,
        [AppCategories.Chat] = 6,
        [AppCategories.CloudSync] = 4,
        [AppCategories.GameLaunchers] = 12,
        [AppCategories.GpuCapture] = 2,
        [AppCategories.Peripherals] = 4,
        [AppCategories.Media] = 3,
    };

    public static readonly IReadOnlyList<string> ForbiddenTerms = new[] { "AMD", "Radeon", "Adrenalin", "RadeonSoftware" };

    /// <summary>Path fragments that would mean editing game files/configs (out of scope, SPEC §1).</summary>
    public static readonly IReadOnlyList<string> ForbiddenPathFragments = new[] { "steamapps", ".cfg", ".ini", "\\Saved Games", "SaveGames" };

    public static readonly IReadOnlySet<string> WhilePlayingModes = new HashSet<string> { "EcoQoS", "LowerPriority" };

    public static IReadOnlyList<string> Validate(AppCatalog catalog, bool builtIn = true)
    {
        var errors = new List<string>();
        if (catalog.SchemaVersion != 1)
        {
            errors.Add($"Unsupported schemaVersion {catalog.SchemaVersion}.");
        }

        if (builtIn)
        {
            if (catalog.Apps.Count != ExpectedAppCount)
            {
                errors.Add($"Expected {ExpectedAppCount} apps, found {catalog.Apps.Count}.");
            }

            int actions = catalog.Apps.Sum(a => a.ActionCount);
            if (actions != ExpectedActionCount)
            {
                errors.Add($"Expected {ExpectedActionCount} actions, found {actions}.");
            }

            foreach ((string category, int count) in ExpectedCategoryCounts)
            {
                int actual = catalog.Apps.Count(a => a.Category == category);
                if (actual != count)
                {
                    errors.Add($"Category '{category}': expected {count} apps, found {actual}.");
                }
            }
        }

        foreach (var dup in catalog.Apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"Duplicate app id '{dup.Key}'.");
        }

        foreach (AppProfile app in catalog.Apps)
        {
            string p = $"[{app.Id}]";
            if (string.IsNullOrWhiteSpace(app.Id) || string.IsNullOrWhiteSpace(app.Name))
            {
                errors.Add($"{p} id and name are required.");
            }

            if (!AppCategories.All.Contains(app.Category))
            {
                errors.Add($"{p} unknown category '{app.Category}'.");
            }

            if (app.DetectPaths.Count == 0 && (app.UninstallNames?.Count ?? 0) == 0)
            {
                errors.Add($"{p} needs detectPaths or uninstallNames.");
            }

            string text = $"{app.Id} {app.Name} {string.Join(' ', app.DetectPaths)} {string.Join(' ', app.Processes)}";
            foreach (string term in ForbiddenTerms.Where(t => Regex.IsMatch(text, $@"\b{Regex.Escape(t)}\b", RegexOptions.IgnoreCase)))
            {
                errors.Add($"{p} references out-of-scope term '{term}'.");
            }

            foreach (string path in app.DetectPaths.Concat(app.Caches.SelectMany(c => c.Paths)))
            {
                errors.AddRange(ValidatePath(path).Select(e => $"{p} {e}"));
            }

            foreach (AppCache cache in app.Caches)
            {
                if (cache.Paths.Count == 0)
                {
                    errors.Add($"{p} cache '{cache.Id}' has no paths.");
                }

                if (cache.IsSensitive && (cache.DefaultChecked || !cache.Name.Contains("(signs you out)", StringComparison.Ordinal)))
                {
                    errors.Add($"{p} cookie/history cache '{cache.Id}' must be unchecked by default and say \"(signs you out)\".");
                }

                foreach (string bad in ForbiddenPathFragments.Where(f => cache.Paths.Any(x => x.Contains(f, StringComparison.OrdinalIgnoreCase))))
                {
                    errors.Add($"{p} cache '{cache.Id}' touches game files/config ('{bad}').");
                }
            }

            foreach (AppToggle toggle in app.Toggles)
            {
                if (toggle.AllActions.Count == 0)
                {
                    errors.Add($"{p} toggle '{toggle.Id}' has no action.");
                }

                foreach (TweakAction action in toggle.AllActions.Where(a => !AppToggleTypes.All.Contains(a.Type)))
                {
                    errors.Add($"{p} toggle '{toggle.Id}' uses unsupported action type '{action.Type}'.");
                }

                foreach (TweakAction action in toggle.AllActions)
                {
                    if (action.Type == AppToggleTypes.Startup && (action.Match?.Count ?? 0) == 0 && action.PackageFamily is null)
                    {
                        errors.Add($"{p} startup toggle '{toggle.Id}' needs match patterns or a packageFamily.");
                    }

                    if (action.Type == AppToggleTypes.ChromiumFlag && (action.LocalStatePath is null || action.Flag is null || action.ApplyState is null))
                    {
                        errors.Add($"{p} chromium_flag toggle '{toggle.Id}' needs localStatePath, flag and applyState.");
                    }
                }
            }

            if (app.WhilePlaying is { } rule && !WhilePlayingModes.Contains(rule.Mode))
            {
                errors.Add($"{p} invalid whilePlaying mode '{rule.Mode}'.");
            }
        }

        return errors;
    }

    /// <summary>Paths must start with an allowed %VARIABLE% and never be a bare variable root.</summary>
    public static IEnumerable<string> ValidatePath(string path)
    {
        IReadOnlyList<string> vars = AppPathExpander.VariablesIn(path);
        if (vars.Count == 0 || !path.StartsWith('%'))
        {
            yield return $"path '{path}' must start with an environment variable such as %LOCALAPPDATA%.";
        }

        foreach (string v in vars.Where(v => !AppPathExpander.AllowedVariables.Contains(v)))
        {
            yield return $"path '{path}' uses unsupported variable %{v}%.";
        }

        if (path.TrimEnd('\\').EndsWith('%'))
        {
            yield return $"path '{path}' points at a whole system folder.";
        }
    }
}
