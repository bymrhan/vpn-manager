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
        Check(FortiGui.ShowsTunnel("Personal VPNs SANBOR-YENİ", "SANBOR-YENİ"), "grup önekli değer eşleşir");
        Check(FortiGui.ShowsTunnel("Personal VPNs sanbor-yeni", "SANBOR-YENİ"), "büyük/küçük harf (Türkçe İ) eşleşir");
        Check(FortiGui.ShowsTunnel("SANBOR-YENİ", "SANBOR-YENİ"), "öneksiz değer eşleşir");
        Check(!FortiGui.ShowsTunnel("Personal VPNs ESKI-SANBOR-YENİ", "SANBOR-YENİ"), "ad parçası eşleşmez");
        Check(!FortiGui.ShowsTunnel("Personal VPNs SANBOR", "SANBOR-YENİ"), "farklı tünel eşleşmez");
        Check(!FortiGui.ShowsTunnel("", "X"), "boş değer eşleşmez");
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
}
