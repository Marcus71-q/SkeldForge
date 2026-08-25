using System.Collections.ObjectModel;
using SkeldForge.Models;
using SkeldForge.UI.Commands;
using SkeldForge.ViewModels.Infrastructure;

namespace SkeldForge.ViewModels.Pages;

public sealed class VanillaCleanupViewModel : ObservableObject, IRefreshableViewModel
{
    private readonly MainViewModel _main;
    private CleanupScanResult? _scan;
    private CleanupReport? _report;
    private bool _isScanning;

    public VanillaCleanupViewModel(MainViewModel main)
    {
        _main = main;
        StartScanCommand = new AsyncRelayCommand(ScanAsync, () => HasGame && !IsScanning);
        CleanCommand = new AsyncRelayCommand(CleanAsync, () => Scan is { KnownCount: > 0 } && !IsScanning);
        ScanAgainCommand = new AsyncRelayCommand(ScanAsync, () => HasGame && !IsScanning);
        CancelPreviewCommand = new RelayCommand(CancelPreview);
        ViewChangesCommand = new AsyncRelayCommand(() => Report is null ? Task.CompletedTask : _main.Root.Dialogs.ShowCleanupReportAsync(Report));
        RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, () => Report is not null && !string.IsNullOrWhiteSpace(Report.BackupId));
        LaunchVanillaCommand = new AsyncRelayCommand(LaunchVanillaAsync, () => HasGame);
    }

    public ObservableCollection<CleanupFinding> KnownFindings { get; } = [];
    public ObservableCollection<CleanupFinding> ProtectedFindings { get; } = [];
    public ObservableCollection<CleanupFinding> UnknownFindings { get; } = [];
    public AsyncRelayCommand StartScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public AsyncRelayCommand ScanAgainCommand { get; }
    public RelayCommand CancelPreviewCommand { get; }
    public AsyncRelayCommand ViewChangesCommand { get; }
    public AsyncRelayCommand RestoreBackupCommand { get; }
    public AsyncRelayCommand LaunchVanillaCommand { get; }
    public bool HasGame => _main.GameInstallation is not null;
    public bool IsIntro => Scan is null && Report is null;
    public bool HasScan => Scan is not null && Report is null;
    public bool HasReport => Report is not null;
    public bool IsScanning { get => _isScanning; private set { if (SetProperty(ref _isScanning, value)) NotifyCommands(); } }
    public CleanupScanResult? Scan { get => _scan; private set { if (SetProperty(ref _scan, value)) OnPropertiesChanged(nameof(IsIntro), nameof(HasScan)); } }
    public CleanupReport? Report { get => _report; private set { if (SetProperty(ref _report, value)) OnPropertiesChanged(nameof(IsIntro), nameof(HasScan), nameof(HasReport)); } }

    public void Refresh()
    {
        OnPropertyChanged(nameof(HasGame));
        NotifyCommands();
    }

    private async Task ScanAsync()
    {
        if (_main.GameInstallation is null) return;
        IsScanning = true;
        Report = null;
        try
        {
            var result = await _main.RunBusyAsync("Cleanup.Scanning", progress => _main.Root.ScanVanillaAsync(_main.GameInstallation.Path, progress));
            if (!result.Success || result.Value is null) { await _main.ShowFailureAsync(result); return; }
            Scan = result.Value;
            RebuildFindings();
            NotifyCommands();
        }
        finally { IsScanning = false; }
    }

    private async Task CleanAsync()
    {
        if (Scan is null) return;
        if (!await _main.Root.Dialogs.ConfirmAsync(_main.Text["Cleanup.ConfirmTitle"], _main.Text["Cleanup.ConfirmMessage"], true)) return;
        var result = await _main.RunBusyAsync("Cleanup.Progress", progress => _main.Root.CleanVanillaAsync(Scan, progress));
        if (!result.Success || result.Value is null) { await _main.ShowFailureAsync(result); return; }
        Report = result.Value;
        Scan = null;
        await _main.Root.Dialogs.ShowCleanupReportAsync(result.Value);
        NotifyCommands();
    }

    private async Task RestoreBackupAsync()
    {
        if (Report is null) return;
        var backup = await _main.Root.GetBackupAsync(Report.BackupId);
        if (backup is null) return;
        if (!await _main.Root.Dialogs.ConfirmAsync(_main.Text["Backups.RestoreTitle"], _main.Text["Backups.RestoreMessage"])) return;
        var result = await _main.RunBusyAsync("Progress.Restoring", progress => _main.Root.RestoreBackupAsync(backup, progress));
        if (result.Value is not null)
        {
            await _main.Root.Dialogs.ShowRestoreReportAsync(result.Value);
            return;
        }
        if (!result.Success) await _main.ShowFailureAsync(result);
    }

    private async Task LaunchVanillaAsync()
    {
        if (_main.GameInstallation is null) return;
        var result = await _main.Root.LaunchVanillaAsync(_main.GameInstallation.Path);
        if (!result.Success) await _main.ShowFailureAsync(result);
    }

    private void RebuildFindings()
    {
        KnownFindings.Clear(); ProtectedFindings.Clear(); UnknownFindings.Clear();
        if (Scan is null) return;
        foreach (var item in Scan.Findings)
        {
            if (item.Classification is CleanupClassification.KnownModComponent or CleanupClassification.KnownModPlugin) KnownFindings.Add(item);
            else if (item.Classification == CleanupClassification.ProtectedGameComponent) ProtectedFindings.Add(item);
            else UnknownFindings.Add(item);
        }
    }

    private void CancelPreview()
    {
        Scan = null;
        KnownFindings.Clear();
        ProtectedFindings.Clear();
        UnknownFindings.Clear();
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        StartScanCommand.NotifyCanExecuteChanged(); CleanCommand.NotifyCanExecuteChanged(); ScanAgainCommand.NotifyCanExecuteChanged();
        RestoreBackupCommand.NotifyCanExecuteChanged(); LaunchVanillaCommand.NotifyCanExecuteChanged();
    }
}
