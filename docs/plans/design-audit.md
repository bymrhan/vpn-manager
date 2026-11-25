# Tasarım denetimi (2.1.1 → 2.1.2), feat/design-audit

Kaynak: kullanıcının yeniden gönderdiği tasarım brifi. Denetim kod okumayla ve **pencere açmayan**
çevrimdışı bir çizim düzeneğiyle yapıldı (aşağıda "Görsel doğrulama"). Satır numaraları bu dalın
son hâline göredir.

Özet: 10 brif maddesinde 5 gerçek eksik bulundu; 4'ü düzeltildi, 1'i küçük tutulamayacağı için
önerildi. Ayrıca birkaç küçük tutarsızlık önerilen olarak not edildi.

| # | Brif maddesi | Durum | Kanıt | Düzeltme |
|---|---|---|---|---|
| 1 | Antrasit zemin, safir vurgular, yarı saydam cam, ince kenar yansımaları, optik derinlik | tamam | `modules/Settings.cs:257-265` (grafit/arduvaz palet, safir `#537BA9`/`#8BAED9`), `modules/Glass.cs:302-330` (Mica/akrilik üstünde ton, cam katmanları, kenar yansıması), `Glass.WindowFrame` | yok |
| 2 | Profil kartları kompakt, yuvarlatılmış, okunaklı | tamam | `modules/GlassList.cs:17` (58 px satır, 4 px aralık), `:429-431` (12 px yarıçaplı cam kart) | yok |
| 3 | Firma baş harflerinden düşük doygunluklu avatarlar | tamam | `OpenVpnQuickSearch.cs:365-374` (8 kısık arduvaz ton, ada göre sabit), `modules/GlassList.cs:442-448` | yok |
| 4 | Firma adının altında sırasıyla: sağlayıcı logosu ve adı · sunucu · kullanıcı | **eksikti → düzeltildi** | Önce: logo yalnız avatarın köşesinde 15 px rozet, alt satırda yoktu; OpenVPN satırları sağlayıcı adını hiç göstermiyordu. Şimdi `modules/GlassList.cs:515-517` logo + `VpnProviders.RowSubLine` (`modules/VpnProviders.cs`) | 688bdbd (testli) |
| 5 | FortiClient, OpenVPN, WatchGuard (ve Sophos) için ayırt edilebilir logolar | kısmi | `assets/provider-*.svg`: dört farklı biçim (Forti kalkan+F, OpenVPN halka+çift ok, Sophos yuvarlak kare+S, WatchGuard altıgen+W). Satır boyutunda (13 px) biçimden ayırt ediliyor. Ancak bunlar üreticilerin **resmî** logoları değil, uygulamaya özgü tek renkli simgeler | Resmî logolar ticari marka/lisans konusu; kullanıcı izin/varlık sağlarsa `assets/provider-*.svg` dosyaları değiştirilerek eklenebilir (önerilen, yapılmadı) |
| 6 | Sağda son bağlantı zamanı, sabitleme ve bağlanma düğmeleri | tamam | `modules/GlassList.cs:483` (göreli zaman her zaman), `:469` bağlan düğmesi, yıldız: sabitliyse her zaman, değilse üzerine gelince/seçiliyken görünür (sakin kart için bilinçli) | yok |
| 7 | Seçili profilde safir cam vurgusu | tamam | `modules/GlassList.cs:377-378` (`GlassKind.Selected` + safir kenar, yaylı kayan levha) | yok |
| 8 | Aktif bağlantı Dynamic Island benzeri animasyonlu kapsülde | **kısmi → düzeltildi** | `modules/GlassUi.cs:289-444`: genişlik yayı (morph), içerik geçişi, bağlanıyor/doğrulanıyor/bağlı/kesiliyor/hata durumları, dönen simge, bağlıyken nefes halkası. Hata: bağlanma/hata parıltısı (`pulse`) hiç adımlanmıyordu, söneceği yerde sabit kalıyordu | d85f383 |
| 9 | Menü, kaydırma, filtreleme, silme, sabitleme, bağlantı, hata animasyonları | **kısmi → düzeltildi** | Menü: `modules/MenuKit.cs:276-312,429` (kademeli belirme). Kaydırma: yaylı `scroll` (`GlassList.cs:358`). Filtre: kademeli giriş + FLIP (`:127,163`). Sabitleme: yıldız sıçraması + FLIP (`:93`). Bağlantı: dönen yay, başarı halkası, hata sarsıntısı (`:426`). **Silme: satır anında kayboluyordu** (yalnız alttakiler kayıyordu) | 9712df2: silinen satır yerinde solar ve sola kayar (`GlassList.cs:382`), azaltılmış harekette kapalı |
| 10 | Tüm ekranlarda tutarlı materyal, tipografi, ikonografi, renk | **kısmi → kısmen düzeltildi** | Ana panel, Ayarlar, GlassDialog, menüler, güncelleme/hata bildirimleri, kurulum sihirbazı aynı cam + `SButton` + `Ui`/`SFonts` kullanıyor. Aykırı olanlar: kimlik bilgisi penceresi, bağlantı geçmişi, yinelenen profiller **kare, düz WinForms düğmeleri** ve sabit kodlu renkler (`Duplicates.cs` eski `Danger` sabitleri) kullanıyordu | 6f6670e: üç pencerede `SButton` cam hap düğmeler; `SButton` artık `IButtonControl` (Enter/Esc, DialogResult; testli) |

