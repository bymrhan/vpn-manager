using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

// =====================================================================
// OpenVPN Connect profile sources (2.1.2).
// The config.json layout (persist:root -> status -> profiles, both levels JSON strings) was verified
// on OpenVPN Connect 3.9.x only. Other versions (3.6.0 reported) may store it differently, so:
//   1. ProfileJson parses tolerantly (string or object levels, map or array, alternate key names,
//      a bounded walker for a profile collection at another level);
//   2. ProfileSources tries other config.json locations, then the .ovpn files OpenVPN Connect keeps
//      in its profiles folder, then the folder / files the user picked by hand;
//   3. SchemaOutline shows the layout as key names and JSON types only (never values) for the log
//      and the "Tanı bilgisini kopyala" button.
// Profiles built from .ovpn files get id = file name without ".ovpn" and EstimatedId = true:
// OpenVPN Connect stores a profile as profiles\<id>.ovpn and "--connect-shortcut=<id>" takes that
// id (assumption, true for 3.9). For a file outside that folder the id is only a guess.
// =====================================================================

internal enum ProfileReadReason { Ok, FileMissing, Unreadable, BadJson, UnknownLayout, NoProfiles }

internal sealed class ProfileParseResult
{
    public List<VpnProfile> Profiles = new List<VpnProfile>();
    public ProfileReadReason Reason = ProfileReadReason.Ok;
    public string Layout = "";   // where the profiles were found, key names only ("persist:root/status/profiles")
}

// Pure functions over JSON text: no file access, testable.
internal static class ProfileJson
{
    public static readonly string[] NameKeys = { "profileDisplayName", "profileName", "name", "displayName", "title" };
    public static readonly string[] HostKeys = { "hostname", "remoteHost", "host", "server", "remote" };
    public static readonly string[] FileKeys = { "filePath", "path", "file", "source" };
    public static readonly string[] UserKeys = { "username", "user" };
    public static readonly string[] TimeKeys = { "lastConnected", "lastConnectedAt" };
    public const int MaxDepth = 6, MaxNodes = 5000, MaxOutlineLines = 150;
    static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly string[] CollectionNames = { "profiles", "items", "byId", "entities", "list", "connections", "servers", "entries" };

