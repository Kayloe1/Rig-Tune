using System.Text.Json;

namespace RigTune.Core.Storage;

/// <summary>
/// Serialized, cached access to one JSON document on disk. All mutations run under a
/// single async lock and are persisted atomically before the call returns.
/// Reads return deep copies so callers can never mutate cached state outside the lock.
/// </summary>
public sealed class JsonDocumentStore<T> where T : class, new()
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options;
    private T? _cache;

    public JsonDocumentStore(string path, JsonSerializerOptions? options = null)
    {
        FilePath = Path.GetFullPath(path);
        _options = options ?? AtomicJsonFile.DefaultOptions;
    }

    public string FilePath { get; }

    public async Task<T> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            T doc = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return Clone(doc);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies <paramref name="mutate"/> to the document and persists it atomically.</summary>
    public async Task<TResult> UpdateAsync<TResult>(Func<T, TResult> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            T working = Clone(await LoadCoreAsync(cancellationToken).ConfigureAwait(false));
            TResult result = mutate(working);
            await AtomicJsonFile.WriteAsync(FilePath, working, _options, cancellationToken).ConfigureAwait(false);
            _cache = working; // only commit to cache after the disk write succeeded
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task UpdateAsync(Action<T> mutate, CancellationToken cancellationToken = default) =>
        UpdateAsync<bool>(doc =>
        {
            mutate(doc);
            return true;
        }, cancellationToken);

    /// <summary>Drops the in-memory cache so the next access re-reads the file.</summary>
    public void Invalidate()
    {
        _gate.Wait();
        try
        {
            _cache = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> LoadCoreAsync(CancellationToken cancellationToken) =>
        _cache ??= await AtomicJsonFile.ReadOrDefaultAsync(FilePath, static () => new T(), _options, cancellationToken)
            .ConfigureAwait(false);

    private T Clone(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, _options), _options) ?? new T();
}
