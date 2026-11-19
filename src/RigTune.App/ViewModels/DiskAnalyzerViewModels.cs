using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigTune.App.Converters;
using RigTune.App.Services;
using RigTune.Core.Models;
using RigTune.Core.Services.DiskAnalyzer;
using Wpf.Ui.Controls;

namespace RigTune.App.ViewModels;

public sealed record DriveItem(string Root, string Label)
{
    public override string ToString() => Label;
}

public sealed record CategorySegment(string Name, long Bytes, Brush Brush, double Weight)
{
    public string Text => $"{Name} · {Format.Bytes(Bytes)}";
}

/// <summary>One lazily expanded node of the folder tree.</summary>
public sealed partial class DiskNodeViewModel : ObservableObject
{
    private static readonly DiskNodeViewModel Placeholder = new();
    private readonly DiskAnalyzerViewModel? _owner;
    private bool _loaded;

    private DiskNodeViewModel()
    {
        Name = "Loading…";
    }

    public DiskNodeViewModel(DiskNode node, long parentSize, DiskAnalyzerViewModel owner, string? syntheticName = null, long? syntheticSize = null)
    {
        Node = node;
        _owner = owner;
        IsSynthetic = syntheticName is not null;
        Name = syntheticName ?? node.Name;
        Size = syntheticSize ?? node.Size;
        Percent = parentSize > 0 ? 100.0 * Size / parentSize : 100;
        if (!IsSynthetic && node.IsDirectory && (node.Children.Count > 0 || node.SmallFileCount > 0))
        {
            Children.Add(Placeholder);
        }
    }

    public DiskNode? Node { get; }
    public bool IsSynthetic { get; }
    public string Name { get; }
    public long Size { get; }
    public double Percent { get; }
    public string SizeText => Format.Bytes(Size);
    public string PercentText => $"{Percent:0.#}%";
    public string FullPath => Node?.FullPath ?? string.Empty;
    public bool IsDirectory => Node?.IsDirectory == true && !IsSynthetic;
    public bool IsInaccessible => Node?.IsInaccessible == true;
    public string Icon => IsSynthetic ? "📄" : IsDirectory ? "📁" : "📄";
    public ObservableCollection<DiskNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private bool _isProtected;

    public bool CanModify => !IsSynthetic && Node is not null;

    [RelayCommand]
    private Task LockAsync() => _owner?.LockCommand.ExecuteAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task UnlockAsync() => _owner?.UnlockCommand.ExecuteAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void OpenInExplorer() => _owner?.OpenInExplorerCommand.Execute(this);

    [RelayCommand]
    private Task DeleteAsync() => _owner?.DeleteCommand.ExecuteAsync(this) ?? Task.CompletedTask;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
        {
            _loaded = true;
            Children.Clear();
            _owner?.PopulateChildren(this);
        }
    }
}

public sealed partial class FindingViewModel : ObservableObject
{
    public FindingViewModel(SpaceFinding finding) => Finding = finding;

    public SpaceFinding Finding { get; }
    public string Title => Finding.Title;
    public string Path => Finding.AllPaths.Count > 1 ? $"{Finding.Path} (+{Finding.AllPaths.Count - 1} more)" : Finding.Path;
    public string SizeText => Finding.Bytes > 0 ? Format.Bytes(Finding.Bytes) : "—";
    public string Reason => Finding.Reason;
    public bool CanAct => Finding.CanAct && !IsDone;

    public string ActionLabel => Finding.Action switch
    {
        AdvisorAction.RecycleContents => "Move contents to Recycle Bin",
        AdvisorAction.RecycleItem => "Move to Recycle Bin",
        AdvisorAction.RunCleanup => "Clean with System Care",
        AdvisorAction.EmptyRecycleBin => "Empty Recycle Bin",
        AdvisorAction.UninstallSteamGame => "Uninstall in Steam",
        AdvisorAction.OpenDiskCleanup => "Open Disk Cleanup",
        _ => string.Empty,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct))]
    private bool _isDone;

    [ObservableProperty]
    private string _result = string.Empty;
}

