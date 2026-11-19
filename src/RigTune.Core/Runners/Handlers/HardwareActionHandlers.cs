using RigTune.Core.Hardware;
using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Services;

namespace RigTune.Core.Runners.Handlers;

/// <summary>
/// <c>pci_msi</c> and <c>interrupt_affinity</c> actions. Both act only on PCI devices the user selects
/// in the inspector; every registry write (MSISupported, DevicePolicy, AssignmentSetOverride,
/// DevicePriority) is snapshotted. Affinity masks are validated against the detected topology so
/// P-cores (0–15) and E-cores (16–27) are explicit.
/// </summary>
public sealed class PciActionHandler : ITweakActionHandler
{
    private readonly PciRunner _pci;
    private readonly Func<CpuTopology> _topology;

    public PciActionHandler(PciRunner pci, Func<CpuTopology>? topology = null)
    {
        _pci = pci;
        _topology = topology ?? HostProfileDetector.DetectTopology;
    }

    public IReadOnlyCollection<string> Types { get; } = new[] { TweakActionTypes.PciMsi, TweakActionTypes.InterruptAffinity };

    public Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context) => Task.Run(() =>
    {
        if (IsXhciImod(action))
        {
            IReadOnlyList<ActionState> xhci = _pci.EnumeratePciDevices(InterruptDeviceCategory.Usb)
                .Select(d => ActionState.Info(d.Device.Name, d.MsiSupported == true ? "MSI on" : "Line-based / default",
                    "Interrupter moderation (IMOD) lives in controller MMIO registers; RigTune does not write them."))
                .ToList();
            return xhci;
        }

        CpuTopology topology = _topology();
        IEnumerable<PciDeviceInfo> devices = action.Type == TweakActionTypes.PciMsi
            ? _pci.EnumeratePciDevices()
            : Categories(action).SelectMany(c => _pci.EnumeratePciDevices(c));
        return (IReadOnlyList<ActionState>)devices.Select(d => action.Type == TweakActionTypes.PciMsi
                ? ActionState.Info(d.Device.Name, d.MsiSupported switch { true => "MSI on", false => "MSI off", _ => "Driver default" })
                : ActionState.Info(d.Device.Name, DescribeAffinity(d, topology)))
            .ToList();
    });

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        if (IsXhciImod(action))
        {
            throw new NotSupportedException(
                "xHCI interrupt moderation is a controller MMIO register; changing it needs a kernel driver, which RigTune deliberately does not ship. " +
                "Use the MSI and interrupt-affinity tools for USB controllers instead.");
        }

        if (context.Parameters.Selections.Count == 0)
        {
            throw new TweakInputRequiredException("Select one or more devices in the inspector first.");
        }

        var log = new List<string>();
        foreach (string instanceId in context.Parameters.Selections)
        {
            if (action.Type == TweakActionTypes.PciMsi)
            {
                await context.Snapshots.ApplyRegistryAsync(context.Tweak.Id, PciRunner.MsiTarget(instanceId),
                    RegistryValueData.FromDWord(1), context.Description, true, context.CancellationToken).ConfigureAwait(false);
                log.Add($"{instanceId}: MSI mode enabled");
            }
            else
            {
                ulong mask = context.Parameters.AffinityMask
                             ?? throw new TweakInputRequiredException("Choose at least one CPU core for the interrupt.");
                ValidateMask(mask);
                await context.Snapshots.ApplyRegistryAsync(context.Tweak.Id, PciRunner.AffinityTarget(instanceId, "DevicePolicy"),
                    RegistryValueData.FromDWord(PciRunner.IrqPolicySpecifiedProcessors), context.Description, true,
                    context.CancellationToken).ConfigureAwait(false);
                await context.Snapshots.ApplyRegistryAsync(context.Tweak.Id, PciRunner.AffinityTarget(instanceId, "AssignmentSetOverride"),
                    PciRunner.AffinityMaskValue(mask), context.Description, true, context.CancellationToken).ConfigureAwait(false);
                log.Add($"{instanceId}: interrupts pinned to {DescribeMask(mask, _topology())}");
            }

            if (context.Parameters.DevicePriority is { } priority and >= 0 and <= 3)
            {
                await context.Snapshots.ApplyRegistryAsync(context.Tweak.Id, PciRunner.AffinityTarget(instanceId, "DevicePriority"),
                    RegistryValueData.FromDWord((uint)priority), context.Description, true, context.CancellationToken).ConfigureAwait(false);
                log.Add($"{instanceId}: device priority {priority}");
            }
        }

        return log;
    }

    public static IEnumerable<InterruptDeviceCategory> Categories(TweakAction action) =>
        (action.DeviceClass ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(PciRunner.ParseCategory)
        .Where(c => c is not null)
        .Select(c => c!.Value);

    /// <summary>"P-cores 0–3 · E-cores 16" style label for an affinity mask.</summary>
    public static string DescribeMask(ulong mask, CpuTopology topology)
    {
        var selected = Enumerable.Range(0, 64).Where(i => (mask & (1UL << i)) != 0).ToList();
        if (topology.PhysicalCoreCount == 0 || !topology.IsHybrid)
        {
            return $"CPUs {CpuTopology.FormatRanges(selected)}";
        }

        var p = selected.Where(topology.PerformanceLogicalProcessors.Contains).ToList();
        var e = selected.Where(topology.EfficientLogicalProcessors.Contains).ToList();
        var parts = new List<string>();
        if (p.Count > 0)
        {
            parts.Add($"P-cores {CpuTopology.FormatRanges(p)}");
        }

        if (e.Count > 0)
        {
            parts.Add($"E-cores {CpuTopology.FormatRanges(e)}");
        }

        return string.Join(" · ", parts);
    }

    private static string DescribeAffinity(PciDeviceInfo device, CpuTopology topology) =>
        device.DevicePolicy == PciRunner.IrqPolicySpecifiedProcessors && device.AssignmentSetOverride is { } mask
            ? DescribeMask(mask, topology)
            : "Windows default (all cores)";

    private void ValidateMask(ulong mask)
    {
        int logical = Math.Max(_topology().LogicalProcessorCount, Environment.ProcessorCount);
        ulong allowed = logical >= 64 ? ulong.MaxValue : (1UL << logical) - 1;
        if (mask == 0 || (mask & ~allowed) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mask), $"Affinity mask 0x{mask:X} is outside the {logical} logical processors.");
        }
    }

    private static bool IsXhciImod(TweakAction action) =>
        string.Equals(action.Operation, "xhci_imod", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// <c>tcp_profile</c> actions (handled by <see cref="NetworkAdapterRunner"/>): global TCP settings,
/// Nagle per TCP/IP interface, and per-adapter advanced properties (Interrupt Moderation, RSS).
/// </summary>
public sealed class NetworkActionHandler : ITweakActionHandler
{
    private readonly NetworkAdapterRunner _adapters;
    private readonly TcpGlobalStateRunner _tcp;
    private readonly IRegistryAccess _registry;

    public NetworkActionHandler(NetworkAdapterRunner adapters, TcpGlobalStateRunner tcp, IRegistryAccess registry)
    {
        _adapters = adapters;
        _tcp = tcp;
        _registry = registry;
    }

    public IReadOnlyCollection<string> Types { get; } = new[] { TweakActionTypes.TcpProfile };

    public Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context) => Task.Run(() =>
    {
        switch (Op(action))
        {
            case "global":
                return (IReadOnlyList<ActionState>)new[] { DismActionHandler.StateOf(_tcp, action.Setting!, context.ResolveState(action)) };
            case "nagle":
                return _adapters.EnumerateAdapters().Where(a => a.NetCfgInstanceId is not null).Select(a =>
                {
                    uint? ack = _registry.ReadValue(NetworkAdapterRunner.TcpInterfaceTarget(a.NetCfgInstanceId!, "TcpAckFrequency"))?.DWord;
                    uint? noDelay = _registry.ReadValue(NetworkAdapterRunner.TcpInterfaceTarget(a.NetCfgInstanceId!, "TCPNoDelay"))?.DWord;
                    bool applied = ack == 1 && noDelay == 1;
                    return new ActionState(applied ? ActionStatus.Applied : ActionStatus.NotApplied, a.Device.Name,
                        $"TcpAckFrequency={ack?.ToString() ?? "default"}, TCPNoDelay={noDelay?.ToString() ?? "default"}", "1 / 1");
                }).ToList();
            default:
                string keyword = action.Keyword!;
                return _adapters.EnumerateAdapters().Select(a =>
                {
                    string? value = keyword == NetworkAdapterRunner.RssKeyword ? a.Rss : a.InterruptModeration;
                    bool supported = keyword == NetworkAdapterRunner.RssKeyword ? a.SupportsRss : a.SupportsInterruptModeration;
                    return supported
                        ? ActionState.Info(a.Device.Name, value switch { "0" => "Disabled", "1" => "Enabled", null => "Driver default", _ => value })
                        : new ActionState(ActionStatus.NotApplicable, a.Device.Name, Note: $"Driver does not expose {keyword}.");
                }).ToList();
        }
    });

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        var log = new List<string>();
        switch (Op(action))
        {
            case "global":
            {
                string desired = context.ResolveState(action);
                StateSnapshotEntry e = await context.Snapshots.ApplyStateAsync(context.Tweak.Id, _tcp.Kind, action.Setting!,
                    desired, context.Description, cancellationToken: context.CancellationToken).ConfigureAwait(false);
                log.Add($"{e.TargetDisplay}: {e.OriginalState} → {desired}");
                break;
            }

            case "nagle":
                foreach (NetworkAdapterInfo a in await Task.Run(_adapters.EnumerateAdapters).ConfigureAwait(false))
                {
                    if (a.NetCfgInstanceId is null ||
                        (context.Parameters.Selections.Count > 0 && !context.Parameters.Selections.Contains(a.Device.InstanceId)))
                    {
                        continue;
                    }

                    foreach (string valueName in new[] { "TcpAckFrequency", "TCPNoDelay" })
                    {
                        await context.Snapshots.ApplyRegistryAsync(context.Tweak.Id,
                            NetworkAdapterRunner.TcpInterfaceTarget(a.NetCfgInstanceId, valueName), RegistryValueData.FromDWord(1),
                            context.Description, cancellationToken: context.CancellationToken).ConfigureAwait(false);
                    }

                    log.Add($"{a.Device.Name}: Nagle disabled (TcpAckFrequency=1, TCPNoDelay=1)");
                }

                break;

            default:
            {
                if (context.Parameters.Selections.Count == 0)
                {
                    throw new TweakInputRequiredException("Select an adapter in the inspector first.");
                }

                string value = context.ResolveState(action);
                foreach (NetworkAdapterInfo a in (await Task.Run(_adapters.EnumerateAdapters).ConfigureAwait(false))
                         .Where(a => context.Parameters.Selections.Contains(a.Device.InstanceId)))
                {
                    await context.Snapshots.ApplyRegistryAsync(context.Tweak.Id,
                        NetworkAdapterRunner.AdapterPropertyTarget(a, action.Keyword!), RegistryValueData.FromString(value),
                        context.Description, cancellationToken: context.CancellationToken).ConfigureAwait(false);
                    await Task.Run(() => _adapters.RestartAdapter(a)).ConfigureAwait(false);
                    log.Add($"{a.Device.Name}: {action.Keyword} = {value} (adapter restarted)");
                }

                break;
            }
        }

        return log;
    }

    private static string Op(TweakAction action) => (action.Operation ?? "global").ToLowerInvariant();
}

