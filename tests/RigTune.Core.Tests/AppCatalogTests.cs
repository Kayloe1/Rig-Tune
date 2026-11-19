using System.Text.Json;
using RigTune.Core.Models;
using RigTune.Core.Services.AppOptimizer;
using RigTune.Core.Storage;

namespace RigTune.Core.Tests;

/// <summary>Validates the bundled Catalogs\apps.json (SPEC §4.2, §7).</summary>
public sealed class AppCatalogTests
{
    private static readonly AppCatalog Catalog = AppOptimizerService.LoadFile(RigTunePaths.BundledAppsFile);

    [Fact]
    public void Catalog_is_copied_to_output_and_passes_full_validation()
    {
        Assert.True(File.Exists(RigTunePaths.BundledAppsFile));
        Assert.Empty(AppCatalogValidator.Validate(Catalog));
    }

    [Fact]
    public void Exactly_38_apps_and_118_actions()
    {
        Assert.Equal(38, Catalog.Apps.Count);
        Assert.Equal(118, Catalog.Apps.Sum(a => a.ActionCount));
    }

    [Fact]
    public void App_ids_are_unique() =>
        Assert.Equal(38, Catalog.Apps.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Theory]
    [InlineData("Browsers", 7)]
    [InlineData("Chat & Conferencing", 6)]
    [InlineData("Cloud Sync", 4)]
    [InlineData("Game Launchers", 12)]
    [InlineData("GPU & Capture", 2)]
    [InlineData("Peripherals", 4)]
    [InlineData("Media & Utilities", 3)]
    public void Category_counts_match_spec(string category, int expected) =>
        Assert.Equal(expected, Catalog.Apps.Count(a => a.Category == category));

    [Theory]
    [InlineData("chrome", 6)]
    [InlineData("edge", 8)]
    [InlineData("firefox", 5)]
    [InlineData("brave", 6)]
    [InlineData("opera", 4)]
    [InlineData("opera_gx", 4)]
    [InlineData("vivaldi", 3)]
    [InlineData("discord", 3)]
    [InlineData("telegram", 3)]
    [InlineData("whatsapp", 3)]
    [InlineData("slack", 3)]
    [InlineData("teams", 3)]
    [InlineData("zoom", 3)]
    [InlineData("onedrive", 3)]
    [InlineData("google_drive", 3)]
    [InlineData("dropbox", 3)]
    [InlineData("icloud", 2)]
    [InlineData("steam", 3)]
    [InlineData("xbox", 3)]
    [InlineData("ea_app", 3)]
    [InlineData("battlenet", 3)]
    [InlineData("epic", 3)]
    [InlineData("ubisoft", 3)]
    [InlineData("riot", 3)]
    [InlineData("gog", 3)]
    [InlineData("rockstar", 2)]
    [InlineData("twok", 2)]
    [InlineData("paradox", 2)]
    [InlineData("minecraft", 2)]
    [InlineData("nvidia_app", 4)]
    [InlineData("overwolf", 2)]
    [InlineData("razer_synapse", 2)]
    [InlineData("ghub", 2)]
    [InlineData("icue", 2)]
    [InlineData("steelseries_gg", 2)]
    [InlineData("spotify", 3)]
    [InlineData("adobe_cc", 3)]
    [InlineData("wallpaper_engine", 1)]
    public void Per_app_action_counts_match_spec(string id, int actions) =>
        Assert.Equal(actions, Catalog.Apps.Single(a => a.Id == id).ActionCount);

    [Fact]
    public void Zero_amd_entries()
    {
        string json = File.ReadAllText(RigTunePaths.BundledAppsFile);
        Assert.DoesNotMatch(@"\bAMD\b", json);
        Assert.DoesNotContain("Radeon", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Adrenalin", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cookies_and_history_are_never_checked_by_default_and_warn_about_sign_out()
    {
        List<AppCache> sensitive = Catalog.Apps.SelectMany(a => a.Caches).Where(c => c.IsSensitive).ToList();

        Assert.True(sensitive.Count >= 13); // every browser has cookies; Chromium browsers also history
        Assert.All(sensitive, c =>
        {
            Assert.False(c.DefaultChecked, c.Id);
            Assert.Contains("(signs you out)", c.Name);
        });
    }

    [Fact]
    public void No_cache_touches_game_files_or_config()
    {
        foreach (string path in Catalog.Apps.SelectMany(a => a.Caches).SelectMany(c => c.Paths))
        {
            Assert.DoesNotContain("steamapps", path, StringComparison.OrdinalIgnoreCase);
            Assert.False(path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase), path);
        }
    }

    [Fact]
    public void Every_path_expands_with_only_known_variables()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
            ["APPDATA"] = @"C:\Users\me\AppData\Roaming",
            ["PROGRAMDATA"] = @"C:\ProgramData",
            ["PROGRAMFILES"] = @"C:\Program Files",
            ["PROGRAMFILES(X86)"] = @"C:\Program Files (x86)",
            ["USERPROFILE"] = @"C:\Users\me",
            ["TEMP"] = @"C:\Users\me\AppData\Local\Temp",
        };

