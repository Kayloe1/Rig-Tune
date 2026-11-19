using System.Diagnostics;
using RigTune.Core.Models;
using RigTune.Core.Platform;

namespace RigTune.Core.Services.SystemCare;

/// <summary>
/// Startup apps from HKCU/HKLM Run (64- and 32-bit) and both Startup folders. Enabling/disabling only
/// writes the <c>StartupApproved</c> flag (snapshotted, so it is revertable), never touching the original
/// entry — exactly what Task Manager does, so both stay in sync.
/// </summary>
public sealed class StartupManager
{
    public const string TweakId = "systemcare.startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private readonly IRegistryAccess _registry;
    private readonly SnapshotService _snapshots;
    private readonly AuditLogService _audit;
    private readonly string _userStartupFolder;
    private readonly string _commonStartupFolder;
    private readonly TimeProvider _time;

    public StartupManager(IRegistryAccess registry, SnapshotService snapshots, AuditLogService audit)
        : this(registry, snapshots, audit,
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup))
    {
    }

    public StartupManager(IRegistryAccess registry, SnapshotService snapshots, AuditLogService audit,
        string userStartupFolder, string commonStartupFolder, TimeProvider? time = null)
    {
        _registry = registry;
        _snapshots = snapshots;
        _audit = audit;
        _userStartupFolder = userStartupFolder;
        _commonStartupFolder = commonStartupFolder;
        _time = time ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<StartupEntry>> EnumerateAsync() => Task.Run(Enumerate);

    public IReadOnlyList<StartupEntry> Enumerate()
    {
        var entries = new List<StartupEntry>();
        AddRun(entries, StartupLocation.CurrentUserRun, RegistryHiveName.HKCU, RegistryViewName.Registry64);
        AddRun(entries, StartupLocation.LocalMachineRun, RegistryHiveName.HKLM, RegistryViewName.Registry64);
        AddRun(entries, StartupLocation.LocalMachineRun32, RegistryHiveName.HKLM, RegistryViewName.Registry32);
        AddFolder(entries, StartupLocation.CurrentUserStartupFolder, _userStartupFolder);
        AddFolder(entries, StartupLocation.AllUsersStartupFolder, _commonStartupFolder);
        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Writes the Task Manager-compatible StartupApproved flag for the entry (snapshotted first).</summary>
    public async Task SetEnabledAsync(StartupEntry entry, bool enabled, CancellationToken cancellationToken = default,
        string tweakId = TweakId)
    {
        RegistryTarget target = ApprovedTarget(entry.Location, entry.Name);
        RegistryValueData value = RegistryValueData.FromBinary(StartupApprovedFlags.Encode(enabled, _time.GetUtcNow()));
        await _snapshots.ApplyRegistryAsync(tweakId, target, value, $"Startup app {entry.Name}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(AuditAction.StartupChanged,
            $"{(enabled ? "Enabled" : "Disabled")} startup app \"{entry.Name}\"", tweakId, target.Display,
            $"{entry.LocationDisplay}: {entry.Command}", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>StartupApproved value for a location. HKLM 32-bit Run entries live under <c>Run32</c>.</summary>
    public static RegistryTarget ApprovedTarget(StartupLocation location, string name) => location switch
    {
        StartupLocation.CurrentUserRun => new(RegistryHiveName.HKCU, RegistryViewName.Registry64, $@"{ApprovedKey}\Run", name),
        StartupLocation.LocalMachineRun => new(RegistryHiveName.HKLM, RegistryViewName.Registry64, $@"{ApprovedKey}\Run", name),
        StartupLocation.LocalMachineRun32 => new(RegistryHiveName.HKLM, RegistryViewName.Registry64, $@"{ApprovedKey}\Run32", name),
        StartupLocation.CurrentUserStartupFolder => new(RegistryHiveName.HKCU, RegistryViewName.Registry64, $@"{ApprovedKey}\StartupFolder", name),
        _ => new(RegistryHiveName.HKLM, RegistryViewName.Registry64, $@"{ApprovedKey}\StartupFolder", name),
    };

    /// <summary>Extracts the executable path from a Run command line (quoted or unquoted, env vars expanded).</summary>
    public static string? ExtractExecutable(string command)
    {
        string c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.Length == 0)
        {
            return null;
        }

        if (c[0] == '"')
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : c.Trim('"');
        }

        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            return c[..(exe + 4)];
        }

        int space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }

    private void AddRun(List<StartupEntry> entries, StartupLocation location, RegistryHiveName hive, RegistryViewName view)
    {
        foreach (string name in _registry.GetValueNames(hive, view, RunKey))
        {
            if (name.Length == 0)
            {
                continue;
            }

            string command = _registry.ReadValue(new RegistryTarget(hive, view, RunKey, name))?.String ?? string.Empty;
            entries.Add(Build(location, name, command, ExtractExecutable(command)));
        }
    }

    private void AddFolder(List<StartupEntry> entries, StartupLocation location, string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(Build(location, name, file, file));
        }
    }

    private StartupEntry Build(StartupLocation location, string name, string command, string? executable)
    {
        byte[]? flags = _registry.ReadValue(ApprovedTarget(location, name))?.GetBytes();
        return new StartupEntry(location, name, command, ReadPublisher(executable),
            StartupApprovedFlags.IsEnabled(flags), StartupApprovedFlags.DisabledAt(flags));
    }

    private static string? ReadPublisher(string? executable)
    {
        try
        {
            if (executable is null || !File.Exists(executable) ||
                executable.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string? company = FileVersionInfo.GetVersionInfo(executable).CompanyName;
            return string.IsNullOrWhiteSpace(company) ? null : company.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
