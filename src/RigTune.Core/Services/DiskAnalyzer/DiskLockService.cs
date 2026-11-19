using RigTune.Core.Models;
using RigTune.Core.Storage;

namespace RigTune.Core.Services.DiskAnalyzer;

/// <summary>User "Mark as Important" locks, persisted in disk_locks.json.</summary>
public sealed class DiskLockService
{
    private readonly JsonDocumentStore<DiskLocksDocument> _store;
    private readonly AuditLogService _audit;

    public DiskLockService(JsonDocumentStore<DiskLocksDocument> store, AuditLogService audit)
    {
        _store = store;
        _audit = audit;
    }

    public event EventHandler? LocksChanged;

    public async Task<IReadOnlyList<string>> GetLocksAsync(CancellationToken cancellationToken = default) =>
        (await _store.ReadAsync(cancellationToken).ConfigureAwait(false)).LockedPaths;

    public async Task LockAsync(string path, CancellationToken cancellationToken = default)
    {
        string normalized = PathGuard.Normalize(path);
        bool added = await _store.UpdateAsync(doc =>
        {
            if (doc.LockedPaths.Any(p => string.Equals(PathGuard.Normalize(p), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            doc.LockedPaths.Add(normalized);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        if (added)
        {
            await _audit.LogAsync(AuditAction.Info, $"Marked as Important: {normalized}", "disk.lock", normalized,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            LocksChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task UnlockAsync(string path, CancellationToken cancellationToken = default)
    {
        string normalized = PathGuard.Normalize(path);
        int removed = await _store.UpdateAsync(doc =>
            doc.LockedPaths.RemoveAll(p => string.Equals(PathGuard.Normalize(p), normalized, StringComparison.OrdinalIgnoreCase)),
            cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            await _audit.LogAsync(AuditAction.Info, $"Unmarked Important: {normalized}", "disk.lock", normalized,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            LocksChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
