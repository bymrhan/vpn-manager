# Profile Rename Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename VPN profiles from the panel; the name is stored by the panel for WatchGuard and written into the client's own store for OpenVPN Connect, Sophos Connect and FortiClient SSL.

**Architecture:** One new module `modules\Rename.cs` holds the pure parts (name rules, panel-side stores, the OpenVPN `config.json` rewrite, the FortiClient key copy, id migration). `SearchForm` gets the dialog, the menu item / `F2`, and one `RenameProfile(VpnProfile)` dispatcher per provider. Names keep being read from the clients; the only overlay is `NameStore.Apply` after profiles are loaded.

**Tech Stack:** C# 5 (csc from .NET Framework 4.8, no project file), WinForms, `JavaScriptSerializer`, `Microsoft.Win32.Registry`. Tests: `tests\Tests.cs` (`Run("title", Method)` + `Check(bool, "text")`), run with `powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1`.

**Spec:** `docs/superpowers/specs/2026-11-27-profile-rename-design.md`

## Global Constraints

- C# 5 syntax only (no `?.`, no `$""`, no expression-bodied members, no `out var`): the build uses `Framework64\v4.0.30319\csc.exe`.
- New code goes in `modules\Rename.cs` and `modules\RenameDialog.cs`; `build.ps1` and `tests\run-tests.ps1` pick up `modules\*.cs` by themselves.
- UI text is Turkish, code comments and log lines English, as in the rest of the code.
- Profile names, server names and user names never go to `AppLog` (log provider, outcome, failing step only).
- Name rule values: trimmed; 1-64 characters; no control characters; FortiClient also forbids `\ / : * ? " < > |`; no other profile of the same provider with the same name (case-insensitive, `tr-TR`).
- Panel files live in `%APPDATA%\OpenVpnQuickSearch`: `names.json`, `pending-renames.json`. OpenVPN backup file: `config.json.vpnm-yedek` next to `config.json`.
- Rename is not offered for Brave rows, `forti-ipsec:` rows, or when `Demo.On`.
- No VPN connection may be dropped by a rename: OpenVPN writes wait for "no tunnel, nothing in flight"; a connected / connecting FortiClient tunnel is refused.
- Commits: the user has asked to approve commits. Run a task's commit step only after that approval; the working copy also holds earlier uncommitted fixes (`Glass.cs`, `GlassList.cs`, `Connection.cs`, `QuietWindow.cs`, `OpenVpnQuickSearch.cs`, `tests\Tests.cs`) that must be committed separately first.
- Touching the real clients (closing OpenVPN Connect, `sccli update`, registry writes, UAC) happens only in the manual verification steps, with the user's go-ahead.

## Review Focus

