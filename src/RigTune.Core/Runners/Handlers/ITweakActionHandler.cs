using System.Text.Json;
using RigTune.Core.Models;
using RigTune.Core.Services;

namespace RigTune.Core.Runners.Handlers;

/// <summary>Everything a handler needs while evaluating or applying one action.</summary>
public sealed class ActionContext
{
    public ActionContext(TweakDefinition tweak, TweakParameters parameters, SnapshotService snapshots,
        CancellationToken cancellationToken)
    {
        Tweak = tweak;
        Parameters = parameters;
        Snapshots = snapshots;
        CancellationToken = cancellationToken;
    }

    public TweakDefinition Tweak { get; }
    public TweakParameters Parameters { get; }
    public SnapshotService Snapshots { get; }
    public CancellationToken CancellationToken { get; }

    public TweakOption? SelectedOption =>
        Tweak.Options?.FirstOrDefault(o => o.Id == (Parameters.OptionId ?? Tweak.DefaultOption));

    /// <summary>The value to apply: option value → slider value → catalog <c>applyValue</c>.</summary>
    public JsonElement? ResolveValue(TweakAction action)
    {
        if (action.Key is { } key)
        {
            if (SelectedOption?.Values.TryGetValue(key, out JsonElement optionValue) == true)
            {
                return optionValue;
            }

            if (Parameters.Values.TryGetValue(key, out double slider))
            {
                return JsonSerializer.SerializeToElement((ulong)Math.Round(slider));
            }
        }

        return action.ApplyValue;
    }

    /// <summary>Numeric value for power-style actions (option → slider → <paramref name="fallback"/>).</summary>
    public uint? ResolveNumber(TweakAction action, uint? fallback)
    {
        if (action.Key is { } key)
        {
            if (SelectedOption?.Values.TryGetValue(key, out JsonElement optionValue) == true)
            {
                return TweakValueParser.IsDeleteMarker(optionValue)
                    ? fallback
                    : checked((uint)TweakValueParser.ToUInt64(optionValue));
            }

            if (Parameters.Values.TryGetValue(key, out double slider))
            {
                return (uint)Math.Round(Math.Max(0, slider));
            }
        }

        return fallback;
    }

    public string ResolveState(TweakAction action) =>
        Parameters.StateValue ?? action.ApplyState ?? (action.ApplyValue is { } v ? TweakValueParser.ToText(v) : string.Empty);

    public string Description => Tweak.Name;
}

/// <summary>Executes one or more SPEC §4.1 action types.</summary>
public interface ITweakActionHandler
{
    IReadOnlyCollection<string> Types { get; }

    /// <summary>Live state of the action's target(s) compared with the expected applied state.</summary>
    Task<IReadOnlyList<ActionState>> GetStateAsync(TweakAction action, ActionContext context);

    /// <summary>Applies the action (snapshotting first where the change is reversible). Returns log lines.</summary>
    Task<IReadOnlyList<string>> ApplyAsync(TweakAction action, ActionContext context);
}

/// <summary>Thrown when a tweak needs input (device selection, …) that was not provided.</summary>
public sealed class TweakInputRequiredException : InvalidOperationException
{
    public TweakInputRequiredException(string message) : base(message)
    {
    }
}
