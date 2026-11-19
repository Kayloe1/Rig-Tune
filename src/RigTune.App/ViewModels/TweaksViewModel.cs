using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RigTune.App.Services;
using RigTune.Core.Hardware;
using RigTune.Core.Models;
using RigTune.Core.Runners;
using RigTune.Core.Runners.Handlers;
using RigTune.Core.Services;
using Wpf.Ui.Controls;

namespace RigTune.App.ViewModels;

public enum TweakFilter
{
    All,
    Active,
    NeedsRestart,
    DangerZone,
}

public sealed partial class TweaksViewModel : ObservableObject
{
    private readonly TweakEngine _engine;
    private readonly SettingsService _settings;
    private readonly RestorePointService _restorePoints;
    private readonly DialogService _dialogs;
    private readonly PciRunner _pci;
    private readonly NetworkAdapterRunner _network;
    private readonly DeviceManagerRunner _devices;
    private readonly PowerRunner _power;
    private readonly RegistryActionHandler _registryHandler;
    private bool _loaded;

    public TweaksViewModel(TweakEngine engine, SettingsService settings, RestorePointService restorePoints,
        DialogService dialogs, PciRunner pci, NetworkAdapterRunner network, DeviceManagerRunner devices, PowerRunner power,
        RegistryActionHandler registryHandler)
    {
        _engine = engine;
        _settings = settings;
        _restorePoints = restorePoints;
        _dialogs = dialogs;
        _pci = pci;
        _network = network;
        _devices = devices;
        _power = power;
        _registryHandler = registryHandler;

        foreach (TweakDefinition t in engine.Tweaks)
        {
            Items.Add(new TweakItemViewModel(t));
        }

        View = CollectionViewSource.GetDefaultView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TweakItemViewModel.GroupKey)));
        View.Filter = o => o is TweakItemViewModel item && Matches(item);
    }

    public ObservableCollection<TweakItemViewModel> Items { get; } = new();

    public ICollectionView View { get; }

    public IReadOnlyList<TweakFilter> Filters { get; } = Enum.GetValues<TweakFilter>();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private TweakFilter _filter = TweakFilter.All;

    [ObservableProperty]
    private bool _isDangerZoneEnabled;

    [ObservableProperty]
    private bool _isLoadingStatus;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    private bool _isBusy;

    // ---- status banner
    [ObservableProperty]
    private bool _isStatusOpen;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    // ---- inspector drawer
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInspectorOpen))]
    private TweakItemViewModel? _selected;

    public bool IsInspectorOpen => Selected is not null;

    public ObservableCollection<ChoiceItem> Options { get; } = new();

    [ObservableProperty]
    private ChoiceItem? _selectedOption;

    public ObservableCollection<SliderItem> Sliders { get; } = new();

    public ObservableCollection<SelectableItem> Selectables { get; } = new();

    public ObservableCollection<CoreItem> Cores { get; } = new();

    public ObservableCollection<PowerSettingInfo> ExpertSettings { get; } = new();

    public ObservableCollection<ChoiceItem> StateChoices { get; } = new();

    [ObservableProperty]
    private ChoiceItem? _selectedState;

    public IReadOnlyList<ChoiceItem> PriorityChoices { get; } = new[]
    {
        new ChoiceItem("-1", "Leave unchanged"), new ChoiceItem("1", "Low"), new ChoiceItem("2", "Normal"), new ChoiceItem("3", "High"),
    };

    [ObservableProperty]
    private ChoiceItem? _selectedPriority;

    [ObservableProperty]
    private string _interactiveHint = string.Empty;

    [ObservableProperty]
    private string _hardwareChecks = string.Empty;

    [ObservableProperty]
    private bool _isInspectorLoading;

    public bool ShowSelectables => Selected?.Definition.Interactive is "pci_msi" or "interrupt_affinity" or "network_adapters" or "devices";
    public bool ShowCores => Selected?.Definition.Interactive == "interrupt_affinity";
    public bool ShowPriority => Selected?.Definition.Interactive is "pci_msi" or "interrupt_affinity";
    public bool ShowStateChoices => Selected?.Definition.Interactive == "network_adapters";
    public bool ShowExpert => Selected?.Definition.Interactive == "power_plan";

    // ------------------------------------------------------------------ lifecycle

    public async Task OnNavigatedToAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        IsDangerZoneEnabled = (await _settings.GetAsync()).DangerZoneEnabled;
        UpdateLocks();
        await RefreshStatusesAsync();
    }

    partial void OnSearchTextChanged(string value) => View.Refresh();

    partial void OnFilterChanged(TweakFilter value) => View.Refresh();

    private bool Matches(TweakItemViewModel item)
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string q = SearchText.Trim();
            if (!item.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !item.Description.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !item.GroupKey.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !item.TargetSummary.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return Filter switch
        {
            TweakFilter.Active => item.IsActive,
            TweakFilter.NeedsRestart => item.RequiresRestart,
            TweakFilter.DangerZone => item.IsDangerZone,
            _ => true,
        };
    }

    [RelayCommand]
    private void SetFilter(TweakFilter filter) => Filter = filter;

    [RelayCommand]
    private async Task RefreshStatusesAsync()
    {
        IsLoadingStatus = true;
        try
        {
            using var throttle = new SemaphoreSlim(4);
            await Task.WhenAll(Items.Select(async item =>
            {
                await throttle.WaitAsync();
                try
                {
                    await RefreshItemAsync(item);
                }
                finally
                {
                    throttle.Release();
                }
            }));
        }
        finally
        {
            IsLoadingStatus = false;
            UpdateSummary();
            View.Refresh();
        }
    }

    private async Task RefreshItemAsync(TweakItemViewModel item, TweakParameters? parameters = null)
    {
        try
        {
            TweakStatusReport report = await Task.Run(() => _engine.GetStatusAsync(item.Definition, parameters));
            item.Update(report);
        }
        catch (Exception ex)
        {
            item.StatusText = $"Status unavailable: {ex.Message}";
        }
    }

    private void UpdateSummary()
    {
        int active = Items.Count(i => i.IsActive && !i.IsPreset && !i.IsReadOnly);
        Summary = $"{Items.Count} tweaks · {active} active · {Items.Count(i => i.IsDangerZone)} Danger Zone";
    }

    // ------------------------------------------------------------------ danger zone

    partial void OnIsDangerZoneEnabledChanged(bool value)
    {
        if (!_loaded)
        {
            return;
        }

        _ = PersistDangerZoneAsync(value);
    }

    private async Task PersistDangerZoneAsync(bool value)
    {
        if (value && !await _dialogs.ConfirmAsync("Enable the Danger Zone?",
                "Danger Zone tweaks can reduce security (VBS, Spectre mitigations), break WSL2/Hyper-V, stop Windows Update or " +
                "disable recovery. Each one still snapshots and can be reverted, and you will be offered a restore point first.",
                "Enable Danger Zone", danger: true))
        {
            _loaded = false; // suppress re-entry while flipping back
            IsDangerZoneEnabled = false;
            _loaded = true;
            return;
        }

        await _settings.UpdateAsync(s => s.DangerZoneEnabled = value);
        UpdateLocks();
    }

    private void UpdateLocks()
    {
        foreach (TweakItemViewModel item in Items)
        {
            item.IsLocked = item.IsDangerZone && !IsDangerZoneEnabled;
        }
    }

    // ------------------------------------------------------------------ apply / revert

    [RelayCommand]
    private async Task ApplyAsync(TweakItemViewModel? item)
    {
        if (item is null || item.IsBusy)
        {
            return;
        }

        if (item.IsLocked)
        {
            ShowStatus(InfoBarSeverity.Warning, "Danger Zone is locked", "Turn on the Danger Zone switch to apply this tweak.");
            return;
        }

        TweakParameters parameters = item == Selected ? BuildParameters() : TweakParameters.None;
        if ((item.IsDangerZone || item.IsPreset) && !await OfferRestorePointAsync($"You are about to apply \"{item.Name}\"."))
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            TweakApplyResult result = await Task.Run(() => _engine.ApplyAsync(item.Definition, parameters));
            ShowStatus(result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                result.Success ? $"{item.Name}: done{(result.RequiresRestart ? " — restart required" : string.Empty)}" : $"{item.Name}: failed",
                result.Message);
        }
        finally
        {
            item.IsBusy = false;
        }

        if (item.IsPreset)
        {
            await RefreshStatusesAsync();
        }
        else
        {
            await RefreshItemAsync(item, parameters);
            UpdateSummary();
        }
    }

    [RelayCommand]
    private async Task RevertAsync(TweakItemViewModel? item)
    {
        if (item is null || item.IsBusy)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            TweakApplyResult result = await Task.Run(() => _engine.RevertAsync(item.Definition));
            ShowStatus(result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                $"{item.Name}: {(result.Success ? "reverted" : "revert failed")}{(result.RequiresRestart ? " — restart required" : string.Empty)}",
                result.Message);
        }
        finally
        {
            item.IsBusy = false;
        }

        await RefreshItemAsync(item);
        UpdateSummary();
    }

    /// <summary>Area-level "Apply Recommended" from a group header (group key "Category › Subcategory").</summary>
    [RelayCommand]
    private async Task ApplyPresetAsync(string? groupKey)
    {
        TweakItemViewModel? sample = Items.FirstOrDefault(i => i.GroupKey == groupKey);
        if (sample is null)
        {
            return;
        }

        var scope = new PresetScope { Category = sample.Definition.Category, Subcategory = sample.Definition.Subcategory };
        IReadOnlyList<TweakDefinition> members = _engine.GetPresetTweaks(scope);
        if (members.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "Nothing to apply", "This area has no Recommended tweaks.");
            return;
        }

        if (!await OfferRestorePointAsync($"You are about to apply {members.Count} Recommended tweak(s) in {groupKey}. Danger Zone and permanent tweaks are never included."))
        {
            return;
        }

        IsBusy = true;
        try
        {
            PresetResult result = await Task.Run(() => _engine.ApplyPresetAsync(scope));
            ShowStatus(result.Failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                $"Recommended preset applied{(result.RequiresRestart ? " — restart required" : string.Empty)}",
                $"{result.Succeeded} applied, {result.Failed} failed." +
                string.Concat(result.Results.Where(r => !r.Success).Select(r => $"\n• {r.TweakId}: {r.Message}")));
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshStatusesAsync();
    }

    private bool CanRevertAll() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private async Task RevertAllAsync()
    {
        if (!await _dialogs.ConfirmAsync("Revert all tweaks?",
                "RigTune will restore the exact original state of every active change, newest first.", "Revert All", danger: true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            RevertAllResult result = await Task.Run(() => _engine.RevertAllAsync());
            ShowStatus(result.Failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning, "Revert All finished",
                $"{result.Succeeded} restored, {result.Failed} failed." +
                (result.NeedingRestart.Count > 0 ? $" {result.NeedingRestart.Count} item(s) need a restart." : string.Empty));
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshStatusesAsync();
    }

    /// <returns>False when the user cancelled.</returns>
    private async Task<bool> OfferRestorePointAsync(string action)
    {
        switch (await _dialogs.AskRestorePointAsync(action))
        {
            case RestorePointChoice.Cancel:
                return false;
            case RestorePointChoice.ContinueWithout:
                return true;
        }

        ShowStatus(InfoBarSeverity.Informational, "Creating restore point…", "This can take a minute.");
        RestorePointCreateResult rp = await _restorePoints.CreateAsync($"RigTune: before {action.Split('"').ElementAtOrDefault(1) ?? "tweaks"}");
        if (!rp.Success)
        {
            ShowStatus(InfoBarSeverity.Error, "Restore point failed — nothing was changed", rp.Message);
            return false;
        }

        return true;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        IsStatusOpen = true;
    }

    // ------------------------------------------------------------------ inspector

    [RelayCommand]
    private async Task SelectAsync(TweakItemViewModel? item)
    {
        Selected = item;
        Options.Clear();
        Sliders.Clear();
        Selectables.Clear();
        Cores.Clear();
        ExpertSettings.Clear();
        StateChoices.Clear();
        InteractiveHint = string.Empty;
        HardwareChecks = string.Empty;
        SelectedPriority = PriorityChoices[0];
        OnPropertyChanged(nameof(ShowSelectables));
        OnPropertyChanged(nameof(ShowCores));
        OnPropertyChanged(nameof(ShowPriority));
        OnPropertyChanged(nameof(ShowStateChoices));
        OnPropertyChanged(nameof(ShowExpert));
        if (item is null)
        {
            return;
        }

        TweakDefinition t = item.Definition;
        foreach (TweakOption o in t.Options ?? new())
        {
            Options.Add(new ChoiceItem(o.Id, o.Name, o.Description));
        }

        SelectedOption = Options.FirstOrDefault(o => o.Id == t.DefaultOption) ?? Options.FirstOrDefault();

        foreach (TweakAction a in t.Actions.Where(a => a.Range is not null && a.Key is not null))
        {
            Sliders.Add(new SliderItem(a.Key!, SliderLabel(a.Key!), a.Range!.Min, a.Range.Max, a.Range.Step, a.Range.Unit,
                a.AcValue ?? a.Range.Min));
        }

        IsInspectorLoading = true;
        try
        {
            await LoadInteractiveAsync(t);
            await RefreshItemAsync(item);
        }
        catch (Exception ex)
        {
            InteractiveHint = $"Could not read live hardware data: {ex.Message}";
        }
        finally
        {
            IsInspectorLoading = false;
        }
    }

    [RelayCommand]
    private void CloseInspector() => Selected = null;

    private async Task LoadInteractiveAsync(TweakDefinition t)
    {
        switch (t.Interactive)
        {
            case "pci_msi":
                foreach (PciDeviceInfo d in await Task.Run(() => _pci.EnumeratePciDevices()))
                {
                    Selectables.Add(new SelectableItem(d.Device.InstanceId, d.Device.Name,
                        $"{d.Category} · MSI {(d.MsiSupported switch { true => "on", false => "off", _ => "driver default" })}", d.Category.ToString()));
                }

                InteractiveHint = "Tick the devices to switch to MSI mode. Only change devices whose drivers support MSI (GPU, NVMe, modern NICs and USB controllers usually do).";
                break;

            case "interrupt_affinity":
                TweakAction action = t.Actions[0];
                IEnumerable<InterruptDeviceCategory> categories = PciActionHandler.Categories(action);
                foreach (InterruptDeviceCategory c in categories)
                {
                    foreach (PciDeviceInfo d in await Task.Run(() => _pci.EnumeratePciDevices(c)))
                    {
                        Selectables.Add(new SelectableItem(d.Device.InstanceId, d.Device.Name, d.Category.ToString(), d.Category.ToString()));
                    }
                }

                CpuTopology topo = await Task.Run(HostProfileDetector.DetectTopology);
                IEnumerable<int> all = topo.LogicalProcessorCount > 0
                    ? topo.PerformanceLogicalProcessors.Concat(topo.EfficientLogicalProcessors)
                    : Enumerable.Range(0, Environment.ProcessorCount);
                foreach (int lp in all.OrderBy(i => i))
                {
                    Cores.Add(new CoreItem(lp, topo.LogicalProcessorCount == 0 || topo.PerformanceLogicalProcessors.Contains(lp)));
                }

                InteractiveHint = topo.IsHybrid
                    ? $"P-cores: logical {CpuTopology.FormatRanges(topo.PerformanceLogicalProcessors)} · E-cores: logical {CpuTopology.FormatRanges(topo.EfficientLogicalProcessors)}. Prefer P-cores for latency-sensitive devices; avoid core 0, which handles many system interrupts."
                    : "Select the logical processors that should service the selected devices' interrupts.";
                break;

            case "network_adapters":
                string keyword = t.Actions[0].Keyword!;
                foreach (NetworkAdapterInfo a in await Task.Run(_network.EnumerateAdapters))
                {
                    bool supported = keyword == NetworkAdapterRunner.RssKeyword ? a.SupportsRss : a.SupportsInterruptModeration;
                    string? value = keyword == NetworkAdapterRunner.RssKeyword ? a.Rss : a.InterruptModeration;
                    if (supported)
                    {
                        Selectables.Add(new SelectableItem(a.Device.InstanceId, a.Device.Name,
                            $"{keyword}: {value switch { "0" => "Disabled", "1" => "Enabled", null => "driver default", _ => value }}"));
                    }
                }

                StateChoices.Add(new ChoiceItem("1", "Enabled"));
                StateChoices.Add(new ChoiceItem("0", "Disabled"));
                SelectedState = StateChoices.FirstOrDefault(c => c.Id == t.Actions[0].ApplyState) ?? StateChoices[0];
                InteractiveHint = Selectables.Count == 0
                    ? "No physical adapter exposes this property."
                    : "Choose adapters and the value to set. The adapter restarts briefly to apply it.";
                break;

            case "devices":
                foreach (DeviceCandidate c in await Task.Run(() => _devices.FindCandidates(t.Actions[0].Devices ?? new())))
                {
                    Selectables.Add(new SelectableItem(c.Device.InstanceId, c.Device.Name,
                        (c.Device.IsDisabled ? "Disabled" : "Enabled") + (c.Note is null ? string.Empty : $" · {c.Note}"), c.Rating,
                        selected: c.Rating == "Safe" && !c.Device.IsDisabled));
                }

                InteractiveHint = Selectables.Count == 0
                    ? "None of the rated devices are present on this PC."
                    : "Safe devices are pre-selected. Review Moderate and Risky ones carefully before ticking them.";
                break;

            case "power_plan":
                foreach (PowerSettingInfo s in await Task.Run(() => _power.EnumerateSubgroup(PowerRunner.Guids.SubProcessor)))
                {
                    ExpertSettings.Add(s);
                }

                HardwareChecks = (await Task.Run(PowerRunner.GetHardwareChecks)).Summary;
                if (Sliders.Count > 0)
                {
                    foreach (SliderItem slider in Sliders)
                    {
                        TweakAction a = t.Actions.First(x => x.Key == slider.Key);
                        try
                        {
                            Guid scheme = _power.GetActiveScheme();
                            slider.Value = _power.ReadAcValue(scheme, Guid.Parse(a.Subgroup!), Guid.Parse(a.Setting!));
                        }
                        catch (Win32Exception)
                        {
                        }
                    }
                }

                InteractiveHint = "Sliders start at your current AC values. The expert view lists every processor setting of the active plan, including hidden ones.";
                break;
        }
    }

    private TweakParameters BuildParameters()
    {
        var p = new TweakParameters { OptionId = SelectedOption?.Id };
        foreach (SliderItem s in Sliders)
        {
            p.Values[s.Key] = s.Value;
        }

        p.Selections.AddRange(Selectables.Where(s => s.IsSelected).Select(s => s.Id));
        ulong mask = Cores.Where(c => c.IsSelected).Aggregate(0UL, (m, c) => m | (1UL << c.Index));
        if (mask != 0)
        {
            p.AffinityMask = mask;
        }

        if (SelectedPriority is { } pr && int.TryParse(pr.Id, out int priority) && priority >= 0)
        {
            p.DevicePriority = priority;
        }

        if (ShowStateChoices)
        {
            p.StateValue = SelectedState?.Id;
        }

        return p;
    }

    private static string SliderLabel(string key) => key switch
    {
        "sleep" => "Sleep after",
        "disk" => "Turn off disk after",
        "display" => "Turn off display after",
        "min_cpu" => "Minimum processor state",
        "max_cpu" => "Maximum processor state",
        _ => key,
    };

    // ------------------------------------------------------------------ inspector utilities

    [RelayCommand]
    private void CopyPath(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            ShowStatus(InfoBarSeverity.Informational, "Copied", text);
        }
    }

    [RelayCommand]
    private void OpenInRegedit(TweakItemViewModel? item)
    {
        TweakAction? action = item?.Definition.Actions.FirstOrDefault(a => a.Type == TweakActionTypes.Registry);
        RegistryTarget? target = action is null ? null : _registryHandler.ResolveTarget(action);
        if (target is null)
        {
            return;
        }

        string hive = target.Hive switch
        {
            RegistryHiveName.HKLM => "HKEY_LOCAL_MACHINE",
            RegistryHiveName.HKCU => "HKEY_CURRENT_USER",
            RegistryHiveName.HKCR => "HKEY_CLASSES_ROOT",
            RegistryHiveName.HKU => "HKEY_USERS",
            _ => "HKEY_CURRENT_CONFIG",
        };

        // Regedit reopens the key stored in its own LastKey preference.
        using (RegistryKey applet = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
        {
            applet.SetValue("LastKey", $@"Computer\{hive}\{target.KeyPath}");
        }

        Process.Start(new ProcessStartInfo("regedit.exe", "-m") { UseShellExecute = true });
    }

    [RelayCommand]
    private static void OpenServices() => Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true });
}
