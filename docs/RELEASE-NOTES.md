# Sürüm notları

GitHub sürüm notlarına bu listeden kopyalanır (bkz. `docs/RELEASING.md`). Uygulamanın güncelleme
bildirimi notların ilk satırlarını gösterir; en önemli maddeler başta durur.

## 2.1.3

- **Brave profilleri (Microsoft 365):** Ayarlar > Genel > *Brave profilleri (Microsoft 365)* açılınca Brave'in profilleri taranır ve yeni *Brave 365* sekmesinde (Ctrl+6) listelenir; arama, sabitleme ve son açılan sırası diğer profillerle aynıdır. Satıra tıklamak / Enter o Brave profilini Microsoft 365 sayfasında açar. Tümü sekmesinde yalnızca arama sonucu olarak görünür. Varsayılan kapalıdır: kapalıyken Brave taranmaz, sekme ve ayar yalnızca Brave kuruluysa görünür. Brave'in dosyalarına yazılmaz.
- **Sağlayıcı logoları:** OpenVPN, FortiClient, Sophos Connect ve WatchGuard satırlarında artık sağlayıcıların kendi logoları (20 px, keskin) görünür; avatar köşesindeki rozet sunucu erişilebilirliğini gösterir.
- **Kayıtlı hesap:** FortiClient, Sophos ve WatchGuard satırlarının sağ tık menüsünde *Farklı kullanıcıyla bağlan…* eklendi. *Kayıtlı parolayı değiştir…* penceresinde parolayı boş bırakırsanız kayıtlı parola korunur; yalnızca kullanıcı adını değiştirebilirsiniz.
- **Daha geniş pencere:** varsayılan genişlik büyütüldü; sunucu ve kullanıcı adı satırda kesilmez.

## 2.1.2

- **Kurulum yardımcısı:** OpenVPN Connect ve FortiClient'ın yanında Sophos Connect ile WatchGuard SSL VPN de taranır ve listelenir (bağlantı / sunucu sayısıyla); FortiClient, Sophos ve WatchGuard profillerini gösterme anahtarları eklendi (kurulu olmayan istemci için soluk). Tema seçimi sihirbazda anında uygulanır ve ana pencereye taşınır.
- **Sık kullanılanlar:** listede *Son kullanılanlar*'ın altında yeni *Sık kullanılanlar* grubu: son 60 günde en az 3 kez bağlanılan profiller (en çok kullanılan önce, en fazla 5; 3 saniyeden kısa oturumlar ve yinelenen geçmiş kayıtları sayılmaz). Bağlı, sabitlenen veya son kullanılan olarak zaten görünen profil tekrar listelenmez, boş kalırsa grup gizlenir; sağlayıcı filtresine uyar. Ayarlar > Genel > *Sık kullanılanları göster* ile kapatılabilir.
- **Profiller okunamazsa:** OpenVPN Connect'in `config.json` dosyası esnek okunur (farklı iç düzenler, başka konumlar); okunamazsa profiller OpenVPN Connect'in `profiles` klasöründeki `.ovpn` dosyalarından listelenir ve kaynak 30 sn'de bir yeniden denenir.
- Boş liste nedenini söyler (dosya yok / okunamadı / düzen tanınmıyor / 0 profil) ve *Profil klasörünü seç…* ile *Dosya seç…* düğmelerini gösterir; aynı işlemler Ayarlar > Genel > *Profil kaynağı* altında da var.
- *Tanı bilgisini kopyala*: sürümler, denenen konumlar ve `config.json`'ın yalnızca anahtar adlarıyla türleri (değer, ad ve sunucu içermez); aynı özet bir kez uygulama günlüğüne yazılır.
- **Derin Mavi teması:** Ayarlar > Görünüm'de ve ilk kurulum sihirbazında dördüncü tema: lacivert / mavi-arduvaz cam, yumuşak mavi kenarlar ve yeşil bağlantı vurgusu. Koyu bir temadır; yerleşim değişmez, Koyu ve Açık temaların görünümü aynı kalır.
- **Görünüm iyileştirmeleri:** her profil satırının alt satırı artık sağlayıcı logosu ve adıyla başlar (*[logo] Sağlayıcı · sunucu · kullanıcı*, OpenVPN satırlarında da); silinen profil yerinde solarak çıkar; bağlantı kapsülündeki bağlanma/hata parıltısı söner; kimlik bilgisi, bağlantı geçmişi ve yinelenen profiller pencerelerinde düğmeler Ayarlar'daki cam düğmelerle aynıdır.

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
