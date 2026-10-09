# Muhabbet Kuşu

Windows için yerel Türkçe metinden sese uygulaması. WinUI 3 arayüzü, **EMA Lightning** ve **Antalia-2 Mini** modellerini tek çalışma alanında sunar.

- Sekmelerden model seçimi ve Windows temasına uyumlu arayüz.
- Hız, örnekleme ve seed ayarları; Antalia için adım, CFG ve EQ kontrolleri.
- Üretim ilerlemesi, ses galerisi, oynatma ve WAV/MP3 dışa aktarma.

## Ekran Görüntüleri

**EMA Lightning**

![EMA Lightning çalışma alanı](docs/screenshots/ema-lightning.png)

**Antalia-2 Mini**

![Antalia-2 Mini ve özel ayarları](docs/screenshots/antalia-mini.png)

## Kurulum

Windows 10/11 (x64), .NET 8 SDK ve PATH üzerinde Python gerekir. MP3 dışa aktarma için FFmpeg de PATH üzerinde bulunmalıdır.

```powershell
python -m pip install ema-lightning antalia-mini
.\build.ps1
.\run.ps1
```

İlk çalıştırmada model ağırlıkları indirilir. İndirme tamamlandıktan sonra ses üretimi yerel çalışır. Üretilen dosyalar `outputs/` klasörüne kaydedilir. Publish çıktısı Windows App Runtime dosyalarını içerir; ayrı runtime paketi kurulumu gerekmez.

## Lisans

Uygulama [MIT](LICENSE) lisansı ile sunulur. Modeller ve bağımlılıklar kendi lisanslarına tabidir.
