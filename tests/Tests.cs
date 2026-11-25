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
        Run("Forti kimlik hedefi sabit", FortiTargetPinned);
        Run("WatchGuard ayrıştırma", WatchGuardParsing);
        Run("WatchGuard günlük durumu", WatchGuardLog);
        Run("WatchGuard kimlik bilgisi öneki", WatchGuardCredentialPrefix);
        Run("WatchGuard Load hiçbir şey başlatmaz", WatchGuardLoad);
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
        Check(SophosStore.HasState("State: Disconnected\n") && !SophosStore.HasState("garbage\nlines") && !SophosStore.HasState(""), "durum anahtarı anlaşıldı mı");
        Check(!SophosStore.UseAdapterFallback(true) && SophosStore.UseAdapterFallback(false), "adaptör yedeği yalnız çıktı anlaşılmadıysa");
        Check(SophosStore.Load() != null, "Load hata vermez");
    }

    // Forti's stored target must stay "OpenVpnQuickSearch:forti:<name>" (existing saved credentials).
    static void FortiTargetPinned()
    {
        const string name = "__oqs_test_target__";
        FortiCredentialStore.Delete(name);
        try
        {
            Check(FortiCredentialStore.Save(name, new FortiCredential { Username = "u", Password = "p" }) == null, "önek vermeden kaydet");
            Check(FortiCredentialStore.Has(name, "OpenVpnQuickSearch:forti:"), "hedef OpenVpnQuickSearch:forti:<ad>");
        }
        finally { FortiCredentialStore.Delete(name); }
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

    static void WatchGuardParsing()
    {
        var s = WatchGuardStore.ParseServers("vpn1.example:443,vpn2.example:10443,");
        Check(s.Count == 2 && s[0] == "vpn1.example:443" && s[1] == "vpn2.example:10443", "kayıt değeri (sondaki virgül)");
        Check(WatchGuardStore.ParseServers(" vpn1.example:443 ,,VPN1.example:443,").Count == 1, "boşluk, boş öğe ve tekrar");
        Check(WatchGuardStore.ParseServers("").Count == 0 && WatchGuardStore.ParseServers(null).Count == 0 && WatchGuardStore.ParseServers(",").Count == 0, "boş değer");

        Check(WatchGuardStore.HostOf("vpn1.example:443") == "vpn1.example", "host:port -> host");
        Check(WatchGuardStore.HostOf("vpn1.example") == "vpn1.example", "portsuz giriş");
        Check(WatchGuardStore.HostOf("[fd00::1]:443") == "fd00::1" && WatchGuardStore.HostOf("fd00::1") == "fd00::1", "IPv6");

        Check(WatchGuardStore.ProfileName("vpn1.example:443", 1) == "WatchGuard SSL VPN", "tek sunucu: yalın ad");
        Check(WatchGuardStore.ProfileName("vpn1.example:443", 2) == "WatchGuard SSL VPN · vpn1.example", "birden çok sunucu: ad + host");

        var p = WatchGuardStore.MakeProfile("vpn2.example:10443", "kullanici.ornek", 2);
        Check(p.Id == "watchguard-ssl:vpn2.example:10443" && WatchGuardStore.IsWatchGuard(p) && !WatchGuardStore.IsWatchGuard(new VpnProfile { Id = "sophos:x" }), "kimlik öneki");
        Check(WatchGuardStore.ServerOf(p) == "vpn2.example:10443" && p.Host == "vpn2.example:10443" && p.Username == "kullanici.ornek", "sunucu ve kullanıcı adı");
        Check(p.Source == "" && p.Keys.Length > 0 && p.Initials.Length > 0, "profil alanları");

        Check(WatchGuardStore.ServerMatches("vpn1.example:443", "VPN1.example:443"), "tam giriş eşleşir");
        Check(WatchGuardStore.ServerMatches("vpn1.example", "vpn1.example:443"), "portsuz gösterim eşleşir");
        Check(!WatchGuardStore.ServerMatches("vpn1.example:444", "vpn1.example:443"), "farklı port eşleşmez");
        Check(!WatchGuardStore.ServerMatches("vpn10.example:443", "vpn1.example:443") && !WatchGuardStore.ServerMatches("", "x"), "farklı host / boş eşleşmez");
        var list = new List<string> { "vpn1.example:443", "vpn2.example:10443" };
        Check(WatchGuardStore.MatchServer("vpn2.example:10443", list) == "vpn2.example:10443" && WatchGuardStore.MatchServer("vpn3.example:1", list) == null, "günlükteki sunucu → kayıt girişi");

        Check(WatchGuardStore.IsClientTapAdapter("TAP-Windows Adapter V9") && WatchGuardStore.IsClientTapAdapter("TAP-Windows Adapter V9 #2"), "WatchGuard TAP bağdaştırıcısı");
        Check(!WatchGuardStore.IsClientTapAdapter("TAP-Windows Adapter V9 for OpenVPN Connect") && !WatchGuardStore.IsClientTapAdapter("Sophos TAP Adapter")
              && !WatchGuardStore.IsClientTapAdapter("TAP-Windows Adapter V9-WFP Native MAC Layer LightWeight Filter-0000"), "başka bağdaştırıcılar sayılmaz");
        Check(WatchGuardGui.MakeWParam(1005, 0).ToInt64() == 1005 && WatchGuardGui.MakeWParam(1034, 1).ToInt64() == 0x1040A, "WM_COMMAND wParam");
    }

    static void WatchGuardLog()
    {
        const string start = "2026-01-01T10:00:00.000 Launching WatchGuard Mobile VPN with SSL client. Version 1.0 (Build 1)\r\r\n";
        const string req = "2026-01-01T10:00:01.000 Requesting client configuration from vpn2.example:10443\r\r\n";
        const string state = "2026-01-01T10:00:05.000 OVPN:>STATE:1,CONNECTED,SUCCESS,10.0.0.2,192.0.2.1,10443,,\r\r\n";
        const string ok = "2026-01-01T10:00:06.000 SSLVPN Connected\r\r\n";
        const string closed = "2026-01-01T11:00:00.000 Connection Closed.\r\r\n";
        const string exiting = "2026-01-01T11:00:00.000 OVPN:>LOG:2,,MANAGEMENT: >STATE:2,EXITING,SIGTERM,,,,,\r\r\n";

        var a = WatchGuardStore.ParseLog(start + req + state + ok);
        Check(a.Known && a.Connected && a.Server == "vpn2.example:10443", "bağlandı + sunucu");
        var b = WatchGuardStore.ParseLog(start + req + state);
        Check(b.Connected, "yalnız openvpn CONNECTED,SUCCESS da bağlı sayılır");
        Check(!WatchGuardStore.ParseLog(start + req + ok + closed).Connected, "Connection Closed bağlantıyı bitirir");
        Check(!WatchGuardStore.ParseLog(start + req + ok + exiting).Connected, "EXITING bağlantıyı bitirir");
        var c = WatchGuardStore.ParseLog(start + req + ok + start);
        Check(c.Known && !c.Connected && c.Server == "", "istemcinin yeniden başlaması durumu sıfırlar");
        var d = WatchGuardStore.ParseLog(start + req);
        Check(d.Known && !d.Connected && d.Server == "vpn2.example:10443", "bağlanırken bağlı sayılmaz");
        var e = WatchGuardStore.ParseLog("garbage\nmore");
        Check(!e.Known && !e.Connected && !WatchGuardStore.ParseLog(null).Known, "anlaşılmayan metin: bilinmiyor");

        Check(WatchGuardStore.Decide(true, true, false, a) && WatchGuardStore.Decide(true, false, true, a), "günlük bağlı + tünel açık");
        Check(!WatchGuardStore.Decide(false, true, true, a), "istemci çalışmıyorsa bağlı değil");
        Check(!WatchGuardStore.Decide(true, false, false, a), "tünel yoksa eski günlük kaydı sayılmaz");
        Check(!WatchGuardStore.Decide(true, true, true, WatchGuardStore.ParseLog(start + req + ok + closed)), "günlük kapandı diyorsa bağdaştırıcı sayılmaz");
        Check(WatchGuardStore.Decide(true, true, false, e) && !WatchGuardStore.Decide(true, false, true, e), "günlük yoksa bağdaştırıcı karar verir");
    }

    static void WatchGuardCredentialPrefix()
    {
        const string name = "__oqs_test_wg__.example:443";
        var px = WatchGuardStore.CredentialPrefix;
        Check(px != SophosStore.CredentialPrefix && px != "OpenVpnQuickSearch:forti:", "önek Forti ve Sophos'tan farklı");
        FortiCredentialStore.Delete(name); FortiCredentialStore.Delete(name, px);
        try
        {
            Check(FortiCredentialStore.Save(name, new FortiCredential { Username = "u", Password = "p" }, px) == null, "WatchGuard öneğiyle kaydet");
            Check(FortiCredentialStore.Has(name, px) && !FortiCredentialStore.Has(name) && !FortiCredentialStore.Has(name, SophosStore.CredentialPrefix), "diğer hedefler etkilenmez");
            Check(FortiCredentialStore.Delete(name, px) && !FortiCredentialStore.Has(name, px), "sil");
        }
        finally { FortiCredentialStore.Delete(name, px); }
    }

    // Load() reads the registry only: as many profiles as Server entries, and no client process.
    static void WatchGuardLoad()
    {
        var running = WatchGuardStore.ClientRunning;
        var profiles = WatchGuardStore.Load();
        Check(profiles != null, "Load hata vermez");
        if (WatchGuardStore.IsInstalled) Check(profiles.Count == WatchGuardStore.Servers().Count, "kayıttaki her sunucu için bir profil (" + profiles.Count + ")");
        foreach (var p in profiles) if (!WatchGuardStore.IsWatchGuard(p)) Check(false, "profil kimliği watchguard-ssl: ile başlar");
        Check(WatchGuardStore.ClientRunning == running, "istemci başlatılmadı");
    }
}
