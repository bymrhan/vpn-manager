using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

// =====================================================================
// Yedekleme: OpenVPN Connect profillerini .ovpn dosyaları + manifest ile zip'ler.
// Kayıtlı parolalar (savedPassword / privateKeyPassword) hiçbir zaman okunmaz ve yazılmaz.
// Yedek özel anahtar içerebilir; bu yüzden kullanıcıya uyarı metni de eklenir.
// =====================================================================

internal static class BackupService
{
    const string FolderName = "OpenVPN Yedekleri";
    const string ZipPrefix = "OpenVPN-Yedek-";
    const string ZipPattern = "OpenVPN-Yedek-*.zip";
    const string ManifestName = "manifest.json";
    const string ReadmeName = "BENIOKU.txt";
    const string OvpnExt = ".ovpn";
    const int MaxBaseNameLength = 100;

    static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string DefaultFolder
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FolderName); }
    }

    // OpenVPN Connect keeps the real profile text in <AppData>\OpenVPN Connect\profiles\<id>.ovpn.
    static string ProfilesDir
    {
        get { return Path.Combine(Path.GetDirectoryName(ProfileStore.ConfigPath), "profiles"); }
    }

    // Creates one zip with "<profile>.ovpn" files, manifest.json and BENIOKU.txt.
    // count = number of .ovpn files written (profiles without readable config are listed in the manifest with file = null).
    public static string CreateBackup(string folder, ICollection<string> pinnedIds, out int count)
    {
        if (string.IsNullOrWhiteSpace(folder)) folder = DefaultFolder;
        Directory.CreateDirectory(folder);

        var profiles = ProfileStore.Load();
        var pinned = pinnedIds != null ? new HashSet<string>(pinnedIds) : new HashSet<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifest = new List<object>();
        var files = new List<KeyValuePair<string, byte[]>>();

        foreach (var p in profiles)
        {
            string fileName = null;
            var bytes = GetOvpnBytes(p.Id);
            if (bytes != null)
            {
                fileName = UniqueFileName(SafeBaseName(p.Name), usedNames);
                files.Add(new KeyValuePair<string, byte[]>(fileName, bytes));
            }

            var m = new Dictionary<string, object>();
            m["id"] = p.Id;
            m["name"] = p.Name;
            m["host"] = p.Host;
            m["username"] = p.Username;
            m["source"] = p.Source;
            m["lastConnected"] = p.LastConnected;
            m["pinned"] = pinned.Contains(p.Id);
            m["file"] = fileName;
            manifest.Add(m);
        }

        var manifestJson = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(manifest);
        var readme = BuildReadme(files.Count, profiles.Count);

        var finalPath = Path.Combine(folder, NewZipName(folder));
        var partPath = finalPath + ".part"; // not matched by ZipPattern, so Prune never sees it
        try
        {
            using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, false))
            {
                foreach (var f in files) WriteEntry(zip, f.Key, f.Value);
                WriteEntry(zip, ManifestName, new UTF8Encoding(false).GetBytes(manifestJson));
                WriteEntry(zip, ReadmeName, new UTF8Encoding(true).GetBytes(readme));
            }
            File.Move(partPath, finalPath);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }

        count = files.Count;
        return finalPath;
    }

    // Writes one profile as a single .ovpn file (bytes copied as-is, no re-encoding).
    public static void ExportProfile(string profileId, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Hedef dosya yolu boş olamaz.", "targetPath");
        var bytes = GetOvpnBytes(profileId);
        if (bytes == null) throw new InvalidOperationException("Bu profilin .ovpn yapılandırması bulunamadı.");

        var full = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        var partPath = full + ".part";
        try
        {
            using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
                fs.Write(bytes, 0, bytes.Length);
            if (File.Exists(full)) File.Replace(partPath, full, null);
            else File.Move(partPath, full);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    // Returns the .ovpn text of a profile, or null when it is not available.
    public static string GetOvpnText(string profileId)
    {
        var bytes = GetOvpnBytes(profileId);
        return bytes == null ? null : DecodeText(bytes);
    }

    // Deletes the oldest "OpenVPN-Yedek-*.zip" files beyond 'keep'. Returns the number deleted.
    public static int Prune(string folder, int keep)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
        if (keep < 0) keep = 0;

        var zips = new DirectoryInfo(folder).GetFiles(ZipPattern)
            .Where(f => f.Name.StartsWith(ZipPrefix, StringComparison.OrdinalIgnoreCase)
                     && f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var deleted = 0;
        foreach (var f in zips.Skip(keep))
        {
            try { f.Delete(); deleted++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }

    // lastBackupUtc / configStampUtc must be UTC.
    // "off" and unknown values never run; "change" runs when the OpenVPN config is newer than the last backup.
    public static bool AutoBackupDue(DateTime lastBackupUtc, string frequency, DateTime configStampUtc)
    {
        var f = (frequency ?? "").Trim().ToLowerInvariant();
        switch (f)
        {
            case "change": return configStampUtc > lastBackupUtc;
            case "daily": return DateTime.UtcNow - lastBackupUtc >= TimeSpan.FromDays(1);
            case "weekly": return DateTime.UtcNow - lastBackupUtc >= TimeSpan.FromDays(7);
            default: return false;
        }
    }

    // ----- helpers -----

    static byte[] GetOvpnBytes(string profileId)
    {
        if (!IsSafeId(profileId)) return null;
        try
        {
            var primary = TryRead(Path.Combine(ProfilesDir, profileId + OvpnExt));
            if (primary != null) return primary;

            // Fallback: the original import path stored in config.json (may no longer exist).
            var source = SourcePathFor(profileId);
            if (!string.IsNullOrEmpty(source) && Path.IsPathRooted(source)) return TryRead(source);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    static byte[] TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = ReadAllShared(path);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static byte[] ReadAllShared(string path)
    {
        // OpenVPN Connect may be writing the file at the same time; allow shared access.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            return ms.ToArray();
        }
    }

    // Reads only the "filePath" of one profile from config.json (no secrets are read).
    static string SourcePathFor(string profileId)
    {
        var configPath = ProfileStore.ConfigPath;
        if (!File.Exists(configPath)) return null;

        string text;
        using (var fs = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();

        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        var root = js.DeserializeObject(text) as IDictionary<string, object>;
        if (root == null || !root.ContainsKey("persist:root")) return null;
        var persisted = js.DeserializeObject(Convert.ToString(root["persist:root"])) as IDictionary<string, object>;
        if (persisted == null || !persisted.ContainsKey("status")) return null;
        var status = js.DeserializeObject(Convert.ToString(persisted["status"])) as IDictionary<string, object>;
        if (status == null) return null;
        var profiles = status.ContainsKey("profiles") ? status["profiles"] as IDictionary<string, object> : null;
        if (profiles == null) return null;

        foreach (var pair in profiles)
        {
            var item = pair.Value as IDictionary<string, object>;
            if (item == null) continue;
            var id = item.ContainsKey("id") && item["id"] != null ? Convert.ToString(item["id"]) : pair.Key;
            if (!string.Equals(id, profileId, StringComparison.Ordinal)) continue;
            object fp;
            return item.TryGetValue("filePath", out fp) && fp != null ? Convert.ToString(fp) : null;
        }
        return null;
    }

    // Profile ids are used as file names, so only plain id characters are accepted.
    static bool IsSafeId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 200) return false;
        if (id == "." || id == "..") return false;
        foreach (var c in id)
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;
        return true;
    }

    static string DecodeText(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    // Windows-safe base name (without extension).
    static string SafeBaseName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name ?? "")
            sb.Append(Array.IndexOf(invalid, c) >= 0 || char.IsWhiteSpace(c) ? ' ' : c);

        var s = sb.ToString();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        s = s.Trim().TrimEnd('.', ' ');
        if (s.Length > MaxBaseNameLength) s = s.Substring(0, MaxBaseNameLength).TrimEnd('.', ' ');
        if (s.Length == 0) s = "profil";

        var upper = s.ToUpperInvariant();
        foreach (var r in ReservedNames)
            if (upper == r) return "_" + s;
        return s;
    }

    // "name.ovpn", then "name (2).ovpn", "name (3).ovpn"... (case-insensitive, as on NTFS).
    static string UniqueFileName(string baseName, HashSet<string> used)
    {
        var candidate = baseName + OvpnExt;
        var n = 2;
        while (used.Contains(candidate))
        {
            candidate = baseName + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + OvpnExt;
            n++;
        }
        used.Add(candidate);
        return candidate;
    }

    static string NewZipName(string folder)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var name = ZipPrefix + stamp + ".zip";
        var n = 2;
        while (File.Exists(Path.Combine(folder, name)) || File.Exists(Path.Combine(folder, name + ".part")))
        {
            name = ZipPrefix + stamp + "-" + n.ToString(CultureInfo.InvariantCulture) + ".zip";
            n++;
        }
        return name;
    }

    static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name);
        using (var s = entry.Open()) s.Write(data, 0, data.Length);
    }

    static string BuildReadme(int ovpnCount, int profileCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("OpenVPN Hızlı Arama - Profil Yedeği");
        sb.AppendLine("====================================");
        sb.AppendLine("Oluşturma tarihi : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        sb.AppendLine("Profil sayısı    : " + profileCount.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Yedeklenen .ovpn : " + ovpnCount.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();
        sb.AppendLine("İÇERİK");
        sb.AppendLine("- *.ovpn        : Her profilin OpenVPN yapılandırma dosyası (sunucu, sertifika ve özel anahtar dahil).");
        sb.AppendLine("- manifest.json : Profillerin listesi (id, ad, sunucu, kullanıcı adı, kaynak dosya, son bağlantı, sabit, dosya adı).");
        sb.AppendLine("- BENIOKU.txt   : Bu açıklama.");
        sb.AppendLine("Yapılandırma dosyası bulunamayan profiller manifest.json içinde \"file\": null ile işaretlenir.");
        sb.AppendLine();
        sb.AppendLine("ÖNEMLİ");
        sb.AppendLine("- Kayıtlı parolalar bu yedekte YOKTUR. Kayıtlı parola ve özel anahtar parolaları dışarıda bırakılmıştır.");
        sb.AppendLine("- Yedek özel anahtar içerir. Bu dosyayı güvenli bir yerde saklayın; e-posta veya ortak klasörle paylaşmayın.");
        sb.AppendLine("- Parola olmadığı için geri yükledikten sonra bağlanırken parolanızı yeniden girmeniz gerekebilir.");
        sb.AppendLine();
        sb.AppendLine("GERİ YÜKLEME");
        sb.AppendLine("1. OpenVPN Connect uygulamasını açın.");
        sb.AppendLine("2. Profil ekleme (içe aktarma) bölümünden Dosya seçeneğini kullanın.");
        sb.AppendLine("3. Bu zip'ten çıkardığınız .ovpn dosyasını seçin, profili kontrol edip bağlanın.");
        sb.AppendLine("4. Hangi .ovpn dosyasının hangi profil olduğunu manifest.json dosyasındaki \"name\" ve \"file\" alanlarından görebilirsiniz.");
        return sb.ToString();
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
