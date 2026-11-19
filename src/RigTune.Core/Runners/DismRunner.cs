using System.Management;
using System.Xml.Linq;
using RigTune.Core.Platform;

namespace RigTune.Core.Runners;

/// <summary>
/// Windows image servicing: Optional Features (live WMI query, DISM toggle), Appx/Store package
/// removal and restore, OneDrive/Edge uninstallers, plus boot (BCD) and WinRE configuration.
/// </summary>
public sealed class DismRunner
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    private readonly object _cacheLock = new();
    private Dictionary<string, string>? _features;
    private DateTime _featuresAt;

    /// <summary>All optional features → "Enabled" / "Disabled" / "Absent" / "Unknown".</summary>
    public IReadOnlyDictionary<string, string> QueryFeatures(bool refresh = false)
    {
        lock (_cacheLock)
        {
            if (!refresh && _features is not null && DateTime.UtcNow - _featuresAt < CacheLifetime)
            {
                return _features;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher("SELECT Name, InstallState FROM Win32_OptionalFeature");
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject feature in results)
            {
                using (feature)
                {
                    string name = feature["Name"] as string ?? string.Empty;
                    map[name] = Convert.ToUInt32(feature["InstallState"] ?? 4u) switch
                    {
                        1 => "Enabled",
                        2 => "Disabled",
                        3 => "Absent",
                        _ => "Unknown",
                    };
                }
            }

            _features = map;
            _featuresAt = DateTime.UtcNow;
            return map;
        }
    }

    public string? GetFeatureState(string feature) =>
        QueryFeatures().TryGetValue(feature, out string? state) ? state : null;

    public void SetFeatureState(string feature, bool enable)
    {
        string verb = enable ? "/Enable-Feature /All" : "/Disable-Feature";
        // 3010 = success, restart required.
        ProcessRunner.RunChecked("dism", $"/Online {verb} /FeatureName:{feature} /NoRestart /Quiet",
            TimeSpan.FromMinutes(20), 3010);
        lock (_cacheLock)
        {
            _features = null;
        }
    }

    /// <summary>Removes an Appx package for all users and de-provisions it for new users.</summary>
    public CommandResult RemoveAppxPackage(string packageName)
    {
        string name = Sanitize(packageName);
        string script =
            $"Get-AppxPackage -AllUsers -Name '{name}' | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue; " +
            $"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq '{name}' | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue";
        return PowerShell(script);
    }

    /// <summary>Re-registers / reinstalls a package (Microsoft Store via <c>wsreset -i</c>).</summary>
    public CommandResult RestoreAppxPackage(string packageName)
    {
        if (packageName.Equals("Microsoft.WindowsStore", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessRunner.Run("wsreset", "-i", TimeSpan.FromMinutes(10));
        }

        string name = Sanitize(packageName);
        return PowerShell(
            $"Get-AppxPackage -AllUsers -Name '{name}' | ForEach-Object {{ Add-AppxPackage -DisableDevelopmentMode -Register \"$($_.InstallLocation)\\AppXManifest.xml\" }}");
    }

    public bool IsAppxInstalled(string packageName)
    {
        CommandResult result = PowerShell($"if (Get-AppxPackage -AllUsers -Name '{Sanitize(packageName)}') {{ 'yes' }} else {{ 'no' }}");
        return result.Output.Contains("yes", StringComparison.OrdinalIgnoreCase);
    }

    public CommandResult UninstallOneDrive()
    {
        ProcessRunner.Run("taskkill", "/f /im OneDrive.exe");
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OneDriveSetup.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "OneDriveSetup.exe"),
        };
        string? setup = candidates.FirstOrDefault(File.Exists);
        return setup is null
            ? new CommandResult(2, string.Empty, "OneDriveSetup.exe not found (OneDrive may already be removed).")
            : ProcessRunner.Run(setup, "/uninstall", TimeSpan.FromMinutes(5));
    }

    public static bool IsOneDriveInstalled() =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "OneDrive", "OneDrive.exe"))
        || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Microsoft OneDrive", "OneDrive.exe"));

    public CommandResult UninstallEdge()
    {
        string? setup = FindEdgeSetup();
        return setup is null
            ? new CommandResult(2, string.Empty, "Microsoft Edge installer not found.")
            : ProcessRunner.Run(setup, "--uninstall --system-level --verbose-logging --force-uninstall",
                TimeSpan.FromMinutes(5));
    }

    public static bool IsEdgeInstalled() => FindEdgeSetup() is not null;

    private static string? FindEdgeSetup()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft", "Edge", "Application");
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateDirectories(root)
            .Select(d => Path.Combine(d, "Installer", "setup.exe"))
            .Where(File.Exists)
            .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static CommandResult PowerShell(string script) =>
        ProcessRunner.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            TimeSpan.FromMinutes(10));

    private static string Sanitize(string packageName) =>
        new(packageName.Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '*').ToArray());
}

