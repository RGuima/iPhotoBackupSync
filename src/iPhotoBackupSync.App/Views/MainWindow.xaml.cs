using System.Windows;
using iPhotoBackupSync.App.ViewModels;

namespace iPhotoBackupSync.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) => (DataContext as MainViewModel)?.SaveSettings();
    }
}
