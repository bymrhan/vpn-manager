using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

// Automated tests. Compiled together with the app sources by tests\run-tests.ps1 (build.ps1 runs
// it before packaging). Needs no VPN, no FortiClient and no network. The credential test writes
// and deletes one Windows Credential Manager entry ("OpenVpnQuickSearch:forti:__oqs_test__").
static class Tests
{
    static int failed, passed;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
        if (ok) passed++; else failed++;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine(name);
        try { test(); }
        catch (Exception ex) { Check(false, "beklenmeyen hata: " + ex); }
    }

    [STAThread]
    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Run("ConnectionMonitor: açık tutulan log", OpenLogIsReRead);
        Run("FortiGui.ShowsTunnel", ShowsTunnel);
        Run("FortiCredentialStore", CredentialStore);
        Run("Forti son bağlanma zamanı", FortiLastConnected);
        Run("Sophos Connect ayrıştırma", SophosParsing);
        Run("Sophos kimlik bilgisi öneki", SophosCredentialPrefix);
        Console.WriteLine();
        Console.WriteLine(passed + " geçti, " + failed + " başarısız");
        return failed == 0 ? 0 : 1;
    }

    // OpenVPN Connect keeps ovpn.log open for the whole session; NTFS then leaves the directory
    // entry's size and time stale. A CONNECTED event written after a quiet period must be seen.
    static void OpenLogIsReRead()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ovpnqs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var field = typeof(ConnectionMonitor).GetField("LogDirectoryOverride", BindingFlags.Static | BindingFlags.NonPublic);
        var last = typeof(ConnectionMonitor).GetMethod("LastLogEvent", BindingFlags.Instance | BindingFlags.NonPublic);
        field.SetValue(null, dir);
        try
        {
            var m = new ConnectionMonitor(null);
            using (var w = new FileStream(Path.Combine(dir, "ovpn.log"), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
            {
                Action<string> write = s => { var b = Encoding.UTF8.GetBytes(s); w.Write(b, 0, b.Length); w.Flush(); };
                write("[x] EVENT: DISCONNECTED ⏎\n");
                last.Invoke(m, null);                   // first read: entry stale, then synced by the read
                Thread.Sleep(300);
                var before = (string)last.Invoke(m, null); // steady state: cache equals the synced entry
                Thread.Sleep(1200);
                write("[x] EVENT: CONNECTED user@host⏎\n");
                var after = (string)last.Invoke(m, null);
                Check(before == "DISCONNECTED", "ilk olay DISCONNECTED (" + before + ")");
                Check(after == "CONNECTED", "yazılan yeni olay görülür (" + after + ")");
            }
        }
        finally
        {
            field.SetValue(null, null);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static void ShowsTunnel()
    {
        Check(FortiGui.ShowsTunnel("Personal VPNs ÖRNEK-İŞ", "ÖRNEK-İŞ"), "grup önekli değer eşleşir");
        Check(FortiGui.ShowsTunnel("Personal VPNs örnek-iş", "ÖRNEK-İŞ"), "büyük/küçük harf (Türkçe İ) eşleşir");
        Check(FortiGui.ShowsTunnel("ÖRNEK-İŞ", "ÖRNEK-İŞ"), "öneksiz değer eşleşir");
        Check(!FortiGui.ShowsTunnel("Personal VPNs ESKI-ÖRNEK-İŞ", "ÖRNEK-İŞ"), "ad parçası eşleşmez");
        Check(!FortiGui.ShowsTunnel("Personal VPNs ÖRNEK", "ÖRNEK-İŞ"), "farklı tünel eşleşmez");
        Check(!FortiGui.ShowsTunnel("", "X"), "boş değer eşleşmez");
    }

    // FortiClient keeps no last-connected time: Forti profiles get it from the history, so that
    // they show under "Son bağlanılanlar" after a connect and disconnect.
    static void FortiLastConnected()
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 11, 24, 8, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);
        var history = new List<HistoryEntry>
        {
            new HistoryEntry { ProfileId = "forti-ssl:ALFA", StartUtc = t1, EndUtc = t1.AddMinutes(5) },
            new HistoryEntry { ProfileId = "forti-ssl:ALFA", StartUtc = t2, EndUtc = t2.AddMinutes(5) },
            new HistoryEntry { ProfileId = "ovpn-1", StartUtc = t2, EndUtc = t2.AddMinutes(1) },
            new HistoryEntry { ProfileId = "", StartUtc = t2, EndUtc = t2 },
        };
        var starts = ConnectionHistory.LastStarts(history);
        Check(starts["forti-ssl:ALFA"] == (long)(t2 - epoch).TotalMilliseconds, "profil başına en son başlangıç");
        Check(!starts.ContainsKey(""), "kimliksiz kayıt yok sayılır");

        var forti = new VpnProfile { Id = "forti-ssl:ALFA", Name = "ALFA", LastConnected = 0 };
        var other = new VpnProfile { Id = "forti-ssl:BETA", Name = "BETA", LastConnected = 0 };
        var ovpn = new VpnProfile { Id = "ovpn-1", Name = "x", LastConnected = 5 };
        FortiStore.ApplyLastConnected(new[] { forti, other, ovpn }, starts);
        Check(forti.LastConnected == starts["forti-ssl:ALFA"], "bağlanılmış Forti profili zaman alır");
        Check(other.LastConnected == 0, "hiç bağlanılmamış Forti profili 0 kalır");
        Check(ovpn.LastConnected == 5, "OpenVPN profiline dokunulmaz (kendi zamanı config.json'dan)");
    }

    static void CredentialStore()
    {
        const string tunnel = "__oqs_test__";
        FortiCredentialStore.Delete(tunnel);
        try
        {
            Check(!FortiCredentialStore.Has(tunnel), "başlangıçta kayıt yok");
            Check(FortiCredentialStore.Save(tunnel, new FortiCredential { Username = "kullanıcı", Password = "deneme-Şİ1" }) == null, "kaydet");
            var back = FortiCredentialStore.Load(tunnel);
            Check(back != null && back.Username == "kullanıcı" && back.Password == "deneme-Şİ1", "geri oku (Türkçe karakter)");
            Check(FortiCredentialStore.Save(tunnel, new FortiCredential { Username = "u2", Password = "p2" }) == null
                  && FortiCredentialStore.Load(tunnel).Password == "p2", "üzerine yaz");
            Check(FortiCredentialStore.Delete(tunnel) && !FortiCredentialStore.Has(tunnel), "sil");
            Check(FortiCredentialStore.Delete(tunnel), "olmayanı silmek hata değil");
            Check(FortiCredentialStore.Load(tunnel) == null, "olmayan kayıt null döner");
        }
        finally { FortiCredentialStore.Delete(tunnel); }
    }

    static void SophosParsing()
    {
        Check(SophosStore.ParseList("Connections:\r\n  None\r\n").Count == 0, "boş liste (None)");
        Check(SophosStore.ParseList("").Count == 0 && SophosStore.ParseList(null).Count == 0, "boş metin");
        var names = SophosStore.ParseList("Connections:\r\n  ofis-ornek\r\n  - Ev Örnek VPN\r\n\r\n  ofis-ornek\r\n");
        Check(names.Count == 2 && names[0] == "ofis-ornek" && names[1] == "Ev Örnek VPN", "birden çok bağlantı (madde işareti, boşluklu ad, tekrar yok)");
        Check(SophosStore.ParseList("Connections:\n  a.example\nOther:\n  b\n").Count == 1, "başka bölüm listeyi bitirir");
        Check(SophosStore.ParseList("garbage \u0001 ??\nnot a list").Count == 0, "çöp metin boş döner");

        Check(SophosStore.ParseHost("Name: x\nGateway: vpn.example:443\n") == "vpn.example:443", "gateway anahtarı");
        Check(SophosStore.ParseHost("  \"server\": \"vpn2.example\",\n") == "vpn2.example", "JSON benzeri satır");
        Check(SophosStore.ParseHost("nothing useful\n???") == "" && SophosStore.ParseHost(null) == "", "host yoksa boş");

        Check(SophosStore.ParseConnected("State: Connected\n"), "connected durumu");
        Check(!SophosStore.ParseConnected("State: Disconnected\n"), "disconnected bağlı sayılmaz");
        Check(!SophosStore.ParseConnected("Status: connecting\n") && !SophosStore.ParseConnected("random\ntext") && !SophosStore.ParseConnected(""), "belirsiz durum bağlı sayılmaz");

        var p = SophosStore.MakeProfile("ofis-ornek", "vpn.example");
        Check(p.Id == "sophos:ofis-ornek" && SophosStore.IsSophos(p) && !SophosStore.IsSophos(new VpnProfile { Id = "forti-ssl:x" }), "kimlik öneki");
        Check(SophosStore.ConnectionName(p) == "ofis-ornek" && p.Source == "" && p.Keys.Length > 0 && p.Initials.Length > 0, "profil alanları");
        Check(SophosStore.Quote("a b") == "\"a b\"" && SophosStore.Quote("x") == "x" && SophosStore.Quote("a\"b") == "\"a\\\"b\"", "argüman tırnaklama");
        Check(SophosStore.Load() != null, "Load hata vermez");
    }

    // Sophos and Forti credentials with the same name must not collide in Credential Manager.
    static void SophosCredentialPrefix()
    {
        const string name = "__oqs_test_sophos__";
        FortiCredentialStore.Delete(name); FortiCredentialStore.Delete(name, SophosStore.CredentialPrefix);
        try
        {
            Check(FortiCredentialStore.Save(name, new FortiCredential { Username = "u", Password = "p" }, SophosStore.CredentialPrefix) == null, "Sophos öneğiyle kaydet");
            Check(FortiCredentialStore.Has(name, SophosStore.CredentialPrefix) && !FortiCredentialStore.Has(name), "Forti hedefi etkilenmez");
            Check(FortiCredentialStore.Delete(name, SophosStore.CredentialPrefix) && !FortiCredentialStore.Has(name, SophosStore.CredentialPrefix), "sil");
        }
        finally { FortiCredentialStore.Delete(name, SophosStore.CredentialPrefix); }
    }
}
