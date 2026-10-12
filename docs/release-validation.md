# Windows dağıtım kontrolü

## Dijital imza

MIT lisansı dijital imza sağlamaz. Güvenilir bir kod imzalama sertifikası gerekir. Kendinden imzalı sertifika yayıncı doğrulaması için kullanılmaz.

Sertifikayı Windows kullanıcı sertifika deposuna kurun. Özel anahtar veya parolayı depoya eklemeyin. Windows SDK içindeki SignTool ile paketi üretin:

```powershell
.\build-setup.ps1 -SigningCertificateThumbprint '<40 karakter parmak izi>' -SignToolPath 'C:\...\signtool.exe'
```

Uygulama, setup ve kaldırma programı SHA256 ile imzalanır. Zaman damgası eklenir. Uygulama ve setup imzaları doğrulanır. Sertifika yoksa varsayılan çıktı imzasızdır. Geçerli imza, SmartScreen uyarısının kesin olarak kalkacağı anlamına gelmez.

## Temiz Windows testi

```powershell
.\scripts\prepare-windows-validation.ps1
```

Windows Sandbox bulunan bir bilgisayarda `artifacts/windows-validation/CleanWindows.wsb` dosyasını açın. Paket yalnızca test girdilerini ve sonuç klasörünü paylaşır. İlk model indirme için internet gerekir.

Başka bir temiz Windows sanal makinesinde `input` klasörünü kopyalayın. Ardından:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\test-clean-windows.ps1 -ResultsFolder C:\ValidationResults -ConfirmDisposableEnvironment
```

Test kurulum, model indirme, iki model ile çevrimdışı WAV üretimi, MP3 dönüşümü, pencere açılması ve kaldırma işlemini kontrol eder. `report.json` sonucu kaydeder. Testi geliştirme bilgisayarında çalıştırmayın. Gerçek ağ kesintisi, ilk açılış indirme akışı, görsel kontrol ve standart kullanıcı hesabı ayrıca sınanmalıdır.

Hazırlanmış test paketi, tamamlanmış temiz Windows testi değildir. Sonuç raporunda `passed: true` görülmeden dağıtımı doğrulanmış olarak işaretlemeyin.
