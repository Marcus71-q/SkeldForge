using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using SkeldForge.Models;
using SkeldForge.Services;
using SkeldForge.UI.Commands;
using SkeldForge.ViewModels.Infrastructure;
using SkeldForge.ViewModels.Pages;

namespace SkeldForge.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppCompositionRoot _root;
    private ObservableObject? _currentPage;
    private NavigationItemViewModel? _selectedNavigation;
    private bool _isSetupRequired;
    private bool _isInitializing = true;
    private bool _isBusy;
    private bool _isProgressIndeterminate = true;
    private double _progressValue;
    private string _progressText = string.Empty;
    private string _progressDetail = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _statusIsError;
    private CancellationTokenSource? _statusTimer;

    public MainViewModel(AppCompositionRoot root)
    {
        _root = root;
        Modpacks.CollectionChanged += ForwardCollectionChange;
        LibraryMods.CollectionChanged += ForwardCollectionChange;
        SelectNavigationCommand = new RelayCommand<NavigationItemViewModel>(SelectNavigation);
        DismissStatusCommand = new RelayCommand(ClearStatus);
    }

    public AppCompositionRoot Root => _root;
    public LocalizationService Text => LocalizationService.Current;
    public ObservableCollection<ModpackInfo> Modpacks { get; } = [];
    public ObservableCollection<ModInfo> LibraryMods { get; } = [];
    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; } = [];
    public AppSettings Settings { get; private set; } = new();
    public GameInstallation? GameInstallation { get; private set; }
    public FirstRunViewModel? FirstRun { get; private set; }
    public RelayCommand<NavigationItemViewModel> SelectNavigationCommand { get; }
    public RelayCommand DismissStatusCommand { get; }

    public ObservableObject? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public bool IsSetupRequired
    {
        get => _isSetupRequired;
        private set => SetProperty(ref _isSetupRequired, value);
    }

    public bool IsInitializing
    {
        get => _isInitializing;
        private set => SetProperty(ref _isInitializing, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string ProgressDetail
    {
        get => _progressDetail;
        private set => SetProperty(ref _progressDetail, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public async Task InitializeAsync()
    {
        try
        {
            Settings = await _root.InitializeAsync();
            OnPropertyChanged(nameof(Settings));
            Text.SetCulture(Settings.Language);
            await LoadGameInstallationAsync();
            await RefreshCollectionsAsync();
            CreatePages();

            var isValid = GameInstallation is not null;
            IsSetupRequired = !Settings.FirstLaunchComplete || !isValid;
            FirstRun = new FirstRunViewModel(this, GameInstallation);
            OnPropertyChanged(nameof(FirstRun));
            SelectNavigation(NavigationItems.First());
        }
        catch (Exception ex)
        {
            await _root.Logger.ErrorAsync("Application initialization failed.", ex);
            await _root.Dialogs.ShowErrorAsync(Text["Dialog.ErrorTitle"], Text["Error.Generic"], ex.Message);
            IsSetupRequired = true;
            FirstRun = new FirstRunViewModel(this, null);
            OnPropertyChanged(nameof(FirstRun));
        }
        finally
        {
            IsInitializing = false;
        }
    }

    public async Task CompleteFirstRunAsync(GameInstallation installation)
    {
        GameInstallation = installation;
        Settings.AmongUsPath = installation.Path;
        Settings.FirstLaunchComplete = true;
        await _root.SaveSettingsAsync(Settings);
        IsSetupRequired = false;
        RefreshAllPages();
    }

    public async Task RefreshCollectionsAsync()
    {
        var modpacks = await _root.GetModpacksAsync();
        var mods = await _root.GetLibraryModsAsync();
        Replace(Modpacks, modpacks);
        Replace(LibraryMods, mods);
        RefreshAllPages();
    }

    public async Task RefreshGameInstallationAsync()
    {
        await LoadGameInstallationAsync();
        RefreshAllPages();
    }

    public void Navigate(string key)
    {
        var item = NavigationItems.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
            SelectNavigation(item);
    }

    public void ShowModpack(ModpackInfo model)
    {
        var item = NavigationItems.First(x => x.Key == "modpacks");
        SelectNavigation(item);
        CurrentPage = new ModpackDetailsViewModel(this, model);
    }

    public void ShowStatus(string message, bool isError = false)
    {
        _statusTimer?.Cancel();
        StatusIsError = isError;
        StatusMessage = message;
        _statusTimer = new CancellationTokenSource();
        _ = ClearStatusLaterAsync(_statusTimer.Token);
    }

    public async Task<T> RunBusyAsync<T>(string progressKey, Func<IProgress<OperationProgress>, Task<T>> action)
    {
        IsBusy = true;
        ProgressText = Text[progressKey];
        ProgressDetail = string.Empty;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        var progress = new Progress<OperationProgress>(p =>
        {
            ProgressText = Text[_root.ResolveProgressLocalizationKey(p.StageKey)];
            ProgressDetail = p.CurrentItem ?? string.Empty;
            IsProgressIndeterminate = p.Total <= 0;
            ProgressValue = p.Fraction * 100;
        });

        try
        {
            return await action(progress);
        }
        finally
        {
            IsBusy = false;
            ProgressDetail = string.Empty;
        }
    }

    public async Task ShowFailureAsync(OperationResult result)
    {
        var key = _root.ResolveErrorLocalizationKey(result.ErrorKey);
        await _root.Dialogs.ShowErrorAsync(Text["Dialog.ErrorTitle"], Text[key], result.TechnicalMessage);
    }

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        OnPropertyChanged(nameof(Settings));
        RefreshAllPages();
    }

    public void RequireSetup()
    {
        GameInstallation = null;
        OnPropertyChanged(nameof(GameInstallation));
        FirstRun = new FirstRunViewModel(this, null);
        OnPropertyChanged(nameof(FirstRun));
        IsSetupRequired = true;
        RefreshAllPages();
    }

    private async Task LoadGameInstallationAsync()
    {
        GameInstallation = string.IsNullOrWhiteSpace(Settings.AmongUsPath)
            ? null
            : await _root.InspectGameAsync(Settings.AmongUsPath);
        OnPropertyChanged(nameof(GameInstallation));
    }

    private void CreatePages()
    {
        NavigationItems.Clear();
        AddNavigation("home", "Nav.Home", "\uE80F", new HomeViewModel(this));
        AddNavigation("modpacks", "Nav.Modpacks", "\uE8B7", new ModpacksViewModel(this));
        AddNavigation("library", "Nav.ModLibrary", "\uE8F1", new ModLibraryViewModel(this));
        AddNavigation("cleanup", "Nav.VanillaCleanup", "\uE74D", new VanillaCleanupViewModel(this));
        AddNavigation("backups", "Nav.Backups", "\uE81C", new BackupsViewModel(this));
        AddNavigation("settings", "Nav.Settings", "\uE713", new SettingsViewModel(this));
        AddNavigation("about", "Nav.About", "\uE946", new AboutViewModel());
    }

    private void AddNavigation(string key, string labelKey, string glyph, ObservableObject page)
    {
        var item = new NavigationItemViewModel
        {
            Key = key,
            LabelKey = labelKey,
            Glyph = glyph,
            Page = page,
            SelectCommand = SelectNavigationCommand
        };
        NavigationItems.Add(item);
    }

    private void SelectNavigation(NavigationItemViewModel? item)
    {
        if (item is null) return;
        if (_selectedNavigation is not null) _selectedNavigation.IsSelected = false;
        _selectedNavigation = item;
        item.IsSelected = true;
        CurrentPage = item.Page;
        if (item.Page is IRefreshableViewModel refreshable)
            refreshable.Refresh();
    }

    private void RefreshAllPages()
    {
        foreach (var item in NavigationItems)
            if (item.Page is IRefreshableViewModel refreshable)
                refreshable.Refresh();
    }

    private void ForwardCollectionChange(object? sender, NotifyCollectionChangedEventArgs e) => RefreshAllPages();

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        destination.Clear();
        foreach (var item in source)
            destination.Add(item);
    }

    private async Task ClearStatusLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            ClearStatus();
        }
        catch (OperationCanceledException) { }
    }

    private void ClearStatus() => StatusMessage = string.Empty;
}

public interface IRefreshableViewModel
{
    void Refresh();
}
