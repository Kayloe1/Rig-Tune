using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Services;

namespace RigTune.Core.Runners.Handlers;

/// <summary>
/// <c>dism</c> actions: optional features (snapshotted state), Appx/Store removal &amp; restore,
/// OneDrive / Edge uninstallers, boot configuration (BCD) and WinRE (snapshotted state).
/// </summary>
public sealed class DismActionHandler : ITweakActionHandler
{
    private readonly DismRunner _dism;
    private readonly DismFeatureStateRunner _features;
    private readonly BcdStateRunner _bcd;
    private readonly WinReStateRunner _winre;

    public DismActionHandler(DismRunner dism, DismFeatureStateRunner features, BcdStateRunner bcd, WinReStateRunner winre)
    {
        _dism = dism;
        _features = features;
        _bcd = bcd;
        _winre = winre;
    }

    public IReadOnlyCollection<string> Types { get; } = new[] { TweakActionTypes.Dism };

    public Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context) => Task.Run(() =>
    {
        string op = Op(action);
        ActionState state = op switch
        {
            "query" => Summarize(),
            "feature" => StateOf(_features, action.Feature!, context.ResolveState(action)),
            "bcd" => StateOf(_bcd, action.Setting!, context.ResolveState(action)),
            "winre" => StateOf(_winre, "winre", context.ResolveState(action)),
            "appx_remove" => Installed(_dism.IsAppxInstalled(action.PackageName!), $"Appx: {action.PackageName}", wantInstalled: false),
            "appx_restore" => Installed(_dism.IsAppxInstalled(action.PackageName!), $"Appx: {action.PackageName}", wantInstalled: true),
            "onedrive_uninstall" => Installed(DismRunner.IsOneDriveInstalled(), "Microsoft OneDrive", wantInstalled: false),
            "edge_uninstall" => Installed(DismRunner.IsEdgeInstalled(), "Microsoft Edge", wantInstalled: false),
            _ => new ActionState(ActionStatus.Unsupported, op),
        };
        return (IReadOnlyList<ActionState>)new[] { state };
    });

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        string op = Op(action);
        string desired = context.ResolveState(action);
        switch (op)
        {
            case "query":
                IReadOnlyDictionary<string, string> all = await Task.Run(() => _dism.QueryFeatures(refresh: true)).ConfigureAwait(false);
                return new[] { $"{all.Count(f => f.Value == "Enabled")} of {all.Count} optional features enabled." };
            case "feature":
                return await ApplyState(_features.Kind, action.Feature!, desired, context).ConfigureAwait(false);
            case "bcd":
                return await ApplyState(_bcd.Kind, action.Setting!, desired, context).ConfigureAwait(false);
            case "winre":
                return await ApplyState(_winre.Kind, "winre", desired, context).ConfigureAwait(false);
            case "appx_remove":
                return Log(await Task.Run(() => _dism.RemoveAppxPackage(action.PackageName!)).ConfigureAwait(false), $"Removed {action.PackageName}");
            case "appx_restore":
                return Log(await Task.Run(() => _dism.RestoreAppxPackage(action.PackageName!)).ConfigureAwait(false), $"Restored {action.PackageName}");
            case "onedrive_uninstall":
                return Log(await Task.Run(_dism.UninstallOneDrive).ConfigureAwait(false), "OneDrive uninstalled (cloud files are kept online)");
            case "edge_uninstall":
                return Log(await Task.Run(_dism.UninstallEdge).ConfigureAwait(false), "Microsoft Edge uninstall requested");
            default:
                throw new NotSupportedException($"dism operation '{op}' is not supported.");
        }
    }

    private static async Task<IReadOnlyList<string>> ApplyState(string kind, string target, string desired, ActionContext context)
    {
        StateSnapshotEntry entry = await context.Snapshots.ApplyStateAsync(context.Tweak.Id, kind, target, desired,
            context.Description, context.Tweak.RequiresRestart, context.CancellationToken).ConfigureAwait(false);
        return new[] { $"{entry.TargetDisplay}: {entry.OriginalState} → {desired}" };
    }

    private ActionState Summarize()
    {
        IReadOnlyDictionary<string, string> all = _dism.QueryFeatures();
        return ActionState.Info("Optional Windows Features", $"{all.Count(f => f.Value == "Enabled")} of {all.Count} enabled");
    }

    internal static ActionState StateOf(IStateRunner runner, string target, string expected)
    {
        string? live = runner.ReadState(target);
        return live is null
            ? new ActionState(ActionStatus.NotApplicable, runner.Describe(target), "(not present)", expected)
            : new ActionState(string.Equals(live, expected, StringComparison.OrdinalIgnoreCase) ? ActionStatus.Applied : ActionStatus.NotApplied,
                runner.Describe(target), live, expected);
    }

    private static ActionState Installed(bool installed, string target, bool wantInstalled) =>
        new(installed == wantInstalled ? ActionStatus.Applied : ActionStatus.NotApplied, target,
            installed ? "Installed" : "Not installed", wantInstalled ? "Installed" : "Not installed");

    private static IReadOnlyList<string> Log(CommandResult result, string success) =>
        result.Succeeded(3010)
            ? new[] { success }
            : throw new InvalidOperationException(result.Describe());

    private static string Op(TweakAction action) => (action.Operation ?? "feature").ToLowerInvariant();
}

