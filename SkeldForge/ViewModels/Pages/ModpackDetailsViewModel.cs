using System.Collections.ObjectModel;
using System.IO;
using SkeldForge.Models;
using SkeldForge.UI.Commands;
using SkeldForge.ViewModels.Infrastructure;

namespace SkeldForge.ViewModels.Pages;

public sealed class ModpackDetailsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private ModpackInfo _model;
    private GameUpdateComparison? _gameUpdate;
    private bool _isCheckingGameUpdate;

    public ModpackDetailsViewModel(MainViewModel main, ModpackInfo model)
    {
        _main = main;
        _model = model;
        BackCommand = new RelayCommand(() => _main.Navigate("modpacks"));
        PlayCommand = new AsyncRelayCommand(PlayAsync);
        AddModCommand = new AsyncRelayCommand(SelectAndInstallAsync);
        ToggleModCommand = new AsyncRelayCommand<ModCardViewModel>(ToggleModAsync, x => x is not null);
        RemoveModCommand = new AsyncRelayCommand<ModCardViewModel>(RemoveModAsync, x => x is not null);
        ModDetailsCommand = new AsyncRelayCommand<ModCardViewModel>(x => x is null ? Task.CompletedTask : _main.Root.Dialogs.ShowModDetailsAsync(x.Model));
        DuplicateCommand = new AsyncRelayCommand(DuplicateAsync);
        RenameCommand = new AsyncRelayCommand(RenameAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        EditVersionCommand = new AsyncRelayCommand(EditVersionAsync);
        CheckGameUpdateCommand = new AsyncRelayCommand(CheckGameUpdateAsync, () => !IsCheckingGameUpdate && _main.GameInstallation is not null);
        UpdateGameCommand = new AsyncRelayCommand(UpdateGameAsync, () => HasGameUpdate && !IsCheckingGameUpdate);
        BackupInstanceCommand = new AsyncRelayCommand(BackupInstanceAsync);
        OpenFolderCommand = new RelayCommand(() => _main.Root.OpenFolder(Model.GameDirectory));
        _main.Text.LanguageChanged += (_, _) =>
            OnPropertiesChanged(nameof(GameUpdateStatusText), nameof(SourceGameVersionText));
        RebuildMods();
        _ = CheckGameUpdateCoreAsync(showFailure: false);
    }

    public ModpackInfo Model => _model;
    public ObservableCollection<ModCardViewModel> Mods { get; } = [];
    public RelayCommand BackCommand { get; }
    public AsyncRelayCommand PlayCommand { get; }
    public AsyncRelayCommand AddModCommand { get; }
    public AsyncRelayCommand<ModCardViewModel> ToggleModCommand { get; }
    public AsyncRelayCommand<ModCardViewModel> RemoveModCommand { get; }
    public AsyncRelayCommand<ModCardViewModel> ModDetailsCommand { get; }
    public AsyncRelayCommand DuplicateCommand { get; }
    public AsyncRelayCommand RenameCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand EditVersionCommand { get; }
    public AsyncRelayCommand CheckGameUpdateCommand { get; }
    public AsyncRelayCommand UpdateGameCommand { get; }
    public AsyncRelayCommand BackupInstanceCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public bool IsCheckingGameUpdate
    {
        get => _isCheckingGameUpdate;
        private set
        {
            if (!SetProperty(ref _isCheckingGameUpdate, value)) return;
            CheckGameUpdateCommand.NotifyCanExecuteChanged();
            UpdateGameCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(GameUpdateStatusText));
        }
    }
    public bool HasGameUpdate => _gameUpdate?.HasChanged == true;
    public string SourceGameVersionText => Model.SourceGameVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        ? _main.Text["Common.Unknown"]
        : Model.SourceGameVersion;
    public string GameUpdateStatusText => IsCheckingGameUpdate
        ? _main.Text["Modpack.CheckingGameUpdate"]
        : _gameUpdate is null
            ? _main.Text["Modpack.GameUpdateUnknown"]
            : _main.Text[_gameUpdate.HasChanged ? "Modpack.GameUpdateDetected" : "Modpack.GameCurrent"];

    public async Task HandleDroppedFilesAsync(IReadOnlyList<string> files)
    {
        if (files.Count != 1 || !string.Equals(Path.GetExtension(files[0]), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            await _main.Root.Dialogs.ShowInfoAsync(_main.Text["Dialog.WarningTitle"], _main.Text["Package.DropOnlyZip"]);
            return;
        }
        await AnalyzeAndInstallAsync(files[0]);
    }

    private async Task SelectAndInstallAsync()
    {
        var path = _main.Root.Dialogs.PickZip();
        if (path is not null) await AnalyzeAndInstallAsync(path);
    }

    private async Task AnalyzeAndInstallAsync(string path)
    {
        var analysis = await _main.RunBusyAsync("Progress.Analyzing", _ => _main.Root.AnalyzePackageAsync(path));
        if (!analysis.IsSafe)
        {
            await _main.Root.Dialogs.ReviewPackageAsync(analysis);
            return;
        }
        if (!await _main.Root.Dialogs.ReviewPackageAsync(analysis)) return;

        var result = await _main.RunBusyAsync("Progress.Installing", progress =>
            _main.Root.InstallModFromArchiveAsync(Model, path, analysis, progress));
        if (!result.Success || result.Value is null) { await _main.ShowFailureAsync(result); return; }
        SetModel(result.Value.Modpack);
        await _main.RefreshCollectionsAsync();
        _main.ShowStatus(_main.Text.Format("Success.ModInstalled", result.Value.Mod.Name));
    }

    private async Task ToggleModAsync(ModCardViewModel? card)
    {
        if (card is null) return;
        var target = !card.IsEnabled;
        var result = await _main.RunBusyAsync("Progress.Installing", progress =>
            _main.Root.SetModEnabledAsync(Model, card.Id, target, progress));
        if (!result.Success || result.Value is null) { await _main.ShowFailureAsync(result); return; }
        SetModel(result.Value);
        await _main.RefreshCollectionsAsync();
    }

    private async Task RemoveModAsync(ModCardViewModel? card)
    {
        if (card is null) return;
        if (!await _main.Root.Dialogs.ConfirmAsync(
                _main.Text["Mod.RemoveTitle"],
                _main.Text.Format("Mod.RemoveMessage", card.Name), true)) return;
        var result = await _main.RunBusyAsync("Progress.Installing", progress =>
            _main.Root.RemoveModAsync(Model, card.Id, progress));
        if (!result.Success || result.Value is null) { await _main.ShowFailureAsync(result); return; }
        SetModel(result.Value);
        await _main.RefreshCollectionsAsync();
        _main.ShowStatus(_main.Text.Format("Success.ModRemoved", card.Name));
    }

    private async Task PlayAsync()
    {
        var result = await _main.Root.LaunchModpackAsync(Model);
        if (!result.Success) { await _main.ShowFailureAsync(result); return; }
        _main.Settings.LastLaunchedModpackId = Model.Id;
        await _main.Root.SaveSettingsAsync(_main.Settings);
        await RefreshFromDiskAsync();
        await _main.RefreshCollectionsAsync();
    }

    private async Task DuplicateAsync()
    {
        var name = await _main.Root.Dialogs.PromptAsync(
            _main.Text["Modpack.DuplicateTitle"], _main.Text["Modpack.NamePrompt"],
            _main.Text.Format("Modpack.DuplicateName", Model.Name));
        if (string.IsNullOrWhiteSpace(name)) return;
        var result = await _main.RunBusyAsync("Progress.Copying", progress => _main.Root.DuplicateModpackAsync(Model.Id, name, progress));
        if (!result.Success || result.Value is null) { await _main.ShowFailureAsync(result); return; }
        await _main.RefreshCollectionsAsync();
        _main.ShowStatus(_main.Text.Format("Success.ModpackDuplicated", result.Value.Name));
        _main.ShowModpack(result.Value);
    }

    private async Task RenameAsync()
    {
        var name = await _main.Root.Dialogs.PromptAsync(_main.Text["Modpack.RenameTitle"], _main.Text["Modpack.NamePrompt"], Model.Name);
        if (string.IsNullOrWhiteSpace(name) || name == Model.Name) return;
        var result = await _main.Root.RenameModpackAsync(Model.Id, name);
        if (!result.Success || result.Value is null) { await _main.ShowFailureAsync(result); return; }
        SetModel(result.Value);
        await _main.RefreshCollectionsAsync();
    }

    private async Task EditVersionAsync()
    {
        var version = await _main.Root.Dialogs.PromptAsync(_main.Text["Modpack.ChangeVersion"], _main.Text["Modpack.VersionPrompt"], Model.Version);
        if (string.IsNullOrWhiteSpace(version) || version == Model.Version) return;
        Model.Version = version.Trim();
        var result = await _main.Root.SaveModpackAsync(Model);
        if (!result.Success || result.Value is null) { await _main.ShowFailureAsync(result); return; }
        SetModel(result.Value);
        await _main.RefreshCollectionsAsync();
    }

    private Task CheckGameUpdateAsync() => CheckGameUpdateCoreAsync(showFailure: true);

    private async Task CheckGameUpdateCoreAsync(bool showFailure)
    {
        if (_main.GameInstallation is null) return;
        IsCheckingGameUpdate = true;
        try
        {
            _gameUpdate = _main.Root.CompareGameVersion(_main.GameInstallation, Model);
            OnPropertiesChanged(nameof(HasGameUpdate), nameof(GameUpdateStatusText));
            UpdateGameCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            await _main.Root.Logger.WarningAsync("Could not compare the current game with the modpack source.", ex);
            if (showFailure)
            {
                await _main.Root.Dialogs.ShowErrorAsync(
                    _main.Text["Dialog.ErrorTitle"],
                    _main.Text["Modpack.GameUpdateCheckFailed"],
                    ex.Message);
            }
        }
        finally
        {
            IsCheckingGameUpdate = false;
        }
    }

    private async Task UpdateGameAsync()
    {
        if (_main.GameInstallation is null || !HasGameUpdate) return;
        if (!await _main.Root.Dialogs.ConfirmAsync(
                _main.Text["Modpack.UpdateGameTitle"],
                _main.Text["Modpack.UpdateGameMessage"],
                true)) return;

        var result = await _main.RunBusyAsync("Modpack.UpdatingGame", progress =>
            _main.Root.UpdateModpackBaseAsync(Model, _main.GameInstallation.Path, progress));
        if (!result.Success || result.Value is null)
        {
            await _main.ShowFailureAsync(result);
            return;
        }

        SetModel(result.Value);
        await _main.RefreshCollectionsAsync();
        await CheckGameUpdateCoreAsync(showFailure: false);
        _main.ShowStatus(_main.Text["Success.InstanceUpdated"]);
    }

    private async Task BackupInstanceAsync()
    {
        var result = await _main.RunBusyAsync("Progress.BackingUp", progress =>
            _main.Root.CreateInstanceBackupAsync(Model, progress));
        if (!result.Success || result.Value is null)
        {
            await _main.ShowFailureAsync(result);
            return;
        }

        _main.ShowStatus(_main.Text["Success.InstanceBackupCreated"]);
    }

    private async Task DeleteAsync()
    {
        if (!await _main.Root.Dialogs.ConfirmAsync(
                _main.Text["Modpack.DeleteTitle"],
                _main.Text.Format("Modpack.DeleteMessage", Model.Name), true)) return;
        var result = await _main.Root.DeleteModpackAsync(Model.Id);
        if (!result.Success) { await _main.ShowFailureAsync(result); return; }
        await _main.RefreshCollectionsAsync();
        _main.Navigate("modpacks");
    }

    private async Task ExportAsync()
    {
        var target = _main.Root.Dialogs.SaveSkeldpack(Model.Name);
        if (target is null) return;
        var result = await _main.RunBusyAsync("Progress.Exporting", progress => _main.Root.ExportSkeldpackAsync(Model, target, progress));
        if (!result.Success) { await _main.ShowFailureAsync(result); return; }
        _main.ShowStatus(_main.Text["Success.Exported"]);
    }

    private async Task RefreshFromDiskAsync()
    {
        var updated = await _main.Root.GetModpackAsync(Model.Id);
        if (updated is not null) SetModel(updated);
    }

    private void SetModel(ModpackInfo model)
    {
        _model = model;
        OnPropertiesChanged(nameof(Model), nameof(SourceGameVersionText));
        RebuildMods();
    }

    private void RebuildMods()
    {
        Mods.Clear();
        foreach (var mod in Model.Mods.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            Mods.Add(new ModCardViewModel(mod));
    }
}
