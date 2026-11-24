# OpenVPN Hızlı Arama

OpenVPN Connect'teki kayıtlı profilleri firma adı, sunucu adresi, kullanıcı adı veya kaynak dosya adına göre filtreleyen küçük Windows yardımcı uygulaması.

- Yan panel: çerçevesiz pencere, OpenVPN Connect'in görünür çerçevesine (DWM extended frame bounds) aralıksız ve aynı yükseklikte yapışır. WinEvent kancaları (konum, küçültme, gösterme/gizleme, ön plan) ile anında takip eder; z-sırası her zaman OpenVPN'in hemen üstündedir. Renkler OpenVPN Connect 3.x temasından alındı.
- Uygulama ikonu `IconArt` ile çizilir; `app.ico` exe'ye `/win32icon` ve kaynak olarak gömülür. İkonu yeniden üretmek için önce ikonsuz derleyin, `IconArt.WriteIco("app.ico")` metodunu reflection ile çağırın, sonra aşağıdaki komutla derleyin.
- Koyu tema, koyu başlık çubuğu (Windows 11), DPI farkındalığı. Liste özel çizilir: avatar, ad, sunucu · kullanıcı, son bağlantı zamanı, yumuşak kaydırma.
- Arama boşken gruplar: Sabitlenenler, Son bağlanılanlar (5), Tüm profiller (A–Z). Arama sonuçları eşleşme kalitesine göre sıralanır, eşleşen harfler vurgulanır.
- Sabitleme: satırdaki yıldız, sağ tık menüsü veya `Ctrl+P`. Kayıt: `%APPDATA%\OpenVpnQuickSearch\pinned.txt`.
- Sağ tık menüsü: sunucu adresi, kullanıcı adı veya profil adını panoya kopyalar.
- Yazdıkça filtreler. Birden çok kelime desteklenir; her kelime en az bir alanla eşleşmelidir. Türkçe karakter ve aksan farkı yok sayılır.
- `Enter` veya profil kartına tek tıklama seçili profili OpenVPN Connect'in `--connect-shortcut=<profil-id>` seçeneğiyle hemen başlatır. Panel yapışıksa açık kalır, ayrıysa kısa bir animasyondan sonra gizlenir.
- `Yukarı/Aşağı`, `PageUp/PageDown` profil seçer. `Ctrl+F` arama alanına döner. `Esc` önce aramayı temizler, sonra pencereyi gizler.
- `Ctrl+Alt+V` genel kısayolu pencereyi açar veya gizler.
- Bildirim alanı simgesi ve ☰ menüsü: Aç, Profilleri yenile, Çıkış. X düğmesi yalnızca gizler. Windows kapanışı veya oturum kapatma engellenmez.
- `config.json` değişince profil listesi kendiliğinden yenilenir. Dosya paylaşımlı modda okunur.
- Parola, özel anahtar ve sertifika içeriğini kullanmaz; yalnızca OpenVPN Connect'in profil metadatasını kullanır.
- `--background` modunda pencere görünmeden başlar ve OpenVPN Connect penceresini izler. OpenVPN penceresi görünür hale geldiğinde yan panel olarak yapışır. Kullanıcı paneli üst şeritten sürüklerse ayrılır; OpenVPN kenarına bırakılınca yeniden yapışır.
- Kullanıcının gizlediği pencere, OpenVPN açık kaldığı sürece geri açılmaz. Yalnızca OpenVPN yeniden görünür hale geldiğinde otomatik açılır.
- Tek örnek çalışır; masaüstü kısayolu arka plandaki örneği görünür hale getirir.

## Modüller (`modules\`)

- `BackupService.cs`: tam yedek zip (`%APPDATA%\OpenVPN Connect\profiles\<id>.ovpn` dosyalarından), tek profil dışa aktarma, otomatik yedek kararı, eski yedekleri silme.
- `ImportService.cs`: yedek zip açma, `--import-profile=<yol> --name=<ad>` ile içe aktarma, `--remove-profile=<id>` ile silme (bayraklar `resources\app.asar` içinde doğrulandı; yeniden adlandırma bayrağı yok).
- `Duplicates.cs`: yinelenen profil bulucu ve silme penceresi.
- `Connection.cs`: bağlantı durumu (ağ bağdaştırıcısı + son log olayı + `status.connectedProfile.id`), `--disconnect-shortcut`, bağlantı geçmişi (CSV) ve geçmiş penceresi.
- `Reachability.cs`: sunucu erişim testi (TCP bağlantı / UDP için ping), 8 eşzamanlı, 120 sn önbellek.
- `Settings.cs`: ayarlar (`%APPDATA%\OpenVpnQuickSearch\settings.json`), açık/koyu/sistem teması, kısayol ayrıştırma, başlangıç kısayolu, ayarlar penceresi.

## Kurulum paketi

`build.ps1` programı derler ve `outputs\OpenVPN-Hizli-Arama-Kurulum.zip` dosyasını üretir:
`uygulama\` (exe, config, kullanım), `profiller\` (bu bilgisayarın OpenVPN profilleri + FortiClient SSL tünel listesi, **şifresiz**),
`Kur.cmd`/`kur.ps1` (kullanıcı klasörüne kurar, Başlat menüsü, sihirbazı açar), `Kaldir.cmd`/`kaldir.ps1`, `BENIOKU.txt`.
`-NoProfiles` ile profilsiz genel paket üretilir. İlk açılışta (`settings.json` yoksa veya `--setup`) `SetupWizard` çalışır:
VPN uygulamalarını tarar, paketteki eksik profilleri içe aktarır (OpenVPN CLI; FortiClient için yönetici izinli `FortiClient-Ice-Aktar.ps1`), başlangıç ayarlarını yapar.

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

## Testler

- `tests\Tests.cs`: otomatik testler (OpenVPN log algılama, Forti tünel adı eşleştirme, Windows Kimlik Bilgileri deposu). VPN, FortiClient veya ağ gerektirmez. `build.ps1` paketlemeden önce çalıştırır; test başarısızsa paket üretilmez.
- `tests\manual\FortiLive.cs`: açık FortiClient penceresine karşı elle yapılan denemeler. `connect` ve `disconnect` gerçek VPN tüneli açıp kapatır; yalnızca bilerek çalıştırın.

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\manual\run-forti-live.ps1 probe "TÜNEL-ADI"
```

## Derleme

```bash
C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe /nologo /target:winexe /optimize+ /out:OpenVPN-Hizli-Arama.exe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Accessibility.dll /win32icon:app.ico /resource:app.ico,app.ico OpenVpnQuickSearch.cs modules\*.cs
```
