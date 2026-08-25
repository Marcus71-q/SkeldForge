using System.Windows;
using System.Windows.Input;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Views.Dialogs;

public partial class CleanupReportWindow : Window
{
    public CleanupReportWindow(CleanupReport report)
    {
        InitializeComponent();
        var text = LocalizationService.Current;
        DataContext = new
        {
            Report = report,
            Sections = new[]
            {
                new ReportSection(text["Cleanup.ReportRemoved"], report.Removed),
                new ReportSection(text["Cleanup.ReportBackedUp"], report.BackedUp),
                new ReportSection(text["Cleanup.ReportSkipped"], report.Skipped),
                new ReportSection(text["Cleanup.ReportUnknown"], report.Unknown),
                new ReportSection(text["Cleanup.ReportErrors"], report.Errors)
            }
        };
    }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}

public sealed record ReportSection(string Title, IReadOnlyList<string> Items);
