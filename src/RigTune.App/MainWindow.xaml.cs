using System.Windows;
using RigTune.App.Views;
using Wpf.Ui.Controls;

namespace RigTune.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        RootNavigation.SetServiceProvider(services);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(DashboardView));
    }

    private void OnTrayOpen(object sender, RoutedEventArgs e)
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void OnTrayExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
