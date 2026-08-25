# SkeldTranslate 0.3.0

Among Us 2026.8.18 / BepInEx 6 IL2CPP için istemci taraflı sohbet çevirisi prototipi.

## Kurulum

1. `SkeldTranslate.dll` dosyasını `Among Us/BepInEx/plugins/SkeldTranslate/` içine koyun.
2. Oyunu bir kez açıp kapatın.
3. Oyunu yeniden açın. Varsayılan `MyMemory` sağlayıcısı hesap ve API anahtarı istemez.

İsteğe bağlı DeepL kullanımı için `Provider = DeepL` yapıp `ApiKey` alanını doldurun. Anahtarsız kullanım için `Provider = MyMemory` olarak bırakın.

Gelen mesajlar `Original text (Türkçe çeviri)` biçiminde gösterilir. Sohbet ekranının üstündeki düğme gelen çeviriyi açıp kapatır. Mesaj alanının sağındaki `Çevir` düğmesi Türkçe taslağı İngilizce önizlemeye dönüştürür; mesaj yalnızca normal Enter/mavi ok ile gönderilir.

Mesaj metinleri çeviri için seçilen sağlayıcıya gönderilir; oyuncu adları gönderilmez. MyMemory anahtarsızdır ve kısa sohbetler için tasarlanmıştır; yoğun kullanımda hizmetin günlük sınırları uygulanabilir.