/// <summary><c>ntfs</c> actions: registry-backed NTFS behaviour (snapshotted) and ReTrim (permanent, harmless).</summary>
public sealed class NtfsActionHandler : ITweakActionHandler
{
    private readonly IRegistryAccess _registry;

    public NtfsActionHandler(IRegistryAccess registry) => _registry = registry;

    public IReadOnlyCollection<string> Types { get; } = new[] { TweakActionTypes.Ntfs };

    public Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context)
    {
        if (IsRetrim(action))
        {
            return Task.FromResult<IReadOnlyList<ActionState>>(new[] { ActionState.Info("SSD ReTrim", "Run on demand") });
        }

        RegistryTarget target = Target(action);
        RegistryValueData expected = Expected(action, context);
        RegistryValueData? live = _registry.ReadValue(target);
        return Task.FromResult<IReadOnlyList<ActionState>>(new[]
        {
            new ActionState(expected.Equals(live) ? ActionStatus.Applied : ActionStatus.NotApplied, target.Display,
                live?.ToDisplayString() ?? "(not set)", expected.ToDisplayString()),
        });
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        if (IsRetrim(action))
        {
            return await Task.Run(NtfsRunner.RetrimAllVolumes).ConfigureAwait(false);
        }

        RegistryTarget target = Target(action);
        RegistryValueData value = Expected(action, context);
        await context.Snapshots.ApplyRegistryAsync(context.Tweak.Id, target, value, context.Description,
            context.Tweak.RequiresRestart, context.CancellationToken).ConfigureAwait(false);
        return new[] { $"{target.Display} → {value.ToDisplayString()}" };
    }

    private static RegistryTarget Target(TweakAction action) =>
        NtfsRunner.SettingTarget(action.Setting ?? string.Empty)
        ?? throw new FormatException($"Unknown NTFS setting '{action.Setting}'.");

    private static RegistryValueData Expected(TweakAction action, ActionContext context) =>
        RegistryValueData.FromDWord(checked((uint)TweakValueParser.ToUInt64(
            context.ResolveValue(action) ?? throw new FormatException("ntfs action without applyValue."))));

    private static bool IsRetrim(TweakAction action) =>
        string.Equals(action.Setting, "retrim", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Read-only inspections (<c>wasapi_inspect</c>, <c>cpu_microcode_inspect</c>) and the app-scoped <c>timer_resolution</c>.</summary>
public sealed class InspectionActionHandler : ITweakActionHandler
{
    private readonly WasapiInspectService _wasapi;
    private readonly TimerResolutionService _timer;

    public InspectionActionHandler(WasapiInspectService wasapi, TimerResolutionService timer)
    {
        _wasapi = wasapi;
        _timer = timer;
    }

    public IReadOnlyCollection<string> Types { get; } = new[]
    {
        TweakActionTypes.WasapiInspect, TweakActionTypes.CpuMicrocodeInspect, TweakActionTypes.TimerResolution,
    };

    public async Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context)
    {
        switch (action.Type)
        {
            case TweakActionTypes.WasapiInspect:
                return (await _wasapi.InspectAsync().ConfigureAwait(false))
                    .Select(e => ActionState.Info($"{e.Flow}: {e.Name}{(e.IsDefault ? " (default)" : string.Empty)}",
                        $"{e.SampleRate / 1000.0:0.#} kHz · {e.BitsPerSample}-bit · {e.Channels} ch · period {e.DefaultPeriodMs:0.##} ms (min {e.MinimumPeriodMs:0.##} ms)"))
                    .ToList();
            case TweakActionTypes.CpuMicrocodeInspect:
                CpuInfo cpu = await Task.Run(HostProfileDetector.DetectCpu).ConfigureAwait(false);
                CpuTopology t = cpu.Topology;
                return new[]
                {
                    ActionState.Info("Intel microcode revision", cpu.MicrocodeDisplay,
                        "Read-only from HKLM\\HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0\\Update Revision. 0x12B or newer includes Intel's 13th/14th-gen Vmin stability fix."),
                    ActionState.Info("P-core threads", CpuTopology.FormatRanges(t.PerformanceLogicalProcessors), $"{t.PerformanceCoreCount} P-cores"),
                    ActionState.Info("E-core threads", CpuTopology.FormatRanges(t.EfficientLogicalProcessors), $"{t.EfficientCoreCount} E-cores"),
                };
            default:
                TimerResolutionInfo info = _timer.Query();
                return new[]
                {
                    ActionState.Info("System timer resolution", $"{info.CurrentMs:0.###} ms (finest {info.MinimumMs:0.###} ms)",
                        _timer.IsActive ? "RigTune's request is active for its own process." : "No RigTune request active."),
                };
        }
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        if (action.Type != TweakActionTypes.TimerResolution)
        {
            return (await GetStateAsync(action, context).ConfigureAwait(false))
                .Select(s => $"{s.Target}: {s.Current}").ToList();
        }

        TimerResolutionResult r = await _timer.RequestAsync(action.TargetMs ?? 0.5).ConfigureAwait(false);
        return new[]
        {
            $"Timer resolution {r.Before.CurrentMs:0.###} ms → {r.After.CurrentMs:0.###} ms (RigTune process only)",
            $"Measured Sleep(1): {r.Sleep1BeforeMs:0.###} ms → {r.Sleep1AfterMs:0.###} ms",
            "Released automatically on Reset or when RigTune exits.",
        };
    }
}

/// <summary><c>cleanup</c> actions: the 7 System Care cleanups, with a live reclaimable-size estimate as status.</summary>
public sealed class CleanupActionHandler : ITweakActionHandler
{
    private readonly Services.SystemCare.CleanupEngine _cleanup;

    public CleanupActionHandler(Services.SystemCare.CleanupEngine cleanup) => _cleanup = cleanup;

    public IReadOnlyCollection<string> Types { get; } = new[] { TweakActionTypes.Cleanup };

    public async Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context)
    {
        CleanupEstimate e = await _cleanup.EstimateAsync(action.Operation!, context.CancellationToken).ConfigureAwait(false);
        return new[]
        {
            ActionState.Info($"Cleanup: {action.Operation}", e.Note ?? $"{Services.SystemCare.CleanupEngine.FormatBytes(e.Bytes)} in {e.Files} files"),
        };
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        CleanupResult r = await _cleanup.CleanAsync(action.Operation!, context.CancellationToken).ConfigureAwait(false);
        return r.Success ? new[] { r.Message } : throw new InvalidOperationException(r.Message);
    }
}
