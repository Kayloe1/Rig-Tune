using System.Diagnostics;
using System.Text;
using RigTune.Core.Models;
using Windows.Management.Deployment;

namespace RigTune.Core.Services.AppxWinget;

/// <summary>Installed Store/UWP packages (abstracted for tests).</summary>
public interface IAppxPackageSource
{
    Task<IReadOnlyList<AppxPackageInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task RemoveAsync(AppxPackageInfo package, CancellationToken cancellationToken = default);

    Task DeprovisionAsync(AppxPackageInfo package, CancellationToken cancellationToken = default);
}

/// <summary>Windows.Management.Deployment.PackageManager implementation (current user).</summary>
public sealed class WinRtAppxPackageSource : IAppxPackageSource
{
    public Task<IReadOnlyList<AppxPackageInfo>> ListAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var manager = new PackageManager();
        var list = new List<AppxPackageInfo>();
        foreach (Windows.ApplicationModel.Package p in manager.FindPackagesForUser(string.Empty))
        {
            try
            {
                if (p.IsResourcePackage)
                {
                    continue;
                }

                string displayName;
                try
                {
                    displayName = p.DisplayName;
                }
                catch (Exception)
                {
                    displayName = p.Id.Name;
                }

                Windows.ApplicationModel.PackageVersion v = p.Id.Version;
                list.Add(new AppxPackageInfo(p.Id.Name, p.Id.FullName, p.Id.FamilyName,
                    string.IsNullOrWhiteSpace(displayName) ? p.Id.Name : displayName, p.Id.Publisher,
                    $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}", p.SignatureKind.ToString(), p.IsFramework));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                // Packages we cannot inspect are skipped.
            }
        }

        return (IReadOnlyList<AppxPackageInfo>)list
            .GroupBy(p => p.FullName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }, cancellationToken);

    public async Task RemoveAsync(AppxPackageInfo package, CancellationToken cancellationToken = default)
    {
        DeploymentResult result = await new PackageManager().RemovePackageAsync(package.FullName);
        if (result.ExtendedErrorCode is { } error && error.HResult != 0)
        {
            throw new InvalidOperationException(result.ErrorText);
        }
    }

    public async Task DeprovisionAsync(AppxPackageInfo package, CancellationToken cancellationToken = default)
    {
        DeploymentResult result = await new PackageManager().DeprovisionPackageForAllUsersAsync(package.FamilyName);
        if (result.ExtendedErrorCode is { } error && error.HResult != 0)
        {
            throw new InvalidOperationException(result.ErrorText);
        }
    }
}

/// <summary>Thin wrapper around the winget CLI (abstracted for tests).</summary>
public interface IWingetCli
{
    bool IsAvailable { get; }

    Task<string> ListUpgradesRawAsync(CancellationToken cancellationToken = default);

    Task<int> UpgradeAsync(string id, Action<string> onOutput, CancellationToken cancellationToken = default);
}

public sealed class WingetCli : IWingetCli
{
    private const string CommonArgs = "--accept-source-agreements --disable-interactivity";

    public bool IsAvailable => ResolvePath() is not null;

    public async Task<string> ListUpgradesRawAsync(CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        await RunAsync($"upgrade --include-unknown {CommonArgs}", line => output.AppendLine(line), cancellationToken).ConfigureAwait(false);
        return output.ToString();
    }

    public Task<int> UpgradeAsync(string id, Action<string> onOutput, CancellationToken cancellationToken = default)
    {
        string safeId = new(id.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+').ToArray());
        return RunAsync($"upgrade --id {safeId} --exact --silent --accept-package-agreements {CommonArgs}", onOutput, cancellationToken);
    }

    private static async Task<int> RunAsync(string arguments, Action<string> onOutput, CancellationToken cancellationToken)
    {
        string exe = ResolvePath() ?? throw new InvalidOperationException("winget (App Installer) is not installed.");
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { onOutput(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { onOutput(e.Data); } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        return process.ExitCode;
    }

    private static string? ResolvePath()
    {
        string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias))
        {
            return alias;
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}
