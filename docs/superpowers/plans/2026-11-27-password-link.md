# Taşınan OpenVPN Parolalarını Eşleştirme — Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** İkinci cihazda, Windows'a taşınmış eski `org.openvpn.client./<eski kimlik>` kayıtlarını yeni profil kimliklerine bağlamak.

**Architecture:** Saf eşleştirme mantığı (`PasswordLink.Plan`) yedeğin `manifest.json` dosyasından ve profil listesinden bir plan üretir. `OpenVpnCredentials.Copy` kaydı çözmeden kopyalar. `savedPassword` bayrağı, ad değiştirmenin `config.json` yazma yolundan (`OpenVpnRename.ApplyToFile` / `ApplyWithRestart`) geçer.

**Tech Stack:** C# 5, .NET Framework 4.8, `csc`; testler `tests\Tests.cs` (`Run` + `Check`), komut `powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1`.

**Spec:** `docs/superpowers/specs/2026-11-27-password-link-design.md`

## Global Constraints

- Parola metni hiçbir yerde çözülmez, gösterilmez, günlüğe ya da dosyaya yazılmaz; `AppLog` yalnız sayı alır.
- Profil, sunucu, kullanıcı adı `AppLog`'a gitmez.
- Var olan kayıt üzerine yazılmaz; eski kayıt silinmez.
- OpenVPN Connect yalnız tünel yokken kapatılır (`OpenVpnBusy()`).
- C# 5 sözdizimi.

## Review Focus

- Aynı ada sahip iki profil ya da aynı profile düşen iki girdi: hiçbiri bağlanmaz (Task 1 testi).
- `config.json` yazılamaz: bu çalıştırmada kopyalanan kayıtlar silinir (Task 3 testi).
- Yeni kimlikte kayıt zaten var: dokunulmaz (Task 1, Task 2 testleri).
- Manifesti olmayan ya da bozuk zip: boş plan, hata yok (Task 1 testi).
- Aynı cihazda çalıştırma (kimlikler aynı): hiçbir şey yapılmaz (Task 1 testi).

---

### Task 1: Manifest okuma ve eşleştirme planı

**Files:** Create `modules\PasswordLink.cs`; Test `tests\Tests.cs` (`PasswordLinkPlanning`).

**Interfaces — Produces:**
- `sealed class BackupEntry { public string Id, Name, File; }`
- `sealed class PasswordLinkPlan { List<KeyValuePair<string,string>> Pairs; int Ambiguous, NoProfile, NoOldPassword, AlreadySaved, SameId; int Skipped { get; } }` (Pairs: eski kimlik → yeni kimlik)
- `PasswordLink.ReadManifest(string zipPath) : List<BackupEntry>` (hata: boş liste)
- `PasswordLink.Plan(IList<BackupEntry> manifest, IList<KeyValuePair<string,string>> profiles, Func<string,bool> hasPassword) : PasswordLinkPlan` (profiles: kimlik, ad)
- `PasswordLink.Summary(PasswordLinkPlan) : string`

- [ ] Test yaz, kırmızı gör, kodu yaz, yeşil gör.

### Task 2: Kayıt kopyalama

**Files:** Modify `modules\PasswordLink.cs` (`OpenVpnCredentials`); Test `PasswordLinkCredentials`.

**Interfaces — Produces:** `OpenVpnCredentials.Prefix = "org.openvpn.client./"`, `Has(id, prefix = null)`, `Copy(oldId, newId, prefix = null) : bool` (eski yoksa, yeni varsa ya da yazılamazsa `false`; kullanıcı alanı = `newId`; bayt dizisi aynen), `Delete(id, prefix = null)`. Testler `OpenVpnQuickSearch:test-link:` önekiyle çalışır.

### Task 3: Bayrak yazma ve uygulama sırası

**Files:** Modify `modules\Rename.cs` (`OpenVpnRename`), `modules\PasswordLink.cs`; Test `PasswordLinkApply`.

**Interfaces — Produces:**
- `OpenVpnRename.MarkSaved(string json, ICollection<string> ids, out string error) : string`, `OpenVpnRename.SavedOf(string json, string id) : bool`
- `OpenVpnRename.ApplyToFile(string configPath, IDictionary<string,string> renames, ICollection<string> savedIds)` ve `ApplyWithRestart(renames, savedIds)`; eski iki parametreli imzalar kalır.
- `PasswordLink.Apply(IList<KeyValuePair<string,string>> pairs, Func<string,string,bool> copy, Action<string> delete, Func<ICollection<string>,string> writeFlags, out int linked) : string` (hata metni ya da `null`; `writeFlags` hata dönerse kopyalananlar `delete` ile silinir).

### Task 4: Arayüz

**Files:** Modify `modules\SettingsWindow.cs` (`public Action LinkPasswords`, Yedekleme sayfasında satır), `OpenVpnQuickSearch.cs` (`PickAndLinkPasswords`, `LinkPasswords(zip)`, geri yükleme sonunda öneri).

Onay: `GlassDialog` `YesNoCancel` — Evet: yalnız bir profil (listede seçili profil planda ise o, değilse ilk çift); Hayır: hepsi; İptal.

### Task 5: Belgeler ve tam doğrulama

`docs/RELEASE-NOTES.md` ("Sonraki sürüm"), `paket\OpenVPN-Hizli-Arama-Kullanim.txt`; tüm testler; `build.ps1 -NoProfiles`.
