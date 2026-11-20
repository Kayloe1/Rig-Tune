using System.Windows.Controls;
using RigTune.App.ViewModels;

namespace RigTune.App.Views;

public partial class UninstallerView : Page
{
    public UninstallerView(UninstallerViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.OnNavigatedToAsync();
    }

    public UninstallerViewModel ViewModel { get; }
}