    static JavaScriptSerializer Js() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 }; }

    static IDictionary<string, object> Dict(object v) { return v as IDictionary<string, object>; }
    static object Get(IDictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
    static bool IsCollection(object v) { return v is IDictionary<string, object> || v is object[]; }

    // A string that holds JSON (OpenVPN Connect 3.9 nests persist:root and status that way) is
    // parsed; anything else comes back unchanged.
    public static object Expand(object v, JavaScriptSerializer js)
    {
        var s = v as string;
        if (s == null) return v;
        var t = s.TrimStart();
        if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) return v;
        try { var r = js.DeserializeObject(s); return r ?? v; } catch { return v; }
    }

    // (key, value) pairs of a map (key = map key) or an array (key = null).
    static IEnumerable<KeyValuePair<string, object>> Elements(object coll)
    {
        var d = Dict(coll);
        if (d != null) return d;
        var a = coll as object[];
        if (a != null) return a.Select(x => new KeyValuePair<string, object>(null, x));
        return new KeyValuePair<string, object>[0];
    }

    // Scalar text; objects and arrays give "" (never "System.Object[]").
    static string Text(IDictionary<string, object> d, string k)
    {
        var v = Get(d, k);
        if (v == null || v is IDictionary<string, object> || v is object[] || v is ArrayList) return "";
        var s = v as string;
        return s ?? Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    static string FirstText(IDictionary<string, object> d, string[] keys)
    {
        foreach (var k in keys) { var t = Text(d, k); if (!string.IsNullOrWhiteSpace(t)) return t; }
        return "";
    }

    static bool HasAny(IDictionary<string, object> d, string[] keys) { return FirstText(d, keys).Length > 0; }

    // Epoch milliseconds from epoch ms, epoch seconds (below 1e11) or an ISO-8601 text; 0 when unknown.
    public static long ParseTimestamp(object v)
    {
        if (v == null || v is bool || v is IDictionary<string, object> || v is object[]) return 0;
        double n;
        var s = v as string;
        if (s != null)
        {
            s = s.Trim();
            if (s.Length == 0) return 0;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n))
            {
                DateTime dt;
                if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt)) return 0;
                var ms = (dt - Epoch).TotalMilliseconds;
                return ms > 0 ? (long)ms : 0;
            }
        }
        else
        {
            try { n = Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
        if (double.IsNaN(n) || double.IsInfinity(n) || n <= 0) return 0;
        if (n < 1e11) n *= 1000;          // seconds
        if (n > 1e15) return 0;            // not a time
        return (long)n;
    }

    // One profile from an item; key = its map key (null in an array). null when no id can be found.
    public static VpnProfile FromItem(IDictionary<string, object> item, string key)
    {
        if (item == null) return null;
        var id = Text(item, "id");
        if (string.IsNullOrWhiteSpace(id)) id = key ?? "";
        if (string.IsNullOrWhiteSpace(id)) id = Text(item, "profileId");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var name = FirstText(item, NameKeys);
        var source = FirstText(item, FileKeys);
        long last = 0;
        foreach (var k in TimeKeys) { last = ParseTimestamp(Get(item, k)); if (last > 0) break; }
        var p = new VpnProfile
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? "Adsız profil" : name.Trim(),
            Host = FirstText(item, HostKeys).Trim(),
            Username = FirstText(item, UserKeys).Trim(),
            Source = string.IsNullOrWhiteSpace(source) ? "" : SafeFileName(source),
            LastConnected = last
        };
        ProfileStore.Finish(p);
        return p;
    }

    static string SafeFileName(string path) { try { return Path.GetFileName(path); } catch { return path; } }

    public static List<VpnProfile> FromCollection(object coll, JavaScriptSerializer js)
    {
        var result = new List<VpnProfile>();
        foreach (var kv in Elements(coll))
        {
            var p = FromItem(Dict(Expand(kv.Value, js)), kv.Key);
            if (p != null) result.Add(p);
        }
        return result;
    }

    // An element looks like a profile: a name-like key and an id-, host- or file-like key.
    static bool LooksLikeProfile(IDictionary<string, object> d)
    {
        return d != null && HasAny(d, NameKeys) && (HasAny(d, new[] { "id", "profileId" }) || HasAny(d, HostKeys) || HasAny(d, FileKeys));
    }

    // A non-empty collection where at least half of the elements (and at least one) look like profiles.
    public static bool LooksLikeProfiles(object coll, JavaScriptSerializer js)
    {
        int total = 0, like = 0;
        foreach (var kv in Elements(coll))
        {
            if (++total > 200) break;
            if (LooksLikeProfile(Dict(Expand(kv.Value, js)))) like++;
        }
        return like > 0 && like * 2 >= Math.Min(total, 200);
    }

    // Finds the profile collection: the 3.9 path first, then other plausible levels, then a bounded
    // breadth-first walk. layout gets the path in key names (ids collapsed to <id>).
    public static object FindProfilesNode(object rootObj, JavaScriptSerializer js, out string layout)
    {
        layout = "";
        rootObj = Expand(rootObj, js);
        var root = Dict(rootObj);
        if (root == null)
        {
            if (rootObj is object[] && LooksLikeProfiles(rootObj, js)) { layout = "(kök dizi)"; return rootObj; }
            return null;
        }
        var persisted = Dict(Expand(Get(root, "persist:root"), js));
        var status = Dict(Expand(Get(persisted, "status"), js));
        var profileNode = Dict(Expand(Get(persisted, "profile"), js));
        var known = new[]
        {
            new KeyValuePair<string, object>("persist:root/status/profiles", Get(status, "profiles")),
            new KeyValuePair<string, object>("profiles", Get(root, "profiles")),
            new KeyValuePair<string, object>("persist:root/profiles", Get(persisted, "profiles")),
            new KeyValuePair<string, object>("persist:root/profile/items", Get(profileNode, "items")),
        };
        object empty = null; string emptyLayout = null;
        foreach (var k in known)
        {
            var v = Expand(k.Value, js);
            if (!IsCollection(v)) continue;
            var els = Elements(v).ToList();
            if (els.Count == 0) { if (empty == null) { empty = v; emptyLayout = k.Key; } continue; }
            if (els.Any(e => Dict(Expand(e.Value, js)) != null)) { layout = k.Key; return v; }
        }
        string walked;
        var found = Walk(root, js, out walked);
        if (found != null) { layout = walked; return found; }
        if (empty != null) { layout = emptyLayout; return empty; }
        // 3.9 shape without a "profiles" key (nothing added yet): no profiles, as before 2.1.2.
        if (status != null && !status.ContainsKey("profiles")) { layout = "persist:root/status"; return new Dictionary<string, object>(); }
        return null;
    }

    static object Walk(IDictionary<string, object> root, JavaScriptSerializer js, out string layout)
    {
        layout = "";
        var queue = new Queue<Tuple<object, string, int>>();
        queue.Enqueue(Tuple.Create((object)root, "", 0));
        var nodes = 0;
        while (queue.Count > 0 && nodes < MaxNodes)
        {
            var cur = queue.Dequeue(); nodes++;
            if (cur.Item3 >= MaxDepth) continue;
            var d = Dict(cur.Item1);
            var a = cur.Item1 as object[];
            IEnumerable<KeyValuePair<string, object>> children = d != null ? (IEnumerable<KeyValuePair<string, object>>)d
                : a != null ? a.Take(50).Select(x => new KeyValuePair<string, object>("[]", x)) : null;
            if (children == null) continue;
            foreach (var kv in children)
            {
                var child = Expand(kv.Value, js);
                if (!IsCollection(child)) continue;
                var path = cur.Item2.Length == 0 ? KeyLabel(kv.Key) : cur.Item2 + "/" + KeyLabel(kv.Key);
                if (LooksLikeProfiles(child, js)) { layout = "bulunan: " + path; return child; }
                queue.Enqueue(Tuple.Create(child, path, cur.Item3 + 1));
            }
        }
        return null;
    }

    public static ProfileParseResult Parse(string json)
    {
        var r = new ProfileParseResult();
        var js = Js();
        object root;
        try { root = js.DeserializeObject(json ?? ""); }
        catch { r.Reason = ProfileReadReason.BadJson; return r; }
        if (root == null) { r.Reason = ProfileReadReason.BadJson; return r; }
        string layout;
        var node = FindProfilesNode(root, js, out layout);
        if (node == null) { r.Reason = ProfileReadReason.UnknownLayout; return r; }
        r.Layout = layout;
        r.Profiles = FromCollection(node, js);
        r.Reason = r.Profiles.Count > 0 ? ProfileReadReason.Ok : ProfileReadReason.NoProfiles;
        return r;
    }

    // ---------- Schema outline: key names and JSON types only ----------

    static readonly Regex SafeKey = new Regex(@"^[A-Za-z_$][A-Za-z0-9_$:-]{0,47}$", RegexOptions.CultureInvariant);
    static readonly Regex ByKey = new Regex(@"^by[A-Z]|[a-z]By[A-Z]", RegexOptions.CultureInvariant);   // "byName", "profilesById"
    static readonly Regex GuidLike =new Regex(@"^[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}$|^[0-9a-fA-F]{12,}$", RegexOptions.CultureInvariant);

    public static bool IdLikeKey(string k)
    {
        if (string.IsNullOrEmpty(k)) return true;
        if (GuidLike.IsMatch(k)) return true;
        if (k.Count(char.IsDigit) >= 4) return true;
        return !SafeKey.IsMatch(k);
    }

    // A key as shown in an outline: identifier-like names stay, anything else (ids, names with
    // blanks or dots, non-ASCII) becomes <id>.
    public static string KeyLabel(string k) { return k == "[]" ? "[]" : IdLikeKey(k) ? "<id>" : k; }

    static string TypeName(object v)
    {
        if (v == null) return "null";
        if (v is string) return "string";
        if (v is bool) return "bool";
        if (v is int || v is long || v is decimal || v is double || v is float) return "number";
        return "other";
    }

    // A map keyed by ids (or names): its keys are never listed; one element stands for all.
    static bool IsIdMap(IDictionary<string, object> d, string label, JavaScriptSerializer js)
    {
        if (d.Count == 0) return false;
        if (CollectionNames.Contains(label, StringComparer.OrdinalIgnoreCase) || ByKey.IsMatch(label)) return true;
        if (d.Keys.Count(IdLikeKey) * 2 >= d.Count) return true;
        if (d.Count < 2) return false;
        var sets = d.Values.Take(5).Select(x => Dict(Expand(x, js))).ToList();
        if (sets.Any(x => x == null || x.Count < 3)) return false;
        var a = new HashSet<string>(sets[0].Keys); var b = new HashSet<string>(sets[1].Keys);
        var inter = a.Count(b.Contains); var union = a.Union(b).Count();
        return union > 0 && inter * 10 >= union * 6;
    }

    static object Representative(IEnumerable<object> values, JavaScriptSerializer js)
    {
        object best = null; var bestCount = -2;
        foreach (var v in values.Take(200))
        {
            var e = Expand(v, js); var d = Dict(e);
            var n = d != null ? d.Count : e is object[] ? 0 : -1;
            if (n > bestCount) { best = v; bestCount = n; }
        }
        return best;
    }

    public static List<string> SchemaOutlineLines(string json)
    {
        var lines = new List<string>();
        var js = Js();
        object root;
        try { root = js.DeserializeObject(json ?? ""); }
        catch (Exception ex) { lines.Add("(geçerli JSON değil: " + ex.GetType().Name + ")"); return lines; }
        Outline(root, "(kök)", 0, js, lines);
        return lines;
    }

    public static string SchemaOutline(string json) { return string.Join("\n", SchemaOutlineLines(json).ToArray()); }

    static void Outline(object v, string label, int depth, JavaScriptSerializer js, List<string> lines)
    {
        if (lines.Count >= MaxOutlineLines) { if (lines.Count == MaxOutlineLines) lines.Add("…"); return; }
        var indent = new string(' ', depth * 2);
        var note = "";
        if (v is string) { var e = Expand(v, js); if (!(e is string)) { v = e; note = "string→"; } }
        var d = Dict(v); var a = v as object[];
        if (d != null)
        {
            var map = IsIdMap(d, label, js);
            lines.Add(indent + label + " (" + note + "object, " + d.Count.ToString(CultureInfo.InvariantCulture) + (map ? " entries, keys <id>)" : " keys)"));
            if (d.Count == 0) return;
            if (depth >= MaxDepth) { lines.Add(indent + "  …"); return; }
            if (map) Outline(Representative(d.Values, js), "<id>", depth + 1, js, lines);
            else foreach (var kv in d) Outline(kv.Value, KeyLabel(kv.Key), depth + 1, js, lines);
        }
        else if (a != null)
        {
            lines.Add(indent + label + " (" + note + "array, " + a.Length.ToString(CultureInfo.InvariantCulture) + " items)");
            if (a.Length == 0) return;
            if (depth >= MaxDepth) { lines.Add(indent + "  …"); return; }
            Outline(Representative(a, js), "[]", depth + 1, js, lines);
        }
        else lines.Add(indent + label + " (" + TypeName(v) + ")");
    }
}

