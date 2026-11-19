using System.Windows.Controls;
using RigTune.App.ViewModels;

namespace RigTune.App.Views;

public partial class AppOptimizerView : Page
{
    public AppOptimizerView(AppOptimizerViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.OnNavigatedToAsync();
    }

    public AppOptimizerViewModel ViewModel { get; }
}
