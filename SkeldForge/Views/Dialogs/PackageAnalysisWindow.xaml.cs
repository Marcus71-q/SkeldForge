using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Views.Dialogs;

public partial class PackageAnalysisWindow : Window
{
    public PackageAnalysisWindow(ModPackageAnalysis analysis)
    {
        InitializeComponent();
        DataContext = new PackageAnalysisDialogData(analysis);
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PackageAnalysisDialogData data)
        {
            data.Analysis.DetectedName = string.IsNullOrWhiteSpace(data.Analysis.DetectedName)
                ? "Unknown Package"
                : data.Analysis.DetectedName.Trim();
            data.Analysis.DetectedVersion = string.IsNullOrWhiteSpace(data.Analysis.DetectedVersion)
                ? "Unknown"
                : data.Analysis.DetectedVersion.Trim();
        }

        DialogResult = true;
    }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}

public sealed class PackageAnalysisDialogData
{
    public PackageAnalysisDialogData(ModPackageAnalysis analysis)
    {
        Analysis = analysis;
        SafetyText = LocalizationService.Current[analysis.IsSafe ? "Package.Safe" : "Package.Unsafe"];
        SafetyDetail = analysis.IsSafe
            ? string.Empty
            : LocalizationService.Current[
                analysis.RejectionReason?.Contains("unsafe", StringComparison.OrdinalIgnoreCase) == true ||
                analysis.RejectionReason?.Contains("traversal", StringComparison.OrdinalIgnoreCase) == true
                    ? "Error.ZipUnsafe"
                    : analysis.RejectionReason?.Contains("invalid", StringComparison.OrdinalIgnoreCase) == true ||
                      analysis.RejectionReason?.Contains("corrupt", StringComparison.OrdinalIgnoreCase) == true
                        ? "Error.ZipCorrupt"
                        : "Error.Generic"];
        SafetyIcon = analysis.IsSafe ? "\uE73E" : "\uEA39";
        SafetyForeground = (Brush)Application.Current.FindResource(analysis.IsSafe ? "SuccessBrush" : "DangerBrush");
        SafetyBackground = (Brush)Application.Current.FindResource(analysis.IsSafe ? "SuccessDarkBrush" : "DangerDarkBrush");
    }
    public ModPackageAnalysis Analysis { get; }
    public string SafetyText { get; }
    public string SafetyDetail { get; }
    public string SafetyIcon { get; }
    public Brush SafetyForeground { get; }
    public Brush SafetyBackground { get; }
    public bool IsUnknownStructure => !Analysis.HasBepInExStructure && !Analysis.HasPluginDlls && !Analysis.HasLoaderComponents;
}