// .ovpn files: only a few directives are read (remote, setenv PROFILE / FRIENDLY_NAME and the
// OVPN_ACCESS_SERVER_PROFILE / FRIENDLY_NAME / friendly_name comments). Inline blocks such as
// <ca>, <cert>, <key>, <tls-auth> are skipped line by line and never kept; nothing is logged.
internal static class OvpnScan
{
    public const int MaxFiles = 500, MaxDepth = 2, MaxDirs = 200;
    public const long MaxFileBytes = 1024 * 1024;
    static readonly Regex OpenTag = new Regex(@"^<([A-Za-z0-9-]{1,40})>$", RegexOptions.CultureInvariant);
    static readonly Regex IdStem = new Regex(@"^[0-9a-fA-F_-]{6,}$|^[0-9]+$", RegexOptions.CultureInvariant);

    static string Clean(string s)
    {
        if (s == null) return null;
        s = s.Trim().Trim('"', '\'').Trim();
        if (s.Length == 0) return null;
        return s.Length > 120 ? s.Substring(0, 120) : s;
    }

    static string After(string text, string prefix)
    {
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? Clean(text.Substring(prefix.Length).TrimStart('=', ':', ' ', '\t')) : null;
    }

    public static VpnProfile FromText(string text, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? "") ?? "";
        if (stem.Trim().Length == 0) return null;
        string friendly = null, asProfile = null, envProfile = null, host = null, block = null;
        using (var sr = new StringReader(text ?? ""))
        {
            string line; var n = 0;
            while ((line = sr.ReadLine()) != null && ++n < 20000)
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (block != null)
                {
                    if (t.Equals("</" + block + ">", StringComparison.OrdinalIgnoreCase)) { block = null; continue; }
                    if (!block.Equals("connection", StringComparison.OrdinalIgnoreCase)) continue;   // secrets: skipped
                }
                else
                {
                    var m = OpenTag.Match(t);
                    if (m.Success) { block = m.Groups[1].Value; continue; }
                }
                if (t[0] == '#' || t[0] == ';')
                {
                    var c = t.TrimStart('#', ';').Trim();
                    friendly = friendly ?? After(c, "OVPN_ACCESS_SERVER_FRIENDLY_NAME=") ?? (c.StartsWith("friendly_name", StringComparison.OrdinalIgnoreCase) ? After(c, "friendly_name") : null);
                    asProfile = asProfile ?? After(c, "OVPN_ACCESS_SERVER_PROFILE=");
                    continue;
                }
                var parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts[0].Equals("remote", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2 && host == null) host = Clean(parts[1]);
                else if (parts[0].Equals("setenv", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                {
                    var value = Clean(string.Join(" ", parts.Skip(2).ToArray()));
                    if (parts[1].Equals("PROFILE", StringComparison.OrdinalIgnoreCase)) envProfile = envProfile ?? value;
                    else if (parts[1].Equals("FRIENDLY_NAME", StringComparison.OrdinalIgnoreCase)) friendly = friendly ?? value;
                }
            }
        }
        // A file named like an id (OpenVPN Connect's own profiles\<id>.ovpn) reads better as its host.
        var name = friendly ?? asProfile ?? envProfile ?? (IdStem.IsMatch(stem) && host != null ? host : stem);
        var p = new VpnProfile { Id = stem, Name = name.Trim(), Host = host ?? "", Username = "", Source = Path.GetFileName(fileName), EstimatedId = true };
        ProfileStore.Finish(p);
        return p;
    }

