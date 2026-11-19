using System.Collections.Concurrent;
using System.Diagnostics;
using RigTune.Core.Models;
using RigTune.Core.Storage;

namespace RigTune.Core.Services.DiskAnalyzer;

/// <summary>
/// Multi-threaded drive scanner (SPEC §5.6): parallel traversal of the upper levels for NVMe throughput,
/// cancellation, live progress, inaccessible folders skipped (never fatal), junctions/symlinks never followed,
/// Games/Media/Apps/System/Other totals, and per-drive cached results (tree in memory, summary in disk_locks.json).
/// </summary>
public sealed class DiskAnalyzerService
{
    /// <summary>Files at least this big are listed individually in the tree; smaller ones are aggregated.</summary>
    public const long LargeFileThreshold = 4L * 1024 * 1024;

    private const int ParallelDepth = 3;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false, // we want to count skipped folders
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    private readonly JsonDocumentStore<DiskLocksDocument> _store;
    private readonly Func<CancellationToken, Task<DiskCategoryClassifier>> _classifier;
    private readonly ConcurrentDictionary<string, DriveScanResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="classifier">Builds the classifier per scan (e.g. with the latest discovered game folders).</param>
    public DiskAnalyzerService(JsonDocumentStore<DiskLocksDocument> store, Func<CancellationToken, Task<DiskCategoryClassifier>> classifier)
    {
        _store = store;
        _classifier = classifier;
    }

    public DriveScanResult? GetCached(string driveRoot) => _cache.GetValueOrDefault(Key(driveRoot));

    public async Task<DriveSummary?> GetCachedSummaryAsync(string driveRoot, CancellationToken cancellationToken = default) =>
        (await _store.ReadAsync(cancellationToken).ConfigureAwait(false)).CachedSummaries.GetValueOrDefault(Key(driveRoot));

    public async Task<DriveScanResult> ScanAsync(string driveRoot, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DiskCategoryClassifier classifier = await _classifier(cancellationToken).ConfigureAwait(false);
        var counters = new Counters();
        var categoryBytes = new long[Enum.GetValues<DiskCategory>().Length];
        var sw = Stopwatch.StartNew();
        using var reporter = new Timer(_ => progress?.Report(counters.Snapshot()), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));

        DiskNode root = await Task.Run(() =>
        {
            var info = new DirectoryInfo(driveRoot);
            DiskCategory rootCategory = classifier.ClassifyDirectory(info.FullName, DiskCategory.Other);
            return ScanDirectory(info, rootCategory, null, 0, classifier, counters, categoryBytes, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);

        progress?.Report(counters.Snapshot());
        var totals = Enum.GetValues<DiskCategory>().ToDictionary(c => c, c => Interlocked.Read(ref categoryBytes[(int)c]));
        var result = new DriveScanResult(driveRoot, root, DateTimeOffset.Now, sw.Elapsed, counters.Skipped, totals);
        _cache[Key(driveRoot)] = result;

        await _store.UpdateAsync(doc => doc.CachedSummaries[Key(driveRoot)] = new DriveSummary
        {
            Drive = Key(driveRoot),
            ScannedBytes = root.Size,
            Files = root.FileCount,
            ScannedAt = result.ScannedAt,
            CategoryBytes = totals,
        }, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private static DiskNode ScanDirectory(DirectoryInfo dir, DiskCategory category, DiskNode? parent, int depth,
        DiskCategoryClassifier classifier, Counters counters, long[] categoryBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var node = new DiskNode(dir.Name.Length == 0 ? dir.FullName : dir.Name, dir.FullName, true, category, parent);
        counters.Current = dir.FullName;
        Interlocked.Increment(ref counters.Directories);

        List<FileSystemInfo> entries;
        try
        {
            entries = dir.EnumerateFileSystemInfos("*", Options).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.IsInaccessible = true;
            Interlocked.Increment(ref counters.Skipped);
            return node;
        }

        var subdirs = new List<DirectoryInfo>();
        foreach (FileSystemInfo entry in entries)
        {
            if (entry is DirectoryInfo d)
            {
                subdirs.Add(d);
                continue;
            }

            if (entry is not FileInfo f)
            {
                continue;
            }

            long length;
            try
            {
                length = f.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            node.Size += length;
            node.FileCount++;
            Interlocked.Increment(ref counters.Files);
            Interlocked.Add(ref counters.Bytes, length);
            Interlocked.Add(ref categoryBytes[(int)classifier.ClassifyFile(f.Name, category)], length);
            if (length >= LargeFileThreshold)
            {
                node.Children.Add(new DiskNode(f.Name, f.FullName, false, classifier.ClassifyFile(f.Name, category), node)
                {
                    Size = length,
                    FileCount = 1,
                });
            }
            else
            {
                node.SmallFilesBytes += length;
                node.SmallFileCount++;
            }
        }

        var children = new ConcurrentBag<DiskNode>();
        void Visit(DirectoryInfo sub) => children.Add(ScanDirectory(sub, classifier.ClassifyDirectory(sub.FullName, category), node,
            depth + 1, classifier, counters, categoryBytes, cancellationToken));

        if (depth < ParallelDepth && subdirs.Count > 1)
        {
            Parallel.ForEach(subdirs, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
            }, Visit);
        }
        else
        {
            subdirs.ForEach(Visit);
        }

        foreach (DiskNode child in children)
        {
            node.Size += child.Size;
            node.FileCount += child.FileCount;
            node.Children.Add(child);
        }

        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        return node;
    }

    /// <summary>Removes a deleted item from the cached tree and subtracts its size from every ancestor.</summary>
    public bool RemoveFromCache(string path)
    {
        foreach (DriveScanResult result in _cache.Values)
        {
            DiskNode? node = Find(result.Root, path);
            if (node?.Parent is { } parent)
            {
                parent.Children.Remove(node);
                for (DiskNode? a = parent; a is not null; a = a.Parent)
                {
                    a.Size -= node.Size;
                    a.FileCount -= node.FileCount;
                }

                return true;
            }
        }

        return false;
    }

    public static DiskNode? Find(DiskNode root, string path)
    {
        if (!PathGuard.IsSameOrUnder(path, root.FullPath))
        {
            return null;
        }

        DiskNode current = root;
        while (!string.Equals(PathGuard.Normalize(current.FullPath), PathGuard.Normalize(path), StringComparison.OrdinalIgnoreCase))
        {
            DiskNode? next = current.Children.FirstOrDefault(c => PathGuard.IsSameOrUnder(path, c.FullPath));
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static string Key(string driveRoot) => PathGuard.Normalize(driveRoot);

    private sealed class Counters
    {
        public long Files;
        public long Directories;
        public long Bytes;
        public long Skipped;
        public volatile string Current = string.Empty;

        public ScanProgress Snapshot() =>
            new(Interlocked.Read(ref Files), Interlocked.Read(ref Directories), Interlocked.Read(ref Bytes), Interlocked.Read(ref Skipped), Current);
    }
}
