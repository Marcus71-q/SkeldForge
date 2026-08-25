using System.Windows.Input;

namespace SkeldForge.ViewModels.Infrastructure;

public sealed class NavigationItemViewModel : ObservableObject
{
    private bool _isSelected;

    public NavigationItemViewModel()
    {
        SkeldForge.Services.LocalizationService.Current.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Label));
    }

    public required string Key { get; init; }
    public required string LabelKey { get; init; }
    public required string Glyph { get; init; }
    public required ObservableObject Page { get; init; }
    public required ICommand SelectCommand { get; init; }
    public string Label => SkeldForge.Services.LocalizationService.Current[LabelKey];

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