    public static VpnProfile FromFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            if (fi.Length > MaxFileBytes) return FromText("", fi.Name);
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true)) text = sr.ReadToEnd();
            return FromText(text, fi.Name);
        }
        catch { return null; }
    }

    // *.ovpn under the roots, breadth first: depth 0 = the root itself. Bounded by maxFiles and MaxDirs.
    public static List<string> FindFiles(IEnumerable<string> roots, int maxDepth, int maxFiles)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<KeyValuePair<string, int>>();
        foreach (var r in roots) if (!string.IsNullOrWhiteSpace(r)) queue.Enqueue(new KeyValuePair<string, int>(r, 0));
        var dirs = 0;
        while (queue.Count > 0 && files.Count < maxFiles && dirs < MaxDirs)
        {
            var cur = queue.Dequeue();
            string full;
            try { full = Path.GetFullPath(cur.Key); } catch { continue; }
            if (!seen.Add(full) || !Directory.Exists(full)) continue;
            dirs++;
            try
            {
                foreach (var f in Directory.GetFiles(full, "*.ovpn").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (!f.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase)) continue;   // "*.ovpn" also matches "*.ovpnx" on 8.3 names
                    files.Add(f);
                    if (files.Count >= maxFiles) break;
                }
                if (cur.Value < maxDepth)
                    foreach (var d in Directory.GetDirectories(full).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                        queue.Enqueue(new KeyValuePair<string, int>(d, cur.Value + 1));
                    }
            }
            catch { }
        }
        return files;
    }
}

