using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

// =====================================================================
// Brave browser profiles (Microsoft 365 shortcuts). Brave keeps one profile per signed-in account
// but its profile picker has no search. This lists the profiles from Brave's "Local State" file
// and opens a chosen one with --profile-directory. Read-only: Brave's files are never written.
// On by default but only active when Brave is installed; Settings.ShowBrave = off scans nothing.
// =====================================================================
internal static class BraveStore
{
    public const string IdPrefix = "brave:";
    public const string AdminUrl = "https://admin.cloud.microsoft/";

    public static bool IsBrave(VpnProfile p) { return p != null && p.Id != null && p.Id.StartsWith(IdPrefix, StringComparison.Ordinal); }
    public static string DirOf(VpnProfile p) { return IsBrave(p) ? p.Id.Substring(IdPrefix.Length) : ""; }

    public static string ExePath
    {
        get
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            };
            foreach (var c in candidates) if (c.Length > 3 && File.Exists(c)) return c;
            return null;
        }
    }

    public static string LocalStatePath
    {
        get { return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", @"BraveSoftware\Brave-Browser\User Data\Local State"); }
    }

    public static bool IsInstalled { get { return ExePath != null && File.Exists(LocalStatePath); } }

    // Profiles from Local State JSON (profile.info_cache). Ephemeral (guest) profiles are skipped.
    // Name = the name shown in Brave's picker; Host = the profile folder; Username = the account e-mail when Brave has it.
    public static List<VpnProfile> Parse(string json)
    {
        var list = new List<VpnProfile>();
        var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
        if (root == null) return list;
        var profile = Get(root, "profile") as Dictionary<string, object>;
        var cache = profile == null ? null : Get(profile, "info_cache") as Dictionary<string, object>;
        if (cache == null) return list;
        foreach (var kv in cache)
        {
            var info = kv.Value as Dictionary<string, object>;
            if (info == null) continue;
            var eph = Get(info, "is_ephemeral");
            if (eph is bool && (bool)eph) continue;
            var name = (Get(info, "name") as string ?? "").Trim();
            if (name.Length == 0) name = kv.Key;
            var user = (Get(info, "user_name") as string ?? "").Trim();
            var p = new VpnProfile { Id = IdPrefix + kv.Key, Name = name, Host = kv.Key, Username = user, Source = "Microsoft 365" };
            ProfileStore.Finish(p);
            p.Keys = p.Keys.Concat(new[] { ProfileStore.SearchKey("BRAVE MICROSOFT 365 O365") }).ToArray();
            list.Add(p);
        }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static object Get(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) ? v : null; }

    public static List<VpnProfile> Load()
    {
        var path = LocalStatePath;
        if (!File.Exists(path)) return new List<VpnProfile>();
        string text;
        // Brave may be writing the file: shared read access.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
        var list = Parse(text);
        var last = LoadLast();
        foreach (var p in list) { long v; if (last.TryGetValue(DirOf(p), out v)) p.LastConnected = v; }
        return list;
    }

    // Opens the profile's Brave window. Returns an error text or null.
    // mode "admin": the Microsoft 365 admin center; "last": no address, so Brave shows the profile's own startup tabs.
    public static string Open(VpnProfile p, string mode)
    {
        var exe = ExePath;
        if (exe == null) return "Brave bulunamadı";
        var dir = DirOf(p);
        if (dir.Length == 0 || dir.IndexOfAny(new[] { '"', '\\', '/' }) >= 0) return "Geçersiz Brave profili";
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--profile-directory=\"" + dir + "\"" + (mode == "last" ? "" : " " + AdminUrl)) { UseShellExecute = false });
            RecordLaunch(dir);
            return null;
        }
        catch (Exception ex) { return "Brave açılamadı: " + ex.Message; }
    }

    // Own "last opened" list (profile folder -> unix ms), next to the settings.
    static string LastPath { get { return Path.Combine(Path.GetDirectoryName(SettingsStore.FilePath), "brave-last.txt"); } }

    public static Dictionary<string, long> LoadLast()
    {
        var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(LastPath))
                foreach (var line in File.ReadAllLines(LastPath, Encoding.UTF8))
                {
                    var i = line.LastIndexOf('\t'); long v;
                    if (i > 0 && long.TryParse(line.Substring(i + 1), out v)) d[line.Substring(0, i)] = v;
                }
        }
        catch { }
        return d;
    }

    static void RecordLaunch(string dir)
    {
        try
        {
            var d = LoadLast();
            d[dir] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            Directory.CreateDirectory(Path.GetDirectoryName(LastPath));
            File.WriteAllLines(LastPath, d.Select(kv => kv.Key + "\t" + kv.Value), new UTF8Encoding(false));
        }
        catch { }
    }

    // Brave's own icon, read from brave.exe (no logo file is shipped); null when Brave is missing.
    public static Image Logo()
    {
        try { var exe = ExePath; if (exe == null) return null; using (var ico = Icon.ExtractAssociatedIcon(exe)) return ico == null ? null : ico.ToBitmap(); }
        catch { return null; }
    }
}
