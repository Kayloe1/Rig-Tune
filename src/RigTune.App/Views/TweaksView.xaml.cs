using System.Windows.Controls;
using RigTune.App.ViewModels;

namespace RigTune.App.Views;

public partial class TweaksView : Page
{
    public TweaksView(TweaksViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.OnNavigatedToAsync();
    }

    public TweaksViewModel ViewModel { get; }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TweakList.SelectedItem is TweakItemViewModel item && ViewModel.SelectCommand.CanExecute(item))
        {
            ViewModel.SelectCommand.Execute(item);
        }
    }
}