internal sealed class SourceAttempt
{
    public string Label = "";       // with %APPDATA% etc. and "/" separators, no user names
    public bool Exists, IsDir;
    public long Size;               // bytes (files) or .ovpn count (folders)
    public ProfileReadReason Reason = ProfileReadReason.FileMissing;
}

internal sealed class ProfileLoadReport
{
    public string Source = "none";  // "config", "profiles-dir", "none"
    public string SourceLabel = "";
    public ProfileReadReason PrimaryReason = ProfileReadReason.FileMissing;
    public bool PrimaryExists;
    public long PrimarySize;
    public string Layout = "";
    public int Count, ManualCount, ManualMissing;
    public bool ManualConfigured;
    public List<SourceAttempt> Attempts = new List<SourceAttempt>();

    // The primary config.json did not give the list: fallbacks were (or are) in use.
    public bool Degraded { get { return Source != "config" || SourceLabel != ProfileSources.PrimaryLabel; } }
    // Probably a half-written file: the caller keeps the old list and retries.
    public bool Transient { get { return PrimaryExists && (PrimaryReason == ProfileReadReason.Unreadable || PrimaryReason == ProfileReadReason.BadJson); } }
}

internal static class ProfileSources
{
    // Test hooks: folders used instead of %APPDATA%, %LOCALAPPDATA%, %PROGRAMDATA%.
    internal static string AppDataOverride, LocalAppDataOverride, ProgramDataOverride;
    public const string PrimaryLabel = "%APPDATA%/OpenVPN Connect/config.json";
    static readonly HashSet<string> logged = new HashSet<string>(StringComparer.Ordinal);
    static readonly object gate = new object();