/// <summary><c>schtask</c> actions: enable/disable maintenance tasks (snapshotted state). Missing tasks are skipped.</summary>
public sealed class ScheduledTaskActionHandler : ITweakActionHandler
{
    private readonly TaskSchedulerRunner _tasks;

    public ScheduledTaskActionHandler(TaskSchedulerRunner tasks) => _tasks = tasks;

    public IReadOnlyCollection<string> Types { get; } = new[] { TweakActionTypes.ScheduledTask };

    public Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context) =>
        Task.Run(() => (IReadOnlyList<ActionState>)new[]
        {
            DismActionHandler.StateOf(_tasks, action.TaskPath!, context.ResolveState(action)),
        });

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        string path = action.TaskPath!;
        if (await Task.Run(() => _tasks.ReadState(path)).ConfigureAwait(false) is null)
        {
            return new[] { $"Task {path}: not present, skipped." };
        }

        string desired = context.ResolveState(action);
        await context.Snapshots.ApplyStateAsync(context.Tweak.Id, _tasks.Kind, path, desired, context.Description,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        return new[] { $"Task {path} → {desired}" };
    }
}

/// <summary>
/// <c>device_manager</c> actions: disable user-selected unused devices (rated Safe/Moderate/Risky by
/// catalog rules) and "Restore All" which re-enables every device RigTune disabled.
/// </summary>
public sealed class DeviceManagerActionHandler : ITweakActionHandler
{
    private readonly DeviceManagerRunner _devices;

    public DeviceManagerActionHandler(DeviceManagerRunner devices) => _devices = devices;

    public IReadOnlyCollection<string> Types { get; } = new[] { TweakActionTypes.DeviceManager };

    public async Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context)
    {
        if (IsRestore(action))
        {
            int disabledByRigTune = (await context.Snapshots.GetActiveAsync(context.CancellationToken).ConfigureAwait(false))
                .OfType<StateSnapshotEntry>().Count(e => e.Kind == _devices.Kind);
            return new[] { ActionState.Info("Devices disabled by RigTune", disabledByRigTune.ToString()) };
        }

        IReadOnlyList<DeviceCandidate> candidates = await Task.Run(() => _devices.FindCandidates(action.Devices ?? new())).ConfigureAwait(false);
        return candidates.Select(c => new ActionState(
                c.Device.IsDisabled ? ActionStatus.Applied : ActionStatus.NotApplied,
                $"[{c.Rating}] {c.Device.Name}", c.Device.IsDisabled ? "Disabled" : "Enabled", "Disabled", c.Note))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        if (IsRestore(action))
        {
            var log = new List<string>();
            foreach (StateSnapshotEntry entry in (await context.Snapshots.GetActiveAsync(context.CancellationToken).ConfigureAwait(false))
                     .OfType<StateSnapshotEntry>().Where(e => e.Kind == _devices.Kind))
            {
                RevertResult r = await context.Snapshots.RevertAsync(entry.Id, context.CancellationToken).ConfigureAwait(false);
                log.Add($"{entry.TargetDisplay}: {(r.Success ? "re-enabled" : r.Message)}");
            }

            return log.Count == 0 ? new[] { "No devices were disabled by RigTune." } : log;
        }

        if (context.Parameters.Selections.Count == 0)
        {
            throw new TweakInputRequiredException("Select the devices to disable in the inspector first.");
        }

        var results = new List<string>();
        foreach (string instanceId in context.Parameters.Selections)
        {
            StateSnapshotEntry e = await context.Snapshots.ApplyStateAsync(context.Tweak.Id, _devices.Kind, instanceId,
                "Disabled", context.Description, cancellationToken: context.CancellationToken).ConfigureAwait(false);
            results.Add($"{e.TargetDisplay} disabled");
        }

        return results;
    }

    private static bool IsRestore(TweakAction action) =>
        string.Equals(action.Operation, "restore_all", StringComparison.OrdinalIgnoreCase);
}
