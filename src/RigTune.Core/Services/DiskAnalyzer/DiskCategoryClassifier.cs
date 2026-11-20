using RigTune.Core.Models;

namespace RigTune.Core.Services.DiskAnalyzer;

/// <summary>Assigns folders/files to Games, Media, Apps, System or Other (SPEC §5.6 breakdown bar).</summary>
public sealed class DiskCategoryClassifier
{
    private static readonly string[] GameSegments =
    {
        "steamapps", "XboxGames", "Epic Games", "EA Games", "Riot Games", "GOG Games", "GOG Galaxy\\Games",
        "Ubisoft Game Launcher\\games", "Battle.net Games", "Games",
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".ts", ".jpg", ".jpeg", ".png", ".gif", ".heic", ".webp",
        ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".psd", ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus",
    };

    private static readonly string[] MediaFolders = { "Videos", "Pictures", "Music", "Captures" };

    private readonly string _windows;
    private readonly string[] _apps;
    private readonly string _users;
    private readonly IReadOnlyList<string> _gameRoots;

    public DiskCategoryClassifier(string windowsDir, IEnumerable<string> appRoots, string usersRoot, IEnumerable<string>? gameRoots = null)
    {
        _windows = PathGuard.Normalize(windowsDir);
        _apps = appRoots.Where(r => !string.IsNullOrEmpty(r)).Select(PathGuard.Normalize).ToArray();
        _users = PathGuard.Normalize(usersRoot);
        _gameRoots = (gameRoots ?? Array.Empty<string>()).Select(PathGuard.Normalize).ToList();
    }

    public static DiskCategoryClassifier ForThisMachine(IEnumerable<string>? gameRoots = null)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new DiskCategoryClassifier(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            },
            Path.GetDirectoryName(profile) ?? @"C:\Users",
            gameRoots);
    }

    public DiskCategory ClassifyDirectory(string path, DiskCategory parent)
    {
        if (parent is DiskCategory.System or DiskCategory.Games)
        {
            return parent;
        }

        string p = PathGuard.Normalize(path);
        string[] segments = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (PathGuard.IsSameOrUnder(p, _windows) || p.EndsWith(@"\Windows.old", StringComparison.OrdinalIgnoreCase) ||
            segments.Any(s => s.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                              s.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) ||
            (segments.Length == 2 && segments[0].EndsWith(':') && segments[1].Equals("Recovery", StringComparison.OrdinalIgnoreCase)))
        {
            return DiskCategory.System;
        }

        if (_gameRoots.Any(g => PathGuard.IsSameOrUnder(p, g)) || IsGameSegmentPath(p, segments))
        {
            return DiskCategory.Games;
        }

        if (parent != DiskCategory.Other)
        {
            return parent;
        }

        // Only a profile's own AppData folder (Users\<name>\AppData) counts as Apps; an "AppData"
        // segment elsewhere in the path (or a user's temp dir holding unrelated files) does not.
        if (_apps.Any(a => PathGuard.IsSameOrUnder(p, a)) || IsProfileAppData(p))
        {
            return DiskCategory.Apps;
        }

        if (PathGuard.IsSameOrUnder(p, _users))
        {
            string[] rel = p[_users.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (rel.Length >= 2 && MediaFolders.Contains(rel[1], StringComparer.OrdinalIgnoreCase))
            {
                return DiskCategory.Media;
            }
        }

        return DiskCategory.Other;
    }

    public DiskCategory ClassifyFile(string fileName, DiskCategory folderCategory)
    {
        string name = fileName.Split('\\', '/').Last();
        if (folderCategory == DiskCategory.Other)
        {
            if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
            {
                return DiskCategory.System;
            }

            if (MediaExtensions.Contains(Path.GetExtension(name)))
            {
                return DiskCategory.Media;
            }
        }

        return folderCategory;
    }

    private bool IsProfileAppData(string p)
    {
        if (!PathGuard.IsSameOrUnder(p, _users))
        {
            return false;
        }

        string[] rel = p[_users.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return rel.Length >= 2 && rel[1].Equals("AppData", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGameSegmentPath(string p, string[] segments)
    {
        if (segments.Any(s => s.Equals("steamapps", StringComparison.OrdinalIgnoreCase) || s.Equals("XboxGames", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // "<drive>:\Games", "...\Epic Games", "...\EA Games" etc. as a folder name anywhere.
        return GameSegments.Where(g => !g.Contains('\\')).Any(g => segments.Any(s => s.Equals(g, StringComparison.OrdinalIgnoreCase))) ||
               GameSegments.Where(g => g.Contains('\\')).Any(g => p.Contains("\\" + g, StringComparison.OrdinalIgnoreCase));
    }
}
