using System.IO;
using System.Windows;
using Microsoft.Win32;
using SkeldForge.Models;
using SkeldForge.Views.Dialogs;

namespace SkeldForge.Services;

public interface IDialogService
{
    Task ShowInfoAsync(string title, string message, string? details = null);
    Task ShowErrorAsync(string title, string message, string? details = null);
    Task<bool> ConfirmAsync(string title, string message, bool isDestructive = false);
    Task<string?> PromptAsync(string title, string label, string initialValue = "");
    string? PickFolder(string? initialDirectory = null);
    string? PickModPackage(string? initialDirectory = null);
    string? PickSkeldpack(string? initialDirectory = null);
    string? SaveSkeldpack(string suggestedName, string? initialDirectory = null);
    Task<bool> ReviewPackageAsync(ModPackageAnalysis analysis);
    Task<bool> ReviewImportAsync(SkeldpackImportPreview preview);
    Task ShowModDetailsAsync(ModInfo mod);
    Task ShowCleanupReportAsync(CleanupReport report);
    Task ShowBackupManifestAsync(BackupManifest manifest);
    Task ShowRestoreReportAsync(RestoreReport report);
}

public sealed class DialogService : IDialogService
{
    private readonly LocalizationService _text = LocalizationService.Current;

    public Task ShowInfoAsync(string title, string message, string? details = null)
    {
        ShowMessage(title, message, details, MessageDialogKind.Information, false);
        return Task.CompletedTask;
    }

    public Task ShowErrorAsync(string title, string message, string? details = null)
    {
        ShowMessage(title, message, details, MessageDialogKind.Error, false);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, bool isDestructive = false) =>
        Task.FromResult(ShowMessage(title, message, null, isDestructive ? MessageDialogKind.Danger : MessageDialogKind.Warning, true));

    public Task<string?> PromptAsync(string title, string label, string initialValue = "")
    {
        var dialog = new PromptDialogWindow(title, label, initialValue) { Owner = FindOwner() };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Value.Trim() : null);
    }

    public string? PickFolder(string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null
        };
        return dialog.ShowDialog(FindOwner()) == true ? dialog.FolderName : null;
    }

    public string? PickModPackage(string? initialDirectory = null) => PickFile(
        "Among Us mod package (*.zip;*.dll)|*.zip;*.dll|ZIP archive (*.zip)|*.zip|BepInEx plugin (*.dll)|*.dll", initialDirectory);

    public string? PickSkeldpack(string? initialDirectory = null) => PickFile(
        "SkeldForge modpack (*.skeldpack)|*.skeldpack|All files (*.*)|*.*", initialDirectory);

    public string? SaveSkeldpack(string suggestedName, string? initialDirectory = null)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleanName = string.Concat(suggestedName.Select(c => invalid.Contains(c) ? '_' : c));
        var dialog = new SaveFileDialog
        {
            Filter = "SkeldForge modpack (*.skeldpack)|*.skeldpack",
            AddExtension = true,
            DefaultExt = ".skeldpack",
            FileName = $"{cleanName}.skeldpack",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null,
            OverwritePrompt = true
        };
        return dialog.ShowDialog(FindOwner()) == true ? dialog.FileName : null;
    }

    public Task<bool> ReviewPackageAsync(ModPackageAnalysis analysis)
    {
        var dialog = new PackageAnalysisWindow(analysis) { Owner = FindOwner() };
        return Task.FromResult(dialog.ShowDialog() == true);
    }

    public Task<bool> ReviewImportAsync(SkeldpackImportPreview preview)
    {
        var dialog = new SkeldpackImportWindow(preview) { Owner = FindOwner() };
        return Task.FromResult(dialog.ShowDialog() == true);
    }

    public Task ShowModDetailsAsync(ModInfo mod)
    {
        new ModDetailsWindow(mod) { Owner = FindOwner() }.ShowDialog();
        return Task.CompletedTask;
    }

    public Task ShowCleanupReportAsync(CleanupReport report)
    {
        new CleanupReportWindow(report) { Owner = FindOwner() }.ShowDialog();
        return Task.CompletedTask;
    }

    public Task ShowBackupManifestAsync(BackupManifest manifest)
    {
        new BackupManifestWindow(manifest) { Owner = FindOwner() }.ShowDialog();
        return Task.CompletedTask;
    }

    public Task ShowRestoreReportAsync(RestoreReport report)
    {
        new RestoreReportWindow(report) { Owner = FindOwner() }.ShowDialog();
        return Task.CompletedTask;
    }

    private bool ShowMessage(string title, string message, string? details, MessageDialogKind kind, bool showCancel)
    {
        var dialog = new MessageDialogWindow(title, message, details, kind, showCancel) { Owner = FindOwner() };
        return dialog.ShowDialog() == true;
    }

    private string? PickFile(string filter, string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            Multiselect = false,
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null
        };
        return dialog.ShowDialog(FindOwner()) == true ? dialog.FileName : null;
    }

    private static Window? FindOwner() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
}
