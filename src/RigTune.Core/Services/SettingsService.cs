using RigTune.Core.Models;
using RigTune.Core.Storage;

namespace RigTune.Core.Services;

/// <summary>Loads and atomically persists <c>settings.json</c>.</summary>
public sealed class SettingsService
{
    private readonly JsonDocumentStore<AppSettings> _store;

    public SettingsService(RigTunePaths paths) : this(new JsonDocumentStore<AppSettings>(paths.SettingsFile))
    {
    }

    public SettingsService(JsonDocumentStore<AppSettings> store) => _store = store;

    public event EventHandler<AppSettings>? SettingsChanged;

    public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default) => _store.ReadAsync(cancellationToken);

    public async Task<AppSettings> UpdateAsync(Action<AppSettings> mutate, CancellationToken cancellationToken = default)
    {
        await _store.UpdateAsync(mutate, cancellationToken).ConfigureAwait(false);
        AppSettings current = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        SettingsChanged?.Invoke(this, current);
        return current;
    }
}
