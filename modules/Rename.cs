using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

// =====================================================================
// Renaming profiles from the panel (docs\superpowers\specs\2026-11-27-profile-rename-design.md).
// The client is the source of truth: a rename is written into the VPN client's own store and the
// panel keeps reading names from there. Only WatchGuard (its client has no name) keeps the name in
// the panel's names.json. This file holds the parts that touch no client: name rules, the panel's
// own files and the OpenVPN config.json transformation.
// =====================================================================

internal static class NameRules
{
    public const int MaxLength = 64;
    const string FortiForbidden = "\\/:*?\"<>|";
    static readonly CompareInfo Tr = CultureInfo.GetCultureInfo("tr-TR").CompareInfo;

    public static string Clean(string name) { return (name ?? "").Trim(); }

    // null when the name can be used, else the reason (Turkish, shown under the text box).
    // otherNames: names of the other profiles of the same provider. allowEmpty: WatchGuard, where an
    // empty name means "remove my name".
    public static string Check(string name, VpnKind kind, IEnumerable<string> otherNames, bool allowEmpty)
    {
        var n = Clean(name);
        if (n.Length == 0) return allowEmpty ? null : "Ad boş olamaz.";
        if (n.Length > MaxLength) return "Ad en çok 64 karakter olabilir.";
        foreach (var c in n) if (char.IsControl(c)) return "Ad geçersiz karakter içeriyor.";
        if (kind == VpnKind.Forti && n.IndexOfAny(FortiForbidden.ToCharArray()) >= 0) return "FortiClient adında \\ / : * ? \" < > | kullanılamaz.";
        if (otherNames != null)
            foreach (var o in otherNames)
                if (o != null && Tr.Compare(Clean(o), n, CompareOptions.IgnoreCase) == 0) return "Bu adda başka bir profil var.";
        return null;
    }
}

// What the rename dialog says and for which rows rename exists.
internal static class RenameText
{
    public static string Hint(VpnKind kind, bool openVpnBusy)
    {
        switch (kind)
        {
            case VpnKind.WatchGuard: return "Ad yalnız VPN Manager'da saklanır. Boş bırakırsanız otomatik ad geri gelir.";
            case VpnKind.Forti: return "Windows yönetici onayı isteyecek.";
            case VpnKind.Sophos: return "Sophos Connect'te görünen ad değişir.";
            case VpnKind.Brave: return "";
            default: return openVpnBusy ? "OpenVPN bağlantısı kesilince uygulanacak." : "OpenVPN Connect kapatılıp yeniden açılacak.";
        }
    }

    // Not for Brave rows, FortiClient IPsec tunnels, demo rows, or OpenVPN rows built from a .ovpn
    // file (their id is a guess: there is no entry in config.json to rename).
    public static bool Offered(VpnProfile p, bool demo)
    {
        if (p == null || p.Id == null || demo || p.EstimatedId) return false;
        if (BraveStore.IsBrave(p)) return false;
        if (p.Id.StartsWith(FortiStore.IdPrefixIpsec, StringComparison.Ordinal)) return false;
        return true;
    }
}

// A small { "id": "name" } JSON file in %APPDATA%\OpenVpnQuickSearch. A missing, empty or broken
// file reads as "no entries" and never throws; writes go through a temp file.
internal sealed class NameFile
{
    readonly string fileName;
    public NameFile(string fileName) { this.fileName = fileName; }

    string FilePath
    {
        get { return Path.Combine(NameStore.FolderOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenVpnQuickSearch"), fileName); }
    }

    public Dictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Demo.On) return result;
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return result;
            var raw = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IDictionary<string, object>;
            if (raw == null) return result;
            foreach (var kv in raw)
            {
                var v = kv.Value as string;
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrWhiteSpace(v)) result[kv.Key] = v;
            }
        }
        catch { result.Clear(); }
        return result;
    }

    // name null or empty removes the entry.
    public void Set(string id, string name)
    {
        if (Demo.On || string.IsNullOrEmpty(id)) return;
        var all = Load();
        if (string.IsNullOrWhiteSpace(name)) all.Remove(id); else all[id] = name.Trim();
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(all), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }
}

