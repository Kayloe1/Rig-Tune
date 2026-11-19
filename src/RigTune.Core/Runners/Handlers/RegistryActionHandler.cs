using System.Globalization;
using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Services;

namespace RigTune.Core.Runners.Handlers;

/// <summary>
/// <c>registry</c> actions: set / delete / clearBits / setBits / inspect, on either registry view.
/// Bit operations (e.g. Accessibility StickyKeys <c>Flags</c>) compute the new value from the live
/// value, keeping the original kind (REG_SZ numbers stay REG_SZ).
/// </summary>
public sealed class RegistryActionHandler : ITweakActionHandler
{
    public const string NvidiaDisplayKeyToken = "{NVIDIA_DISPLAY_KEY}";
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private readonly IRegistryAccess _registry;

    public RegistryActionHandler(IRegistryAccess registry) => _registry = registry;

    public IReadOnlyCollection<string> Types { get; } = new[] { TweakActionTypes.Registry };

    public Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context)
    {
        RegistryTarget? target = ResolveTarget(action);
        if (target is null)
        {
            return Result(new ActionState(ActionStatus.NotApplicable, action.Path ?? "?", Note: "Hardware key not found on this PC."));
        }

        RegistryValueData? live = _registry.ReadValue(target);
        string op = (action.Operation ?? "set").ToLowerInvariant();
        if (op == "inspect")
        {
            return Result(ActionState.Info(target.Display, live?.ToDisplayString() ?? "(not set)"));
        }

        if (op is "clearbits" or "setbits")
        {
            if (live is null || ToNumber(live) is not { } number)
            {
                return Result(new ActionState(ActionStatus.NotApplicable, target.Display, "(not set)",
                    Note: "Value not present; Windows default applies."));
            }

            uint mask = action.BitMask ?? 0;
            bool applied = op == "clearbits" ? (number & mask) == 0 : (number & mask) == mask;
            return Result(new ActionState(applied ? ActionStatus.Applied : ActionStatus.NotApplied, target.Display,
                live.ToDisplayString(), $"{(op == "clearbits" ? "bits cleared" : "bits set")}: 0x{mask:X}"));
        }

        RegistryValueData? expected = Expected(action, context);
        bool matches = expected is null ? live is null : expected.Equals(live);
        return Result(new ActionState(matches ? ActionStatus.Applied : ActionStatus.NotApplied, target.Display,
            live?.ToDisplayString() ?? "(not set)", expected?.ToDisplayString() ?? "(deleted)"));
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context)
    {
        RegistryTarget target = ResolveTarget(action)
                                ?? throw new InvalidOperationException($"Registry key {action.Path} was not found on this PC.");
        string op = (action.Operation ?? "set").ToLowerInvariant();
        RegistryValueData? value;
        switch (op)
        {
            case "inspect":
                return new[] { $"{target.Display} = {_registry.ReadValue(target)?.ToDisplayString() ?? "(not set)"}" };

            case "clearbits":
            case "setbits":
                RegistryValueData? live = _registry.ReadValue(target);
                if (live is null || ToNumber(live) is not { } number)
                {
                    return new[] { $"{target.Display}: not present, nothing to change." };
                }

                uint mask = action.BitMask ?? 0;
                uint updated = op == "clearbits" ? number & ~mask : number | mask;
                if (updated == number)
                {
                    return new[] { $"{target.Display}: already {live.ToDisplayString()}." };
                }

                value = FromNumberKeepingKind(live, updated);
                break;

            default:
                value = Expected(action, context);
                break;
        }

        await context.Snapshots.ApplyRegistryAsync(context.Tweak.Id, target, value, context.Description,
            context.Tweak.RequiresRestart, context.CancellationToken).ConfigureAwait(false);
        return new[] { $"{target.Display} → {value?.ToDisplayString() ?? "(deleted)"}" };
    }

    public RegistryTarget? ResolveTarget(TweakAction action)
    {
        string path = action.Path ?? throw new FormatException("Registry action without a path.");
        if (path.Contains(NvidiaDisplayKeyToken, StringComparison.OrdinalIgnoreCase))
        {
            string? key = FindNvidiaDisplayKey();
            if (key is null)
            {
                return null;
            }

            path = path.Replace(NvidiaDisplayKeyToken, key, StringComparison.OrdinalIgnoreCase);
        }

        return new RegistryTarget(TweakValueParser.ParseHive(action.Hive), TweakValueParser.ParseView(action.View),
            path.Trim('\\'), action.ValueName ?? string.Empty);
    }

    private static RegistryValueData? Expected(TweakAction action, ActionContext context)
    {
        if (string.Equals(action.Operation, "delete", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (context.ResolveValue(action) is not { } json || TweakValueParser.IsDeleteMarker(json))
        {
            return null;
        }

        return TweakValueParser.ToRegistryValue(json, TweakValueParser.ParseKind(action.ValueKind));
    }

    private string? FindNvidiaDisplayKey()
    {
        for (int i = 0; i < 16; i++)
        {
            string key = $@"{DisplayClassKey}\{i:D4}";
            RegistryValueData? provider = _registry.ReadValue(new RegistryTarget(RegistryHiveName.HKLM,
                RegistryViewName.Registry64, key, "ProviderName"));
            if (provider?.String?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true)
            {
                return key;
            }
        }

        return null;
    }

    private static uint? ToNumber(RegistryValueData data) => data.Kind switch
    {
        RegistryValueKind.DWord => data.DWord,
        RegistryValueKind.String or RegistryValueKind.ExpandString =>
            uint.TryParse(data.String, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint n) ? n : null,
        _ => null,
    };

    private static RegistryValueData FromNumberKeepingKind(RegistryValueData original, uint value) => original.Kind switch
    {
        RegistryValueKind.DWord => RegistryValueData.FromDWord(value),
        RegistryValueKind.ExpandString => RegistryValueData.FromExpandString(value.ToString(CultureInfo.InvariantCulture)),
        _ => RegistryValueData.FromString(value.ToString(CultureInfo.InvariantCulture)),
    };

    private static Task<IReadOnlyList<ActionState>> Result(params ActionState[] states) =>
        Task.FromResult<IReadOnlyList<ActionState>>(states);
}
