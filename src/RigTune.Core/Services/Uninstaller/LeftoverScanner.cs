using System.Text;
using System.Text.RegularExpressions;
using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Services.DiskAnalyzer;

namespace RigTune.Core.Services.Uninstaller;

/// <summary>Where leftover folders and shortcuts are looked for. Injectable for tests.</summary>
public sealed record LeftoverRoots(
    IReadOnlyList<string> AppFolders,
    IReadOnlyList<string> ShortcutFolders)
{
    public static LeftoverRoots ForThisMachine()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        string local = F(Environment.SpecialFolder.LocalApplicationData);
        var apps = new List<string>
        {
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData),
            F(Environment.SpecialFolder.ApplicationData),
            local,
            Path.Combine(local, "Programs"),
            Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
            F(Environment.SpecialFolder.CommonStartMenu) is { Length: > 0 } cs ? Path.Combine(cs, "Programs") : string.Empty,
            F(Environment.SpecialFolder.StartMenu) is { Length: > 0 } us ? Path.Combine(us, "Programs") : string.Empty,
        };
        var shortcuts = new List<string>
        {
            F(Environment.SpecialFolder.CommonStartMenu) is { Length: > 0 } cs2 ? Path.Combine(cs2, "Programs") : string.Empty,
            F(Environment.SpecialFolder.StartMenu) is { Length: > 0 } us2 ? Path.Combine(us2, "Programs") : string.Empty,
            F(Environment.SpecialFolder.CommonDesktopDirectory),
            F(Environment.SpecialFolder.DesktopDirectory),
        };
        return new LeftoverRoots(
            apps.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            shortcuts.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}

/// <summary>
/// Finds what an uninstalled program left behind. Deliberately conservative:
/// <list type="bullet">
/// <item>names must match exactly after normalization ("7-Zip 23.01 (x64)" ≙ "7-Zip"); no partial/fuzzy matches;</item>
/// <item>nothing that belongs to — contains, or sits inside — a program that is still installed;</item>
/// <item>never a root (Program Files, AppData, Start Menu…), a hard-protected path, or a generic name (Common Files, Microsoft…);</item>
/// <item>publisher-level folders/keys are only offered when no other installed program has that publisher, and never pre-selected.</item>
/// </list>
/// Only call this after the program is gone (<see cref="UninstallResult.CanScanLeftovers"/>).
/// </summary>
public sealed partial class LeftoverScanner
{
    private static readonly string[] RegistryBasesHklm = { "SOFTWARE" };