// names.json: names the panel keeps itself (WatchGuard servers; any id works).
internal static class NameStore
{
    internal static string FolderOverride; // tests only
    static readonly NameFile file = new NameFile("names.json");

    public static Dictionary<string, string> Load() { return file.Load(); }
    public static void Set(string profileId, string name) { file.Set(profileId, name); }

    // Names from a backup: only ids that have no name here are added. Returns how many were added.
    public static int Merge(IDictionary<string, string> fromBackup)
    {
        if (fromBackup == null) return 0;
        var have = Load(); var added = 0;
        foreach (var kv in fromBackup)
        {
            if (have.ContainsKey(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
            Set(kv.Key, kv.Value); added++;
        }
        return added;
    }

    // Overlays the panel's names on freshly loaded profiles: names.json, then a rename that still
    // waits to be written into OpenVPN Connect (pending wins: it is the name the user asked for).
    public static void Apply(IEnumerable<VpnProfile> profiles)
    {
        if (profiles == null) return;
        var names = Load(); var pending = PendingRenames.Load();
        if (names.Count == 0 && pending.Count == 0) return;
        foreach (var p in profiles)
        {
            if (p == null || p.Id == null) continue;
            string name;
            if (!pending.TryGetValue(p.Id, out name) && !names.TryGetValue(p.Id, out name)) continue;
            if (name == p.Name) continue;
            p.Name = name;
            p.Initials = ProfileStore.MakeInitials(name);
            // only the name key changes: loaders may have added keys of their own (provider label)
            if (p.Keys != null && p.Keys.Length > 0) p.Keys[0] = ProfileStore.SearchKey(name); else ProfileStore.Finish(p);
        }
    }
}

// pending-renames.json: OpenVPN renames asked for while a tunnel was up; written into OpenVPN
// Connect once nothing is connected.
internal static class PendingRenames
{
    static readonly NameFile file = new NameFile("pending-renames.json");

    public static Dictionary<string, string> Load() { return file.Load(); }
    public static void Set(string profileId, string name) { file.Set(profileId, name); }
    public static void Remove(string profileId) { file.Set(profileId, null); }
    public static bool IsPending(string profileId) { return profileId != null && Load().ContainsKey(profileId); }
}

// ---------------------------------------------------------------------
// OpenVPN Connect: the profile name lives in config.json. "persist:root" is a JSON string whose
// "status" is a JSON string again; status.profiles.<id> has profileDisplayName, profileName and
// name ("host [display name]"). Only the two levels on that path are decoded and encoded again;
// every other value stays the string it was.
// ---------------------------------------------------------------------
internal static class OpenVpnRename
{
    const string BadLayout = "config.json beklenen düzende değil.";
    const string NotFound = "Profil OpenVPN Connect'te bulunamadı.";

    static JavaScriptSerializer Js() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 }; }

    sealed class Doc
    {
        public IDictionary<string, object> Root, Persist, Status, Profiles;
    }

    static Doc Open(string json)
    {
        try
        {
            var js = Js();
            var d = new Doc();
            d.Root = js.DeserializeObject(json ?? "") as IDictionary<string, object>;
            if (d.Root == null) return null;
            object raw;
            if (!d.Root.TryGetValue("persist:root", out raw) || !(raw is string)) return null;
            d.Persist = js.DeserializeObject((string)raw) as IDictionary<string, object>;
            if (d.Persist == null || !d.Persist.TryGetValue("status", out raw) || !(raw is string)) return null;
            d.Status = js.DeserializeObject((string)raw) as IDictionary<string, object>;
            if (d.Status == null || !d.Status.TryGetValue("profiles", out raw)) return null;
            d.Profiles = raw as IDictionary<string, object>;
            return d.Profiles == null ? null : d;
        }
        catch { return null; }
    }

