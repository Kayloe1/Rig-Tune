using System.Windows.Controls;
using RigTune.App.ViewModels;

namespace RigTune.App.Views;

public partial class DiskAnalyzerView : Page
{
    public DiskAnalyzerView(DiskAnalyzerViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.OnNavigatedToAsync();
    }

    public DiskAnalyzerViewModel ViewModel { get; }
}
