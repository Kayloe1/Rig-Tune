using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Platform;

namespace RigTune.Core.Services.Uninstaller;

/// <summary>
/// Lists installed desktop programs the same way "Installed apps" / Programs and Features does: the
/// 64-bit and 32-bit HKLM Uninstall keys plus the per-user HKCU key, hiding system components, updates
/// and patches, and entries without a way to uninstall.
/// </summary>
public sealed partial class InstalledProgramReader
{
    public const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHiveName Hive, RegistryViewName View)[] Roots =
    {
        (RegistryHiveName.HKLM, RegistryViewName.Registry64),
        (RegistryHiveName.HKLM, RegistryViewName.Registry32),
        (RegistryHiveName.HKCU, RegistryViewName.Default),
    };

    private static readonly string[] UpdateReleaseTypes = { "Update", "Hotfix", "Security Update", "Service Pack" };

    private readonly IRegistryAccess _registry;

    public InstalledProgramReader(IRegistryAccess registry) => _registry = registry;

    public IReadOnlyList<InstalledProgram> ReadAll()
    {
        var programs = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((RegistryHiveName hive, RegistryViewName view) in Roots)
        {
            foreach (string keyName in _registry.GetSubKeyNames(hive, view, UninstallRoot))
            {
                InstalledProgram? program;
                try
                {
                    program = Read(hive, view, keyName);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    continue;
                }

                // The same product can appear in both views on some installers; keep the first (64-bit) one.
                if (program is not null && seen.Add($"{program.DisplayName}|{program.Version}|{program.KeyName}"))
                {
                    programs.Add(program);
                }
            }
        }

        return programs.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool StillRegistered(InstalledProgram program) =>
        _registry.KeyExists(program.Hive, program.View, program.KeyPath) &&
        !string.IsNullOrWhiteSpace(ReadString(program.Hive, program.View, program.KeyPath, "DisplayName"));

    /// <summary>Reads one entry; null when it should be hidden from the list.</summary>
    public InstalledProgram? Read(RegistryHiveName hive, RegistryViewName view, string keyName)
    {
        string keyPath = UninstallRoot + "\\" + keyName;
        string? displayName = ReadString(hive, view, keyPath, "DisplayName")?.Trim();
        if (string.IsNullOrEmpty(displayName))
        {
            return null;
        }

        if (ReadDword(hive, view, keyPath, "SystemComponent") == 1 ||
            !string.IsNullOrEmpty(ReadString(hive, view, keyPath, "ParentKeyName")) ||
            UpdateReleaseTypes.Contains(ReadString(hive, view, keyPath, "ReleaseType") ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        string? uninstall = Clean(ReadString(hive, view, keyPath, "UninstallString"));
        string? quiet = Clean(ReadString(hive, view, keyPath, "QuietUninstallString"));
        bool windowsInstaller = ReadDword(hive, view, keyPath, "WindowsInstaller") == 1;
        string? productCode = windowsInstaller || IsMsiExec(uninstall) ? FindProductCode(keyName, uninstall) : null;
        if (uninstall is null && quiet is null && productCode is null)
        {
            return null;
        }

        uint? sizeKb = ReadDword(hive, view, keyPath, "EstimatedSize");
        return new InstalledProgram
        {
            Hive = hive,
            View = view,
            KeyPath = keyPath,
            KeyName = keyName,
            DisplayName = displayName,
            Publisher = Clean(ReadString(hive, view, keyPath, "Publisher")),
            Version = Clean(ReadString(hive, view, keyPath, "DisplayVersion")),
            InstallDate = ParseInstallDate(ReadString(hive, view, keyPath, "InstallDate")),
            EstimatedSizeBytes = sizeKb is > 0 ? (long)sizeKb.Value * 1024 : null,
            InstallLocation = Clean(ReadString(hive, view, keyPath, "InstallLocation"))?.Trim('"'),
            UninstallString = uninstall,
            QuietUninstallString = quiet,
            DisplayIcon = Clean(ReadString(hive, view, keyPath, "DisplayIcon")),
            ProductCode = productCode,
            IsInnoSetup = ReadString(hive, view, keyPath, "Inno Setup: App Path") is not null ||
                          InnoUninstaller().IsMatch(uninstall ?? string.Empty),
        };
    }

    public static DateOnly? ParseInstallDate(string? raw)
    {
        string s = (raw ?? string.Empty).Trim();
        foreach (string format in new[] { "yyyyMMdd", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "d.M.yyyy" })
        {
            if (DateOnly.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                return date;
            }
        }

        return null;
    }

    private static bool IsMsiExec(string? command) =>
        command is not null && command.TrimStart('"').StartsWith("msiexec", StringComparison.OrdinalIgnoreCase);

    private static string? FindProductCode(string keyName, string? uninstall)
    {
        Match fromKey = ProductCodePattern().Match(keyName);
        if (fromKey.Success && fromKey.Value.Length == keyName.Length)
        {
            return fromKey.Value.ToUpperInvariant();
        }

        Match fromCommand = ProductCodePattern().Match(uninstall ?? string.Empty);
        return fromCommand.Success ? fromCommand.Value.ToUpperInvariant() : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string? ReadString(RegistryHiveName hive, RegistryViewName view, string keyPath, string name)
    {
        RegistryValueData? data = _registry.ReadValue(new RegistryTarget(hive, view, keyPath, name));
        return data?.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString ? data.String : null;
    }

    private uint? ReadDword(RegistryHiveName hive, RegistryViewName view, string keyPath, string name)
    {
        RegistryValueData? data = _registry.ReadValue(new RegistryTarget(hive, view, keyPath, name));
        return data?.Kind switch
        {
            RegistryValueKind.DWord => data.DWord,
            RegistryValueKind.QWord => (uint?)Math.Min(data.QWord ?? 0, uint.MaxValue),
            RegistryValueKind.String when uint.TryParse(data.String, out uint v) => v,
            _ => null,
        };
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex ProductCodePattern();

    [GeneratedRegex(@"unins\d{3}\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex InnoUninstaller();
}
