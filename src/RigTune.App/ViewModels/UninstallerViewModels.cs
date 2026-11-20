using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigTune.App.Converters;
using RigTune.App.Services;
using RigTune.Core.Models;
using RigTune.Core.Services;
using RigTune.Core.Services.Uninstaller;
using Wpf.Ui.Controls;

namespace RigTune.App.ViewModels;

public sealed partial class ProgramItemViewModel : ObservableObject
{
    public ProgramItemViewModel(InstalledProgram program, bool supportsQuiet)
    {
        Program = program;
        SupportsQuiet = supportsQuiet;
        Warning = UninstallCautions.Warning(program);
    }

    public InstalledProgram Program { get; }
    public bool SupportsQuiet { get; }
    public string? Warning { get; }
    public bool HasWarning => Warning is not null;

    public string Name => Program.DisplayName;
    public string Publisher => Program.Publisher ?? "Unknown publisher";
    public string Version => Program.Version ?? string.Empty;
    public long SizeBytes => Program.EstimatedSizeBytes ?? 0;
    public string SizeText => Program.EstimatedSizeBytes is { } b ? Format.Bytes(b) : "—";
    public DateOnly InstallDateSort => Program.InstallDate ?? DateOnly.MinValue;
    public string InstallDateText => Program.InstallDate?.ToString("d MMM yyyy") ?? string.Empty;
    public string Scope => Program.ScopeLabel;
    public string ModeText => SupportsQuiet ? (Program.IsMsi ? "MSI · silent OK" : "Silent OK") : "Opens its uninstaller";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _resultText = string.Empty;
}

public sealed partial class LeftoverItemViewModel : ObservableObject
{
    public LeftoverItemViewModel(Leftover leftover)
    {
        Leftover = leftover;
        _isSelected = leftover.DefaultSelected;
    }

    public Leftover Leftover { get; }
    public string Path => Leftover.Path;
    public string Reason => Leftover.Reason;
    public string ProgramName => Leftover.ProgramName;
    public bool IsReviewOnly => Leftover.Confidence == LeftoverConfidence.Medium;

    public string KindText => Leftover.Kind switch
    {
        LeftoverKind.Folder => "Folder",
        LeftoverKind.File => "Shortcut",
        _ => "Registry key",
    };

    public string SizeText => Leftover.Kind == LeftoverKind.RegistryKey ? string.Empty : Format.Bytes(Leftover.SizeBytes);

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _resultText = string.Empty;
}

/// <summary>Uninstaller page: list, search and batch-uninstall programs, then clean up their leftovers.</summary>
public sealed partial class UninstallerViewModel : StatusViewModel
{
    private readonly UninstallerService _service;
    private readonly RestorePointService _restorePoints;
    private readonly DialogService _dialogs;
    private CancellationTokenSource? _batchCts;
    private bool _loaded;

    public UninstallerViewModel(UninstallerService service, RestorePointService restorePoints, DialogService dialogs)
    {
        _service = service;
        _restorePoints = restorePoints;
        _dialogs = dialogs;
        ProgramsView = CollectionViewSource.GetDefaultView(Programs);
        ProgramsView.Filter = o => o is ProgramItemViewModel p && Matches(p);
        ApplySort();
    }

    public ObservableCollection<ProgramItemViewModel> Programs { get; } = new();
    public ICollectionView ProgramsView { get; }
    public ObservableCollection<LeftoverItemViewModel> Leftovers { get; } = new();

    public IReadOnlyList<string> SortOptions { get; } = new[] { "Name", "Size (largest first)", "Install date (newest first)", "Publisher" };

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedSort = "Name";

    [ObservableProperty]
    private bool _preferQuiet = true;

    [ObservableProperty]
    private bool _scanLeftoversAfter = true;

    [ObservableProperty]
    private string _summary = "Loading installed programs…";

    [ObservableProperty]
    private string _selectionText = "Nothing selected";

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private bool _hasLeftovers;

    [ObservableProperty]
    private string _leftoverSummary = string.Empty;

    partial void OnSearchTextChanged(string value) => ProgramsView.Refresh();

    partial void OnSelectedSortChanged(string value) => ApplySort();

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
        IsBusy = true;
        try
        {
            HashSet<string> keep = Programs.Where(p => p.IsSelected).Select(p => p.Program.Id).ToHashSet();
            IReadOnlyList<InstalledProgram> list = await _service.ListAsync();
            foreach (ProgramItemViewModel old in Programs)
            {
                old.PropertyChanged -= OnItemChanged;
            }

            Programs.Clear();
            foreach (InstalledProgram p in list)
            {
                var item = new ProgramItemViewModel(p, _service.SupportsQuiet(p)) { IsSelected = keep.Contains(p.Id) };
                item.PropertyChanged += OnItemChanged;
                Programs.Add(item);
            }

            long total = list.Sum(p => p.EstimatedSizeBytes ?? 0);
            Summary = $"{list.Count} programs installed · {Format.Bytes(total)} reported by their installers";
            UpdateSelection();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Could not read installed programs", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (ProgramItemViewModel p in Programs)
        {
            p.IsSelected = false;
        }
    }

