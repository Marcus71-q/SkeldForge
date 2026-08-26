using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using SkeldForge.Core;
using SkeldForge.Models;
using SkeldForge.UI.Commands;
using SkeldForge.ViewModels.Infrastructure;

namespace SkeldForge.ViewModels.Pages;

public sealed class ModLibraryViewModel : ObservableObject, IRefreshableViewModel
{
    private readonly MainViewModel _main;
    private string _searchText = string.Empty;
    private ChoiceItem<ModSort>? _selectedSort;

    public ModLibraryViewModel(MainViewModel main)
    {
        _main = main;
        ModsView = CollectionViewSource.GetDefaultView(Mods);
        ModsView.Filter = Filter;
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        DetailsCommand = new AsyncRelayCommand<ModCardViewModel>(x => x is null ? Task.CompletedTask : _main.Root.Dialogs.ShowModDetailsAsync(x.Model));
        OpenFolderCommand = new RelayCommand(() => _main.Root.OpenFolder(_main.Root.Paths.ModLibraryDirectory));
        RebuildSortOptions();
        _main.Text.LanguageChanged += (_, _) => RebuildSortOptions();
        Refresh();
    }

    public ObservableCollection<ModCardViewModel> Mods { get; } = [];
    public ICollectionView ModsView { get; }
    public ObservableCollection<ChoiceItem<ModSort>> SortOptions { get; } = [];
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand<ModCardViewModel> DetailsCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) ModsView.Refresh(); } }
    public ChoiceItem<ModSort>? SelectedSort { get => _selectedSort; set { if (SetProperty(ref _selectedSort, value)) ApplySort(); } }

    public void Refresh()
    {
        Mods.Clear();
        foreach (var mod in _main.LibraryMods) Mods.Add(new ModCardViewModel(mod));
        ApplySort();
    }

    private async Task ImportAsync()
    {
        var selectedPath = _main.Root.Dialogs.PickModPackage();
        if (selectedPath is null) return;
        ModPackageInput input;
        try
        {
            input = ModPackageInput.Prepare(selectedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await _main.Root.Dialogs.ShowErrorAsync(
                _main.Text["Dialog.ErrorTitle"], _main.Text["Error.ZipCorrupt"], ex.Message);
            return;
        }

        using (input)
        {
        var path = input.ArchivePath;
        var analysis = await _main.RunBusyAsync("Progress.Analyzing", _ => _main.Root.AnalyzePackageAsync(path));
        if (!analysis.IsSafe)
        {
            await _main.Root.Dialogs.ReviewPackageAsync(analysis);
            return;
        }
        if (!await _main.Root.Dialogs.ReviewPackageAsync(analysis)) return;
        var result = await _main.RunBusyAsync("Progress.Importing", _ => _main.Root.ImportLibraryPackageAsync(path, analysis));
        if (!result.Success || result.Value is null) { await _main.ShowFailureAsync(result); return; }
        await _main.RefreshCollectionsAsync();
        _main.ShowStatus(result.Value.IsDuplicate ? _main.Text["Package.Duplicate"] : _main.Text.Format("Success.ModImported", result.Value.Mod.Name));
        }
    }

    private bool Filter(object value) => value is ModCardViewModel mod &&
        (string.IsNullOrWhiteSpace(SearchText) || mod.Name.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase));

    private void RebuildSortOptions()
    {
        var selected = SelectedSort?.Value ?? ModSort.Name;
        SortOptions.Clear();
        SortOptions.Add(new(ModSort.Name, _main.Text["ModLibrary.Sort.Name"]));
        SortOptions.Add(new(ModSort.ImportDate, _main.Text["ModLibrary.Sort.Imported"]));
        SortOptions.Add(new(ModSort.Version, _main.Text["ModLibrary.Sort.Version"]));
        SortOptions.Add(new(ModSort.Status, _main.Text["ModLibrary.Sort.Status"]));
        SelectedSort = SortOptions.First(x => x.Value == selected);
    }

    private void ApplySort()
    {
        using var defer = ModsView.DeferRefresh();
        ModsView.SortDescriptions.Clear();
        var sort = SelectedSort?.Value ?? ModSort.Name;
        ModsView.SortDescriptions.Add(sort switch
        {
            ModSort.ImportDate => new SortDescription("Model.ImportDate", ListSortDirection.Descending),
            ModSort.Version => new SortDescription(nameof(ModCardViewModel.Version), ListSortDirection.Ascending),
            ModSort.Status => new SortDescription(nameof(ModCardViewModel.IsEnabled), ListSortDirection.Descending),
            _ => new SortDescription(nameof(ModCardViewModel.Name), ListSortDirection.Ascending)
        });
    }
}