public sealed partial class DiskAnalyzerViewModel : ObservableObject
{
    private static readonly IReadOnlyDictionary<DiskCategory, Brush> CategoryBrushes = new Dictionary<DiskCategory, Brush>
    {
        [DiskCategory.Games] = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF))),
        [DiskCategory.Media] = Freeze(new SolidColorBrush(Color.FromRgb(0xB1, 0x8C, 0xFF))),
        [DiskCategory.Apps] = Freeze(new SolidColorBrush(Color.FromRgb(0x4E, 0xD6, 0x9A))),
        [DiskCategory.System] = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x95))),
        [DiskCategory.Other] = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xB0, 0x4C))),
    };

    private readonly DiskAnalyzerService _analyzer;
    private readonly DiskLockService _locks;
    private readonly DiskDeletionService _deletion;
    private readonly SpaceAdvisorService _advisor;
    private readonly PathGuard _guard;
    private readonly DialogService _dialogs;
    private CancellationTokenSource? _scanCts;
    private IReadOnlyList<string> _lockSet = Array.Empty<string>();
    private bool _loaded;

    public DiskAnalyzerViewModel(DiskAnalyzerService analyzer, DiskLockService locks, DiskDeletionService deletion,
        SpaceAdvisorService advisor, PathGuard guard, DialogService dialogs)
    {
        _analyzer = analyzer;
        _locks = locks;
        _deletion = deletion;
        _advisor = advisor;
        _guard = guard;
        _dialogs = dialogs;
        foreach (DriveInfo d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            Drives.Add(new DriveItem(d.RootDirectory.FullName,
                $"{d.Name.TrimEnd('\\')} {(string.IsNullOrEmpty(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel)} — {Format.Bytes(d.TotalFreeSpace)} free of {Format.Bytes(d.TotalSize)}"));
        }

        _selectedDrive = Drives.FirstOrDefault();
    }

    public ObservableCollection<DriveItem> Drives { get; } = new();
    public ObservableCollection<CategorySegment> Segments { get; } = new();
    public ObservableCollection<DiskNodeViewModel> RootItems { get; } = new();
    public ObservableCollection<FindingViewModel> SafeFindings { get; } = new();
    public ObservableCollection<FindingViewModel> CheckFindings { get; } = new();
    public ObservableCollection<FindingViewModel> KeepFindings { get; } = new();
    public IReadOnlyList<long> SmallThresholds { get; } = new long[] { 10, 50, 100, 500 };

    [ObservableProperty]
    private DriveItem? _selectedDrive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _scanInfo = "Choose a drive and press Scan.";

    [ObservableProperty]
    private bool _hideSmall = true;

    [ObservableProperty]
    private long _smallThresholdMb = 50;

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private string _advisorSummary = "Press Analyze to look for space you can reclaim. Nothing is deleted without your confirmation.";

    [ObservableProperty]
    private bool _isStatusOpen;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    public string SafeHeader => $"SAFE TO REMOVE · {Format.Bytes(SafeFindings.Sum(f => f.Finding.Bytes))}";
    public string CheckHeader => $"CHECK FIRST · {Format.Bytes(CheckFindings.Sum(f => f.Finding.Bytes))}";
    public string KeepHeader => $"KEEP · {Format.Bytes(KeepFindings.Sum(f => f.Finding.Bytes))}";

    public async Task OnNavigatedToAsync()
    {
        _lockSet = await _locks.GetLocksAsync();
        if (!_loaded)
        {
            _loaded = true;
            await ShowCachedAsync();
        }
    }

    partial void OnSelectedDriveChanged(DriveItem? value) => _ = ShowCachedAsync();

    partial void OnHideSmallChanged(bool value) => RebuildTree();

    partial void OnSmallThresholdMbChanged(long value) => RebuildTree();

    private async Task ShowCachedAsync()
    {
        if (SelectedDrive is null)
        {
            return;
        }

        if (_analyzer.GetCached(SelectedDrive.Root) is { } cached)
        {
            ShowResult(cached);
            return;
        }

        RootItems.Clear();
        Segments.Clear();
        DriveSummary? summary = await _analyzer.GetCachedSummaryAsync(SelectedDrive.Root);
        if (summary is not null)
        {
            ShowSegments(summary.CategoryBytes);
            ScanInfo = $"Last scan {summary.ScannedAt.LocalDateTime:yyyy-MM-dd HH:mm}: {Format.Bytes(summary.ScannedBytes)} in {summary.Files:N0} files. Scan again to browse the folder tree.";
        }
        else
        {
            ScanInfo = "This drive has not been scanned yet.";
        }
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (SelectedDrive is null)
        {
            return;
        }

        IsScanning = true;
        _scanCts = new CancellationTokenSource();
        var drive = new DriveInfo(SelectedDrive.Root);
        long used = Math.Max(1, drive.TotalSize - drive.TotalFreeSpace);
        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressValue = Math.Min(100, 100.0 * p.Bytes / used);
            ProgressText = $"{p.Files:N0} files · {Format.Bytes(p.Bytes)} · {p.Skipped:N0} folders skipped (no access)\n{p.CurrentPath}";
        });

        try
        {
            ShowResult(await _analyzer.ScanAsync(SelectedDrive.Root, progress, _scanCts.Token));
        }
        catch (OperationCanceledException)
        {
            ScanInfo = "Scan cancelled.";
        }
        finally
        {
            IsScanning = false;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private void ShowResult(DriveScanResult result)
    {
        ShowSegments(result.CategoryBytes);
        ScanInfo = $"Scanned {result.ScannedAt.LocalDateTime:HH:mm} in {result.Duration.TotalSeconds:0.0}s: {Format.Bytes(result.Root.Size)} in {result.Root.FileCount:N0} files" +
                   (result.SkippedFolders > 0 ? $" · {result.SkippedFolders:N0} protected folders skipped" : string.Empty);
        RebuildTree();
    }

    private void ShowSegments(IReadOnlyDictionary<DiskCategory, long> bytes)
    {
        Segments.Clear();
        foreach ((DiskCategory category, long size) in bytes.OrderBy(b => b.Key))
        {
            Segments.Add(new CategorySegment(category.ToString(), size, CategoryBrushes[category], size));
        }
    }

    private void RebuildTree()
    {
        RootItems.Clear();
        if (SelectedDrive is null || _analyzer.GetCached(SelectedDrive.Root) is not { } result)
        {
            return;
        }

        var root = new DiskNodeViewModel(result.Root, result.Root.Size, this);
        Decorate(root);
        RootItems.Add(root);
        root.IsExpanded = true;
    }

    internal void PopulateChildren(DiskNodeViewModel parent)
    {
        if (parent.Node is not { } node)
        {
            return;
        }

        long threshold = HideSmall ? SmallThresholdMb * 1024 * 1024 : 0;
        int hidden = 0;
        long hiddenBytes = 0;
        foreach (DiskNode child in node.Children.OrderByDescending(c => c.Size))
        {
            if (child.Size < threshold)
            {
                hidden++;
                hiddenBytes += child.Size;
                continue;
            }

            var vm = new DiskNodeViewModel(child, node.Size, this);
            Decorate(vm);
            parent.Children.Add(vm);
        }

        hiddenBytes += node.SmallFilesBytes;
        if (hiddenBytes > 0)
        {
            string label = hidden > 0
                ? $"{hidden:N0} smaller item(s) + {node.SmallFileCount:N0} small files"
                : $"{node.SmallFileCount:N0} small files";
            parent.Children.Add(new DiskNodeViewModel(node, node.Size, this, label, hiddenBytes));
        }
    }

    private void Decorate(DiskNodeViewModel vm)
    {
        vm.IsLocked = PathGuard.IsLocked(vm.FullPath, _lockSet);
        vm.IsProtected = _guard.IsHardProtected(vm.FullPath);
    }

    private void RefreshDecorations(IEnumerable<DiskNodeViewModel> items)
    {
        foreach (DiskNodeViewModel item in items.Where(i => !i.IsSynthetic && i.Node is not null))
        {
            Decorate(item);
            RefreshDecorations(item.Children);
        }
    }

    // ------------------------------------------------------------------ context actions

    [RelayCommand]
    private async Task LockAsync(DiskNodeViewModel? item)
    {
        if (item is { IsSynthetic: false, Node: not null })
        {
            await _locks.LockAsync(item.FullPath);
            _lockSet = await _locks.GetLocksAsync();
            RefreshDecorations(RootItems);
        }
    }

    [RelayCommand]
    private async Task UnlockAsync(DiskNodeViewModel? item)
    {
        if (item is { IsSynthetic: false, Node: not null })
        {
            await _locks.UnlockAsync(item.FullPath);
            _lockSet = await _locks.GetLocksAsync();
            RefreshDecorations(RootItems);
        }
    }

    [RelayCommand]
    private static void OpenInExplorer(DiskNodeViewModel? item)
    {
        if (item is { Node: not null } && !string.IsNullOrEmpty(item.FullPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", item.IsDirectory ? $"\"{item.FullPath}\"" : $"/select,\"{item.FullPath}\"")
            {
                UseShellExecute = true,
            });
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(DiskNodeViewModel? item)
    {
        if (item is not { IsSynthetic: false, Node: not null })
        {
            return;
        }

        DeletionCheck check = await _deletion.CheckAsync(item.FullPath);
        if (!check.Allowed)
        {
            ShowStatus(InfoBarSeverity.Warning, "Can't delete this item", check.Reason);
            return;
        }

        if (!await _dialogs.ConfirmAsync("Move to Recycle Bin?",
                $"{item.FullPath}\n\nSize: {item.SizeText}{(item.IsDirectory ? $" in {item.Node.FileCount:N0} files" : string.Empty)}\n\nIt goes to the Recycle Bin, so you can restore it until the bin is emptied.",
                "Move to Recycle Bin", danger: true))
        {
            return;
        }

        DeletionResult r = await _deletion.RecycleAsync(new DeletionRequest(item.FullPath, item.Size, Confirmed: true));
        ShowStatus(r.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error, r.Success ? "Moved to Recycle Bin" : "Not deleted", $"{item.FullPath}\n{r.Message}");
        if (r.Success)
        {
            RebuildTree();
        }
    }

    // ------------------------------------------------------------------ space advisor

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        IsAnalyzing = true;
        try
        {
            IReadOnlyList<SpaceFinding> findings = await _advisor.AnalyzeAsync();
            SafeFindings.Clear();
            CheckFindings.Clear();
            KeepFindings.Clear();
            foreach (SpaceFinding f in findings)
            {
                (f.Group switch
                {
                    AdvisorGroup.SafeToRemove => SafeFindings,
                    AdvisorGroup.CheckFirst => CheckFindings,
                    _ => KeepFindings,
                }).Add(new FindingViewModel(f));
            }

            OnPropertyChanged(nameof(SafeHeader));
            OnPropertyChanged(nameof(CheckHeader));
            OnPropertyChanged(nameof(KeepHeader));
            AdvisorSummary = $"Safe to remove: {Format.Bytes(findings.Where(f => f.Group == AdvisorGroup.SafeToRemove).Sum(f => f.Bytes))} · " +
                             $"Check first: {Format.Bytes(findings.Where(f => f.Group == AdvisorGroup.CheckFirst).Sum(f => f.Bytes))}";
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Space Advisor failed", ex.Message);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private async Task ActOnFindingAsync(FindingViewModel? item)
    {
        if (item is not { CanAct: true })
        {
            return;
        }

        string paths = string.Join("\n", item.Finding.AllPaths.Take(8)) + (item.Finding.AllPaths.Count > 8 ? "\n…" : string.Empty);
        if (!await _dialogs.ConfirmAsync($"{item.ActionLabel}?", $"{item.Title}\n\n{paths}\n\nSize: {item.SizeText}\n\n{item.Reason}", item.ActionLabel,
                danger: item.Finding.Action is AdvisorAction.EmptyRecycleBin or AdvisorAction.RecycleItem))
        {
            return;
        }

        item.Result = await _advisor.ExecuteAsync(item.Finding, confirmed: true);
        item.IsDone = item.Finding.Action is not (AdvisorAction.UninstallSteamGame or AdvisorAction.OpenDiskCleanup);
        ShowStatus(InfoBarSeverity.Success, item.Title, item.Result);
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        IsStatusOpen = true;
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
