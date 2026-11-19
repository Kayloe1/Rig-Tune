using RigTune.Core.Models;
using RigTune.Core.Platform;

namespace RigTune.Core.Services;

/// <summary>
/// Cooldown-free System Restore point creation and enumeration (SPEC §3.3.1).
/// Before creating a point, <c>SystemRestorePointCreationFrequency</c> is set to 0 through
/// <see cref="SnapshotService"/>, so its original value (or absence) is captured and revertable.
/// </summary>
public sealed class RestorePointService
{
    public const string CooldownTweakId = "system.restore.cooldown_bypass";

    public static readonly RegistryTarget CooldownTarget = new(
        RegistryHiveName.HKLM,
        RegistryViewName.Registry64,
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore",
        "SystemRestorePointCreationFrequency");

    private readonly IRestorePointBackend _backend;
    private readonly IRegistryAccess _registry;
    private readonly SnapshotService _snapshots;
    private readonly AuditLogService _audit;

    public RestorePointService(IRestorePointBackend backend, IRegistryAccess registry, SnapshotService snapshots,
        AuditLogService audit)
    {
        _backend = backend;
        _registry = registry;
        _snapshots = snapshots;
        _audit = audit;
    }

    /// <summary>Ensures the 24-hour creation cooldown is disabled (snapshotting the original first).</summary>
    public async Task EnsureCooldownBypassedAsync(CancellationToken cancellationToken = default)
    {
        RegistryValueData desired = RegistryValueData.FromDWord(0);
        RegistryValueData? current = _registry.ReadValue(CooldownTarget);
        if (desired.Equals(current))
        {
            return;
        }

        await _snapshots.ApplyRegistryAsync(CooldownTweakId, CooldownTarget, desired,
            "Bypass the 24-hour System Restore point creation cooldown", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RestorePointCreateResult> CreateAsync(string? description,
        RestorePointType type = RestorePointType.ModifySettings, CancellationToken cancellationToken = default)
    {
        string label = string.IsNullOrWhiteSpace(description)
            ? $"RigTune restore point {DateTime.Now:yyyy-MM-dd HH:mm}"
            : description.Trim();

        RestorePointCreateResult result;
        try
        {
            await EnsureCooldownBypassedAsync(cancellationToken).ConfigureAwait(false);
            result = await Task.Run(() => _backend.Create(label, type), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new RestorePointCreateResult(false, ex.Message, null, "error");
        }

        await _audit.LogAsync(
            result.Success ? AuditAction.RestorePointCreated : AuditAction.RestorePointFailed,
            result.Success ? $"Created restore point \"{label}\"" : $"Failed to create restore point \"{label}\"",
            target: "System Restore",
            details: $"{result.Message} (method: {result.Method}{(result.SequenceNumber is { } s ? $", seq {s}" : string.Empty)})",
            success: result.Success,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        return result;
    }

    public Task<IReadOnlyList<RestorePointInfo>> EnumerateAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => _backend.Enumerate(), cancellationToken);

    /// <summary>Launches the Windows System Restore wizard.</summary>
    public static void OpenSystemRestoreWizard() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("rstrui.exe") { UseShellExecute = true });
}
