using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Web.Script.Serialization;

// Profile import / remove / backup extraction via OpenVPN Connect's command line.
// Flags verified in resources\app.asar (see the flag constants below).

internal sealed class ImportResult
{
    public int Imported, Skipped, Failed;
    public List<string> Messages = new List<string>();
}

internal static class ImportService
{
    // Verified: "--import-profile=<path-to-profile> --name=<profile-name> --username=<username> ..."
    const string FlagImportProfile = "--import-profile=";
    const string FlagName = "--name=";
    const string FlagUsername = "--username=";
    // Verified: "--remove-profile=<profile-id|profile-name>"
    const string FlagRemoveProfile = "--remove-profile=";
    // Verified in app.asar but not used here: "list-profiles", "connect-shortcut", "disconnect-shortcut".
    // No rename flag exists in OpenVPN Connect 3.9 (no "rename-profile" string in app.asar).

    const int WaitMs = 15000;
    const string NoRenameMessage = "OpenVPN Connect bu işlemi komut satırından desteklemiyor";
    const string NotFoundMessage = "OpenVPN Connect bulunamadı.";
    const string OvpnExtension = ".ovpn";
    const long MaxEntryBytes = 10L * 1024 * 1024;

    static readonly CultureInfo Tr = new CultureInfo("tr-TR");

    // ---------- Backup ----------

