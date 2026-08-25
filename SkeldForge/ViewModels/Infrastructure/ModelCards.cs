using SkeldForge.Models;
using SkeldForge.Services;

namespace SkeldForge.ViewModels.Infrastructure;

public sealed class ModpackCardViewModel : ObservableObject
{
    private readonly LocalizationService _text = LocalizationService.Current;
    public ModpackCardViewModel(ModpackInfo model)
    {
        Model = model;
        _text.LanguageChanged += OnLanguageChanged;
    }

    public ModpackInfo Model { get; }
    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Version => Model.Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        ? _text["Common.Unknown"]
        : Model.Version;
    public int ModCount => Model.Mods.Count;
    public string ModCountText => _text.Format("Modpacks.ModCount", Model.Mods.Count);
    public string CreatedText => _text.Format("Modpacks.Created", Model.CreatedAt.ToLocalTime().ToString("d"));
    public string LastUsedText => Model.LastLaunchedAt is null
        ? CreatedText
        : _text.Format("Home.LastPlayed", Model.LastLaunchedAt.Value.ToLocalTime().ToString("g"));
    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertiesChanged(nameof(ModCountText), nameof(CreatedText), nameof(LastUsedText));
}

public sealed class ModCardViewModel : ObservableObject
{
    private readonly LocalizationService _text = LocalizationService.Current;
    private bool _isEnabled;

    public ModCardViewModel(ModInfo model)
    {
        Model = model;
        _isEnabled = model.IsEnabled;
        _text.LanguageChanged += (_, _) => OnPropertiesChanged(nameof(Version), nameof(StatusText), nameof(CompatibilityText), nameof(ImportedText));
    }

    public ModInfo Model { get; }
    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Version => Model.Version;
    public string HashShort => Model.PackageHash.Length > 14 ? $"{Model.PackageHash[..14]}…" : Model.PackageHash;
    public string StatusText => _text[_isEnabled ? "Common.Enabled" : "Common.Disabled"];
    public string CompatibilityText => _text[$"Status.{Model.Compatibility}"];
    public string ImportedText => _text.Format("ModLibrary.Imported", Model.ImportDate.ToLocalTime().ToString("g"));
    public bool HasMissingDependency => Model.Dependencies.Any(x => x.Status == DependencyStatus.Missing);
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value)) return;
            Model.IsEnabled = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }
}

public sealed class BackupCardViewModel : ObservableObject
{
    private readonly LocalizationService _text = LocalizationService.Current;
    public BackupCardViewModel(BackupManifest model)
    {
        Model = model;
        _text.LanguageChanged += (_, _) => OnPropertiesChanged(nameof(KindText), nameof(FileCountText));
    }
    public BackupManifest Model { get; }
    public string Id => Model.Id;
    public string DisplayName => string.IsNullOrWhiteSpace(Model.DisplayName) ? KindText : Model.DisplayName;
    public string KindText => _text[Model.Kind == BackupKind.VanillaCleanup ? "Backups.VanillaCleanup" : "Backups.Instance"];
    public string DateText => Model.Timestamp.ToLocalTime().ToString("g");
    public string FileCountText => _text.Format("Backups.FileCount", Model.Files.Count);
}
