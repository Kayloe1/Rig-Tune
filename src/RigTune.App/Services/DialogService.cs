using Wpf.Ui.Controls;

namespace RigTune.App.Services;

public enum RestorePointChoice
{
    CreateAndContinue,
    ContinueWithout,
    Cancel,
}

/// <summary>Fluent confirmation dialogs (SPEC §3.2 restore-point prompt).</summary>
public sealed class DialogService
{
    public async Task<RestorePointChoice> AskRestorePointAsync(string action)
    {
        var box = new MessageBox
        {
            Title = "Create a restore point first?",
            Content = $"{action}\n\nA System Restore point lets you roll Windows back even if something unexpected happens. " +
                      "RigTune also snapshots every value it changes.",
            PrimaryButtonText = "Create Restore Point & Continue",
            SecondaryButtonText = "Continue Without Restore Point",
            CloseButtonText = "Cancel",
            PrimaryButtonAppearance = ControlAppearance.Primary,
        };

        return await box.ShowDialogAsync() switch
        {
            MessageBoxResult.Primary => RestorePointChoice.CreateAndContinue,
            MessageBoxResult.Secondary => RestorePointChoice.ContinueWithout,
            _ => RestorePointChoice.Cancel,
        };
    }

    /// <summary>Shows arbitrary form content in a Fluent dialog; true when the primary button was pressed.</summary>
    public async Task<bool> ShowFormAsync(string title, object content, string primaryText)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            PrimaryButtonAppearance = ControlAppearance.Primary,
            MinWidth = 560,
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }
}