    // Extracts *.ovpn entries from a backup zip into tempDir. Returns the extracted file paths.
    // pinnedNames receives the "name" of every manifest entry whose "pinned" flag is true.
    // Throws InvalidDataException with a Turkish message when the zip cannot be read.
    public static List<string> ExtractBackup(string zipPath, string tempDir, out List<string> pinnedNames)
    {
        pinnedNames = new List<string>();
        var extracted = new List<string>();
        if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath)) throw new FileNotFoundException("Yedek dosyası bulunamadı.", zipPath);
        if (string.IsNullOrEmpty(tempDir)) throw new ArgumentException("Geçici klasör belirtilmedi.", "tempDir");

        Directory.CreateDirectory(tempDir);
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var manifest = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, "manifest.json", StringComparison.OrdinalIgnoreCase));
                if (manifest != null)
                {
                    try { ReadPinned(manifest, pinnedNames); }
                    catch (ArgumentException) { throw new InvalidDataException("Yedek manifest dosyası okunamadı."); }
                }

                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    // Zip-slip guard: only the bare file name is used, never the stored path.
                    var safeName = Path.GetFileName(entry.FullName.Replace('/', '\\'));
                    if (string.IsNullOrEmpty(safeName) || !safeName.EndsWith(OvpnExtension, StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Length > MaxEntryBytes) continue;

                    var target = UniqueName(tempDir, safeName, used);
                    using (var src = entry.Open())
                    using (var dst = File.Create(target)) src.CopyTo(dst);
                    extracted.Add(target);
                }
            }
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("Yedek dosyası okunamadı veya bozuk.");
        }
        return extracted;
    }

    // Reads the backup manifest and returns a display-name -> username map (usernames only; the
    // backup never contains passwords). Keys are file names without ".ovpn", matching ImportFiles.
    public static Dictionary<string, string> ReadUsernames(string zipPath)
    {
        var map = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath)) return map;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var manifest = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, "manifest.json", StringComparison.OrdinalIgnoreCase));
                if (manifest == null) return map;
                string text;
                using (var sr = new StreamReader(manifest.Open(), Encoding.UTF8)) text = sr.ReadToEnd();
                var items = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 }.DeserializeObject(text) as object[];
                if (items == null) return map;
                foreach (var raw in items)
                {
                    var item = raw as IDictionary<string, object>;
                    if (item == null) continue;
                    object user, file, name;
                    if (!item.TryGetValue("username", out user) || user == null || string.IsNullOrWhiteSpace(Convert.ToString(user))) continue;
                    var key = "";
                    if (item.TryGetValue("file", out file) && file != null) key = Path.GetFileNameWithoutExtension(Convert.ToString(file));
                    if (key == "" && item.TryGetValue("name", out name) && name != null) key = Convert.ToString(name).Trim();
                    if (key != "") map[key] = Convert.ToString(user).Trim();
                }
            }
        }
        catch { }
        return map;
    }

    static void ReadPinned(ZipArchiveEntry manifest, List<string> pinnedNames)
    {
        string text;
        using (var sr = new StreamReader(manifest.Open(), Encoding.UTF8)) text = sr.ReadToEnd();
        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
        var items = js.DeserializeObject(text) as object[];
        if (items == null) return;
        foreach (var raw in items)
        {
            var item = raw as IDictionary<string, object>;
            if (item == null) continue;
            object pinned;
            if (!item.TryGetValue("pinned", out pinned) || pinned == null) continue;
            if (!string.Equals(Convert.ToString(pinned, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase)) continue;
            object name;
            if (item.TryGetValue("name", out name) && name != null && !string.IsNullOrWhiteSpace(Convert.ToString(name)))
                pinnedNames.Add(Convert.ToString(name).Trim());
        }
    }

    static string UniqueName(string dir, string fileName, HashSet<string> used)
    {
        var stem = fileName.Substring(0, fileName.Length - OvpnExtension.Length);
        var candidate = fileName;
        var n = 2;
        while (used.Contains(candidate) || File.Exists(Path.Combine(dir, candidate)))
            candidate = stem + " (" + n++.ToString(CultureInfo.InvariantCulture) + ")" + OvpnExtension;
        used.Add(candidate);
        return Path.Combine(dir, candidate);
    }

    // ---------- Import ----------

    // Imports each .ovpn file with "--import-profile=<path> --name=<name>", sequentially.
    // Display name = file name without ".ovpn". Names already in existingNames (Turkish, case-insensitive) are skipped.
    public static ImportResult ImportFiles(IEnumerable<string> ovpnPaths, ICollection<string> existingNames)
    {
        return ImportFiles(ovpnPaths, existingNames, null);
    }

    // usernamesByName (optional): display-name -> username, pre-fills the profile's user name on
    // import (--username). Passwords are never handled here; the user enters them once per device.
    public static ImportResult ImportFiles(IEnumerable<string> ovpnPaths, ICollection<string> existingNames, IDictionary<string, string> usernamesByName)
    {
        var result = new ImportResult();
        if (ovpnPaths == null) return result;

        var known = new HashSet<string>();
        if (existingNames != null) foreach (var n in existingNames) if (!string.IsNullOrWhiteSpace(n)) known.Add(Key(n));

        var exe = FindOpenVpnPath();
        foreach (var path in ovpnPaths)
        {
            var fileName = path == null ? "" : Path.GetFileName(path);
            var name = DisplayName(fileName);
            if (string.IsNullOrEmpty(name)) { result.Failed++; result.Messages.Add("Geçersiz dosya adı: " + (fileName ?? "")); continue; }

            if (known.Contains(Key(name)))
            {
                result.Skipped++;
                result.Messages.Add("\"" + name + "\" zaten kayıtlı, atlandı.");
                continue;
            }
            if (!File.Exists(path))
            {
                result.Failed++;
                result.Messages.Add("\"" + name + "\" dosyası bulunamadı.");
                continue;
            }
            if (exe == null)
            {
                result.Failed++;
                result.Messages.Add(NotFoundMessage);
                continue;
            }

            var arguments = FlagImportProfile + Quote(path) + " " + FlagName + Quote(name);
            string user;
            if (usernamesByName != null && usernamesByName.TryGetValue(name, out user) && !string.IsNullOrWhiteSpace(user))
                arguments += " " + FlagUsername + Quote(user.Trim());

            string error;
            if (RunOpenVpn(exe, arguments, out error))
            {
                result.Imported++;
                known.Add(Key(name));
                result.Messages.Add("\"" + name + "\" içe aktarıldı.");
            }
            else
            {
                result.Failed++;
                result.Messages.Add("\"" + name + "\" içe aktarılamadı: " + error);
            }
        }
        return result;
    }

    // ---------- Remove / rename ----------

    public static bool RemoveProfile(string profileId, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(profileId)) { error = "Profil kimliği boş."; return false; }
        var exe = FindOpenVpnPath();
        if (exe == null) { error = NotFoundMessage; return false; }
        if (!RunOpenVpn(exe, FlagRemoveProfile + Quote(profileId.Trim()), out error)) return false;
        return true;
    }

    // OpenVPN Connect 3.9 has no rename flag, so this always fails with a Turkish message.
    public static bool RenameProfile(string profileId, string newName, out string error)
    {
        error = NoRenameMessage;
        return false;
    }

    // ---------- Temp cleanup ----------

    // Overwrites and deletes extracted profile files (they contain secrets). Never throws.
    public static void CleanupTemp(string tempDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tempDir) || !Directory.Exists(tempDir)) return;
            var full = Path.GetFullPath(tempDir).TrimEnd('\\');
            if (full.Length <= 3 || string.IsNullOrEmpty(Path.GetFileName(full))) return; // never wipe a drive root

            foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    fi.Attributes = FileAttributes.Normal;
                    if (fi.Length > 0)
                    {
                        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None))
                        {
                            var zeros = new byte[Math.Min(fi.Length, 65536L)];
                            long left = fi.Length;
                            while (left > 0)
                            {
                                var n = (int)Math.Min(zeros.Length, left);
                                fs.Write(zeros, 0, n);
                                left -= n;
                            }
                            fs.Flush();
                        }
                    }
                    File.Delete(file);
                }
                catch { }
            }
            Directory.Delete(tempDir, true);
        }
        catch { }
    }

    // ---------- Helpers ----------

    static bool RunOpenVpn(string exe, string arguments, out string error)
    {
        error = null;
        try
        {
            using (var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                if (p == null) { error = "işlem başlatılamadı."; return false; }
                if (!p.WaitForExit(WaitMs)) { error = "zaman aşımı (15 sn)."; return false; }
                if (p.ExitCode != 0) { error = "OpenVPN Connect hata kodu " + p.ExitCode.ToString(CultureInfo.InvariantCulture) + " döndürdü."; return false; }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    static string FindOpenVpnPath()
    {
        var roots = new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetEnvironmentVariable("ProgramFiles(x86)") };
        foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var p = Path.Combine(root, "OpenVPN Connect", "OpenVPNConnect.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static string DisplayName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "";
        if (fileName.EndsWith(OvpnExtension, StringComparison.OrdinalIgnoreCase)) fileName = fileName.Substring(0, fileName.Length - OvpnExtension.Length);
        return fileName.Trim();
    }

    // Turkish-culture, case-insensitive key: "I" and "ı" map to the same key, as do "i" and "İ".
    static string Key(string name) { return name.Trim().ToUpper(Tr); }

    static string Quote(string value)
    {
        return value.IndexOf(' ') >= 0 ? "\"" + value + "\"" : value;
    }
}
