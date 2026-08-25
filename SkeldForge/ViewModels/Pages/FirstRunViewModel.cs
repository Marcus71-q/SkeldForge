using SkeldForge.Models;
using SkeldForge.Services;
using SkeldForge.UI.Commands;
using SkeldForge.ViewModels.Infrastructure;

namespace SkeldForge.ViewModels.Pages;

public sealed class FirstRunViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private GameInstallation? _installation;
    private string _selectedPath = string.Empty;
    private bool _isSearching;
    private bool _hasValidationResult;
    private bool _isValid;
    private string _statusKey = "Setup.NotDetected";
    private ChoiceItem<string>? _selectedLanguage;

    public FirstRunViewModel(MainViewModel main, GameInstallation? installation)
    {
        _main = main;
        _installation = installation;
        _selectedPath = installation?.Path ?? string.Empty;
        _isValid = installation is not null;
        _hasValidationResult = installation is not null;
        _statusKey = installation is not null ? "Setup.Detected" : "Setup.NotDetected";

        SelectFolderCommand = new AsyncRelayCommand(SelectFolderAsync);
        ContinueCommand = new AsyncRelayCommand(ContinueAsync, () => IsValid && !IsSearching);
        RetryDetectionCommand = new AsyncRelayCommand(DetectAsync, () => !IsSearching);
        Languages =
        [
            new ChoiceItem<string>("en-US", "English"),
            new ChoiceItem<string>("tr-TR", "Türkçe"),
            new ChoiceItem<string>("ru-RU", "Русский"),
            new ChoiceItem<string>("es-ES", "Español"),
            new ChoiceItem<string>("ja-JP", "日本語"),
            new ChoiceItem<string>("th-TH", "ไทย"),
            new ChoiceItem<string>("id-ID", "Bahasa Indonesia"),
            new ChoiceItem<string>("ko-KR", "한국어"),
            new ChoiceItem<string>("zh-CN", "简体中文"),
            new ChoiceItem<string>("de-DE", "Deutsch"),
            new ChoiceItem<string>("fr-FR", "Français"),
            new ChoiceItem<string>("pt-BR", "Português (Brasil)"),
            new ChoiceItem<string>("it-IT", "Italiano"),
            new ChoiceItem<string>("ar-SA", "العربية"),
            new ChoiceItem<string>("hi-IN", "हिन्दी")
        ];
        _selectedLanguage = Languages.First(x => x.Value == Text.CurrentCulture);
        Text.LanguageChanged += (_, _) => OnPropertyChanged(nameof(StatusText));
        if (installation is null)
            _ = DetectAsync();
    }

    public LocalizationService Text => LocalizationService.Current;
    public IReadOnlyList<ChoiceItem<string>> Languages { get; }
    public AsyncRelayCommand SelectFolderCommand { get; }
    public AsyncRelayCommand ContinueCommand { get; }
    public AsyncRelayCommand RetryDetectionCommand { get; }

    public string SelectedPath
    {
        get => _selectedPath;
        private set => SetProperty(ref _selectedPath, value);
    }
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (!SetProperty(ref _isSearching, value)) return;
            ContinueCommand.NotifyCanExecuteChanged();
            RetryDetectionCommand.NotifyCanExecuteChanged();
        }
    }
    public bool HasValidationResult
    {
        get => _hasValidationResult;
        private set => SetProperty(ref _hasValidationResult, value);
    }
    public bool IsValid
    {
        get => _isValid;
        private set
        {
            if (!SetProperty(ref _isValid, value)) return;
            ContinueCommand.NotifyCanExecuteChanged();
        }
    }
    public string StatusText => Text[_statusKey];
    public ChoiceItem<string>? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!SetProperty(ref _selectedLanguage, value) || value is null) return;
            Text.SetCulture(value.Value);
            _main.Settings.Language = value.Value;
        }
    }

    public async Task DetectAsync()
    {
        IsSearching = true;
        HasValidationResult = false;
        SetStatus("Setup.Searching");
        try
        {
            var installation = await _main.Root.DetectGameAsync();
            if (installation is not null)
            {
                SetInstallation(installation);
            }
            else
            {
                _installation = null;
                SelectedPath = string.Empty;
                IsValid = false;
                SetStatus("Setup.NotDetected");
                HasValidationResult = true;
            }
        }
        catch (Exception ex)
        {
            await _main.Root.Logger.WarningAsync("Automatic Among Us detection failed.", ex);
            IsValid = false;
            SetStatus("Setup.NotDetected");
            HasValidationResult = true;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task SelectFolderAsync()
    {
        var folder = _main.Root.Dialogs.PickFolder(SelectedPath);
        if (folder is null) return;
        IsSearching = true;
        try
        {
            var installation = await _main.Root.InspectGameAsync(folder);
            if (installation is null)
            {
                _installation = null;
                SelectedPath = folder;
                IsValid = false;
                HasValidationResult = true;
                SetStatus("Setup.InvalidPath");
                return;
            }
            SetInstallation(installation);
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task ContinueAsync()
    {
        if (_installation is not null)
            await _main.CompleteFirstRunAsync(_installation);
    }

    private void SetInstallation(GameInstallation installation)
    {
        _installation = installation;
        SelectedPath = installation.Path;
        IsValid = true;
        HasValidationResult = true;
        SetStatus("Setup.Detected");
    }

    private void SetStatus(string key)
    {
        _statusKey = key;
        OnPropertyChanged(nameof(StatusText));
    }
}
