using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigTune.Core.Storage;

/// <summary>
/// Crash-safe JSON persistence. Writes go to a uniquely named temp file in the
/// same directory, are flushed to disk, and then atomically swapped over the
/// destination with <see cref="File.Replace(string, string, string?)"/> (keeping a
/// <c>.bak</c> last-known-good copy). A reader therefore only ever sees either
/// the complete old document or the complete new one — never a torn write.
/// </summary>
public static class AtomicJsonFile
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    public static JsonSerializerOptions CreateDefaultOptions() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string BackupPathFor(string path) => path + ".bak";

    public static async Task WriteAsync<T>(string path, T value, JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        SemaphoreSlim gate = PathLocks.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteCoreAsync(fullPath, value, options ?? DefaultOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task WriteCoreAsync<T>(string fullPath, T value, JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Path has no parent directory.", nameof(fullPath));
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 16 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, BackupPathFor(fullPath), ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, fullPath, overwrite: true);
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Reads a document. Missing file → <paramref name="createDefault"/>. Corrupt file →
    /// falls back to the <c>.bak</c> copy; if that is also unusable, the corrupt file is
    /// quarantined (renamed to <c>*.corrupt-*</c>, never deleted) and a default is returned.
    /// </summary>
    public static async Task<T> ReadOrDefaultAsync<T>(string path, Func<T> createDefault,
        JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        options ??= DefaultOptions;
        SemaphoreSlim gate = PathLocks.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(fullPath))
            {
                // A crash between Replace's internal steps can leave only the backup.
                T? fromBackup = await TryDeserializeAsync<T>(BackupPathFor(fullPath), options, cancellationToken)
                    .ConfigureAwait(false);
                return fromBackup ?? createDefault();
            }

            T? primary = await TryDeserializeAsync<T>(fullPath, options, cancellationToken).ConfigureAwait(false);
            if (primary is not null)
            {
                return primary;
            }

            T? backup = await TryDeserializeAsync<T>(BackupPathFor(fullPath), options, cancellationToken)
                .ConfigureAwait(false);
            Quarantine(fullPath);
            return backup ?? createDefault();
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<T?> TryDeserializeAsync<T>(string path, JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length == 0)
            {
                return default;
            }

            return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }

    private static void Quarantine(string fullPath)
    {
        try
        {
            string target = $"{fullPath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(fullPath, target);
        }
        catch (IOException)
        {
            // Best effort; the next successful write replaces it anyway.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
