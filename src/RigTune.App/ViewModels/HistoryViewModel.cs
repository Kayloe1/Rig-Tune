using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigTune.Core.Models;
using RigTune.Core.Services;
using Wpf.Ui.Controls;

namespace RigTune.App.ViewModels;

public sealed record SnapshotRow(Guid Id, string TweakId, string Target, string Original, string Applied,
    DateTimeOffset CapturedAt, bool RequiresRestart, string? LastError);

/// <summary>Backups, Drift &amp; History: active snapshots (per-item / Revert All) and the audit log.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly SnapshotService _snapshots;
    private readonly AuditLogService _audit;

    public HistoryViewModel(SnapshotService snapshots, AuditLogService audit)
    {
        _snapshots = snapshots;
        _audit = audit;
    }

    public ObservableCollection<SnapshotRow> ActiveSnapshots { get; } = new();

    public ObservableCollection<AuditEntry> History { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isStatusOpen;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IReadOnlyList<SnapshotEntry> active = await _snapshots.GetActiveAsync();
        ActiveSnapshots.Clear();
        foreach (SnapshotEntry e in active)
        {
            ActiveSnapshots.Add(new SnapshotRow(e.Id, e.TweakId, e.TargetDisplay, e.OriginalDisplay, e.AppliedDisplay,
                e.CapturedAtUtc.ToLocalTime(), e.RequiresRestart, e.LastError));
        }

        IReadOnlyList<AuditEntry> history = await _audit.GetRecentAsync(500);
        History.Clear();
        foreach (AuditEntry entry in history)
        {
            History.Add(entry);
        }

        RevertAllCommand.NotifyCanExecuteChanged();
    }

    private bool CanRevert() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync(SnapshotRow? row)
    {
        if (row is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            RevertResult result = await _snapshots.RevertAsync(row.Id);
            ShowStatus(result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                result.Success ? "Reverted" : "Revert failed",
                result.Success && result.RequiresRestart ? $"{result.Message} — restart required." : result.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private bool CanRevertAll() => !IsBusy && ActiveSnapshots.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private async Task RevertAllAsync()
    {
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Revert all changes?",
            Content = $"RigTune will restore the exact original state of {ActiveSnapshots.Count} active change(s), newest first.",
            PrimaryButtonText = "Revert All",
            CloseButtonText = "Cancel",
        };
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        IsBusy = true;
        try
        {
            RevertAllResult result = await _snapshots.RevertAllAsync();
            string restart = result.NeedingRestart.Count > 0
                ? $" {result.NeedingRestart.Count} item(s) need a restart to take effect."
                : string.Empty;
            ShowStatus(result.Failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                "Revert All finished",
                $"{result.Succeeded} restored, {result.Failed} failed.{restart}");
        }
        finally
        {
            IsBusy = false;
        }

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
