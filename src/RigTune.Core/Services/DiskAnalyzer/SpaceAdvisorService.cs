using System.Diagnostics;
using RigTune.Core.Models;
using RigTune.Core.Services.AppOptimizer;
using RigTune.Core.Services.GameMode;
using RigTune.Core.Services.SystemCare;

namespace RigTune.Core.Services.DiskAnalyzer;

/// <summary>Folders the advisor inspects (overridable for tests).</summary>
public sealed record AdvisorEnvironment(
    string LocalAppData,
    string Temp,
    string WindowsDir,
    string Downloads,
    string SystemDrive,
    string? SteamRoot)
{
    public static AdvisorEnvironment ForThisMachine(string? steamRoot)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new AdvisorEnvironment(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
            windows,
            Path.Combine(profile, "Downloads"),
            Path.GetPathRoot(windows) ?? @"C:\",
            steamRoot);
    }
}

/// <summary>
/// Deterministic, offline Space Advisor (SPEC §5.6 — no LLM). Groups findings into Safe to Remove,
/// Check First and Keep with sizes and plain-English reasons; every action routes through the guarded
/// Recycle Bin path, System Care cleanups (for Windows-owned temp zones), Steam's own uninstaller or
/// Disk Cleanup.
/// </summary>
public sealed class SpaceAdvisorService
{
    public const long LargeDownloadBytes = 200L * 1024 * 1024;
    public const long InstallerBytes = 50L * 1024 * 1024;
    public const long LargeGameBytes = 5L * 1024 * 1024 * 1024;
    public static readonly TimeSpan OldDownloadAge = TimeSpan.FromDays(30);
    private static readonly string[] InstallerExtensions = { ".iso", ".zip", ".exe", ".msi", ".7z", ".rar" };

    private readonly AdvisorEnvironment _env;
    private readonly AppOptimizerService? _apps;
    private readonly DiskLockService _locks;
    private readonly DiskDeletionService _deletion;
    private readonly IRecycleBin _recycleBin;
    private readonly CleanupEngine? _cleanup;
    private readonly DiskAnalyzerService? _analyzer;
    private readonly AuditLogService _audit;
    private readonly TimeProvider _time;

