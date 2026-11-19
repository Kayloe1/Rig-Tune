using RigTune.Core.Models;
using RigTune.Core.Services;
using RigTune.Core.Services.AppxWinget;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

public sealed class AppxWingetTests : IDisposable
{
    private readonly SnapshotHarness _h = new();
    private readonly FakeAppxSource _source = new();
    private readonly FakeWinget _winget = new();
    private readonly SettingsService _settings;
    private readonly AppxWingetService _service;

    public AppxWingetTests()
    {
        _settings = new SettingsService(_h.Paths);
        _service = new AppxWingetService(_source, _winget, _settings, _h.Audit);
    }

    private static AppxPackageInfo Pkg(string name, string signature = "Store", bool framework = false) =>
        new(name, $"{name}_1.0.0.0_x64__8wekyb3d8bbwe", $"{name}_8wekyb3d8bbwe", name, "CN=Microsoft", "1.0.0.0", signature, framework);

    // ------------------------------------------------------------------ classifier

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.StorePurchaseApp")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.Windows.ShellExperienceHost")]
    [InlineData("Microsoft.Windows.StartMenuExperienceHost")]
    [InlineData("MicrosoftWindows.Client.CBS")]
    [InlineData("Microsoft.WebView2Runtime")]
    [InlineData("Microsoft.Win32WebViewHost")]
    [InlineData("Microsoft.WindowsAppRuntime.1.6")]
    [InlineData("Microsoft.VCLibs.140.00.UWPDesktop")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("NVIDIACorp.NVIDIAControlPanel")]
    [InlineData("SomeVendor.NVIDIAAppHelper")]
    [InlineData("RealtekSemiconductorCorp.RealtekAudioControl")]
    [InlineData("DolbyLaboratories.DolbyAccess")]
    [InlineData("Contoso.SpatialAudioPanel")]
    public void Critical_packages_are_always_protected(string name)
    {
        Assert.Equal(AppxCategory.Protected, AppxClassifier.Classify(Pkg(name)).Category);
        Assert.Equal(AppxCategory.Protected, AppxClassifier.Classify(Pkg(name, "Developer")).Category);
    }

    [Fact]
    public void Frameworks_and_system_signed_packages_are_protected()
    {
        Assert.Equal(AppxCategory.Protected, AppxClassifier.Classify(Pkg("Contoso.Framework", framework: true)).Category);
        Assert.Equal(AppxCategory.Protected, AppxClassifier.Classify(Pkg("Microsoft.BingNews", "System")).Category);
    }

    [Theory]
    [InlineData("Clipchamp.Clipchamp")]
    [InlineData("Microsoft.BingNews")]
    [InlineData("Microsoft.BingWeather")]
    [InlineData("Microsoft.MicrosoftSolitaireCollection")]
    [InlineData("Microsoft.Windows.DevHome")]
    [InlineData("Microsoft.WindowsFeedbackHub")]
    [InlineData("Microsoft.People")]
    [InlineData("Microsoft.WindowsSoundRecorder")]
    public void Spec_bloatware_is_safe_to_remove(string name) =>
        Assert.Equal(AppxCategory.SafeToRemove, AppxClassifier.Classify(Pkg(name)).Category);

    [Theory]
    [InlineData("Microsoft.Windows.Photos")]
    [InlineData("Microsoft.WindowsCamera")]
    [InlineData("Microsoft.WindowsCalculator")]
    [InlineData("Microsoft.Paint")]
    [InlineData("Microsoft.GamingApp")]
    [InlineData("Microsoft.XboxGamingOverlay")]
    public void Spec_optional_apps_are_optional(string name) =>
        Assert.Equal(AppxCategory.Optional, AppxClassifier.Classify(Pkg(name)).Category);

    [Fact]
    public void No_safe_or_optional_pattern_overlaps_a_protected_pattern()
    {
        foreach (string safe in AppxClassifier.SafeToRemovePatterns.Concat(AppxClassifier.OptionalPatterns).Where(p => !p.Contains('*')))
        {
            Assert.Null(AppxClassifier.Matches(safe, AppxClassifier.ProtectedPatterns));
        }
    }

    [Theory]
    [InlineData("Microsoft.VCLibs.140.00", "Microsoft.VCLibs*", true)]
    [InlineData("Contoso.Audio.Panel", "*Audio*", true)]
    [InlineData("Microsoft.HEIFImageExtension", "Microsoft.*Extension", false)]
    [InlineData("microsoft.windowsstore", "Microsoft.WindowsStore", true)]
    [InlineData("Microsoft.WindowsStoreX", "Microsoft.WindowsStore", false)]
    public void Pattern_matching(string name, string pattern, bool expected) =>
        Assert.Equal(expected, AppxClassifier.IsMatch(name, pattern));

    // ------------------------------------------------------------------ removal guard

    [Fact]
    public async Task Protected_packages_are_hard_blocked_unless_danger_zone_is_on()
    {
        AppxRemovalResult blocked = await _service.RemoveAsync(Pkg("Microsoft.WindowsStore"), deprovision: true);

        Assert.False(blocked.Success);
        Assert.Empty(_source.Removed);
        Assert.Empty(_source.Deprovisioned);

        await _settings.UpdateAsync(s => s.DangerZoneEnabled = true);
        AppxRemovalResult allowed = await _service.RemoveAsync(Pkg("Microsoft.WindowsStore"), deprovision: false);
        Assert.True(allowed.Success);
        Assert.Equal(new[] { "Microsoft.WindowsStore" }, _source.Removed);
    }

    [Fact]
    public async Task Safe_package_removal_with_deprovision_is_audited()
    {
        AppxRemovalResult result = await _service.RemoveAsync(Pkg("Clipchamp.Clipchamp"), deprovision: true);

        Assert.True(result.Success);
        Assert.True(result.Deprovisioned);
        Assert.Equal(new[] { "Clipchamp.Clipchamp_8wekyb3d8bbwe" }, _source.Deprovisioned);
        Assert.Contains(await _h.Audit.GetAllAsync(), e => e.Action == AuditAction.Uninstalled && e.Success);
    }

    [Fact]
    public async Task Listing_groups_and_orders_by_category()
    {
        _source.Packages.AddRange(new[] { Pkg("Microsoft.WindowsStore"), Pkg("Microsoft.BingNews"), Pkg("Microsoft.Paint"), Pkg("Valve.SteamHelper") });

        IReadOnlyList<AppxPackageClassified> list = await _service.ListPackagesAsync();

        Assert.Equal(new[] { AppxCategory.SafeToRemove, AppxCategory.Optional, AppxCategory.Protected, AppxCategory.Other },
            list.Select(p => p.Category));
    }

    // ------------------------------------------------------------------ winget

    private const string SampleOutput =
        "   - \r   \\ \r   | \r\n" +
        "  ██████████████████████████████  1024 KB / 1024 KB\r\n" +
        "Name                          Id                      Version      Available    Source\r\n" +
        "--------------------------------------------------------------------------------------\r\n" +
        "Discord                       Discord.Discord         1.0.9150     1.0.9170     winget\r\n" +
        "Mozilla Firefox (x64 en-US)   Mozilla.Firefox         119.0        120.0.1      winget\r\n" +
        "Steam                         Valve.Steam             Unknown      2.10.91.91   winget\r\n" +
        "NVIDIA App with a very long … NVIDIA.NVIDIAApp        < 11.0.1     11.0.2.312   winget\r\n" +
        "4 upgrades available.\r\n" +
        "\r\n" +
        "The following packages have an upgrade available, but require explicit targeting for upgrade:\r\n" +
        "Name    Id               Version Available Source\r\n" +
        "-------------------------------------------------\r\n" +
        "Spotify Spotify.Spotify  1.2.25  1.2.26    winget\r\n";

    [Fact]
    public void Parses_winget_upgrade_table_with_spinner_noise_and_explicit_section()
    {
        IReadOnlyList<WingetUpgrade> upgrades = WingetOutputParser.ParseUpgrades(SampleOutput);

        Assert.Equal(new[] { "Discord.Discord", "Mozilla.Firefox", "Valve.Steam", "NVIDIA.NVIDIAApp", "Spotify.Spotify" },
            upgrades.Select(u => u.Id));
        WingetUpgrade firefox = upgrades[1];
        Assert.Equal("Mozilla Firefox (x64 en-US)", firefox.Name);
        Assert.Equal("119.0", firefox.Version);
        Assert.Equal("120.0.1", firefox.Available);
        Assert.Equal("winget", firefox.Source);
        Assert.Equal("Unknown", upgrades[2].Version);
        Assert.Equal("< 11.0.1", upgrades[3].Version);
        Assert.False(upgrades[0].RequiresExplicitTargeting);
        Assert.True(upgrades[4].RequiresExplicitTargeting);
    }

    [Fact]
    public void Parses_localized_headers_by_column_position()
    {
        const string german =
            "Name       ID               Version  Verfügbar  Quelle\n" +
            "--------------------------------------------------------\n" +
            "7-Zip      7zip.7zip        23.01    24.08      winget\n" +
            "1 Aktualisierungen verfügbar.\n";

        WingetUpgrade u = Assert.Single(WingetOutputParser.ParseUpgrades(german));

        Assert.Equal(("7-Zip", "7zip.7zip", "23.01", "24.08"), (u.Name, u.Id, u.Version, u.Available));
    }

    [Fact]
    public void Wide_character_rows_fall_back_to_right_to_left_tokens()
    {
        const string output =
            "Name          Id              Version Available Source\n" +
            "------------------------------------------------------\n" +
            "网易云音乐 Pro    NetEase.Music  2.10.1  3.0.0     winget\n";

        WingetUpgrade u = Assert.Single(WingetOutputParser.ParseUpgrades(output));

        Assert.Equal("NetEase.Music", u.Id);
        Assert.Equal("3.0.0", u.Available);
    }

    [Theory]
    [InlineData("No installed package found matching input criteria.\r\n")]
    [InlineData("")]
    public void No_upgrades_yields_empty_list(string output) => Assert.Empty(WingetOutputParser.ParseUpgrades(output));

    [Fact]
    public async Task Upgrade_streams_output_and_is_audited()
    {
        var lines = new List<string>();
        _winget.ListOutput = SampleOutput;
        WingetUpgrade discord = (await _service.ListUpgradesAsync())[0];

        WingetResult result = await _service.UpgradeAsync(discord, lines.Add);

        Assert.True(result.Success);
        Assert.Equal(new[] { "Discord.Discord" }, _winget.Upgraded);
        Assert.Contains("Successfully installed", lines);
        Assert.Contains(await _h.Audit.GetAllAsync(), e => e.Action == AuditAction.Updated && e.Target == "Discord.Discord");
    }

    public void Dispose() => _h.Dispose();
}
