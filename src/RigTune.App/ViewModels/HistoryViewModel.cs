using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RigTune.App.Services;
using RigTune.Core.Models;
using RigTune.Core.Services;
using Wpf.Ui.Controls;

namespace RigTune.App.ViewModels;

public sealed record SnapshotRow(Guid Id, string Target, string Original, string Applied, DateTimeOffset CapturedAt, string? LastError);

/// <summary>All active snapshots of one tweak (per-tweak Revert).</summary>
public sealed record TweakSnapshotGroup(string TweakId, string Name, bool RequiresRestart, IReadOnlyList<SnapshotRow> Items)
{
    public string Summary => $"{Items.Count} change(s) · applied {Items.Min(i => i.CapturedAt):yyyy-MM-dd HH:mm}{(RequiresRestart ? " · restart" : string.Empty)}";
}

public sealed partial class DriftRowViewModel : ObservableObject
{
    public DriftRowViewModel(DriftItem item) => Item = item;

    public DriftItem Item { get; }
    public string Name => Item.TweakName;
    public string Target => Item.Target;
    public string Expected => Item.Expected;
    public string Current => Item.Current;
    public string? Note => Item.Note;

    [ObservableProperty]
    private string _result = string.Empty;

    [ObservableProperty]
    private bool _isFixed;
}