1. `names.json` / `pending-renames.json` is empty, truncated or not JSON: the list still loads, with client names (Task 2).
2. OpenVPN `config.json` changes shape (no `status.profiles`, `persist:root` not a string): `Rewrite` returns an error and nothing is written (Task 5).
3. The new name contains `"`, `\`, a non-ASCII Turkish letter or an emoji: the double-encoded JSON stays valid and round-trips (Task 5).
4. FortiClient target name already exists in one hive but not the other: nothing is changed in either hive (Task 7).
5. A pending OpenVPN rename whose profile was deleted, or whose name was meanwhile changed inside OpenVPN Connect: the entry is dropped, not applied (Task 6).

---

### Task 1: Name rules

**Files:**
- Create: `modules\Rename.cs`
- Test: `tests\Tests.cs`

**Interfaces:**
- Produces: `internal static class NameRules { public const int MaxLength = 64; public static string Check(string name, VpnKind kind, IEnumerable<string> otherNames, bool allowEmpty); public static string Clean(string name); }`. `Check` returns `null` when valid, else the Turkish reason. `Clean` trims.

- [ ] **Step 1: Write the failing test** `RenameNameRules` in `tests\Tests.cs`, registered with `Run("Ad kuralları", RenameNameRules);`

```csharp
var others = new[] { "Acme", "IŞIK Tekstil" };
Check(NameRules.Check("Yeni Ad", VpnKind.OpenVpn, others, false) == null, "geçerli ad");
Check(NameRules.Check("   ", VpnKind.OpenVpn, others, false) != null, "boş ad reddedilir");
Check(NameRules.Check("", VpnKind.WatchGuard, others, true) == null, "WatchGuard: boş ad = adı kaldır");
Check(NameRules.Check(new string('a', 64), VpnKind.OpenVpn, others, false) == null, "64 karakter geçerli");
Check(NameRules.Check(new string('a', 65), VpnKind.OpenVpn, others, false) != null, "65 karakter reddedilir");
Check(NameRules.Check("a\tb", VpnKind.OpenVpn, others, false) != null, "denetim karakteri reddedilir");
Check(NameRules.Check("acme", VpnKind.OpenVpn, others, false) != null, "aynı ad (büyük/küçük harf) reddedilir");
Check(NameRules.Check("ışık tekstil", VpnKind.OpenVpn, others, false) != null, "Türkçe harf eşlemesi: ışık = IŞIK");
Check(NameRules.Check("a/b", VpnKind.Forti, others, false) != null, "FortiClient: / yasak");
Check(NameRules.Check("a/b", VpnKind.OpenVpn, others, false) == null, "diğerlerinde / serbest");
Check(NameRules.Clean("  Ad  ") == "Ad", "kırpılır");
```

- [ ] **Step 2: Run** `tests\run-tests.ps1`. Expected: compile error, `NameRules` does not exist.
- [ ] **Step 3: Implement `NameRules`** in `modules\Rename.cs`. Compare with `CultureInfo.GetCultureInfo("tr-TR").CompareInfo.Compare(a, b, CompareOptions.IgnoreCase)`. Reasons: "Ad boş olamaz.", "Ad en çok 64 karakter olabilir.", "Ad geçersiz karakter içeriyor.", "FortiClient adında \\ / : * ? \" < > | kullanılamaz.", "Bu adda başka bir profil var.". Check `VpnKind` member names in `modules\VpnProviders.cs` and use them as they are.
- [ ] **Step 4: Run** tests. Expected: all PASS, 0 failed.
- [ ] **Step 5: Commit** `feat: name rules for profile rename`.

### Task 2: Panel-side name stores

**Files:**
- Modify: `modules\Rename.cs`
- Test: `tests\Tests.cs`

**Interfaces:**
- Produces:
  - `internal static class NameStore { internal static string FolderOverride; public static Dictionary<string,string> Load(); public static void Set(string profileId, string name); /* null or "" removes */ public static void Apply(IEnumerable<VpnProfile> profiles); }` — file `names.json`.
  - `internal static class PendingRenames { public static Dictionary<string,string> Load(); public static void Set(string profileId, string name); public static void Remove(string profileId); public static bool IsPending(string profileId); }` — file `pending-renames.json`, same folder, same override.
  - `Apply` sets `Name` for every profile with an entry in `names.json` **or** `pending-renames.json` (pending wins), then calls `ProfileStore.Finish(p)` (it recomputes initials and search keys; see `ProfileSources.FromItem`).

- [ ] **Step 1: Write the failing test** `RenameNameStore`, `Run("Ad deposu", RenameNameStore);`. Use a temp folder through `NameStore.FolderOverride`, reset it in `finally`.

```csharp
Check(NameStore.Load().Count == 0, "dosya yokken boş");
NameStore.Set("watchguard-ssl:1.2.3.4:443", "Acme Merkez");
Check(NameStore.Load()["watchguard-ssl:1.2.3.4:443"] == "Acme Merkez", "kaydedilir ve okunur");
var p = new VpnProfile { Id = "watchguard-ssl:1.2.3.4:443", Name = "WatchGuard SSL VPN · 1.2.3.4", Host = "1.2.3.4:443", Username = "", Source = "" };
var q = new VpnProfile { Id = "watchguard-ssl:5.6.7.8:443", Name = "WatchGuard SSL VPN · 5.6.7.8", Host = "5.6.7.8:443", Username = "", Source = "" };
NameStore.Apply(new[] { p, q });
Check(p.Name == "Acme Merkez" && p.Initials == ProfileStore.MakeInitials("Acme Merkez"), "ad ve baş harfler uygulanır");
Check(q.Name == "WatchGuard SSL VPN · 5.6.7.8", "kaydı olmayan profil değişmez");
Check(p.Keys.Any(k => k.Contains(ProfileStore.SearchKey("Acme"))), "yeni adla aranabilir");
NameStore.Set("watchguard-ssl:1.2.3.4:443", "");
Check(!NameStore.Load().ContainsKey("watchguard-ssl:1.2.3.4:443"), "boş ad kaydı siler");
File.WriteAllText(Path.Combine(dir, "names.json"), "{ bozuk");
Check(NameStore.Load().Count == 0, "bozuk dosya: boş sayılır, hata vermez");
File.WriteAllText(Path.Combine(dir, "names.json"), "");
Check(NameStore.Load().Count == 0, "boş dosya: boş sayılır");
PendingRenames.Set("123", "Bekleyen");
Check(PendingRenames.IsPending("123") && PendingRenames.Load()["123"] == "Bekleyen", "bekleyen ad kaydedilir");
var o = new VpnProfile { Id = "123", Name = "Eski", Host = "", Username = "", Source = "" };
NameStore.Apply(new[] { o });
Check(o.Name == "Bekleyen", "bekleyen ad listede hemen görünür");
PendingRenames.Remove("123");
Check(!PendingRenames.IsPending("123"), "bekleyen ad silinir");
```

- [ ] **Step 2: Run** tests. Expected: compile error.
- [ ] **Step 3: Implement** both stores. Write through `<file>.tmp` then `File.Replace` / `File.Move`; read errors return an empty dictionary. `JavaScriptSerializer` for the JSON. If `ProfileStore.Finish` or `SearchKey` has a different accessibility or shape than the test assumes, adapt the test's last two `Check` lines to the real API, not the API to the test.
- [ ] **Step 4: Call `NameStore.Apply(profiles)`** in `SearchForm.ReloadProfiles` (`OpenVpnQuickSearch.cs`, after the `VpnProviders.ApplyLastConnected(extra, starts)` call near line 2568, on the merged list before it is filtered).
- [ ] **Step 5: Run** tests. Expected: all PASS.
- [ ] **Step 6: Commit** `feat: panel-side profile names (names.json, pending renames)`.

### Task 3: Dialog, menu item, F2, WatchGuard rename

**Files:**
- Create: `modules\RenameDialog.cs`
- Modify: `OpenVpnQuickSearch.cs` (`ShowRowMenu` ~line 978, `OnFormKeyDown` ~line 2743, new `RenameProfile`)
- Modify: `modules\Rename.cs`
- Test: `tests\Tests.cs`

**Interfaces:**
- Consumes: `NameRules.Check`, `NameRules.Clean`, `NameStore.Set`.
- Produces:
  - `internal static class RenameText { public static string Hint(VpnKind kind, bool openVpnBusy); public static bool Offered(VpnProfile p, bool demo); }`
  - `internal sealed class RenameDialog : Form { public RenameDialog(string currentName, string hint, Func<string,string> validate); public string Result; }` — `ShowDialog(owner) == DialogResult.OK` when saved; `Result` is the cleaned name.
  - `void SearchForm.RenameProfile(VpnProfile p)` — dispatcher; later tasks add their provider branch here.

- [ ] **Step 1: Write the failing test** `RenameTexts`, `Run("Ad değiştirme metinleri", RenameTexts);`

```csharp
Check(RenameText.Hint(VpnKind.WatchGuard, false) == "Ad yalnız VPN Manager'da saklanır. Boş bırakırsanız otomatik ad geri gelir.", "WatchGuard açıklaması");
Check(RenameText.Hint(VpnKind.OpenVpn, false) == "OpenVPN Connect kapatılıp yeniden açılacak.", "OpenVPN, boşta");
Check(RenameText.Hint(VpnKind.OpenVpn, true) == "OpenVPN bağlantısı kesilince uygulanacak.", "OpenVPN, bağlıyken");
Check(RenameText.Hint(VpnKind.Forti, false) == "Windows yönetici onayı isteyecek.", "FortiClient açıklaması");
Check(RenameText.Hint(VpnKind.Sophos, false) == "Sophos Connect'te görünen ad değişir.", "Sophos açıklaması");
Check(RenameText.Offered(new VpnProfile { Id = "watchguard-ssl:1.2.3.4:443" }, false), "WatchGuard: sunulur");
Check(!RenameText.Offered(new VpnProfile { Id = "forti-ipsec:x" }, false), "Forti IPsec: sunulmaz");
Check(!RenameText.Offered(new VpnProfile { Id = BraveStore.IdPrefix + "Profile 1" }, false), "Brave: sunulmaz");
Check(!RenameText.Offered(new VpnProfile { Id = "123" }, true), "demo: sunulmaz");
```

- [ ] **Step 2: Run** tests. Expected: compile error.
- [ ] **Step 3: Implement `RenameText`** in `modules\Rename.cs`.
- [ ] **Step 4: Implement `RenameDialog`** in `modules\RenameDialog.cs`: copy the window setup, glass painting and `SButton` usage of the credential dialog in `modules\FortiCredentials.cs` (class starting near line 120: one `TextBox`, labels, primary / secondary buttons). Title "Yeniden adlandır", text box pre-filled and fully selected, `MaxLength = 64`, muted hint line, a reason line in `Ui.Danger` fed by `validate(text)` on every `TextChanged`, "Kaydet" disabled while the reason is not null, `Enter` = Kaydet, `Esc` = Vazgeç.
- [ ] **Step 5: Add `RenameProfile(VpnProfile p)`** to `SearchForm`: return unless `RenameText.Offered(p, Demo.On)`; build `otherNames` from `profiles` of the same `VpnProviders.KindOf` except `p`; show the dialog with `validate = s => NameRules.Check(s, kind, otherNames, kind == VpnKind.WatchGuard)`; unchanged name returns. WatchGuard branch: `NameStore.Set(p.Id, result)`, `ReloadProfiles(true)`, keep the row selected, `SetStatus("\"" + old + "\" → \"" + shown + "\" olarak değiştirildi")` (for an emptied name: `"Otomatik ad geri getirildi"`), `AppLog.Info("rename", "watchguard ok")`. Other kinds for now: `GlassDialog.Show(this, "Bu istemci için ad değiştirme henüz yok.", Text)`.
- [ ] **Step 6: Menu and key.** In `ShowRowMenu` after the "Sabitle" item: `if (RenameText.Offered(p, Demo.On)) rowMenu.Items.Add(Item("Yeniden adlandır…", Glyph.Settings, delegate { RenameProfile(p); }, "F2"));`. In `OnFormKeyDown`: `else if (e.KeyCode == Keys.F2) RenameProfile(list.SelectedProfile);` before the `Down` branch.
- [ ] **Step 7: Run** tests. Expected: all PASS.
- [ ] **Step 8: Manual check (no client is touched):** build a dev exe as `tests\run-tests.ps1` compiles it but `/target:winexe /main:Program` without `Tests.cs` (see `build.ps1` step 1 for the exact csc line), start it with `--instance=dev`, rename a WatchGuard row, press `F2` on it again, empty the name. Expected: the row shows the new name, the second line still shows the server, the automatic name returns after emptying, `names.json` holds / drops the entry.
- [ ] **Step 9: Commit** `feat: rename dialog, menu item and F2; WatchGuard names`.

### Task 4: Sophos Connect

**Files:**
- Modify: `modules\SophosConnect.cs`, `OpenVpnQuickSearch.cs` (`RenameProfile`)
- Test: `tests\Tests.cs`

**Interfaces:**
- Produces: `internal static string[] SophosStore.RenameArgs(string internalName, string displayName)` = `{ "update", "-n", internalName, "-y", displayName }`; `public static string SophosStore.Rename(VpnProfile p, string displayName)` returning `null` or the message built by the existing failure helper (`SophosStore`'s "(sccli çıkış kodu N)" text, action "Ad değiştirilemedi"); `internal static string SophosStore.DisplayNameFrom(string getOutput)` returning the display name or `""`.

- [ ] **Step 1: Find the display-name key (read-only).** Run `& "${env:ProgramFiles(x86)}\Sophos\Connect\sccli.exe" get -n <one name from sccli list> -t config` and note the key that holds the display name (candidates: `display_name`, `displayname`, `display-name`, `name`). Record the key in a comment at `DisplayNameFrom`. If no key carries it, `DisplayNameFrom` returns `""` always and Step 5 uses the fallback.
- [ ] **Step 2: Write the failing test** `SophosRename`, `Run("Sophos ad değiştirme", SophosRename);`

```csharp
var a = SophosStore.RenameArgs("conn-1", "Acme \"Merkez\"");
Check(a.Length == 5 && a[0] == "update" && a[1] == "-n" && a[2] == "conn-1" && a[3] == "-y" && a[4] == "Acme \"Merkez\"", "sccli update -n AD -y GÖRÜNEN");
Check(SophosStore.JoinArgs(a).Contains("\\\"Merkez\\\""), "tırnaklar kaçırılır");
Check(SophosStore.DisplayNameFrom("") == "", "boş çıktı: ad yok");
// plus one Check with a sample of the real `sccli get` output from Step 1 (values replaced by fictional ones)
```

- [ ] **Step 3: Run** tests. Expected: compile error.
- [ ] **Step 4: Implement** the three members; `Rename` uses the existing `Run(string[] args, out string stdout, out string stderr, out int exitCode)`.
- [ ] **Step 5: Show the display name.** In `SophosStore.Load` (line ~149), where each connection's `sccli get` output is already read for the gateway, set `Name` from `DisplayNameFrom` when not empty. Fallback when Step 1 found no key: after a successful `Rename`, also `NameStore.Set(p.Id, name)`, and extend `NameStore.Apply`'s effect to Sophos ids (it already applies to any id with an entry).
- [ ] **Step 6: Sophos branch in `RenameProfile`:** run `SophosStore.Rename` on a thread-pool thread (as `RefreshSophosAsync` does), then on the UI thread: error → `GlassDialog.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning)`; success → `RefreshSophosAsync()` forced (reset `sophosLoadedUtc`), status toast, `AppLog.Info("rename", "sophos ok")` / `AppLog.Warn("rename", "sophos failed: exit")`.
- [ ] **Step 7: Run** tests. Expected: all PASS.
- [ ] **Step 8: Manual verification (changes Sophos Connect; ask the user first):** rename one Sophos connection in the dev exe; `sccli list` / the Sophos window shows the new display name; connect still works by the internal name; rename it back.
- [ ] **Step 9: Commit** `feat: rename Sophos Connect connections (sccli update -y)`.

### Task 5: OpenVPN `config.json` rewrite (pure)

**Files:**
- Modify: `modules\Rename.cs`
- Test: `tests\Tests.cs`

**Interfaces:**
- Produces: `internal static class OpenVpnRename { public static string Rewrite(string json, string profileId, string newName, out string error); public static string NameOf(string json, string profileId); }`. `Rewrite` returns the new file text or `null` with `error` set ("config.json beklenen düzende değil." / "Profil OpenVPN Connect'te bulunamadı."). `NameOf` returns `profileDisplayName` (or `""`).

- [ ] **Step 1: Write the failing test** `OpenVpnRewrite`, `Run("OpenVPN config.json ad yazma", OpenVpnRewrite);`. Build the fixture with `JavaScriptSerializer` exactly as the real file is shaped: root `{ "appliedPatches": "[\"x\"]", "persist:root": "<string>", "persist:systemService": "{}" }`; `persist:root` = serialized `{ "status": "<string>", "lastAction": "{\"type\":\"X\"}", "_persist": "{\"version\":-1}" }`; `status` = serialized `{ "profiles": { "111": { "id":"111", "profileDisplayName":"Eski Ad", "profileName":"Eski Ad", "name":"vpn.ornek.example [Eski Ad]", "hostname":"vpn.ornek.example", "savedPassword":true }, "222": { "id":"222", "profileDisplayName":"Diğer", "profileName":"Diğer", "name":"gw.ornek.example [Diğer]" } }, "connectedProfile": { "id":"111", "profileName":"Eski Ad", "profileDisplayName":"Eski Ad", "profileType":"pc" }, "previouslyConnectedProfile": { "id":"222", "profileName":"Diğer", "profileDisplayName":"Diğer" }, "connectedAt": 5 }`.

```csharp
string err;
var outJson = OpenVpnRename.Rewrite(fixture, "111", "Yeni \"Ad\" \\ İş 🚀", out err);
Check(outJson != null && err == null, "yazma başarılı");
Check(OpenVpnRename.NameOf(outJson, "111") == "Yeni \"Ad\" \\ İş 🚀", "özel karakterli ad geri okunur");
// parse outJson the same way and compare:
Check(P(outJson, "111", "profileName") == "Yeni \"Ad\" \\ İş 🚀", "profileName değişir");
Check(P(outJson, "111", "name") == "vpn.ornek.example [Yeni \"Ad\" \\ İş 🚀]", "name içindeki eski ad değişir");
Check(P(outJson, "111", "hostname") == "vpn.ornek.example" && PB(outJson, "111", "savedPassword"), "diğer alanlar korunur");
Check(P(outJson, "222", "profileDisplayName") == "Diğer" && P(outJson, "222", "name") == "gw.ornek.example [Diğer]", "diğer profil değişmez");
Check(Connected(outJson, "connectedProfile", "profileDisplayName") == "Yeni \"Ad\" \\ İş 🚀", "connectedProfile güncellenir");
Check(Connected(outJson, "previouslyConnectedProfile", "profileDisplayName") == "Diğer", "başka kimlikli previouslyConnectedProfile değişmez");
Check(Root(outJson, "appliedPatches") == "[\"x\"]" && Root(outJson, "persist:systemService") == "{}", "kökteki diğer anahtarlar aynı");
Check(RootIsString(outJson, "persist:root") && PersistIsString(outJson, "status") && PersistIsString(outJson, "lastAction"), "çift kodlama korunur");
Check(OpenVpnRename.Rewrite(fixture, "999", "X", out err) == null && err == "Profil OpenVPN Connect'te bulunamadı.", "bilinmeyen kimlik");
Check(OpenVpnRename.Rewrite("{\"persist:root\":{\"other\":{}}}", "111", "X", out err) == null && err == "config.json beklenen düzende değil.", "beklenmeyen düzen");
Check(OpenVpnRename.Rewrite("{ bozuk", "111", "X", out err) == null && err != null, "bozuk JSON");
var noBracket = fixture.Replace("vpn.ornek.example [Eski Ad]", "vpn.ornek.example");
Check(P(OpenVpnRename.Rewrite(noBracket, "111", "Yeni", out err), "111", "name") == "vpn.ornek.example", "name eski adı içermiyorsa dokunulmaz");
```

`P`, `PB`, `Connected`, `Root`, `RootIsString`, `PersistIsString` are small local test helpers that decode the two string levels.

- [ ] **Step 2: Run** tests. Expected: compile error.
- [ ] **Step 3: Implement `Rewrite` / `NameOf`.** Deserialize root; `persist:root` must be a string → deserialize; `status` must be a string → deserialize; `profiles` must be an object. Change the fields, then serialize `status` back to a string, `persist:root` back to a string, root to text, with `MaxJsonLength = int.MaxValue` and `RecursionLimit = 256` as `ConnectionMonitor.RefreshConfig` uses. Values that were strings at a level stay strings (only the two levels on the path are decoded). `name`: replace the old `profileDisplayName` with the new name only when it occurs in `name`.
- [ ] **Step 4: Run** tests. Expected: all PASS.
- [ ] **Step 5: Commit** `feat: OpenVPN config.json name rewrite`.

### Task 6: OpenVPN apply cycle and pending renames

**Files:**
- Modify: `modules\Rename.cs`, `OpenVpnQuickSearch.cs` (`RenameProfile`, `OnConnectionChanged` ~line 1635, startup), `modules\GlassList.cs` (pending mark)
- Test: `tests\Tests.cs`

**Interfaces:**
- Consumes: `OpenVpnRename.Rewrite`, `OpenVpnRename.NameOf`, `PendingRenames.*`, `ProfileStore.ConfigPath`.
- Produces:
  - `internal enum PendingAction { Apply, Drop }` and `internal static PendingAction OpenVpnRename.Decide(string pendingName, bool profileExists, string clientName)` — `Drop` when the profile is gone or `clientName == pendingName`, else `Apply`.
  - `internal static string OpenVpnRename.ApplyToFile(string configPath, IDictionary<string,string> renames)` — backup to `configPath + ".vpnm-yedek"`, rewrite every id, write `configPath + ".vpnm-tmp"`, re-read and verify each name with `NameOf` and that the profile count is unchanged, replace; any failure restores the backup and returns the message. `null` on success.
  - `internal static string OpenVpnRename.ApplyWithRestart(IDictionary<string,string> renames)` — close OpenVPN Connect (`CloseMainWindow` on each `OpenVPNConnect` process, wait up to 5 s for all to exit, `Kill` the rest), `ApplyToFile(ProfileStore.ConfigPath, ...)`, start `OpenVPNConnect.exe` again (`QuietWindow.KeepProcessInBackground("OpenVPNConnect", 12)` first when no window was showing before). Returns `null` or the message.
  - `public Func<VpnProfile, bool> ProfileList.IsPendingName` (default: false).

- [ ] **Step 1: Write the failing test** `OpenVpnApply`, `Run("OpenVPN ad uygulama", OpenVpnApply);` with the Task 5 fixture written to a temp `config.json`:

```csharp
Check(OpenVpnRename.Decide("Yeni", true, "Eski") == PendingAction.Apply, "bekleyen ad uygulanır");
Check(OpenVpnRename.Decide("Yeni", false, "") == PendingAction.Drop, "profil silinmiş: bırakılır");
Check(OpenVpnRename.Decide("Yeni", true, "Yeni") == PendingAction.Drop, "istemcide zaten o ad: bırakılır");
var err = OpenVpnRename.ApplyToFile(path, new Dictionary<string, string> { { "111", "Yeni Ad" }, { "222", "İkinci" } });
Check(err == null, "dosyaya yazılır");
var text = File.ReadAllText(path);
Check(OpenVpnRename.NameOf(text, "111") == "Yeni Ad" && OpenVpnRename.NameOf(text, "222") == "İkinci", "iki ad da dosyada");
Check(File.Exists(path + ".vpnm-yedek") && OpenVpnRename.NameOf(File.ReadAllText(path + ".vpnm-yedek"), "111") == "Eski Ad", "yedek eski içeriği tutar");
Check(!File.Exists(path + ".vpnm-tmp"), "geçici dosya kalmaz");
var before = File.ReadAllText(path);
Check(OpenVpnRename.ApplyToFile(path, new Dictionary<string, string> { { "999", "X" } }) != null, "bilinmeyen kimlik hata verir");
Check(File.ReadAllText(path) == before, "hata olunca dosya değişmez");
```

- [ ] **Step 2: Run** tests. Expected: compile error.
- [ ] **Step 3: Implement** `Decide`, `ApplyToFile`, `ApplyWithRestart`.
- [ ] **Step 4: Run** tests. Expected: all PASS.
- [ ] **Step 5: Wire the panel.** OpenVPN branch of `RenameProfile`: `busy = connectedId != null || connectingProfileId != null || disconnectStarted != DateTime.MinValue` (also the value passed to `RenameText.Hint`). Busy → `PendingRenames.Set(p.Id, name)`, reload, status `"Ad OpenVPN bağlantısı kesilince yazılacak"`. Not busy → `ApplyWithRestart` on a thread-pool thread with the single rename, then UI thread: error dialog or reload + toast. New `void ApplyPendingOpenVpnRenames()`: load pending, `Decide` each against `profiles` and the current `config.json` (`NameOf`), drop / collect, one `ApplyWithRestart` for the collected ones, remove the applied entries on success, keep them on failure and show the message once. Call it from `OnConnectionChanged` when `id == null` and nothing is in flight, and once after the first profile load at startup under the same condition.
- [ ] **Step 6: Pending mark.** In `ProfileList.PaintRow` draw `Assets.Draw(g, "reach-checking", ...)` (the existing clock icon) in `Ui.Warning`, `S(12)` square, right after the name text when `IsPendingName(p)`; tooltip "OpenVPN'e yazılmayı bekliyor" through the existing `tip` when the pointer is over it. `SearchForm` sets `list.IsPendingName = p => PendingRenames.IsPending(p.Id)`.
- [ ] **Step 7: Manual verification on the real client (closes and reopens OpenVPN Connect; ask the user first, no tunnel up).** Rename one OpenVPN profile in the dev exe. Then, in OpenVPN Connect: the profile shows the new name; connect and disconnect it once (forces OpenVPN Connect to save `config.json`); the name is still the new one in OpenVPN Connect and in the panel; saved password still works. Second pass: connect a profile, rename another one, see the clock mark, disconnect, see OpenVPN Connect restart and the mark disappear.
  **If OpenVPN Connect drops the edit after its own save: stop. Restore `config.json.vpnm-yedek` with OpenVPN Connect closed, report to the user; the OpenVPN approach must be reconsidered (spec, "Open question").** Record which of `profileDisplayName` / `profileName` / `name` OpenVPN Connect shows, in a comment at `Rewrite`.
- [ ] **Step 8: Commit** `feat: rename OpenVPN Connect profiles (config.json, deferred while connected)`.

### Task 7: FortiClient SSL tunnels

**Files:**
- Modify: `modules\Rename.cs`, `modules\FortiClient.cs`, `OpenVpnQuickSearch.cs` (`RenameProfile`, `Main` argument loop ~line 3041)
- Test: `tests\Tests.cs`

**Interfaces:**
- Produces:
  - `internal static class FortiRename { public const int Ok = 0, NotFound = 2, TargetExists = 3, CopyFailed = 4, Declined = 5; public static int RenameKey(RegistryKey tunnelsParent, string oldName, string newName); public static bool Exists(RegistryKey tunnelsParent, string name); public static int RenameAll(IList<RegistryKey> tunnelParents, string oldName, string newName); public static string Message(int code); public static int RunElevated(string oldName, string newName); public static int ElevatedMain(string oldB64, string newB64); }`
  - `RenameAll`: `TargetExists` when `newName` exists under **any** parent (nothing changed); `NotFound` when `oldName` exists under none; otherwise `RenameKey` on each parent that has `oldName`; on a failure after an earlier parent succeeded, rename that one back and return `CopyFailed`.
  - `RenameKey`: create `newName`, copy every value with `GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames)` + `GetValueKind`, copy subkeys recursively, verify value count, `DeleteSubKeyTree(oldName)`.
  - `internal static class IdMigration { public static HashSet<string> Pins(HashSet<string> pins, string oldId, string newId); public static string[] HistoryLines(string[] lines, string oldId, string newId, string newName); }` — history lines are the raw `history.csv` lines (`ProfileId;ProfileName;StartUtc;EndUtc`, quoting as `ConnectionHistory.Join`); only rows whose first field equals `oldId` change.
  - `public static void ConnectionHistory.Rewrite(string oldId, string newId, string newName)` using `HistoryLines`, under the existing `gate`, and clearing `cached`.
  - Messages: `NotFound` "Tünel FortiClient'ta bulunamadı.", `TargetExists` "Bu adda bir FortiClient tüneli zaten var.", `CopyFailed` "Kayıt defteri güncellenemedi; ad değişmedi.", `Declined` "Yönetici onayı verilmedi; ad değişmedi.".

- [ ] **Step 1: Write the failing test** `FortiRenameKeys`, `Run("FortiClient ad değiştirme", FortiRenameKeys);`. Scratch keys under `HKCU\Software\OpenVpnQuickSearch.Tests\<guid>\A` and `...\B` (two "hives"), deleted in `finally`. In A and B create `Eski` with `Server` (String) = "vpn.ornek.example:443", `promptusername` (DWord) = 1, `DATA1` (Binary) = `{1,2,3}`, `Description` (ExpandString) = "%TEMP%".

```csharp
Check(FortiRename.RenameAll(new[] { a, b }, "Eski", "Yeni") == FortiRename.Ok, "iki anahtar da yeniden adlandırılır");
Check(!FortiRename.Exists(a, "Eski") && FortiRename.Exists(a, "Yeni") && !FortiRename.Exists(b, "Eski") && FortiRename.Exists(b, "Yeni"), "eski yok, yeni var");
using (var k = a.OpenSubKey("Yeni"))
{
    Check((string)k.GetValue("Server") == "vpn.ornek.example:443" && k.GetValueKind("Server") == RegistryValueKind.String, "metin değeri korunur");
    Check((int)k.GetValue("promptusername") == 1 && k.GetValueKind("promptusername") == RegistryValueKind.DWord, "DWORD korunur");
    Check(((byte[])k.GetValue("DATA1")).SequenceEqual(new byte[] { 1, 2, 3 }) && k.GetValueKind("DATA1") == RegistryValueKind.Binary, "ikili değer korunur");
    Check((string)k.GetValue("Description", null, RegistryValueOptions.DoNotExpandEnvironmentNames) == "%TEMP%" && k.GetValueKind("Description") == RegistryValueKind.ExpandString, "ExpandString genişletilmeden korunur");
}
// target exists in one hive only: nothing changes anywhere
a.CreateSubKey("Bir").SetValue("Server", "x"); b.CreateSubKey("Bir").SetValue("Server", "x"); b.CreateSubKey("Dolu").SetValue("Server", "y");
Check(FortiRename.RenameAll(new[] { a, b }, "Bir", "Dolu") == FortiRename.TargetExists, "hedef bir kovanda varsa reddedilir");
Check(FortiRename.Exists(a, "Bir") && FortiRename.Exists(b, "Bir") && !FortiRename.Exists(a, "Dolu"), "hiçbir kovan değişmez");
Check(FortiRename.RenameAll(new[] { a, b }, "Yok", "Z") == FortiRename.NotFound, "olmayan tünel");
// only in one hive (this PC has one tunnel only in HKCU)
b.CreateSubKey("Tek").SetValue("Server", "t");
Check(FortiRename.RenameAll(new[] { a, b }, "Tek", "Tek2") == FortiRename.Ok && FortiRename.Exists(b, "Tek2") && !FortiRename.Exists(a, "Tek2"), "yalnız bir kovandaki tünel");
// id migration
var pins = IdMigration.Pins(new HashSet<string> { "forti-ssl:Eski", "123" }, "forti-ssl:Eski", "forti-ssl:Yeni");
Check(pins.SetEquals(new[] { "forti-ssl:Yeni", "123" }), "sabitleme yeni kimliğe taşınır");
var lines = IdMigration.HistoryLines(new[] { "ProfileId;ProfileName;StartUtc;EndUtc", "forti-ssl:Eski;Eski;2026-11-26T14:05:51Z;2026-11-26T14:06:25Z", "123;A;2026-11-27T05:41:13Z;2026-11-27T05:41:40Z" }, "forti-ssl:Eski", "forti-ssl:Yeni", "Yeni");
Check(lines[0] == "ProfileId;ProfileName;StartUtc;EndUtc" && lines[1] == "forti-ssl:Yeni;Yeni;2026-11-26T14:05:51Z;2026-11-26T14:06:25Z" && lines[2].StartsWith("123;A;"), "geçmiş satırları yeni kimliğe taşınır");
Check(FortiRename.Message(FortiRename.Declined) == "Yönetici onayı verilmedi; ad değişmedi.", "UAC reddi metni");
```

- [ ] **Step 2: Run** tests. Expected: compile error.
- [ ] **Step 3: Implement** `FortiRename` (pure part), `IdMigration`, `ConnectionHistory.Rewrite`.
- [ ] **Step 4: Run** tests. Expected: all PASS.
- [ ] **Step 5: Elevated entry.** `ElevatedMain` decodes the two base64 (UTF-8) arguments, validates the new name with `NameRules.Check(..., VpnKind.Forti, none, false)`, opens the two tunnel parents writable with the 64-bit view exactly as `FortiStore.OpenRead` picks hive and view (`SslKey`, and its `Software` spelling under HKCU), calls `RenameAll`, then updates the HKCU `SelectionKey` value `connection` when it equals the old name; returns the code. In `Main`'s argument loop: `--rename-forti` followed by two arguments → `return FortiRename.ElevatedMain(args[i + 1], args[i + 2]);` before any window, mutex or settings work. `RunElevated`: `ProcessStartInfo { FileName = Application.ExecutablePath, Arguments = "--rename-forti " + b64old + " " + b64new, Verb = "runas", UseShellExecute = true, WindowStyle = Hidden }`, `WaitForExit(30000)`; `Win32Exception` with `NativeErrorCode == 1223` → `Declined`; timeout → `CopyFailed`. HKCU note: an elevated process of the same user writes the same HKCU; if the UAC prompt is answered with another account's credentials HKCU is that account's, so the panel process renames the HKCU key itself (not elevated) **after** the elevated call returned `Ok` or `NotFound` for HKLM. Split accordingly: elevated part = HKLM parent only; panel part = HKCU parent + selection value; `TargetExists` is checked by the panel across both (read access) before asking for elevation.
- [ ] **Step 6: Forti branch in `RenameProfile`:** only `forti-ssl:`; refuse with "Önce bağlantıyı kesin." when `p.Id` is `fortiConnectedId`, `fortiConnectingId` or `fortiDisconnectingId`. Run on a thread-pool thread. On `Ok`: move the saved password (`FortiCredentialStore.Load(old)` → `Save(new, cred)` → `Delete(old)`, skip when none), `pins = IdMigration.Pins(...)` + `PinStore.Save`, `ConnectionHistory.Rewrite`, move the entry in `NameStore` / reachability state if one is keyed by the old id (grep `Reachability` for id-keyed dictionaries), `ReloadProfiles(true)`, select the new id, toast. Other codes: `GlassDialog` with `FortiRename.Message(code)`. `AppLog.Info("rename", "forti ok")` / `AppLog.Warn("rename", "forti failed: code " + code)`.
- [ ] **Step 7: Run** tests. Expected: all PASS.
- [ ] **Step 8: Manual verification (UAC prompt, changes FortiClient's registry; ask the user first, tunnel not connected).** Export `HKLM\SOFTWARE\Fortinet\FortiClient\Sslvpn\Tunnels` and the HKCU twin to `.reg` files first. Rename one tunnel in the dev exe: one UAC prompt; FortiClient shows the new name (restart only its console if it does not, and note the result in a comment); connect with the saved password works; pin and history follow; decline UAC on a second try → message, nothing changed. Rename it back.
- [ ] **Step 9: Commit** `feat: rename FortiClient SSL tunnels (elevated registry rename, id migration)`.

### Task 8: Backup, notes, release build

**Files:**
- Modify: `modules\BackupService.cs`, `modules\ImportService.cs`, `docs\RELEASE-NOTES.md`, `paket\OpenVPN-Hizli-Arama-Kullanim.txt`
- Test: `tests\Tests.cs`

**Interfaces:**
- Consumes: `NameStore.Load`, `NameStore.Set`.
- Produces: the backup zip carries `names.json` at its root; restore merges its entries with `NameStore.Set` (existing panel names win over the backup's only when the user restores into a PC that already has a name for that id: keep the existing one).

- [ ] **Step 1: Write the failing test** next to the existing backup tests (grep `CreateBackup` in `tests\Tests.cs`): after `NameStore.Set("watchguard-ssl:1.2.3.4:443", "Acme")`, a backup's zip contains an entry `names.json` whose JSON has that pair; restoring it into an empty `NameStore` folder brings the pair back; restoring over an existing different name for the same id keeps the existing one.
- [ ] **Step 2: Run** tests. Expected: FAIL (no `names.json` in the zip).
- [ ] **Step 3: Implement** in `BackupService.CreateBackup` (add the file when the store is not empty) and in the restore path of `ImportService` that reads `manifest.json`.
- [ ] **Step 4: Run** tests. Expected: all PASS.
- [ ] **Step 5: Docs.** `docs\RELEASE-NOTES.md`: a new top entry in the file's existing format: rename from the panel (`F2`), per-client behaviour in four lines, FortiClient IPsec and Brave not covered. `paket\OpenVPN-Hizli-Arama-Kullanim.txt`: a short "Profil adını değiştirme" section with the same four lines.
- [ ] **Step 6: Full build.** `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -NoProfiles`. Expected: tests 0 failed, exit code 0, `outputs\OpenVPN-Hizli-Arama-Kurulum-Profilsiz.zip` written.
- [ ] **Step 7: Commit** `feat: names.json in backups; release notes for profile rename`.
