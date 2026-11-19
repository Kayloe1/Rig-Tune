using System.Text.RegularExpressions;
using RigTune.Core.Models;

namespace RigTune.Core.Services.AppxWinget;

/// <summary>
/// Parses the fixed-width tables printed by <c>winget upgrade --include-unknown</c>. Column positions are
/// taken from the header line above the dashed separator (language-independent), progress-spinner and
/// carriage-return noise is removed, and the "require explicit targeting" section is recognised.
/// </summary>
public static partial class WingetOutputParser
{
    [GeneratedRegex(@"\S+")]
    private static partial Regex WordRegex();

    public static IReadOnlyList<WingetUpgrade> ParseUpgrades(string output)
    {
        var results = new List<WingetUpgrade>();
        List<string> lines = CleanLines(output);
        bool explicitSection = false;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Contains("explicit targeting", StringComparison.OrdinalIgnoreCase))
            {
                explicitSection = true;
                continue;
            }

            if (!IsSeparator(line) || i == 0)
            {
                continue;
            }

            int[] starts = WordRegex().Matches(lines[i - 1]).Select(m => m.Index).ToArray();
            if (starts.Length < 4)
            {
                continue;
            }

            for (int r = i + 1; r < lines.Count; r++)
            {
                string row = lines[r];
                if (string.IsNullOrWhiteSpace(row) || IsSeparator(row))
                {
                    break;
                }

                WingetUpgrade? upgrade = ParseRow(row, starts, explicitSection);
                if (upgrade is null)
                {
                    break; // summary line ("3 upgrades available.") ends the table
                }

                results.Add(upgrade);
                i = r;
            }
        }

        return results;
    }

    /// <summary>Removes spinner frames, progress bars and carriage-return overwrites.</summary>
    public static List<string> CleanLines(string output) =>
        output.Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
            .Select(l => l.TrimEnd())
            .Where(l => !IsNoise(l))
            .ToList();

    private static WingetUpgrade? ParseRow(string row, int[] starts, bool explicitSection)
    {
        string Column(int index)
        {
            int start = starts[index];
            if (start >= row.Length)
            {
                return string.Empty;
            }

            int end = index + 1 < starts.Length ? Math.Min(starts[index + 1], row.Length) : row.Length;
            return row[start..end].Trim();
        }

        string name = Column(0), id = Column(1), version = Column(2), available = Column(3);
        string source = starts.Length > 4 ? Column(4) : string.Empty;

        // Misaligned (wide characters in the name): fall back to splitting from the right.
        if (id.Contains(' ') || available.Length == 0)
        {
            string[] tokens = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int need = starts.Length > 4 ? 5 : 4;
            if (tokens.Length < need)
            {
                return null;
            }

            int k = tokens.Length;
            source = starts.Length > 4 ? tokens[--k] : string.Empty;
            available = tokens[--k];
            version = tokens[--k];
            id = tokens[--k];
            name = string.Join(' ', tokens.Take(k));
        }

        if (id.Length == 0 || id.Contains(' ') || available.Length == 0 || version.Length == 0)
        {
            return null;
        }

        return new WingetUpgrade(name, id, version, available, source, explicitSection);
    }

    private static bool IsSeparator(string line) => line.Length >= 10 && line.Trim().All(c => c is '-' or '─');

    private static bool IsNoise(string line)
    {
        string t = line.Trim();
        return t.Length > 0 && (t is "-" or "\\" or "|" or "/" || t.All(c => c is '█' or '▒' or ' ' or '░') ||
                                (t.Contains('█') || t.Contains('▒')) && t.EndsWith('%') || t.Contains("KB /") || t.Contains("MB /"));
    }
}
