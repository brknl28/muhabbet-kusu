# Muhabbet Kuşu

Türkçe metinden sese dönüştürme (TTS) masaüstü uygulaması.

Bu uygulama iki yerel Türkçe ses modelini destekler:
- **EMA Lightning**
- **Antalia-2 Mini**

> **Not (Modeller Hakkında):** Model ağırlıkları doğrudan proje deposunun içinde yer almaz. İlk çalıştırmada Python kütüphaneleri (`ema-lightning` ve `antalia-mini`) aracılığıyla Hugging Face üzerinden otomatik olarak indirilir ve yerel bilgisayarınızda önbelleğe alınır. Sonraki kullanımlar tamamen çevrimdışı ve yerel çalışır.

## Hızlı Başlangıç

1. Gerekli Python paketlerini kurun:
```powershell
pip install ema-lightning antalia-mini
```

2. Uygulamayı derleyin ve çalıştırın:
```powershell
.\build.ps1
.\run.ps1
```

## Lisans

Bu proje **MIT Lisansı** ile lisanslanmıştır. Kullanılan TTS modelleri ve kütüphaneleri kendi açık kaynak lisanslarına (Apache-2.0 / BSD) tabidir.
