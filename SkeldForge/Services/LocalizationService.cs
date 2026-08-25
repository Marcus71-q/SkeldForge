using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SkeldForge.Services;

/// <summary>Loads the user-facing JSON catalog and notifies every XAML localization binding on a live language change.</summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationService> Shared = new(() => new LocalizationService());
    private readonly Dictionary<string, string> _fallback = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private string _currentCulture = "en-US";

    private LocalizationService()
    {
        LoadCatalog("en-US", _fallback);
        SetCulture("en-US");
    }

    public static LocalizationService Current => Shared.Value;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string CurrentCulture => _currentCulture;

    public IReadOnlyList<string> SupportedCultures { get; } =
        ["en-US", "tr-TR", "ru-RU", "es-ES", "ja-JP", "th-TH", "id-ID",
         "ko-KR", "zh-CN", "de-DE", "fr-FR", "pt-BR", "it-IT", "ar-SA", "hi-IN"];

    public string this[string key]
    {
        get
        {
            if (_strings.TryGetValue(key, out var value))
                return value;
            if (_fallback.TryGetValue(key, out value))
                return value;
            return $"[{key}]";
        }
    }

    public bool SetCulture(string? cultureName)
    {
        var normalized = SupportedCultures.FirstOrDefault(x =>
            x.Equals(cultureName, StringComparison.OrdinalIgnoreCase)) ?? "en-US";
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!LoadCatalog(normalized, next))
        {
            if (normalized == "en-US" || _fallback.Count == 0)
                return false;
            next = new Dictionary<string, string>(_fallback, StringComparer.OrdinalIgnoreCase);
            normalized = "en-US";
        }

        _strings = next;
        _currentCulture = normalized;
        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public string Get(string key) => this[key];

    public string Format(string key, params object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, this[key], args);
        }
        catch (FormatException)
        {
            return this[key];
        }
    }

    private static bool LoadCatalog(string cultureName, IDictionary<string, string> destination)
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Languages", $"{cultureName}.json"),
                Path.Combine(Environment.CurrentDirectory, "Languages", $"{cultureName}.json"),
                Path.Combine(Environment.CurrentDirectory, "SkeldForge", "Languages", $"{cultureName}.json")
            };
            var path = candidates.FirstOrDefault(File.Exists);
            if (path is null)
                return false;

            using var stream = File.OpenRead(path);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (values is null)
                return false;

            foreach (var pair in values)
                destination[pair.Key] = pair.Value;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