    static string AppData { get { return AppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
    static string LocalAppData { get { return LocalAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
    static string ProgramData { get { return ProgramDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }

    // label, path. The primary (verified on 3.9) first. The install folder (%ProgramFiles%) is not a data folder and is never read.
    public static List<KeyValuePair<string, string>> ConfigCandidates()
    {
        var list = new List<KeyValuePair<string, string>>();
        Add(list, PrimaryLabel, AppData, "config.json");
        Add(list, "%LOCALAPPDATA%/OpenVPN Connect/config.json", LocalAppData, "config.json");
        Add(list, "%PROGRAMDATA%/OpenVPN Connect/config.json", ProgramData, "config.json");
        return list;
    }

    // The folders where OpenVPN Connect keeps <id>.ovpn files.
    public static List<KeyValuePair<string, string>> ProfileDirs()
    {
        var list = new List<KeyValuePair<string, string>>();
        Add(list, "%APPDATA%/OpenVPN Connect/profiles", AppData, "profiles");
        Add(list, "%LOCALAPPDATA%/OpenVPN Connect/profiles", LocalAppData, "profiles");
        return list;
    }

    static void Add(List<KeyValuePair<string, string>> list, string label, string root, string leaf)
    {
        if (string.IsNullOrEmpty(root)) return;
        string path;
        try { path = Path.GetFullPath(Path.Combine(root, "OpenVPN Connect", leaf)); } catch { return; }
        if (list.Any(x => string.Equals(x.Value, path, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(new KeyValuePair<string, string>(label, path));
    }

    // Reads one config.json: never throws. text is the file content when it could be read.
    public static ProfileParseResult ReadConfig(string path, SourceAttempt at, out string text)
    {
        text = null;
        var r = new ProfileParseResult();
        try { at.Exists = File.Exists(path); } catch { at.Exists = false; }
        if (!at.Exists) { r.Reason = at.Reason = ProfileReadReason.FileMissing; return r; }
        try
        {
            at.Size = new FileInfo(path).Length;
            // OpenVPN Connect may be writing the file at the same time; allow shared access.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
        }
        catch { r.Reason = at.Reason = ProfileReadReason.Unreadable; return r; }
        r = ProfileJson.Parse(text);
        at.Reason = r.Reason;
        return r;
    }

    // OpenVPN profiles from the first source that has any, plus the manual sources. Never throws.
    public static List<VpnProfile> Load(AppSettings s, out ProfileLoadReport rep)
    {
        rep = new ProfileLoadReport();
        List<VpnProfile> main = null;
        string primaryText = null;
        var first = true;
        foreach (var c in ConfigCandidates())
        {
            var at = new SourceAttempt { Label = c.Key };
            string text;
            var pr = ReadConfig(c.Value, at, out text);
            rep.Attempts.Add(at);
            if (first) { rep.PrimaryReason = at.Reason; rep.PrimaryExists = at.Exists; rep.PrimarySize = at.Size; primaryText = text; first = false; }
            if (pr.Reason == ProfileReadReason.Ok) { main = pr.Profiles; rep.Source = "config"; rep.SourceLabel = c.Key; rep.Layout = pr.Layout; break; }
        }
        if (main == null)
        {
            var dirs = ProfileDirs();
            var found = new List<VpnProfile>();
            foreach (var d in dirs)
            {
                var at = new SourceAttempt { Label = d.Key, IsDir = true };
                try { at.Exists = Directory.Exists(d.Value); } catch { }
                if (at.Exists)
                {
                    var files = OvpnScan.FindFiles(new[] { d.Value }, 1, OvpnScan.MaxFiles - found.Count);
                    at.Size = files.Count;
                    foreach (var f in files) { var p = OvpnScan.FromFile(f); if (p != null && !found.Any(x => x.Id == p.Id)) found.Add(p); }
                    at.Reason = files.Count > 0 ? ProfileReadReason.Ok : ProfileReadReason.NoProfiles;
                    if (found.Count > 0 && rep.Source == "none") { rep.Source = "profiles-dir"; rep.SourceLabel = d.Key; }
                }
                rep.Attempts.Add(at);
            }
            main = found;
        }
        rep.Count = main.Count;
        foreach (var p in Manual(s, rep))
        {
            if (main.Any(x => x.Id == p.Id || (x.Source.Length > 0 && string.Equals(x.Source, p.Source, StringComparison.OrdinalIgnoreCase)))) continue;
            main.Add(p); rep.ManualCount++;
        }
        if (rep.PrimaryExists && rep.PrimaryReason != ProfileReadReason.Ok) LogOnce(rep, primaryText);
        return main;
    }

    // Profiles from the folder picked in settings (one level of subfolders) and from the picked files.
    static List<VpnProfile> Manual(AppSettings s, ProfileLoadReport rep)
    {
        var list = new List<VpnProfile>();
        if (s == null) return list;
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.ProfileFolder)) { rep.ManualConfigured = true; paths.AddRange(OvpnScan.FindFiles(new[] { s.ProfileFolder }, 1, OvpnScan.MaxFiles)); }
        if (s.ExtraProfileFiles != null)
            foreach (var f in s.ExtraProfileFiles.Take(OvpnScan.MaxFiles))
            {
                rep.ManualConfigured = true;
                bool exists; try { exists = File.Exists(f); } catch { exists = false; }
                if (exists) paths.Add(f); else rep.ManualMissing++;
            }
        foreach (var f in paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(OvpnScan.MaxFiles))
        {
            var p = OvpnScan.FromFile(f);
            if (p == null || list.Any(x => x.Id == p.Id)) continue;
            p.Origin = "manual";
            list.Add(p);
        }
        return list;
    }

    // ---------- Texts ----------

    public static string ReasonText(ProfileReadReason r)
    {
        switch (r)
        {
            case ProfileReadReason.FileMissing: return "OpenVPN Connect'in profil deposu (config.json) bulunamadı.";
            case ProfileReadReason.Unreadable: return "config.json açılamadı (dosya kilitli ya da erişim yok).";
            case ProfileReadReason.BadJson: return "config.json okunamadı: geçerli JSON değil.";
            case ProfileReadReason.UnknownLayout: return "config.json'da profil listesi bulunamadı: bu OpenVPN Connect sürümünün düzeni tanınmıyor.";
            case ProfileReadReason.NoProfiles: return "config.json'da kayıtlı profil yok (0 profil).";
            default: return "";
        }
    }

    // "Kaynak: config.json (12 profil)" / "Kaynak: profil klasörü (…)" / "Okunamadı: …"
    public static string StatusText(ProfileLoadReport r)
    {
        if (r == null) return "Henüz okunmadı";
        string s;
        if (r.Source == "config") s = "Kaynak: config.json" + (r.SourceLabel == PrimaryLabel ? "" : " (" + r.SourceLabel + ")") + " (" + r.Count + " profil)";
        else if (r.Source == "profiles-dir") s = "Kaynak: profil klasörü (" + r.Count + " profil, tahmini kimlik) · config.json: " + ReasonText(r.PrimaryReason);
        else if (r.ManualCount > 0) s = "Kaynak: elle seçilen dosyalar · config.json: " + ReasonText(r.PrimaryReason);
        else s = "Okunamadı: " + ReasonText(r.PrimaryReason);
        if (r.ManualCount > 0) s += " · elle eklenen: " + r.ManualCount;
        if (r.ManualMissing > 0) s += " · bulunamayan dosya: " + r.ManualMissing;
        return s;
    }

    // ---------- Diagnosis ----------

    // "sürüm 3.6.0 (exe) · sürüm 3.6.0 (kayıt)" from the installed exe and the uninstall registry key;
    // "bulunamadı". Each number follows "sürüm" so the LogSanitizer keeps four-part versions (not an IP).
    public static string OpenVpnVersion()
    {
        string exe = null, reg = null;
        try
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var path = Path.Combine(root, "OpenVPN Connect", "OpenVPNConnect.exe");
                if (!File.Exists(path)) continue;
                var vi = FileVersionInfo.GetVersionInfo(path);
                exe = !string.IsNullOrEmpty(vi.ProductVersion) ? vi.ProductVersion : vi.FileVersion;
                break;
            }
        }
        catch { }
        try { reg = RegistryVersion(); } catch { }
        if (exe == null && reg == null) return "bulunamadı";
        var parts = new List<string>();
        if (exe != null) parts.Add("sürüm " + exe.Trim() + " (exe)");
        if (reg != null && reg != exe) parts.Add("sürüm " + reg.Trim() + " (kayıt)");
        return string.Join(" · ", parts.ToArray());
    }

    static string RegistryVersion()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(hive, view))
                    using (var u = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (u == null) continue;
                        foreach (var name in u.GetSubKeyNames())
                            using (var k = u.OpenSubKey(name))
                            {
                                if (k == null) continue;
                                var dn = k.GetValue("DisplayName") as string;
                                if (dn == null || !dn.StartsWith("OpenVPN Connect", StringComparison.OrdinalIgnoreCase)) continue;
                                var v = k.GetValue("DisplayVersion") as string;
                                if (!string.IsNullOrEmpty(v)) return v;
                            }
                    }
                }
                catch { }
            }
        return null;
    }

