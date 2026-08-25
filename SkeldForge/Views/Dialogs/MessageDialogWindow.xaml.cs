using System.Windows;
using System.Windows.Media;
using SkeldForge.Services;

namespace SkeldForge.Views.Dialogs;

public enum MessageDialogKind { Information, Warning, Error, Danger }

public partial class MessageDialogWindow : Window
{
    public MessageDialogWindow(string title, string message, string? details, MessageDialogKind kind, bool showCancel)
    {
        InitializeComponent();
        DataContext = new MessageDialogData(title, message, details ?? string.Empty, kind, showCancel);
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

public sealed class MessageDialogData
{
    public MessageDialogData(string title, string message, string details, MessageDialogKind kind, bool showCancel)
    {
        Title = title;
        Message = message;
        Details = details;
        ShowCancel = showCancel;
        IsDestructive = kind == MessageDialogKind.Danger;
        Icon = kind switch
        {
            MessageDialogKind.Information => "\uE946",
            MessageDialogKind.Warning => "\uE7BA",
            MessageDialogKind.Error or MessageDialogKind.Danger => "\uEA39",
            _ => "\uE946"
        };
        IconBackground = BrushFor(kind switch
        {
            MessageDialogKind.Information => "InfoDarkBrush",
            MessageDialogKind.Warning => "WarningDarkBrush",
            _ => "DangerDarkBrush"
        });
        IconForeground = BrushFor(kind switch
        {
            MessageDialogKind.Information => "InfoBrush",
            MessageDialogKind.Warning => "WarningBrush",
            _ => "DangerBrush"
        });
        AcceptText = showCancel ? LocalizationService.Current["Common.Confirm"] : LocalizationService.Current["Common.Close"];
    }

    public string Title { get; }
    public string Message { get; }
    public string Details { get; }
    public bool ShowCancel { get; }
    public bool IsDestructive { get; }
    public string Icon { get; }
    public Brush IconBackground { get; }
    public Brush IconForeground { get; }
    public string AcceptText { get; }

    private static Brush BrushFor(string key) => (Brush)Application.Current.FindResource(key);
}