/// <summary>Optional Windows Features as snapshot-able state ("Enabled"/"Disabled").</summary>
public sealed class DismFeatureStateRunner : IStateRunner
{
    private readonly DismRunner _dism;

    public DismFeatureStateRunner(DismRunner dism) => _dism = dism;

    public string Kind => "dism_feature";

    public string? ReadState(string target) => _dism.GetFeatureState(target) switch
    {
        null => null,
        "Absent" => "Disabled",
        string s => s,
    };

    public void WriteState(string target, string state) =>
        _dism.SetFeatureState(target, string.Equals(state, "Enabled", StringComparison.OrdinalIgnoreCase));

    public string Describe(string target) => $"Optional feature: {target}";
}

/// <summary>
/// Boot Configuration Data elements of <c>{current}</c> (e.g. <c>hypervisorlaunchtype</c>,
/// <c>bootmenupolicy</c>, <c>bootlog</c>). The state "&lt;unset&gt;" means the element is absent.
/// </summary>
public sealed class BcdStateRunner : IStateRunner
{
    public const string Unset = "<unset>";

    public string Kind => "bcd";

    public string? ReadState(string target)
    {
        CommandResult result = ProcessRunner.Run("bcdedit", "/enum {current}");
        if (!result.Succeeded())
        {
            return null;
        }

        foreach (string line in result.Output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(target + " ", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[target.Length..].Trim();
            }
        }

        return Unset;
    }

    public void WriteState(string target, string state)
    {
        string element = new(target.Where(c => char.IsLetterOrDigit(c)).ToArray());
        if (state == Unset)
        {
            ProcessRunner.RunChecked("bcdedit", $"/deletevalue {{current}} {element}");
        }
        else
        {
            string value = new(state.Where(c => char.IsLetterOrDigit(c)).ToArray());
            ProcessRunner.RunChecked("bcdedit", $"/set {{current}} {element} {value}");
        }
    }

    public string Describe(string target) => $"Boot configuration: {{current}} {target}";
}

/// <summary>Windows Recovery Environment on/off (read from ReAgent.xml, written via reagentc).</summary>
public sealed class WinReStateRunner : IStateRunner
{
    public string Kind => "winre";

    public string? ReadState(string target)
    {
        string xml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Recovery", "ReAgent.xml");
        if (!File.Exists(xml))
        {
            return null;
        }

        try
        {
            XElement? install = XDocument.Load(xml).Descendants("InstallState").FirstOrDefault();
            return install?.Attribute("state")?.Value == "1" ? "Enabled" : "Disabled";
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public void WriteState(string target, string state) =>
        ProcessRunner.RunChecked("reagentc",
            string.Equals(state, "Enabled", StringComparison.OrdinalIgnoreCase) ? "/enable" : "/disable");

    public string Describe(string target) => "Windows Recovery Environment";
}
