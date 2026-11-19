using System.Globalization;
using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Services;

namespace RigTune.Core.Runners.Handlers;

/// <summary><c>service</c> actions: start type (+ optional stop). Services missing on this PC are skipped.</summary>
public sealed class ServiceActionHandler : ITweakActionHandler
{
    private readonly IServiceAccess _services;

    public ServiceActionHandler(IServiceAccess services) => _services = services;

    public IReadOnlyCollection<string> Types { get; } = new[] { TweakActionTypes.Service };

    public Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context)
    {
        string name = action.ServiceName ?? throw new FormatException("Service action without serviceName.");
        ServiceStartType expected = ParseStartType(action.StartType);
        ServiceState? state = _services.Query(name);
        ActionState result = state is null
            ? new ActionState(ActionStatus.NotApplicable, $"Service: {name}", "(not installed)", expected.ToString())
            : new ActionState(state.StartType == expected ? ActionStatus.Applied : ActionStatus.NotApplied,
                $"Service: {name}", $"{state.StartType}{(state.IsRunning ? ", running" : ", stopped")}", expected.ToString());
        return Task.FromResult<IReadOnlyList<ActionState>>(new[] { result });
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        string name = action.ServiceName!;
        if (_services.Query(name) is null)
        {
            return new[] { $"Service {name}: not installed, skipped." };
        }

        ServiceStartType startType = ParseStartType(action.StartType);
        await context.Snapshots.ApplyServiceAsync(context.Tweak.Id, name, startType, action.StopService,
            context.Description, context.Tweak.RequiresRestart, context.CancellationToken).ConfigureAwait(false);
        return new[] { $"Service {name} → {startType}{(action.StopService ? " (stopped)" : string.Empty)}" };
    }

    public static ServiceStartType ParseStartType(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out ServiceStartType t)
            ? t
            : throw new FormatException($"Unknown service start type '{value}'.");
}

/// <summary>
/// <c>power</c> actions on the active scheme: AC/DC value indices (with option/slider overrides) and
/// the "unhide" operation that exposes hidden settings (Attributes = 2), both fully snapshotted.
/// </summary>
public sealed class PowerActionHandler : ITweakActionHandler
{
    private const uint AttributeShown = 2;
    private readonly IPowerAccess _power;
    private readonly IRegistryAccess _registry;

    public PowerActionHandler(IPowerAccess power, IRegistryAccess registry)
    {
        _power = power;
        _registry = registry;
    }

    public IReadOnlyCollection<string> Types { get; } = new[] { TweakActionTypes.Power };

    public Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context)
    {
        (Guid sub, Guid setting) = Parse(action);
        if (IsUnhide(action))
        {
            RegistryTarget target = PowerRunner.AttributesTarget(sub, setting);
            RegistryValueData? live = _registry.ReadValue(target);
            bool shown = live?.DWord is { } a && (a & 1) == 0;
            return Result(new ActionState(shown ? ActionStatus.Applied : ActionStatus.NotApplied,
                $"Power setting {setting} visibility", shown ? "Shown" : "Hidden", "Shown"));
        }

        try
        {
            Guid scheme = _power.GetActiveScheme();
            uint? expectedAc = context.ResolveNumber(action, action.AcValue);
            uint? expectedDc = action.DcValue is null ? null : context.ResolveNumber(action, action.DcValue);
            uint ac = _power.ReadAcValue(scheme, sub, setting);
            uint dc = _power.ReadDcValue(scheme, sub, setting);
            bool applied = (expectedAc is null || ac == expectedAc) && (expectedDc is null || dc == expectedDc);
            return Result(new ActionState(applied ? ActionStatus.Applied : ActionStatus.NotApplied,
                $"Power {sub}/{setting}", $"AC={ac}, DC={dc}",
                $"AC={expectedAc?.ToString(CultureInfo.InvariantCulture) ?? "-"}, DC={expectedDc?.ToString(CultureInfo.InvariantCulture) ?? "-"}"));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or KeyNotFoundException)
        {
            return Result(new ActionState(ActionStatus.NotApplicable, $"Power {sub}/{setting}", Note: ex.Message));
        }
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        (Guid sub, Guid setting) = Parse(action);
        if (IsUnhide(action))
        {
            await context.Snapshots.ApplyRegistryAsync(context.Tweak.Id, PowerRunner.AttributesTarget(sub, setting),
                RegistryValueData.FromDWord(AttributeShown), context.Description,
                cancellationToken: context.CancellationToken).ConfigureAwait(false);
            return new[] { $"Power setting {setting} is now visible in Power Options." };
        }

        uint? ac = context.ResolveNumber(action, action.AcValue);
        uint? dc = action.DcValue is null ? null : context.ResolveNumber(action, action.DcValue);
        if (action.Range is { } range)
        {
            ac = ac is { } a ? (uint)Math.Clamp(a, range.Min, range.Max) : null;
            dc = dc is { } d ? (uint)Math.Clamp(d, range.Min, range.Max) : null;
        }

        await context.Snapshots.ApplyPowerAsync(context.Tweak.Id, sub, setting, ac, dc, null, context.Description,
            context.Tweak.RequiresRestart, context.CancellationToken).ConfigureAwait(false);
        return new[] { $"Power {setting}: AC={ac?.ToString(CultureInfo.InvariantCulture) ?? "unchanged"}, DC={dc?.ToString(CultureInfo.InvariantCulture) ?? "unchanged"}" };
    }

    private static bool IsUnhide(TweakAction action) =>
        string.Equals(action.Operation, "unhide", StringComparison.OrdinalIgnoreCase);

    private static (Guid Sub, Guid Setting) Parse(TweakAction action) =>
        (Guid.Parse(action.Subgroup ?? throw new FormatException("Power action without subgroup.")),
            Guid.Parse(action.Setting ?? throw new FormatException("Power action without setting.")));

    private static Task<IReadOnlyList<ActionState>> Result(params ActionState[] s) =>
        Task.FromResult<IReadOnlyList<ActionState>>(s);
}

