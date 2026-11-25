# install.ps1: sessiz yerinde guncelleme - rapor

Branch: `feat/install-update`

## Ne degisti
- `install.ps1`: SHA-256 dogrulamasi ve paket acildiktan sonra karar verilir. Kurulum klasorunde
  `OpenVPN-Hizli-Arama.exe` varsa VE paketin `kur.ps1`'i `-Update`, `-InstallDir`, `-Background`
  parametrelerini tanimliyorsa (dosya PowerShell AST ile yalnizca ayristirilir, calistirilmaz):
  1. Bu kurulumun exe'sinin tam yoluyla eslesen surecler bulunur (`Get-CimInstance Win32_Process`,
     hata olursa `Get-Process .Path`); yalnizca ad ile esleme yapilmaz.
  2. `CloseMainWindow` istenir, 3 sn beklenir, hala calisan surec (yol tekrar dogrulanarak, PID
     yeniden kullanimina karsi) zorla durdurulur. Uygulamanin kendi cikisi yalnizca tepsi menusundeki
     "Cikis"tir; WM_CLOSE pencereyi tepsiye gizler. Ayarlar her degisiklikte kaydedildigi icin zorla
     durdurma veri kaybettirmez.
  3. `powershell.exe -NoProfile -ExecutionPolicy Bypass -File kur.ps1 -Update -Background -InstallDir <dir> [-Version <etiket, v'siz>]`
     (`-Version` yalnizca `$env:VPNM_VERSION` verildiyse ve kur.ps1 kabul ediyorsa).
  4. Sonuc Turkce (ASCII) bildirilir: 0 = guncellendi; 1 = eski surum geri yuklendi ve yeniden
     baslatildi (guncelleme.log); diger kodlar (2/3: dosyaya dokunulmadan cikis) icin uygulama
     calisiyorduysa eski surum `--background` ile yeniden baslatilir.
  Aksi halde (ilk kurulum, exe yok, ya da `-Update` bilmeyen eski paket) etkilesimli kur.ps1 cagrisi
  aynen korunur. TLS 1.2, SHA-256 zorunlulugu, `VPNM_VERSION`, gecici klasor temizligi, hata yakalama
  ve Windows disi koruma degismedi. Dosya ASCII olarak kaldi. Test kancasi (env degiskeni) eklenmedi;
  karar mantigi dosyanin basindaki fonksiyonlara alindi.
- `paket\kur.ps1`: yalnizca yorumlar. `-Update` dali uygulamaya bagimli degil: `-WaitPid` 0 iken
  beklemez, `.bekliyor` gecici paket klasorune yazilir (zararsiz), exe yoksa `exit 2`, yedekle/kopyala,
  `finally` icinde `--background` ile yeniden baslatir, hatada `guncelleme-basarisiz.txt` yazar.
  Etkilesimli yol degismedi.
- `tests\install-update.tests.ps1` (28 kontrol): fonksiyonlari install.ps1'den AST ile ayiklar; ASCII,
  SHA-256 -> Expand -> karar sirasi, mod karari (yeni/eski/kismi paket, exe yok, klasor yok), arguman
  listesi, sahte kur.ps1'i bosluk ve `[ ]` iceren yollarla calistirip argumanlari ve cikis kodunu
  dogrular, surec secimi (yalnizca tam yol). Tumuyle %TEMP% altinda, ag yok, gercek kurulum/uygulama
  yok. `tests\run-tests.ps1` ayni surecte calistirir (~1 sn ek).
- README (Kurulum bolumu: "Eski surumden guncelleme" paragrafi) ve docs\RELEASE-NOTES.md 2.1.0 maddesi.

## Dogrulama
`tests\run-tests.ps1`: 431 gecti, 0 basarisiz; install.ps1 testleri 28 gecti.
Gercek install.ps1 / kur.ps1 gercek yollara karsi calistirilmadi (istendigi gibi).

## Notlar / riskler
- Sessiz guncelleme ancak `-Update` destekli paket (2.1.0) "latest" surum olunca devreye girer;
  o zamana kadar (2.0.0 latest iken) komut bugunku gibi sihirbazli kurulum yapar.
- install.ps1 main'e birlestirildigi anda tek satirlik komut icin gecerli olur.
- Guncelleme sonrasi uygulama, onceden calismiyor olsa bile `--background` ile baslatilir (kur.ps1 davranisi).
- Uygulama baska bir kullanici/yukseltilmis surecte calisiyorsa yolu okunamaz ve durdurulmaz; kur.ps1
  kopyalama hatasinda geri alir ve 1 doner.
