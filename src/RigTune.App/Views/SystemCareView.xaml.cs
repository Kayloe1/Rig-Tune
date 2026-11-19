using System.Windows.Controls;
using RigTune.App.ViewModels;

namespace RigTune.App.Views;

public partial class SystemCareView : Page
{
    public SystemCareView(SystemCareViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadTabAsync(ViewModel.SelectedTab);
    }

    public SystemCareViewModel ViewModel { get; }

    private void OnConsoleTextChanged(object sender, TextChangedEventArgs e) => WingetConsole.ScrollToEnd();
}