    static string Text(IDictionary<string, object> d, string key)
    {
        object v; return d != null && d.TryGetValue(key, out v) && v is string ? (string)v : "";
    }

    static IDictionary<string, object> Profile(Doc d, string id)
    {
        object v; return d.Profiles.TryGetValue(id ?? "", out v) ? v as IDictionary<string, object> : null;
    }

    public static int ProfileCount(string json) { var d = Open(json); return d == null ? -1 : d.Profiles.Count; }

    // The name OpenVPN Connect shows for the profile, "" when the profile or the layout is not there.
    public static string NameOf(string json, string profileId)
    {
        var d = Open(json); if (d == null) return "";
        var item = Profile(d, profileId); if (item == null) return "";
        var n = Text(item, "profileDisplayName");
        return n.Length > 0 ? n : Text(item, "profileName");
    }

    // The file text with the profile renamed, or null with the reason in error.
    public static string Rewrite(string json, string profileId, string newName, out string error)
    {
        error = null;
        var d = Open(json);
        if (d == null) { error = BadLayout; return null; }
        var item = Profile(d, profileId);
        if (item == null) { error = NotFound; return null; }
        try
        {
            var old = Text(item, "profileDisplayName"); if (old.Length == 0) old = Text(item, "profileName");
            item["profileDisplayName"] = newName; item["profileName"] = newName;
            var full = Text(item, "name");
            if (old.Length > 0 && full.Contains(old)) item["name"] = full.Replace(old, newName);
            foreach (var which in new[] { "connectedProfile", "previouslyConnectedProfile" })
            {
                object raw;
                var cp = d.Status.TryGetValue(which, out raw) ? raw as IDictionary<string, object> : null;
                if (cp == null || Text(cp, "id") != profileId) continue;
                if (cp.ContainsKey("profileDisplayName")) cp["profileDisplayName"] = newName;
                if (cp.ContainsKey("profileName")) cp["profileName"] = newName;
            }
            var js = Js();
            d.Persist["status"] = js.Serialize(d.Status);
            d.Root["persist:root"] = js.Serialize(d.Persist);
            return js.Serialize(d.Root);
        }
        catch { error = BadLayout; return null; }
    }

    // A rename that waited for the tunnel to go down: write it now, or forget it (the profile is
    // gone, or OpenVPN Connect already has that name).
    public static PendingAction Decide(string pendingName, bool profileExists, string clientName)
    {
        return !profileExists || clientName == pendingName ? PendingAction.Drop : PendingAction.Apply;
    }

    // Would closing OpenVPN Connect now cost a connection? connected: the monitor's state.
    // lastEvent / sinceLogChangeSeconds: OpenVPN Connect's own log, which also shows a connection it
    // is building by itself (started from its own window) or one whose adapter is not visible yet.
    // An event older than two minutes with no tunnel is a leftover (crash, restart), not activity.
    public static bool Busy(bool connected, string lastEvent, double sinceLogChangeSeconds)
    {
        if (connected) return true;
        if (string.IsNullOrEmpty(lastEvent) || lastEvent == "DISCONNECTED" || lastEvent == "EXITING" || lastEvent == "PAUSE") return false;
        return sinceLogChangeSeconds < 120;
    }

