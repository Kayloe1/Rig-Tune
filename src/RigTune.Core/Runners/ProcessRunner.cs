using System.Diagnostics;
using System.Text;

namespace RigTune.Core.Runners;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded(params int[] okCodes) => ExitCode == 0 || okCodes.Contains(ExitCode);

    public string Describe() => $"exit {ExitCode}: {(Error.Length > 0 ? Error : Output).Trim()}";
}

/// <summary>Runs Windows command-line tools (dism, bcdedit, netsh, …) hidden, with a timeout.</summary>
public static class ProcessRunner
{
    public static CommandResult Run(string fileName, string arguments, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(ResolveSystemTool(fileName), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(5)).TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return new CommandResult(-1, stdout.ToString(), $"{fileName} timed out.");
        }

        process.WaitForExit();
        return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static CommandResult RunChecked(string fileName, string arguments, TimeSpan? timeout = null,
        params int[] okCodes)
    {
        CommandResult result = Run(fileName, arguments, timeout);
        if (!result.Succeeded(okCodes))
        {
            throw new InvalidOperationException($"{fileName} {arguments} failed ({result.Describe()}).");
        }

        return result;
    }

    /// <summary>Prefers the 64-bit System32 copy so tools are never redirected to SysWOW64.</summary>
    private static string ResolveSystemTool(string fileName)
    {
        if (Path.IsPathRooted(fileName))
        {
            return fileName;
        }

        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string candidate = Path.Combine(system32, fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + ".exe");
        return File.Exists(candidate) ? candidate : fileName;
    }
}
