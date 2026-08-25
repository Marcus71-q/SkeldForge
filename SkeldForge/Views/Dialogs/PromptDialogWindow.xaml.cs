using System.Windows;

namespace SkeldForge.Views.Dialogs;

public partial class PromptDialogWindow : Window
{
    private readonly PromptDialogData _data;

    public PromptDialogWindow(string title, string label, string initialValue)
    {
        InitializeComponent();
        _data = new PromptDialogData(title, label, initialValue);
        DataContext = _data;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
    }

    public string Value => _data.Value;

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_data.Value))
        {
            ValidationText.Visibility = Visibility.Visible;
            ValueBox.Focus();
            return;
        }
        DialogResult = true;
    }
}

public sealed class PromptDialogData
{
    public PromptDialogData(string title, string label, string value)
    {
        Title = title;
        Label = label;
        Value = value;
    }
    public string Title { get; }
    public string Label { get; }
    public string Value { get; set; }
}
