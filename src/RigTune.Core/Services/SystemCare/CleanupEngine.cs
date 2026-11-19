using RigTune.Core.Models;

namespace RigTune.Core.Services.SystemCare;

/// <summary>
/// The 7 System Care cleanup actions (SPEC §5.3 / §6.F) with pre-scan size estimates.
/// File cleanups never follow junctions/symlinks, skip files in use, respect a minimum age, and refuse
/// to operate on protected roots (drive roots, Windows, Program Files, profile roots).
/// </summary>
public sealed class CleanupEngine
{
    public const string TempSystemFiles = "temp_system_files";
    public const string TempFiles = "temp_files";
    public const string Prefetch = "prefetch";
    public const string DnsCache = "dns_cache";
    public const string GpuShaderCache = "gpu_shader_cache";
    public const string Thumbnails = "thumbnails";
    public const string RamStandby = "ram_standby";

    private readonly AuditLogService _audit;
    private readonly ISystemMaintenanceNative _native;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, CleanupDefinition> _definitions;

    public CleanupEngine(AuditLogService audit, ISystemMaintenanceNative native)
        : this(audit, native, DefaultDefinitions())
    {
    }

    public CleanupEngine(AuditLogService audit, ISystemMaintenanceNative native, IEnumerable<CleanupDefinition> definitions,
        TimeProvider? time = null)
    {
        _audit = audit;
        _native = native;
        _time = time ?? TimeProvider.System;
        _definitions = definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CleanupDefinition> Definitions => _definitions.Values.ToList();

    public CleanupDefinition? Find(string id) => _definitions.GetValueOrDefault(id);

    public static IReadOnlyList<CleanupDefinition> DefaultDefinitions()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        TimeSpan day = TimeSpan.FromDays(1);
        return new[]
        {
            new CleanupDefinition(TempSystemFiles, "Clean Up Temporary System Files",
                "Old Windows Update downloads, Delivery Optimization cache, archived error reports and minidumps.", CleanupKind.Files,
                new[]
                {
                    new CleanupLocation(Path.Combine(windows, "SoftwareDistribution", "Download"), MinAge: TimeSpan.FromDays(2)),
                    new CleanupLocation(Path.Combine(windows, "SoftwareDistribution", "DeliveryOptimization"), MinAge: day),
                    new CleanupLocation(Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"), MinAge: day),
                    new CleanupLocation(Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive")),
                    new CleanupLocation(Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue")),
                    new CleanupLocation(Path.Combine(windows, "Minidump"), "*.dmp", Recursive: false),
                }, IsPermanent: true),
            new CleanupDefinition(TempFiles, "Delete Windows & User Temp Files",
                "Files older than 24 hours in your %TEMP% folder and C:\\Windows\\Temp (files in use are skipped).", CleanupKind.Files,
                new[]
                {
                    new CleanupLocation(Path.GetTempPath(), MinAge: day),
                    new CleanupLocation(Path.Combine(windows, "Temp"), MinAge: day),
                }, IsPermanent: true),
            new CleanupDefinition(Prefetch, "Clear Windows Prefetch Cache",
                "Prefetch trace files (*.pf). Windows rebuilds them; the first launch of each app is slightly slower.", CleanupKind.Files,
                new[] { new CleanupLocation(Path.Combine(windows, "Prefetch"), "*.pf", Recursive: false) }, IsPermanent: false),
            new CleanupDefinition(DnsCache, "Flush DNS Cache", "Clears cached DNS lookups (fixes stale or poisoned name resolution).",
                CleanupKind.DnsCache, Array.Empty<CleanupLocation>(), IsPermanent: false),
            new CleanupDefinition(GpuShaderCache, "Clear GPU Shader Cache",
                "NVIDIA DXCache/GLCache and the DirectX shader cache. Games recompile shaders on next launch (expect a one-time stutter).",
                CleanupKind.Files,
                new[]
                {
                    new CleanupLocation(Path.Combine(local, "NVIDIA", "DXCache")),
                    new CleanupLocation(Path.Combine(local, "NVIDIA", "GLCache")),
                    new CleanupLocation(Path.Combine(local, "NVIDIA Corporation", "NV_Cache")),
                    new CleanupLocation(Path.Combine(programData, "NVIDIA Corporation", "NV_Cache")),
                    new CleanupLocation(Path.Combine(local, "D3DSCache")),
                }, IsPermanent: false),
            new CleanupDefinition(Thumbnails, "Delete Thumbnail Cache",
                "Explorer thumbnail databases. Files Explorer has open are skipped (they clear after sign-out).", CleanupKind.Files,
                new[] { new CleanupLocation(Path.Combine(local, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db", Recursive: false) },
                IsPermanent: true),
            new CleanupDefinition(RamStandby, "Clean RAM Working Sets & Standby Cache",
                "Trims process working sets and purges the standby list. Useful before a long gaming session; Windows refills the cache over time.",
                CleanupKind.Memory, Array.Empty<CleanupLocation>(), IsPermanent: true),
        };
    }

    // ------------------------------------------------------------------ estimate

    public Task<CleanupEstimate> EstimateAsync(string id, CancellationToken cancellationToken = default) =>
        Task.Run(() => Estimate(id, cancellationToken), cancellationToken);

    public CleanupEstimate Estimate(string id, CancellationToken cancellationToken = default)
    {
        CleanupDefinition def = Find(id) ?? throw new KeyNotFoundException($"Unknown cleanup '{id}'.");
        switch (def.Kind)
        {
            case CleanupKind.DnsCache:
                return new CleanupEstimate(id, 0, 0, "Nothing on disk — instant");
            case CleanupKind.Memory:
                long? standby = _native.GetStandbyBytes();
                return new CleanupEstimate(id, standby ?? 0, 0, standby is null ? "Standby size unavailable" : "RAM in standby list");
        }

        long bytes = 0;
        int files = 0;
        foreach (FileInfo file in EnumerateCandidates(def, cancellationToken))
        {
            bytes += file.Length;
            files++;
        }

        return new CleanupEstimate(id, bytes, files);
    }

    // ------------------------------------------------------------------ clean

    public Task<CleanupResult> CleanAsync(string id, CancellationToken cancellationToken = default) =>
        Task.Run(() => CleanCoreAsync(id, cancellationToken), cancellationToken);

    private async Task<CleanupResult> CleanCoreAsync(string id, CancellationToken cancellationToken)
    {
        CleanupDefinition def = Find(id) ?? throw new KeyNotFoundException($"Unknown cleanup '{id}'.");
        CleanupResult result = def.Kind switch
        {
            CleanupKind.DnsCache => _native.FlushDnsCache()
                ? new CleanupResult(id, true, 0, 0, 0, "DNS resolver cache flushed.")
                : new CleanupResult(id, false, 0, 0, 0, "Could not flush the DNS cache."),
            CleanupKind.Memory => CleanMemory(id),
            _ => CleanFiles(def, cancellationToken),
        };

        await _audit.LogAsync(AuditAction.Cleaned, $"{def.Name}: {result.Message}", $"care.{id}",
            string.Join("; ", def.Locations.Select(l => l.Root)),
            $"Freed {result.BytesFreed:N0} bytes, {result.FilesDeleted} file(s) deleted, {result.FilesSkipped} skipped.",
            result.Success, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private CleanupResult CleanMemory(string id)
    {
        long before = _native.GetStandbyBytes() ?? 0;
        int trimmed = _native.EmptyWorkingSets();
        bool purged = _native.PurgeStandbyList();
        long after = _native.GetStandbyBytes() ?? before;
        long freed = Math.Max(0, before - after);
        return new CleanupResult(id, purged || trimmed > 0, freed, 0, 0,
            $"Trimmed {trimmed} working sets; standby list {(purged ? "purged" : "could not be purged")}.");
    }

    private CleanupResult CleanFiles(CleanupDefinition def, CancellationToken cancellationToken)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (FileInfo file in EnumerateCandidates(def, cancellationToken))
        {
            try
            {
                long length = file.Length;
                if (file.IsReadOnly)
                {
                    file.IsReadOnly = false;
                }

                file.Delete();
                freed += length;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++; // in use or protected
            }
        }

        foreach (CleanupLocation location in def.Locations.Where(l => l.Recursive && IsSafeRoot(l.Root) && Directory.Exists(l.Root)))
        {
            RemoveEmptySubdirectories(location.Root);
        }

        return new CleanupResult(def.Id, true, freed, deleted, skipped,
            $"Freed {FormatBytes(freed)} ({deleted} files{(skipped > 0 ? $", {skipped} in use skipped" : string.Empty)}).");
    }

    // ------------------------------------------------------------------ helpers

    private IEnumerable<FileInfo> EnumerateCandidates(CleanupDefinition def, CancellationToken cancellationToken)
    {
        DateTime now = _time.GetUtcNow().UtcDateTime;
        foreach (CleanupLocation location in def.Locations)
        {
            if (!IsSafeRoot(location.Root) || !Directory.Exists(location.Root))
            {
                continue;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = location.Recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint, // never follow junctions or symlinks
                ReturnSpecialDirectories = false,
            };

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(location.Root, location.Pattern, options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                    if (!info.Exists || (location.MinAge is { } minAge && now - info.LastWriteTimeUtc < minAge))
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                yield return info;
            }
        }
    }

    private static void RemoveEmptySubdirectories(string root)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(root, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Hard guard: never clean a drive root, Windows/System32, Program Files, Users or a profile root.</summary>
    public static bool IsSafeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (Path.GetPathRoot(full) is { } driveRoot &&
            string.Equals(Path.TrimEndingDirectorySeparator(driveRoot), full, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] forbidden =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? string.Empty,
        };

        return !forbidden.Where(f => !string.IsNullOrEmpty(f))
            .Any(f => string.Equals(Path.TrimEndingDirectorySeparator(f), full, StringComparison.OrdinalIgnoreCase));
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
