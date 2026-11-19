using System.Text.Json;
using System.Xml.Linq;
using RigTune.Core.Models;
using RigTune.Core.Platform;

namespace RigTune.Core.Services.GameMode;

/// <summary>
/// Finds installed games: Steam (libraryfolders.vdf + appmanifest_*.acf), Epic (launcher manifests),
/// Xbox / Game Pass (<c>X:\XboxGames\*\Content</c> + MicrosoftGame.config), and EA app / Battle.net
/// (uninstall entries by publisher). Launchers, redistributables and crash handlers are filtered out.
/// </summary>
public sealed class GameLibraryScanner
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private readonly IRegistryAccess _registry;
    private readonly Func<IEnumerable<string>> _driveRoots;
    private readonly string _programData;

    public GameLibraryScanner(IRegistryAccess registry)
        : this(registry,
            () => DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
    {
    }

    public GameLibraryScanner(IRegistryAccess registry, Func<IEnumerable<string>> driveRoots, string programData)
    {
        _registry = registry;
        _driveRoots = driveRoots;
        _programData = programData;
    }

    public Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var games = new List<DiscoveredGame>();
        void Try(Func<IEnumerable<DiscoveredGame>> scan)
        {
            try
            {
                games.AddRange(scan());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException)
            {
                // A broken launcher manifest never stops the rest of the scan.
            }
        }

        if (FindSteamRoot() is { } steam)
        {
            Try(() => ScanSteam(steam));
        }

        Try(() => ScanEpic(Path.Combine(_programData, "Epic", "EpicGamesLauncher", "Data", "Manifests")));
        Try(() => ScanXbox(_driveRoots()));
        Try(() => ScanUninstallEntries(GameSource.EA, new[] { "Electronic Arts" }, new[] { "EA app", "EA Desktop", "Origin" }));
        Try(() => ScanUninstallEntries(GameSource.BattleNet, new[] { "Blizzard Entertainment" }, new[] { "Battle.net" }));
        cancellationToken.ThrowIfCancellationRequested();
        return (IReadOnlyList<DiscoveredGame>)games
            .GroupBy(g => g.InstallDir.TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }, cancellationToken);

    // ------------------------------------------------------------------ Steam

    public string? FindSteamRoot()
    {
        string? path = _registry.ReadValue(new RegistryTarget(RegistryHiveName.HKCU, RegistryViewName.Registry64, @"Software\Valve\Steam", "SteamPath"))?.String
                       ?? _registry.ReadValue(new RegistryTarget(RegistryHiveName.HKLM, RegistryViewName.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"))?.String;
        return path is not null && Directory.Exists(path) ? path.Replace('/', Path.DirectorySeparatorChar) : null;
    }

    /// <summary>Library folder paths from libraryfolders.vdf (both the 2021+ and the legacy format).</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string vdfText, string steamRoot)
    {
        VdfNode root = VdfParser.Parse(vdfText);
        VdfNode? folders = root["libraryfolders"] ?? root["LibraryFolders"];
        var paths = new List<string> { steamRoot };
        if (folders is not null)
        {
            foreach ((string key, VdfNode node) in folders.Children)
            {
                if (!int.TryParse(key, out _))
                {
                    continue;
                }

                string? path = node.Value ?? node.Get("path");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }
        }

        return paths.Select(p => p.TrimEnd('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>(appid, name, installdir) from an appmanifest_*.acf; null for non-games or incomplete manifests.</summary>
    public static (string AppId, string Name, string InstallDir, DateTimeOffset? LastUpdated)? ParseAppManifest(string acfText)
    {
        VdfNode? app = VdfParser.Parse(acfText)["AppState"];
        string? id = app?.Get("appid"), name = app?.Get("name"), dir = app?.Get("installdir");
        if (id is null || name is null || dir is null || GameExeFilter.IsNonGameTitle(name))
        {
            return null;
        }

        DateTimeOffset? updated = long.TryParse(app!.Get("LastUpdated"), out long unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;
        return (id, name, dir, updated);
    }

    public IEnumerable<DiscoveredGame> ScanSteam(string steamRoot)
    {
        string vdf = new[]
        {
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
        }.FirstOrDefault(File.Exists) ?? string.Empty;
        IReadOnlyList<string> libraries = vdf.Length == 0
            ? new[] { steamRoot }
            : ParseLibraryFolders(File.ReadAllText(vdf), steamRoot);

        foreach (string library in libraries)
        {
            string steamapps = Path.Combine(library.Replace('\\', Path.DirectorySeparatorChar), "steamapps");
            if (!Directory.Exists(steamapps))
            {
                continue;
            }

            foreach (string acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                if (ParseAppManifest(File.ReadAllText(acf)) is not { } m)
                {
                    continue;
                }

                string installDir = Path.Combine(steamapps, "common", m.InstallDir);
                if (Directory.Exists(installDir))
                {
                    yield return new DiscoveredGame(m.Name, GameSource.Steam, installDir, FindExecutables(installDir), m.AppId, m.LastUpdated);
                }
            }
        }
    }

    // ------------------------------------------------------------------ Epic

    public IEnumerable<DiscoveredGame> ScanEpic(string manifestsDir)
    {
        if (!Directory.Exists(manifestsDir))
        {
            yield break;
        }

        foreach (string item in Directory.EnumerateFiles(manifestsDir, "*.item"))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(item));
            JsonElement root = doc.RootElement;
            string? name = root.TryGetProperty("DisplayName", out JsonElement n) ? n.GetString() : null;
            string? dir = root.TryGetProperty("InstallLocation", out JsonElement d) ? d.GetString() : null;
            string? launch = root.TryGetProperty("LaunchExecutable", out JsonElement l) ? l.GetString() : null;
            bool isGame = !root.TryGetProperty("AppCategories", out JsonElement cats) ||
                          cats.EnumerateArray().Any(c => string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase));
            if (name is null || dir is null || !isGame || GameExeFilter.IsNonGameTitle(name) || !Directory.Exists(dir))
            {
                continue;
            }

            var exes = FindExecutables(dir).ToList();
            if (launch is not null)
            {
                string main = Path.Combine(dir, launch.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(main) && !exes.Contains(main, StringComparer.OrdinalIgnoreCase))
                {
                    exes.Insert(0, main);
                }
            }

            yield return new DiscoveredGame(name, GameSource.Epic, dir, exes);
        }
    }

    // ------------------------------------------------------------------ Xbox / Game Pass

    public IEnumerable<DiscoveredGame> ScanXbox(IEnumerable<string> driveRoots)
    {
        foreach (string drive in driveRoots)
        {
            string root = Path.Combine(drive, "XboxGames");
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string gameDir in Directory.EnumerateDirectories(root))
            {
                string content = Path.Combine(gameDir, "Content");
                string dir = Directory.Exists(content) ? content : gameDir;
                string name = Path.GetFileName(gameDir);
                var exes = new List<string>();
                string config = Path.Combine(dir, "MicrosoftGame.config");
                if (File.Exists(config))
                {
                    XDocument xml = XDocument.Load(config);
                    name = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals")
                               ?.Attribute("DefaultDisplayName")?.Value is { Length: > 0 } display && !display.StartsWith("ms-resource", StringComparison.Ordinal)
                        ? display
                        : name;
                    exes.AddRange(xml.Descendants().Where(e => e.Name.LocalName == "Executable")
                        .Select(e => e.Attribute("Name")?.Value)
                        .Where(v => !string.IsNullOrEmpty(v))
                        .Select(v => Path.Combine(dir, v!.Replace('\\', Path.DirectorySeparatorChar))));
                }

                if (exes.Count == 0)
                {
                    exes.AddRange(FindExecutables(dir));
                }

                yield return new DiscoveredGame(name, GameSource.Xbox, dir, exes);
            }
        }
    }

    // ------------------------------------------------------------------ EA app / Battle.net

    public IEnumerable<DiscoveredGame> ScanUninstallEntries(GameSource source, string[] publishers, string[] launcherNames)
    {
        foreach (RegistryViewName view in new[] { RegistryViewName.Registry64, RegistryViewName.Registry32 })
        {
            foreach (string sub in _registry.GetSubKeyNames(RegistryHiveName.HKLM, view, UninstallKey))
            {
                string key = $@"{UninstallKey}\{sub}";
                string? publisher = Read(view, key, "Publisher");
                string? name = Read(view, key, "DisplayName");
                string? location = Read(view, key, "InstallLocation")?.Trim('"');
                if (publisher is null || name is null || string.IsNullOrWhiteSpace(location) ||
                    !publishers.Any(p => publisher.Contains(p, StringComparison.OrdinalIgnoreCase)) ||
                    launcherNames.Any(l => name.StartsWith(l, StringComparison.OrdinalIgnoreCase)) ||
                    GameExeFilter.IsNonGameTitle(name) || !Directory.Exists(location))
                {
                    continue;
                }

                yield return new DiscoveredGame(name, source, location, FindExecutables(location));
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Game executables under a folder (depth-limited, never following junctions), minus helpers/redists.</summary>
    public static IReadOnlyList<string> FindExecutables(string dir, int maxDepth = 4)
    {
        var results = new List<string>();
        var pending = new Queue<(string Dir, int Depth)>();
        pending.Enqueue((dir, 0));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        while (pending.Count > 0 && results.Count < 200)
        {
            (string current, int depth) = pending.Dequeue();
            try
            {
                results.AddRange(Directory.EnumerateFiles(current, "*.exe", options).Where(f => !GameExeFilter.IsExcludedExe(Path.GetFileName(f))));
                if (depth < maxDepth)
                {
                    foreach (string sub in Directory.EnumerateDirectories(current, "*", options))
                    {
                        string leaf = Path.GetFileName(sub);
                        if (!leaf.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase) &&
                            !leaf.Equals("Redist", StringComparison.OrdinalIgnoreCase) &&
                            !leaf.Equals("__Installer", StringComparison.OrdinalIgnoreCase) &&
                            !leaf.Equals("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) &&
                            !leaf.Equals("BattlEye", StringComparison.OrdinalIgnoreCase))
                        {
                            pending.Enqueue((sub, depth + 1));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return results;
    }

    private string? Read(RegistryViewName view, string key, string value) =>
        _registry.ReadValue(new RegistryTarget(RegistryHiveName.HKLM, view, key, value))?.String;
}