    const string WriteFailed = "config.json yazılamadı; ad değişmedi.";
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    static string ReadShared(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8)) return sr.ReadToEnd();
    }

    // Renames the given profiles (id -> new name) in the file. All or nothing: config.json is only
    // replaced after the new text was written to a temp file, read back and checked. The previous
    // file stays next to it as config.json.vpnm-yedek. Returns null, or the reason.
    // OpenVPN Connect must not be running: it would overwrite the file from memory.
    public static string ApplyToFile(string configPath, IDictionary<string, string> renames) { return ApplyToFile(configPath, renames, null); }

    // True when the profile is there and marked as having a saved password.
    public static bool SavedOf(string json, string profileId)
    {
        var d = Open(json); if (d == null) return false;
        var item = Profile(d, profileId); object v;
        return item != null && item.TryGetValue("savedPassword", out v) && v is bool && (bool)v;
    }

    // The file text with savedPassword = true for the given profiles (the password itself is in
    // Windows Credential Manager, see OpenVpnCredentials), or null with the reason in error.
    public static string MarkSaved(string json, ICollection<string> ids, out string error)
    {
        error = null;
        var d = Open(json);
        if (d == null) { error = BadLayout; return null; }
        try
        {
            foreach (var id in ids)
            {
                var item = Profile(d, id);
                if (item == null) { error = NotFound; return null; }
                item["savedPassword"] = true;
            }
            var js = Js();
            d.Persist["status"] = js.Serialize(d.Status);
            d.Root["persist:root"] = js.Serialize(d.Persist);
            return js.Serialize(d.Root);
        }
        catch { error = BadLayout; return null; }
    }

    // savedIds (optional): profiles that get savedPassword = true in the same write.
    public static string ApplyToFile(string configPath, IDictionary<string, string> renames, ICollection<string> savedIds)
    {
        if (renames == null) renames = new Dictionary<string, string>();
        if (savedIds == null) savedIds = new string[0];
        if (renames.Count == 0 && savedIds.Count == 0) return null;
        var WriteFailed = renames.Count > 0 ? OpenVpnRename.WriteFailed : "config.json yazılamadı; değişiklik yapılmadı.";
        var tmp = configPath + ".vpnm-tmp";
        try
        {
            if (!File.Exists(configPath)) return "OpenVPN Connect profil deposu bulunamadı.";
            var original = ReadShared(configPath);
            var count = ProfileCount(original);
            var text = original;
            foreach (var kv in renames)
            {
                string error;
                text = Rewrite(text, kv.Key, kv.Value, out error);
                if (text == null) return error;
            }
            if (savedIds.Count > 0)
            {
                string error;
                text = MarkSaved(text, savedIds, out error);
                if (text == null) return error;
            }
            File.WriteAllText(tmp, text, Utf8);
            var check = File.ReadAllText(tmp, Encoding.UTF8);
            if (ProfileCount(check) != count) return WriteFailed;
            foreach (var kv in renames) if (NameOf(check, kv.Key) != kv.Value) return WriteFailed;
            foreach (var id in savedIds) if (!SavedOf(check, id)) return WriteFailed;
            File.Copy(configPath, configPath + ".vpnm-yedek", true);
            File.Replace(tmp, configPath, null);
            return null;
        }
        catch { return WriteFailed; }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    // Closes OpenVPN Connect, renames the profiles in config.json, starts OpenVPN Connect again.
    // The caller makes sure no tunnel is up. Returns null, or the reason.
    public static string ApplyWithRestart(IDictionary<string, string> renames) { return ApplyWithRestart(renames, null); }

    // The one result that is not a failure of the write: config.json has the change.
    public const string RestartFailed = "Değişiklik yazıldı, ancak OpenVPN Connect yeniden başlatılamadı. Elle açın.";

    public static string ApplyWithRestart(IDictionary<string, string> renames, ICollection<string> savedIds)
    {
        if ((renames == null || renames.Count == 0) && (savedIds == null || savedIds.Count == 0)) return null;
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN Connect", "OpenVPNConnect.exe");
        if (!File.Exists(exe)) { var w = Environment.GetEnvironmentVariable("ProgramW6432"); if (!string.IsNullOrEmpty(w)) exe = Path.Combine(w, "OpenVPN Connect", "OpenVPNConnect.exe"); }
        var wasRunning = false; var hadWindow = false;
        // OpenVPN Connect is ended below; should that catch it in the middle of a save, the file as
        // it is now (complete) is put back before anything else.
        string intact = null;
        try { var now = ReadShared(ProfileStore.ConfigPath); if (ProfileCount(now) >= 0) intact = now; } catch { }
        if (intact == null) return "OpenVPN Connect profil deposu okunamadı; ad değişmedi.";
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("OpenVPNConnect");
            wasRunning = procs.Length > 0;
            foreach (var p in procs) { try { if (p.MainWindowHandle != IntPtr.Zero) { hadWindow = true; p.CloseMainWindow(); } } catch { } }
            // the window only hides to the tray: the processes are ended after a short grace period
            var until = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < until && System.Diagnostics.Process.GetProcessesByName("OpenVPNConnect").Length > 0) System.Threading.Thread.Sleep(100);
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("OpenVPNConnect")) { try { p.Kill(); p.WaitForExit(3000); } catch { } finally { p.Dispose(); } }
            foreach (var p in procs) p.Dispose();
            until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && System.Diagnostics.Process.GetProcessesByName("OpenVPNConnect").Length > 0) System.Threading.Thread.Sleep(100);
            if (System.Diagnostics.Process.GetProcessesByName("OpenVPNConnect").Length > 0) return "OpenVPN Connect kapatılamadı; ad değişmedi.";
        }
        catch { return "OpenVPN Connect kapatılamadı; ad değişmedi."; }

        try { if (ProfileCount(ReadShared(ProfileStore.ConfigPath)) < 0) File.WriteAllText(ProfileStore.ConfigPath, intact, Utf8); } catch { }
        var result = ApplyToFile(ProfileStore.ConfigPath, renames, savedIds);

        if (wasRunning && File.Exists(exe))
        {
            try
            {
                if (!hadWindow) QuietWindow.KeepProcessInBackground("OpenVPNConnect", 12);
                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, UseShellExecute = true })) { }
            }
            catch { if (result == null) result = RestartFailed; }
        }
        return result;
    }
}

