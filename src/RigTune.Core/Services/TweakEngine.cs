using RigTune.Core.Models;
using RigTune.Core.Runners.Handlers;

namespace RigTune.Core.Services;

public sealed record PresetResult(PresetScope Scope, IReadOnlyList<TweakApplyResult> Results)
{
    public int Succeeded => Results.Count(r => r.Success);
    public int Failed => Results.Count(r => !r.Success);
    public bool RequiresRestart => Results.Any(r => r.Success && r.RequiresRestart);
}

/// <summary>
/// Data-driven tweak engine. Dispatches each catalog action to the handler registered for its
/// SPEC §4.1 type, snapshots everything through <see cref="SnapshotService"/>, enforces the
/// Danger Zone contract, and rolls back a tweak's partial changes if one of its actions fails.
/// </summary>
public sealed class TweakEngine
{
    private readonly TweakCatalogService _catalog;
    private readonly SnapshotService _snapshots;
    private readonly SettingsService _settings;
    private readonly AuditLogService _audit;
    private readonly Dictionary<string, ITweakActionHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public TweakEngine(TweakCatalogService catalog, SnapshotService snapshots, SettingsService settings,
        AuditLogService audit, IEnumerable<ITweakActionHandler> handlers)
    {
        _catalog = catalog;
        _snapshots = snapshots;
        _settings = settings;
        _audit = audit;
        foreach (ITweakActionHandler handler in handlers)
        {
            foreach (string type in handler.Types)
            {
                _handlers[type] = handler;
            }
        }
    }

    public IReadOnlyList<TweakDefinition> Tweaks => _catalog.Tweaks;

    public TweakDefinition? Find(string id) => _catalog.Find(id);

    public bool CanHandle(string actionType) => _handlers.ContainsKey(actionType);

    // ------------------------------------------------------------------ status

