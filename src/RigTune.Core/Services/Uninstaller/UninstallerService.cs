using RigTune.Core.Models;
using RigTune.Core.Services.DiskAnalyzer;
using RigTune.Core.Storage;

namespace RigTune.Core.Services.Uninstaller;

/// <summary>
/// Program uninstaller with batch support and leftover cleanup. Uninstallers run one at a time (Windows
/// Installer can't run two at once anyway). Success is judged by whether the program's "Installed apps"
/// entry is gone, not by exit codes, which many uninstallers don't set reliably. Leftovers are only ever
/// removed after explicit selection: folders and files go to the Recycle Bin, registry keys are exported
/// to a .reg backup first and are left alone if the backup fails. Everything is audit-logged.
/// </summary>
public sealed class UninstallerService
{
    private const string AuditId = "uninstaller";

    private readonly InstalledProgramReader _reader;
    private readonly LeftoverScanner _scanner;
    private readonly IUninstallProcessRunner _runner;
    private readonly IRegistryKeyRemover _keys;
    private readonly IRecycleBin _recycleBin;
    private readonly PathGuard _guard;
    private readonly DiskLockService _locks;
    private readonly AuditLogService _audit;
    private readonly RigTunePaths _paths;
    private readonly Func<string, bool> _fileExists;
    private readonly TimeSpan _entryRemovalGrace;

    public UninstallerService(InstalledProgramReader reader, LeftoverScanner scanner, IUninstallProcessRunner runner,
        IRegistryKeyRemover keys, IRecycleBin recycleBin, PathGuard guard, DiskLockService locks, AuditLogService audit,
        RigTunePaths paths, Func<string, bool>? fileExists = null, TimeSpan? entryRemovalGrace = null)
    {
        _reader = reader;
        _scanner = scanner;
        _runner = runner;
        _keys = keys;
        _recycleBin = recycleBin;
        _guard = guard;
        _locks = locks;
        _audit = audit;
        _paths = paths;
        _fileExists = fileExists ?? File.Exists;
        _entryRemovalGrace = entryRemovalGrace ?? TimeSpan.FromSeconds(5);
    }

    public string BackupFolder => Path.Combine(_paths.Root, "UninstallBackups");