    [RelayCommand]
    private Task UninstallOneAsync(ProgramItemViewModel? item) =>
        item is null ? Task.CompletedTask : UninstallAsync(new[] { item });

    [RelayCommand]
    private Task UninstallSelectedAsync() => UninstallAsync(Programs.Where(p => p.IsSelected).ToList());

    [RelayCommand]
    private void StopBatch()
    {
        _batchCts?.Cancel();
        ProgressText = "Stopping after the current program… (its uninstaller keeps running if it is open)";
    }

    private async Task UninstallAsync(IReadOnlyList<ProgramItemViewModel> items)
    {
        if (IsRunning)
        {
            return;
        }

        if (items.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "Nothing selected", "Tick the programs to uninstall, or use the button on a single row.");
            return;
        }

        int silent = items.Count(i => i.SupportsQuiet);
        string mode = PreferQuiet
            ? silent == items.Count
                ? "All of them can be removed silently."
                : $"{silent} can be removed silently; {items.Count - silent} will open their own uninstaller window — answer it to continue."
            : "Each program's own uninstaller window will open, one at a time.";
        string list = string.Join("\n", items.Take(25).Select(i => $"• {i.Name}{(i.HasWarning ? "  ⚠ " + i.Warning : string.Empty)}")) +
                      (items.Count > 25 ? $"\n… and {items.Count - 25} more" : string.Empty);
        string leftovers = ScanLeftoversAfter ? "\n\nAfterwards RigTune looks for leftover folders and registry keys for you to review." : string.Empty;
        if (!await _dialogs.ConfirmAsync(items.Count == 1 ? $"Uninstall {items[0].Name}?" : $"Uninstall {items.Count} programs?",
                $"{list}\n\n{mode}{leftovers}", "Uninstall", danger: items.Any(i => i.HasWarning)))
        {
            return;
        }

        if (!await OfferRestorePointAsync(items))
        {
            return;
        }

        IsRunning = true;
        IsBusy = true;
        _batchCts = new CancellationTokenSource();
        Leftovers.Clear();
        HasLeftovers = false;
        IReadOnlyList<UninstallResult> results;
        try
        {
            var progress = new Progress<(int Index, InstalledProgram Program)>(p =>
            {
                ProgressText = $"Uninstalling {p.Index + 1} of {items.Count}: {p.Program.DisplayName}…" +
                               (PreferQuiet && UninstallCommandBuilder.SupportsQuiet(p.Program) ? string.Empty : "  (finish it in the uninstaller window)");
                ProgramItemViewModel? row = items.FirstOrDefault(i => i.Program.Id == p.Program.Id);
                if (row is not null)
                {
                    row.ResultText = "Uninstalling…";
                }
            });
            results = await _service.UninstallBatchAsync(items.Select(i => i.Program).ToList(), PreferQuiet, progress, _batchCts.Token);
        }
        finally
        {
            _batchCts.Dispose();
            _batchCts = null;
            IsRunning = false;
            IsBusy = false;
            ProgressText = string.Empty;
        }

        foreach (UninstallResult r in results)
        {
            if (items.FirstOrDefault(i => i.Program.Id == r.Program.Id) is { } row)
            {
                row.ResultText = r.Message;
            }
        }

        int removed = results.Count(r => r.Outcome == UninstallOutcome.Removed);
        int notRemoved = results.Count - removed;
        string details = string.Join("\n", results.Where(r => r.Outcome != UninstallOutcome.Removed)
            .Select(r => $"{r.Program.DisplayName}: {r.Message}"));
        ShowStatus(notRemoved == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            $"{removed} of {results.Count} uninstalled" + (results.Any(r => r.RestartRequired) ? " · restart Windows to finish" : string.Empty),
            details.Length > 0 ? details : "Everything selected was removed.");

        if (ScanLeftoversAfter && results.Any(r => r.CanScanLeftovers))
        {
            await ScanLeftoversAsync(results);
        }

