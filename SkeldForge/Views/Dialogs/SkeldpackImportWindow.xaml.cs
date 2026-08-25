using System.Windows;
using System.Windows.Input;
using SkeldForge.Models;

namespace SkeldForge.Views.Dialogs;

public partial class SkeldpackImportWindow : Window
{
    public SkeldpackImportWindow(SkeldpackImportPreview preview)
    {
        InitializeComponent();
        DataContext = new { Preview = preview };
    }
    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
