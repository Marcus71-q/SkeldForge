using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using SkeldForge.Models;
using SkeldForge.UI.Commands;
using SkeldForge.ViewModels.Infrastructure;

namespace SkeldForge.ViewModels.Pages;

public sealed class ModpacksViewModel : ObservableObject, IRefreshableViewModel
{
    private readonly MainViewModel _main;
    private string _searchText = string.Empty;
    private ChoiceItem<ModpackSort>? _selectedSort;

    public ModpacksViewModel(MainViewModel main)
    {
        _main = main;
        CardsView = CollectionViewSource.GetDefaultView(Cards);
        CardsView.Filter = Filter;
        CreateCommand = new AsyncRelayCommand(() => ModpackWorkflows.CreateAsync(_main));
        ImportCommand = new AsyncRelayCommand(() => ModpackWorkflows.ImportAsync(_main));
        OpenCommand = new RelayCommand<ModpackCardViewModel>(x => { if (x is not null) _main.ShowModpack(x.Model); });
        RebuildSortOptions();
        _main.Text.LanguageChanged += (_, _) => RebuildSortOptions();
        Refresh();
    }

    public ObservableCollection<ModpackCardViewModel> Cards { get; } = [];
    public ICollectionView CardsView { get; }
    public ObservableCollection<ChoiceItem<ModpackSort>> SortOptions { get; } = [];
    public AsyncRelayCommand CreateCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public RelayCommand<ModpackCardViewModel> OpenCommand { get; }
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) CardsView.Refresh(); }
    }
    public ChoiceItem<ModpackSort>? SelectedSort
    {
        get => _selectedSort;
        set { if (SetProperty(ref _selectedSort, value)) ApplySort(); }
    }

    public void Refresh()
    {
        Cards.Clear();
        foreach (var model in _main.Modpacks)
            Cards.Add(new ModpackCardViewModel(model));
        ApplySort();
    }

    private bool Filter(object value) => value is ModpackCardViewModel card &&
        (string.IsNullOrWhiteSpace(SearchText) || card.Name.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase));

    private void RebuildSortOptions()
    {
        var selected = SelectedSort?.Value ?? ModpackSort.Name;
        SortOptions.Clear();
        SortOptions.Add(new(ModpackSort.Name, _main.Text["Modpacks.Sort.Name"]));
        SortOptions.Add(new(ModpackSort.RecentlyUsed, _main.Text["Modpacks.Sort.Recent"]));
        SortOptions.Add(new(ModpackSort.DateCreated, _main.Text["Modpacks.Sort.Created"]));
        SortOptions.Add(new(ModpackSort.Version, _main.Text["Modpacks.Sort.Version"]));
        SelectedSort = SortOptions.First(x => x.Value == selected);
    }

    private void ApplySort()
    {
        using var defer = CardsView.DeferRefresh();
        CardsView.SortDescriptions.Clear();
        var sort = SelectedSort?.Value ?? ModpackSort.Name;
        CardsView.SortDescriptions.Add(sort switch
        {
            ModpackSort.RecentlyUsed => new SortDescription("Model.LastLaunchedAt", ListSortDirection.Descending),
            ModpackSort.DateCreated => new SortDescription("Model.CreatedAt", ListSortDirection.Descending),
            ModpackSort.Version => new SortDescription(nameof(ModpackCardViewModel.Version), ListSortDirection.Ascending),
            _ => new SortDescription(nameof(ModpackCardViewModel.Name), ListSortDirection.Ascending)
        });
    }
}