/// <summary>Backups, Drift &amp; History (SPEC §5.7).</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    public const string AllActions = "All";
    private readonly SnapshotService _snapshots;
    private readonly AuditLogService _audit;
    private readonly DriftDetectorService _drift;
    private readonly ProfileService _profiles;
    private readonly SettingsService _settings;
    private readonly TweakEngine _engine;
    private readonly DialogService _dialogs;
    private bool _loaded;
    private bool _suppress = true;

    public HistoryViewModel(SnapshotService snapshots, AuditLogService audit, DriftDetectorService drift, ProfileService profiles,
        SettingsService settings, TweakEngine engine, DialogService dialogs)
    {
        _snapshots = snapshots;
        _audit = audit;
        _drift = drift;
        _profiles = profiles;
        _settings = settings;
        _engine = engine;
        _dialogs = dialogs;
        HistoryView = CollectionViewSource.GetDefaultView(History);
        HistoryView.Filter = o => o is AuditEntry e && MatchesHistory(e);
        _drift.ScanCompleted += (_, r) => Application.Current?.Dispatcher.InvokeAsync(() => ShowDrift(r));
        _audit.EntryAdded += (_, e) => Application.Current?.Dispatcher.InvokeAsync(() => History.Insert(0, e));
    }

    // ---- drift
    public ObservableCollection<DriftRowViewModel> DriftedItems { get; } = new();

    [ObservableProperty]
    private string _driftSummary = "Press Check Now to compare every active change with the live system.";

    [ObservableProperty]
    private bool _checkDriftOnLaunch;

    // ---- snapshots
    public ObservableCollection<TweakSnapshotGroup> SnapshotGroups { get; } = new();

    [ObservableProperty]
    private string _snapshotSummary = string.Empty;

    // ---- audit log
    public ObservableCollection<AuditEntry> History { get; } = new();
    public ICollectionView HistoryView { get; }
    public IReadOnlyList<string> ActionFilters { get; } = new[] { AllActions }.Concat(Enum.GetNames<AuditAction>()).ToList();

    [ObservableProperty]
    private string _historySearch = string.Empty;

    [ObservableProperty]
    private string _historyFilter = AllActions;

    // ---- shared
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertTweakCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReapplyAllCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isStatusOpen;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    partial void OnHistorySearchChanged(string value) => HistoryView.Refresh();

    partial void OnHistoryFilterChanged(string value) => HistoryView.Refresh();

    partial void OnCheckDriftOnLaunchChanged(bool value)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(s => s.CheckDriftOnLaunch = value);
        }
    }

    public async Task OnNavigatedToAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            CheckDriftOnLaunch = (await _settings.GetAsync()).CheckDriftOnLaunch;
            _suppress = false;
            IReadOnlyList<AuditEntry> all = await _audit.GetRecentAsync(5000);
            History.Clear();
            foreach (AuditEntry e in all)
            {
                History.Add(e);
            }
        }

        if (_drift.LastResult is { } last)
        {
            ShowDrift(last);
        }

        await RefreshSnapshotsAsync();
    }

    private bool MatchesHistory(AuditEntry e)
    {
        if (HistoryFilter != AllActions && e.Action.ToString() != HistoryFilter)
        {
            return false;
        }

        string q = HistorySearch.Trim();
        return q.Length == 0 ||
               e.Summary.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               (e.TweakId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (e.Target?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (e.Details?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // ------------------------------------------------------------------ drift

    [RelayCommand]
    private async Task CheckDriftAsync()
    {
        IsBusy = true;
        try
        {
            ShowDrift(await _drift.ScanAsync());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowDrift(DriftScanResult result)
    {
        DriftedItems.Clear();
        foreach (DriftItem item in result.Drifted)
        {
            DriftedItems.Add(new DriftRowViewModel(item));
        }

        int unavailable = result.Items.Count(i => i.Status == DriftStatus.Unavailable);
        DriftSummary = result.ActiveCount == 0
            ? "No active changes to check."
            : result.Drifted.Count == 0
                ? $"All {result.ActiveCount} active change(s) are still in place (checked {result.ScannedAt.LocalDateTime:HH:mm:ss})."
                : $"{result.Drifted.Count} of {result.ActiveCount} active change(s) were reverted outside RigTune (checked {result.ScannedAt.LocalDateTime:HH:mm:ss})." +
                  (unavailable > 0 ? $" {unavailable} could not be read." : string.Empty);
    }

    [RelayCommand]
    private async Task ReapplyAsync(DriftRowViewModel? row)
    {
        if (row is null || row.IsFixed)
        {
            return;
        }

        ReapplyResult r = await _drift.ReapplyAsync(row.Item.SnapshotId);
        row.Result = r.Message;
        row.IsFixed = r.Success;
    }

    private bool CanBulk() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBulk))]
    private async Task ReapplyAllAsync()
    {
        IsBusy = true;
        try
        {
            IReadOnlyList<ReapplyResult> results = await _drift.ReapplyAllDriftedAsync();
            ShowStatus(results.All(r => r.Success) ? InfoBarSeverity.Success : InfoBarSeverity.Warning, "Re-apply All Drifted",
                results.Count == 0 ? "Nothing had drifted." : $"{results.Count(r => r.Success)} re-applied, {results.Count(r => !r.Success)} failed.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ snapshots

    [RelayCommand]
    public async Task RefreshSnapshotsAsync()
    {
        IReadOnlyList<SnapshotEntry> active = await _snapshots.GetActiveAsync();
        SnapshotGroups.Clear();
        foreach (IGrouping<string, SnapshotEntry> g in active.GroupBy(e => e.TweakId, StringComparer.OrdinalIgnoreCase))
        {
            SnapshotGroups.Add(new TweakSnapshotGroup(g.Key, _engine.Find(g.Key)?.Name ?? g.First().Description ?? g.Key,
                g.Any(e => e.RequiresRestart),
                g.Select(e => new SnapshotRow(e.Id, e.TargetDisplay, e.OriginalDisplay, e.AppliedDisplay, e.CapturedAtUtc.ToLocalTime(), e.LastError)).ToList()));
        }

        SnapshotSummary = active.Count == 0
            ? "No active changes — your system is in its original state."
            : $"{active.Count} active change(s) across {SnapshotGroups.Count} tweak(s). Each one stores the exact original value.";
        RevertAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanBulk))]
    private async Task RevertTweakAsync(TweakSnapshotGroup? group)
    {
        if (group is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            RevertAllResult r = await _snapshots.RevertTweakAsync(group.TweakId);
            ShowStatus(r.Failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Error, $"Reverted {group.Name}",
                $"{r.Succeeded} restored, {r.Failed} failed.{(r.NeedingRestart.Count > 0 ? " Restart required." : string.Empty)}");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshSnapshotsAsync();
    }

    private bool CanRevertAll() => !IsBusy && SnapshotGroups.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private async Task RevertAllAsync()
    {
        if (!await _dialogs.ConfirmAsync("Revert all changes?",
                $"RigTune will restore the exact original state of every active change ({SnapshotGroups.Sum(g => g.Items.Count)}), newest first.",
                "Revert All", danger: true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            RevertAllResult r = await _snapshots.RevertAllAsync();
            ShowStatus(r.Failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning, "Revert All finished",
                $"{r.Succeeded} restored, {r.Failed} failed.{(r.NeedingRestart.Count > 0 ? $" {r.NeedingRestart.Count} item(s) need a restart." : string.Empty)}");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshSnapshotsAsync();
    }

    // ------------------------------------------------------------------ profiles

    [RelayCommand]
    private async Task ExportProfileAsync()
    {
        var dialog = new SaveFileDialog { Filter = "RigTune profile (*.json)|*.json", FileName = $"RigTune-profile-{DateTime.Now:yyyyMMdd}.json" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            TweakProfile profile = await _profiles.ExportAsync(dialog.FileName);
            ShowStatus(InfoBarSeverity.Success, "Profile exported", $"{profile.Tweaks.Count} active tweak(s) saved to {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Export failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        var dialog = new OpenFileDialog { Filter = "RigTune profile (*.json)|*.json" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            TweakProfile profile = ProfileService.Read(dialog.FileName);
            if (!await _dialogs.ConfirmAsync("Import profile?",
                    $"Apply {profile.Tweaks.Count} tweak(s) exported {profile.ExportedAt.LocalDateTime:yyyy-MM-dd} from {profile.MachineName}?\n\n" +
                    "Each change is snapshotted first. Danger Zone tweaks are skipped unless the Danger Zone is on; device-specific tweaks are skipped.",
                    "Apply profile"))
            {
                return;
            }

            IsBusy = true;
            ProfileImportResult result = await _profiles.ImportAsync(profile);
            ShowStatus(result.Skipped == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning, "Profile imported",
                $"{result.Applied} applied, {result.Skipped} skipped." +
                string.Concat(result.Items.Where(i => !i.Success).Take(8).Select(i => $"\n• {i.Name}: {i.Message}")));
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Import failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshSnapshotsAsync();
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        IsStatusOpen = true;
    }
}
