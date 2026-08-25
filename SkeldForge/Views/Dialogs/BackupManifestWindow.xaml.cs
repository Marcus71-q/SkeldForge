using System.Windows;
using System.Windows.Input;
using SkeldForge.Models;

namespace SkeldForge.Views.Dialogs;

public partial class BackupManifestWindow : Window
{
    public BackupManifestWindow(BackupManifest manifest) { InitializeComponent(); DataContext = manifest; }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