    /// <summary>Normalized names that are never leftovers (shared by Windows or many vendors).</summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "commonfiles", "microsoftshared", "internetexplorer", "windowsapps", "windowsdefender",
        "windowspowershell", "windowsnt", "windowsmail", "windowsmediaplayer", "windowsphotoviewer", "windowssidebar",
        "packages", "temp", "tmp", "programs", "installer", "packagecache", "cache", "caches", "app", "application", "bin",
        "program", "current", "data", "config", "settings", "logs", "log", "users", "user", "default", "public", "common",
        "shared", "update", "updates", "updater", "uninstall", "uninstaller", "setup", "tools", "plugins", "x64", "x86",
        "classes", "policies", "wow6432node", "clients", "registeredapplications", "odbc", "partner", "khronos", "oem",
        "intel", "nvidia", "nvidiacorporation", "amd", "realtek", "dell", "hp", "lenovo", "asus", "msi", "google", "apple",
        "mozilla", "adobe", "oracle", "java", "python", "nodejs", "steam", "steamapps", "epicgames", "games", "startup",
        "accessories", "administrativetools", "maintenance", "systemtools", "accessibility", "desktop", "documents",
        "downloads", "music", "pictures", "videos", "onedrive", "dropbox", "regid19910800microsoft",
        "regid19910800microsoftcorporation", "rigtune",
    };

    private readonly IRegistryAccess _registry;
    private readonly PathGuard _guard;
    private readonly LeftoverRoots _roots;

    public LeftoverScanner(IRegistryAccess registry, PathGuard guard, LeftoverRoots roots)
    {
        _registry = registry;
        _guard = guard;
        _roots = roots;
    }

    /// <param name="removed">Snapshot of the program taken before it was uninstalled.</param>
    /// <param name="stillInstalled">Programs that are installed now (read after the uninstall).</param>
    public IReadOnlyList<Leftover> Scan(InstalledProgram removed, IReadOnlyList<InstalledProgram> stillInstalled)
    {
        IReadOnlyList<InstalledProgram> others = stillInstalled.Where(p => p.Id != removed.Id).ToList();
        HashSet<string> names = NameVariants(removed);
        string? publisher = NormalizePublisher(removed.Publisher);

        // Names and publishers other installed programs still use can't be claimed by this one.
        var otherNames = new HashSet<string>(others.SelectMany(NameVariants), StringComparer.OrdinalIgnoreCase);
        names.ExceptWith(otherNames);
        bool publisherShared = publisher is null || GenericNames.Contains(publisher) || otherNames.Contains(publisher) ||
                               others.Any(o => NormalizePublisher(o.Publisher) == publisher);
        List<string> otherFolders = others.SelectMany(OwnedFolders).Where(IsMeaningfulFolder).ToList();

        var results = new Dictionary<string, Leftover>(StringComparer.OrdinalIgnoreCase);
        void Add(Leftover l)
        {
            if (!results.ContainsKey(l.Identity) || l.Confidence < results[l.Identity].Confidence)
            {
                results[l.Identity] = l;
            }
        }

        // 1. The program's own install folder, if the uninstaller left it behind.
        if (removed.InstallLocation is { } location && Directory.Exists(location) && IsSafeFolder(location, otherFolders) &&
            IsMeaningfulFolder(location))
        {
            Add(Folder(removed, location, "Install folder left behind by the uninstaller", LeftoverConfidence.High));
        }

        // 2. Same-name folders in Program Files, ProgramData, AppData and the Start Menu (+ Publisher\Name).
        foreach (string root in _roots.AppFolders.Where(Directory.Exists))
        {
            foreach (string dir in SafeSubdirectories(root))
            {
                string n = Normalize(Path.GetFileName(dir));
                if (names.Contains(n))
                {
                    if (IsSafeFolder(dir, otherFolders))
                    {
                        Add(Folder(removed, dir, $"Named after the program in {RootLabel(root)}", LeftoverConfidence.High));
                    }
                }
                else if (publisher is not null && n == publisher)
                {
                    foreach (string sub in SafeSubdirectories(dir).Where(s => names.Contains(Normalize(Path.GetFileName(s)))))
                    {
                        if (IsSafeFolder(sub, otherFolders))
                        {
                            Add(Folder(removed, sub, $"Program folder under its publisher in {RootLabel(root)}", LeftoverConfidence.High));
                        }
                    }

                    if (!publisherShared && IsSafeFolder(dir, otherFolders))
                    {
                        Add(Folder(removed, dir, "Publisher folder (no other installed program uses this publisher) — check before removing",
                            LeftoverConfidence.Medium));
                    }
                }
            }
        }

        // 3. Start menu / desktop shortcuts with the program's name.
        foreach (string root in _roots.ShortcutFolders.Where(Directory.Exists))
        {
            foreach (string file in SafeFiles(root, "*.lnk").Concat(SafeFiles(root, "*.url")))
            {
                if (names.Contains(Normalize(Path.GetFileNameWithoutExtension(file))) && _guard.IsHardProtected(file) is false)
                {
                    Add(new Leftover
                    {
                        ProgramName = removed.DisplayName,
                        Kind = LeftoverKind.File,
                        Path = PathGuard.Normalize(file),
                        Reason = "Shortcut to the removed program",
                        Confidence = LeftoverConfidence.High,
                        SizeBytes = SafeLength(file),
                    });
                }
            }
        }

        // 4. Registry: Software\Name, Software\Publisher\Name (HKCU, HKLM 64-bit and 32-bit), plus an orphaned Uninstall entry.
        foreach ((RegistryHiveName hive, RegistryViewName view, string bases) in RegistryRoots())
        {
            foreach (string sub in _registry.GetSubKeyNames(hive, view, bases))
            {
                string n = Normalize(sub);
                string path = bases + "\\" + sub;
                if (names.Contains(n))
                {
                    Add(Key(removed, hive, view, path, "Settings key named after the program", LeftoverConfidence.High));
                }
                else if (publisher is not null && n == publisher)
                {
                    foreach (string child in _registry.GetSubKeyNames(hive, view, path).Where(c => names.Contains(Normalize(c))))
                    {
                        Add(Key(removed, hive, view, path + "\\" + child, "Settings key under its publisher", LeftoverConfidence.High));
                    }

                    if (!publisherShared)
                    {
                        Add(Key(removed, hive, view, path, "Publisher key (no other installed program uses this publisher) — check before removing",
                            LeftoverConfidence.Medium));
                    }
                }
            }
        }

        if (_registry.KeyExists(removed.Hive, removed.View, removed.KeyPath))
        {
            Add(Key(removed, removed.Hive, removed.View, removed.KeyPath, "Orphaned \"Installed apps\" entry", LeftoverConfidence.High));
        }

        // Don't list a folder/key when its parent is already listed (removing the parent covers it).
        List<Leftover> list = results.Values.ToList();
        return list
            .Where(l => !list.Any(p => !ReferenceEquals(p, l) && p.Kind == l.Kind && Contains(p, l) && p.Confidence <= l.Confidence))
            .OrderBy(l => l.Kind)
            .ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Lower-case letters and digits only, with anything in brackets, versions and architecture words removed:
    /// "7-Zip 23.01 (x64)" → "7zip", "Notepad++ (64-bit x64)" → "notepad".
    /// </summary>
    public static string Normalize(string? name)
    {
        string s = Brackets().Replace(name ?? string.Empty, " ");
        s = ArchWords().Replace(s, " ");
        string previous;
        do
        {
            previous = s;
            s = TrailingVersion().Replace(s.TrimEnd(), string.Empty);
        }
        while (s != previous);

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    /// <summary>"Valve Corporation" → "valve", "Discord Inc." → "discord".</summary>
    public static string? NormalizePublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return null;
        }

        string s = CompanySuffix().Replace(publisher, " ");
        string n = Normalize(s);
        return n.Length >= 2 ? n : null;
    }

    /// <summary>Normalized names a program's folders/keys may use.</summary>
    public static HashSet<string> NameVariants(InstalledProgram program)
    {
        var raw = new List<string?> { program.DisplayName };
        if (!program.KeyName.Contains('{'))
        {
            raw.Add(InnoSuffix().Replace(program.KeyName, string.Empty));
        }

        if (program.InstallLocation is { } location)
        {
            raw.Add(Path.GetFileName(PathGuard.Normalize(location)));
        }

        // "Google Chrome" by "Google LLC" → also "Chrome".
        string? firstPublisherWord = program.Publisher?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstPublisherWord is not null &&
            program.DisplayName.StartsWith(firstPublisherWord + " ", StringComparison.OrdinalIgnoreCase))
        {
            raw.Add(program.DisplayName[(firstPublisherWord.Length + 1)..]);
        }

        return new HashSet<string>(
            raw.Select(Normalize).Where(n => n.Length >= 3 && !GenericNames.Contains(n)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> OwnedFolders(InstalledProgram p)
    {
        if (p.InstallLocation is { Length: > 0 } location)
        {
            yield return location;
        }

        string? exe = p.UninstallString is { } u ? UninstallCommandBuilder.Split(u, File.Exists)?.File : null;
        if (exe is not null && UninstallCommandBuilder.IsAbsoluteWindowsPath(exe) && !exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(exe) is { } dir)
        {
            yield return dir;
        }
    }

    private bool IsMeaningfulFolder(string folder)
    {
        string p = PathGuard.Normalize(folder);
        return !PathGuard.IsDriveRoot(p) && !_guard.IsHardProtected(p) &&
               !_roots.AppFolders.Concat(_roots.ShortcutFolders).Any(r => PathGuard.IsSameOrUnder(r, p)) &&
               !GenericNames.Contains(Normalize(Path.GetFileName(p)));
    }

    private bool IsSafeFolder(string folder, IReadOnlyList<string> otherFolders) =>
        IsMeaningfulFolder(folder) &&
        !otherFolders.Any(o => PathGuard.IsSameOrUnder(o, folder) || PathGuard.IsSameOrUnder(folder, o));

    private static Leftover Folder(InstalledProgram program, string path, string reason, LeftoverConfidence confidence) => new()
    {
        ProgramName = program.DisplayName,
        Kind = LeftoverKind.Folder,
        Path = PathGuard.Normalize(path),
        Reason = reason,
        Confidence = confidence,
        SizeBytes = FolderSize(path),
    };

    private static Leftover Key(InstalledProgram program, RegistryHiveName hive, RegistryViewName view, string keyPath, string reason,
        LeftoverConfidence confidence) => new()
    {
        ProgramName = program.DisplayName,
        Kind = LeftoverKind.RegistryKey,
        Path = $@"{hive}\{keyPath}" + (view == RegistryViewName.Registry32 ? " (32-bit)" : string.Empty),
        Reason = reason,
        Confidence = confidence,
        Hive = hive,
        View = view,
        KeyPath = keyPath,
    };

    private static bool Contains(Leftover parent, Leftover child) => parent.Kind == LeftoverKind.RegistryKey
        ? parent.Hive == child.Hive && parent.View == child.View &&
          (child.KeyPath ?? string.Empty).StartsWith(parent.KeyPath + "\\", StringComparison.OrdinalIgnoreCase)
        : PathGuard.IsSameOrUnder(child.Path, parent.Path) && !string.Equals(child.Path, parent.Path, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(RegistryHiveName, RegistryViewName, string)> RegistryRoots()
    {
        yield return (RegistryHiveName.HKCU, RegistryViewName.Default, "Software");
        foreach (string b in RegistryBasesHklm)
        {
            yield return (RegistryHiveName.HKLM, RegistryViewName.Registry64, b);
            yield return (RegistryHiveName.HKLM, RegistryViewName.Registry32, b);
        }
    }

    private static string RootLabel(string root) => Path.GetFileName(PathGuard.Normalize(root)) is { Length: > 0 } name ? name : root;

    private static IEnumerable<string> SafeSubdirectories(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir)
                .Where(d => (new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) == 0)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try
        {
            return Directory.GetFiles(dir, pattern);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Array.Empty<string>();
        }
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return 0;
        }
    }

    private static long FolderSize(string dir)
    {
        long total = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            int count = 0;
            foreach (FileInfo f in new DirectoryInfo(dir).EnumerateFiles("*", options))
            {
                total += f.Length;
                if (++count > 200_000)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }

        return total;
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\b(x64|x86|amd64|arm64|64-bit|32-bit|64bit|32bit|win64|win32)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArchWords();

    [GeneratedRegex(@"[\s_-]+v?\d+(\.\d+)*[a-z]?$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@"[,.]?\s*\b(inc|llc|ltd|limited|corp|corporation|co|gmbh|ag|sa|srl|bv|plc|pty|company|software|technologies|technology|games|studios|entertainment)\b\.?",
        RegexOptions.IgnoreCase)]
    private static partial Regex CompanySuffix();

    [GeneratedRegex(@"_is1$", RegexOptions.IgnoreCase)]
    private static partial Regex InnoSuffix();
}
