using System.Text.RegularExpressions;
using RigTune.Core.Models;

namespace RigTune.Core.Services.Uninstaller;

/// <summary>
/// Turns an Uninstall entry into a runnable command. Silent mode is only used where it is documented and
/// reliable: the vendor's own QuietUninstallString, Windows Installer (<c>/qn</c>) and Inno Setup
/// (<c>/VERYSILENT</c>). Anything else opens the program's normal uninstaller window.
/// </summary>
public static partial class UninstallCommandBuilder
{
    /// <param name="fileExists">Resolves unquoted paths containing spaces (e.g. <c>C:\Program Files\X\uninst.exe /x</c>).</param>
    public static UninstallCommand? Build(InstalledProgram program, bool preferQuiet, Func<string, bool> fileExists)
    {
        if (program.IsMsi)
        {
            return preferQuiet
                ? new UninstallCommand("msiexec.exe", $"/x {program.ProductCode} /qn /norestart", true, "Windows Installer, silent")
                : new UninstallCommand("msiexec.exe", $"/x {program.ProductCode}", false, "Windows Installer");
        }

        if (preferQuiet && program.QuietUninstallString is { } quietLine && Split(quietLine, fileExists) is { } quiet)
        {
            return new UninstallCommand(quiet.File, quiet.Args, true, "vendor silent command");
        }

        if (program.UninstallString is not { } line || Split(line, fileExists) is not { } cmd)
        {
            return null;
        }

        if (preferQuiet && program.IsInnoSetup)
        {
            string args = Append(cmd.Args, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART");
            return new UninstallCommand(cmd.File, args, true, "Inno Setup, silent");
        }

        return new UninstallCommand(cmd.File, cmd.Args, false, "program's own uninstaller");
    }

    /// <summary>True when the program can be removed without its uninstaller window.</summary>
    public static bool SupportsQuiet(InstalledProgram program) =>
        program.IsMsi || program.QuietUninstallString is not null || program.IsInnoSetup;

    /// <summary>Splits a command line into executable + arguments; null when it is empty.</summary>
    public static (string File, string Args)? Split(string commandLine, Func<string, bool> fileExists)
    {
        string line = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (line.Length == 0)
        {
            return null;
        }

        if (line[0] == '"')
        {
            int end = line.IndexOf('"', 1);
            return end < 0
                ? (line.Trim('"'), string.Empty)
                : (line[1..end], line[(end + 1)..].Trim());
        }

        // Unquoted: take the shortest prefix ending in .exe that exists (handles spaces in the path).
        foreach (Match m in ExeEnd().Matches(line))
        {
            string candidate = line[..(m.Index + m.Length)];
            if (fileExists(candidate))
            {
                return (candidate, line[candidate.Length..].Trim());
            }
        }

        Match first = ExeEnd().Match(line);
        if (first.Success)
        {
            string file = line[..(first.Index + first.Length)];
            return (file, line[file.Length..].Trim());
        }

        int space = line.IndexOf(' ');
        return space < 0 ? (line, string.Empty) : (line[..space], line[(space + 1)..].Trim());
    }

    /// <summary>True when the uninstaller's executable is missing (broken entry).</summary>
    public static bool IsMissing(UninstallCommand command, Func<string, bool> fileExists) =>
        IsAbsoluteWindowsPath(command.FileName) && !fileExists(command.FileName);

    /// <summary>"C:\…" or "\\server\…" (checked explicitly so it behaves the same in tests on any OS).</summary>
    public static bool IsAbsoluteWindowsPath(string path) =>
        (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') ||
        path.StartsWith(@"\\", StringComparison.Ordinal);

    private static string Append(string args, params string[] switches)
    {
        var parts = new List<string> { args };
        parts.AddRange(switches.Where(s => !args.Contains(s, StringComparison.OrdinalIgnoreCase)));
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }

    [GeneratedRegex(@"\.exe(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExeEnd();
}

/// <summary>Programs other software depends on. They can still be removed, but get a warning.</summary>
public static class UninstallCautions
{
    private static readonly string[] SharedPatterns =
    {
        "Microsoft Visual C++*", "Microsoft .NET*", "Microsoft Windows Desktop Runtime*", "Microsoft ASP.NET Core*",
        "Microsoft Edge*", "Microsoft Update Health Tools", "Windows Software Development Kit*", "Windows SDK*",
        "NVIDIA Graphics Driver*", "NVIDIA PhysX*", "NVIDIA HD Audio*", "NVIDIA USBC*", "NVIDIA FrameView SDK*",
        "Intel(R)*", "Intel *Driver*", "Intel *Management Engine*", "Realtek*", "DirectX*", "Microsoft GameInput*",
    };

    private static readonly string[] LauncherPatterns =
    {
        "Steam", "EA app", "Epic Games Launcher", "Ubisoft Connect", "Battle.net", "GOG GALAXY*",
    };

    /// <summary>A warning to show for this program, or null.</summary>
    public static string? Warning(InstalledProgram program)
    {
        if (AppxWinget.AppxClassifier.Matches(program.DisplayName, SharedPatterns) is not null)
        {
            return "Shared runtime or driver: other programs or games may need it.";
        }

        return AppxWinget.AppxClassifier.Matches(program.DisplayName, LauncherPatterns) is not null
            ? "Game launcher: uninstalling it can also remove the games installed through it."
            : null;
    }
}
