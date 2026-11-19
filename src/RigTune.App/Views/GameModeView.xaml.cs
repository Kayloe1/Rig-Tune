using System.Windows.Controls;
using RigTune.App.ViewModels;

namespace RigTune.App.Views;

public partial class GameModeView : Page
{
    public GameModeView(GameModeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.OnNavigatedToAsync();
    }

    public GameModeViewModel ViewModel { get; }
}