    public SpaceAdvisorService(AdvisorEnvironment env, DiskLockService locks, DiskDeletionService deletion, IRecycleBin recycleBin,
        AuditLogService audit, AppOptimizerService? apps = null, CleanupEngine? cleanup = null, DiskAnalyzerService? analyzer = null,
        TimeProvider? time = null)
    {
        _env = env;
        _locks = locks;
        _deletion = deletion;
        _recycleBin = recycleBin;
        _audit = audit;
        _apps = apps;
        _cleanup = cleanup;
        _analyzer = analyzer;
        _time = time ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<SpaceFinding>> AnalyzeAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        IReadOnlyList<string> locks = await _locks.GetLocksAsync(cancellationToken).ConfigureAwait(false);
        var findings = new List<SpaceFinding>();
        findings.AddRange(SafeZones());
        findings.AddRange(DownloadFindings());
        findings.AddRange(GameFindings(locks));
        findings.AddRange(KeepZones(locks));

        // Anything inside an Important folder moves to Keep with no action.
        return (IReadOnlyList<SpaceFinding>)findings
            .Select(f => f.Group != AdvisorGroup.Keep && f.AllPaths.Any(p => PathGuard.IsLocked(p, locks))
                ? f with { Group = AdvisorGroup.Keep, Reason = "Inside a folder you marked as Important.", Action = AdvisorAction.None }
                : f)
            .Where(f => f.Bytes > 0 || f.Group == AdvisorGroup.Keep)
            .OrderBy(f => f.Group)
            .ThenByDescending(f => f.Bytes)
            .ToList();
    }, cancellationToken);

    // ------------------------------------------------------------------ Safe to Remove

    private IEnumerable<SpaceFinding> SafeZones()
    {
        if (_apps is not null)
        {
            foreach (AppProfile app in _apps.Apps.Where(a => a.Category == AppCategories.Browsers))
            {
                List<string> dirs = app.Caches.Where(c => c.DefaultChecked && !c.IsSensitive)
                    .SelectMany(c => c.Paths).SelectMany(p => AppPathExpander.Expand(p)).Where(Directory.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (dirs.Count > 0)
                {
                    yield return Zone(AdvisorGroup.SafeToRemove, $"{app.Name} cache", dirs,
                        "Cached web content; the browser rebuilds it. Cookies and history are NOT included.", AdvisorAction.RecycleContents);
                }
            }
        }

        yield return Zone(AdvisorGroup.SafeToRemove, "GPU shader caches",
            new[] { Path.Combine(_env.LocalAppData, "NVIDIA", "DXCache"), Path.Combine(_env.LocalAppData, "NVIDIA", "GLCache"), Path.Combine(_env.LocalAppData, "D3DSCache") },
            "Compiled shaders; games rebuild them on next launch (expect a one-time stutter).", AdvisorAction.RecycleContents);
        yield return Zone(AdvisorGroup.SafeToRemove, "Your temp folder (%TEMP%)", new[] { _env.Temp },
            "Temporary files left by installers and apps. Files in use are skipped.", AdvisorAction.RecycleContents);
        yield return Zone(AdvisorGroup.SafeToRemove, "Windows temp & Windows Update downloads",
            new[] { Path.Combine(_env.WindowsDir, "Temp"), Path.Combine(_env.WindowsDir, "SoftwareDistribution", "Download") },
            "Leftovers Windows no longer needs. Cleaned by System Care (Windows-owned folders are never deleted from the tree view).",
            AdvisorAction.RunCleanup, CleanupEngine.TempSystemFiles);
        yield return Zone(AdvisorGroup.SafeToRemove, "Crash dumps",
            new[] { Path.Combine(_env.LocalAppData, "CrashDumps") },
            "Memory dumps from crashed apps, only useful for debugging.", AdvisorAction.RecycleContents);
        yield return Zone(AdvisorGroup.SafeToRemove, "Windows minidumps", new[] { Path.Combine(_env.WindowsDir, "Minidump") },
            "Blue-screen minidumps. Cleaned by System Care.", AdvisorAction.RunCleanup, CleanupEngine.TempSystemFiles);

        string nvidiaExtract = Path.Combine(_env.SystemDrive, "NVIDIA");
        if (Directory.Exists(nvidiaExtract))
        {
            yield return Zone(AdvisorGroup.SafeToRemove, @"Old NVIDIA installer files (C:\NVIDIA)", new[] { nvidiaExtract },
                "Extracted driver installers; the installed driver does not need them.", AdvisorAction.RecycleItem);
        }

        (long bin, long items) = SafeQueryRecycleBin();
        if (bin > 0)
        {
            yield return new SpaceFinding(AdvisorGroup.SafeToRemove, $"Recycle Bin ({items:N0} items)", "Recycle Bin", bin,
                "Already-deleted files. Emptying is permanent.", AdvisorAction.EmptyRecycleBin);
        }
    }

    // ------------------------------------------------------------------ Check First

    private IEnumerable<SpaceFinding> DownloadFindings()
    {
        string windowsOld = Path.Combine(_env.SystemDrive, "Windows.old");
        if (Directory.Exists(windowsOld))
        {
            yield return Zone(AdvisorGroup.CheckFirst, "Previous Windows installation (Windows.old)", new[] { windowsOld },
                "Needed only to roll back a feature update. Removed with Disk Cleanup › Previous Windows installation(s).",
                AdvisorAction.OpenDiskCleanup);
        }

        if (!Directory.Exists(_env.Downloads))
        {
            yield break;
        }

        DateTimeOffset now = _time.GetUtcNow();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string file in Directory.EnumerateFiles(_env.Downloads, "*", options))
        {
            FileInfo info = new(file);
            TimeSpan age = now.UtcDateTime - info.LastWriteTimeUtc;
            bool installer = InstallerExtensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase);
            if (age < OldDownloadAge)
            {
                continue;
            }

            if (info.Length >= LargeDownloadBytes)
            {
                yield return new SpaceFinding(AdvisorGroup.CheckFirst, info.Name, info.FullName, info.Length,
                    $"Large download untouched for {(int)age.TotalDays} days.", AdvisorAction.RecycleItem);
            }
            else if (installer && info.Length >= InstallerBytes)
            {
                yield return new SpaceFinding(AdvisorGroup.CheckFirst, info.Name, info.FullName, info.Length,
                    $"Old installer/archive ({info.Extension}) from {(int)age.TotalDays} days ago; usually re-downloadable.", AdvisorAction.RecycleItem);
            }
        }
    }

    private IEnumerable<SpaceFinding> GameFindings(IReadOnlyList<string> locks)
    {
        if (_env.SteamRoot is not { } steam || !Directory.Exists(steam))
        {
            yield break;
        }

        IReadOnlyDictionary<string, DateTimeOffset> played = SteamPlayHistory.ReadAll(steam);
        DateTimeOffset now = _time.GetUtcNow();
        string vdf = new[] { Path.Combine(steam, "steamapps", "libraryfolders.vdf"), Path.Combine(steam, "config", "libraryfolders.vdf") }
            .FirstOrDefault(File.Exists) ?? string.Empty;
        IReadOnlyList<string> libraries = vdf.Length == 0 ? new[] { steam } : GameLibraryScanner.ParseLibraryFolders(File.ReadAllText(vdf), steam);

        foreach (string library in libraries)
        {
            string steamapps = Path.Combine(library.Replace('\\', Path.DirectorySeparatorChar), "steamapps");
            if (!Directory.Exists(steamapps))
            {
                continue;
            }

            foreach (string acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                VdfNode? state = VdfParser.Parse(File.ReadAllText(acf))["AppState"];
                if (GameLibraryScanner.ParseAppManifest(File.ReadAllText(acf)) is not { } m)
                {
                    continue;
                }

                string dir = Path.Combine(steamapps, "common", m.InstallDir);
                long size = long.TryParse(state?.Get("SizeOnDisk"), out long s) && s > 0 ? s : SafeFileCleaner.Measure(new[] { dir }).Bytes;
                DateTimeOffset? last = played.TryGetValue(m.AppId, out DateTimeOffset p) ? p : null;
                PlayRecency recency = SteamPlayHistory.Classify(last, now);
                string when = last is { } l ? $"last played {(int)(now - l).TotalDays} days ago ({l.LocalDateTime:yyyy-MM-dd})" : "no play time recorded";

                if (recency == PlayRecency.Recent || size < LargeGameBytes)
                {
                    yield return new SpaceFinding(AdvisorGroup.Keep, m.Name, dir, size, $"Steam game, {when}.");
                    continue;
                }

                string tag = recency switch
                {
                    PlayRecency.Dormant90 => "Not played in 90+ days",
                    PlayRecency.Dormant60 => "Not played in 60+ days",
                    _ => "Never launched on this account",
                };
                yield return new SpaceFinding(AdvisorGroup.CheckFirst, $"{m.Name} — {tag}", dir, size,
                    $"Dormant Steam game ({when}). Uninstall through Steam so your library stays consistent; you can re-download it any time.",
                    AdvisorAction.UninstallSteamGame, m.AppId);
            }
        }
    }

    // ------------------------------------------------------------------ Keep

    private IEnumerable<SpaceFinding> KeepZones(IReadOnlyList<string> locks)
    {
        foreach (string locked in locks)
        {
            yield return new SpaceFinding(AdvisorGroup.Keep, $"Important: {Path.GetFileName(locked.TrimEnd('\\', '/'))}", locked,
                CachedSize(locked), "You marked this as Important; it can't be deleted until unmarked.");
        }

        yield return new SpaceFinding(AdvisorGroup.Keep, "Windows", _env.WindowsDir, CachedSize(_env.WindowsDir),
            "The operating system. Hard-protected.");
        foreach (string name in new[] { "pagefile.sys", "swapfile.sys", "hiberfil.sys" })
        {
            string path = Path.Combine(_env.SystemDrive, name);
            long size = SafeLength(path);
            if (size > 0)
            {
                yield return new SpaceFinding(AdvisorGroup.Keep, name, path, size, "Windows memory file. Hard-protected (managed by Windows settings).");
            }
        }
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Runs a finding's action. The UI must have shown the path and size and got confirmation.</summary>
    public async Task<string> ExecuteAsync(SpaceFinding finding, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed)
        {
            return "Not confirmed — nothing was changed.";
        }

        switch (finding.Action)
        {
            case AdvisorAction.RecycleContents:
            {
                var messages = new List<string>();
                foreach (string path in finding.AllPaths.Where(Directory.Exists))
                {
                    DeletionResult r = await _deletion.RecycleContentsAsync(new DeletionRequest(path, finding.Bytes, true), cancellationToken).ConfigureAwait(false);
                    messages.Add($"{path}: {r.Message}");
                }

                return string.Join("\n", messages);
            }

            case AdvisorAction.RecycleItem:
                return (await _deletion.RecycleAsync(new DeletionRequest(finding.Path, finding.Bytes, true), cancellationToken).ConfigureAwait(false)).Message;

            case AdvisorAction.RunCleanup when _cleanup is not null && finding.ActionArgument is { } id:
                return (await _cleanup.CleanAsync(id, cancellationToken).ConfigureAwait(false)).Message;

            case AdvisorAction.EmptyRecycleBin:
                bool emptied = _recycleBin.Empty();
                await _audit.LogAsync(AuditAction.Cleaned, emptied ? "Emptied the Recycle Bin" : "Could not empty the Recycle Bin", "disk.recycle_bin",
                    details: $"{finding.Bytes:N0} bytes", success: emptied, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                return emptied ? "Recycle Bin emptied." : "Windows could not empty the Recycle Bin.";

            case AdvisorAction.UninstallSteamGame when finding.ActionArgument is { } appId && appId.All(char.IsDigit):
                Process.Start(new ProcessStartInfo($"steam://uninstall/{appId}") { UseShellExecute = true });
                await _audit.LogAsync(AuditAction.Info, $"Opened Steam uninstall for {finding.Title}", "disk.advisor", finding.Path,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                return "Steam's uninstall dialog was opened.";

            case AdvisorAction.OpenDiskCleanup:
                Process.Start(new ProcessStartInfo("cleanmgr.exe", $"/d {_env.SystemDrive.TrimEnd('\\', ':')}") { UseShellExecute = true });
                return "Disk Cleanup opened — choose \"Previous Windows installation(s)\".";

            default:
                return "No action for this item.";
        }
    }

    // ------------------------------------------------------------------ helpers

    private SpaceFinding Zone(AdvisorGroup group, string title, IEnumerable<string> paths, string reason, AdvisorAction action,
        string? argument = null)
    {
        List<string> existing = paths.Where(p => Directory.Exists(p) || File.Exists(p)).ToList();
        long bytes = existing.Count == 0 ? 0 : SafeFileCleaner.Measure(existing).Bytes;
        return new SpaceFinding(group, title, existing.FirstOrDefault() ?? paths.First(), bytes, reason, action, argument,
            existing.Skip(1).ToList());
    }

    private long CachedSize(string path)
    {
        if (_analyzer?.GetCached(Path.GetPathRoot(path) ?? path) is { } scan && DiskAnalyzerService.Find(scan.Root, path) is { } node)
        {
            return node.Size;
        }

        return 0;
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private (long Bytes, long Items) SafeQueryRecycleBin()
    {
        try
        {
            return _recycleBin.QuerySize();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (0, 0);
        }
    }
}
