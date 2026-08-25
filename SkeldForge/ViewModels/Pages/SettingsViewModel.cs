using System.Diagnostics;
using System.IO;
using SkeldForge.Models;
using SkeldForge.UI.Commands;
using SkeldForge.ViewModels.Infrastructure;

namespace SkeldForge.ViewModels.Pages;

public sealed class SettingsViewModel : ObservableObject, IRefreshableViewModel
{
    private readonly MainViewModel _main;
    private string _gamePath = string.Empty;
    private string _dataRoot = string.Empty;
    private string _backupRoot = string.Empty;
    private ChoiceItem<string>? _selectedLanguage;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Languages =
        [
            new("en-US", "English"), new("tr-TR", "Türkçe"), new("ru-RU", "Русский"),
            new("es-ES", "Español"), new("ja-JP", "日本語"), new("th-TH", "ไทย"),
            new("id-ID", "Bahasa Indonesia"), new("ko-KR", "한국어"), new("zh-CN", "简体中文"),
            new("de-DE", "Deutsch"), new("fr-FR", "Français"), new("pt-BR", "Português (Brasil)"),
            new("it-IT", "Italiano"), new("ar-SA", "العربية"), new("hi-IN", "हिन्दी")
        ];
        BrowseGameCommand = new AsyncRelayCommand(BrowseGameAsync);
        BrowseDataCommand = new RelayCommand(BrowseData);
        BrowseBackupCommand = new RelayCommand(BrowseBackup);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ResetCommand = new AsyncRelayCommand(ResetAsync);
        OpenLogsCommand = new RelayCommand(() => _main.Root.OpenFolder(_main.Root.Paths.LogsDirectory));
        OpenInstancesCommand = new RelayCommand(() => _main.Root.OpenFolder(_main.Root.Paths.InstancesDirectory));
        OpenLibraryCommand = new RelayCommand(() => _main.Root.OpenFolder(_main.Root.Paths.ModLibraryDirectory));
        Refresh();
    }

    public IReadOnlyList<ChoiceItem<string>> Languages { get; }
    public AsyncRelayCommand BrowseGameCommand { get; }
    public RelayCommand BrowseDataCommand { get; }
    public RelayCommand BrowseBackupCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand OpenInstancesCommand { get; }
    public RelayCommand OpenLibraryCommand { get; }
    public string GamePath { get => _gamePath; set => SetProperty(ref _gamePath, value); }
    public string DataRoot { get => _dataRoot; set => SetProperty(ref _dataRoot, value); }
    public string BackupRoot { get => _backupRoot; set => SetProperty(ref _backupRoot, value); }
    public ChoiceItem<string>? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!SetProperty(ref _selectedLanguage, value) || value is null) return;
            _main.Text.SetCulture(value.Value);
        }
    }

    public void Refresh()
    {
        GamePath = _main.Settings.AmongUsPath;
        DataRoot = _main.Settings.DataRoot;
        BackupRoot = _main.Settings.BackupRoot;
        SelectedLanguage = Languages.First(x => x.Value == _main.Settings.Language);
    }

    private async Task BrowseGameAsync()
    {
        var path = _main.Root.Dialogs.PickFolder(GamePath);
        if (path is null) return;
        var installation = await _main.Root.InspectGameAsync(path);
        if (installation is null)
        {
            await _main.Root.Dialogs.ShowErrorAsync(_main.Text["Dialog.ErrorTitle"], _main.Text["Settings.PathInvalid"]);
            return;
        }
        GamePath = installation.Path;
    }

    private void BrowseData()
    {
        var path = _main.Root.Dialogs.PickFolder(DataRoot);
        if (path is not null) DataRoot = path;
    }

    private void BrowseBackup()
    {
        var path = _main.Root.Dialogs.PickFolder(BackupRoot);
        if (path is not null) BackupRoot = path;
    }

    private async Task SaveAsync()
    {
        var installation = await _main.Root.InspectGameAsync(GamePath);
        if (installation is null)
        {
            await _main.Root.Dialogs.ShowErrorAsync(_main.Text["Dialog.ErrorTitle"], _main.Text["Settings.PathInvalid"]);
            return;
        }
        var settings = _main.Settings;
        settings.Language = SelectedLanguage?.Value ?? "en-US";
        settings.AmongUsPath = installation.Path;
        settings.DataRoot = Path.GetFullPath(DataRoot);
        settings.BackupRoot = Path.GetFullPath(BackupRoot);
        await _main.Root.SaveSettingsAsync(settings);
        _main.ApplySettings(settings);
        await _main.RefreshGameInstallationAsync();
        await _main.RefreshCollectionsAsync();
        _main.ShowStatus(_main.Text["Settings.Saved"]);
    }

    private async Task ResetAsync()
    {
        if (!await _main.Root.Dialogs.ConfirmAsync(_main.Text["Settings.ResetTitle"], _main.Text["Settings.ResetMessage"])) return;
        var settings = await _main.Root.ResetSettingsAsync();
        _main.Text.SetCulture(settings.Language);
        _main.ApplySettings(settings);
        Refresh();
        await _main.RefreshGameInstallationAsync();
        _main.ShowStatus(_main.Text["Settings.Saved"]);
        if (string.IsNullOrWhiteSpace(settings.AmongUsPath))
            _main.RequireSetup();
    }
}