## Sorular ve cevaplar

- **Dört logo özgün mü, satır boyutunda ayırt edilebilir mi, alt satırda adın önünde mi?** Uygulamaya
  özgü (resmî değil), biçimleri farklı, 13 px'te ayırt ediliyor; artık her satırda
  "[logo] Sağlayıcı · sunucu · kullanıcı" sırasıyla adın önünde (erişilebilirlik simgesi varsa en başta).
- **Satırlar sağda son bağlantı zamanı + sabitleme + bağlan düğmesi gösteriyor mu?** Evet (yıldız ve
  bağlan düğmesi sakin satırlarda üzerine gelince/seçiliyken; sabitli ve bağlı satırlarda her zaman).
- **Seçili satır safir cam mı?** Evet.
- **Kapsül animasyonlu mu?** Evet: görünme/morph/durum geçişleri; hata ve bağlanma parıltısı artık söner.
- **Animasyonlar:** menü açılışı, kaydırma, filtre, sabitleme, bağlanma, hata sarsıntısı vardı;
  silme animasyonu eklendi.

## Önerilen, yapılmadı (küçük tutulamadı veya zevk meselesi)

1. Kimlik bilgisi / geçmiş / yinelenenler pencereleri hâlâ yerel başlık çubuğu (koyu, `FixedDialog`),
   kare `TextBox` (`FixedSingle`) ve yerel `CheckBox` kullanıyor; GlassDialog gibi çerçevesiz cam pencereye
   taşımak ve `SearchBox` benzeri alan/`SToggle` kullanmak orta boy bir iş (`FortiCredentials.cs:123-200`).
2. Hata raporu önizlemesi (`IssueUi.cs:30-33`) Consolas + yerel kaydırma çubuklu `TextBox`; tanılama
   metni için kabul edilebilir.
3. `OpenVpnQuickSearch.cs:2796` başlangıç çökmesinde `MessageBox`: arayüz kurulamamış olabileceği için
   bilinçli bırakıldı.
4. `OpenVpnQuickSearch.cs:511` `HeaderTitleFont` ("Segoe UI Semibold" sabit) kullanılmıyor; birleştirme
   gürültüsü olmasın diye silinmedi.
5. Alt satıra logo gelince avatar köşesindeki sağlayıcı rozeti tekrara düştü; kaldırılabilir (zevk).
6. Resmî sağlayıcı logoları (madde 5).

## Görsel doğrulama

Düzenek (depoya eklenmedi): `%TEMP%\claude\…\scratchpad\harness\Harness.cs` + `render.ps1`; kaynakları
SVG'ler gömülü olarak derler, `Demo.On` kurgusal profilleriyle `ProfileList`, `ConnectionIsland` ve
`FortiCredentialForm`'u **göstermeden** çizer (denetimler için `OnPaint` doğrudan ekran dışı bitmap'e,
pencere için alt denetimler tek tek `DrawToBitmap`). PNG'ler: `scratchpad\audit\` (`*-before`, `*-f1..f4`,
koyu ve açık tema). Hiçbir pencere gösterilmedi, uygulama çalıştırılmadı.

Düzeneğin gösteremedikleri: DWM akrilik/Mica bulanıklığı (opak yedek zemin çizildi), gerçek hareket
(yalnız tek kareler: silme ortası, hata başı/sonu), üzerine gelme durumları, yerel `TextBox` içerikleri,
açık temada ARGB bitmap'e `TextRenderer` metni (kalın/bulanık görünüyor, düzenek kusuru), menüler,
Ayarlar ve sihirbaz (yalnız kodla denetlendi).

Testler: `tests\run-tests.ps1` → 471 C# + 28 kurulum testi geçti (yeni: alt satır 3, diyalog düğmesi 4).
