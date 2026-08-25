using System.Windows;
using System.Windows.Input;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Views.Dialogs;

public partial class RestoreReportWindow : Window
{
    public RestoreReportWindow(RestoreReport report)
    {
        InitializeComponent();
        var text = LocalizationService.Current;
        DataContext = new RestoreReportDialogData(
            report.RestoredCount,
            report.ConflictCount,
            report.ErrorCount,
            report.Entries.Select(entry => new RestoreEntryDialogData(
                entry.RelativePath,
                text[$"Restore.Status.{entry.Status}"],
                text[entry.Status switch
                {
                    RestoreEntryStatus.Restored => "Restore.Message.Restored",
                    RestoreEntryStatus.Conflict => "Restore.Message.Conflict",
                    RestoreEntryStatus.Skipped => "Restore.Message.Skipped",
                    _ => "Restore.Message.Error"
                }])).ToList());
    }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}

public sealed record RestoreReportDialogData(
    int RestoredCount,
    int ConflictCount,
    int ErrorCount,
    IReadOnlyList<RestoreEntryDialogData> Entries);

public sealed record RestoreEntryDialogData(string RelativePath, string Status, string Message);
