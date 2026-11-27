# Taşınan OpenVPN parolalarını eşleştirme — tasarım

Tarih: 2026-11-27. Onay: kullanıcı, sohbet içinde ("Onaylıyorum").

## Sorun

Kullanıcı Windows Kimlik Bilgileri'ni ikinci cihaza taşıdı ve profilleri yedekten geri yükledi.
OpenVPN Connect yine parola soruyor.

Ölçüm (ikinci cihaz, yalnız sayılar): 62 profil, 140 `org.openvpn.client./<kimlik>` kaydı, profille
eşleşen kayıt 0, `savedPassword=true` 1.

Neden: OpenVPN Connect parolayı `org.openvpn.client./<profil kimliği>` hedefiyle tutar; kaydın
kullanıcı alanı da profil kimliğidir (ilk cihazda 140/140). İçe aktarılan profil yeni kimlik alır,
taşınan kayıtlar eski kimliğin adıyla kalır. `config.json` içindeki `savedPassword` bayrağı da kapalıdır.

## Amaç

İkinci cihazda, zaten Windows'ta duran eski kayıtları yeni profillere bağlamak. Parola hiçbir zaman
gösterilmez, dosyaya, günlüğe ya da panoya yazılmaz; baytları çözülmeden kopyalanır.

Amaç dışı: parolaları dosyaya dışa aktarmak; FortiClient / WatchGuard / Sophos (uygulamanın o kayıtları
tünel adına bağlıdır, ad aynıysa zaten çalışır); eski kayıtları silmek.

## Yaklaşımlar

1. **Eşleştirme (seçilen).** Yedek zip'indeki `manifest.json` eski kimlik, ad ve dosya adını tutar.
   Yeni profil ada göre bulunur; kayıt yeni kimliğe kopyalanır, bayrak açılır. Parola diske çıkmaz.
2. Şifreli parola dışa aktarma dosyası. Parolalar diske çıkar; parolalar zaten ikinci cihazda olduğu
   için gereksiz risk.
3. Elle: her parolayı yeniden girmek.

## Davranış

1. Kullanıcı yedek zip'ini seçer (Ayarlar › Yedekleme › "Parolaları eşleştir…"). Geri yükleme
   bittiğinde, eşleşen kayıt varsa aynı işlem önerilir.
2. Eşleşme: manifest girdisinin dosya adı (uzantısız; içe aktarma profili bu adla kaydeder) ya da adı,
   Türkçe büyük/küçük harf duyarsız olarak tam bir profille eşleşmeli ve o profile yalnız bir girdi
   düşmeli. Aksi belirsizdir, atlanır.
3. Atlananlar: belirsiz ad; karşılığı olmayan girdi; eski kaydı olmayan girdi; yeni kimliğinde zaten
   kayıt olan profil (üzerine yazılmaz); kimliği değişmemiş profil; kimliği dosya adından tahmin
   edilen ya da elle eklenen profil.
4. Onay penceresi sayıları gösterir ve üç seçenek sunar: yalnız bir profilde dene, hepsi, vazgeç.
5. Uygulama: kayıtlar kopyalanır (`org.openvpn.client./<yeni>`, kullanıcı alanı = yeni kimlik, bayt
   dizisi aynen); OpenVPN Connect kapatılır; `config.json` içinde o profillerin `savedPassword` alanı
   `true` yapılır (ad değiştirmedeki yazma yolu: geçici dosya, geri okuma, `config.json.vpnm-yedek`);
   OpenVPN Connect yeniden açılır.
6. `config.json` yazılamazsa bu çalıştırmada oluşturulan kayıtlar silinir.
7. Tünel açıkken ya da bağlantı kurulurken işlem yapılmaz; kullanıcıya bağlantıyı kesmesi söylenir.

## Bilinen risk

OpenVPN Connect'in elle açılan bayrağı ve kopyalanan kaydı kabul ettiği denenmedi; ilk gerçek deneme
ikinci cihazdadır. "Yalnız bir profilde dene" seçeneği bunun içindir.

## Uygulayıcıya bırakılanlar

Pencere metinleri, satırın Ayarlar sayfasındaki yeri, günlük satırları (yalnız sayılar).
