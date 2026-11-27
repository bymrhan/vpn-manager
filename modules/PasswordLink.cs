using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

// =====================================================================
// Saved OpenVPN passwords that were carried to another PC (Windows Credential Manager backup).
//
// OpenVPN Connect keeps a saved password as the generic credential "org.openvpn.client./<profile id>"
// (user name = the profile id) and marks the profile with savedPassword = true in config.json.
// A profile imported on the second PC gets a new id, so the carried credentials (old ids) belong to
// no profile and OpenVPN Connect asks for the password again.
//
// The backup's manifest.json has the old id, the name and the file name of every profile; from it
// the old credential is copied to the new id and the flag is set. The password is never decoded,
// shown, logged or written to a file: its bytes go from one credential to the other as they are.
// =====================================================================

// One profile of a backup's manifest.json.
internal sealed class BackupEntry
{
    public string Id, Name, File;
}

internal sealed class PasswordLinkPlan
{
    // old profile id (the backup's) -> the id the profile has on this PC
    public readonly List<KeyValuePair<string, string>> Pairs = new List<KeyValuePair<string, string>>();
    public int Ambiguous, NoProfile, NoOldPassword, AlreadySaved, SameId;
    public int Skipped { get { return Ambiguous + NoProfile + NoOldPassword + AlreadySaved + SameId; } }
}

internal static class PasswordLink
{
    const long MaxManifestBytes = 16 * 1024 * 1024;
    static readonly CultureInfo Tr = new CultureInfo("tr-TR");

    // Same key as ImportService: Turkish, case-insensitive.
    static string Key(string name) { return (name ?? "").Trim().ToUpper(Tr); }

    // id, name and file of every manifest entry that has an id. Empty when the zip or the manifest
    // cannot be read.
    public static List<BackupEntry> ReadManifest(string zipPath)
    {
        var list = new List<BackupEntry>();
        try
        {
            if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath)) return list;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var manifest = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, "manifest.json", StringComparison.OrdinalIgnoreCase));
                if (manifest == null || manifest.Length > MaxManifestBytes) return list;
                string text;
                using (var sr = new StreamReader(manifest.Open(), Encoding.UTF8)) text = sr.ReadToEnd();
                var items = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 }.DeserializeObject(text) as object[];
                if (items == null) return list;
                foreach (var raw in items)
                {
                    var item = raw as IDictionary<string, object>;
                    if (item == null) continue;
                    var id = Text(item, "id");
                    if (id.Length == 0) continue;
                    var file = Text(item, "file");
                    list.Add(new BackupEntry { Id = id, Name = Text(item, "name"), File = file.Length == 0 ? null : file });
                }
            }
        }
        catch { list.Clear(); }
        return list;
    }

    static string Text(IDictionary<string, object> d, string key)
    {
        object v; return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture).Trim() : "";
    }

    // Which carried passwords belong to which profile of this PC.
    // profiles: id and name of the profiles OpenVPN Connect has here. hasPassword(id): a credential
    // for that profile id is in Windows. An import names the profile after the backup's file name,
    // so that is tried first, then the name. Anything that is not exactly one-to-one is left alone.
    public static PasswordLinkPlan Plan(IList<BackupEntry> manifest, IList<KeyValuePair<string, string>> profiles, Func<string, bool> hasPassword)
    {
        var plan = new PasswordLinkPlan();
        if (manifest == null || profiles == null || hasPassword == null) return plan;

        var byName = new Dictionary<string, List<string>>();
        foreach (var p in profiles)
        {
            if (string.IsNullOrEmpty(p.Key)) continue;
            var k = Key(p.Value); if (k.Length == 0) continue;
            List<string> ids;
            if (!byName.TryGetValue(k, out ids)) byName[k] = ids = new List<string>();
            if (!ids.Contains(p.Key)) ids.Add(p.Key);
        }

        // manifest entry -> the one profile it names (null: none or several)
        var target = new Dictionary<BackupEntry, string>();
        var hits = new Dictionary<string, int>();
        foreach (var e in manifest)
        {
            if (e == null || string.IsNullOrEmpty(e.Id)) continue;
            List<string> ids = null;
            var fileKey = e.File == null ? "" : Key(Path.GetFileNameWithoutExtension(e.File));
            if (fileKey.Length == 0 || !byName.TryGetValue(fileKey, out ids))
            {
                var nameKey = Key(e.Name);
                if (nameKey.Length == 0 || !byName.TryGetValue(nameKey, out ids)) ids = null;
            }
            if (ids == null) { plan.NoProfile++; continue; }
            if (ids.Count != 1) { plan.Ambiguous++; continue; }
            target[e] = ids[0];
            int n; hits.TryGetValue(ids[0], out n); hits[ids[0]] = n + 1;
        }

        foreach (var kv in target)
        {
            var oldId = kv.Key.Id; var newId = kv.Value;
            if (hits[newId] != 1) { plan.Ambiguous++; continue; }
            if (oldId == newId) { plan.SameId++; continue; }
            if (!hasPassword(oldId)) { plan.NoOldPassword++; continue; }
            if (hasPassword(newId)) { plan.AlreadySaved++; continue; }
            plan.Pairs.Add(new KeyValuePair<string, string>(oldId, newId));
        }
        return plan;
    }

    // Numbers only: no profile, server or user name.
    public static string Summary(PasswordLinkPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append(plan.Pairs.Count + " profilin taşınmış parolası bulundu.");
        var parts = new List<string>();
        if (plan.AlreadySaved > 0) parts.Add(plan.AlreadySaved + " profilin parolası zaten kayıtlı");
        if (plan.NoOldPassword > 0) parts.Add(plan.NoOldPassword + " profil için taşınmış parola yok");
        if (plan.NoProfile > 0) parts.Add(plan.NoProfile + " yedek girdisinin bu cihazda profili yok");
        if (plan.Ambiguous > 0) parts.Add(plan.Ambiguous + " girdi belirsiz (aynı adlı profiller)");
        if (plan.SameId > 0) parts.Add(plan.SameId + " profilin kimliği değişmemiş");
        if (parts.Count > 0) sb.Append("\nAtlanan: " + string.Join(", ", parts.ToArray()) + ".");
        return sb.ToString();
    }

    public const string NothingCopied = "Parolalar Windows Kimlik Bilgileri'nde kopyalanamadı; hiçbir şey değişmedi.";

    // Copies each old credential to its new id (copy), then sets the flags (writeFlags: gets the new
    // ids, returns null or the reason). When the flags cannot be written, the credentials made in
    // this run are removed again (delete). linked: profiles that now have their password.
    public static string Apply(IList<KeyValuePair<string, string>> pairs, Func<string, string, bool> copy, Action<string> delete,
                               Func<ICollection<string>, string> writeFlags, out int linked)
    {
        linked = 0;
        var made = new List<string>();
        if (pairs != null)
            foreach (var p in pairs)
            {
                bool ok;
                try { ok = copy(p.Key, p.Value); } catch { ok = false; }
                if (ok) made.Add(p.Value);
            }
        if (made.Count == 0) return NothingCopied;
        string error;
        try { error = writeFlags(made); } catch { error = "config.json yazılamadı; parolalar eşleştirilmedi."; }
        if (error != null)
        {
            foreach (var id in made) { try { delete(id); } catch { } }
            return error;
        }
        linked = made.Count;
        return null;
    }
}

