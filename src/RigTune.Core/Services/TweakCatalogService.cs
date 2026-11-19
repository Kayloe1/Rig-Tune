using System.Text.Json;
using RigTune.Core.Models;
using RigTune.Core.Storage;

namespace RigTune.Core.Services;

/// <summary>
/// Loads the bundled <c>Catalogs\tweaks.json</c> and merges the user-extensible
/// <c>%LOCALAPPDATA%\RigTune\custom_tweaks.json</c> (custom entries add to, or override by id).
/// </summary>
public sealed class TweakCatalogService
{
    private readonly string _bundledPath;
    private readonly string? _customPath;
    private TweakCatalog? _catalog;

    public TweakCatalogService(RigTunePaths paths) : this(RigTunePaths.BundledTweaksFile, paths.CustomTweaksFile)
    {
    }

    public TweakCatalogService(string bundledPath, string? customPath = null)
    {
        _bundledPath = bundledPath;
        _customPath = customPath;
    }

    public IReadOnlyList<string> LoadErrors { get; private set; } = Array.Empty<string>();

    public TweakCatalog Catalog => _catalog ??= Load();

    public IReadOnlyList<TweakDefinition> Tweaks => Catalog.Tweaks;

    public TweakDefinition? Find(string id) =>
        Catalog.Tweaks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    public static TweakCatalog LoadFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<TweakCatalog>(stream, AtomicJsonFile.DefaultOptions)
               ?? throw new InvalidDataException($"{path} is empty.");
    }

    private TweakCatalog Load()
    {
        TweakCatalog catalog = LoadFile(_bundledPath);
        var errors = new List<string>();
        if (_customPath is not null && File.Exists(_customPath))
        {
            try
            {
                TweakCatalog custom = LoadFile(_customPath);
                IReadOnlyList<string> customErrors = TweakCatalogValidator.Validate(custom, requireExactCount: false);
                if (customErrors.Count > 0)
                {
                    errors.AddRange(customErrors.Select(e => $"custom_tweaks.json: {e}"));
                }
                else
                {
                    foreach (TweakDefinition tweak in custom.Tweaks)
                    {
                        catalog.Tweaks.RemoveAll(t => string.Equals(t.Id, tweak.Id, StringComparison.OrdinalIgnoreCase));
                        catalog.Tweaks.Add(tweak);
                    }
                }
            }
            catch (JsonException ex)
            {
                errors.Add($"custom_tweaks.json: {ex.Message}");
            }
        }

        LoadErrors = errors;
        return catalog;
    }
}

/// <summary>Schema and safety-contract validation for tweak catalogs (SPEC §3.2, §4.1).</summary>
public static class TweakCatalogValidator
{
    public const int ExpectedTweakCount = 185;

    public static readonly IReadOnlySet<string> Impacts = new HashSet<string> { "Low", "Medium", "High" };

    /// <summary>Terms for hardware/features explicitly out of scope (SPEC §1).</summary>
    public static readonly IReadOnlyList<string> ForbiddenTerms = new[]
    {
        "AMD", "Radeon", "Adrenalin", "Defrag", "SvcHostSplitThreshold",
    };

    public static IReadOnlyList<string> Validate(TweakCatalog catalog, bool requireExactCount = true)
    {
        var errors = new List<string>();
        if (catalog.SchemaVersion != 1)
        {
            errors.Add($"Unsupported schemaVersion {catalog.SchemaVersion}.");
        }

        if (requireExactCount && catalog.Tweaks.Count != ExpectedTweakCount)
        {
            errors.Add($"Expected {ExpectedTweakCount} tweaks, found {catalog.Tweaks.Count}.");
        }

        foreach (IGrouping<string, TweakDefinition> dup in catalog.Tweaks.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"Duplicate tweak id '{dup.Key}'.");
        }