internal enum PendingAction { Apply, Drop }

// ---------------------------------------------------------------------
// FortiClient SSL tunnels: the tunnel name is the name of its registry key under
// ...\Sslvpn\Tunnels in HKLM (needs elevation) and HKCU. A registry key cannot be renamed in
// place: it is copied to the new name and the old one is deleted.
// ---------------------------------------------------------------------
internal static class FortiRename
{
    public const int Ok = 0, NotFound = 2, TargetExists = 3, CopyFailed = 4, Declined = 5;
    public const string Flag = "--rename-forti";
    const string TunnelsPath = @"SOFTWARE\Fortinet\FortiClient\Sslvpn\Tunnels";

    public static string Message(int code)
    {
        switch (code)
        {
            case Ok: return null;
            case NotFound: return "Tünel FortiClient'ta bulunamadı.";
            case TargetExists: return "Bu adda bir FortiClient tüneli zaten var.";
            case Declined: return "Yönetici onayı verilmedi; ad değişmedi.";
            default: return "Kayıt defteri güncellenemedi; ad değişmedi.";
        }
    }

    public static bool Exists(Microsoft.Win32.RegistryKey tunnelsParent, string name)
    {
        if (tunnelsParent == null || string.IsNullOrEmpty(name)) return false;
        using (var k = tunnelsParent.OpenSubKey(name)) return k != null;
    }

    static void CopyKey(Microsoft.Win32.RegistryKey from, Microsoft.Win32.RegistryKey to)
    {
        foreach (var n in from.GetValueNames())
            to.SetValue(n, from.GetValue(n, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames), from.GetValueKind(n));
        foreach (var s in from.GetSubKeyNames())
            using (var a = from.OpenSubKey(s)) using (var b = to.CreateSubKey(s)) CopyKey(a, b);
    }

    static void CopyTo(Microsoft.Win32.RegistryKey parent, string from, string to)
    {
        using (var a = parent.OpenSubKey(from)) using (var b = parent.CreateSubKey(to))
        {
            CopyKey(a, b);
            if (b.ValueCount != a.ValueCount || b.SubKeyCount != a.SubKeyCount) throw new InvalidOperationException("copy incomplete");
        }
    }