        await RefreshAsync();
    }

    private async Task ScanLeftoversAsync(IReadOnlyList<UninstallResult> results)
    {
        ProgressText = "Looking for leftovers…";
        IsBusy = true;
        try
        {
            IReadOnlyList<Leftover> found = await _service.ScanLeftoversAsync(results);
            Leftovers.Clear();
            foreach (Leftover l in found.OrderBy(l => l.ProgramName).ThenBy(l => l.Confidence).ThenBy(l => l.Kind))
            {
                Leftovers.Add(new LeftoverItemViewModel(l));
            }

            HasLeftovers = Leftovers.Count > 0;
            long bytes = found.Where(l => l.Kind != LeftoverKind.RegistryKey).Sum(l => l.SizeBytes);
            LeftoverSummary = HasLeftovers
                ? $"{found.Count(l => l.Kind == LeftoverKind.Folder)} folders, {found.Count(l => l.Kind == LeftoverKind.File)} shortcuts and " +
                  $"{found.Count(l => l.Kind == LeftoverKind.RegistryKey)} registry keys ({Format.Bytes(bytes)}). " +
                  "Exact matches are ticked; publisher-level items are only suggestions."
                : string.Empty;
            if (!HasLeftovers)
            {
                StatusMessage += "\nNo leftovers found.";
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Leftover scan failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task RemoveLeftoversAsync()
    {
        List<LeftoverItemViewModel> selected = Leftovers.Where(l => l.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "No leftovers selected", "Tick the folders and keys to remove.");
            return;
        }

        int files = selected.Count(l => l.Leftover.Kind != LeftoverKind.RegistryKey);
        int keys = selected.Count - files;
        string review = selected.Any(l => l.IsReviewOnly) ? "\n\n⚠ Includes publisher-level items you ticked manually." : string.Empty;
        if (!await _dialogs.ConfirmAsync("Remove leftovers?",
                $"{files} folder(s)/shortcut(s) go to the Recycle Bin.\n{keys} registry key(s) are backed up to a .reg file first " +
                $"(double-click it to restore), then deleted.{review}", "Remove", danger: selected.Any(l => l.IsReviewOnly)))
        {
            return;
        }

        IsBusy = true;
        try
        {
            IReadOnlyList<LeftoverRemovalResult> results = await _service.RemoveLeftoversAsync(selected.Select(l => l.Leftover).ToList());
            foreach (LeftoverRemovalResult r in results)
            {
                if (selected.FirstOrDefault(l => l.Leftover == r.Leftover) is { } row)
                {
                    row.ResultText = r.Message;
                    row.IsSelected = false;
                }
            }

            int ok = results.Count(r => r.Success);
            ShowStatus(ok == results.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning, $"{ok} of {results.Count} leftovers removed",
                string.Join("\n", results.Where(r => !r.Success).Select(r => $"{r.Leftover.Path}: {r.Message}")) is { Length: > 0 } failed
                    ? failed
                    : "Folders are in the Recycle Bin; registry backups are in the backups folder.");
            foreach (LeftoverItemViewModel done in selected.Where(l => results.Any(r => r.Success && r.Leftover == l.Leftover)))
            {
                Leftovers.Remove(done);
            }

            HasLeftovers = Leftovers.Count > 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DismissLeftovers()
    {
        Leftovers.Clear();
        HasLeftovers = false;
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        Directory.CreateDirectory(_service.BackupFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_service.BackupFolder}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private static void OpenWindowsSettings() => Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });

    private async Task<bool> OfferRestorePointAsync(IReadOnlyList<ProgramItemViewModel> items)
    {
        string action = items.Count == 1 ? $"Uninstall \"{items[0].Name}\"." : $"Uninstall {items.Count} programs.";
        switch (await _dialogs.AskRestorePointAsync(action))
        {
            case RestorePointChoice.Cancel:
                return false;
            case RestorePointChoice.ContinueWithout:
                return true;
        }

        ShowStatus(InfoBarSeverity.Informational, "Creating restore point…", "This can take a minute.");
        RestorePointCreateResult rp = await _restorePoints.CreateAsync(
            items.Count == 1 ? $"RigTune: before uninstalling {items[0].Name}" : $"RigTune: before uninstalling {items.Count} programs");
        if (!rp.Success)
        {
            ShowStatus(InfoBarSeverity.Error, "Restore point failed, nothing was uninstalled", rp.Message);
            return false;
        }

        IsStatusOpen = false;
        return true;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProgramItemViewModel.IsSelected))
        {
            UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        List<ProgramItemViewModel> selected = Programs.Where(p => p.IsSelected).ToList();
        SelectedCount = selected.Count;
        SelectionText = selected.Count == 0
            ? "Nothing selected"
            : $"{selected.Count} selected · {Format.Bytes(selected.Sum(p => p.SizeBytes))}";
    }

    private bool Matches(ProgramItemViewModel p)
    {
        string q = SearchText.Trim();
        return q.Length == 0 ||
               p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               p.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySort()
    {
        ProgramsView.SortDescriptions.Clear();
        (string property, ListSortDirection direction) = SelectedSort switch
        {
            "Size (largest first)" => (nameof(ProgramItemViewModel.SizeBytes), ListSortDirection.Descending),
            "Install date (newest first)" => (nameof(ProgramItemViewModel.InstallDateSort), ListSortDirection.Descending),
            "Publisher" => (nameof(ProgramItemViewModel.Publisher), ListSortDirection.Ascending),
            _ => (nameof(ProgramItemViewModel.Name), ListSortDirection.Ascending),
        };
        ProgramsView.SortDescriptions.Add(new SortDescription(property, direction));
        if (property != nameof(ProgramItemViewModel.Name))
        {
            ProgramsView.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.Name), ListSortDirection.Ascending));
        }
    }
}
