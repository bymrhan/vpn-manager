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
        Run("WatchGuard pencere kararları", WatchGuardWindowRules);
        Run("Sağlayıcı yönlendirme", ProviderRouting);
        Run("Sağlayıcı bağlantı durumu ve yoklama", ProviderState);
        Run("Erişim testi uç noktası", ReachabilityEndpoints);
        Run("Ayarlar: sağlayıcı anahtarları", ProviderSettings);
        Run("Demo sağlayıcı satırları", DemoProviders);
        Run("Güncelleme: sürüm karşılaştırma", UpdateVersions);
        Run("Güncelleme: sürüm JSON'u", UpdateReleaseJson);
        Run("Güncelleme: SHA-256 dosyası", UpdateSha256);
        Run("Güncelleme: denetim zamanı", UpdateDue);
        Run("Güncelleme: boşta mı / kurulu kopya mı", UpdateIdleAndInstall);
        Run("Güncelleme: paket doğrulama", UpdateVerifyPackage);
        Run("Ayarlar: güncelleme", UpdateSettings);
        Run("Güncelleme: başarısızlık işareti", UpdateFailureMarker);
        Run("Güncelleme: zip slip", UpdateZipSlip);
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
        const string state = "2026-01-01T10:00:05.000 OVPN:>STATE:1,CONNECTED,SUCCESS,<vip>,<gw>,10443,,\r\r\n";
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

    // The user's own open dialog is never driven; only the client's exact exit question gets "Yes".
    static void WatchGuardWindowRules()
    {
        Check(WatchGuardGui.PlanForDialog(true, true) == WatchGuardGui.DialogPlan.LeaveToUser, "kullanıcının açık penceresine dokunulmaz");
        Check(WatchGuardGui.PlanForDialog(true, false) == WatchGuardGui.DialogPlan.Drive, "gizli/simge durumundaki pencere sürülür");
        Check(WatchGuardGui.PlanForDialog(false, false) == WatchGuardGui.DialogPlan.Drive, "uygulamanın açtığı pencere sürülür");

        var questions = new[] { "Örnek soru: çıkarsanız açık bağlantılar kapanır.\r\n\r\nÇıkılsın mı?", "Example question: leaving closes the tunnel.\n\nLeave?" };
        Check(WatchGuardGui.IsExitQuestionText("Örnek soru: çıkarsanız açık bağlantılar kapanır.\n\nÇıkılsın mı?", questions), "aynı soru (satır sonu farkı) eşleşir");
        Check(WatchGuardGui.IsExitQuestionText("  example question: leaving closes the tunnel. Leave?  ", questions), "boşluk ve büyük/küçük harf farkı eşleşir");
        Check(!WatchGuardGui.IsExitQuestionText("Do you want to exit and install the update?", questions), "'exit' geçen başka soru eşleşmez");
        Check(!WatchGuardGui.IsExitQuestionText("Example question: leaving closes the tunnel.", questions), "sorunun bir parçası eşleşmez");
        Check(!WatchGuardGui.IsExitQuestionText("", questions) && !WatchGuardGui.IsExitQuestionText("x", null) && !WatchGuardGui.IsExitQuestionText("", new[] { "" }), "boş metin / boş liste eşleşmez");
        Check(WatchGuardGui.NormalizeText(" a \r\n\r\n b\t") == "a b", "boşluk sadeleştirme");
    }

    static VpnProfile P(string id, string name = "x", string source = "") { return new VpnProfile { Id = id, Name = name, Host = "", Username = "", Source = source }; }

    static void ProviderRouting()
    {
        var ovpn = P("ovpn-1"); var forti = P("forti-ssl:ALFA", "ALFA", "FortiClient SSL"); var ipsec = P("forti-ipsec:BETA");
        var sophos = SophosStore.MakeProfile("ofis-ornek", "vpn.example"); var wg = WatchGuardStore.MakeProfile("vpn1.example:443", "", 1);
        Check(VpnProviders.KindOf(ovpn) == VpnKind.OpenVpn && VpnProviders.KindOf(forti) == VpnKind.Forti && VpnProviders.KindOf(ipsec) == VpnKind.Forti, "OpenVPN ve Forti (SSL/IPsec)");
        Check(VpnProviders.KindOf(sophos) == VpnKind.Sophos && VpnProviders.KindOf(wg) == VpnKind.WatchGuard, "Sophos ve WatchGuard");
        Check(VpnProviders.KindOf(null) == VpnKind.OpenVpn && VpnProviders.KindOf(new VpnProfile()) == VpnKind.OpenVpn, "boş profil OpenVPN sayılır, hata vermez");
        Check(VpnProviders.IsOpenVpn(ovpn) && !VpnProviders.IsOpenVpn(forti) && !VpnProviders.IsOpenVpn(sophos) && !VpnProviders.IsOpenVpn(wg), "yalnız OpenVPN profilleri dışa aktarılır / silinir / yinelenen aranır");
        Check(VpnProviders.IsExternal(VpnKind.Sophos) && VpnProviders.IsExternal(VpnKind.WatchGuard) && !VpnProviders.IsExternal(VpnKind.Forti) && !VpnProviders.IsExternal(VpnKind.OpenVpn), "Sophos/WatchGuard ortak yoldan sürülür");

        Check(string.Join(",", VpnProviders.SegmentNames(VpnProviders.Segments(true, true, true))) == "Tümü,OpenVPN,FortiClient,Sophos,WatchGuard", "tüm bölümler sırasıyla");
        Check(string.Join(",", VpnProviders.SegmentNames(VpnProviders.Segments(false, false, false))) == "Tümü,OpenVPN", "sağlayıcı yoksa iki bölüm");
        Check(string.Join(",", VpnProviders.SegmentNames(VpnProviders.Segments(true, false, true))) == "Tümü,OpenVPN,FortiClient,WatchGuard", "Sophos yoksa bölümü yok");
        var all = VpnProviders.Segments(true, true, true); var some = VpnProviders.Segments(false, false, true);
        Check(VpnProviders.IndexOf(some, VpnProviders.SegmentAt(all, 4)) == 2, "seçili WatchGuard bölümü sıra değişince korunur");
        Check(VpnProviders.IndexOf(some, VpnProviders.SegmentAt(all, 3)) == 0, "kaybolan bölüm Tümü'ne döner");
        Check(VpnProviders.SegmentAt(all, 9) == null && VpnProviders.SegmentAt(all, -1) == null && VpnProviders.SegmentAt(null, 0) == null, "aralık dışı indeks Tümü");
        Check(VpnProviders.InSegment(wg, null) && VpnProviders.InSegment(wg, VpnKind.WatchGuard) && !VpnProviders.InSegment(wg, VpnKind.OpenVpn) && !VpnProviders.InSegment(sophos, VpnKind.Forti), "bölüm filtresi");

        Check(VpnProviders.Asset(VpnKind.Sophos) == "provider-sophos" && VpnProviders.Asset(VpnKind.WatchGuard) == "provider-watchguard" && VpnProviders.Asset(VpnKind.Forti) == "provider-forti" && VpnProviders.Asset(VpnKind.OpenVpn) == "provider-openvpn", "rozet simgeleri");
        Check(VpnProviders.RowLabel(ovpn) == null && VpnProviders.RowLabel(forti) == "FortiClient SSL", "OpenVPN satırı etiketsiz, Forti kendi kaynağını gösterir");
        Check(VpnProviders.RowLabel(sophos) == "Sophos Connect" && VpnProviders.RowLabel(wg) == "WatchGuard SSL", "Sophos / WatchGuard satır etiketi");
        Check(VpnProviders.GroupTitle(VpnKind.Sophos) == "SOPHOS CONNECT" && VpnProviders.GroupTitle(VpnKind.WatchGuard) == "WATCHGUARD SSL VPN" && VpnProviders.GroupTitle(VpnKind.Forti) == "FORTICLIENT VPN", "grup başlıkları");

        Check(VpnProviders.CredentialPrefix(VpnKind.Sophos) == SophosStore.CredentialPrefix && VpnProviders.CredentialPrefix(VpnKind.WatchGuard) == WatchGuardStore.CredentialPrefix && VpnProviders.CredentialPrefix(VpnKind.Forti) == null, "kimlik bilgisi önekleri");
        Check(VpnProviders.CredentialKey(sophos) == "ofis-ornek" && VpnProviders.CredentialKey(wg) == "vpn1.example:443" && VpnProviders.CredentialKey(forti) == "ALFA", "kimlik bilgisi / durum anahtarı");
    }

    static void ProviderState()
    {
        var a = WatchGuardStore.MakeProfile("vpn1.example:443", "", 2); var b = WatchGuardStore.MakeProfile("vpn2.example:10443", "", 2);
        var two = new List<VpnProfile> { a, b }; var one = new List<VpnProfile> { a };
        Func<VpnProfile, string> key = VpnProviders.CredentialKey;
        Check(VpnProviders.ResolveConnectedId("VPN2.example:10443", two, key, null, null) == b.Id, "sunucu adı profile eşlenir");
        Check(VpnProviders.ResolveConnectedId("vpn3.example:1", two, key, null, null) == null, "bilinmeyen ad hiçbir satırı bağlı yapmaz");
        Check(VpnProviders.ResolveConnectedId(null, two, key, a.Id, a.Id) == null, "null: bağlı değil");
        Check(VpnProviders.ResolveConnectedId("", two, key, b.Id, a.Id) == b.Id, "adsız bağlantı: gösterilen profil kalır");
        Check(VpnProviders.ResolveConnectedId("", two, key, null, a.Id) == a.Id, "adsız bağlantı: bağlanılan profil");
        Check(VpnProviders.ResolveConnectedId("", one, key, null, null) == a.Id && VpnProviders.ResolveConnectedId("", two, key, null, null) == null, "adsız bağlantı: tek profil, yoksa belirsiz");
        Check(VpnProviders.ResolveConnectedId("", two, key, "watchguard-ssl:gone", null) == null, "listede olmayan eski kimlik kullanılmaz");
        Check(VpnProviders.ResolveConnectedId("x", new List<VpnProfile>(), key, null, null) == null && VpnProviders.ResolveConnectedId("x", null, key, null, null) == null, "profil yok");

        Check(!VpnProviders.DuePoll(VpnKind.Sophos, true, true, 9.9) && VpnProviders.DuePoll(VpnKind.Sophos, false, false, 10), "Sophos en fazla 10 sn'de bir (meşgulken de)");
        Check(VpnProviders.DuePoll(VpnKind.WatchGuard, true, false, 2) && !VpnProviders.DuePoll(VpnKind.WatchGuard, true, false, 1.5), "WatchGuard işlem sürerken 2 sn");
        Check(!VpnProviders.DuePoll(VpnKind.WatchGuard, false, false, 9) && VpnProviders.DuePoll(VpnKind.WatchGuard, false, false, 10), "WatchGuard boşta 10 sn");
        Check(VpnProviders.DuePoll(VpnKind.WatchGuard, false, true, 1) && !VpnProviders.DuePoll(VpnKind.WatchGuard, false, true, 0.5), "WatchGuard komut bitince hemen");

        var s = SophosStore.MakeProfile("ofis", ""); var f = P("forti-ssl:ALFA"); var o = P("ovpn-1"); o.LastConnected = 5;
        var starts = new Dictionary<string, long> { { a.Id, 100 }, { s.Id, 200 }, { f.Id, 300 }, { o.Id, 400 } };
        VpnProviders.ApplyLastConnected(new[] { a, b, s, f, o }, starts);
        Check(a.LastConnected == 100 && s.LastConnected == 200 && b.LastConnected == 0, "Sophos / WatchGuard son bağlanma geçmişten");
        Check(f.LastConnected == 0 && o.LastConnected == 5, "Forti ve OpenVPN'e dokunulmaz");
    }

    static void ReachabilityEndpoints()
    {
        var wg = Reachability.FromHint("watchguard-ssl:vpn1.example:10443", "vpn1.example:10443");
        Check(wg != null && wg.Host == "vpn1.example" && wg.Port == 10443 && wg.Proto == "tcp", "WatchGuard host:port → TCP");
        var wg2 = Reachability.FromHint("watchguard-ssl:vpn1.example", "vpn1.example");
        Check(wg2.Port == 443 && wg2.Proto == "tcp", "WatchGuard portsuz → TCP 443");
        var fo = Reachability.FromHint("forti-ssl:X", "gw.example");
        Check(fo.Port == 443 && fo.Proto == "tcp", "Forti portsuz → TCP 443 (değişmedi)");
        var so = Reachability.FromHint("sophos:ofis", "gw.example");
        Check(so.Host == "gw.example" && so.Proto == "udp", "Sophos portsuz → DNS + ping");
        var so2 = Reachability.FromHint("sophos:ofis", "gw.example:8443");
        Check(so2.Port == 8443 && so2.Proto == "tcp", "Sophos host:port → TCP");
        Check(Reachability.FromHint("sophos:ofis", "") == null && Reachability.FromHint(null, null) == null, "host yoksa test yok");
    }

    static void ProviderSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "ovpnqs-settings-" + Guid.NewGuid().ToString("N") + ".json");
        SettingsStore.PathOverride = path;
        try
        {
            var d = SettingsStore.Load();
            Check(d.ShowForti && d.ShowSophos && d.ShowWatchGuard, "varsayılan: tüm sağlayıcılar açık");
            d.ShowForti = false; d.ShowSophos = false; d.ShowWatchGuard = true;
            SettingsStore.Save(d);
            var back = SettingsStore.Load();
            Check(!back.ShowForti && !back.ShowSophos && back.ShowWatchGuard, "anahtarlar kaydedilip geri okunur");
            File.WriteAllText(path, "{\"ShowSophos\":\"evet\"}");
            Check(SettingsStore.Load().ShowSophos, "yanlış türde değer varsayılana döner");
        }
        finally
        {
            SettingsStore.PathOverride = null;
            try { File.Delete(path); } catch { }
        }
    }

    static void DemoProviders()
    {
        var rows = Demo.Profiles();
        var kinds = new HashSet<VpnKind>();
        foreach (var p in rows) kinds.Add(VpnProviders.KindOf(p));
        Check(kinds.Count == 4, "demo dört sağlayıcıdan da satır içerir");
        var fake = true;
        foreach (var p in rows) if (p.Host.Length > 0 && !p.Host.Split(':')[0].EndsWith(".example", StringComparison.Ordinal)) fake = false;
        Check(fake, "demo sunucuları kurgusal (.example)");
        var reach = 0;
        foreach (var p in rows) if (Demo.Reach(p.Id) != ReachState.Unknown) reach++;
        Check(reach == rows.Count, "her demo satırının erişim durumu var");
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

    // ---------- Updates ----------

    static void UpdateVersions()
    {
        Check(UpdateLogic.CompareVersions("2.10.0", "2.9.0") > 0, "2.10.0 > 2.9.0 (sayısal)");
        Check(UpdateLogic.CompareVersions("v2.1.0", "2.1.0") == 0, "v öneki yok sayılır");
        Check(UpdateLogic.CompareVersions("2.0", "2.0.0") == 0, "2.0 = 2.0.0");
        Check(UpdateLogic.CompareVersions("2.0.0", "2.0.1") < 0, "2.0.0 < 2.0.1");
        Check(UpdateLogic.CompareVersions("3.0.0", "2.99.99") > 0, "3.0.0 > 2.99.99");
        Check(UpdateLogic.NormalizeTag("v2.1.0") == "2.1.0", "etiket v2.1.0 -> 2.1.0");
        Check(UpdateLogic.NormalizeTag("v2.1.0-beta") == null && UpdateLogic.NormalizeTag("nightly") == null && UpdateLogic.NormalizeTag("") == null, "geçersiz etiketler reddedilir");
        Check(UpdateLogic.IsNewer("v2.1.0", "2.0.0") && !UpdateLogic.IsNewer("v2.0.0", "2.0.0") && !UpdateLogic.IsNewer("1.9.9", "2.0.0"), "IsNewer");
        int[] parts;
        Check(UpdateLogic.TryParseVersion(UpdateLogic.CurrentVersion, out parts), "çalışan sürüm okunur (" + UpdateLogic.CurrentVersion + ")");
    }

    static string ReleaseJson(string tag, bool prerelease, bool draft, bool zip, bool sha, string host)
    {
        var assets = new List<string>();
        var b = host + "/bymrhan/vpn-manager/releases/download/" + tag + "/";
        if (zip) assets.Add("{\"name\":\"OpenVPN-Hizli-Arama-Kurulum.zip\",\"browser_download_url\":\"" + b + "OpenVPN-Hizli-Arama-Kurulum.zip\"}");
        if (sha) assets.Add("{\"name\":\"OpenVPN-Hizli-Arama-Kurulum.zip.sha256\",\"browser_download_url\":\"" + b + "OpenVPN-Hizli-Arama-Kurulum.zip.sha256\"}");
        assets.Add("{\"name\":\"notlar.txt\",\"browser_download_url\":\"" + b + "notlar.txt\"}");
        return "{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/bymrhan/vpn-manager/releases/tag/" + tag + "\"," +
               "\"body\":\"## Yenilikler\\n- Örnek değişiklik\",\"prerelease\":" + (prerelease ? "true" : "false") + ",\"draft\":" + (draft ? "true" : "false") +
               ",\"assets\":[" + string.Join(",", assets) + "]}";
    }

    static void UpdateReleaseJson()
    {
        string problem;
        var r = UpdateLogic.ParseRelease(ReleaseJson("v2.1.0", false, false, true, true, "https://github.com"), out problem);
        Check(r != null && r.Version == "2.1.0" && r.Tag == "v2.1.0", "etiket ve sürüm okunur");
        Check(r != null && r.HtmlUrl == "https://github.com/bymrhan/vpn-manager/releases/tag/v2.1.0" && r.Notes.Contains("Örnek değişiklik"), "sürüm sayfası ve notlar okunur");
        Check(r != null && r.CanInstall && r.ZipUrl.EndsWith("/v2.1.0/OpenVPN-Hizli-Arama-Kurulum.zip") && r.ShaUrl.EndsWith(".zip.sha256"), "paket ve SHA-256 dosyası bulunur");
        Check(UpdateLogic.IsOffered(r) && UpdateLogic.ShouldOffer(r, "2.0.0", "", false), "yeni sürüm önerilir");
        Check(!UpdateLogic.ShouldOffer(r, "2.1.0", "", false) && !UpdateLogic.ShouldOffer(r, "2.2.0", "", false), "aynı / eski sürüm önerilmez");
        Check(!UpdateLogic.ShouldOffer(r, "2.0.0", "2.1.0", false) && UpdateLogic.ShouldOffer(r, "2.0.0", "2.1.0", true), "atlanan sürüm yalnızca elle denetimde gösterilir");

        var pre = UpdateLogic.ParseRelease(ReleaseJson("v2.2.0", true, false, true, true, "https://github.com"), out problem);
        Check(pre != null && !UpdateLogic.IsOffered(pre) && !UpdateLogic.ShouldOffer(pre, "2.0.0", "", true), "ön sürüm yok sayılır");
        var draft = UpdateLogic.ParseRelease(ReleaseJson("v2.2.0", false, true, true, true, "https://github.com"), out problem);
        Check(draft != null && !UpdateLogic.IsOffered(draft), "taslak yok sayılır");

        var noSha = UpdateLogic.ParseRelease(ReleaseJson("v2.1.0", false, false, true, false, "https://github.com"), out problem);
        Check(noSha != null && !noSha.CanInstall && noSha.ShaUrl == null, "SHA-256 dosyası yoksa kurulmaz");
        var noZip = UpdateLogic.ParseRelease(ReleaseJson("v2.1.0", false, false, false, true, "https://github.com"), out problem);
        Check(noZip != null && !noZip.CanInstall && noZip.ZipUrl == null, "paket yoksa kurulmaz");
        var foreign = UpdateLogic.ParseRelease(ReleaseJson("v2.1.0", false, false, true, true, "https://mirror.example"), out problem);
        Check(foreign != null && !foreign.CanInstall, "başka sunucudaki dosyalar kabul edilmez");
        var plain = UpdateLogic.ParseRelease(ReleaseJson("v2.1.0", false, false, true, true, "http://github.com"), out problem);
        Check(plain != null && !plain.CanInstall, "HTTPS olmayan adres kabul edilmez");

        Check(UpdateLogic.ParseRelease(ReleaseJson("nightly", false, false, true, true, "https://github.com"), out problem) == null && problem != null, "geçersiz etiket: sürüm yok");
        Check(UpdateLogic.ParseRelease("{bozuk", out problem) == null && problem != null, "bozuk JSON: sürüm yok");
        var odd = UpdateLogic.ParseRelease("{\"tag_name\":\"v2.1.0\",\"html_url\":\"https://phish.example/x\"}", out problem);
        Check(odd != null && odd.HtmlUrl == UpdateLogic.ReleasesPage && !odd.CanInstall, "yabancı sürüm sayfası adresi yerine depo sayfası");
        Check(UpdateLogic.NotesPreview("## Yenilikler\n- Bir\n- İki\n- Üç\n- Dört", 200) == "Yenilikler · Bir · İki", "sürüm notu önizlemesi");
    }

    static void UpdateSha256()
    {
        var h = new string('a', 32) + new string('B', 32);
        Check(UpdateLogic.ParseSha256(h + "  OpenVPN-Hizli-Arama-Kurulum.zip\n") == h.ToLowerInvariant(), "sha256sum biçimi");
        Check(UpdateLogic.ParseSha256(h) == h.ToLowerInvariant(), "yalnızca özet");
        Check(UpdateLogic.ParseSha256("yok") == null && UpdateLogic.ParseSha256("") == null && UpdateLogic.ParseSha256(new string('a', 65)) == null, "özet yoksa null");
        Check(UpdateLogic.Sha256Line(h, "x.zip") == h.ToLowerInvariant() + "  x.zip\n", "yazılan satır sürüm dosyasıyla aynı biçimde");
    }

    static void UpdateDue()
    {
        var now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc); var never = DateTime.MinValue;
        Check(UpdateLogic.IsDue("startup", now.AddMinutes(-1), never, false, now) && !UpdateLogic.IsDue("startup", never, never, true, now), "açılışta: her çalıştırmada bir kez");
        Check(UpdateLogic.IsDue("daily", never, never, false, now), "günlük: hiç denetlenmediyse hemen");
        Check(!UpdateLogic.IsDue("daily", now.AddHours(-23), never, true, now) && UpdateLogic.IsDue("daily", now.AddHours(-24), never, true, now), "günlük: 24 saatte bir");
        Check(!UpdateLogic.IsDue("weekly", now.AddDays(-6), never, true, now) && UpdateLogic.IsDue("weekly", now.AddDays(-7), never, true, now), "haftalık: 7 günde bir");
        Check(UpdateLogic.IsDue("daily", now.AddDays(2), never, true, now), "saat geri alındıysa denetlenir");
        Check(!UpdateLogic.IsDue("daily", never, now.AddMinutes(-10), true, now) && UpdateLogic.IsDue("daily", never, now.AddMinutes(-61), true, now), "başarısız denetim 1 saat sonra yeniden denenir");
    }

    static void UpdateIdleAndInstall()
    {
        Check(UpdateLogic.IsIdle(false, 0, false, false), "panel gizli: boşta");
        Check(!UpdateLogic.IsIdle(true, 30000, false, false) && UpdateLogic.IsIdle(true, 120000, false, false), "panel açık: 2 dakika girişsiz kalınca boşta");
        Check(!UpdateLogic.IsIdle(false, 999999, true, false), "bağlanma / kesme sürerken kurulmaz");
        Check(!UpdateLogic.IsIdle(false, 999999, false, true), "pencere açıkken kurulmaz");
        var dir = @"C:\Users\kullanici\AppData\Local\Programs\OpenVPN Hizli Arama";
        Check(UpdateLogic.IsInstalledCopy(dir + @"\OpenVPN-Hizli-Arama.exe", dir) && UpdateLogic.IsInstalledCopy(dir.ToLowerInvariant() + @"\openvpn-hizli-arama.exe", dir + @"\"), "kurulum klasöründeki kopya");
        Check(!UpdateLogic.IsInstalledCopy(@"C:\Araclar\OpenVPN-Hizli-Arama.exe", dir) && !UpdateLogic.IsInstalledCopy(dir + @"\Baska.exe", dir) && !UpdateLogic.IsInstalledCopy(null, dir), "taşınabilir kopya kendini güncellemez");
        Check(UpdateLogic.InstallDir.EndsWith(@"\Programs\OpenVPN Hizli Arama", StringComparison.OrdinalIgnoreCase), "kurulum klasörü kur.ps1 ile aynı");
    }

    static void UpdateVerifyPackage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ovpnqs-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var zip = Path.Combine(dir, "paket.zip");
            Func<string> make = delegate
            {
                if (File.Exists(zip)) File.Delete(zip);
                using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    using (var w = new StreamWriter(z.CreateEntry("kur.ps1").Open())) w.Write("# sahte kur.ps1");
                    using (var w = new StreamWriter(z.CreateEntry("uygulama/OpenVPN-Hizli-Arama.exe").Open())) w.Write("sahte exe");
                }
                using (var sha = System.Security.Cryptography.SHA256.Create()) using (var fs = File.OpenRead(zip))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            };
            string err;
            var hash = make();
            var pkg = UpdateClient.VerifyAndExtract(zip, UpdateLogic.Sha256Line(hash, "OpenVPN-Hizli-Arama-Kurulum.zip"), dir, out err);
            Check(pkg != null && File.Exists(Path.Combine(pkg, "kur.ps1")) && File.Exists(Path.Combine(pkg, "uygulama", "OpenVPN-Hizli-Arama.exe")), "doğru özet: paket açılır");
            if (pkg != null) Directory.Delete(pkg, true);

            make();
            pkg = UpdateClient.VerifyAndExtract(zip, UpdateLogic.Sha256Line(new string('0', 64), "x.zip"), dir, out err);
            Check(pkg == null && err != null && !File.Exists(zip) && !Directory.Exists(Path.Combine(dir, "paket")), "yanlış özet: hiçbir şey açılmaz, paket silinir");

            make();
            pkg = UpdateClient.VerifyAndExtract(zip, "", dir, out err);
            Check(pkg == null && err != null && !Directory.Exists(Path.Combine(dir, "paket")), "özet yok: güncelleme durur");

            string dl;
            Check(UpdateClient.Download(new ReleaseInfo { Tag = "v9.9.9", Version = "9.9.9", ZipUrl = "https://mirror.example/a.zip", ShaUrl = "https://mirror.example/a.zip.sha256" }, out dl) == null && dl != null, "yabancı adresten indirme yapılmaz");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static void UpdateSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "ovpnqs-settings-" + Guid.NewGuid().ToString("N") + ".json");
        SettingsStore.PathOverride = path;
        try
        {
            File.WriteAllText(path, "{\"Hotkey\":\"Ctrl+Alt+V\",\"Theme\":\"light\",\"ShowForti\":true}");
            var old = SettingsStore.Load();
            Check(old.Theme == "light" && old.UpdateMode == "ask" && old.UpdateInterval == "daily" && old.LastUpdateCheckUtc == DateTime.MinValue && old.SkippedVersion == "", "eski settings.json: güncelleme varsayılanları (sor, günlük)");
            var d = AppSettings.Defaults();
            Check(d.UpdateMode == "ask" && d.UpdateInterval == "daily", "varsayılanlar");
            var when = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
            old.UpdateMode = "auto"; old.UpdateInterval = "weekly"; old.LastUpdateCheckUtc = when; old.SkippedVersion = "v2.1.0";
            SettingsStore.Save(old);
            var back = SettingsStore.Load();
            Check(back.UpdateMode == "auto" && back.UpdateInterval == "weekly" && back.LastUpdateCheckUtc == when && back.SkippedVersion == "2.1.0", "güncelleme ayarları kaydedilip geri okunur");
            File.WriteAllText(path, "{\"UpdateMode\":\"hep\",\"UpdateInterval\":5,\"SkippedVersion\":\"bozuk\",\"LastUpdateCheckUtcTicks\":\"x\"}");
            var bad = SettingsStore.Load();
            Check(bad.UpdateMode == "ask" && bad.UpdateInterval == "daily" && bad.SkippedVersion == "" && bad.LastUpdateCheckUtc == DateTime.MinValue, "geçersiz değerler varsayılana döner");
        }
        finally
        {
            SettingsStore.PathOverride = null;
            try { File.Delete(path); } catch { }
        }
    }

    static void UpdateFailureMarker()
    {
        var now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        Func<string, DateTime, string> marker = (v, t) => v + "\r\n" + t.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) + "\r\n";
        Check(!UpdateLogic.FailureMarkerBlocks(null, "2.1.0", now) && !UpdateLogic.FailureMarkerBlocks("", "2.1.0", now), "işaret yok: otomatik kurulur");
        Check(UpdateLogic.FailureMarkerBlocks(marker("2.1.0", now.AddHours(-2)), "2.1.0", now), "aynı sürüm 2 saat önce başarısız: otomatik kurulmaz");
        Check(UpdateLogic.FailureMarkerBlocks(marker("v2.1.0", now.AddHours(-23)), "2.1.0", now), "23 saat sonra hâlâ engelli (v öneki)");
        Check(!UpdateLogic.FailureMarkerBlocks(marker("2.1.0", now.AddHours(-25)), "2.1.0", now), "24 saat geçince süresi doldu");
        Check(!UpdateLogic.FailureMarkerBlocks(marker("2.0.5", now.AddHours(-1)), "2.1.0", now), "başka sürümün işareti engellemez");
        Check(UpdateLogic.FailureMarkerBlocks("2.1.0\r\nbozuk", "2.1.0", now), "okunamayan zaman: engelli sayılır");
        Check(!UpdateLogic.FailureMarkerBlocks(marker("2.1.0", now.AddDays(3)), "2.1.0", now), "gelecekte tarihli işaret yok sayılır");
        Check(!UpdateLogic.FailureMarkerBlocks("bozuk\r\n", "2.1.0", now), "bozuk işaret yok sayılır");
        var dir = Path.Combine(Path.GetTempPath(), "ovpnqs-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Check(UpdateClient.ReadFailureMarker(dir) == null, "kurulum klasöründe işaret yok");
            File.WriteAllText(Path.Combine(dir, UpdateLogic.FailureMarkerName), marker("2.1.0", now));
            Check(UpdateLogic.FailureMarkerBlocks(UpdateClient.ReadFailureMarker(dir), "2.1.0", now.AddHours(1)), "kur.ps1'in yazdığı işaret okunur");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static void UpdateZipSlip()
    {
        var root = Path.Combine(Path.GetTempPath(), "ovpnqs-zipslip-" + Guid.NewGuid().ToString("N"));
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, "paket.zip");
            using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(z.CreateEntry("kur.ps1").Open())) w.Write("# sahte kur.ps1");
                using (var w = new StreamWriter(z.CreateEntry("uygulama/OpenVPN-Hizli-Arama.exe").Open())) w.Write("sahte exe");
                using (var w = new StreamWriter(z.CreateEntry("../evil.txt").Open())) w.Write("dışarı");
                using (var w = new StreamWriter(z.CreateEntry("../../evil2.txt").Open())) w.Write("dışarı");
            }
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create()) using (var fs = File.OpenRead(zip))
                hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            string err; string pkg = null; Exception thrown = null;
            try { pkg = UpdateClient.VerifyAndExtract(zip, UpdateLogic.Sha256Line(hash, "OpenVPN-Hizli-Arama-Kurulum.zip"), work, out err); }
            catch (Exception ex) { thrown = ex; err = null; }
            Check(thrown == null && pkg == null && err != null, "doğru özetli ama ../ içeren paket reddedilir");
            Check(!File.Exists(Path.Combine(work, "evil.txt")) && !File.Exists(Path.Combine(root, "evil.txt")) && !File.Exists(Path.Combine(root, "evil2.txt")) && !File.Exists(Path.Combine(Path.GetDirectoryName(root), "evil2.txt")), "açma klasörü dışına hiçbir şey yazılmaz");
            Check(!Directory.Exists(Path.Combine(work, "paket")), "yarım açılan paket silinir");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}