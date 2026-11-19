using System.Windows.Controls;
using RigTune.App.ViewModels;

namespace RigTune.App.Views;

public partial class DashboardView : Page
{
    public DashboardView(DashboardViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.OnNavigatedToAsync();
        Unloaded += (_, _) => ViewModel.OnNavigatedFrom();
    }

    public DashboardViewModel ViewModel { get; }
}
