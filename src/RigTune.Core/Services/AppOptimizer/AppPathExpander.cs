using System.Text.RegularExpressions;

namespace RigTune.Core.Services.AppOptimizer;

/// <summary>
/// Expands catalog paths: <c>%ENVVAR%</c> tokens, then '*'/'?' wildcards in any segment
/// (e.g. <c>%LOCALAPPDATA%\Google\Chrome\User Data\*\Cache</c>). Wildcard directories never
/// traverse junctions/symlinks.
/// </summary>
public static partial class AppPathExpander
{
    /// <summary>Environment variables catalogs may use.</summary>
    public static readonly IReadOnlySet<string> AllowedVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "LOCALAPPDATA", "APPDATA", "PROGRAMDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "USERPROFILE", "TEMP",
    };

    [GeneratedRegex(@"%([^%]+)%")]
    private static partial Regex VariableRegex();

    public static IReadOnlyList<string> VariablesIn(string pattern) =>
        VariableRegex().Matches(pattern).Select(m => m.Groups[1].Value).ToList();

    /// <summary>Replaces %VAR% tokens; returns null if a variable is unknown.</summary>
    public static string? ExpandVariables(string pattern, Func<string, string?>? lookup = null)
    {
        lookup ??= Environment.GetEnvironmentVariable;
        bool missing = false;
        string result = VariableRegex().Replace(pattern, m =>
        {
            string? value = lookup(m.Groups[1].Value);
            if (string.IsNullOrEmpty(value))
            {
                missing = true;
                return m.Value;
            }

            return value;
        });
        return missing ? null : result;
    }

    /// <summary>Expands variables and converts separators to the platform's (catalogs use '\').</summary>
    public static string? ExpandFile(string pattern, Func<string, string?>? lookup = null) =>
        ExpandVariables(pattern, lookup)?.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// Turns an absolute path picked by the user into a portable catalog path by replacing the longest
    /// matching known folder with its %VARIABLE% (e.g. C:\Program Files\OBS → %PROGRAMFILES%\OBS).
    /// </summary>
    public static string Contract(string path, Func<string, string?>? lookup = null)
    {
        lookup ??= Environment.GetEnvironmentVariable;
        string trimmed = path.Trim().Trim('"');
        (string Var, string Value)? best = null;
        foreach (string variable in AllowedVariables)
        {
            string? value = lookup(variable)?.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(value) || !trimmed.StartsWith(value, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool boundary = trimmed.Length == value.Length || trimmed[value.Length] is '\\' or '/';
            if (boundary && (best is null || value.Length > best.Value.Value.Length))
            {
                best = (variable, value);
            }
        }

        return best is { } b ? $"%{b.Var}%{trimmed[b.Value.Length..]}" : trimmed;
    }

    /// <summary>Last path segment for either separator style.</summary>
    public static string FileName(string path) => path.Split('\\', '/').Last(s => s.Length > 0);

    /// <summary>Existing files/directories matching the pattern.</summary>
    public static IReadOnlyList<string> Expand(string pattern, Func<string, string?>? lookup = null)
    {
        string? expanded = ExpandVariables(pattern, lookup);
        if (expanded is null)
        {
            return Array.Empty<string>();
        }

        string[] segments = expanded.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return Array.Empty<string>();
        }

        string root = expanded.StartsWith('/') ? "/" + segments[0]
            : segments[0].EndsWith(':') ? segments[0] + Path.DirectorySeparatorChar
            : segments[0];
        var current = new List<string> { root };
        for (int i = 1; i < segments.Length; i++)
        {
            bool last = i == segments.Length - 1;
            string seg = segments[i];
            var next = new List<string>();
            foreach (string dir in current.Where(Directory.Exists))
            {
                if (seg.IndexOfAny(new[] { '*', '?' }) < 0)
                {
                    string candidate = Path.Combine(dir, seg);
                    if (Directory.Exists(candidate) || (last && File.Exists(candidate)))
                    {
                        next.Add(candidate);
                    }

                    continue;
                }

                var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                try
                {
                    next.AddRange(Directory.EnumerateDirectories(dir, seg, options));
                    if (last)
                    {
                        next.AddRange(Directory.EnumerateFiles(dir, seg, options));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            current = next;
            if (current.Count == 0)
            {
                break;
            }
        }

        return segments.Length == 1
            ? current.Where(p => Directory.Exists(p) || File.Exists(p)).ToList()
            : current;
    }
}
