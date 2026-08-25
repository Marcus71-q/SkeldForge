using System.Collections.ObjectModel;
using SkeldForge.UI.Commands;
using SkeldForge.ViewModels.Infrastructure;

namespace SkeldForge.ViewModels.Pages;

public sealed class BackupsViewModel : ObservableObject, IRefreshableViewModel
{
    private readonly MainViewModel _main;
    public BackupsViewModel(MainViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        ViewCommand = new AsyncRelayCommand<BackupCardViewModel>(x => x is null ? Task.CompletedTask : _main.Root.Dialogs.ShowBackupManifestAsync(x.Model));
        RestoreCommand = new AsyncRelayCommand<BackupCardViewModel>(RestoreAsync, x => x is not null);
        DeleteCommand = new AsyncRelayCommand<BackupCardViewModel>(DeleteAsync, x => x is not null);
    }

    public ObservableCollection<BackupCardViewModel> Backups { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand<BackupCardViewModel> ViewCommand { get; }
    public AsyncRelayCommand<BackupCardViewModel> RestoreCommand { get; }
    public AsyncRelayCommand<BackupCardViewModel> DeleteCommand { get; }

    public void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var backups = await _main.Root.GetBackupsAsync();
        Backups.Clear();
        foreach (var item in backups.OrderByDescending(x => x.Timestamp)) Backups.Add(new BackupCardViewModel(item));
    }

    private async Task RestoreAsync(BackupCardViewModel? card)
    {
        if (card is null) return;
        if (!await _main.Root.Dialogs.ConfirmAsync(_main.Text["Backups.RestoreTitle"], _main.Text["Backups.RestoreMessage"])) return;
        var result = await _main.RunBusyAsync("Progress.Restoring", progress => _main.Root.RestoreBackupAsync(card.Model, progress));
        if (result.Value is { ConflictCount: > 0 })
        {
            await _main.Root.Dialogs.ShowRestoreReportAsync(result.Value);
            if (!await _main.Root.Dialogs.ConfirmAsync(
                    _main.Text["Backups.OverwriteTitle"],
                    _main.Text["Backups.OverwriteMessage"],
                    true)) return;
            result = await _main.RunBusyAsync("Progress.Restoring", progress =>
                _main.Root.RestoreBackupAsync(card.Model, progress, overwriteConflicts: true));
        }
        if (result.Value is not null)
        {
            await _main.Root.Dialogs.ShowRestoreReportAsync(result.Value);
            return;
        }
        if (!result.Success) await _main.ShowFailureAsync(result);
    }

    private async Task DeleteAsync(BackupCardViewModel? card)
    {
        if (card is null) return;
        if (!await _main.Root.Dialogs.ConfirmAsync(_main.Text["Backups.DeleteTitle"], _main.Text.Format("Backups.DeleteMessage", card.DisplayName), true)) return;
        var result = await _main.Root.DeleteBackupAsync(card.Model);
        if (!result.Success) { await _main.ShowFailureAsync(result); return; }
        await LoadAsync();
        _main.ShowStatus(_main.Text["Success.BackupDeleted"]);
    }
}