    // Renames one key under the parent. Registry names ignore letter case, so a change of case only
    // goes through a temporary name.
    public static int RenameKey(Microsoft.Win32.RegistryKey tunnelsParent, string oldName, string newName)
    {
        // A name is one key directly under the parent. "a\b" would address a key further down (and
        // this also runs elevated): refused before anything is opened.
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName.IndexOf('\\') >= 0 || newName.IndexOf('\\') >= 0) return CopyFailed;
        if (!Exists(tunnelsParent, oldName)) return NotFound;
        var caseOnly = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Exists(tunnelsParent, newName)) return TargetExists;
        var created = new List<string>();
        try
        {
            if (caseOnly)
            {
                var temp = newName + ".vpnm-tmp";
                if (Exists(tunnelsParent, temp)) tunnelsParent.DeleteSubKeyTree(temp, false);
                created.Add(temp); CopyTo(tunnelsParent, oldName, temp);
                tunnelsParent.DeleteSubKeyTree(oldName, false);
                CopyTo(tunnelsParent, temp, newName);
                tunnelsParent.DeleteSubKeyTree(temp, false);
            }
            else
            {
                created.Add(newName); CopyTo(tunnelsParent, oldName, newName);
                tunnelsParent.DeleteSubKeyTree(oldName, false);
            }
            return Ok;
        }
        catch
        {
            // the old key is only deleted after a complete copy: remove what was half made
            try { if (!caseOnly && Exists(tunnelsParent, oldName)) foreach (var c in created) tunnelsParent.DeleteSubKeyTree(c, false); } catch { }
            return CopyFailed;
        }
    }

    // Renames the tunnel under every parent that has it. Nothing is changed when the new name is
    // taken under any parent. A failure after an earlier parent succeeded renames that one back.
    public static int RenameAll(IList<Microsoft.Win32.RegistryKey> tunnelParents, string oldName, string newName)
    {
        if (tunnelParents == null || string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return NotFound;
        var caseOnly = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
        var owners = tunnelParents.Where(p => Exists(p, oldName)).ToList();
        if (owners.Count == 0) return NotFound;
        if (!caseOnly && tunnelParents.Any(p => Exists(p, newName))) return TargetExists;
        var done = new List<Microsoft.Win32.RegistryKey>();
        foreach (var p in owners)
        {
            var code = RenameKey(p, oldName, newName);
            if (code == Ok) { done.Add(p); continue; }
            foreach (var d in done) RenameKey(d, newName, oldName);
            return code;
        }
        return Ok;
    }

    // ---- elevated helper: the panel starts its own exe with these arguments (one UAC prompt) ----

    static string B64(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }

    public static string ElevatedArguments(string oldName, string newName) { return Flag + " " + B64(oldName) + " " + B64(newName); }

    public static bool TryParseArguments(string[] args, out string oldName, out string newName)
    {
        oldName = newName = null;
        if (args == null) return false;
        var i = Array.IndexOf(args, Flag);
        if (i < 0 || i + 2 >= args.Length) return false;
        try
        {
            oldName = Encoding.UTF8.GetString(Convert.FromBase64String(args[i + 1]));
            newName = Encoding.UTF8.GetString(Convert.FromBase64String(args[i + 2]));
            return oldName.Length > 0 && newName.Length > 0;
        }
        catch { oldName = newName = null; return false; }
    }

    static Microsoft.Win32.RegistryKey OpenTunnels(Microsoft.Win32.RegistryHive hive, bool writable)
    {
        try
        {
            var view = Environment.Is64BitOperatingSystem ? Microsoft.Win32.RegistryView.Registry64 : Microsoft.Win32.RegistryView.Default;
            return Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view).OpenSubKey(TunnelsPath, writable);
        }
        catch { return null; }
    }

    // Runs elevated: the HKLM key only (HKCU belongs to whoever answered the UAC prompt, which may
    // be another account; the panel renames its own HKCU key itself).
    public static int ElevatedMain(string[] args)
    {
        string oldName, newName;
        if (!TryParseArguments(args, out oldName, out newName)) return CopyFailed;
        if (NameRules.Check(newName, VpnKind.Forti, null, false) != null) return CopyFailed;
        using (var hklm = OpenTunnels(Microsoft.Win32.RegistryHive.LocalMachine, true))
        {
            if (hklm == null) return CopyFailed;
            return RenameKey(hklm, oldName, newName);
        }
    }

    static int RunElevated(string oldName, string newName)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.Windows.Forms.Application.ExecutablePath, Arguments = ElevatedArguments(oldName, newName),
                Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                if (p == null || !p.WaitForExit(30000)) return CopyFailed;
                return p.ExitCode;
            }
        }
        catch (System.ComponentModel.Win32Exception ex) { return ex.NativeErrorCode == 1223 ? Declined : CopyFailed; } // 1223: the user cancelled the UAC prompt
        catch { return CopyFailed; }
    }

    // Renames the tunnel for this user: HKCU first (no elevation, can be undone), then HKLM through
    // the elevated helper; when that fails or is declined the HKCU key gets its old name back.
    // Also moves FortiClient's "selected connection" value. Returns one of the codes above.
    public static int Run(string oldName, string newName)
    {
        var caseOnly = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
        using (var hkcu = OpenTunnels(Microsoft.Win32.RegistryHive.CurrentUser, true))
        using (var hklm = OpenTunnels(Microsoft.Win32.RegistryHive.LocalMachine, false))
        {
            var inUser = Exists(hkcu, oldName); var inMachine = Exists(hklm, oldName);
            if (!inUser && !inMachine) return NotFound;
            if (!caseOnly && (Exists(hkcu, newName) || Exists(hklm, newName))) return TargetExists;
            if (inUser) { var code = RenameKey(hkcu, oldName, newName); if (code != Ok) return code; }
            if (inMachine)
            {
                var code = RunElevated(oldName, newName);
                if (code != Ok) { if (inUser) RenameKey(hkcu, newName, oldName); return code; }
            }
        }
        try
        {
            using (var sel = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Fortinet\FortiClient\FA_VPN", true))
                if (sel != null && string.Equals(Convert.ToString(sel.GetValue("connection", "")), oldName, StringComparison.OrdinalIgnoreCase))
                    sel.SetValue("connection", newName, Microsoft.Win32.RegistryValueKind.String);
        }
        catch { }
        return Ok;
    }
}

