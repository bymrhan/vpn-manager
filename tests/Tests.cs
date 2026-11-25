using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        Run("OpenVPN penceresi geri getirme", OpenVpnWindowRestore);
        Run("Tanılama: temizleyici sahte sırları siler", SanitizerRemovesSeeds);
        Run("Tanılama: temizleyici idempotent ve kararlı", SanitizerIdempotentAndStable);
        Run("Tanılama: temizleyici kodu bozmaz", SanitizerKeepsCode);
        Run("Tanılama: parmak izi", ErrorFingerprints);
        Run("Tanılama: rapor", ReportBuild);
        Run("Tanılama: GitHub kayıt adresi", IssueLinks);
        Run("Tanılama: yineleme denetimi", DuplicateCheck);
        Run("Tanılama: günlük döndürme", LogRotationRules);
        Run("Tanılama: sıklık sınırı", RateLimit);
        Run("Tanılama: çökme işareti", CrashMarker);
        Run("Tanılama: günlük dosyası", LogWriterFile);
        Run("Ayarlar: tanılama", DiagnosticsSettings);
        Run("Tanılama: Türkçe büyük/küçük harf", SanitizerTurkishCase);
        Run("Tanılama: kullanıcı ve alan adları", SanitizerUsersAndDomains);
        Run("Tanılama: her harf büyüklüğünde sunucu adı", SanitizerHostsAnyCase);
        Run("Tanılama: çok uzun satırlar", SanitizerLongLines);
        Run("Tanılama: diğer gizli değerler", SanitizerMoreSecrets);
        Run("Tanılama: aşırı temizleme sınırı", SanitizerNoOverRedaction);
        Run("Tanılama: çökme işareti, Sonra", CrashMarkerLater);
        Run("Sağlayıcı bölümü görünürlüğü", ProviderSegmentVisibility);
        Run("GitHub gönderimi: anahtar kodlama", IssueTokenCodec);
        Run("GitHub gönderimi: sınırlar", IssueLimitRules);
        Run("GitHub gönderimi: akışlar (sahte GitHub)", IssueClientFlows);
        Run("GitHub gönderimi: adres ve gövde korumaları", IssueClientGuards);
        Run("GitHub gönderimi: anahtar temizleme", TokenSanitizing);
        Run("GitHub gönderimi: otomatik kip kararı", AutoModeDecision);
        Run("GitHub gönderimi: güvenlik incelemesi korumaları", IssueReviewGuards);
        Run("Ayarlar: uygulama nerede görünsün (Presence, eski anahtar)", TaskbarSetting);
        Run("Tepsi / görev çubuğu kararları", TaskbarDecisions);
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

        var fm = SophosStore.FailureMessage("Sophos bağlantısı başlatılamadı", 3);
        Check(fm == "Sophos bağlantısı başlatılamadı (sccli çıkış kodu 3).", "hata metni: sabit ileti + çıkış kodu");
        Check(SophosStore.FailureMessage("Sophos bağlantısı kesilemedi", SophosStore.TimedOut) == "Sophos bağlantısı kesilemedi (sccli yanıt vermedi).", "hata metni: zaman aşımı");
        Check(SophosStore.FailureMessage("x", -1).IndexOf("-1", StringComparison.Ordinal) > 0, "negatif çıkış kodu da yazılır");
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
            Check(FortiCredentialStore.CommentFor(null) == "OpenVPN Hızlı Arama · FortiClient", "Forti açıklaması değişmedi");
            Check(FortiCredentialStore.CommentFor(SophosStore.CredentialPrefix) == "OpenVPN Hızlı Arama · Sophos Connect" && FortiCredentialStore.CommentFor(WatchGuardStore.CredentialPrefix) == "OpenVPN Hızlı Arama · WatchGuard", "Sophos / WatchGuard açıklaması kendi adıyla");
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

        Check(!WatchGuardGui.RevealActivates(true) && WatchGuardGui.RevealActivates(false), "başarılı kesme/bağlanmadan sonra kalan pencere öne getirilmez, hatada getirilir");
        Check(WatchGuardGui.OwnedBy(3, 3) && !WatchGuardGui.OwnedBy(2, 3) && WatchGuardGui.OwnedBy(2, 0), "yalnızca bu işlemin park ettiği pencere geri getirilir (işlem yoksa hepsi)");
        Check(!WatchGuardGui.AnyParked, "başlangıçta park edilmiş pencere yok");
        WatchGuardGui.RevealAllParked(); QuietWindow.Reveal(false);
        Check(!WatchGuardGui.AnyParked && !QuietWindow.IsParked, "çıkışta geri getirme: park yokken hiçbir şey yapmaz");
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

        var streak = 0;
        Check(VpnProviders.UsePoll(false, null, true, false, false, ref streak) == VpnProviders.PollUse.Keep && streak == 0, "başarısız yoklama: gösterilen durum korunur");
        Check(VpnProviders.UsePoll(false, null, false, true, false, ref streak) == VpnProviders.PollUse.Keep, "başarısız yoklama kesme sırasında da bir şey değiştirmez");
        Check(VpnProviders.UsePoll(true, null, true, false, false, ref streak) == VpnProviders.PollUse.Keep && streak == 1, "bağlıyken tek 'yok': henüz düşürülmez");
        Check(VpnProviders.UsePoll(false, null, true, false, false, ref streak) == VpnProviders.PollUse.Keep && streak == 1, "araya giren başarısız yoklama sayacı sıfırlamaz/artırmaz");
        Check(VpnProviders.UsePoll(true, null, true, false, false, ref streak) == VpnProviders.PollUse.Apply && streak == 0, "art arda iki 'yok': bağlantı düşer");
        streak = 1;
        Check(VpnProviders.UsePoll(true, "vpn1.example:443", true, false, false, ref streak) == VpnProviders.PollUse.Apply && streak == 0, "bağlı sonuç hemen uygulanır, sayaç sıfırlanır");
        Check(VpnProviders.UsePoll(true, null, false, false, false, ref streak) == VpnProviders.PollUse.Apply, "zaten bağlı değilken 'yok' hemen uygulanır");
        Check(VpnProviders.UsePoll(true, null, true, true, false, ref streak) == VpnProviders.PollUse.Apply, "kesme sürerken ilk 'yok' hemen uygulanır");
        Check(VpnProviders.UsePoll(true, "ofis", false, false, true, ref streak) == VpnProviders.PollUse.Keep, "komut bitmeden başlamış (eski) yoklama satırı yeniden bağlı yapmaz");

        var s = SophosStore.MakeProfile("ofis", ""); var f = P("forti-ssl:ALFA"); var o = P("ovpn-1"); o.LastConnected = 5;
        var starts = new Dictionary<string, long> { { a.Id, 100 }, { s.Id, 200 }, { f.Id, 300 }, { o.Id, 400 } };
        VpnProviders.ApplyLastConnected(new[] { a, b, s, f, o }, starts);
        Check(a.LastConnected == 100 && s.LastConnected == 200 && b.LastConnected == 0, "Sophos / WatchGuard son bağlanma geçmişten");
        Check(f.LastConnected == 0 && o.LastConnected == 5, "Forti ve OpenVPN'e dokunulmaz");
    }

    static void OpenVpnWindowRestore()
    {
        var t = QuietWindow.OpenVpnRestoreAfterSeconds;
        Check(t >= 8 && t <= 10, "bekleme süresi 8-10 sn");
        Check(!QuietWindow.ShouldRestoreOpenVpnWindow(t - 0.5, false), "kısa süre: pencere küçük kalır");
        Check(QuietWindow.ShouldRestoreOpenVpnWindow(t, false) && QuietWindow.ShouldRestoreOpenVpnWindow(44, false), "süre doldu, bağlanmadı: pencere geri gelir (parola / MFA)");
        Check(!QuietWindow.ShouldRestoreOpenVpnWindow(t + 5, true), "bağlandı: pencere küçük kalır");
        QuietWindow.ForgetMinimized();
        Check(!QuietWindow.HasMinimized && !QuietWindow.RestoreMinimized(), "küçültülen pencere yoksa hiçbir pencereye dokunulmaz");
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

    static void TaskbarSetting()
    {
        var path = Path.Combine(Path.GetTempPath(), "ovpnqs-settings-" + Guid.NewGuid().ToString("N") + ".json");
        SettingsStore.PathOverride = path;
        try
        {
            Check(AppSettings.Defaults().Presence == "tray", "varsayılan: tepsi");
            Check(SettingsStore.Load().Presence == "tray", "dosya yokken tepsi");
            var s = SettingsStore.Load();
            foreach (var mode in new[] { "both", "taskbar", "tray" })
            {
                s.Presence = mode;
                Check(s.Clone().Presence == mode, "Clone değeri taşır (" + mode + ")");
                SettingsStore.Save(s);
                Check(SettingsStore.Load().Presence == mode, "kaydedilip geri okunur (" + mode + ")");
                var text = File.ReadAllText(path);
                Check(text.Contains("\"Presence\":\"" + mode + "\"") && !text.Contains("ShowInTaskbar"), "dosyada yalnız Presence anahtarı var (" + mode + ")");
            }
            s.Presence = "Taskbar"; s.Normalize();
            Check(s.Presence == "tray", "Normalize: geçersiz değer (büyük harf) tepsi");
            s.Presence = null; s.Normalize();
            Check(s.Presence == "tray", "Normalize: boş değer tepsi");

            // 2.1.0 files: only the bool ShowInTaskbar
            File.WriteAllText(path, "{\"Hotkey\":\"Ctrl+Alt+V\",\"AutoStart\":true,\"ShowInTaskbar\":true}");
            var legacy = SettingsStore.Load();
            Check(legacy.Presence == "both" && legacy.AutoStart, "eski ShowInTaskbar=true: tepsi + düğme, diğerleri korunur");
            File.WriteAllText(path, "{\"ShowInTaskbar\":false}");
            Check(SettingsStore.Load().Presence == "tray", "eski ShowInTaskbar=false: tepsi");
            File.WriteAllText(path, "{\"Hotkey\":\"Ctrl+Alt+V\",\"AutoStart\":true}");
            var old = SettingsStore.Load();
            Check(old.Presence == "tray" && old.AutoStart, "anahtarı olmayan eski dosya: tepsi, diğerleri korunur");
            File.WriteAllText(path, "{\"ShowInTaskbar\":\"evet\"}");
            Check(SettingsStore.Load().Presence == "tray", "eski anahtar yanlış türde (metin): tepsi");
            File.WriteAllText(path, "{\"ShowInTaskbar\":1}");
            Check(SettingsStore.Load().Presence == "tray", "eski anahtar yanlış türde (sayı): tepsi");
            File.WriteAllText(path, "{\"ShowInTaskbar\":true,\"Presence\":\"taskbar\"}");
            Check(SettingsStore.Load().Presence == "taskbar", "ikisi de varsa Presence geçerli");
            File.WriteAllText(path, "{\"ShowInTaskbar\":true,\"Presence\":\"tray\"}");
            Check(SettingsStore.Load().Presence == "tray", "Presence=tray eski anahtarı ezer");
            File.WriteAllText(path, "{\"ShowInTaskbar\":true,\"Presence\":\"dugme\"}");
            Check(SettingsStore.Load().Presence == "tray", "geçersiz Presence: tepsi (eski anahtara dönülmez)");
            File.WriteAllText(path, "{\"Presence\":3}");
            Check(SettingsStore.Load().Presence == "tray", "yanlış türde Presence (sayı): tepsi");
            // migrated file is written back with Presence only
            File.WriteAllText(path, "{\"ShowInTaskbar\":true}");
            SettingsStore.Save(SettingsStore.Load());
            var migrated = File.ReadAllText(path);
            Check(migrated.Contains("\"Presence\":\"both\"") && !migrated.Contains("ShowInTaskbar"), "taşınan dosya Presence ile yazılır");
            Check(SettingsStore.Load().Presence == "both", "taşınan değer kalıcı");
        }
        finally
        {
            SettingsStore.PathOverride = null;
            try { File.Delete(path); } catch { }
        }
    }

    static void TaskbarDecisions()
    {
        const int APP = TaskbarButton.WS_EX_APPWINDOW, TOOL = TaskbarButton.WS_EX_TOOLWINDOW, OTHER = 0x100 | 0x8; // WS_EX_WINDOWEDGE | WS_EX_TOPMOST
        Check(PresenceMode.Effective("both", false, false) == "both", "normal çalışma: ayar uygulanır");
        Check(PresenceMode.Effective("taskbar", false, false) == "taskbar", "normal çalışma: görev çubuğu");
        Check(PresenceMode.Effective("taskbar", true, false) == "tray" && PresenceMode.Effective("both", true, false) == "tray", "demo, ayar dosyası verilmeden: tepsi");
        Check(PresenceMode.Effective("taskbar", true, true) == "taskbar", "demo, --settings ile: ayar uygulanır");
        Check(PresenceMode.Effective("x", false, false) == "tray", "geçersiz ayar: tepsi");
        Check(PresenceMode.FromLegacy(true) == "both" && PresenceMode.FromLegacy(false) == "tray", "eski anahtar: true = tepsi + düğme");
        Check(PresenceMode.TrayIcon("tray") && PresenceMode.TrayIcon("both") && !PresenceMode.TrayIcon("taskbar"), "tepsi simgesi: görev çubuğu kipinde gizli");
        Check(!PresenceMode.Button("tray") && PresenceMode.Button("both") && PresenceMode.Button("taskbar"), "düğme: tepsi + düğme ve görev çubuğu");
        Check(!PresenceMode.HideMinimizes("tray") && !PresenceMode.HideMinimizes("both") && PresenceMode.HideMinimizes("taskbar"), "gizle = küçült: yalnız görev çubuğu");
        Check(!PresenceMode.CloseExits("tray") && !PresenceMode.CloseExits("both") && PresenceMode.CloseExits("taskbar"), "kapat = çık: yalnız görev çubuğu");
        Check(PresenceMode.TrayIcon(null) && !PresenceMode.Button(null) && !PresenceMode.CloseExits("bogus"), "geçersiz kip tepsi gibi davranır");
        Check(PresenceMode.StartsMinimized("taskbar", true, false), "görev çubuğu + --background: küçültülmüş başlar");
        Check(!PresenceMode.StartsMinimized("taskbar", true, true), "OpenVPN açıkken yapışık gösterilir");
        Check(!PresenceMode.StartsMinimized("taskbar", false, false), "normal başlangıç: açık");
        Check(!PresenceMode.StartsMinimized("tray", true, false) && !PresenceMode.StartsMinimized("both", true, false), "tepsi kiplerinde --background gizli başlar");
        const int MIN = TaskbarButton.WS_MINIMIZEBOX, POPUP = unchecked((int)0x80000000) | 0x02000000; // WS_POPUP | WS_CLIPCHILDREN
        Check(TaskbarButton.Style(POPUP, true) == (POPUP | MIN) && TaskbarButton.Style(POPUP | MIN, false) == POPUP, "küçültme kutusu açılıp kapanır, diğer bitler kalır");
        Check(TaskbarButton.Style(POPUP | MIN, true) == (POPUP | MIN), "zaten açık: değişmez");
        Check(SettingsForm.PresenceText("taskbar").StartsWith("Görev çubuğu:") && SettingsForm.PresenceText("both").StartsWith("Tepsi + düğme:") && SettingsForm.PresenceText("tray").StartsWith("Tepsi:"), "ayar açıklaması seçime göre değişir");
        Check(TaskbarButton.ExStyle(OTHER, true) == (OTHER | APP), "açmak APPWINDOW ekler, diğer bitler kalır");
        Check(TaskbarButton.ExStyle(OTHER | TOOL, true) == (OTHER | APP), "açmak TOOLWINDOW'u kaldırır");
        Check(TaskbarButton.ExStyle(OTHER | APP, false) == OTHER, "kapatmak yalnız APPWINDOW'u kaldırır");
        Check(TaskbarButton.ExStyle(OTHER | TOOL | APP, false) == (OTHER | TOOL), "kapatmak TOOLWINDOW'a dokunmaz");
        Check(TaskbarButton.ExStyle(TaskbarButton.ExStyle(OTHER, true), false) == OTHER, "aç / kapat başa döner");
        Check(TaskbarButton.Plan(OTHER | APP, true, true) == TaskbarChange.None, "zaten açık: hiçbir şey yapılmaz");
        Check(TaskbarButton.Plan(OTHER, false, true) == TaskbarChange.None, "zaten kapalı: hiçbir şey yapılmaz");
        Check(TaskbarButton.Plan(OTHER, true, false) == TaskbarChange.StyleOnly, "gizli pencere: yalnız stil (yanıp sönme yok)");
        Check(TaskbarButton.Plan(OTHER | APP, false, false) == TaskbarChange.StyleOnly, "gizli pencere kapatma: yalnız stil");
        Check(TaskbarButton.Plan(OTHER, true, true) == TaskbarChange.Reshow, "görünür pencere: gizle / göster");
        Check(TaskbarButton.Plan(OTHER | APP, false, true) == TaskbarChange.Reshow, "görünür pencere kapatma: gizle / göster");
        Check(TaskbarButton.Apply(IntPtr.Zero, true, true) == TaskbarChange.None, "tutamaç yoksa hiçbir şey yapılmaz");

        // A real (hidden, never shown) owned form: the handle survives several flips.
        using (var f = new System.Windows.Forms.Form { ShowInTaskbar = false })
        {
            var h = f.Handle;
            for (var i = 0; i < 4; i++)
            {
                var on = i % 2 == 0;
                Check(TaskbarButton.Apply(h, on, false) == TaskbarChange.StyleOnly && TaskbarButton.Has(h) == on, "gizli formda bit " + (on ? "açıldı" : "kapandı") + " (" + (i + 1) + ")");
            }
            Check(f.Handle == h && f.IsHandleCreated && !f.RecreatingHandle, "tutamaç değişmedi");
            Check(TaskbarButton.Apply(h, false, false) == TaskbarChange.None, "aynı değer ikinci kez: değişiklik yok");
            for (var i = 0; i < 4; i++)
            {
                var on = i % 2 == 0;
                TaskbarButton.SetMinimizeBox(h, on);
                Check(TaskbarButton.HasMinimizeBox(h) == on, "küçültme kutusu " + (on ? "açıldı" : "kapandı") + " (" + (i + 1) + ")");
            }
            Check(f.Handle == h && !TaskbarButton.Iconic(h), "küçültme kutusu tutamacı değiştirmez; gizli form küçültülmüş değil");
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

    // ---------- Diagnostics (modules\Diagnostics.cs) ----------
    // All data below is fictional (.example hosts, documentation IP ranges, made-up names).

    // " (fark: …)" around the first differing character, for failure messages.
    static string Diff(string a, string b)
    {
        if (a == b) return "";
        var i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        var s = Math.Max(0, i - 30);
        return " (fark: «" + a.Substring(s, Math.Min(80, a.Length - s)) + "» / «" + b.Substring(s, Math.Min(80, b.Length - s)) + "»)";
    }

    static LogSanitizer SeededSanitizer()
    {
        var s = new LogSanitizer();
        s.AddProfile(new VpnProfile { Name = "Acme Lojistik Merkez", Host = "vpn.acme-lojistik.example", Username = "ayse.demir" });
        s.AddProfile(new VpnProfile { Name = "Beta Enerji", Host = "198.51.100.7:1194", Username = "beta.vpnuser" });
        s.AddHost("wg-gw.gamma-tekstil.example");                       // a WatchGuard server entry
        s.AddUser("ayse.demir");
        s.AddMachine("ACME-PC-042");
        s.AddDomain("ACMECORP");
        s.AddPathRoot(@"C:\Users\ayse.demir\AppData\Local", "%LOCALAPPDATA%");
        s.AddPathRoot(@"C:\Users\ayse.demir\AppData\Roaming", "%APPDATA%");
        s.AddPathRoot(@"C:\Users\ayse.demir", "~");
        return s;
    }

    const string SeedLog =
        "2026-11-25 14:03:11.123 INFO  forti connect requested kind=Forti profile=Acme Lojistik Merkez host=vpn.acme-lojistik.example\n" +
        "2026-11-25 14:03:12.000 WARN  sophos sccli enable -p Hunter2!Gizli --user beta.vpnuser\n" +
        "2026-11-25 14:03:12.500 ERROR app password=Hunter2!Gizli token=ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ01 secret: \"s3cr3t value\"\n" +
        "Authorization: Basic QWxhZGRpbjpvcGVuIHNlc2FtZQ==\n" +
        "curl -H \"Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.c2lnbmF0dXJl\" https://admin:Hunter2!Gizli@fw.delta-insaat.example/api\n" +
        "mail to ayse.demir@acme-lojistik.example and it@delta-insaat.example\n" +
        "server 203.0.113.45:443, backup 198.51.100.7, v6 2001:db8::42 and fe80::1%12\n" +
        "Beta Enerji connected via wg-gw.gamma-tekstil.example on ACME-PC-042 (ACMECORP\\ayse.demir)\n" +
        "C:\\Users\\ayse.demir\\AppData\\Roaming\\OpenVpnQuickSearch\\settings.json and C:\\Users\\mehmet.kaya\\Desktop\\epsilon.ovpn\n" +
        "share \\\\fs01.zeta-gida.example\\vpn\\profiles\n" +
        "blob deadbeefdeadbeefdeadbeefdeadbeef00112233 key MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC7\n" +
        "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcw\nZmFrZSBrZXkgZGF0YQ==\n-----END PRIVATE KEY-----\n" +
        "<key>\nZmFrZSBpbmxpbmUga2V5\n</key>\n" +
        "unknown host eta-holding.example.com.tr answered\n";

    static readonly string[] Seeds = {
        "acme", "lojistik", "Beta Enerji", "beta.vpnuser", "gamma", "delta", "zeta", "eta-holding", "ayse", "demir", "mehmet", "kaya", "epsilon",
        "ACME-PC-042", "Hunter2", "ghp_", "QWxhZGRpbjpvcGVu", "eyJhbGci", "s3cr3t", "203.0.113.45", "198.51.100.7", "2001:db8", "fe80",
        "deadbeef", "MIIEvQ", "ZmFrZSB", "fs01", "it@" };

    static void SanitizerRemovesSeeds()
    {
        var s = SeededSanitizer();
        var outText = s.Sanitize(SeedLog);
        var leaked = Seeds.Where(x => outText.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        Check(leaked.Count == 0, "sahte sırların hiçbiri kalmaz" + (leaked.Count > 0 ? " (kalan: " + string.Join(", ", leaked) + ")\n" + outText : ""));
        Check(outText.Contains("<profil-1>") && outText.Contains("<sunucu-1>") && outText.Contains("<ip-") && outText.Contains("<kullanici>") && outText.Contains("<makine>") && outText.Contains("<alan>"), "bilinen değerler yer tutucuya döner");
        Check(outText.Contains("%APPDATA%\\OpenVpnQuickSearch\\settings.json") && outText.Contains("~\\Desktop\\epsilon.ovpn") == false && outText.Contains("~\\Desktop"), "kullanıcı klasörleri kısaltılır, dosya adı kalır");
        Check(outText.Contains("<pem>") && outText.Contains("<anahtar>") && outText.Contains("<gizli>") && outText.Contains("<eposta>") && outText.Contains("<blob>"), "PEM, satır içi anahtar, parola, e-posta, blob");
        // a fresh sanitizer without known values still catches the pattern-based ones
        var bare = new LogSanitizer().Sanitize("x 203.0.113.9 y vpn.kappa.example z password=abc123 u ops@kappa.example C:\\Users\\lambda\\x.txt \\\\nas01\\s");
        Check(!bare.Contains("203.0.113.9") && !bare.Contains("kappa") && !bare.Contains("abc123") && !bare.Contains("lambda") && !bare.Contains("nas01"), "bilinmeyen değerler kalıplarla yakalanır: " + bare);
    }

    static void SanitizerIdempotentAndStable()
    {
        var s = SeededSanitizer();
        var once = s.Sanitize(SeedLog);
        Check(s.Sanitize(once) == once, "aynı örnekle ikinci geçiş değiştirmez" + Diff(once, s.Sanitize(once)));
        Check(SeededSanitizer().Sanitize(once) == once && new LogSanitizer().Sanitize(once) == once, "yeni örnekle de değiştirmez (idempotent)" + Diff(once, new LogSanitizer().Sanitize(once)));
        var t = new LogSanitizer();
        var a = t.Sanitize("a vpn.iota.example b vpn.theta.example c vpn.iota.example d 192.0.2.10 e 192.0.2.10 f 192.0.2.11");
        Check(a == "a <sunucu-1> b <sunucu-2> c <sunucu-1> d <ip-1> e <ip-1> f <ip-2>", "yer tutucular rapor içinde kararlı: " + a);
        Check(t.Sanitize("again vpn.theta.example") == "again <sunucu-2>", "aynı örnekte sonraki çağrıda da aynı numara");
        var p = new LogSanitizer(); p.AddProfileName("Omega"); p.AddProfileName("Omega Lojistik"); p.AddProfileName("ab");
        Check(p.Sanitize("Omega Lojistik ve Omega ve ab") == "<profil-2> ve <profil-1> ve ab", "uzun ad önce; 3 karakterden kısa değerler yok sayılır");
        var u = new LogSanitizer(); u.AddUser("ali");
        Check(u.Sanitize("ali ve Kalinin ve ALI") == "ali ve Kalinin ve ALI" && u.KnownCount == 0, "4 karakterden kısa değerler tohum olmaz");
    }

    static void SanitizerKeepsCode()
    {
        var s = new LogSanitizer(); s.AddProfileName("Form"); s.AddProfileName("Connect");
        var frame = "   at SearchForm.ConnectForti in OpenVpnQuickSearch.cs:line 1073";
        Check(s.Sanitize(frame) == frame, "yığın satırı (yalnızca tanımlayıcılar) olduğu gibi kalır");
        var plain = "System.IO.IOException at System.Windows.Forms.Control.Invoke, see Connection.cs and app-20261125.log, api.github.com, sürüm 2.1.0, 2026-11-25 14:03:11.123, clr 4.0.30319.42000, scale=1.50";
        var n = new LogSanitizer().Sanitize(plain);
        Check(n == plain, "kod adları, dosya adları, GitHub, sürümler ve zaman damgaları bozulmaz: " + n);
    }

    static Exception Thrown(Action a) { try { a(); } catch (Exception ex) { return ex; } return null; }
    static int failDepth;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void FailDeep(int n) { if (n <= 0) throw new InvalidOperationException("could not open 'C:\\Users\\ayse.demir\\x.ovpn' on vpn.acme-lojistik.example"); FailDeep(n - 1); failDepth++; } // not a tail call: every level keeps its frame

    static void ErrorFingerprints()
    {
        var a = new ErrorInfo { Type = "System.NullReferenceException" };
        a.Frames.Add(new ErrorFrame { Method = "SearchForm.ConnectForti", File = "OpenVpnQuickSearch.cs", Line = 10 });
        a.Frames.Add(new ErrorFrame { Method = "SearchForm+<>c__DisplayClass1a.<ConnectForti>b__19", File = "OpenVpnQuickSearch.cs", Line = 20 });
        a.Frames.Add(new ErrorFrame { Method = "ProfileList.OnMouseUp", File = "GlassList.cs", Line = 30 });
        a.Frames.Add(new ErrorFrame { Method = "System.Windows.Forms.Control.WndProc" });
        var b = new ErrorInfo { Type = a.Type };
        b.Frames.Add(new ErrorFrame { Method = "SearchForm.ConnectForti", File = "Other.cs", Line = 999 });
        b.Frames.Add(new ErrorFrame { Method = "SearchForm+<>c__DisplayClass2f.<ConnectForti>b__7" });
        b.Frames.Add(new ErrorFrame { Method = "ProfileList.OnMouseUp", Line = 31 });
        b.Frames.Add(new ErrorFrame { Method = "Something.Else" });
        var fa = ErrorFingerprint.Of(a); var fb = ErrorFingerprint.Of(b);
        Check(fa != null && System.Text.RegularExpressions.Regex.IsMatch(fa, "^[0-9a-f]{8}$"), "parmak izi 8 onaltılık karakter: " + fa);
        Check(fa == fb, "dosya yolu, satır numarası, derleyici sayaçları ve 4. çerçeve parmak izini değiştirmez");
        b.Frames[2].Method = "ProfileList.OnMouseDown";
        Check(ErrorFingerprint.Of(b) != fa, "ilk 3 yöntemden biri değişince parmak izi değişir");
        var c = new ErrorInfo { Type = "System.ArgumentException", Frames = a.Frames };
        Check(ErrorFingerprint.Of(c) != fa, "istisna türü parmak izine girer");
        var wrapped = new ErrorInfo { Type = "System.Reflection.TargetInvocationException", Inner = a };
        wrapped.Frames.Add(new ErrorFrame { Method = "System.RuntimeMethodHandle.InvokeMethod" });
        Check(ErrorFingerprint.Of(wrapped) == fa, "sarmalayan istisnada en içteki hata esas alınır");

        var ex1 = Thrown(() => FailDeep(2)); var ex2 = Thrown(() => FailDeep(2));
        var i1 = ErrorInfo.From(ex1);
        Check(ErrorFingerprint.Of(i1) == ErrorFingerprint.Of(ErrorInfo.From(ex2)), "aynı yerden atılan gerçek istisnalar aynı parmak izi");
        Check(i1.Frames.Count >= 3 && i1.Frames.All(f => f.File == null || (f.File.IndexOf('\\') < 0 && f.File.IndexOf('/') < 0)), "çerçevelerde mutlak yol yok: " + string.Join(" | ", i1.Frames.Select(f => f.ToString()).ToArray()));
        Check(i1.Frames[0].Method == "Tests.FailDeep", "çerçeve tür + yöntem adı: " + i1.Frames[0].Method);
        var nested = ErrorInfo.From(new InvalidOperationException("dış", ex1));
        Check(nested.Inner != null && nested.Root.Type == "System.InvalidOperationException" && nested.Inner.Frames.Count > 0, "iç istisna korunur");
    }

    static ReportEnvironment FakeEnv()
    {
        return new ReportEnvironment { AppVersion = "2.1.0", Windows = "10.0 build 26100.2033", Clr = "4.0.30319.42000", NetRelease = "533325", Scale = 1.5F, OpenVpn = true, Forti = true };
    }

    static void ReportBuild()
    {
        var ex = Thrown(() => FailDeep(1));
        var log = new List<string>();
        for (var i = 0; i < 100; i++) log.Add("2026-11-25 14:00:" + (i % 60).ToString("00") + ".000 INFO  app line " + i + " host vpn.acme-lojistik.example user ayse.demir");
        var r = ErrorReport.Build("crash", ErrorInfo.From(ex), FakeEnv(), log, SeededSanitizer(), new DateTime(2026, 11, 25, 11, 0, 0, DateTimeKind.Utc));
        Check(System.Text.RegularExpressions.Regex.IsMatch(r.Title, @"^\[hata\] InvalidOperationException: Tests\.FailDeep \([0-9a-f]{8}\)$"), "başlık biçimi: " + r.Title);
        Check(r.Fingerprint != null && r.Title.Contains("(" + r.Fingerprint + ")") && r.Body.Contains("`" + r.Fingerprint + "`"), "parmak izi başlıkta ve gövdede");
        Check(r.Body.Contains("2.1.0") && r.Body.Contains("26100") && r.Body.Contains("4.0.30319.42000") && r.Body.Contains("OpenVPN Connect: var") && r.Body.Contains("Sophos Connect: yok"), "sürüm, Windows, .NET, sağlayıcılar (var/yok)");
        Check(r.Body.Contains("line 99") && !r.Body.Contains("line 39 ") && r.Body.Contains("Son günlük satırları (60)"), "yalnızca son 60 günlük satırı");
        var leaked = Seeds.Where(x => r.Body.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0 || r.Title.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        Check(leaked.Count == 0 && !r.Body.Contains(":\\"), "rapor sahte sır ve mutlak yol içermez" + (leaked.Count > 0 ? ": " + string.Join(", ", leaked) : ""));
        Check(r.Body.Contains("'<metin>'") && r.Body.Contains("at Tests.FailDeep"), "istisna iletisindeki tırnaklı metin çıkarılır, çerçeveler kalır");
        Check(SeededSanitizer().Sanitize(r.Body) == r.Body, "rapor gövdesi yeniden temizlenince değişmez");
        var back = ErrorReport.ParseRecord(r.ToRecord().Replace("\n", "\r\n"));
        Check(back != null && back.Title == r.Title && back.Kind == "crash" && back.Fingerprint == r.Fingerprint && back.Body.Replace("\r\n", "\n") == r.Body, "kayıt dosyası gidiş-dönüş");
        Check(ErrorReport.ParseRecord("bozuk") == null && ErrorReport.ParseRecord("title: x\nfingerprint: ../..\n---\nb").Fingerprint == null, "bozuk kayıt / geçersiz parmak izi");
        var m = ErrorReport.Build("manual", null, FakeEnv(), new List<string>(), new LogSanitizer(), DateTime.UtcNow);
        Check(m.Kind == "manual" && m.Fingerprint == null && m.Title.StartsWith("[hata] Kullanıcı bildirimi"), "kayıtlı hata yoksa kullanıcı bildirimi");
    }

    static void IssueLinks()
    {
        bool cut;
        var url = IssueLink.NewIssueUrl("[hata] Çökme: ığüşöç (ab12cd34)", "Gövde: ığüşöçİ & = ? #\nikinci satır", IssueLink.MaxUrlLength, out cut);
        Check(!cut && url.StartsWith("https://github.com/bymrhan/vpn-manager/issues/new?template=hata-raporu.yml&labels=hata&title="), "kısa rapor: şablon ve etiket ile");
        Check(url.Contains("%C4%B1") && url.Contains("%C5%9F") && url.Contains("%C3%87") && url.Contains("%26") && url.Contains("%3D") && url.Contains("%0A") && url.IndexOf(' ') < 0, "Türkçe karakterler ve özel karakterler yüzde-kodlanır");
        Check(Uri.UnescapeDataString(url).Contains("&rapor=Gövde: ığüşöçİ & = ? #\nikinci satır"), "kod çözülünce gövde aynen");
        var body = new StringBuilder("### Ortam\n- x\n\n### Son günlük satırları\n```\n");
        for (var i = 0; i < 400; i++) body.Append("2026-11-25 14:00:00.000 INFO  app satır ").Append(i).Append(" ığüş\n");
        body.Append("```\n");
        url = IssueLink.NewIssueUrl("[hata] T: M (ab12cd34)", body.ToString(), IssueLink.MaxUrlLength, out cut);
        var decoded = Uri.UnescapeDataString(url);
        var fences = System.Text.RegularExpressions.Regex.Matches(decoded, "```").Count;
        Check(cut && url.Length <= IssueLink.MaxUrlLength && url.Length > IssueLink.MaxUrlLength - 400, "uzun rapor sınırın altında kısaltılır (" + url.Length + ")");
        Check(decoded.Contains("panoya kopyalandı") && fences % 2 == 0 && decoded.Contains("satır 0 "), "kısaltma notu, kapalı kod bloğu, baştan satırlar");
        Check(IssueLink.IsOurIssueUrl("https://github.com/bymrhan/vpn-manager/issues/12") && !IssueLink.IsOurIssueUrl("https://github.com/other/repo/issues/1") && !IssueLink.IsOurIssueUrl("https://evil.example/?https://github.com/bymrhan/vpn-manager/issues/"), "yalnızca bu deponun kayıt adresleri açılır");
    }

    static void DuplicateCheck()
    {
        const string fp = "ab12cd34";
        var json = "{\"total_count\":4,\"items\":[" +
            "{\"number\":3,\"title\":\"[hata] X: Y (ab12cd34)\",\"html_url\":\"https://github.com/bymrhan/vpn-manager/pull/3\",\"state\":\"open\",\"pull_request\":{}}," +
            "{\"number\":4,\"title\":\"[hata] X: Y (ab12cd34)\",\"html_url\":\"https://github.com/someone/fork/issues/4\",\"state\":\"open\"}," +
            "{\"number\":5,\"title\":\"[hata] X: Y (ab12cd34)\",\"html_url\":\"https://github.com/bymrhan/vpn-manager/issues/5\",\"labels\":[{\"name\":\"hata\"}],\"state\":\"closed\"}," +
            "{\"number\":9,\"title\":\"[hata] X: Y (AB12CD34)\",\"html_url\":\"https://github.com/bymrhan/vpn-manager/issues/9\",\"labels\":[{\"name\":\"hata\"}],\"state\":\"open\"}]}";
        var d = IssueLink.FindDuplicate(json, fp);
        Check(d != null && d.Number == 9 && d.Open, "açık kayıt kapalıya tercih edilir; PR ve başka depo atlanır");
        var closedOnly = "{\"items\":[{\"number\":5,\"title\":\"[hata] X: Y (ab12cd34)\",\"html_url\":\"https://github.com/bymrhan/vpn-manager/issues/5\",\"labels\":[{\"name\":\"hata\"}],\"state\":\"closed\"}]}";
        d = IssueLink.FindDuplicate(closedOnly, fp);
        Check(d != null && d.Number == 5 && !d.Open, "kapalı kayıt da yineleme sayılır");
        Check(IssueLink.FindDuplicate("{\"items\":[{\"number\":1,\"title\":\"başka (ab12cd35)\",\"html_url\":\"https://github.com/bymrhan/vpn-manager/issues/1\",\"state\":\"open\"}]}", fp) == null, "başka parmak izi eşleşmez");
        Check(IssueLink.FindDuplicate("<html>rate limit</html>", fp) == null && IssueLink.FindDuplicate("{\"message\":\"API rate limit exceeded\"}", fp) == null && IssueLink.FindDuplicate(json, "zz") == null, "bozuk yanıt / geçersiz parmak izi: yok");
        string asked = null;
        d = IssueLink.CheckDuplicate(fp, u => { asked = u; return closedOnly; });
        Check(d != null && asked != null && asked.StartsWith("https://api.github.com/search/issues?q=") && asked.Contains("repo%3Abymrhan%2Fvpn-manager") && asked.Contains("label%3Ahata") && asked.Contains(fp), "arama adresi depo ve parmak izi ile");
        Check(IssueLink.CheckDuplicate(fp, u => { throw new System.Net.WebException("ağ yok"); }) == null, "ağ hatası sessizce atlanır");
        var called = false;
        Check(IssueLink.CheckDuplicate("../x", u => { called = true; return json; }) == null && !called, "geçersiz parmak izi için istek yapılmaz");
    }

    static void LogRotationRules()
    {
        var now = new DateTime(2026, 11, 25, 9, 0, 0);
        var ex = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        Check(LogRotation.PickFile(now, ex, 100) == "app-20261125.log", "ilk dosya");
        ex["app-20261125.log"] = 50;
        Check(LogRotation.PickFile(now, ex, 100) == "app-20261125.log", "sınırın altında aynı dosya");
        ex["app-20261125.log"] = 100;
        Check(LogRotation.PickFile(now, ex, 100) == "app-20261125-2.log", "sınırda sonraki parça");
        ex["app-20261125-2.log"] = 10;
        Check(LogRotation.PickFile(now, ex, 100) == "app-20261125-2.log", "en yeni parçaya devam");
        Check(LogRotation.PickFile(now.AddDays(1), ex, 100) == "app-20261126.log", "yeni gün yeni dosya");
        var names = new[] { "app-20261120.log", "app-20261121.log", "app-20261122.log", "app-20261123.log", "app-20261124.log", "app-20261125.log", "app-20261125-2.log", "app-20261125-10.log", "forti.log", "crash-20261125-120000.txt" };
        var del = LogRotation.ToDelete(names, 5);
        Check(del.Count == 3 && del.Contains("app-20261120.log") && del.Contains("app-20261121.log") && del.Contains("app-20261122.log"), "en fazla 5 günlük kalır, eskiler silinir; başka dosyalara dokunulmaz");
        var recs = new[] { "crash-20261120-100000.txt", "crash-20261121-100000.txt", "crash-20261121-100000-2.txt", "error-20261125-100000.txt", "crash-x.txt" };
        Check(LogRotation.RecordsToDelete(recs, "crash", 2).SequenceEqual(new[] { "crash-20261120-100000.txt" }), "kayıt temizliği türe göre");
        Check(LogRotation.NewestRecord(recs) == "error-20261125-100000.txt" && LogRotation.NewestRecord(new[] { "app-20261125.log" }) == null, "en yeni kayıt");
    }

    static void RateLimit()
    {
        var l = new LogRateLimiter(TimeSpan.FromMinutes(10));
        var t = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc); int s;
        Check(l.Allow("poll:sophos", t, out s) && s == 0, "ilk kayıt yazılır");
        Check(!l.Allow("poll:sophos", t.AddMinutes(1), out s) && !l.Allow("poll:sophos", t.AddMinutes(2), out s), "pencere içinde susturulur");
        Check(l.Allow("poll:watchguard", t.AddMinutes(2), out s), "anahtarlar ayrı");
        Check(l.Allow("poll:sophos", t.AddMinutes(10), out s) && s == 2, "pencere sonunda yazılır, atlanan sayısıyla");
        Check(l.Allow("poll:sophos", t.AddMinutes(-5), out s), "saat geri giderse kilitlenmez");
    }

    static void CrashMarker()
    {
        var now = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        var m = CrashPromptLogic.MarkerText("crash-20261125-085959.txt", now.AddMinutes(-1));
        Check(CrashPromptLogic.ShouldOffer(m, "ask", false, now) == "crash-20261125-085959.txt", "işaret varsa sorulur");
        Check(CrashPromptLogic.ShouldOffer(m, "never", false, now) == null && CrashPromptLogic.ShouldOffer(m, "ask", true, now) == null, "\"bir daha sorma\" ve demo: sorulmaz");
        Check(CrashPromptLogic.ShouldOffer(m, "ask", false, now.AddDays(31)) == null && CrashPromptLogic.ShouldOffer(CrashPromptLogic.MarkerText("crash-20261125-085959.txt", now.AddDays(3)), "ask", false, now) == null, "eski ya da gelecekteki işaret yok sayılır");
        Check(CrashPromptLogic.ShouldOffer("..\\..\\settings.json\n2026-11-25T08:00:00Z\n", "ask", false, now) == null && CrashPromptLogic.ShouldOffer("error-20261125-085959.txt\n2026-11-25T08:00:00Z\n", "ask", false, now) == null && CrashPromptLogic.ShouldOffer("", "ask", false, now) == null && CrashPromptLogic.ShouldOffer("crash-20261125-085959.txt\nbozuk", "ask", false, now) == null, "bozuk işaret, yol, işlenmiş hata: sorulmaz");
    }

    // Reads a log file the writer may hold open (File.ReadAllLines would refuse to share it).
    static string[] ReadShared(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8))
            return sr.ReadToEnd().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
    }

    static void LogWriterFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ovpnqs-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var t = new DateTime(2026, 11, 25, 9, 0, 0);
            var w = new LogWriter(dir) { Async = false, Clock = () => t };
            w.Write(LogLevel.Info, "app", "start version=2.1.0", null);
            w.Write(LogLevel.Warn, "forti", "çok\nsatırlı", null);
            w.Write(LogLevel.Error, "crash", "boom", Thrown(() => FailDeep(1)));
            var file = Path.Combine(dir, "app-20261125.log");
            var lines = File.ReadAllLines(file, Encoding.UTF8);
            Check(lines.Length >= 4 && lines[0] == "2026-11-25 09:00:00.000 INFO  app start version=2.1.0" && lines[1] == "2026-11-25 09:00:00.000 WARN  forti çok satırlı", "satır biçimi: zaman düzey kaynak ileti, UTF-8: " + (lines.Length > 1 ? lines[1] : ""));
            Check(lines[2].StartsWith("2026-11-25 09:00:00.000 ERROR crash boom") && lines.Skip(3).Any(l => l.Trim().StartsWith("at Tests.FailDeep")) && !lines.Any(l => l.Contains(":\\")), "istisna yığını yolsuz, ileti tırnaksız");
            var b = File.ReadAllBytes(file);
            Check(b.Length > 3 && !(b[0] == 0xEF && b[1] == 0xBB), "BOM'suz UTF-8");
            Check(w.Recent(2).Count == 2 && w.Recent(100).Count == lines.Length, "son satırlar bellekte");
            w.Enabled = false; w.Write(LogLevel.Info, "app", "yazılmamalı", null);
            Check(!File.ReadAllText(file).Contains("yazılmamalı"), "kapalıyken yazılmaz");

            var r = new LogWriter(dir) { Async = false, Clock = () => t, MaxBytes = 300, Keep = 3 };
            for (var i = 0; i < 40; i++) r.Write(LogLevel.Info, "app", "rotation line " + i + new string('x', 40), null);
            var files = Directory.GetFiles(dir, "app-*.log");
            Check(files.Length == 3 && files.All(f => new FileInfo(f).Length < 300 + 200), "boyut sınırında yeni parça, en fazla Keep dosya (" + files.Length + ")");

            var a = new LogWriter(dir + "-async") { Clock = () => t };
            var threads = new List<Thread>();
            for (var k = 0; k < 4; k++) { var id = k; var th = new Thread(() => { for (var i = 0; i < 50; i++) a.Write(LogLevel.Info, "w" + id, "line " + i, null); }); threads.Add(th); th.Start(); }
            foreach (var th in threads) th.Join();
            for (var wait = 0; wait < 50 && (!File.Exists(Path.Combine(dir + "-async", "app-20261125.log")) || ReadShared(Path.Combine(dir + "-async", "app-20261125.log")).Length < 200); wait++) { a.Flush(); Thread.Sleep(50); }
            a.Flush();
            var asyncLines = ReadShared(Path.Combine(dir + "-async", "app-20261125.log")).Length;
            Check(asyncLines == 200, "4 iş parçacığından 200 satır, kayıpsız (" + asyncLines + ")");

            var bad = new LogWriter("Z:\\::yok\\<>|") { Async = false };
            var threw = false;
            try { bad.Write(LogLevel.Error, "app", "x", new Exception("y")); bad.Flush(); bad.Recent(5); } catch { threw = true; }
            Check(!threw, "yazılamayan klasör: hata atmaz");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(dir + "-async", true); } catch { }
        }
    }

    static void DiagnosticsSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "ovpnqs-settings-" + Guid.NewGuid().ToString("N") + ".json");
        SettingsStore.PathOverride = path;
        try
        {
            File.WriteAllText(path, "{\"Hotkey\":\"Ctrl+Alt+V\",\"Theme\":\"light\"}");
            var old = SettingsStore.Load();
            Check(old.LogEnabled && old.CrashPrompt == "ask", "eski settings.json: günlük açık, çökmede sor");
            old.LogEnabled = false; old.CrashPrompt = "never";
            SettingsStore.Save(old);
            var back = SettingsStore.Load();
            Check(!back.LogEnabled && back.CrashPrompt == "never", "kaydedilip geri okunur");
            File.WriteAllText(path, "{\"LogEnabled\":\"hayir\",\"CrashPrompt\":\"bazen\"}");
            var bad = SettingsStore.Load();
            Check(bad.LogEnabled && bad.CrashPrompt == "ask", "geçersiz değerler varsayılana döner");
        }
        finally
        {
            SettingsStore.PathOverride = null;
            try { File.Delete(path); } catch { }
        }
    }

    // ---------- Diagnostics review fixes (fictional data only) ----------

    static void SanitizerTurkishCase()
    {
        var s = new LogSanitizer();
        foreach (var n in new[] { "KARŞIYAKA BELEDİYESİ", "İzmir Liman", "ışık enerji", "Çağ  Tekstil" }) s.AddProfileName(n);
        var cases = new Dictionary<string, string> {
            { "Karşıyaka Belediyesi", "<profil-1>" }, { "karşıyaka belediyesi", "<profil-1>" }, { "KARŞIYAKA BELEDİYESİ", "<profil-1>" }, { "KARSIYAKA BELEDIYESI", "<profil-1>" }, { "karsiyaka  belediyesi", "<profil-1>" },
            { "izmir liman", "<profil-2>" }, { "İZMİR LİMAN", "<profil-2>" }, { "IZMIR LIMAN", "<profil-2>" }, { "İzmir Liman", "<profil-2>" }, { "izmir\tliman", "<profil-2>" },
            { "IŞIK ENERJİ", "<profil-3>" }, { "Işık Enerji", "<profil-3>" }, { "ışık enerji", "<profil-3>" }, { "isik enerji", "<profil-3>" },
            { "ÇAĞ TEKSTİL", "<profil-4>" }, { "çağ tekstil", "<profil-4>" }, { "Çağ Tekstil", "<profil-4>" }, { "Çağ  Tekstil", "<profil-4>" }, { "CAG TEKSTIL", "<profil-4>" } };
        var bad = cases.Where(kv => s.Sanitize(kv.Key + " bağlandı") != kv.Value + " bağlandı").Select(kv => kv.Key + " -> " + s.Sanitize(kv.Key)).ToList();
        Check(bad.Count == 0, "Türkçe büyük/küçük harf, ASCII karşılığı ve çift boşluk varyantları" + (bad.Count > 0 ? ": " + string.Join(" | ", bad) : ""));
    }

    static void SanitizerUsersAndDomains()
    {
        var s = new LogSanitizer(); s.AddUser(@"ACME\jdoe"); s.AddUser("veli.kaya@corp.example");
        var a = s.Sanitize("jdoe logged; ACME\\JDOE; veli.kaya at work");
        Check(a.IndexOf("jdoe", StringComparison.OrdinalIgnoreCase) < 0 && a.IndexOf("acme", StringComparison.OrdinalIgnoreCase) < 0 && a.IndexOf("veli", StringComparison.OrdinalIgnoreCase) < 0, "DOMAIN\\user ve user@alan: kullanıcı kısmı da tohum: " + a);
        var b = new LogSanitizer().Sanitize("mehmet@intranet failed; ssh admin@10.0.0.1; CORP\\mehmet.yilmaz denied");
        Check(b.IndexOf("mehmet", StringComparison.OrdinalIgnoreCase) < 0 && b.IndexOf("intranet", StringComparison.OrdinalIgnoreCase) < 0 && b.IndexOf("CORP") < 0 && !b.Contains("10.0.0.1") && b.Contains("<kullanici>@") && b.Contains("<alan>\\<kullanici>"), "noktasız user@host, user@IP ve DOMAIN\\user kalıpla: " + b);
        var c = new LogSanitizer().Sanitize("see logs\\app.log and Users\\Public and %APPDATA%\\OpenVpnQuickSearch\\x");
        Check(c == "see logs\\app.log and Users\\Public and %APPDATA%\\OpenVpnQuickSearch\\x", "klasör yolları DOMAIN\\user sanılmaz: " + c);
        var d = new LogSanitizer().Sanitize("D:\\Musteriler\\Beta Gida\\conf and C:\\Program Files\\OpenVPN Connect\\x.exe");
        Check(!d.Contains("Musteriler") && !d.Contains("Beta") && d.Contains("<yol>\\conf") && d.Contains("<yol>\\x.exe"), "mutlak yolların klasörleri çıkar, dosya adı kalır: " + d);
    }

    static void SanitizerHostsAnyCase()
    {
        var s = new LogSanitizer();
        foreach (var h in new[] { "VPN.CORP.EXAMPLE.COM", "gw.example.ZIP", "portal.firma.md", "x.firma.zip", "Vpn.Firma.Com.Tr" })
        {
            var o = s.Sanitize("connect to " + h + " failed");
            Check(o.StartsWith("connect to <sunucu-") && o.EndsWith("> failed"), "sunucu adı yakalanır: " + h + " -> " + o);
        }
        var keep = "System.Net.WebException in Connection.cs and app-20261125.log";
        Check(new LogSanitizer().Sanitize(keep) == keep, ".NET adları ve tek noktalı dosya adları kalır");
        var ex = "    System.IO.IOException: cannot reach vpn.kappa.example";
        Check(new LogSanitizer().Sanitize(ex) == "    System.IO.IOException: cannot reach <sunucu-1>", "istisna satırının türü okunur kalır");
    }

    static void SanitizerLongLines()
    {
        var s = SeededSanitizer();
        var inputs = new Dictionary<string, string> {
            { "a-", "password=Hunter2!Gizli " + string.Concat(Enumerable.Repeat("a-", 25000)) },
            { "email", "ayse.demir@acme-lojistik.example x@" + string.Concat(Enumerable.Repeat("ab.", 16700)) },
            { "A", "token=ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ01 " + new string('A', 50000) },
            { "dots", "203.0.113.45 " + string.Concat(Enumerable.Repeat("a.", 25000)) + "1" },
            { "ovpn", "Acme Lojistik Merkez " + new string('b', 50000) + ".ovpn" } };
        foreach (var kv in inputs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var o = s.Sanitize(kv.Value);
            var m = s.SanitizeMessage(kv.Value);
            sw.Stop();
            var leaked = Seeds.Where(x => o.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Check(sw.ElapsedMilliseconds < 1000 && leaked.Count == 0 && o.Length <= LogSanitizer.MaxLine + 200, "50.000 karakterlik satır (" + kv.Key + "): " + sw.ElapsedMilliseconds + " ms, sızıntı yok" + (leaked.Count > 0 ? ": " + string.Join(",", leaked) : ""));
        }
    }

    static void SanitizerMoreSecrets()
    {
        var cases = new[] {
            new[] { "password: my secret phrase", "my secret", "secret phrase" }, new[] { "pwd=abc def", "abc", "def" },
            new[] { "username=foo1; user: bar2", "foo1", "bar2" }, new[] { "X-Api-Token abcdEFGH1234", "abcdEFGH1234", "abcd" },
            new[] { "token abcdefghijklmnop", "abcdefghijklmnop", "abcdefgh" }, new[] { "Cookie: SVPNCOOKIE=abc123", "abc123", "abc123" },
            new[] { "SVPNCOOKIE=xyz987abc", "xyz987abc", "xyz987" }, new[] { "client.exe connect -h vpn:443 -u veli:Secr3t!", "veli", "Secr3t" },
            new[] { "sccli enable -n Lambda -u ops1", "Lambda", "ops1" }, new[] { "key=AKIAABCDEFGHIJKLMNOP", "AKIAABCDEF", "AKIA" },
            new[] { "ip 192.168.001.010 ok", "192.168.001.010", "168.001" }, new[] { "net use \\\\acmesrv01\\Muhasebe", "acmesrv01", "Muhasebe" } };
        foreach (var c in cases)
        {
            var o = new LogSanitizer().SanitizeMessage(c[0]);
            Check(o.IndexOf(c[1], StringComparison.OrdinalIgnoreCase) < 0 && o.IndexOf(c[2], StringComparison.OrdinalIgnoreCase) < 0, "gizli değer çıkar: " + c[0] + " -> " + o);
        }
        var v = new LogSanitizer().Sanitize("version 2.1.0.5 start, v2.1.0.6, sürüm 2.1.0.7, server 2.1.0.8");
        Check(v == "version 2.1.0.5 start, v2.1.0.6, sürüm 2.1.0.7, server <ip-1>", "dört parçalı sürüm sürüm sözcüğünden sonra kalır: " + v);
    }

    static void SanitizerNoOverRedaction()
    {
        var s = new LogSanitizer();
        s.AddProfileName("Info"); s.AddProfileName("Art"); s.AddUser("user"); s.AddUser("admin"); s.AddProfileName("VPN"); s.AddProfileName("Connect");
        var t = "2026-11-25 14:03:11.123 INFO  openvpn connect requested by user admin, Smart art";
        Check(s.Sanitize(t) == t && s.KnownCount == 0, "4 karakterden kısa ve sık günlük kelimeleri tohum olmaz");
        var p = new LogSanitizer();
        p.AddProfile(new VpnProfile { Name = "Zeta Merkez", Source = @"Kappa Gida\kappa gida.ovpn" });
        var o = p.Sanitize("imported Kappa Gida / kappa gida failed");
        Check(o.IndexOf("kappa", StringComparison.OrdinalIgnoreCase) < 0 && o.IndexOf("gida", StringComparison.OrdinalIgnoreCase) < 0, ".ovpn dosya ve klasör adı tohumdur: " + o);
    }

    static void CrashMarkerLater()
    {
        var now = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        var m = CrashPromptLogic.MarkerText("crash-20261125-085959.txt", now.AddMinutes(-1));
        var m1 = CrashPromptLogic.AfterLater(m);
        Check(m1 != null && CrashPromptLogic.ShouldOffer(m1, "ask", false, now) == "crash-20261125-085959.txt", "\"Sonra\": işaret kalır, sonraki açılışta yine sorulur");
        var m2 = CrashPromptLogic.AfterLater(m1);
        Check(m2 != null && CrashPromptLogic.AfterLater(m2) == null, "en fazla " + CrashPromptLogic.MaxOffers + " kez sorulur, sonra işaret silinir");
        Check(CrashPromptLogic.ShouldOffer(CrashPromptLogic.MarkerText("crash-20261125-085959.txt", now, CrashPromptLogic.MaxOffers), "ask", false, now) == null, "sınıra ulaşmış işaret sorulmaz");
        Check(CrashPromptLogic.AfterLater("bozuk") == null && CrashPromptLogic.AfterLater(null) == null, "bozuk işaret: silinir");
    }

    static void ProviderSegmentVisibility()
    {
        Check(VpnProviders.ShowsExtSegment(true, true, 0), "kurulu ve açık, bağlantısı yok: bölüm görünür");
        Check(!VpnProviders.ShowsExtSegment(true, false, 0), "kurulu değil, satır yok: bölüm yok");
        Check(!VpnProviders.ShowsExtSegment(false, true, 3), "Ayarlar'da kapalı: bölüm yok");
        Check(VpnProviders.ShowsExtSegment(true, false, 2), "kurulu değil ama satırı var (demo): bölüm görünür");
        var so = VpnProviders.EmptySegmentText(VpnKind.Sophos); var wg = VpnProviders.EmptySegmentText(VpnKind.WatchGuard);
        Check(so != null && so[1] == "Sophos Connect'te tanımlı bağlantı yok. .pro veya .ovpn dosyasını Sophos Connect'e ekleyin." && wg != null && wg[1] == "WatchGuard SSL VPN istemcisinde kayıtlı sunucu yok.", "boş bölüm metinleri");
        Check(VpnProviders.EmptySegmentText(VpnKind.OpenVpn) == null && VpnProviders.EmptySegmentText(VpnKind.Forti) == null && VpnProviders.EmptySegmentText(null) == null, "OpenVPN, FortiClient ve Tümü için özel metin yok");
        var segs = VpnProviders.Segments(true, VpnProviders.ShowsExtSegment(true, true, 0), VpnProviders.ShowsExtSegment(true, false, 0));
        Check(string.Join(",", VpnProviders.SegmentNames(segs)) == "Tümü,OpenVPN,FortiClient,Sophos" && VpnProviders.IndexOf(segs, VpnKind.Sophos) == 3 && VpnProviders.SegmentAt(segs, 3) == VpnKind.Sophos, "boş Sophos bölümü Ctrl+4 ile seçilir");
    }

    // ---------- Direct GitHub send (modules\IssueSender.cs). Fake tokens and a fake transport only. ----------

    const string FakeToken = "github_pat_11FAKEFAKEFAKE_testonlynotarealtoken0123456789";

    static void IssueTokenCodec()
    {
        var enc = IssueToken.Encode(FakeToken);
        Check(IssueToken.Decode(enc) == FakeToken, "gidiş-dönüş");
        Check(Encoding.ASCII.GetString(enc).IndexOf("github_pat", StringComparison.Ordinal) < 0 && Encoding.ASCII.GetString(enc).IndexOf("FAKE", StringComparison.Ordinal) < 0, "kaynakta düz metin yok");
        Check(IssueToken.Decode(IssueToken.Encode("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ01")) == "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ01", "klasik ghp_ biçimi de");
        var notToken = Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes("merhaba dünya, bu bir anahtar değil")));
        Check(IssueToken.Decode(null) == null && IssueToken.Decode(new byte[0]) == null && IssueToken.Decode(Encoding.ASCII.GetBytes("@@@ bozuk")) == null &&
              IssueToken.Decode(notToken) == null && IssueToken.Decode(new byte[] { 0xFF, 0xFE, 0x00, 0x41 }) == null && IssueToken.Decode(new byte[5000]) == null, "bozuk veri: null");
        var threw = false; try { IssueToken.Encode("parola123"); } catch (ArgumentException) { threw = true; }
        Check(threw, "anahtar biçiminde olmayan değer kodlanmaz");
        Check(!IssueToken.Available, "test derlemesinde gömülü anahtar yok (tarayıcı akışı)");
    }

    static void IssueLimitRules()
    {
        var t = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        var s = new IssueLimitState();
        Check(IssueLimits.Check(s, "aaaa0001", t) == IssueLimits.Verdict.Allowed, "boş durum: izin");
        IssueLimits.Record(s, "aaaa0001", t);
        Check(IssueLimits.Check(s, "aaaa0001", t.AddHours(23)) == IssueLimits.Verdict.FingerprintLimit && IssueLimits.Check(s, "aaaa0001", t.AddHours(24)) == IssueLimits.Verdict.Allowed, "aynı parmak izi 24 saatte bir");
        IssueLimits.Record(s, "aaaa0002", t.AddHours(1)); IssueLimits.Record(s, null, t.AddHours(2));
        Check(IssueLimits.Check(s, "aaaa0003", t.AddHours(3)) == IssueLimits.Verdict.DailyLimit, "günde en fazla 3");
        Check(IssueLimits.Check(s, "aaaa0003", t.AddHours(24).AddMinutes(1)) == IssueLimits.Verdict.Allowed, "24 saat sonra yeniden");
        Check(IssueLimits.Check(s, null, t.AddHours(3)) != IssueLimits.Verdict.Allowed, "parmak izsiz raporlar da sınırlı");
        var back = IssueLimits.FromJson(IssueLimits.ToJson(s));
        Check(back.Sent.Count == 3 && back.ByFingerprint.Count == 3 && IssueLimits.Check(back, "aaaa0003", t.AddHours(3)) == IssueLimits.Verdict.DailyLimit, "JSON gidiş-dönüş");
        IssueLimits.Stop(back, t.AddHours(5));
        Check(IssueLimits.Check(back, "bbbb0001", t.AddHours(28)) == IssueLimits.Verdict.Stopped && IssueLimits.Check(back, "bbbb0001", t.AddHours(30)) == IssueLimits.Verdict.Allowed, "401 sonrası 24 saat durur");
        Check(IssueLimits.FromJson("{bozuk").Sent.Count == 0 && IssueLimits.FromJson(null).StoppedUntilUtc == DateTime.MinValue && IssueLimits.FromJson("{\"sent\":[\"x\",-5]}").Sent.Count == 0, "bozuk dosya: yeni durum");
    }

    // Fake GitHub: records every call, answers with handler(request).
    sealed class FakeGitHub
    {
        public readonly List<ApiRequest> Calls = new List<ApiRequest>();
        public Func<ApiRequest, ApiResponse> Handler;
        public ApiResponse Call(ApiRequest r) { Calls.Add(r); return Handler(r); }
        public static ApiResponse Ok(int number) { return new ApiResponse { Status = 201, Body = "{\"number\":" + number + ",\"html_url\":\"https://github.com/bymrhan/vpn-manager/issues/" + number + "\"}" }; }
        public static ApiResponse Search(string items) { return new ApiResponse { Status = 200, Body = "{\"total_count\":1,\"items\":[" + items + "]}" }; }
        public static string Item(int number, string state, bool locked) { return "{\"number\":" + number + ",\"title\":\"[hata] X: Y (ab12cd34)\",\"html_url\":\"https://github.com/bymrhan/vpn-manager/issues/" + number + "\",\"labels\":[{\"name\":\"hata\"}],\"state\":\"" + state + "\",\"locked\":" + (locked ? "true" : "false") + "}"; }
    }

    static SendResult SendWith(FakeGitHub g, IssueLimitState limits, string fp, DateTime now)
    {
        return GitHubIssueClient.Send("[hata] X: Y (" + (fp ?? "-") + ")", "### Ortam\n- x", fp, FakeEnv(), FakeToken, g.Call, limits, now);
    }

    static void IssueClientFlows()
    {
        var now = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        // new issue
        var g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search("") : FakeGitHub.Ok(41) };
        var lim = new IssueLimitState();
        var res = SendWith(g, lim, "ab12cd34", now);
        Check(res.Outcome == SendOutcome.Created && res.Number == 41 && res.Url == "https://github.com/bymrhan/vpn-manager/issues/41", "yeni kayıt oluşturulur");
        Check(g.Calls.Count == 2 && g.Calls[0].Method == "GET" && !g.Calls[0].Authorized && g.Calls[0].Token == null && g.Calls[0].Url.Contains("ab12cd34"), "yineleme araması anahtarsız");
        Check(g.Calls[1].Url == "https://api.github.com/repos/bymrhan/vpn-manager/issues" && g.Calls[1].Authorized && g.Calls[1].Token == FakeToken && g.Calls[1].Json.Contains("\"labels\":[\"hata\"]") && g.Calls[1].Json.Contains("[hata] X: Y (ab12cd34)"), "POST bu depoya, etiketle");
        Check(!g.Calls[1].ToString().Contains(FakeToken) && !g.Calls[1].ToString().Contains("Ortam"), "istek metni anahtar ve gövde içermez");
        Check(IssueLimits.Check(lim, "ab12cd34", now.AddHours(1)) == IssueLimits.Verdict.FingerprintLimit, "gönderim sınıra yazılır");
        // 422 label -> retry without labels
        g = new FakeGitHub();
        var posts = 0;
        g.Handler = r => { if (r.Method == "GET") return FakeGitHub.Search(""); posts++; return posts == 1 ? new ApiResponse { Status = 422, Body = "{\"message\":\"Validation Failed\",\"errors\":[{\"resource\":\"Label\",\"code\":\"invalid\"}]}" } : FakeGitHub.Ok(42); };
        res = SendWith(g, new IssueLimitState(), "ab12cd35", now);
        Check(res.Outcome == SendOutcome.Created && res.Number == 42 && posts == 2 && !g.Calls[2].Json.Contains("labels"), "etiket 422: etiketsiz bir kez daha");
        // other 422: no retry
        g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search("") : new ApiResponse { Status = 422, Body = "{\"message\":\"title too long\"}" } };
        res = SendWith(g, new IssueLimitState(), "ab12cd36", now);
        Check(res.Outcome == SendOutcome.Failed && g.Calls.Count == 2, "başka 422: yeniden denenmez");
        // 401 -> stopped for 24 h
        g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search("") : new ApiResponse { Status = 401, Body = "{\"message\":\"Bad credentials\"}" } };
        lim = new IssueLimitState();
        res = SendWith(g, lim, "ab12cd37", now);
        var before = g.Calls.Count;
        var again = SendWith(g, lim, "ab12cd38", now.AddHours(2));
        Check(res.Outcome == SendOutcome.Unauthorized && again.Outcome == SendOutcome.Stopped && g.Calls.Count == before, "401: 24 saat hiç istek yapılmaz");
        Check(SendWith(g, lim, "ab12cd38", now.AddHours(25)).Outcome == SendOutcome.Unauthorized, "24 saat sonra yeniden denenir");
        // 403 / rate limit: no stop
        g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search("") : new ApiResponse { Status = 403, Body = "{\"message\":\"API rate limit exceeded\"}" } };
        lim = new IssueLimitState();
        res = SendWith(g, lim, "ab12cd39", now);
        Check(res.Outcome == SendOutcome.Forbidden && lim.StoppedUntilUtc == DateTime.MinValue && lim.Sent.Count == 0, "403: tarayıcıya düşer, durdurmaz, sayılmaz");
        // open duplicate -> one comment
        g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search(FakeGitHub.Item(12, "open", false)) : new ApiResponse { Status = 201, Body = "{\"id\":7,\"html_url\":\"https://github.com/bymrhan/vpn-manager/issues/12#issuecomment-7\"}" } };
        lim = new IssueLimitState();
        res = SendWith(g, lim, "ab12cd34", now);
        Check(res.Outcome == SendOutcome.Commented && res.Number == 12 && g.Calls[1].Url == "https://api.github.com/repos/bymrhan/vpn-manager/issues/12/comments" && g.Calls[1].Json.Contains("Tekrar: sürüm 2.1.0, Windows 10.0 build 26100.2033"), "açık yineleme: tek \"Tekrar\" yorumu");
        Check(SendWith(g, lim, "ab12cd34", now.AddHours(3)).Outcome == SendOutcome.FingerprintLimit && g.Calls.Count == 2, "aynı hata 24 saat içinde yeniden yorumlanmaz");
        // closed duplicate -> new issue pointing at it
        g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search(FakeGitHub.Item(5, "closed", true)) : FakeGitHub.Ok(43) };
        res = SendWith(g, new IssueLimitState(), "ab12cd34", now);
        Check(res.Outcome == SendOutcome.Created && res.Number == 43 && g.Calls.Count == 2 && g.Calls[1].Url.EndsWith("/issues") && g.Calls[1].Json.Contains("Önceki kayıt: #5 (kapalı)"), "kapalı (kilitli) yineleme: yorum yok, eskiye bağlanan yeni kayıt");
        // open but locked duplicate -> new issue
        g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search(FakeGitHub.Item(6, "open", true)) : FakeGitHub.Ok(44) };
        res = SendWith(g, new IssueLimitState(), "ab12cd34", now);
        Check(res.Outcome == SendOutcome.Created && !g.Calls.Any(c => c.Url.EndsWith("/comments")) && g.Calls[1].Json.Contains("Önceki kayıt: #6"), "kilitli açık yineleme: yorum yapılmaz");
        // daily limit
        g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search("") : FakeGitHub.Ok(50) };
        lim = new IssueLimitState();
        foreach (var fp in new[] { "cc000001", "cc000002", "cc000003" }) SendWith(g, lim, fp, now);
        var calls = g.Calls.Count;
        Check(SendWith(g, lim, "cc000004", now.AddMinutes(5)).Outcome == SendOutcome.DailyLimit && g.Calls.Count == calls, "günde 3'ten sonra istek yok");
        // network failure, search failure, no token
        g = new FakeGitHub { Handler = r => { throw new System.Net.WebException("ağ yok"); } };
        Check(SendWith(g, new IssueLimitState(), "ab12cd34", now).Outcome == SendOutcome.Failed, "ağ hatası: tarayıcıya düşer");
        g = new FakeGitHub { Handler = r => r.Method == "GET" ? new ApiResponse { Status = 0 } : FakeGitHub.Ok(45) };
        Check(SendWith(g, new IssueLimitState(), "ab12cd34", now).Outcome == SendOutcome.Created, "arama başarısız: yine de kayıt açılır");
        g = new FakeGitHub { Handler = r => FakeGitHub.Ok(46) };
        var none = GitHubIssueClient.Send("t", "b", "ab12cd34", FakeEnv(), null, g.Call, new IssueLimitState(), now);
        Check(none.Outcome == SendOutcome.NoToken && g.Calls.Count == 0, "anahtar yoksa hiç istek yok");
        // a 201 whose link is not this repository is not trusted
        g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search("") : new ApiResponse { Status = 201, Body = "{\"number\":1,\"html_url\":\"https://evil.example/x\"}" } };
        Check(SendWith(g, new IssueLimitState(), "ab12cd34", now).Outcome == SendOutcome.Failed, "yabancı bağlantılı yanıt reddedilir");
    }

    static void IssueClientGuards()
    {
        Check(GitHubIssueClient.IsAllowedUrl("https://api.github.com/repos/bymrhan/vpn-manager/issues") && GitHubIssueClient.IsAllowedUrl("https://api.github.com/repos/bymrhan/vpn-manager/issues/12/comments") &&
              GitHubIssueClient.IsAllowedUrl(IssueLink.SearchUrl("ab12cd34")), "izinli adresler");
        Check(!GitHubIssueClient.IsAllowedUrl("http://api.github.com/repos/bymrhan/vpn-manager/issues") && !GitHubIssueClient.IsAllowedUrl("https://api.github.com/repos/other/repo/issues") &&
              !GitHubIssueClient.IsAllowedUrl("https://api.github.com/repos/bymrhan/vpn-manager/issues/1/comments/../../../x") && !GitHubIssueClient.IsAllowedUrl("https://api.github.com/repos/bymrhan/vpn-manager/labels") && !GitHubIssueClient.IsAllowedUrl(null), "başka depo, http, başka uç nokta: hayır");
        Check(GitHubIssueClient.HttpTransport(new ApiRequest { Method = "POST", Url = "https://evil.example/", Token = FakeToken, Authorized = true }).Status == 0, "gerçek taşıyıcı izinsiz adrese hiç bağlanmaz");
        var big = new string('x', 100000);
        var cut = GitHubIssueClient.Truncate("```\n" + big);
        Check(cut.Length <= GitHubIssueClient.MaxBodyChars && cut.Contains("60 KB") && System.Text.RegularExpressions.Regex.Matches(cut, "```").Count % 2 == 0, "gövde 60 KB'ta kesilir, kod bloğu kapanır");
        Check(GitHubIssueClient.IssueJson("a\"b", "satır\nıüş", true).Contains("\\\"") && GitHubIssueClient.IssueJson("t", "b", false).IndexOf("labels", StringComparison.Ordinal) < 0, "JSON kaçışı, etiketsiz biçim");
    }

    static void TokenSanitizing()
    {
        var s = new LogSanitizer();
        var a = s.Sanitize("token in text github_pat_11ABCDEFGHIJKLMNOPQRST_abcdefghijklmnopqrstuvwxyz012345 and ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ01 and gho_abcdefghijklmnopqrstu1");
        Check(a.IndexOf("github_pat", StringComparison.Ordinal) < 0 && a.IndexOf("ghp_", StringComparison.Ordinal) < 0 && a.IndexOf("gho_", StringComparison.Ordinal) < 0, "GitHub anahtar biçimleri çıkar: " + a);
        var t = new LogSanitizer(); t.AddSecret(FakeToken);
        var b = t.Sanitize("Authorization: Bearer x; raw " + FakeToken + " end");
        Check(b.IndexOf("FAKE", StringComparison.Ordinal) < 0 && b.IndexOf("Bearer x", StringComparison.Ordinal) < 0, "gömülü anahtar ve Authorization başlığı rapora girmez: " + b);
        var path = Path.Combine(Path.GetTempPath(), "ovpnqs-settings-" + Guid.NewGuid().ToString("N") + ".json");
        SettingsStore.PathOverride = path;
        try
        {
            var st = AppSettings.Defaults(); st.CrashPrompt = "auto"; SettingsStore.Save(st);
            Check(SettingsStore.Load().CrashPrompt == "auto", "CrashPrompt: auto kaydedilir");
            st.CrashAutoConsent = true; SettingsStore.Save(st);
            Check(SettingsStore.Load().CrashAutoConsent, "CrashAutoConsent kaydedilir");
            st.CrashPrompt = "ask"; SettingsStore.Save(st);
            Check(!SettingsStore.Load().CrashAutoConsent && !AppSettings.Defaults().CrashAutoConsent, "onay yalnızca auto kipinde kalır; varsayılan yok");
        }
        finally { SettingsStore.PathOverride = null; try { File.Delete(path); } catch { } }
    }

    // ---------- Security review fixes ----------

    static void AutoModeDecision()
    {
        Check(CrashPromptLogic.AutoSend("auto", true, true, false, true), "auto + onay + anahtar + günlük açık: sormadan gönderir");
        Check(!CrashPromptLogic.AutoSend("auto", false, true, false, true), "onaysız auto: sorar");
        Check(!CrashPromptLogic.AutoSend("auto", true, false, false, true), "anahtarsız auto: sorar");
        Check(!CrashPromptLogic.AutoSend("auto", true, true, true, true), "demo: asla");
        Check(!CrashPromptLogic.AutoSend("auto", true, true, false, false), "Hata kaydı tut kapalı: gönderilmez");
        Check(!CrashPromptLogic.AutoSend("ask", true, true, false, true) && !CrashPromptLogic.AutoSend("never", true, true, false, true), "ask / never: otomatik yok");
        bool c;
        Check(CrashPromptLogic.StoredMode("auto", true, true, out c) == "auto" && c, "anahtar + onay: auto saklanır, onayla");
        Check(CrashPromptLogic.StoredMode("auto", true, false, out c) == "ask" && !c, "uyarı onaylanmadı: ask");
        Check(CrashPromptLogic.StoredMode("auto", false, true, out c) == "ask" && !c, "anahtarsız derleme: ask");
        Check(CrashPromptLogic.StoredMode("never", true, true, out c) == "never" && !c && CrashPromptLogic.StoredMode("ask", true, true, out c) == "ask" && !c, "never / ask: onay düşer");
    }

    static void IssueReviewGuards()
    {
        var now = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        // 403 on the comment: rate limit / refusal -> no new issue; "locked" -> new issue
        var g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search(FakeGitHub.Item(12, "open", false)) : r.Url.EndsWith("/comments") ? new ApiResponse { Status = 403, Body = "{\"message\":\"API rate limit exceeded\"}" } : FakeGitHub.Ok(60) };
        var res = SendWith(g, new IssueLimitState(), "ab12cd34", now);
        Check(res.Outcome == SendOutcome.Forbidden && !g.Calls.Any(c => c.Url.EndsWith("/issues")), "yorumda 403 (sınır): yeni kayıt açılmaz");
        g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search(FakeGitHub.Item(12, "open", false)) : r.Url.EndsWith("/comments") ? new ApiResponse { Status = 403, Body = "{\"message\":\"Unable to create comment because issue is locked.\"}" } : FakeGitHub.Ok(61) };
        res = SendWith(g, new IssueLimitState(), "ab12cd34", now);
        Check(res.Outcome == SendOutcome.Created && res.Number == 61 && g.Calls.Last().Json.Contains("Önceki kayıt: #12"), "yorumda 403 (kilitli): eskisine bağlanan yeni kayıt");
        g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search(FakeGitHub.Item(12, "open", false)) : r.Url.EndsWith("/comments") ? new ApiResponse { Status = 429, Body = "" } : FakeGitHub.Ok(62) };
        Check(SendWith(g, new IssueLimitState(), "ab12cd34", now).Outcome == SendOutcome.Forbidden && g.Calls.Count == 2, "yorumda 429: tarayıcıya düşer");
        Check(GitHubIssueClient.CommentFallsThrough(new ApiResponse { Status = 404 }) && GitHubIssueClient.CommentFallsThrough(new ApiResponse { Status = 410 }) && !GitHubIssueClient.CommentFallsThrough(new ApiResponse { Status = 500 }), "404 / 410 yeni kayda düşer, 500 düşmez");
        // the token never goes to the search, even if a caller marked it
        g = new FakeGitHub { Handler = r => r.Method == "GET" ? FakeGitHub.Search("") : FakeGitHub.Ok(63) };
        SendWith(g, new IssueLimitState(), "ab12cd34", now);
        Check(g.Calls.Where(c => c.Method == "GET").All(c => !c.Authorized && c.Token == null) && g.Calls.Where(c => c.Method == "POST").All(c => c.Authorized && c.Token == FakeToken), "anahtar yalnızca kayıt / yorum isteğinde");
        Check(GitHubIssueClient.MayAuthorize("https://api.github.com/repos/bymrhan/vpn-manager/issues") && GitHubIssueClient.MayAuthorize("https://api.github.com/repos/bymrhan/vpn-manager/issues/7/comments") &&
              !GitHubIssueClient.MayAuthorize(IssueLink.SearchUrl("ab12cd34")) && !GitHubIssueClient.MayAuthorize("https://api.github.com/repos/bymrhan/vpn-manager/issues/7") && !GitHubIssueClient.MayAuthorize(null), "Authorization yalnızca iki adrese");
        // duplicate search: label hata and the issue's own link required
        var noLabel = "{\"items\":[{\"number\":5,\"title\":\"[hata] X: Y (ab12cd34)\",\"html_url\":\"https://github.com/bymrhan/vpn-manager/issues/5\",\"state\":\"open\"}]}";
        var wrongLink = "{\"items\":[{\"number\":5,\"title\":\"[hata] X: Y (ab12cd34)\",\"html_url\":\"https://github.com/bymrhan/vpn-manager/issues/6\",\"labels\":[{\"name\":\"hata\"}],\"state\":\"open\"}]}";
        Check(IssueLink.FindDuplicate(noLabel, "ab12cd34") == null && IssueLink.FindDuplicate(wrongLink, "ab12cd34") == null, "hata etiketi ve kendi bağlantısı olmayan sonuç yineleme sayılmaz");
        Check(IssueLink.SearchUrl("ab12cd34").Contains("label%3Ahata"), "arama hata etiketiyle");
        // title cap
        var json = GitHubIssueClient.IssueJson(new string('T', 500), "b", true);
        Check(json.Contains(new string('T', 199)) && !json.Contains(new string('T', 200)), "başlık 200 karakterde kesilir");
        var cut = GitHubIssueClient.CutTitle("[hata] NullReferenceException: " + new string('X', 300) + " (ab12cd34)");
        Check(cut.Length == GitHubIssueClient.MaxTitleChars && cut.StartsWith("[hata] NullReferenceException: ") && cut.EndsWith("… (ab12cd34)"), "ortadan kesilir, parmak izi kalır");
        Check(GitHubIssueClient.CutTitle("kısa (ab12cd34)") == "kısa (ab12cd34)", "kısa başlık değişmez");
    }
}
