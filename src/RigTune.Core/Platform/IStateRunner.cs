namespace RigTune.Core.Platform;

/// <summary>
/// A target whose state can be captured as a string and written back exactly — Windows optional
/// features, scheduled tasks, devices, BCD entries, TCP globals, … <see cref="Services.SnapshotService"/>
/// snapshots these generically so every runner gets true rollback.
/// </summary>
public interface IStateRunner
{
    /// <summary>Unique kind key persisted in snapshots (e.g. "dism_feature", "schtask", "device").</summary>
    string Kind { get; }

    /// <summary>Current state, or null when the target does not exist on this PC.</summary>
    string? ReadState(string target);

    void WriteState(string target, string state);

    /// <summary>Human-readable target label.</summary>
    string Describe(string target) => $"{Kind}: {target}";
}
