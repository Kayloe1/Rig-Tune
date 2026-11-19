using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigTune.App.Converters;
using RigTune.App.Services;
using RigTune.Core.Models;
using RigTune.Core.Services.AppOptimizer;
using Wpf.Ui.Controls;

namespace RigTune.App.ViewModels;

public sealed partial class AppCacheItemViewModel : ObservableObject
{
    public AppCacheItemViewModel(AppCache cache)
    {
        Cache = cache;
        _isSelected = cache.DefaultChecked;
    }

    public AppCache Cache { get; }
    public string Name => Cache.Name;
    public bool IsSensitive => Cache.IsSensitive;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _sizeText = "—";
}

public sealed partial class AppToggleItemViewModel : ObservableObject
{
    public AppToggleItemViewModel(AppToggle toggle) => Toggle = toggle;

    public AppToggle Toggle { get; }
    public string Name => Toggle.Name;
    public string? Description => Toggle.Description;
    public string? Warning => Toggle.Warning;

    [ObservableProperty]
    private string _statusText = "Checking…";

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _canApply = true;

    [ObservableProperty]
    private bool _isBusy;
}

/// <summary>One expandable app card.</summary>
public sealed partial class AppCardViewModel : ObservableObject
{
    private readonly AppOptimizerService _service;
    private readonly Action<InfoBarSeverity, string, string> _status;
    private bool _loaded;
    private bool _suppressRule;

    public AppCardViewModel(AppDetection detection, AppOptimizerService service, Action<InfoBarSeverity, string, string> status)
    {
        Profile = detection.Profile;
        IsInstalled = detection.IsInstalled;
        Evidence = detection.Evidence;
        _service = service;
        _status = status;
        foreach (AppCache c in Profile.Caches)
        {
            Caches.Add(new AppCacheItemViewModel(c));
        }

        foreach (AppToggle t in Profile.Toggles)
        {
            Toggles.Add(new AppToggleItemViewModel(t));
        }
    }

    public AppProfile Profile { get; }
    public string Name => Profile.Name;
    public string Category => Profile.Category;
    public bool IsInstalled { get; }
    public string? Evidence { get; }
    public bool IsCustom => Profile.IsCustom;
    public string Summary => $"{Profile.ActionCount} action(s) · {(IsInstalled ? "Installed" : "Not detected")}{(IsCustom ? " · Custom" : string.Empty)}";
    public bool HasCaches => Caches.Count > 0;
    public bool HasToggles => Toggles.Count > 0;

    public ObservableCollection<AppCacheItemViewModel> Caches { get; } = new();
    public ObservableCollection<AppToggleItemViewModel> Toggles { get; } = new();
    public IReadOnlyList<string> Modes { get; } = new[] { "EcoQoS", "LowerPriority" };

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _cacheTotal = string.Empty;

    [ObservableProperty]
    private bool _hasRule;

    [ObservableProperty]
    private bool _whilePlayingEnabled;

    [ObservableProperty]
    private string _whilePlayingMode = "EcoQoS";