    static string Yes(bool b) { return b ? "var" : "yok"; }

    // Everything the maintainer needs to see the layout; no values, no profile or file names.
    // Not sanitized here: callers run the LogSanitizer (CrashReporter.NewSanitizer) over it.
    public static string DiagnosticText(AppSettings s, ProfileLoadReport r, string primaryText)
    {
        var sb = new StringBuilder();
        sb.Append("OpenVPN Hızlı Arama ").Append(UpdateLogic.CurrentVersion).Append(" · profil kaynağı tanı bilgisi\n");
        sb.Append("OpenVPN Connect: ").Append(OpenVpnVersion()).Append('\n');
        sb.Append(StatusText(r)).Append('\n');
        sb.Append("Adaylar:\n");
        foreach (var a in r.Attempts)
        {
            sb.Append("  ").Append(a.Label).Append(" - ").Append(Yes(a.Exists));
            if (a.Exists) sb.Append(a.IsDir ? ", " + a.Size + " .ovpn" : ", " + a.Size + " bayt, sonuç " + a.Reason);
            sb.Append('\n');
        }
        sb.Append("  profil klasörü (ayar) - ").Append(s != null && !string.IsNullOrWhiteSpace(s.ProfileFolder) ? "seçili" : "seçilmedi").Append('\n');
        sb.Append("  elle eklenen dosyalar - ").Append(s != null && s.ExtraProfileFiles != null ? s.ExtraProfileFiles.Count : 0).Append(", bulunamayan ").Append(r.ManualMissing).Append('\n');
        if (r.Layout.Length > 0) sb.Append("Profil düğümü: ").Append(r.Layout).Append('\n');
        if (primaryText != null)
        {
            sb.Append("config.json düzeni (yalnızca anahtar adları ve JSON türleri):\n");
            foreach (var l in ProfileJson.SchemaOutlineLines(primaryText)) sb.Append("  ").Append(l).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    // Reads the primary config.json (for the outline) and builds DiagnosticText; never throws.
    public static string Diagnostic(AppSettings s)
    {
        ProfileLoadReport r;
        Load(s, out r);
        string text = null;
        try { var at = new SourceAttempt(); ReadConfig(ConfigCandidates()[0].Value, at, out text); } catch { }
        return DiagnosticText(s, r, text);
    }

    // One structured warning per distinct (reason, size) per run when the primary file exists but
    // gave no profiles: version, size, the key-name-only layout (sanitized) and the source used.
    static void LogOnce(ProfileLoadReport r, string primaryText)
    {
        var key = r.PrimaryReason + "|" + r.PrimarySize.ToString(CultureInfo.InvariantCulture);
        lock (gate) { if (!logged.Add(key)) return; }
        try
        {
            var san = CrashReporter.NewSanitizer();
            // sanitized line by line: the sanitizer cuts a single line at LogSanitizer.MaxLine characters
            var outline = primaryText == null ? "(okunamadı)" : string.Join(" ; ", ProfileJson.SchemaOutlineLines(primaryText).Take(80).Select(l => san.Sanitize(l.Trim())).ToArray());
            var msg = "config.json gave no profiles: reason " + r.PrimaryReason + ", OpenVPN Connect " + OpenVpnVersion() +
                      ", size " + r.PrimarySize.ToString(CultureInfo.InvariantCulture) + " bytes, source used " + (r.Source == "none" ? "none" : r.SourceLabel) +
                      " (" + r.Count + " profiles, manual " + r.ManualCount + ")";
            AppLog.Warn("profiles", san.Sanitize(msg) + ", layout: " + outline);
        }
        catch { }
    }
}
