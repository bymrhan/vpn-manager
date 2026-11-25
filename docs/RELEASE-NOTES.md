# Sürüm notları

GitHub sürüm notlarına bu listeden kopyalanır (bkz. `docs/RELEASING.md`). Uygulamanın güncelleme
bildirimi notların ilk satırlarını gösterir; en önemli maddeler başta durur.

## 2.1.2

- **Derin Mavi teması:** Ayarlar > Görünüm'de ve ilk kurulum sihirbazında dördüncü tema: lacivert / mavi-arduvaz cam, yumuşak mavi kenarlar ve yeşil bağlantı vurgusu. Koyu bir temadır; yerleşim değişmez, Koyu ve Açık temaların görünümü aynı kalır.

## 2.1.1

- **Uygulama nerede görünsün:** Ayarlar > Genel'de üç seçenek: *Tepsi* (varsayılan), *Tepsi + düğme* (pencere açıkken görev çubuğunda da düğme), *Görev çubuğu* (tepsi simgesi yok; düğme uygulama açık olduğu sürece durur, gizlemek pencereyi düğmeye küçültür, pencereyi kapatmak uygulamadan çıkar). Kaydedince yeniden başlatmadan uygulanır.
- 2.1.0'daki *Görev çubuğunda göster* ayarı taşınır: açıksa *Tepsi + düğme*, kapalıysa *Tepsi*.
- *Görev çubuğu* seçeneğinde pencere küçültülmüşken açılan sorular (ör. önceki çökmenin raporu) pencere geri gelince gösterilir; küçültülmüş pencereye bağlı görünmez iletişim kutusu kalmaz.

## 2.1.0

- **2.0.0'dan güncelleme: kurulum komutunu bir kez yeniden çalıştırın** (`irm https://raw.githubusercontent.com/bymrhan/vpn-manager/main/install.ps1 | iex`). Mevcut kurulum sessizce güncellenir; ayarlar, sabitlenenler ve geçmiş korunur, sihirbaz açılmaz. Sonraki güncellemeler güncelleme ayarlarına göre otomatik gelir.
- **Sophos Connect desteği:** `sccli` üzerinden profilleri listeleme, bağlanma / bağlantıyı kesme ve durum.
- **WatchGuard Mobile VPN with SSL desteği:** kayıtlı sunucular listede; bağlanma istemcinin kendi penceresi üzerinden yapılır.
- **Sağlayıcı ayarları:** FortiClient, Sophos ve WatchGuard için ayrı anahtarlar (Sophos / WatchGuard yalnızca istemci algılanınca); kurulu ve açık istemcinin sekmesi bağlantı yokken de görünür.
- **Arka planda bağlanma:** VPN istemcilerinin pencereleri öne gelmez, yalnızca sizin işlem yapmanız gerekiyorsa görünür; uygulama kapanırken ekran dışında bekleyen pencere bırakılmaz. Kullanıcı beklenirse küçültülen OpenVPN Connect penceresi geri getirilir.
- **FortiClient düzeltmeleri:** kullanıcı adı otomatik doldurmanın geri okunarak doğrulanması, arka planda açılan FortiClient penceresinin kapatılması, bağlanma adımının ayrıntılı durumu.
- **Otomatik güncelleme:** GitHub sürümlerinden denetim (*Sor* / *Otomatik* / *Kapalı*, denetleme sıklığı), SHA-256 doğrulamalı indirme, `kur.ps1 -Update` ile sessiz kurulum ve hata olursa geri alma.
- **Tanılama:** uygulama günlüğü ve çökme kaydı (Ayarlar > Hakkında > Tanılama ile kapatılabilir); çökmeden sonra temizlenmiş hata raporu yalnızca onayınızla gönderilir.
- **Hata raporu gönderme:** rapor tarayıcıda hazır bir GitHub kaydı olarak açılır; anahtar gömülü sürümlerde uygulama kaydı onayınızdan sonra kendisi oluşturur (otomatik kip bir kez onaylanan uyarı ister, kurulum başına günde en fazla 3).
- **Ayarlar penceresi:** yerel kaydırma çubuğu yerine pencere kenarını bozmayan ince kaydırma çubuğu; açıklama satırları metne göre boyutlanır.
- **Görev çubuğunda göster:** Ayarlar > Genel'deki yeni anahtar (varsayılan kapalı). Açıkken uygulama penceresi görünür olduğu sürece görev çubuğunda ve Alt+Tab'da yer alır; ayar kaydedilince yeniden başlatmadan uygulanır.