    public string WhilePlayingHint => WhilePlayingMode == "EcoQoS"
        ? "While a game runs: efficiency mode (EcoQoS) moves it to the E-cores, with Below Normal CPU and low I/O priority."
        : "While a game runs: Below Normal CPU priority so the game gets the P-cores first.";

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = LoadAsync();
        }
    }

    partial void OnWhilePlayingEnabledChanged(bool value) => _ = SaveRuleAsync();

    partial void OnWhilePlayingModeChanged(string value)
    {
        OnPropertyChanged(nameof(WhilePlayingHint));
        _ = SaveRuleAsync();
    }

    public async Task LoadAsync(bool force = false)
    {
        if (_loaded && !force)
        {
            return;
        }

        _loaded = true;
        _suppressRule = true;
        WhilePlayingRule? rule = await _service.GetWhilePlayingAsync(Profile);
        HasRule = rule is not null || Profile.WhilePlaying is not null;
        WhilePlayingEnabled = rule?.Enabled ?? false;
        WhilePlayingMode = rule?.Mode ?? "EcoQoS";
        _suppressRule = false;
        await Task.WhenAll(ScanAsync(), RefreshTogglesAsync());
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IReadOnlyList<AppCacheEstimate> estimates = await _service.ScanCachesAsync(Profile);
        foreach (AppCacheItemViewModel item in Caches)
        {
            AppCacheEstimate? e = estimates.FirstOrDefault(x => x.CacheId == item.Cache.Id);
            item.SizeText = e is null || e.ExistingPaths.Count == 0 ? "Not found" : $"{Format.Bytes(e.Bytes)} · {e.Files:N0} files";
        }

        CacheTotal = $"Reclaimable (selected): {Format.Bytes(estimates.Where(e => Caches.Any(c => c.IsSelected && c.Cache.Id == e.CacheId)).Sum(e => e.Bytes))}";
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        List<string> ids = Caches.Where(c => c.IsSelected).Select(c => c.Cache.Id).ToList();
        if (ids.Count == 0)
        {
            _status(InfoBarSeverity.Informational, Name, "Select at least one cache to clean.");
            return;
        }

        IsBusy = true;
        try
        {
            AppCacheCleanResult r = await _service.CleanCachesAsync(Profile, ids);
            _status(InfoBarSeverity.Success, $"{Name}: freed {Format.Bytes(r.BytesFreed)}", string.Join(" ", r.Messages));
        }
        finally
        {
            IsBusy = false;
        }

        await ScanAsync();
    }

    [RelayCommand]
    private async Task ApplyToggleAsync(AppToggleItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            TweakApplyResult r = await _service.ApplyToggleAsync(Profile, item.Toggle);
            _status(r.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error, $"{Name}: {item.Name}", r.Message);
        }
        finally
        {
            item.IsBusy = false;
        }

        await RefreshToggleAsync(item);
    }

    [RelayCommand]
    private async Task RevertToggleAsync(AppToggleItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            TweakApplyResult r = await _service.RevertToggleAsync(Profile, item.Toggle);
            _status(r.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error, $"{Name}: {item.Name} reverted", r.Message);
        }
        finally
        {
            item.IsBusy = false;
        }

        await RefreshToggleAsync(item);
    }

    private Task RefreshTogglesAsync() => Task.WhenAll(Toggles.Select(RefreshToggleAsync));

    private async Task RefreshToggleAsync(AppToggleItemViewModel item)
    {
        try
        {
            TweakStatusReport report = await Task.Run(() => _service.GetToggleStatusAsync(Profile, item.Toggle));
            item.IsActive = report.Status is TweakStatus.Active or TweakStatus.Partial || report.HasActiveSnapshots;
            item.CanApply = report.Status != TweakStatus.NotApplicable;
            item.StatusText = report.Status == TweakStatus.NotApplicable
                ? report.Actions.FirstOrDefault()?.Current ?? "Not applicable"
                : report.Summary;
        }
        catch (Exception ex)
        {
            item.StatusText = ex.Message;
        }
    }

    private async Task SaveRuleAsync()
    {
        if (_suppressRule || !_loaded)
        {
            return;
        }

        WhilePlayingRule rule = Profile.WhilePlaying?.Clone() ?? new WhilePlayingRule();
        rule.Enabled = WhilePlayingEnabled;
        rule.Mode = WhilePlayingMode;
        rule.CpuPriority = "BelowNormal";
        rule.IoPriority = WhilePlayingMode == "EcoQoS" ? "Low" : rule.IoPriority;
        await _service.SetWhilePlayingAsync(Profile, rule);
        HasRule = true;
    }
}

/// <summary>Model for the Add / Edit Custom App dialog.</summary>
public sealed partial class CustomAppFormModel : ObservableObject
{
    public IReadOnlyList<string> Categories { get; } = AppCategories.All;
    public IReadOnlyList<string> Modes { get; } = new[] { "EcoQoS", "LowerPriority" };

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _category = AppCategories.Media;

    [ObservableProperty]
    private string _executablePath = string.Empty;

    [ObservableProperty]
    private string _cachePaths = string.Empty;

    [ObservableProperty]
    private string _startupMatch = string.Empty;

    [ObservableProperty]
    private bool _whilePlayingEnabled = true;

    [ObservableProperty]
    private string _whilePlayingMode = "LowerPriority";

    public string? ExistingId { get; set; }
}

public sealed partial class AppOptimizerViewModel : ObservableObject
{
    public const string AllCategories = "All";
    private readonly AppOptimizerService _service;
    private readonly DialogService _dialogs;
    private bool _loaded;

    public AppOptimizerViewModel(AppOptimizerService service, DialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
        View = CollectionViewSource.GetDefaultView(Cards);
        View.Filter = o => o is AppCardViewModel c && (ShowAll || c.IsInstalled) &&
                           (SelectedCategory == AllCategories || c.Category == SelectedCategory);
    }

