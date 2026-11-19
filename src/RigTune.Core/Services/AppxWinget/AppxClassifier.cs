using RigTune.Core.Models;

namespace RigTune.Core.Services.AppxWinget;

/// <summary>
/// Groups Store/UWP packages into Safe to Remove / Optional / System-Critical (Protected) / Other.
/// The protected blocklist is evaluated FIRST so a critical package can never be classified as removable.
/// Patterns ending in '*' are prefix matches; matching is case-insensitive on the package Name.
/// </summary>
public static class AppxClassifier
{
    public static readonly IReadOnlyList<string> ProtectedPatterns = new[]
    {
        // Store & app installation (winget lives in DesktopAppInstaller)
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.Services.Store.Engagement",
        // Shell & core experience
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "MicrosoftWindows.Client.*",
        "Microsoft.Windows.CloudExperienceHost", "Microsoft.Windows.ContentDeliveryManager", "Microsoft.Windows.Search",
        "Microsoft.Windows.SecureAssessmentBrowser", "Microsoft.LockApp", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl",
        "Microsoft.Windows.OOBENetwork*", "Microsoft.Windows.PeopleExperienceHost", "Microsoft.Windows.XGpuEjectDialog",
        "Microsoft.Windows.AssignedAccessLockApp", "Microsoft.Windows.CapturePicker", "Microsoft.Windows.PinningConfirmationDialog",
        "Microsoft.Windows.Apprep.ChxApp", "Microsoft.Windows.NarratorQuickStart", "Microsoft.Windows.ParentalControls",
        "Microsoft.ECApp", "Microsoft.CredDialogHost", "Microsoft.BioEnrollment", "Microsoft.Win32WebViewHost", "Microsoft.Windows.FileExplorer",
        "windows.immersivecontrolpanel", "Windows.PrintDialog", "Windows.CBSPreview", "1527c705-839a-4832-9118-54d4Bd6a0c89",
        "c5e2524a-ea46-4f67-841f-6a9465d9d515", "E2A4F912-2574-4A75-9BB0-0D023378592B", "F46D4000-FD22-4DB4-AC8E-4E1DDDE828FE",
        // Security
        "Microsoft.SecHealthUI", "Microsoft.Windows.SecHealthUI",
        // Runtimes & frameworks
        "Microsoft.WebView2*", "Microsoft.WindowsAppRuntime*", "Microsoft.VCLibs*", "Microsoft.UI.Xaml*", "Microsoft.NET.*",
        "Microsoft.Services.Store*", "Microsoft.WinAppRuntime*", "Microsoft.Advertising.Xaml",
        // Hardware vendors on this PC
        "NVIDIACorp.*", "*NVIDIA*",
        // Audio stacks
        "RealtekSemiconductorCorp.*", "DolbyLaboratories.*", "*Audio*", "*Nahimic*", "*WavesAudio*",
    };

    public static readonly IReadOnlyList<string> SafeToRemovePatterns = new[]
    {
        "Clipchamp.Clipchamp", "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingSearch", "Microsoft.MicrosoftSolitaireCollection",
        "Microsoft.Windows.DevHome", "Microsoft.WindowsFeedbackHub", "Microsoft.People", "Microsoft.WindowsSoundRecorder",
        "Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.MicrosoftOfficeHub", "Microsoft.Todos", "Microsoft.WindowsMaps",
        "Microsoft.PowerAutomateDesktop", "Microsoft.549981C3F5F10", "MicrosoftTeams", "MSTeams", "Microsoft.OutlookForWindows",
        "Microsoft.ZuneVideo", "Microsoft.MixedReality.Portal", "Microsoft.SkypeApp", "Microsoft.Wallet", "Microsoft.Messaging",
        "Microsoft.MicrosoftJournal", "MicrosoftCorporationII.MicrosoftFamily", "Microsoft.Copilot", "Microsoft.Windows.Ai.Copilot.Provider",
    };

    public static readonly IReadOnlyList<string> OptionalPatterns = new[]
    {
        "Microsoft.Windows.Photos", "Microsoft.WindowsCamera", "Microsoft.WindowsCalculator", "Microsoft.Paint", "Microsoft.MSPaint",
        "Microsoft.GamingApp", "Microsoft.XboxApp", "Microsoft.XboxGamingOverlay", "Microsoft.XboxGameOverlay", "Microsoft.XboxSpeechToTextOverlay",
        "Microsoft.Xbox.TCUI", "Microsoft.XboxIdentityProvider", "Microsoft.ScreenSketch", "Microsoft.WindowsNotepad", "Microsoft.WindowsTerminal",
        "Microsoft.ZuneMusic", "Microsoft.YourPhone", "Microsoft.WindowsAlarms", "Microsoft.MicrosoftStickyNotes", "Microsoft.Windows.DevHome.*",
        "MicrosoftWindows.CrossDevice", "MicrosoftCorporationII.QuickAssist", "Microsoft.Edge.GameAssist", "Microsoft.*Extension",
        "Microsoft.WidgetsPlatformRuntime", "Microsoft.StartExperiencesApp",
    };

    public static AppxPackageClassified Classify(AppxPackageInfo package)
    {
        if (package.IsFramework)
        {
            return new(package, AppxCategory.Protected, "Framework/runtime other apps depend on.");
        }

        if (Matches(package.Name, ProtectedPatterns) is { } p)
        {
            return new(package, AppxCategory.Protected, $"System-critical ({p}).");
        }

        if (string.Equals(package.SignatureKind, "System", StringComparison.OrdinalIgnoreCase))
        {
            return new(package, AppxCategory.Protected, "Part of Windows (system-signed).");
        }

        if (Matches(package.Name, SafeToRemovePatterns) is not null)
        {
            return new(package, AppxCategory.SafeToRemove, "Pre-installed extra; nothing depends on it.");
        }

        if (Matches(package.Name, OptionalPatterns) is not null)
        {
            return new(package, AppxCategory.Optional, "Useful built-in app — remove only if you never use it.");
        }

        return new(package, AppxCategory.Other, "Installed app not in RigTune's lists.");
    }

    public static string? Matches(string name, IEnumerable<string> patterns) =>
        patterns.FirstOrDefault(pattern => IsMatch(name, pattern));

    public static bool IsMatch(string name, string pattern)
    {
        bool leading = pattern.StartsWith('*'), trailing = pattern.EndsWith('*');
        string core = pattern.Trim('*');
        return (leading, trailing) switch
        {
            (true, true) => name.Contains(core, StringComparison.OrdinalIgnoreCase),
            (false, true) => name.StartsWith(core, StringComparison.OrdinalIgnoreCase),
            (true, false) => name.EndsWith(core, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(name, core, StringComparison.OrdinalIgnoreCase),
        };
    }
}