    public async Task<TweakStatusReport> GetStatusAsync(TweakDefinition tweak, TweakParameters? parameters = null,
        CancellationToken cancellationToken = default)
    {
        bool hasSnapshots = (await _snapshots.GetActiveForTweakAsync(tweak.Id, cancellationToken).ConfigureAwait(false)).Count > 0;
        if (tweak.IsPreset)
        {
            return new TweakStatusReport(tweak.Id, TweakStatus.Informational,
                new[] { ActionState.Info("Preset", $"{GetPresetTweaks(tweak.PresetScope!).Count} recommended tweaks") }, hasSnapshots);
        }

        var context = new ActionContext(tweak, parameters ?? TweakParameters.None, _snapshots, cancellationToken);
        var states = new List<ActionState>();
        foreach (TweakAction action in tweak.Actions)
        {
            if (!_handlers.TryGetValue(action.Type, out ITweakActionHandler? handler))
            {
                states.Add(new ActionState(ActionStatus.Unsupported, action.Type, Note: "No runner available."));
                continue;
            }

            try
            {
                states.AddRange(await handler.GetStateAsync(action, context).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                states.Add(new ActionState(ActionStatus.Unknown, action.Type, Note: ex.Message));
            }
        }

        return new TweakStatusReport(tweak.Id, Aggregate(states, hasSnapshots), states, hasSnapshots);
    }

    public static TweakStatus Aggregate(IReadOnlyList<ActionState> states, bool hasSnapshots)
    {
        var relevant = states.Where(s => s.Status is ActionStatus.Applied or ActionStatus.NotApplied).ToList();
        if (relevant.Count == 0)
        {
            if (hasSnapshots)
            {
                return TweakStatus.Active;
            }

            if (states.Count > 0 && states.All(s => s.Status == ActionStatus.NotApplicable))
            {
                return TweakStatus.NotApplicable;
            }

            return states.Any(s => s.Status is ActionStatus.Informational or ActionStatus.Unsupported)
                ? TweakStatus.Informational
                : TweakStatus.Unknown;
        }

        int applied = relevant.Count(s => s.Status == ActionStatus.Applied);
        return applied == relevant.Count ? TweakStatus.Active : applied == 0 ? TweakStatus.Inactive : TweakStatus.Partial;
    }

    // ------------------------------------------------------------------ apply / revert

    public async Task<TweakApplyResult> ApplyAsync(TweakDefinition tweak, TweakParameters? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (tweak.IsDangerZone && !(await _settings.GetAsync(cancellationToken).ConfigureAwait(false)).DangerZoneEnabled)
        {
            return new TweakApplyResult(tweak.Id, false,
                "This is a Danger Zone tweak. Enable the Danger Zone toggle first.", false);
        }

        if (tweak.IsPreset)
        {
            PresetResult preset = await ApplyPresetAsync(tweak.PresetScope!, cancellationToken).ConfigureAwait(false);
            return new TweakApplyResult(tweak.Id, preset.Failed == 0,
                $"{preset.Succeeded} applied, {preset.Failed} failed.", preset.RequiresRestart, preset.Succeeded);
        }

        var context = new ActionContext(tweak, parameters ?? TweakParameters.None, _snapshots, cancellationToken);
        HashSet<Guid> before = (await _snapshots.GetActiveForTweakAsync(tweak.Id, cancellationToken).ConfigureAwait(false))
            .Select(e => e.Id).ToHashSet();
        var log = new List<string>();
        int applied = 0;
        foreach (TweakAction action in tweak.Actions)
        {
            try
            {
                ITweakActionHandler handler = _handlers.GetValueOrDefault(action.Type)
                                              ?? throw new NotSupportedException($"No runner for action type '{action.Type}'.");
                log.AddRange(await handler.ApplyAsync(action, context).ConfigureAwait(false));
                applied++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string rollback = await RollBackNewSnapshotsAsync(tweak, before, cancellationToken).ConfigureAwait(false);
                await _audit.LogAsync(AuditAction.ApplyFailed, $"Failed to apply \"{tweak.Name}\"", tweak.Id, tweak.TargetSummary,
                    $"{ex.Message}{rollback}", success: false, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                return new TweakApplyResult(tweak.Id, false, ex.Message + rollback, false, applied);
            }
        }

        if (tweak.IsReadOnly || tweak.IsPermanent || tweak.Actions.All(a => a.Type == Models.TweakActionTypes.TimerResolution))
        {
            await _audit.LogAsync(tweak.IsReadOnly ? AuditAction.Info : AuditAction.Applied,
                $"{(tweak.IsReadOnly ? "Inspected" : "Ran")} \"{tweak.Name}\"", tweak.Id, tweak.TargetSummary,
                string.Join(Environment.NewLine, log), requiresRestart: tweak.RequiresRestart,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }

        return new TweakApplyResult(tweak.Id, true, log.Count == 0 ? "Applied." : string.Join(Environment.NewLine, log),
            tweak.RequiresRestart, applied);
    }

    /// <summary>Restores the exact pre-change state of everything this tweak changed.</summary>
    public async Task<TweakApplyResult> RevertAsync(TweakDefinition tweak, CancellationToken cancellationToken = default)
    {
        RevertAllResult result = await _snapshots.RevertTweakAsync(tweak.Id, cancellationToken).ConfigureAwait(false);
        if (result.Results.Count == 0)
        {
            return new TweakApplyResult(tweak.Id, true, "Nothing to revert — RigTune has no active changes for this tweak.", false);
        }

        return new TweakApplyResult(tweak.Id, result.Failed == 0,
            $"{result.Succeeded} restored, {result.Failed} failed.", result.NeedingRestart.Count > 0, result.Succeeded);
    }

    public Task<RevertAllResult> RevertAllAsync(CancellationToken cancellationToken = default) =>
        _snapshots.RevertAllAsync(cancellationToken);

    // ------------------------------------------------------------------ presets

    /// <summary>
    /// Recommended tweaks inside a scope. Never includes Danger Zone, permanent (no-undo), read-only,
    /// interactive or nested preset tweaks (SPEC §3.2).
    /// </summary>
    public IReadOnlyList<TweakDefinition> GetPresetTweaks(PresetScope scope) =>
        _catalog.Tweaks.Where(t =>
                t.InRecommendedPreset && !t.IsDangerZone && !t.IsPermanent && !t.IsReadOnly && !t.IsPreset &&
                t.Interactive is null &&
                t.Category == scope.Category &&
                (scope.Subcategory is null || t.Subcategory == scope.Subcategory))
            .ToList();

    public async Task<PresetResult> ApplyPresetAsync(PresetScope scope, CancellationToken cancellationToken = default)
    {
        var results = new List<TweakApplyResult>();
        foreach (TweakDefinition tweak in GetPresetTweaks(scope))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ApplyAsync(tweak, null, cancellationToken).ConfigureAwait(false));
        }

        var preset = new PresetResult(scope, results);
        await _audit.LogAsync(AuditAction.Info,
            $"Recommended preset {scope.Category}{(scope.Subcategory is null ? string.Empty : " › " + scope.Subcategory)}: {preset.Succeeded} applied, {preset.Failed} failed",
            success: preset.Failed == 0, requiresRestart: preset.RequiresRestart, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return preset;
    }

    private async Task<string> RollBackNewSnapshotsAsync(TweakDefinition tweak, HashSet<Guid> before,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SnapshotEntry> created = (await _snapshots.GetActiveForTweakAsync(tweak.Id, cancellationToken).ConfigureAwait(false))
            .Where(e => !before.Contains(e.Id)).ToList();
        if (created.Count == 0)
        {
            return string.Empty;
        }

        int ok = 0;
        foreach (SnapshotEntry entry in created)
        {
            if ((await _snapshots.RevertAsync(entry.Id, CancellationToken.None).ConfigureAwait(false)).Success)
            {
                ok++;
            }
        }

        return $" (rolled back {ok} of {created.Count} change(s) this tweak had already made)";
    }
}