    public ObservableCollection<AppCardViewModel> Cards { get; } = new();
    public ICollectionView View { get; }
    public IReadOnlyList<string> Categories { get; } = new[] { AllCategories }.Concat(AppCategories.All).ToList();

    [ObservableProperty]
    private string _selectedCategory = AllCategories;

    [ObservableProperty]
    private bool _showAll;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isStatusOpen;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    partial void OnSelectedCategoryChanged(string value) => View.Refresh();

    partial void OnShowAllChanged(bool value) => View.Refresh();

    public async Task OnNavigatedToAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            IReadOnlyList<AppDetection> detections = await _service.DetectAsync();
            Cards.Clear();
            foreach (AppDetection d in detections.OrderByDescending(d => d.IsInstalled).ThenBy(d => d.Profile.Name, StringComparer.OrdinalIgnoreCase))
            {
                Cards.Add(new AppCardViewModel(d, _service, ShowStatus));
            }

            int builtIn = detections.Count(d => !d.Profile.IsCustom);
            Summary = $"{detections.Count(d => d.IsInstalled)} of {detections.Count} profiles detected on this PC" +
                      $" ({builtIn} built-in, {detections.Count - builtIn} custom)";
            if (_service.LoadErrors.Count > 0)
            {
                ShowStatus(InfoBarSeverity.Warning, "custom_apps.json has problems", string.Join("\n", _service.LoadErrors));
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Could not load app profiles", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task AddCustomAppAsync() => EditAsync(new CustomAppFormModel());

    [RelayCommand]
    private Task EditCustomAppAsync(AppCardViewModel? card)
    {
        if (card is null || !card.IsCustom)
        {
            return Task.CompletedTask;
        }

        AppProfile p = card.Profile;
        return EditAsync(new CustomAppFormModel
        {
            ExistingId = p.Id,
            Name = p.Name,
            Category = p.Category,
            ExecutablePath = p.DetectPaths.FirstOrDefault() ?? string.Empty,
            CachePaths = string.Join(Environment.NewLine, p.Caches.SelectMany(c => c.Paths)),
            StartupMatch = p.Toggles.SelectMany(t => t.AllActions).FirstOrDefault(a => a.Type == AppToggleTypes.Startup)?.Match?.FirstOrDefault() ?? string.Empty,
            WhilePlayingEnabled = p.WhilePlaying?.Enabled ?? false,
            WhilePlayingMode = p.WhilePlaying?.Mode ?? "LowerPriority",
        });
    }

    [RelayCommand]
    private async Task DeleteCustomAppAsync(AppCardViewModel? card)
    {
        if (card is null || !card.IsCustom ||
            !await _dialogs.ConfirmAsync("Delete custom app?", $"Remove the \"{card.Name}\" profile from custom_apps.json?", "Delete", danger: true))
        {
            return;
        }

        await _service.DeleteCustomAppAsync(card.Profile.Id);
        await RefreshAsync();
    }

    private async Task EditAsync(CustomAppFormModel form)
    {
        var view = new Views.CustomAppForm { DataContext = form };
        if (!await _dialogs.ShowFormAsync(form.ExistingId is null ? "Add Custom App" : "Edit Custom App", view, "Save"))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(form.Name) || string.IsNullOrWhiteSpace(form.ExecutablePath))
        {
            ShowStatus(InfoBarSeverity.Error, "Custom app not saved", "Name and executable path are required.");
            return;
        }

        AppProfile app = AppOptimizerService.BuildCustomApp(
            form.Name,
            form.Category,
            AppPathExpander.Contract(form.ExecutablePath),
            form.CachePaths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => AppPathExpander.Contract(p)),
            form.StartupMatch,
            new WhilePlayingRule
            {
                Enabled = form.WhilePlayingEnabled,
                Mode = form.WhilePlayingMode,
                CpuPriority = "BelowNormal",
                IoPriority = form.WhilePlayingMode == "EcoQoS" ? "Low" : "Normal",
            });
        if (form.ExistingId is not null)
        {
            app.Id = form.ExistingId;
        }

        IReadOnlyList<string> errors = await _service.SaveCustomAppAsync(app);
        if (errors.Count > 0)
        {
            ShowStatus(InfoBarSeverity.Error, "Custom app not saved", string.Join("\n", errors));
            return;
        }

        ShowStatus(InfoBarSeverity.Success, $"Saved \"{app.Name}\"", "Stored in %LOCALAPPDATA%\\RigTune\\custom_apps.json.");
        await RefreshAsync();
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        IsStatusOpen = true;
    }
}
