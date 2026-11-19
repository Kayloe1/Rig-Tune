using RigTune.Core.Models;
using RigTune.Core.Storage;

namespace RigTune.Core.Services;

/// <summary>Append-only, timestamped audit log persisted to <c>history.json</c>.</summary>
public sealed class AuditLogService
{
    public const int DefaultMaxEntries = 10_000;

    private readonly JsonDocumentStore<HistoryDocument> _store;
    private readonly TimeProvider _time;
    private readonly int _maxEntries;

    public AuditLogService(RigTunePaths paths, TimeProvider? time = null, int maxEntries = DefaultMaxEntries)
        : this(new JsonDocumentStore<HistoryDocument>(paths.HistoryFile), time, maxEntries)
    {
    }

    public AuditLogService(JsonDocumentStore<HistoryDocument> store, TimeProvider? time = null,
        int maxEntries = DefaultMaxEntries)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
        _maxEntries = Math.Max(1, maxEntries);
    }

    /// <summary>Raised after an entry has been durably written.</summary>
    public event EventHandler<AuditEntry>? EntryAdded;

    public async Task<AuditEntry> LogAsync(AuditAction action, string summary, string? tweakId = null,
        string? target = null, string? details = null, bool success = true, bool requiresRestart = false,
        CancellationToken cancellationToken = default)
    {
        var entry = new AuditEntry
        {
            TimestampUtc = _time.GetUtcNow(),
            Action = action,
            Summary = summary,
            TweakId = tweakId,
            Target = target,
            Details = details,
            Success = success,
            RequiresRestart = requiresRestart,
        };

        await _store.UpdateAsync(doc =>
        {
            doc.Entries.Add(entry);
            int overflow = doc.Entries.Count - _maxEntries;
            if (overflow > 0)
            {
                doc.Entries.RemoveRange(0, overflow);
            }
        }, cancellationToken).ConfigureAwait(false);

        EntryAdded?.Invoke(this, entry);
        return entry;
    }

    /// <summary>Most recent entries first.</summary>
    public async Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int count = 50, CancellationToken cancellationToken = default)
    {
        HistoryDocument doc = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return doc.Entries.OrderByDescending(e => e.TimestampUtc).Take(Math.Max(0, count)).ToList();
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        HistoryDocument doc = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return doc.Entries.OrderBy(e => e.TimestampUtc).ToList();
    }
}
