using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RigTune.Core.Models;

namespace RigTune.App.ViewModels;

/// <summary>One row in the Tweaks list.</summary>
public sealed partial class TweakItemViewModel : ObservableObject
{
    public TweakItemViewModel(TweakDefinition definition)
    {
        Definition = definition;
        GroupKey = $"{definition.Category} › {definition.Subcategory}";
    }

    public TweakDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string GroupKey { get; }
    public bool IsDangerZone => Definition.IsDangerZone;
    public bool RequiresRestart => Definition.RequiresRestart;
    public bool IsRecommended => Definition.InRecommendedPreset;
    public bool IsPreset => Definition.IsPreset;
    public bool IsReadOnly => Definition.IsReadOnly;
    public string? Warning => Definition.Warning;
    public string TargetSummary => Definition.TargetSummary;
    public bool HasRegistryTarget => Definition.Actions.Any(a => a.Type == TweakActionTypes.Registry);
    public bool HasServiceTarget => Definition.Actions.Any(a => a.Type == TweakActionTypes.Service);
    public bool CanRevert => !Definition.IsPreset && !Definition.IsReadOnly;

    public string UndoTag => Definition.UndoType switch
    {
        TweakUndoTypes.RunOnce => "1x",
        TweakUndoTypes.Permanent => "Perm",
        TweakUndoTypes.ReadOnly => "Read-only",
        _ => string.Empty,
    };

    public string UndoNote => Definition.UndoType switch
    {
        TweakUndoTypes.Toggle => "Fully reversible: the exact original values are snapshotted before any change and restored by Revert.",
        TweakUndoTypes.RunOnce => "Runs once. Any settings it changes are snapshotted and can be reverted; one-off operations (uninstalls, re-scans) cannot.",
        TweakUndoTypes.Permanent => "Permanent: no automatic undo (files are deleted or the change has no stored original). Never part of a Recommended preset.",
        _ => "Read-only inspection: nothing on the system is changed.",
    };

    public string ApplyLabel => Definition switch
    {
        { IsPreset: true } => "Apply preset",
        { IsReadOnly: true } => "Inspect",
        { UndoType: TweakUndoTypes.Permanent or TweakUndoTypes.RunOnce } => "Run",
        _ => "Apply",
    };

    [ObservableProperty]
    private TweakStatus _status = TweakStatus.Unknown;

    [ObservableProperty]
    private string _statusText = "Checking…";

    [ObservableProperty]
    private bool _hasActiveSnapshots;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLocked;

    public ObservableCollection<ActionState> ActionStates { get; } = new();

    public bool IsActive => Status is TweakStatus.Active or TweakStatus.Partial || HasActiveSnapshots;

    public void Update(TweakStatusReport report)
    {
        Status = report.Status;
        HasActiveSnapshots = report.HasActiveSnapshots;
        StatusText = report.Summary + (report.HasActiveSnapshots && report.Status != TweakStatus.Active ? " · RigTune changes active" : string.Empty);
        ActionStates.Clear();
        foreach (ActionState state in report.Actions)
        {
            ActionStates.Add(state);
        }

        OnPropertyChanged(nameof(IsActive));
    }
}
