using System.Collections.ObjectModel;
using SkeldForge.ViewModels.Infrastructure;
using SkeldForge.UI.Commands;

namespace SkeldForge.ViewModels.Pages;

public sealed class HomeViewModel : ObservableObject, IRefreshableViewModel
{
    private readonly MainViewModel _main;
    private ModpackCardViewModel? _recent;

    public HomeViewModel(MainViewModel main)
    {
        _main = main;
        CreateModpackCommand = new AsyncRelayCommand(() => ModpackWorkflows.CreateAsync(_main));
        LaunchVanillaCommand = new AsyncRelayCommand(LaunchVanillaAsync, () => HasGame);
        OpenModpackCommand = new RelayCommand<ModpackCardViewModel>(x => { if (x is not null) _main.ShowModpack(x.Model); });
        PlayModpackCommand = new AsyncRelayCommand<ModpackCardViewModel>(PlayAsync, x => x is not null);
        _main.Text.LanguageChanged += (_, _) => OnPropertyChanged(nameof(GamePath));
        Refresh();
    }

    public ObservableCollection<ModpackCardViewModel> Cards { get; } = [];
    public AsyncRelayCommand CreateModpackCommand { get; }
    public AsyncRelayCommand LaunchVanillaCommand { get; }
    public RelayCommand<ModpackCardViewModel> OpenModpackCommand { get; }
    public AsyncRelayCommand<ModpackCardViewModel> PlayModpackCommand { get; }
    public bool HasGame => _main.GameInstallation is not null;
    public string GamePath => _main.GameInstallation?.Path ?? _main.Text["Common.NotAvailable"];
    public int ModpackCount => _main.Modpacks.Count;
    public int LibraryCount => _main.LibraryMods.Count;
    public ModpackCardViewModel? Recent
    {
        get => _recent;
        private set => SetProperty(ref _recent, value);
    }

    public void Refresh()
    {
        Cards.Clear();
        foreach (var model in _main.Modpacks.OrderByDescending(x => x.LastLaunchedAt ?? x.CreatedAt).Take(6))
            Cards.Add(new ModpackCardViewModel(model));
        var recentModel = _main.Modpacks.FirstOrDefault(x => x.Id == _main.Settings.LastLaunchedModpackId)
            ?? _main.Modpacks.Where(x => x.LastLaunchedAt is not null).OrderByDescending(x => x.LastLaunchedAt).FirstOrDefault();
        Recent = recentModel is null ? null : new ModpackCardViewModel(recentModel);
        OnPropertiesChanged(nameof(HasGame), nameof(GamePath), nameof(ModpackCount), nameof(LibraryCount));
        LaunchVanillaCommand.NotifyCanExecuteChanged();
    }

    private async Task LaunchVanillaAsync()
    {
        if (_main.GameInstallation is null) return;
        var result = await _main.Root.LaunchVanillaAsync(_main.GameInstallation.Path);
        if (!result.Success) await _main.ShowFailureAsync(result);
    }

    private async Task PlayAsync(ModpackCardViewModel? card)
    {
        if (card is null) return;
        var result = await _main.Root.LaunchModpackAsync(card.Model);
        if (!result.Success) { await _main.ShowFailureAsync(result); return; }
        _main.Settings.LastLaunchedModpackId = card.Id;
        await _main.Root.SaveSettingsAsync(_main.Settings);
        await _main.RefreshCollectionsAsync();
    }
}