        var categories = catalog.Tweaks.Select(t => t.Category).ToHashSet(StringComparer.Ordinal);
        foreach (TweakDefinition t in catalog.Tweaks)
        {
            string p = $"[{t.Id}]";
            if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Category) ||
                string.IsNullOrWhiteSpace(t.Subcategory) || string.IsNullOrWhiteSpace(t.Description) || string.IsNullOrWhiteSpace(t.TargetSummary))
            {
                errors.Add($"{p} id, name, category, subcategory, description and targetSummary are required.");
            }

            if (!TweakRiskTiers.All.Contains(t.RiskTier))
            {
                errors.Add($"{p} invalid riskTier '{t.RiskTier}'.");
            }

            if (!TweakUndoTypes.All.Contains(t.UndoType))
            {
                errors.Add($"{p} invalid undoType '{t.UndoType}'.");
            }

            if (!Impacts.Contains(t.Impact))
            {
                errors.Add($"{p} invalid impact '{t.Impact}'.");
            }

            if (t.InRecommendedPreset && (t.IsDangerZone || t.IsPermanent || t.IsReadOnly || t.IsPreset || t.Interactive is not null))
            {
                errors.Add($"{p} Danger Zone, permanent, read-only, interactive and preset tweaks cannot be in a Recommended preset.");
            }

            foreach (string term in ForbiddenTerms)
            {
                if (ContainsWord($"{t.Id} {t.Name} {t.Description} {t.TargetSummary}", term))
                {
                    errors.Add($"{p} references out-of-scope term '{term}'.");
                }
            }

            if (t.IsPreset)
            {
                if (t.Actions.Count > 0)
                {
                    errors.Add($"{p} preset tweaks must not have actions.");
                }

                if (!categories.Contains(t.PresetScope!.Category))
                {
                    errors.Add($"{p} preset scope category '{t.PresetScope.Category}' does not exist.");
                }
                else if (t.PresetScope.Subcategory is { } sub &&
                         !catalog.Tweaks.Any(x => x.Category == t.PresetScope.Category && x.Subcategory == sub))
                {
                    errors.Add($"{p} preset scope subcategory '{sub}' does not exist.");
                }

                continue;
            }

            if (t.Actions.Count == 0)
            {
                errors.Add($"{p} has no actions.");
            }

            for (int i = 0; i < t.Actions.Count; i++)
            {
                errors.AddRange(ValidateAction(t, t.Actions[i]).Select(e => $"{p} action {i}: {e}"));
            }

            if (t.Options is { Count: > 0 } options)
            {
                var keys = t.Actions.Select(a => a.Key).Where(k => k is not null).ToHashSet();
                if (t.DefaultOption is not null && options.All(o => o.Id != t.DefaultOption))
                {
                    errors.Add($"{p} defaultOption '{t.DefaultOption}' is not an option.");
                }

                foreach (string key in options.SelectMany(o => o.Values.Keys).Where(k => !keys.Contains(k)))
                {
                    errors.Add($"{p} option value key '{key}' does not match any action key.");
                }
            }
        }

        return errors;
    }

    private static IEnumerable<string> ValidateAction(TweakDefinition tweak, TweakAction a)
    {
        if (!TweakActionTypes.All.Contains(a.Type))
        {
            yield return $"invalid action type '{a.Type}'.";
            yield break;
        }

        bool optionDriven = a.Key is not null && (tweak.Options?.Any(o => o.Values.ContainsKey(a.Key)) == true || a.Range is not null);
        switch (a.Type)
        {
            case TweakActionTypes.Registry:
                if (a.Hive is null || a.Path is null || a.ValueName is null)
                {
                    yield return "registry needs hive, path and valueName.";
                }

                string op = (a.Operation ?? "set").ToLowerInvariant();
                if (op == "set" && (a.ValueKind is null || (a.ApplyValue is null && !optionDriven)))
                {
                    yield return "registry set needs valueKind and applyValue.";
                }

                if (op is "clearbits" or "setbits" && a.BitMask is null)
                {
                    yield return "bit operations need bitMask.";
                }

                if (op is not ("set" or "delete" or "clearbits" or "setbits" or "inspect"))
                {
                    yield return $"unknown registry operation '{op}'.";
                }

                if (a.ValueKind is not null)
                {
                    string? kindError = null;
                    try
                    {
                        TweakValueParser.ParseKind(a.ValueKind);
                        TweakValueParser.ParseHive(a.Hive);
                    }
                    catch (FormatException ex)
                    {
                        kindError = ex.Message;
                    }

                    if (kindError is not null)
                    {
                        yield return kindError;
                    }
                }

                break;
            case TweakActionTypes.Service:
                if (a.ServiceName is null || !Enum.TryParse<ServiceStartType>(a.StartType, true, out _))
                {
                    yield return "service needs serviceName and a valid startType.";
                }

                break;
            case TweakActionTypes.Power:
                if (!Guid.TryParse(a.Subgroup, out _) || !Guid.TryParse(a.Setting, out _))
                {
                    yield return "power needs subgroup and setting GUIDs.";
                }

                if (!string.Equals(a.Operation, "unhide", StringComparison.OrdinalIgnoreCase) &&
                    a.AcValue is null && a.DcValue is null && !optionDriven)
                {
                    yield return "power needs acValue/dcValue (or an option/range key).";
                }

                break;
            case TweakActionTypes.NvApiDrs:
                if (a.SettingId is null || !a.SettingId.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    yield return "nvapi_drs needs a hex settingId.";
                }

                break;
            case TweakActionTypes.Dism:
                if (a.Operation is null)
                {
                    yield return "dism needs an operation.";
                }
                else if (a.Operation == "feature" && (a.Feature is null || a.ApplyState is null))
                {
                    yield return "dism feature needs feature and applyState.";
                }

                break;
            case TweakActionTypes.ScheduledTask:
                if (a.TaskPath is null || !a.TaskPath.StartsWith('\\') || a.ApplyState is null)
                {
                    yield return "schtask needs taskPath (starting with \\) and applyState.";
                }

                break;
            case TweakActionTypes.TcpProfile:
            case TweakActionTypes.DeviceManager:
            case TweakActionTypes.Cleanup:
                if (a.Operation is null)
                {
                    yield return $"{a.Type} needs an operation.";
                }

                break;
            case TweakActionTypes.Ntfs:
                if (a.Setting is null)
                {
                    yield return "ntfs needs a setting.";
                }

                break;
            case TweakActionTypes.InterruptAffinity:
                if (a.DeviceClass is null)
                {
                    yield return "interrupt_affinity needs deviceClass.";
                }

                break;
        }
    }

    private static bool ContainsWord(string text, string term) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(term)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
