using RigTune.Core.Models;
using RigTune.Core.Services.AppxWinget;

namespace RigTune.Core.Services.GameMode;

/// <summary>Filters out launchers, redistributables, installers, crash handlers and anti-cheat services.</summary>
public static class GameExeFilter
{
    public static readonly IReadOnlyList<string> ExcludedPatterns = new[]
    {
        "unins*", "*setup*", "*install*", "*crash*", "*report*", "*redist*", "vcredist*", "dxsetup*", "dotnet*",
        "*prereq*", "easyanticheat*", "eac*launcher*", "beservice*", "battleye*", "*updater*", "*update.exe", "*launcher*",
        "*helper*", "cefsharp*", "qtwebengineprocess*", "*config*", "*server*", "*overlay*", "*touchup*", "cleanup*",
        "*debug*", "*diagnostic*", "*benchmark*tool*", "physx*", "oalinst*", "*uploader*", "*vc_redist*", "dxwebsetup*",
        "steamerrorreporter*", "*cefprocess*", "*_be.exe", "*_eac.exe",
    };

    /// <summary>Steam / store entries that are tools, not games (they would wrongly trigger Game Mode).</summary>
    public static readonly IReadOnlyList<string> NonGameTitles = new[]
    {
        "Steamworks Common Redistributables", "*Redistributable*", "Proton*", "Steam Linux Runtime*", "SteamVR*",
        "Wallpaper Engine*", "Spacewar", "Steamworks*", "*Dedicated Server*", "*SDK*",
    };

    public static bool IsExcludedExe(string exeName)
    {
        string name = Path.GetFileName(exeName.Replace('\\', Path.DirectorySeparatorChar));
        return ExcludedPatterns.Any(p => AppxClassifier.IsMatch(name, p));
    }

    public static bool IsNonGameTitle(string title) => NonGameTitles.Any(p => AppxClassifier.IsMatch(title, p));
}

/// <summary>
/// Decides whether a started process is a game: exclusion list wins, then the custom allowlist
/// (exe name or full path), then discovered games (exact exe or any non-excluded exe inside an install folder).
/// </summary>
public sealed class GameMatcher
{
    private readonly List<DiscoveredGame> _games;
    private readonly HashSet<string> _allowNames;
    private readonly HashSet<string> _allowPaths;
    private readonly HashSet<string> _excluded;

    public GameMatcher(IEnumerable<DiscoveredGame> games, IEnumerable<string> allowlist, IEnumerable<string> exclusions)
    {
        _games = games.ToList();
        List<string> allow = allowlist.Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        _allowPaths = allow.Where(a => a.Contains('\\') || a.Contains('/')).Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _allowNames = allow.Where(a => !a.Contains('\\') && !a.Contains('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _excluded = exclusions.Select(e => e.Trim()).Where(e => e.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The matching game title, or null when the process is not a game.</summary>
    public string? Match(string processName, string? fullPath)
    {
        string exe = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe";
        if (_excluded.Contains(exe) || (fullPath is not null && _excluded.Contains(Normalize(fullPath))))
        {
            return null;
        }

        if (_allowNames.Contains(exe) || (fullPath is not null && _allowPaths.Contains(Normalize(fullPath))))
        {
            return Path.GetFileNameWithoutExtension(exe);
        }

        if (fullPath is null || GameExeFilter.IsExcludedExe(exe))
        {
            return null;
        }

        string path = Normalize(fullPath);
        foreach (DiscoveredGame game in _games)
        {
            if (game.Executables.Any(e => string.Equals(Normalize(e), path, StringComparison.OrdinalIgnoreCase)))
            {
                return game.Name;
            }

            string dir = Normalize(game.InstallDir).TrimEnd('\\') + "\\";
            if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            {
                return game.Name;
            }
        }

        return null;
    }

    private static string Normalize(string path) => path.Replace('/', '\\').Trim().Trim('"');
}
