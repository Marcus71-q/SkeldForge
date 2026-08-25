using System.Windows;
using System.Windows.Controls;
using SkeldForge.ViewModels.Pages;

namespace SkeldForge.Views;

public partial class ModpackDetailsView : UserControl
{
    public ModpackDetailsView() => InitializeComponent();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is ModpackDetailsViewModel vm && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await vm.HandleDroppedFilesAsync(files);
    }
}