        IEnumerable<string> paths = Catalog.Apps.SelectMany(a => a.DetectPaths.Concat(a.Caches.SelectMany(c => c.Paths)))
            .Concat(Catalog.Apps.SelectMany(a => a.Toggles).SelectMany(t => t.AllActions)
                .Where(x => x.LocalStatePath is not null).Select(x => x.LocalStatePath!));
        foreach (string path in paths)
        {
            Assert.Empty(AppCatalogValidator.ValidatePath(path));
            string? expanded = AppPathExpander.ExpandVariables(path, v => env.GetValueOrDefault(v));
            Assert.NotNull(expanded);
            Assert.DoesNotContain("%", expanded);
            Assert.StartsWith(@"C:\", expanded);
        }
    }

    [Fact]
    public void Toggle_actions_use_supported_types_and_browsers_have_policies()
    {
        Assert.All(Catalog.Apps.SelectMany(a => a.Toggles).SelectMany(t => t.AllActions),
            a => Assert.Contains(a.Type, AppToggleTypes.All));
        Assert.Contains(Catalog.Apps.Single(a => a.Id == "chrome").Toggles,
            t => t.Action?.Path == @"SOFTWARE\Policies\Google\Chrome" && t.Action.ValueName == "BackgroundModeEnabled");
    }

    [Fact]
    public void Validator_rejects_checked_cookies_amd_and_bad_paths()
    {
        var bad = new AppCatalog
        {
            Apps =
            {
                new AppProfile
                {
                    Id = "amd_software", Name = "AMD Software", Category = "Browsers", DetectPaths = { @"C:\AMD" },
                    Caches = { new AppCache { Id = "cookies", Name = "Cookies", Paths = { @"%BOGUS%\x" }, DefaultChecked = true } },
                    WhilePlaying = new WhilePlayingRule { Mode = "Turbo" },
                },
            },
        };

        IReadOnlyList<string> errors = AppCatalogValidator.Validate(bad, builtIn: false);

        Assert.Contains(errors, e => e.Contains("out-of-scope term 'AMD'"));
        Assert.Contains(errors, e => e.Contains("must be unchecked by default"));
        Assert.Contains(errors, e => e.Contains("must start with an environment variable"));
        Assert.Contains(errors, e => e.Contains("unsupported variable %BOGUS%"));
        Assert.Contains(errors, e => e.Contains("invalid whilePlaying mode"));
    }

    [Fact]
    public void Absolute_paths_are_contracted_to_the_longest_known_variable()
    {
        var env = new Dictionary<string, string> { ["USERPROFILE"] = @"C:\Users\me", ["APPDATA"] = @"C:\Users\me\AppData\Roaming", ["PROGRAMFILES"] = @"C:\Program Files" };
        Func<string, string?> lookup = v => env.GetValueOrDefault(v);

        Assert.Equal(@"%APPDATA%\obs-studio\logs", AppPathExpander.Contract(@"C:\Users\me\AppData\Roaming\obs-studio\logs", lookup));
        Assert.Equal(@"%PROGRAMFILES%\OBS\obs64.exe", AppPathExpander.Contract("\"C:\\Program Files\\OBS\\obs64.exe\"", lookup));
        Assert.Equal(@"C:\Program FilesX\a.exe", AppPathExpander.Contract(@"C:\Program FilesX\a.exe", lookup));
    }

    [Fact]
    public void Catalog_json_round_trips() =>
        Assert.Equal(38, JsonSerializer.Deserialize<AppCatalog>(
            JsonSerializer.Serialize(Catalog, AtomicJsonFile.DefaultOptions), AtomicJsonFile.DefaultOptions)!.Apps.Count);
}