/// <summary><c>nvapi_drs</c> actions on the NVIDIA global (base) profile, snapshotted before any write.</summary>
public sealed class NvApiActionHandler : ITweakActionHandler
{
    private readonly INvApiDrsAccess _nvapi;

    public NvApiActionHandler(INvApiDrsAccess nvapi) => _nvapi = nvapi;

    public IReadOnlyCollection<string> Types { get; } = new[] { TweakActionTypes.NvApiDrs };

    public Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context)
    {
        uint id = ParseId(action);
        string target = $"NVIDIA DRS 0x{id:X8}";
        if (!_nvapi.IsAvailable)
        {
            return Result(new ActionState(ActionStatus.NotApplicable, target, Note: "NVIDIA driver / NVAPI not available."));
        }

        NvDrsValue live = _nvapi.ReadGlobal(id);
        string current = live.ExistsInBaseProfile ? $"0x{live.Value:X8}" : "(driver default)";
        if (string.Equals(action.Operation, "inspect", StringComparison.OrdinalIgnoreCase))
        {
            return Result(ActionState.Info(target, current));
        }

        uint? expected = Expected(action, context);
        bool applied = expected is null ? !live.ExistsInBaseProfile : live.ExistsInBaseProfile && live.Value == expected;
        return Result(new ActionState(applied ? ActionStatus.Applied : ActionStatus.NotApplied, target, current,
            expected is { } e ? $"0x{e:X8}" : "(driver default)"));
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        uint id = ParseId(action);
        if (string.Equals(action.Operation, "inspect", StringComparison.OrdinalIgnoreCase))
        {
            NvDrsValue live = _nvapi.IsAvailable ? _nvapi.ReadGlobal(id) : default;
            return new[] { $"NVIDIA DRS 0x{id:X8} = {(live.ExistsInBaseProfile ? $"0x{live.Value:X8}" : "(driver default)")}" };
        }

        uint? value = Expected(action, context);
        await context.Snapshots.ApplyNvApiDrsAsync(context.Tweak.Id, id, value, context.Description,
            context.Tweak.RequiresRestart, context.CancellationToken).ConfigureAwait(false);
        return new[] { $"NVIDIA DRS 0x{id:X8} → {(value is { } v ? $"0x{v:X8}" : "driver default")}" };
    }

    private static uint? Expected(TweakAction action, ActionContext context) =>
        context.ResolveValue(action) is { } json && !TweakValueParser.IsDeleteMarker(json)
            ? checked((uint)TweakValueParser.ToUInt64(json))
            : null;

    public static uint ParseId(TweakAction action) =>
        checked((uint)TweakValueParser.ParseUInt64(action.SettingId ?? throw new FormatException("nvapi_drs action without settingId.")));

    private static Task<IReadOnlyList<ActionState>> Result(params ActionState[] s) =>
        Task.FromResult<IReadOnlyList<ActionState>>(s);
}