    public Task<IReadOnlyList<InstalledProgram>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => _reader.ReadAll(), cancellationToken);

    public bool SupportsQuiet(InstalledProgram program) => UninstallCommandBuilder.SupportsQuiet(program);

    /// <summary>Uninstalls one program and waits for it to finish.</summary>
    public async Task<UninstallResult> UninstallAsync(InstalledProgram program, bool preferQuiet,
        CancellationToken cancellationToken = default)
    {
        UninstallResult result = await RunAsync(program, preferQuiet, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(AuditAction.Uninstalled,
            result.Outcome == UninstallOutcome.Removed ? $"Uninstalled \"{program.DisplayName}\"" : $"Uninstall of \"{program.DisplayName}\": {result.Outcome}",
            AuditId, program.DisplayName,
            $"{program.Publisher} {program.Version} · {program.ScopeLabel} · {result.Message}".Trim(),
            success: result.Outcome == UninstallOutcome.Removed, requiresRestart: result.RestartRequired,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Uninstalls programs one after another. <paramref name="cancellationToken"/> stops the batch: the program
    /// currently running finishes on its own, RigTune just stops waiting and skips the rest.
    /// </summary>
    public async Task<IReadOnlyList<UninstallResult>> UninstallBatchAsync(IReadOnlyList<InstalledProgram> programs, bool preferQuiet,
        IProgress<(int Index, InstalledProgram Program)>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<UninstallResult>();
        for (int i = 0; i < programs.Count; i++)
        {
            InstalledProgram program = programs[i];
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new UninstallResult(program, UninstallOutcome.Cancelled, null, "Skipped: batch was stopped."));
                continue;
            }

            progress?.Report((i, program));
            results.Add(await UninstallAsync(program, preferQuiet, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Scans for leftovers of programs that are really gone. Reads the current program list for conflict checks.</summary>
    public async Task<IReadOnlyList<Leftover>> ScanLeftoversAsync(IEnumerable<UninstallResult> results,
        CancellationToken cancellationToken = default)
    {
        List<InstalledProgram> removed = results.Where(r => r.CanScanLeftovers).Select(r => r.Program).ToList();
        if (removed.Count == 0)
        {
            return Array.Empty<Leftover>();
        }

        return await Task.Run(() =>
        {
            IReadOnlyList<InstalledProgram> installed = _reader.ReadAll();
            var all = new Dictionary<string, Leftover>(StringComparer.OrdinalIgnoreCase);
            foreach (InstalledProgram program in removed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (Leftover l in _scanner.Scan(program, installed))
                {
                    all.TryAdd(l.Identity, l);
                }
            }

            return (IReadOnlyList<Leftover>)all.Values.ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the selected leftovers: files/folders to the Recycle Bin, registry keys after a .reg backup.</summary>
    public async Task<IReadOnlyList<LeftoverRemovalResult>> RemoveLeftoversAsync(IReadOnlyList<Leftover> leftovers,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> locks = await _locks.GetLocksAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<LeftoverRemovalResult>();

        // Parents first, so a child inside an already-recycled folder is reported as gone rather than failing.
        foreach (Leftover l in leftovers.OrderBy(l => l.Path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeftoverRemovalResult r = await Task.Run(() => RemoveOne(l, locks), cancellationToken).ConfigureAwait(false);
            results.Add(r);
            await _audit.LogAsync(AuditAction.Cleaned,
                $"{(r.Success ? "Removed" : "Could not remove")} leftover of \"{l.ProgramName}\"", AuditId, l.Path,
                r.BackupFile is null ? r.Message : $"{r.Message} Backup: {r.BackupFile}", r.Success,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }

        return results;
    }

    private async Task<UninstallResult> RunAsync(InstalledProgram program, bool preferQuiet, CancellationToken cancellationToken)
    {
        UninstallCommand? command = UninstallCommandBuilder.Build(program, preferQuiet, _fileExists);
        if (command is null)
        {
            return new UninstallResult(program, UninstallOutcome.UninstallerMissing, null,
                "This entry has no uninstall command. Its leftovers can be cleaned up.");
        }

        if (UninstallCommandBuilder.IsMissing(command, _fileExists))
        {
            return new UninstallResult(program, UninstallOutcome.UninstallerMissing, null,
                $"The uninstaller ({command.FileName}) no longer exists, so the program is already gone. Its leftovers can be cleaned up.");
        }

        int exit;
        try
        {
            exit = await _runner.RunAsync(command, () => !_reader.StillRegistered(program), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new UninstallResult(program, UninstallOutcome.Cancelled, null,
                "Stopped waiting. If the uninstaller window is still open it keeps running; refresh the list when it's done.");
        }
        catch (Exception ex)
        {
            return new UninstallResult(program, UninstallOutcome.Failed, null, $"Could not start the uninstaller: {ex.Message}");
        }

        // Some uninstallers remove the entry a moment after their process exits.
        DateTime until = DateTime.UtcNow + _entryRemovalGrace;
        bool registered = _reader.StillRegistered(program);
        while (registered && DateTime.UtcNow < until)
        {
            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            registered = _reader.StillRegistered(program);
        }

        bool restart = exit is 3010 or 1641;
        string how = command.IsQuiet ? $" ({command.Description})" : string.Empty;
        if (!registered)
        {
            return new UninstallResult(program, UninstallOutcome.Removed, exit,
                restart ? $"Removed{how}. Restart Windows to finish." : $"Removed{how}.", restart);
        }

        return exit switch
        {
            1602 => new UninstallResult(program, UninstallOutcome.Cancelled, exit, "Cancelled in the uninstaller."),
            0 or 3010 or 1641 => new UninstallResult(program, UninstallOutcome.StillInstalled, exit,
                restart
                    ? "The uninstaller needs a restart to finish; the program is still listed until then."
                    : "The uninstaller closed but the program is still installed (it may have been cancelled).",
                restart),
            _ => new UninstallResult(program, UninstallOutcome.Failed, exit, $"The uninstaller reported an error (exit code {exit})."),
        };
    }

    private LeftoverRemovalResult RemoveOne(Leftover l, IReadOnlyList<string> locks)
    {
        try
        {
            if (l.Kind == LeftoverKind.RegistryKey)
            {
                if (l.KeyPath is null || !IsRemovableKey(l))
                {
                    return new LeftoverRemovalResult(l, false, "Blocked: not a program key.");
                }

                string file = Path.Combine(BackupFolder,
                    $"{DateTime.Now:yyyyMMdd-HHmmss}_{Sanitize(l.ProgramName)}_{Sanitize(l.KeyPath.Split('\\').Last())}_{Guid.NewGuid().ToString("N")[..6]}.reg");
                _keys.Export(l.Hive, l.View, l.KeyPath, file);
                _keys.DeleteTree(l.Hive, l.View, l.KeyPath);
                return new LeftoverRemovalResult(l, true, "Registry key removed (backed up first).", file);
            }

            bool exists = l.Kind == LeftoverKind.Folder ? Directory.Exists(l.Path) : File.Exists(l.Path);
            if (!exists)
            {
                return new LeftoverRemovalResult(l, true, "Already gone.");
            }

            DeletionCheck check = _guard.Evaluate(l.Path, locks);
            if (!check.Allowed)
            {
                return new LeftoverRemovalResult(l, false, $"Blocked: {check.Reason}");
            }

            if (l.Kind == LeftoverKind.Folder)
            {
                _recycleBin.SendDirectoryToRecycleBin(l.Path);
            }
            else
            {
                _recycleBin.SendFileToRecycleBin(l.Path);
            }

            return new LeftoverRemovalResult(l, true, "Moved to the Recycle Bin.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new LeftoverRemovalResult(l, false, ex.Message);
        }
    }

    /// <summary>Only keys at least two levels below Software (Software\X) or an Uninstall entry may be removed.</summary>
    private static bool IsRemovableKey(Leftover l)
    {
        string[] parts = (l.KeyPath ?? string.Empty).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (l.Hive is not (RegistryHiveName.HKCU or RegistryHiveName.HKLM) || parts.Length < 2 ||
            !parts[0].Equals("Software", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string second = parts[1];
        if (second.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            // Only an individual Uninstall entry under Microsoft is allowed.
            return parts.Length == 6 && string.Join('\\', parts[..5]).Equals(InstalledProgramReader.UninstallRoot, StringComparison.OrdinalIgnoreCase);
        }

        return !second.Equals("Classes", StringComparison.OrdinalIgnoreCase) &&
               !second.Equals("Policies", StringComparison.OrdinalIgnoreCase) &&
               !second.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string s)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new(s.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return clean.Length > 40 ? clean[..40] : clean;
    }
}
