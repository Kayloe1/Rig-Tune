using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Services;
using RigTune.Core.Services.AppOptimizer;
using RigTune.Core.Services.GameMode;
using Wpf.Ui.Controls;

namespace RigTune.App.ViewModels;

public sealed partial class DisplayRowViewModel : ObservableObject
{
    private readonly Func<DisplayRowViewModel, Task> _pin;

    public DisplayRowViewModel(DisplayStatus status, Func<DisplayRowViewModel, Task> pin)
    {
        Display = status.Display;
        _pin = pin;
        Choices = new[] { "Not pinned" }.Concat(status.Display.SupportedHz.OrderByDescending(h => h).Select(h => $"{h} Hz")).ToList();
        _selectedChoice = status.TargetHz is { } t ? $"{t} Hz" : "Not pinned";
        IsDrifted = status.IsDrifted;
    }

    public DisplayInfo Display { get; }
    public string Name => Display.FriendlyName + (Display.IsPrimary ? " (primary)" : string.Empty);
    public string Detail => $"{Display.DeviceName} · {Display.Width}×{Display.Height} · now {Display.CurrentHz} Hz · max {Display.MaxHz} Hz";
    public IReadOnlyList<string> Choices { get; }

    public int? TargetHz => SelectedChoice.EndsWith(" Hz", StringComparison.Ordinal) && int.TryParse(SelectedChoice[..^3], out int hz) ? hz : null;

    [ObservableProperty]
    private string _selectedChoice;

    [ObservableProperty]
    private bool _isDrifted;

    partial void OnSelectedChoiceChanged(string value) => _ = _pin(this);
}

public sealed partial class GameRowViewModel : ObservableObject
{
    public GameRowViewModel(DiscoveredGame game) => Game = game;

    public DiscoveredGame Game { get; }
    public string Name => Game.Name;
    public string Source => Game.Source.ToString();
    public string Detail => $"{Game.InstallDir} · {Game.Executables.Count} executable(s)";
}

public sealed record ThrottleRow(string Name, string Detail);

public sealed partial class GameModeViewModel : ObservableObject
{
    private readonly GameModeService _gameMode;
    private readonly RefreshRateWatchdogService _watchdog;
    private readonly GameLibraryScanner _scanner;
    private readonly SettingsService _settings;
    private readonly AppOptimizerService _apps;
    private readonly StartWithWindowsService _autoStart;
    private bool _loaded;
    private bool _suppress = true;

    public GameModeViewModel(GameModeService gameMode, RefreshRateWatchdogService watchdog, GameLibraryScanner scanner,
        SettingsService settings, AppOptimizerService apps, StartWithWindowsService autoStart)
    {
        _gameMode = gameMode;
        _watchdog = watchdog;
        _scanner = scanner;
        _settings = settings;
        _apps = apps;
        _autoStart = autoStart;
        _gameMode.SessionChanged += (_, _) => OnUi(UpdateSession);
        _watchdog.StatusChanged += (_, status) => OnUi(() => UpdateDisplays(status));
    }

    // ---- session
    [ObservableProperty]
    private string _sessionTitle = "Game Mode is idle";

    [ObservableProperty]
    private string _sessionDetail = string.Empty;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _watcherText = string.Empty;

    public ObservableCollection<ThrottleRow> ThrottledNow { get; } = new();
    public ObservableCollection<ThrottleRow> BackgroundRules { get; } = new();

    // ---- settings
    [ObservableProperty]
    private bool _autoDetect;

    [ObservableProperty]
    private bool _ramTrimEnabled;

    [ObservableProperty]
    private double _ramTrimThreshold = 80;

    [ObservableProperty]
    private bool _switchPowerPlan;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _autoRestoreRefresh;

    // ---- displays
    public ObservableCollection<DisplayRowViewModel> Displays { get; } = new();

    [ObservableProperty]
    private string _watchdogSummary = string.Empty;

    // ---- games
    public ObservableCollection<GameRowViewModel> Games { get; } = new();
    public ObservableCollection<string> Allowlist { get; } = new();
    public ObservableCollection<string> Exclusions { get; } = new();

    [ObservableProperty]
    private string _newAllowEntry = string.Empty;

    [ObservableProperty]
    private string _newExclusion = string.Empty;

