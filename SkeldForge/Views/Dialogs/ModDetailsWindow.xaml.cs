using System.Windows;
using System.Windows.Input;
using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.Views.Dialogs;

public partial class ModDetailsWindow : Window
{
    public ModDetailsWindow(ModInfo mod)
    {
        InitializeComponent();
        DataContext = new ModDetailsDialogData(mod);
    }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}

public sealed class ModDetailsDialogData
{
    private readonly LocalizationService _text = LocalizationService.Current;
    public ModDetailsDialogData(ModInfo mod)
    {
        Mod = mod;
        Dependencies = mod.Dependencies.Select(x => new DependencyDialogData(
            x.Name,
            _text[$"Dependency.{x.Status}"],
            x.Evidence)).ToList();
    }
    public ModInfo Mod { get; }
    public string VersionText => Mod.Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        ? _text["Common.Unknown"]
        : Mod.Version;
    public string StatusText => _text[Mod.IsEnabled ? "Common.Enabled" : "Common.Disabled"];
    public string CompatibilityText => _text[$"Status.{Mod.Compatibility}"];
    public IReadOnlyList<DependencyDialogData> Dependencies { get; }
}

public sealed record DependencyDialogData(string Name, string Status, string Evidence);
