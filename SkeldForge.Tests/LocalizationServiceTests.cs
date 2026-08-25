using SkeldForge.Services;
using SkeldForge.Models;
using System.Text.Json;

namespace SkeldForge.Tests;

public sealed class LocalizationServiceTests
{
    [Fact]
    public void UnsupportedCultureFallsBackToEnglishAndMissingKeyIsVisible()
    {
        var service = LocalizationService.Current;
        var original = service.CurrentCulture;
        try
        {
            Assert.True(service.SetCulture("unsupported-Culture"));
            Assert.Equal("en-US", service.CurrentCulture);
            Assert.Equal("Create Modpack", service["Modpacks.Create"]);
            Assert.Equal("[Test.Key.That.Does.Not.Exist]", service["Test.Key.That.Does.Not.Exist"]);
        }
        finally
        {
            service.SetCulture(original);
        }
    }

    [Fact]
    public void TurkishCatalogCanBeAppliedWithoutRestart()
    {
        var service = LocalizationService.Current;
        var original = service.CurrentCulture;
        try
        {
            var notifications = 0;
            EventHandler handler = (_, _) => notifications++;
            service.LanguageChanged += handler;
            try
            {
                Assert.True(service.SetCulture("tr-TR"));
                Assert.Equal("tr-TR", service.CurrentCulture);
                Assert.Equal("Modpack Oluştur", service["Modpacks.Create"]);
                Assert.True(notifications > 0);
            }
            finally
            {
                service.LanguageChanged -= handler;
            }
        }
        finally
        {
            service.SetCulture(original);
        }
    }

    [Theory]
    [InlineData("ru-RU", "Главная")]
    [InlineData("es-ES", "Inicio")]
    [InlineData("ja-JP", "ホーム")]
    [InlineData("th-TH", "หน้าหลัก")]
    [InlineData("id-ID", "Beranda")]
    [InlineData("ko-KR", "홈")]
    [InlineData("zh-CN", "主页")]
    [InlineData("de-DE", "Startseite")]
    [InlineData("fr-FR", "Accueil")]
    [InlineData("pt-BR", "Início")]
    [InlineData("it-IT", "Home")]
    [InlineData("ar-SA", "الرئيسية")]
    [InlineData("hi-IN", "होम")]
    public void AdditionalCatalogsCanBeApplied(string culture, string expectedHome)
    {
        var service = LocalizationService.Current;
        var original = service.CurrentCulture;
        try
        {
            Assert.True(service.SetCulture(culture));
            Assert.Equal(culture, service.CurrentCulture);
            Assert.Equal(expectedHome, service["Nav.Home"]);
            Assert.NotEqual("Create Modpack", service["Modpacks.Create"]);
            Assert.False(service["Modpacks.Create"].StartsWith("[", StringComparison.Ordinal));
        }
        finally
        {
            service.SetCulture(original);
        }
    }

    [Fact]
    public void FreshSettingsContainNoNonFiniteJsonNumbers()
    {
        var json = JsonSerializer.Serialize(new AppSettings { Language = "en-US" }, JsonDefaults.Options);
        Assert.DoesNotContain("NaN", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Infinity", json, StringComparison.OrdinalIgnoreCase);
    }
}
