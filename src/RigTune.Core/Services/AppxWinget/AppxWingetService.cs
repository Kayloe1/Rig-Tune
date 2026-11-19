using RigTune.Core.Models;

namespace RigTune.Core.Services.AppxWinget;

/// <summary>
/// UWP/Appx bloatware manager and winget updater (SPEC §5.3). Protected packages are hard-blocked
/// here in the service (not just in the UI) unless the Danger Zone is enabled. All actions are audit-logged.
/// </summary>
public sealed class AppxWingetService
{
    private readonly IAppxPackageSource _packages;
    private readonly IWingetCli _winget;
    private readonly SettingsService _settings;
    private readonly AuditLogService _audit;

    public AppxWingetService(IAppxPackageSource packages, IWingetCli winget, SettingsService settings, AuditLogService audit)
    {
        _packages = packages;
        _winget = winget;
        _settings = settings;
        _audit = audit;
    }

    public bool IsWingetAvailable => _winget.IsAvailable;

    public async Task<IReadOnlyList<AppxPackageClassified>> ListPackagesAsync(CancellationToken cancellationToken = default) =>
        (await _packages.ListAsync(cancellationToken).ConfigureAwait(false))
        .Select(AppxClassifier.Classify)
        .OrderBy(p => p.Category)
        .ThenBy(p => p.Package.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Removes a package for the current user and optionally de-provisions it for new users.</summary>
    public async Task<AppxRemovalResult> RemoveAsync(AppxPackageInfo package, bool deprovision,
        CancellationToken cancellationToken = default)
    {
        AppxPackageClassified classified = AppxClassifier.Classify(package);
        if (classified.IsProtected && !(await _settings.GetAsync(cancellationToken).ConfigureAwait(false)).DangerZoneEnabled)
        {
            var blocked = new AppxRemovalResult(package.Name, false, false,
                $"Blocked: {classified.Reason} Enable the Danger Zone to remove protected packages.");
            await _audit.LogAsync(AuditAction.Info, $"Blocked removal of protected package {package.Name}", "systemcare.appx",
                package.FullName, blocked.Message, success: false, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return blocked;
        }

        AppxRemovalResult result;
        try
        {
            await _packages.RemoveAsync(package, cancellationToken).ConfigureAwait(false);
            bool deprovisioned = false;
            if (deprovision)
            {
                await _packages.DeprovisionAsync(package, cancellationToken).ConfigureAwait(false);
                deprovisioned = true;
            }

            result = new AppxRemovalResult(package.Name, true, deprovisioned,
                deprovisioned ? "Removed and de-provisioned for new users." : "Removed for the current user.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new AppxRemovalResult(package.Name, false, false, ex.Message);
        }

        await _audit.LogAsync(AuditAction.Uninstalled,
            $"{(result.Success ? "Removed" : "Failed to remove")} Appx package \"{package.DisplayName}\"", "systemcare.appx",
            package.FullName, $"{classified.Category}: {result.Message}", result.Success,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<WingetUpgrade>> ListUpgradesAsync(CancellationToken cancellationToken = default) =>
        WingetOutputParser.ParseUpgrades(await _winget.ListUpgradesRawAsync(cancellationToken).ConfigureAwait(false));

    public async Task<WingetResult> UpgradeAsync(WingetUpgrade upgrade, Action<string> onOutput,
        CancellationToken cancellationToken = default)
    {
        onOutput($"> winget upgrade --id {upgrade.Id} --exact --silent");
        int exit;
        try
        {
            exit = await _winget.UpgradeAsync(upgrade.Id, onOutput, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            onOutput(ex.Message);
            exit = -1;
        }

        var result = new WingetResult(upgrade.Id, exit == 0, exit,
            exit == 0 ? $"{upgrade.Name} upgraded {upgrade.Version} → {upgrade.Available}." : $"{upgrade.Name}: winget exit code 0x{exit:X8}.");
        onOutput(result.Message);
        await _audit.LogAsync(AuditAction.Updated, result.Message, "systemcare.winget", upgrade.Id,
            $"{upgrade.Version} → {upgrade.Available} ({upgrade.Source})", result.Success,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return result;
    }
}
