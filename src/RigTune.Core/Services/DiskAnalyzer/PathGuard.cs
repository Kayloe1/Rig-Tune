using RigTune.Core.Models;

namespace RigTune.Core.Services.DiskAnalyzer;

/// <summary>
/// Disk Analyzer deletion safety (SPEC §5.6). Hard-protected locations can never be deleted, nor can any
/// folder that contains them; user "Important" locks protect the locked path, everything inside it, and
/// every ancestor whose deletion would take the locked path with it. Comparisons are case-insensitive
/// on normalized Windows paths (separators unified, "." and ".." resolved, trailing separators removed).
/// </summary>
public sealed class PathGuard
{
    private static readonly string[] RootFiles = { "pagefile.sys", "swapfile.sys", "hiberfil.sys", "DumpStack.log", "DumpStack.log.tmp" };
    private static readonly string[] ProtectedSegments = { "System Volume Information", "$Recycle.Bin" };

    private readonly List<string> _subtrees;
    private readonly List<string> _exact;
    private readonly string _usersRoot;

    public PathGuard(string windowsDir, string programFiles, string? programFilesX86, string programData, string usersRoot)
    {
        _usersRoot = Normalize(usersRoot);
        _subtrees = new List<string> { Normalize(windowsDir), Normalize(programFiles) + @"\WindowsApps" };
        _exact = new List<string> { Normalize(programFiles), Normalize(programData), _usersRoot };
        if (!string.IsNullOrEmpty(programFilesX86))
        {
            _exact.Add(Normalize(programFilesX86));
        }
    }

    public static PathGuard ForThisMachine()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new PathGuard(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetDirectoryName(profile) ?? @"C:\Users");
    }

    /// <summary>Canonical form: backslashes, no duplicate/trailing separators, "." and ".." resolved.</summary>
    public static string Normalize(string path)
    {
        string p = path.Trim().Trim('"').Replace('/', '\\');
        bool unc = p.StartsWith(@"\\", StringComparison.Ordinal);
        var parts = new List<string>();
        foreach (string segment in p.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count > 1)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(segment);
        }

        string joined = string.Join('\\', parts);
        if (unc)
        {
            return @"\\" + joined;
        }

        if (p.StartsWith('\\'))
        {
            return "\\" + joined;
        }

        return parts.Count == 1 && joined.EndsWith(':') ? joined + "\\" : joined;
    }

    public static bool IsSameOrUnder(string path, string root)
    {
        string p = Normalize(path), r = Normalize(root).TrimEnd('\\');
        return string.Equals(p.TrimEnd('\\'), r, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDriveRoot(string path)
    {
        string p = Normalize(path);
        return (p.Length == 3 && p[1] == ':' && p[2] == '\\') || p == "\\";
    }

    /// <summary>Why a path is hard-protected, or null when it is not.</summary>
    public string? ProtectionReason(string path)
    {
        string p = Normalize(path);
        if (IsDriveRoot(p))
        {
            return "Drive roots can't be deleted.";
        }

        foreach (string root in _subtrees)
        {
            if (IsSameOrUnder(p, root))
            {
                return $"Inside a protected system location ({root}).";
            }

            if (IsSameOrUnder(root, p))
            {
                return $"Contains a protected system location ({root}).";
            }
        }

        foreach (string exact in _exact)
        {
            if (string.Equals(p, exact, StringComparison.OrdinalIgnoreCase))
            {
                return $"{exact} is a protected system folder.";
            }
        }

        string[] segments = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => ProtectedSegments.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            return "System Volume Information and $Recycle.Bin are managed by Windows.";
        }

        if (segments.Length == 2 && segments[0].EndsWith(':') && RootFiles.Contains(segments[1], StringComparer.OrdinalIgnoreCase))
        {
            return $"{segments[1]} is a Windows memory/paging file.";
        }

        if (IsSameOrUnder(p, _usersRoot))
        {
            string relative = p[_usersRoot.Length..].TrimStart('\\');
            string[] rel = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (rel.Length == 1)
            {
                return "User profile folders can't be deleted.";
            }

            if (rel.Length == 2 && rel[1].Equals("AppData", StringComparison.OrdinalIgnoreCase))
            {
                return "A profile's AppData folder can't be deleted.";
            }
        }

        return null;
    }

    public bool IsHardProtected(string path) => ProtectionReason(path) is not null;

    /// <summary>Full deletion check: hard protection, then user locks (the path, its ancestors and descendants).</summary>
    public DeletionCheck Evaluate(string path, IEnumerable<string> lockedPaths)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new DeletionCheck(false, "No path.");
        }

        if (ProtectionReason(path) is { } reason)
        {
            return new DeletionCheck(false, reason);
        }

        foreach (string locked in lockedPaths)
        {
            if (IsSameOrUnder(path, locked))
            {
                return new DeletionCheck(false, $"Marked as Important ({Normalize(locked)}). Unmark it first.");
            }

            if (IsSameOrUnder(locked, path))
            {
                return new DeletionCheck(false, $"Contains an Important item ({Normalize(locked)}). Unmark it first.");
            }
        }

        return DeletionCheck.Ok;
    }

    /// <summary>True when the path itself or one of its ancestors is locked.</summary>
    public static bool IsLocked(string path, IEnumerable<string> lockedPaths) =>
        lockedPaths.Any(l => IsSameOrUnder(path, l));
}
