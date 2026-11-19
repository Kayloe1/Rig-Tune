using System.Diagnostics;
using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Runners.Handlers;
using RigTune.Core.Services.AppxWinget;
using RigTune.Core.Services.SystemCare;

namespace RigTune.Core.Services.AppOptimizer;

/// <summary>
/// <c>startup</c> app toggle: disables an app's auto-start the way Task Manager does — StartupApproved
/// flags for Run/Startup-folder entries matching the patterns, and the packaged StartupTask
/// <c>State</c> (1 = disabled by user) for MSIX apps. All writes are snapshotted.
/// </summary>
public sealed class AppStartupActionHandler : ITweakActionHandler
{
    public const string PackagedAppDataKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private readonly StartupManager _startup;
    private readonly IRegistryAccess _registry;

    public AppStartupActionHandler(StartupManager startup, IRegistryAccess registry)
    {
        _startup = startup;
        _registry = registry;
    }

    public IReadOnlyCollection<string> Types { get; } = new[] { AppToggleTypes.Startup };

    public Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context) => Task.Run(() =>
    {
        var states = Matching(action)
            .Select(e => new ActionState(e.IsEnabled ? ActionStatus.NotApplied : ActionStatus.Applied, $"Startup: {e.Name}",
                e.IsEnabled ? "Enabled" : "Disabled", "Disabled"))
            .Concat(PackagedTasks(action).Select(t =>
            {
                uint? state = _registry.ReadValue(t)?.DWord;
                bool disabled = state is 0 or 1 or 3;
                return new ActionState(disabled ? ActionStatus.Applied : ActionStatus.NotApplied, $"Startup task: {t.KeyPath.Split('\\')[^1]}",
                    disabled ? "Disabled" : "Enabled", "Disabled");
            }))
            .ToList();
        return (IReadOnlyList<ActionState>)(states.Count > 0
            ? states
            : new[] { new ActionState(ActionStatus.NotApplicable, "Auto-start", "No startup entry found") });
    });

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        var log = new List<string>();
        foreach (StartupEntry entry in Matching(action).Where(e => e.IsEnabled))
        {
            await _startup.SetEnabledAsync(entry, false, context.CancellationToken, context.Tweak.Id).ConfigureAwait(false);
            log.Add($"Auto-start disabled: {entry.Name}");
        }

        foreach (RegistryTarget task in PackagedTasks(action))
        {
            await context.Snapshots.ApplyRegistryAsync(context.Tweak.Id, task, RegistryValueData.FromDWord(1), context.Description,
                cancellationToken: context.CancellationToken).ConfigureAwait(false);
            log.Add($"Startup task disabled: {task.KeyPath.Split('\\')[^1]}");
        }

        return log.Count == 0 ? new[] { "Auto-start is already off (or the app has no startup entry)." } : log;
    }

    private IEnumerable<StartupEntry> Matching(TweakAction action)
    {
        List<string> patterns = action.Match ?? new List<string>();
        return patterns.Count == 0
            ? Enumerable.Empty<StartupEntry>()
            : _startup.Enumerate().Where(e => patterns.Any(p => AppxClassifier.IsMatch(e.Name, p) || AppxClassifier.IsMatch(e.Command, p)));
    }

    private IEnumerable<RegistryTarget> PackagedTasks(TweakAction action)
    {
        if (string.IsNullOrEmpty(action.PackageFamily))
        {
            yield break;
        }

        string familyKey = $@"{PackagedAppDataKey}\{action.PackageFamily}";
        foreach (string task in _registry.GetSubKeyNames(RegistryHiveName.HKCU, RegistryViewName.Registry64, familyKey))
        {
            var target = new RegistryTarget(RegistryHiveName.HKCU, RegistryViewName.Registry64, $@"{familyKey}\{task}", "State");
            if (_registry.ReadValue(target) is { Kind: Microsoft.Win32.RegistryValueKind.DWord })
            {
                yield return target;
            }
        }
    }
}

/// <summary><c>chromium_flag</c> app toggle (e.g. parallel downloading), snapshotted as generic state.</summary>
public sealed class ChromiumFlagActionHandler : ITweakActionHandler
{
    private readonly ChromiumFlagStateRunner _runner;
    private readonly Func<string, string?>? _environment;

    public ChromiumFlagActionHandler(ChromiumFlagStateRunner runner, Func<string, string?>? environment = null)
    {
        _runner = runner;
        _environment = environment;
    }

    public IReadOnlyCollection<string> Types { get; } = new[] { AppToggleTypes.ChromiumFlag };

    public Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context)
    {
        string? target = Target(action);
        string expected = context.ResolveState(action);
        return Task.FromResult<IReadOnlyList<ActionState>>(new[]
        {
            target is null
                ? new ActionState(ActionStatus.NotApplicable, $"Browser flag {action.Flag}", "Browser profile not found")
                : DismActionHandler.StateOf(_runner, target, expected),
        });
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        string target = Target(action) ?? throw new InvalidOperationException("The browser's Local State file was not found.");
        if (action.ProcessName is { } process &&
            Process.GetProcessesByName(Path.GetFileNameWithoutExtension(process)).Length > 0)
        {
            throw new InvalidOperationException($"Close {process} first — the browser rewrites its Local State on exit.");
        }

        string desired = context.ResolveState(action);
        StateSnapshotEntry entry = await context.Snapshots.ApplyStateAsync(context.Tweak.Id, _runner.Kind, target, desired,
            context.Description, cancellationToken: context.CancellationToken).ConfigureAwait(false);
        return new[] { $"{entry.TargetDisplay}: {entry.OriginalState} → {desired}" };
    }

    private string? Target(TweakAction action)
    {
        string? path = action.LocalStatePath is null ? null : AppPathExpander.ExpandFile(action.LocalStatePath, _environment);
        return path is not null && File.Exists(path) && action.Flag is not null ? ChromiumFlagStateRunner.Target(path, action.Flag) : null;
    }
}
