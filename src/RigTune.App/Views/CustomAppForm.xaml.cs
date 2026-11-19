using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RigTune.App.ViewModels;

namespace RigTune.App.Views;

public partial class CustomAppForm : UserControl
{
    public CustomAppForm()
    {
        InitializeComponent();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog() == true && DataContext is CustomAppFormModel model)
        {
            model.ExecutablePath = dialog.FileName;
            if (string.IsNullOrWhiteSpace(model.Name))
            {
                model.Name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }
    }
}
