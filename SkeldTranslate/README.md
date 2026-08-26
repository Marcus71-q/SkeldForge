# SkeldTranslate 0.4.3

Among Us 2026.8.18 / BepInEx 6 IL2CPP için istemci taraflı sohbet çevirisi prototipi.

## Kurulum

1. `SkeldTranslate.dll` dosyasını `Among Us/BepInEx/plugins/SkeldTranslate/` içine koyun.
2. Oyunu bir kez açıp kapatın.
3. Oyunu yeniden açın. Varsayılan `MyMemory` sağlayıcısı hesap ve API anahtarı istemez.

İsteğe bağlı DeepL kullanımı için `Provider = DeepL` yapıp `ApiKey` alanını doldurun. Anahtarsız kullanım için `Provider = MyMemory` olarak bırakın.

Gelen mesajlar `Original text (Türkçe çeviri)` biçiminde gösterilir. Sohbet ekranının üstündeki düğme gelen çeviriyi açıp kapatır. Mesaj alanının sağındaki `Çevir` düğmesi Türkçe taslağı İngilizce önizlemeye dönüştürür; mesaj yalnızca normal Enter/mavi ok ile gönderilir.

Mesaj metinleri çeviri için seçilen sağlayıcıya gönderilir; oyuncu adları gönderilmez. MyMemory anahtarsızdır ve kısa sohbetler için tasarlanmıştır; yoğun kullanımda hizmetin günlük sınırları uygulanabilir.

## Kaynaktan derleme

SkeldTranslate, Among Us tarafından üretilen yerel BepInEx IL2CPP başvurularına ihtiyaç duyar. Bu oyun dosyaları depoya dâhil edilmez. `GameRefs`, `core` ve `interop` klasörlerini içeren BepInEx klasörünü göstermelidir:

```powershell
dotnet build .\SkeldTranslate\SkeldTranslate.csproj -c Release `
  -p:GameRefs="C:\path\to\Among Us\BepInEx"
```

Kalıcı yerel kullanım için aynı yolu `SKELDTRANSLATE_GAME_REFS` ortam değişkeninde tanımlayabilirsiniz. Ana SkeldForge çözümü, oyun dosyaları olmadan herkes tarafından derlenebilmesi için yalnızca uygulamayı ve testleri içerir.