// A FortiClient tunnel's panel id contains its name ("forti-ssl:<name>"): what the panel keyed by
// the old id follows the rename.
internal static class IdMigration
{
    public static HashSet<string> Pins(HashSet<string> pins, string oldId, string newId)
    {
        var result = new HashSet<string>(pins ?? new HashSet<string>());
        if (result.Remove(oldId)) result.Add(newId);
        return result;
    }

    static string Quote(string v)
    {
        v = v ?? "";
        return v.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    // First field of a history.csv line (quotes removed) and where the third field starts.
    static bool FirstTwo(string line, out string first, out int restStart)
    {
        first = null; restStart = -1;
        var i = 0; var fields = 0; var sb = new StringBuilder();
        while (fields < 2 && i <= line.Length)
        {
            sb.Clear();
            if (i < line.Length && line[i] == '"')
            {
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; }
                    else if (line[i] == '"') { i++; break; }
                    else sb.Append(line[i++]);
                }
            }
            else while (i < line.Length && line[i] != ';') sb.Append(line[i++]);
            if (i >= line.Length || line[i] != ';') return false;
            i++; fields++;
            if (fields == 1) first = sb.ToString();
        }
        restStart = i;
        return true;
    }

    // history.csv lines ("ProfileId;ProfileName;StartUtc;EndUtc") with the rows of oldId moved to
    // newId / newName. Every other line is returned as it was.
    public static string[] HistoryLines(string[] lines, string oldId, string newId, string newName)
    {
        var result = new string[lines.Length];
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n]; string first; int rest;
            result[n] = FirstTwo(line, out first, out rest) && first == oldId ? Quote(newId) + ";" + Quote(newName) + ";" + line.Substring(rest) : line;
        }
        return result;
    }
}
