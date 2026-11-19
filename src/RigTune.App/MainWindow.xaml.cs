using System.Windows;
using RigTune.App.Tray;
using RigTune.App.Views;
using Wpf.Ui.Controls;

namespace RigTune.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(IServiceProvider services, SystemTrayManager tray)
    {
        InitializeComponent();
        RootNavigation.SetServiceProvider(services);
        Loaded += (_, _) =>
        {
            RootNavigation.Navigate(typeof(DashboardView));
            tray.Attach(this, TrayIcon);
            if (App.StartMinimized)
            {
                Hide(); // --minimized (Start with Windows): live in the tray only
            }
        };
    }
}
