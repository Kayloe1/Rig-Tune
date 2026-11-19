using System.Windows.Controls;
using RigTune.App.ViewModels;

namespace RigTune.App.Views;

public partial class HistoryView : Page
{
    public HistoryView(HistoryViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    public HistoryViewModel ViewModel { get; }
}