// OpenVPN Connect's saved passwords in Windows Credential Manager. Only whole credentials are
// handled here; the password bytes are never turned into text.
internal static class OpenVpnCredentials
{
    public const string Prefix = "org.openvpn.client./";
    const int CredTypeGeneric = 1, CredPersistLocalMachine = 2, ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public int Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWrite(ref CREDENTIAL c, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredRead(string target, int type, int flags, out IntPtr c);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr p);

    static string Target(string id, string prefix) { return (prefix ?? Prefix) + id; }

    public static bool Has(string id, string prefix = null)
    {
        if (string.IsNullOrEmpty(id)) return false;
        IntPtr p;
        if (!CredRead(Target(id, prefix), CredTypeGeneric, 0, out p)) return false;
        CredFree(p);
        return true;
    }

    // The credential of oldId again under newId, user name = newId (as OpenVPN Connect writes it).
    // false when there is nothing to copy, newId already has one (never overwritten) or Windows
    // refuses. The old credential stays.
    public static bool Copy(string oldId, string newId, string prefix = null)
    {
        if (string.IsNullOrEmpty(oldId) || string.IsNullOrEmpty(newId) || oldId == newId) return false;
        if (Has(newId, prefix)) return false;
        IntPtr p;
        if (!CredRead(Target(oldId, prefix), CredTypeGeneric, 0, out p)) return false;
        try
        {
            var src = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
            var c = new CREDENTIAL
            {
                Type = CredTypeGeneric, TargetName = Target(newId, prefix), Comment = src.Comment,
                CredentialBlobSize = src.CredentialBlobSize, CredentialBlob = src.CredentialBlob,   // points into p, valid until CredFree
                Persist = src.Persist > 0 ? src.Persist : CredPersistLocalMachine, UserName = newId
            };
            return CredWrite(ref c, 0);
        }
        catch { return false; }
        finally { CredFree(p); }
    }

    public static bool Delete(string id, string prefix = null)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (CredDelete(Target(id, prefix), CredTypeGeneric, 0)) return true;
        return Marshal.GetLastWin32Error() == ErrorNotFound;
    }
}