    [ObservableProperty]
    private string _scanSummary = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    // ---- status
    [ObservableProperty]
    private bool _isStatusOpen;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    public async Task OnNavigatedToAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            AppSettings s = await _settings.GetAsync();
            AutoDetect = s.GameModeAutoDetect;
            RamTrimEnabled = s.RamTrimEnabled;
            RamTrimThreshold = s.RamTrimThresholdPercent;
            SwitchPowerPlan = s.SwitchPowerPlanDuringGames;
            MinimizeToTray = s.MinimizeToTrayOnClose;
            AutoRestoreRefresh = s.AutoRestoreRefreshRate;
            StartWithWindows = await Task.Run(_autoStart.IsEnabled);
            _suppress = false;
            await LoadListsAsync();
        }

        UpdateSession();
        await LoadRulesAsync();
        await _watchdog.CheckAsync("Game Mode page opened");
    }

    // ------------------------------------------------------------------ session

    [RelayCommand]
    private async Task StartAsync()
    {
        GameSessionResult r = await _gameMode.StartManualAsync();
        ShowStatus(r.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning, r.Success ? "Game Mode started" : "Not started", r.Message);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        GameSessionResult r = await _gameMode.StopAsync("stopped manually");
        ShowStatus(r.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning, "Game Mode stopped", r.Message);
    }

    private void UpdateSession()
    {
        GameSession? s = _gameMode.ActiveSession;
        IsActive = s is not null;
        SessionTitle = s is null ? "Game Mode is idle" : $"Game Mode active — {s.GameName ?? "manual session"}";
        SessionDetail = s is null
            ? "Waiting for a game to start. Background apps run normally."
            : $"Started {s.StartedAtUtc.ToLocalTime():HH:mm:ss}" +
              (s.GameOriginalPriorityClass is { } p ? $" · game priority {PriorityClasses.Name(p)} → High" : string.Empty) +
              (s.AppliedPowerPlan is not null ? " · power plan switched" : string.Empty);
        ThrottledNow.Clear();
        foreach (ThrottledProcess t in s?.Throttled ?? new List<ThrottledProcess>())
        {
            ThrottledNow.Add(new ThrottleRow($"{t.Name} (PID {t.Pid})",
                $"{PriorityClasses.Name(t.OriginalPriorityClass)} → Below Normal{(t.EcoQosApplied ? " · EcoQoS (E-cores)" : string.Empty)}" +
                $"{(t.IoPriorityLowered ? " · low I/O" : string.Empty)}{(t.Trimmed ? " · RAM trimmed" : string.Empty)}"));
        }

        WatcherText = !_gameMode.IsWatching
            ? "Auto-detect is off — use Start/Stop."
            : _gameMode.WatcherUsesWmi ? "Watching launches via WMI (Win32_ProcessStartTrace)." : "Watching launches via 2-second polling (WMI unavailable).";
    }

    private async Task LoadRulesAsync()
    {
        IReadOnlyDictionary<string, WhilePlayingRule> rules = await _apps.GetEnabledRulesAsync();
        BackgroundRules.Clear();
        foreach ((string id, WhilePlayingRule rule) in rules.OrderBy(r => r.Key))
        {
            AppProfile? app = _apps.Find(id);
            BackgroundRules.Add(new ThrottleRow(app?.Name ?? id,
                $"{(rule.Mode == "EcoQoS" ? "EcoQoS → E-cores" : "Lower priority")} · CPU {rule.CpuPriority} · I/O {rule.IoPriority} · {string.Join(", ", app?.Processes ?? new List<string>())}"));
        }
    }

    // ------------------------------------------------------------------ settings

    partial void OnAutoDetectChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        if (value)
        {
            _gameMode.StartWatching();
        }
        else
        {
            _gameMode.StopWatching();
        }

        UpdateSession();
        _ = _settings.UpdateAsync(s => s.GameModeAutoDetect = value);
    }

    partial void OnRamTrimEnabledChanged(bool value) => Save(s => s.RamTrimEnabled = value);

    partial void OnRamTrimThresholdChanged(double value) => Save(s => s.RamTrimThresholdPercent = (int)Math.Round(value));

    partial void OnSwitchPowerPlanChanged(bool value) => Save(s => s.SwitchPowerPlanDuringGames = value);

    partial void OnMinimizeToTrayChanged(bool value) => Save(s => s.MinimizeToTrayOnClose = value);

    partial void OnAutoRestoreRefreshChanged(bool value) => Save(s => s.AutoRestoreRefreshRate = value);

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        try
        {
            if (value)
            {
                _autoStart.Enable(Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unknown."));
            }
            else
            {
                _autoStart.Disable();
            }

            Save(s => s.StartWithWindows = value);
            ShowStatus(InfoBarSeverity.Success, value ? "Starts with Windows" : "Won't start with Windows",
                value ? @"Task Scheduler task \RigTune\AutoStart (highest privileges, --minimized) created." : @"Task \RigTune\AutoStart removed.");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Could not change Start with Windows", ex.Message);
        }
    }

    private void Save(Action<AppSettings> mutate)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(mutate);
        }
    }

    // ------------------------------------------------------------------ refresh-rate watchdog

    private void UpdateDisplays(IReadOnlyList<DisplayStatus> status)
    {
        Displays.Clear();
        foreach (DisplayStatus s in status)
        {
            Displays.Add(new DisplayRowViewModel(s, PinAsync));
        }

        int drifted = status.Count(s => s.IsDrifted);
        WatchdogSummary = status.Count == 0
            ? "No displays found."
            : drifted > 0 ? $"{drifted} display(s) below their pinned refresh rate." : "All pinned displays are at their target refresh rate.";
    }

    private async Task PinAsync(DisplayRowViewModel row) => await _watchdog.PinTargetAsync(row.Display, row.TargetHz);

    [RelayCommand]
    private Task CheckDisplaysAsync() => _watchdog.CheckAsync("manual check");

    [RelayCommand]
    private async Task RestoreRefreshAsync()
    {
        IReadOnlyList<string> log = await _watchdog.RestoreAllAsync();
        ShowStatus(InfoBarSeverity.Success, "Refresh rate restore", log.Count == 0 ? "Nothing to restore." : string.Join("\n", log));
    }

    // ------------------------------------------------------------------ games

    private async Task LoadListsAsync()
    {
        GameModeDocument doc = await _gameMode.Store.ReadAsync();
        Games.Clear();
        foreach (DiscoveredGame g in doc.DiscoveredGames)
        {
            Games.Add(new GameRowViewModel(g));
        }

        Allowlist.Clear();
        doc.CustomExeAllowlist.ForEach(Allowlist.Add);
        Exclusions.Clear();
        doc.ExclusionList.ForEach(Exclusions.Add);
        ScanSummary = doc.LastScanUtc is { } at
            ? $"{doc.DiscoveredGames.Count} games found · last scan {at.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "Library not scanned yet.";
        if (doc.LastScanUtc is null)
        {
            await RescanAsync();
        }
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        IsScanning = true;
        try
        {
            IReadOnlyList<DiscoveredGame> games = await _scanner.ScanAsync();
            await _gameMode.UpdateListsAsync(d =>
            {
                d.DiscoveredGames = games.ToList();
                d.LastScanUtc = DateTimeOffset.UtcNow;
            });
            Games.Clear();
            foreach (DiscoveredGame g in games)
            {
                Games.Add(new GameRowViewModel(g));
            }

            ScanSummary = $"{games.Count} games found ({string.Join(", ", games.GroupBy(g => g.Source).Select(g => $"{g.Key} {g.Count()}"))})";
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Library scan failed", ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void BrowseAllow()
    {
        var dialog = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe" };
        if (dialog.ShowDialog() == true)
        {
            NewAllowEntry = dialog.FileName;
        }
    }

    [RelayCommand]
    private Task AddAllowAsync() => AddEntryAsync(NewAllowEntry, Allowlist, d => d.CustomExeAllowlist, () => NewAllowEntry = string.Empty);

    [RelayCommand]
    private Task AddExclusionAsync() => AddEntryAsync(NewExclusion, Exclusions, d => d.ExclusionList, () => NewExclusion = string.Empty);

    [RelayCommand]
    private Task RemoveAllowAsync(string? entry) => RemoveEntryAsync(entry, Allowlist, d => d.CustomExeAllowlist);

    [RelayCommand]
    private Task RemoveExclusionAsync(string? entry) => RemoveEntryAsync(entry, Exclusions, d => d.ExclusionList);

    private async Task AddEntryAsync(string entry, ObservableCollection<string> list, Func<GameModeDocument, List<string>> select, Action clear)
    {
        string value = entry.Trim().Trim('"');
        if (value.Length == 0 || list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        await _gameMode.UpdateListsAsync(d => select(d).Add(value));
        list.Add(value);
        clear();
    }

    private async Task RemoveEntryAsync(string? entry, ObservableCollection<string> list, Func<GameModeDocument, List<string>> select)
    {
        if (entry is null)
        {
            return;
        }

        await _gameMode.UpdateListsAsync(d => select(d).RemoveAll(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase)));
        list.Remove(entry);
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        IsStatusOpen = true;
    }

    private static void OnUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.InvokeAsync(action); // never block: StopAsync may run while the UI thread waits on shutdown
        }
        else
        {
            action();
        }
    }
}
