using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

// =====================================================================
// Diagnostics: the app's own log, crash capture and the consent-based GitHub issue report.
//   LogRotation, LogRateLimiter, LogSanitizer, ErrorInfo / ErrorFingerprint, ErrorReport,
//   IssueLink, CrashPromptLogic - pure decisions, unit-tested (tests\Tests.cs).
//   LogWriter / AppLog - thread-safe, buffered file log (%APPDATA%\OpenVpnQuickSearch\logs).
//   CrashReporter      - unhandled exception hooks, crash records, the report flow.
// Nothing is ever uploaded by the app. A report is opened in the user's own browser as a
// pre-filled GitHub "new issue" page and the user presses Submit there. A public app cannot hold
// a GitHub write token (anyone could extract it), so there is no silent upload. The only network
// call is a read-only search for the report's 8-hex fingerprint (duplicate check), made after the
// user chose "Raporu aç". See README "Hata raporları ve günlükler" and docs\TRIAGE.md.
// =====================================================================

internal enum LogLevel { Info, Warn, Error }

// Log file names and rotation: app-YYYYMMDD.log, then app-YYYYMMDD-2.log ... once a part reaches
// MaxBytes; at most KeepFiles log files stay (oldest deleted). Crash / error records rotate the same way.
internal static class LogRotation
{
    public const long MaxBytes = 512 * 1024;
    public const int KeepFiles = 5;
    public const int KeepRecords = 5;
    static readonly Regex LogName = new Regex(@"^app-(\d{8})(?:-(\d{1,4}))?\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex RecordName = new Regex(@"^(crash|error)-(\d{8}-\d{6})(?:-(\d{1,3}))?\.txt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Day(DateTime t) { return t.ToString("yyyyMMdd", CultureInfo.InvariantCulture); }

    public static bool TryParse(string name, out string day, out int part)
    {
        day = null; part = 0;
        if (name == null) return false;
        var m = LogName.Match(name);
        if (!m.Success) return false;
        day = m.Groups[1].Value;
        part = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
        return true;
    }

    static string PartName(string day, int part) { return part <= 1 ? "app-" + day + ".log" : "app-" + day + "-" + part.ToString(CultureInfo.InvariantCulture) + ".log"; }

    // The file to append to now: today's newest part while it is under maxBytes, else the next part.
    public static string PickFile(DateTime now, IDictionary<string, long> existing, long maxBytes)
    {
        var day = Day(now); var part = 1;
        if (existing != null)
            foreach (var kv in existing) { string d; int p; if (TryParse(kv.Key, out d, out p) && d == day && p > part) part = p; }
        var name = PartName(day, part);
        long size;
        if (existing != null && TryGet(existing, name, out size) && size >= maxBytes) name = PartName(day, part + 1);
        return name;
    }

    static bool TryGet(IDictionary<string, long> d, string name, out long size)
    {
        foreach (var kv in d) if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { size = kv.Value; return true; }
        size = 0; return false;
    }

    // Log files to delete so that at most keep remain (newest by day, then part, are kept).
    public static List<string> ToDelete(IEnumerable<string> names, int keep)
    {
        var logs = new List<KeyValuePair<string, string>>();
        foreach (var n in names ?? new string[0])
        {
            string d; int p;
            if (TryParse(n, out d, out p)) logs.Add(new KeyValuePair<string, string>(n, d + p.ToString("D5", CultureInfo.InvariantCulture)));
        }
        return logs.OrderByDescending(kv => kv.Value, StringComparer.Ordinal).Skip(Math.Max(0, keep)).Select(kv => kv.Key).ToList();
    }

    static string RecordKey(Match m) { return m.Groups[2].Value + (m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture).ToString("D3", CultureInfo.InvariantCulture) : "000"); }

    // Crash / error records with this prefix ("crash" or "error") beyond keep, oldest first out.
    public static List<string> RecordsToDelete(IEnumerable<string> names, string prefix, int keep)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var n in names ?? new string[0])
        {
            var m = RecordName.Match(n ?? "");
            if (m.Success && string.Equals(m.Groups[1].Value, prefix, StringComparison.OrdinalIgnoreCase)) list.Add(new KeyValuePair<string, string>(n, RecordKey(m)));
        }
        return list.OrderByDescending(kv => kv.Value, StringComparer.Ordinal).Skip(Math.Max(0, keep)).Select(kv => kv.Key).ToList();
    }

    // Newest crash or error record by its time stamp; null when there is none.
    public static string NewestRecord(IEnumerable<string> names)
    {
        string best = null, bestKey = null;
        foreach (var n in names ?? new string[0])
        {
            var m = RecordName.Match(n ?? "");
            if (!m.Success) continue;
            var k = RecordKey(m);
            if (bestKey == null || string.CompareOrdinal(k, bestKey) > 0) { best = n; bestKey = k; }
        }
        return best;
    }

    public static bool IsRecordName(string name) { return name != null && RecordName.IsMatch(name); }
}

// At most one entry per key and window; counts what it dropped so the next entry can say so.
// Used for provider poll failures and repeating exceptions (a failing poll must not flood the log).
internal sealed class LogRateLimiter
{
    sealed class Entry { public DateTime Last; public int Suppressed; }
    readonly TimeSpan window;
    readonly Dictionary<string, Entry> map = new Dictionary<string, Entry>(StringComparer.Ordinal);
    readonly object gate = new object();

    public LogRateLimiter(TimeSpan window) { this.window = window; }

    public bool Allow(string key, DateTime now, out int suppressed)
    {
        suppressed = 0;
        lock (gate)
        {
            Entry e;
            if (!map.TryGetValue(key ?? "", out e))
            {
                if (map.Count >= 256) map.Clear();
                map[key ?? ""] = new Entry { Last = now };
                return true;
            }
            if (now - e.Last >= window || now < e.Last)
            {
                suppressed = e.Suppressed; e.Suppressed = 0; e.Last = now;
                return true;
            }
            e.Suppressed++;
            return false;
        }
    }
}

// Removes what must never reach the public issue tracker. Two passes:
//  (a) values known from memory (profile names and .ovpn file / folder names, hosts, usernames,
//      server entries, saved credential usernames, Windows user / machine / domain names, user
//      folders) -> stable placeholders. Matching ignores case the Turkish way too (i I İ ı, ş Ş s S,
//      ğ g, ü u, ö o, ç c are one letter each) and any run of blanks;
//  (b) patterns: PEM and inline OpenVPN key blocks, password / token / cookie / Authorization values,
//      command-line secrets (-p, -u user:pass), URL credentials, GitHub tokens, e-mail, user@host,
//      DOMAIN\user, UNC hosts and shares, C:\Users\<name>, .ovpn names, IPv4 (leading zeros too),
//      IPv6, long hex / base64 blobs, host-like tokens (name.tld, any letter case).
// Every line is cut to MaxLine characters first and every pattern has a match timeout, so a hostile
// or huge line costs milliseconds; a line that still times out becomes <satir-kisaltildi>.
// Placeholders are numbered per instance and stable inside one report (<profil-1>, <sunucu-2>,
// <ip-1>). Text inside our own placeholders is never matched again, so Sanitize is idempotent.
// Over-redaction is preferred to a leak.
internal sealed class LogSanitizer
{
    public const int MinKnownLength = 4;   // shorter names are not seeds (they would garble ordinary words)
    public const int MaxLine = 1000;       // longer lines / messages are cut before any pattern runs
    public const string LineDropped = "<satir-kisaltildi>";
    static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);
    const RegexOptions CI = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const RegexOptions CS = RegexOptions.CultureInvariant;

    static Regex R(string pattern, RegexOptions o) { return new Regex(pattern, o, Budget); }

    // Our placeholders (never rewritten), and the path roots we put in.
    static readonly Regex Protected = R(@"<(?:profil|sunucu|ip|kullanici|makine|alan|eposta|gizli|pem|anahtar|blob|metin|dosya|yol|satir-kisaltildi)(?:-\d+)?>|%(?:APPDATA|LOCALAPPDATA|TEMP)%", CS);

    static readonly Regex Pem = R(@"-----BEGIN [A-Z0-9 ]{1,40}-----[\s\S]*?(?:-----END [A-Z0-9 ]{1,40}-----|$)", CS);
    static readonly Regex InlineKey = R(@"<(ca|cert|key|tls-auth|tls-crypt|tls-crypt-v2|secret|pkcs12|extra-certs|dh|auth-user-pass)>[\s\S]*?(?:</\1>|$)", CI);
    // Values that may contain blanks: everything to the end of the line goes.
    static readonly Regex AuthHeader = R(@"\b(authorization|proxy-authorization|cookie|set-cookie)(\s*[:=]\s*)\S[^\r\n]*", CI);
    static readonly Regex LongSecret = R(@"\b(password|passwd|passphrase|pwd|secret|client_secret|parola|şifre|sifre)(\s*[:=]\s*)\S[^\r\n]*", CI);
    static readonly Regex Bearer = R(@"\b(bearer|basic|token|negotiate|ntlm)(\s+)[A-Za-z0-9._~+/=-]{8,}", CI);
    static readonly Regex KeyValue = R(@"\b(pass|token|access_token|refresh_token|id_token|api[_-]?key|apikey|key|auth|session(?:id)?|svpncookie)(\s*[:=]\s*)(""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;&""']+)", CI);
    static readonly Regex XHeader = R(@"\b(x-[\w-]*(?:token|key|secret|auth)[\w-]*)(\s*[:=]?\s*)([^\s,;&""']+)", CI);
    static readonly Regex UserValue = R(@"\b(user|username|user_name|login|uid)(\s*[:=]\s*)(""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;&""']+)", CI);
    static readonly Regex CliSecret = R(@"(^|[\s""'])(-p|-pw|-pass|--pass|--password|--token|--secret|/p|/password)(\s+|[:=])(""[^""\r\n]*""|'[^'\r\n]*'|[^\s""']+)", CI);
    static readonly Regex CliUser = R(@"(^|[\s""'])(-u|--user|--username|/u|/user)(\s+|[:=])(""[^""\r\n]*""|'[^'\r\n]*'|[^\s""']+)", CI);
    static readonly Regex CliName = R(@"(^|[\s""'])(-n|--name|--connection)(\s+|[:=])(""[^""\r\n]*""|'[^'\r\n]*'|[^\s""']+)", CI);
    static readonly Regex CliHost = R(@"(^|[\s""'])(-h|--host|-s|--server|--gateway)(\s+|[:=])(""[^""\r\n]*""|'[^'\r\n]*'|[^\s""']+)", CI);
    static readonly Regex UrlCredentials = R(@"\b([a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]+@", CI);
    static readonly Regex GitHubToken = R(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b", CS);
    static readonly Regex Email = R(@"(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", CS);
    // user@host where host has no dot or is an address (ssh admin@10.0.0.1, mehmet@intranet)
    static readonly Regex UserAtHost = R(@"(?<![\w.-])([\w.-]+)@([\w.-]+)", CS);
    // DOMAIN\user, not a path segment (the left side must not follow \ / : and must not be a folder name)
    static readonly Regex DomainUser = R(@"(?<![\\/:\w.$-])([A-Za-z0-9-]{2,15})\\([\w.$-]+)", CS);
    static readonly HashSet<string> NotDomains = new HashSet<string>(new[] {
        "users", "windows", "program", "programdata", "appdata", "system32", "syswow64", "documents", "desktop", "downloads",
        "temp", "tmp", "microsoft", "local", "roaming", "logs", "config", "profiles", "openvpn", "fortinet" }, StringComparer.OrdinalIgnoreCase);
    static readonly Regex Unc = R(@"\\\\([^\\/\s]+)\\([^\\/\s]*)", CS);
    static readonly Regex UserFolder = R(@"\b[a-z]:[\\/](?:users|documents and settings)[\\/][^\\/:*?""<>|\r\n]+", CI);
    // Folders of any other absolute path (customer folders): only the file name stays.
    static readonly Regex AbsPath = R(@"\b[A-Za-z]:[\\/](?:[^\\/:*?""<>|\r\n]+[\\/])+", CS);
    static readonly Regex Ipv4 = R(@"(?<![\d.])\d{1,3}(?:\.\d{1,3}){3}(?!\d|\.\d)", CS);
    static readonly Regex VersionBefore = R(@"(?:\bv|\bver|\bversion|\bsürüm|\bsurum|\brelease|\bbuild|\bclr)\s*[:=]?\s*$", CI);
    static readonly Regex Ipv6 = R(@"(?<![0-9a-f:.])(?:(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}|(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})?::(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})?)(?:%[0-9a-z]+)?(?![0-9a-f:])", CI);
    static readonly Regex HexBlob = R(@"(?<![0-9a-z])[0-9a-f]{32,}(?![0-9a-z])", CI);
    static readonly Regex Base64Blob = R(@"(?<![A-Za-z0-9+/=_-])[A-Za-z0-9+/_-]{40,}={0,2}(?![A-Za-z0-9+/=_-])", CS);
    // name.tld in any letter case ("VPN.CORP.EXAMPLE.COM", "gw.example.ZIP")
    static readonly Regex Host = R(@"(?<![\w.-])(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+([A-Za-z][A-Za-z0-9-]{0,22}[A-Za-z0-9])(?![\w-])", CS);
    static readonly Regex OvpnFile = R(@"(?<![^\\/:*?""<>|\s'])[^\\/:*?""<>|\s']+\.ovpn\b", CI);
    static readonly Regex Quoted = R(@"'[^'\r\n]{1,300}'|""[^""\r\n]{1,300}""|“[^”\r\n]{1,300}”|‘[^’\r\n]{1,300}’", CS);
    // A .NET type at the start of an exception line ("    System.IO.IOException: ..."): kept.
    static readonly Regex TypePrefix = R(@"^(\s*(?:--- iç hata: )?[A-Za-z_][\w.`+]*(?:Exception|Error)):", CS);

    // File extensions that look like a TLD; only a single-dot token ("Connection.cs") is kept.
    static readonly HashSet<string> FileExtensions = new HashSet<string>(new[] {
        "cs", "exe", "dll", "log", "txt", "json", "xml", "config", "ini", "ps1", "psm1", "psd1", "cmd", "bat", "pdb", "ovpn",
        "sha256", "yml", "yaml", "png", "svg", "ico", "tmp", "bak", "old", "lnk", "msi", "reg", "dat", "db",
        "sqlite", "cer", "crt", "pem", "key", "p12", "pfx", "html", "htm", "js", "css", "csv", "resources", "manifest", "sys" }, StringComparer.Ordinal);
    // Public hosts of this project that may appear in a report.
    static readonly HashSet<string> AllowedHosts = new HashSet<string>(new[] { "github.com", "api.github.com", "objects.githubusercontent.com", "codeload.github.com" }, StringComparer.OrdinalIgnoreCase);
    // Words of the app's own log lines: never seeds, or every report line would be garbled.
    static readonly HashSet<string> CommonWords = new HashSet<string>(new[] {
        "info", "warn", "warning", "error", "user", "users", "admin", "vpn", "connect", "connected", "disconnect", "disconnected",
        "state", "poll", "update", "forti", "fortinet", "sophos", "watchguard", "openvpn", "openvpn connect", "crash", "report", "start",
        "exit", "test", "default", "client", "server", "local", "true", "false", "null", "outcome", "requested", "failed", "profile",
        "profiles", "config", "temp", "desktop", "documents", "downloads", "public", "guest", "administrator", "system" }, StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);   // Fold(value) -> placeholder
    readonly List<string> known = new List<string>();
    int profiles, hosts, ips;
    Regex knownRegex;

    public int KnownCount { get { return known.Count; } }

    // One comparison key for all Turkish / ASCII case variants and blank runs.
    public static string Fold(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder(s.Length); var blank = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { if (!blank) sb.Append(' '); blank = true; continue; }
            blank = false;
            sb.Append(FoldChar(ch));
        }
        return sb.ToString().Trim();
    }

    static char FoldChar(char ch)
    {
        switch (ch)
        {
            case 'İ': case 'I': case 'ı': return 'i';
            case 'Ş': case 'ş': return 's';
            case 'Ğ': case 'ğ': return 'g';
            case 'Ü': case 'ü': return 'u';
            case 'Ö': case 'ö': return 'o';
            case 'Ç': case 'ç': return 'c';
            default: return char.ToLowerInvariant(ch);
        }
    }

    // Regex for one known value: every letter as the class of its case / Turkish variants.
    static string Pattern(string value)
    {
        var sb = new StringBuilder(); var blank = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch)) { if (!blank) sb.Append(@"\s+"); blank = true; continue; }
            blank = false;
            switch (FoldChar(ch))
            {
                case 'i': sb.Append("[iIİı]"); break;
                case 's': sb.Append("[sSşŞ]"); break;
                case 'g': sb.Append("[gGğĞ]"); break;
                case 'u': sb.Append("[uUüÜ]"); break;
                case 'o': sb.Append("[oOöÖ]"); break;
                case 'c': sb.Append("[cCçÇ]"); break;
                default:
                    var lo = char.ToLowerInvariant(ch); var up = char.ToUpperInvariant(ch);
                    if (lo != up) sb.Append('[').Append(Regex.Escape(lo.ToString())).Append(Regex.Escape(up.ToString())).Append(']');
                    else sb.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }
        return sb.ToString();
    }

    string Next(string kind)
    {
        if (kind == "profil") return "<profil-" + (++profiles).ToString(CultureInfo.InvariantCulture) + ">";
        if (kind == "ip") return "<ip-" + (++ips).ToString(CultureInfo.InvariantCulture) + ">";
        return "<sunucu-" + (++hosts).ToString(CultureInfo.InvariantCulture) + ">";
    }

    // Placeholder for a value found by a pattern: the known one when it is known, else a new number.
    string Number(string kind, string value)
    {
        string ph; var key = Fold(value);
        if (map.TryGetValue(key, out ph)) return ph;
        ph = Next(kind); map[key] = ph;
        return ph;
    }

    static bool Seedable(string value)
    {
        return value != null && value.Trim().Length >= MinKnownLength && !CommonWords.Contains(Fold(value)) && !Protected.IsMatch(value);
    }

    bool Remember(string value, string placeholder)
    {
        if (!Seedable(value)) return false;
        value = value.Trim();
        var key = Fold(value);
        if (map.ContainsKey(key)) return false;
        map[key] = placeholder; known.Add(value); knownRegex = null;
        return true;
    }

    public void AddProfileName(string value)
    {
        if (!Seedable(value) || map.ContainsKey(Fold(value))) return;
        Remember(value, Next("profil"));
    }

    // "vpn.acme.example:443", "203.0.113.7", "[2001:db8::1]:1194"; several separated by , ; or blanks.
    public void AddHost(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (var raw in value.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = raw.Trim();
            var bare = full;
            if (bare.StartsWith("[") && bare.IndexOf(']') > 0) bare = bare.Substring(1, bare.IndexOf(']') - 1);
            else if (bare.Count(c => c == ':') == 1) bare = bare.Substring(0, bare.IndexOf(':'));
            if (!Seedable(bare)) continue;
            string ph;
            if (!map.TryGetValue(Fold(bare), out ph))
            {
                IPAddress ip;
                ph = Next(IPAddress.TryParse(bare, out ip) ? "ip" : "sunucu");
                Remember(bare, ph);
            }
            if (full != bare) Remember(full, ph);
        }
    }

    // "ACME\jdoe" and "jdoe@corp.example" also register "jdoe" (and the domain part).
    public void AddUser(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Remember(value, "<kullanici>");
        var v = value.Trim();
        var slash = v.LastIndexOf('\\');
        if (slash > 0) { AddDomain(v.Substring(0, slash)); v = v.Substring(slash + 1); }
        var at = v.IndexOf('@');
        if (at > 0) v = v.Substring(0, at);
        Remember(v, "<kullanici>");
    }

    public void AddMachine(string value) { Remember(value, "<makine>"); }
    public void AddSecret(string value) { Remember(value, "<gizli>"); }
    public void AddDomain(string value) { Remember(value, "<alan>"); }

    // A folder whose path names the user ("C:\Users\ayse"): replaced by placeholder ("~", "%APPDATA%").
    public void AddPathRoot(string path, string placeholder)
    {
        if (string.IsNullOrEmpty(path)) return;
        var p = path.TrimEnd('\\', '/');
        Remember(p, placeholder);
        Remember(p.Replace('\\', '/'), placeholder);
    }

    // Name, host, username of a loaded profile, and its .ovpn file name and folder (named after customers).
    public void AddProfile(VpnProfile p)
    {
        if (p == null) return;
        AddProfileName(p.Name);
        AddHost(p.Host);
        AddUser(p.Username);
        if (!string.IsNullOrEmpty(p.Source))
        {
            try
            {
                var src = p.Source.Replace('/', '\\');
                var i = src.LastIndexOf('\\');
                var file = i >= 0 ? src.Substring(i + 1) : src;
                var dot = file.LastIndexOf('.');
                AddProfileName(dot > 0 ? file.Substring(0, dot) : file);
                if (i > 0)
                {
                    var dir = src.Substring(0, i); var j = dir.LastIndexOf('\\');
                    var parent = j >= 0 ? dir.Substring(j + 1) : dir;
                    if (parent.IndexOf(':') < 0) AddProfileName(parent);
                }
            }
            catch { }
        }
    }

    Regex KnownRegex()
    {
        if (knownRegex != null || known.Count == 0) return knownRegex;
        var parts = known.OrderByDescending(k => k.Length).Select(Pattern).ToArray();
        knownRegex = R(string.Join("|", parts), CS);
        return knownRegex;
    }

    string KnownPlaceholder(string value)
    {
        string ph;
        return map.TryGetValue(Fold(value), out ph) ? ph : "<gizli>";
    }

    // Applies f to the text between our placeholders only.
    static string Outside(string text, Func<string, string> f)
    {
        var matches = Protected.Matches(text);
        if (matches.Count == 0) return f(text);
        var sb = new StringBuilder(text.Length); var last = 0;
        foreach (Match m in matches)
        {
            sb.Append(f(text.Substring(last, m.Index - last)));
            sb.Append(m.Value);
            last = m.Index + m.Length;
        }
        sb.Append(f(text.Substring(last)));
        return sb.ToString();
    }

    // A stack frame line as this app writes it ("at Type.Method in File.cs:line 12"): identifiers
    // only, left as is so a profile named like a code word ("Form") cannot garble the stack.
    static readonly Regex FrameLine = R(@"^\s*at\s+[\w.+<>`\[\],|$-]+(?:\s+in\s+[\w.-]+(?::line\s+\d+)?)?\s*$", CS);

    public static string Cut(string line)
    {
        if (line == null) return "";
        return line.Length <= MaxLine ? line : line.Substring(0, MaxLine) + "…";
    }

    public string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var cr = lines[i].EndsWith("\r", StringComparison.Ordinal);
            lines[i] = Cut(cr ? lines[i].Substring(0, lines[i].Length - 1) : lines[i]) + (cr ? "\r" : "");
        }
        string t;
        try { t = InlineKey.Replace(Pem.Replace(string.Join("\n", lines), "<pem>"), "<anahtar>"); }
        catch (RegexMatchTimeoutException) { return LineDropped; }
        lines = t.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var cr = lines[i].EndsWith("\r", StringComparison.Ordinal);
            var body = cr ? lines[i].Substring(0, lines[i].Length - 1) : lines[i];
            if (body.Length == 0) continue;
            try { if (!FrameLine.IsMatch(body)) lines[i] = Line(body) + (cr ? "\r" : ""); }
            catch (RegexMatchTimeoutException) { lines[i] = LineDropped + (cr ? "\r" : ""); }
        }
        return string.Join("\n", lines);
    }

    string Line(string t)
    {
        // A leading exception type stays readable; the rest of the line is sanitized.
        var tp = TypePrefix.Match(t);
        if (tp.Success) return tp.Groups[1].Value + ":" + Line(t.Substring(tp.Length));
        // URL credentials, then e-mail, before the known values: a known username inside an
        // address must not leave the domain (or the password) behind
        t = Outside(t, s => UrlCredentials.Replace(s, "$1<gizli>@"));
        t = Outside(t, s => Email.Replace(s, "<eposta>"));
        var kr = KnownRegex();
        if (kr != null) t = Outside(t, s => kr.Replace(s, m => KnownPlaceholder(m.Value)));
        t = Outside(t, s => AuthHeader.Replace(s, "$1$2<gizli>"));
        t = Outside(t, s => LongSecret.Replace(s, "$1$2<gizli>"));
        t = Outside(t, s => Bearer.Replace(s, "$1$2<gizli>"));
        t = Outside(t, s => XHeader.Replace(s, "$1$2<gizli>"));
        t = Outside(t, s => KeyValue.Replace(s, "$1$2<gizli>"));
        t = Outside(t, s => UserValue.Replace(s, "$1$2<kullanici>"));
        t = Outside(t, s => CliSecret.Replace(s, "$1$2$3<gizli>"));
        t = Outside(t, s => CliUser.Replace(s, "$1$2$3<kullanici>"));
        t = Outside(t, s => CliName.Replace(s, "$1$2$3<profil>"));
        t = Outside(t, s => CliHost.Replace(s, "$1$2$3<sunucu>"));
        t = Outside(t, s => GitHubToken.Replace(s, "<gizli>"));
        t = Outside(t, s => UserAtHost.Replace(s, m =>
        {
            var host = m.Groups[2].Value;
            return "<kullanici>@" + (host.IndexOf('.') >= 0 ? host : Number("sunucu", host));
        }));
        t = Outside(t, s => Unc.Replace(s, m => "\\\\" + Number("sunucu", m.Groups[1].Value) + "\\" + (m.Groups[2].Length > 0 ? "<metin>" : "")));
        t = Outside(t, s => UserFolder.Replace(s, "~"));
        t = Outside(t, s => AbsPath.Replace(s, "<yol>\\"));
        t = Outside(t, s => OvpnFile.Replace(s, "<dosya>.ovpn"));   // profile files are named after customers
        t = Outside(t, s => DomainUser.Replace(s, m => NotDomains.Contains(m.Groups[1].Value) ? m.Value : "<alan>\\<kullanici>"));
        t = Outside(t, s => Ipv4.Replace(s, m =>
        {
            if (m.Value.Split('.').Any(o => int.Parse(o, CultureInfo.InvariantCulture) > 255)) return m.Value;
            if (VersionBefore.IsMatch(s.Substring(Math.Max(0, m.Index - 12), Math.Min(12, m.Index)))) return m.Value;   // "version 2.1.0.5"
            return Number("ip", m.Value);
        }));
        t = Outside(t, s => Ipv6.Replace(s, m => m.Value.Trim(':').Length == 0 ? m.Value : Number("ip", m.Value)));
        t = Outside(t, s => HexBlob.Replace(s, "<blob>"));
        t = Outside(t, s => Base64Blob.Replace(s, m => m.Value.Any(char.IsDigit) ? "<blob>" : m.Value));
        t = Outside(t, s => Host.Replace(s, m =>
        {
            var v = m.Value;
            if (AllowedHosts.Contains(v)) return v;
            if (v.StartsWith("System.", StringComparison.Ordinal) || v.StartsWith("Microsoft.", StringComparison.Ordinal)) return v;   // .NET names
            if (v.IndexOf('.') == v.LastIndexOf('.') && FileExtensions.Contains(m.Groups[1].Value.ToLowerInvariant())) return v;   // "Connection.cs"
            return Number("sunucu", v);
        }));
        return t;
    }

    // Exception messages: also everything in quotes (paths, names the app does not know).
    public string SanitizeMessage(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        try { return Sanitize(Quoted.Replace(Cut(text), "'<metin>'")); }
        catch (RegexMatchTimeoutException) { return LineDropped; }
    }

    public static string StripQuoted(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        try { return Quoted.Replace(Cut(text), "'…'"); }
        catch (RegexMatchTimeoutException) { return LineDropped; }
    }
}

// One exception in a form that keeps no absolute paths: type, message, frames (type + method,
// file name, line), inner exception.
internal sealed class ErrorFrame
{
    public string Method;   // "SearchForm.ConnectForti" (namespace-qualified type + method)
    public string File;     // file name only, or null
    public int Line;        // 0 = unknown
    public override string ToString()
    {
        var s = "at " + Method;
        if (!string.IsNullOrEmpty(File)) s += " in " + File + (Line > 0 ? ":line " + Line.ToString(CultureInfo.InvariantCulture) : "");
        return s;
    }
}

internal sealed class ErrorInfo
{
    public string Type = "", Message = "";
    public List<ErrorFrame> Frames = new List<ErrorFrame>();
    public ErrorInfo Inner;

    const int MaxDepth = 5, MaxFrames = 40;

    public static ErrorInfo From(Exception ex) { return From(ex, 0); }

    static ErrorInfo From(Exception ex, int depth)
    {
        if (ex == null) return null;
        var info = new ErrorInfo { Type = ex.GetType().FullName ?? ex.GetType().Name, Message = ex.Message ?? "" };
        try
        {
            var st = new StackTrace(ex, true);
            var frames = st.GetFrames();
            if (frames != null)
                foreach (var f in frames.Take(MaxFrames))
                {
                    var m = f.GetMethod();
                    if (m == null) continue;
                    var type = m.DeclaringType != null ? (m.DeclaringType.FullName ?? m.DeclaringType.Name) : "";
                    string file = null;
                    try { var fn = f.GetFileName(); if (!string.IsNullOrEmpty(fn)) file = FileNameOnly(fn); } catch { }
                    info.Frames.Add(new ErrorFrame { Method = ErrorFingerprint.Normalize((type.Length > 0 ? type + "." : "") + m.Name), File = file, Line = f.GetFileLineNumber() });
                }
        }
        catch { }
        Exception inner = ex.InnerException;
        var agg = ex as AggregateException;
        if (agg != null && agg.InnerExceptions.Count > 0) inner = agg.InnerExceptions[0];
        if (inner != null && depth < MaxDepth) info.Inner = From(inner, depth + 1);
        return info;
    }

    public static string FileNameOnly(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var i = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return i >= 0 ? path.Substring(i + 1) : path;
    }

    // The innermost exception: its type and frames identify the bug best.
    public ErrorInfo Root { get { var e = this; while (e.Inner != null) e = e.Inner; return e; } }
}

// 8 hex characters from the root exception's type and its top 3 frames' type + method names.
// Decision: method names only, no file names and no line numbers, so the same bug keeps its
// fingerprint across builds, install folders and unrelated edits that move lines. Compiler-made
// names (lambdas, display classes) lose their build-specific counters.
internal static class ErrorFingerprint
{
    static readonly Regex Lambda = new Regex(@"(<[^>]*>[a-zA-Z]__)[0-9A-Za-z_]+", RegexOptions.CultureInvariant);
    static readonly Regex Counter = new Regex(@"(DisplayClass|CachedAnonymousMethodDelegate|AnonymousType)[0-9A-Fa-f_]+", RegexOptions.CultureInvariant);

    public static string Normalize(string method)
    {
        if (string.IsNullOrEmpty(method)) return method ?? "";
        return Counter.Replace(Lambda.Replace(method, "$1"), "$1");
    }

    public static IList<string> TopMethods(ErrorInfo e, int count)
    {
        var list = new List<string>();
        if (e == null) return list;
        foreach (var f in e.Frames) { if (list.Count >= count) break; list.Add(Normalize(f.Method)); }
        return list;
    }

    public static string Compute(string exceptionType, IEnumerable<string> topMethods)
    {
        var key = (exceptionType ?? "") + "|" + string.Join("|", (topMethods ?? new string[0]).Select(Normalize).ToArray());
        using (var sha = SHA256.Create())
        {
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            return BitConverter.ToString(h, 0, 4).Replace("-", "").ToLowerInvariant();
        }
    }

    public static string Of(ErrorInfo e)
    {
        if (e == null) return null;
        var root = e.Root;
        var frames = root.Frames.Count > 0 ? root : e;   // an inner exception without frames: use the outer frames
        return Compute(root.Type, TopMethods(frames, 3));
    }
}

// What the app knows about this PC for a report: no names, only versions and yes/no flags.
internal sealed class ReportEnvironment
{
    public string AppVersion = "", Windows = "", Clr = "", NetRelease = "";
    public float Scale = 1F;
    public bool OpenVpn, Forti, Sophos, WatchGuard;

    public static ReportEnvironment Detect()
    {
        var e = new ReportEnvironment();
        try { e.AppVersion = UpdateLogic.CurrentVersion; } catch { }
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (k != null)
                {
                    var major = k.GetValue("CurrentMajorVersionNumber"); var minor = k.GetValue("CurrentMinorVersionNumber");
                    var build = k.GetValue("CurrentBuildNumber") as string; var ubr = k.GetValue("UBR");
                    e.Windows = (major != null ? Convert.ToString(major, CultureInfo.InvariantCulture) + "." + Convert.ToString(minor, CultureInfo.InvariantCulture) : "?") +
                                " build " + (build ?? "?") + (ubr != null ? "." + Convert.ToString(ubr, CultureInfo.InvariantCulture) : "");
                }
            }
        }
        catch { }
        if (e.Windows.Length == 0) e.Windows = Environment.OSVersion.VersionString;
        e.Clr = Environment.Version.ToString();
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                if (k != null && k.GetValue("Release") != null) e.NetRelease = Convert.ToString(k.GetValue("Release"), CultureInfo.InvariantCulture);
        }
        catch { }
        e.Scale = Ui.Scale;
        try
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
                if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "OpenVPN Connect", "OpenVPNConnect.exe"))) { e.OpenVpn = true; break; }
        }
        catch { }
        try { e.Forti = FortiStore.IsInstalled; } catch { }
        try { e.Sophos = SophosStore.IsInstalled; } catch { }
        try { e.WatchGuard = WatchGuardStore.IsInstalled; } catch { }
        return e;
    }

    static string YesNo(bool b) { return b ? "var" : "yok"; }

    public string ProvidersText()
    {
        return "OpenVPN Connect: " + YesNo(OpenVpn) + ", FortiClient: " + YesNo(Forti) + ", Sophos Connect: " + YesNo(Sophos) + ", WatchGuard SSL: " + YesNo(WatchGuard);
    }

    // One log line at start (no names, no paths).
    public string Summary()
    {
        return "version=" + AppVersion + " windows=\"" + Windows + "\" clr=" + Clr + " net-release=" + (NetRelease.Length > 0 ? NetRelease : "?") +
               " scale=" + Scale.ToString("0.00", CultureInfo.InvariantCulture) +
               " openvpn=" + OpenVpn.ToString().ToLowerInvariant() + " forti=" + Forti.ToString().ToLowerInvariant() +
               " sophos=" + Sophos.ToString().ToLowerInvariant() + " watchguard=" + WatchGuard.ToString().ToLowerInvariant();
    }
}

// The report: issue title + Markdown body. Kinds: "crash" (unhandled), "error" (handled exception),
// "manual" ("Son hatayı bildir" with no recorded error).
internal sealed class ErrorReport
{
    public const int LogLines = 60;
    public string Kind = "manual", Title = "", Body = "", Fingerprint;

    public string FullText { get { return "# " + Title + "\n\n" + Body; } }

    static string KindText(string kind)
    {
        return kind == "crash" ? "çökme (yakalanmamış hata)" : kind == "error" ? "işlenmiş hata" : "kullanıcı bildirimi";
    }

    static string ShortType(string fullType)
    {
        if (string.IsNullOrEmpty(fullType)) return "Exception";
        var i = fullType.LastIndexOf('.');
        return i >= 0 ? fullType.Substring(i + 1) : fullType;
    }

    // "SearchForm.ConnectForti" from "Ns.SearchForm.ConnectForti" (last two parts).
    static string ShortMethod(string method)
    {
        if (string.IsNullOrEmpty(method)) return "?";
        var parts = method.Split('.');
        return parts.Length >= 2 ? parts[parts.Length - 2] + "." + parts[parts.Length - 1] : method;
    }

    static string Fence(string s) { return (s ?? "").Replace("```", "'''"); }

    public static ErrorReport Build(string kind, ErrorInfo error, ReportEnvironment env, IList<string> logLines, LogSanitizer s, DateTime utc)
    {
        if (s == null) s = new LogSanitizer();
        if (env == null) env = new ReportEnvironment();
        var r = new ErrorReport { Kind = error == null ? "manual" : (kind ?? "error") };
        var b = new StringBuilder();
        if (error != null)
        {
            r.Fingerprint = ErrorFingerprint.Of(error);
            var root = error.Root;
            var top = root.Frames.Count > 0 ? root.Frames[0].Method : error.Frames.Count > 0 ? error.Frames[0].Method : null;
            r.Title = "[hata] " + ShortType(root.Type) + ": " + ShortMethod(top) + " (" + r.Fingerprint + ")";
        }
        else r.Title = "[hata] Kullanıcı bildirimi (sürüm " + env.AppVersion + ")";
        if (r.Title.Length > 160) r.Title = r.Title.Substring(0, 160);

        b.Append("### Ortam\n");
        b.Append("- Tür: ").Append(KindText(r.Kind)).Append('\n');
        if (r.Fingerprint != null) b.Append("- Parmak izi: `").Append(r.Fingerprint).Append("`\n");
        b.Append("- Uygulama sürümü: ").Append(env.AppVersion).Append('\n');
        b.Append("- Windows: ").Append(env.Windows).Append('\n');
        b.Append("- .NET: CLR ").Append(env.Clr).Append(env.NetRelease.Length > 0 ? " (release " + env.NetRelease + ")" : "").Append('\n');
        b.Append("- Ekran ölçeği: ").Append(env.Scale.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');
        b.Append("- VPN istemcileri: ").Append(env.ProvidersText()).Append('\n');
        b.Append("- Zaman (UTC): ").Append(utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
        if (error != null)
        {
            b.Append("\n### Hata\n```\n");
            var e = error; var depth = 0;
            while (e != null)
            {
                if (depth > 0) b.Append("--- iç hata ---\n");
                b.Append(e.Type).Append(": ").Append(Fence(s.SanitizeMessage(e.Message).Replace("\r", " ").Replace("\n", " "))).Append('\n');
                foreach (var f in e.Frames) b.Append("   ").Append(Fence(s.Sanitize(f.ToString()))).Append('\n');
                e = e.Inner; depth++;
            }
            b.Append("```\n");
        }
        var lines = logLines ?? new string[0];
        var tail = lines.Skip(Math.Max(0, lines.Count - LogLines)).ToList();
        b.Append("\n### Son günlük satırları (").Append(tail.Count.ToString(CultureInfo.InvariantCulture)).Append(")\n```\n");
        foreach (var l in tail) b.Append(Fence(s.Sanitize(l))).Append('\n');
        b.Append("```\n\n");
        b.Append("_Rapor uygulama tarafından temizlendi: parolalar, kullanıcı adları, profil ve sunucu adları, IP ve e-posta adresleri, makine adı ve kişisel klasör yolları çıkarıldı._\n");
        r.Body = b.ToString();
        return r;
    }

    // Crash / error record file: a small header, then the body.
    public string ToRecord()
    {
        return "title: " + Title + "\nkind: " + Kind + "\nfingerprint: " + (Fingerprint ?? "") + "\n---\n" + Body;
    }

    public static ErrorReport ParseRecord(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var t = text.Replace("\r\n", "\n");
        var sep = t.IndexOf("\n---\n", StringComparison.Ordinal);
        if (sep < 0) return null;
        var r = new ErrorReport { Body = t.Substring(sep + 5) };
        foreach (var line in t.Substring(0, sep).Split('\n'))
        {
            var i = line.IndexOf(": ", StringComparison.Ordinal);
            if (i <= 0) continue;
            var k = line.Substring(0, i); var v = line.Substring(i + 2).Trim();
            if (k == "title") r.Title = v;
            else if (k == "kind") r.Kind = v;
            else if (k == "fingerprint") r.Fingerprint = Regex.IsMatch(v, "^[0-9a-f]{8}$") ? v : null;
        }
        if (r.Title.Length == 0) return null;
        if (r.Kind != "crash" && r.Kind != "error") r.Kind = "manual";
        return r;
    }
}

internal sealed class IssueRef
{
    public int Number; public string Url, State, Title; public bool Locked;
    public bool Open { get { return State == "open"; } }
}

// GitHub new-issue link and duplicate check (pure parts; the fetch is injected).
internal static class IssueLink
{
    public const string Template = "hata-raporu.yml";
    public const string Label = "hata";
    public const string ReportField = "rapor";            // id of the textarea in .github/ISSUE_TEMPLATE/hata-raporu.yml
    // Total URL length cap. Browsers and GitHub accept about 8 KB in practice; 7000 leaves room for
    // the redirect GitHub adds when the user still has to sign in.
    public const int MaxUrlLength = 7000;
    public static readonly string IssuesPrefix = "https://github.com/" + UpdateLogic.Repo + "/issues/";
    public static readonly string NewIssue = IssuesPrefix + "new";
    public const string TruncationNote = "\n\n_(Rapor adres sınırına sığmadığı için kısaltıldı. Tam rapor panoya kopyalandı: bu alanı silip Ctrl+V ile yapıştırın.)_\n";

    static string Enc(string s) { return Uri.EscapeDataString(s ?? ""); }

    static int Fences(string s)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf("```", i, StringComparison.Ordinal)) >= 0) { n++; i += 3; }
        return n;
    }

    static string Cut(string[] lines, int count)
    {
        var text = string.Join("\n", lines, 0, count);
        if (Fences(text) % 2 == 1) text += "\n```";
        return text + TruncationNote;
    }

    // Pre-filled form URL (template, label, title, report field). Longer than maxLength: the body is cut
    // at a line boundary with a note, truncated = true (the caller copies the full report to the clipboard).
    public static string NewIssueUrl(string title, string body, int maxLength, out bool truncated)
    {
        truncated = false;
        title = title ?? "";
        if (title.Length > 200) title = title.Substring(0, 200);
        var head = NewIssue + "?template=" + Template + "&labels=" + Label + "&title=" + Enc(title) + "&" + ReportField + "=";
        var full = head + Enc(body);
        if (full.Length <= maxLength) return full;
        truncated = true;
        var lines = (body ?? "").Replace("\r\n", "\n").Split('\n');
        var best = head + Enc(TruncationNote.TrimStart('\n'));
        int lo = 0, hi = lines.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            var u = head + Enc(Cut(lines, mid));
            if (u.Length <= maxLength) { lo = mid; best = u; } else hi = mid - 1;
        }
        return best;
    }

    public static bool IsValidFingerprint(string fp) { return fp != null && Regex.IsMatch(fp, "^[0-9a-f]{8}$"); }

    // Read-only search API: issues of this repository whose title contains the fingerprint.
    public static string SearchUrl(string fingerprint)
    {
        return "https://api.github.com/search/issues?q=" + Enc("repo:" + UpdateLogic.Repo + " is:issue label:" + Label + " in:title " + fingerprint) + "&per_page=20";
    }

    static bool HasLabel(Dictionary<string, object> issue, string name)
    {
        object v;
        var labels = issue.TryGetValue("labels", out v) ? v as object[] : null;
        if (labels == null) return false;
        foreach (var l in labels)
        {
            var ld = l as Dictionary<string, object>;
            object n;
            if (ld != null && ld.TryGetValue("name", out n) && string.Equals(n as string, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // First issue labelled "hata" whose title carries "(<fingerprint>)", open ones first; null when
    // none or the JSON is odd. Issues created by the no-label retry are not matched (docs\TRIAGE.md).
    public static IssueRef FindDuplicate(string json, string fingerprint)
    {
        if (!IsValidFingerprint(fingerprint) || string.IsNullOrEmpty(json)) return null;
        try
        {
            var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) return null;
            object itemsObj;
            if (!root.TryGetValue("items", out itemsObj)) return null;
            var items = itemsObj as object[];
            if (items == null) return null;
            IssueRef closed = null;
            foreach (var o in items)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                object v;
                var title = d.TryGetValue("title", out v) ? v as string : null;
                var url = d.TryGetValue("html_url", out v) ? v as string : null;
                var state = d.TryGetValue("state", out v) ? v as string : null;
                if (d.ContainsKey("pull_request")) continue;
                if (title == null || url == null || title.IndexOf("(" + fingerprint + ")", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!url.StartsWith(IssuesPrefix, StringComparison.Ordinal)) continue;
                int number = 0;
                if (d.TryGetValue("number", out v) && v != null) try { number = Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { }
                // only real reports: label "hata", and the link is this issue's own page
                if (number <= 0 || url != IssuesPrefix + number.ToString(CultureInfo.InvariantCulture) || !HasLabel(d, Label)) continue;
                var r = new IssueRef { Number = number, Url = url, State = state == "open" ? "open" : "closed", Title = title, Locked = d.TryGetValue("locked", out v) && v is bool && (bool)v };
                if (r.Open) return r;
                if (closed == null) closed = r;
            }
            return closed;
        }
        catch { return null; }
    }

    // fetch: GET url -> response text (throws on failure). Any failure = no duplicate known.
    public static IssueRef CheckDuplicate(string fingerprint, Func<string, string> fetch)
    {
        if (!IsValidFingerprint(fingerprint) || fetch == null) return null;
        try { return FindDuplicate(fetch(SearchUrl(fingerprint)), fingerprint); }
        catch { return null; }
    }

    public static bool IsOurIssueUrl(string url) { return url != null && url.StartsWith(IssuesPrefix, StringComparison.Ordinal) && url.IndexOfAny(new[] { ' ', '"', '\r', '\n' }) < 0; }
}

// The marker left by a crash so the next start offers the report.
internal static class CrashPromptLogic
{
    public const string MarkerName = "crash-pending.txt";
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    // Sends without a dialog only when every condition holds: mode "auto", the one-time public-issue
    // warning confirmed (CrashAutoConsent), a token in this build, not demo, logging on.
    public static bool AutoSend(string mode, bool consent, bool tokenAvailable, bool demo, bool logEnabled)
    {
        return mode == "auto" && consent && tokenAvailable && !demo && logEnabled;
    }

    // What the settings keep when the user picks a mode: "auto" needs a token and the confirmed
    // warning, else it falls back to "ask" (and the consent is dropped whenever the mode is not auto).
    public static string StoredMode(string picked, bool tokenAvailable, bool confirmed, out bool consent)
    {
        consent = false;
        if (picked == "never") return "never";
        if (picked == "auto" && tokenAvailable && confirmed) { consent = true; return "auto"; }
        return "ask";
    }

    // "Sonra" keeps the marker: the next start asks again, at most MaxOffers times in all.
    public const int MaxOffers = 3;

    public static string MarkerText(string recordName, DateTime utc) { return MarkerText(recordName, utc, 0); }

    public static string MarkerText(string recordName, DateTime utc, int offers)
    {
        return recordName + "\n" + utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\noffers=" + offers.ToString(CultureInfo.InvariantCulture) + "\n";
    }

    static int Offers(string[] lines)
    {
        int n;
        return lines.Length > 2 && lines[2].StartsWith("offers=", StringComparison.Ordinal) && int.TryParse(lines[2].Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : 0;
    }

    // Record to offer, or null: no / broken / old marker, offered MaxOffers times, "never", demo mode.
    public static string ShouldOffer(string markerText, string crashPromptMode, bool demo, DateTime nowUtc)
    {
        if (demo || crashPromptMode == "never" || string.IsNullOrEmpty(markerText)) return null;
        var lines = markerText.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2) return null;
        var name = lines[0].Trim();
        if (!LogRotation.IsRecordName(name) || !name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase)) return null;
        DateTime when;
        if (!DateTime.TryParseExact(lines[1].Trim(), "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when)) return null;
        if (when > nowUtc.AddDays(1) || nowUtc - when > MaxAge) return null;
        if (Offers(lines) >= MaxOffers) return null;
        return name;
    }

    // Marker after one more "Sonra": the count goes up; null (drop the marker) once MaxOffers is reached.
    public static string AfterLater(string markerText)
    {
        if (string.IsNullOrEmpty(markerText)) return null;
        var lines = markerText.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2) return null;
        DateTime when;
        if (!DateTime.TryParseExact(lines[1].Trim(), "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when)) return null;
        var n = Offers(lines) + 1;
        return n >= MaxOffers ? null : MarkerText(lines[0].Trim(), when, n);
    }
}
// Buffered, thread-safe log file writer. Write never throws and never touches the disk on the
// calling thread (a pool thread drains the buffer; Flush() drains synchronously, e.g. on exit or crash).
internal sealed class LogWriter
{
    readonly string dir;
    readonly object gate = new object(), fileGate = new object();
    readonly StringBuilder pending = new StringBuilder();
    readonly Queue<string> recent = new Queue<string>();
    bool draining;
    string current; DateTime currentDay = DateTime.MinValue; long currentSize;
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    public volatile bool Enabled = true;
    public bool Async = true;                       // tests: false = write on the calling thread
    public Func<DateTime> Clock = () => DateTime.Now;
    public long MaxBytes = LogRotation.MaxBytes;
    public int Keep = LogRotation.KeepFiles;
    const int RecentCapacity = 200, MaxPending = 1024 * 1024;

    public LogWriter(string dir) { this.dir = dir; }
    public string Dir { get { return dir; } }

    static string OneLine(string s) { return (s ?? "").Replace("\r\n", " | ").Replace('\r', ' ').Replace('\n', ' '); }

    public static string Format(DateTime t, LogLevel level, string source, string message)
    {
        var lvl = level == LogLevel.Info ? "INFO " : level == LogLevel.Warn ? "WARN " : "ERROR";
        var src = string.IsNullOrEmpty(source) ? "app" : source.Replace(' ', '-');
        return t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + lvl + " " + src + " " + OneLine(message);
    }

    // Exception lines under an entry: type, quoted-stripped message, frames without paths, inner exceptions.
    public static List<string> ExceptionLines(Exception ex)
    {
        var lines = new List<string>();
        var e = ErrorInfo.From(ex); var depth = 0;
        while (e != null)
        {
            lines.Add("    " + (depth > 0 ? "--- iç hata: " : "") + e.Type + ": " + OneLine(LogSanitizer.StripQuoted(e.Message)));
            foreach (var f in e.Frames.Take(20)) lines.Add("      " + f);
            e = e.Inner; depth++;
        }
        return lines;
    }

    public void Write(LogLevel level, string source, string message, Exception ex)
    {
        if (!Enabled) return;
        try
        {
            var lines = new List<string> { Format(Clock(), level, source, message) };
            if (ex != null) lines.AddRange(ExceptionLines(ex));
            bool start = false;
            lock (gate)
            {
                foreach (var l in lines)
                {
                    recent.Enqueue(l); while (recent.Count > RecentCapacity) recent.Dequeue();
                    if (pending.Length < MaxPending) pending.Append(l).Append("\r\n");
                }
                if (!draining) { draining = true; start = true; }
            }
            if (!start) return;
            if (Async) ThreadPool.QueueUserWorkItem(delegate { Drain(); });
            else Drain();
        }
        catch { }
    }

    // Drain (pool thread) and Flush (caller) both take the buffer while holding fileGate, so chunks
    // reach the file in the order they were written.
    void Drain()
    {
        try
        {
            while (true)
            {
                lock (fileGate)
                {
                    string chunk;
                    lock (gate)
                    {
                        if (pending.Length == 0) { draining = false; return; }
                        chunk = pending.ToString(); pending.Length = 0;
                    }
                    WriteChunk(chunk);
                }
            }
        }
        catch { lock (gate) draining = false; }
    }

    public void Flush()
    {
        try
        {
            lock (fileGate)
            {
                string chunk;
                lock (gate) { chunk = pending.ToString(); pending.Length = 0; }
                if (chunk.Length > 0) WriteChunk(chunk);
            }
        }
        catch { }
    }

    // Caller holds fileGate.
    void WriteChunk(string chunk)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var now = Clock();
            if (current == null || currentDay != now.Date || currentSize >= MaxBytes) Rotate(now);
            var bytes = Utf8.GetBytes(chunk);
            // A reader that holds the file without write sharing (an editor, a copy) fails the open
            // for a moment: retry a few times instead of losing the chunk. AppendData rights make every
            // write an atomic append, also when two instances (--instance) share the folder.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(Path.Combine(dir, current), FileMode.Append, FileSystemRights.AppendData, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None))
                        fs.Write(bytes, 0, bytes.Length);
                    break;
                }
                catch (IOException) { if (attempt >= 4) throw; Thread.Sleep(15); }
            }
            currentSize += bytes.Length;
        }
        catch { current = null; }
    }
    void Rotate(DateTime now)
    {
        var existing = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new DirectoryInfo(dir).GetFiles("app-*.log")) existing[f.Name] = f.Length;
        current = LogRotation.PickFile(now, existing, MaxBytes);
        currentDay = now.Date;
        long size; currentSize = existing.TryGetValue(current, out size) ? size : 0;
        var names = existing.Keys.ToList();
        if (!existing.ContainsKey(current)) names.Add(current);
        foreach (var old in LogRotation.ToDelete(names, Keep))
            if (!string.Equals(old, current, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(Path.Combine(dir, old)); } catch { }
    }

    // Last n lines: this run's buffer; at start (empty buffer) the tail of the newest log file.
    public List<string> Recent(int n)
    {
        lock (gate)
        {
            if (recent.Count > 0) return recent.Skip(Math.Max(0, recent.Count - n)).ToList();
        }
        try
        {
            if (!Directory.Exists(dir)) return new List<string>();
            var names = new DirectoryInfo(dir).GetFiles("app-*.log").Select(f => f.Name).ToList();
            var newest = LogRotation.ToDelete(names, 0).FirstOrDefault();   // sorted newest first
            if (newest == null) return new List<string>();
            using (var fs = new FileStream(Path.Combine(dir, newest), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                var all = new List<string>(); string line;
                while ((line = sr.ReadLine()) != null) { all.Add(line); if (all.Count > 4 * n) all.RemoveRange(0, all.Count - n); }
                return all.Skip(Math.Max(0, all.Count - n)).ToList();
            }
        }
        catch { return new List<string>(); }
    }
}

// The app-wide log. Off (null writer) in demo mode and before Init. Lines never contain passwords,
// profile names, hosts or usernames: connect logs carry the provider kind and the outcome only.
internal static class AppLog
{
    static LogWriter writer;
    static readonly LogRateLimiter limiter = new LogRateLimiter(TimeSpan.FromMinutes(10));

    public static void Init(string dir, bool enabled)
    {
        writer = string.IsNullOrEmpty(dir) ? null : new LogWriter(dir) { Enabled = enabled };
    }

    public static bool Enabled
    {
        get { var w = writer; return w != null && w.Enabled; }
        set { var w = writer; if (w != null) w.Enabled = value; }
    }

    public static void Info(string source, string message) { Write(LogLevel.Info, source, message, null); }
    public static void Warn(string source, string message) { Write(LogLevel.Warn, source, message, null); }
    public static void Error(string source, string message, Exception ex) { Write(LogLevel.Error, source, message, ex); }

    // Once per key and 10 minutes; the next entry says how many were skipped.
    public static void Limited(LogLevel level, string key, string source, string message, Exception ex)
    {
        int skipped;
        if (!limiter.Allow(key, DateTime.UtcNow, out skipped)) return;
        Write(level, source, skipped > 0 ? message + " (+" + skipped.ToString(CultureInfo.InvariantCulture) + " benzer kayıt atlandı)" : message, ex);
    }

    static void Write(LogLevel level, string source, string message, Exception ex)
    {
        var w = writer;
        if (w != null) w.Write(level, source, message, ex);
    }

    public static List<string> Recent(int n) { var w = writer; return w != null && w.Enabled ? w.Recent(n) : new List<string>(); }
    public static void Flush() { var w = writer; if (w != null) w.Flush(); }
}

// Unhandled exception hooks, crash / error records and the report flow (dialogs, browser).
internal static class CrashReporter
{
    public const string Caption = "Hata raporu";
    // Shown before any report goes out: exactly what it contains and what it leaves out, then how it travels.
    public const string ReportContents =
        "Rapor şunları içerir: uygulama sürümü, Windows ve .NET sürümü, ekran ölçeği, hangi VPN istemcilerinin kurulu olduğu (yalnızca var/yok), " +
        "hatanın türü ve kod konumları (sınıf ve yöntem adları), son 60 günlük satırı.\n" +
        "Çıkarılanlar: parolalar ve anahtarlar, kullanıcı adları, profil ve müşteri adları, sunucu adresleri, IP ve e-posta adresleri, " +
        "makine adı, Windows kullanıcı adı ve kişisel klasör yolları.";
    public const string SendText =
        "\"Raporu GitHub'a gönder\" ile uygulama raporu kendisi gönderir ve GitHub'da HERKESE AÇIK bir kayıt (issue) oluşturur; tarayıcı açılmaz. " +
        "Göndermeden önce \"Raporu göster\" ile okuyun. Uygulamadaki anahtar yalnızca bu depoda kayıt açmaya yeter.";
    public const string BrowserText =
        "Rapor tarayıcınızda GitHub'ın yeni kayıt sayfasında açılır; sayfa adresiyle GitHub'a ulaşır ama siz Gönder'e (Submit) basana kadar " +
        "yayınlanmaz, önce okuyup düzenleyebilirsiniz. GitHub hesabı gerekir.";
    public const string AutoWarning =
        "Uyarı: Otomatik kipte rapor sormadan HERKESE AÇIK bir GitHub kaydı olarak gönderilir; temizleme kalıp tabanlıdır ve bir müşteri adını kaçırabilir.";
    public const string NoTokenText =
        "Bu sürümde gönderme anahtarı yok: Otomatik, Sor gibi çalışır ve rapor tarayıcıda açılır.";

    public static string DeliveryText { get { return IssueToken.Available ? SendText : BrowserText; } }

    // Settings note for the chosen CrashPrompt mode.
    public static string SummaryText(string mode)
    {
        var s = "Hata raporu: sürüm, Windows/.NET, kurulu VPN istemcileri (var/yok), hata türü ve kod konumları, son 60 günlük satırı. " +
                "Parolalar, kullanıcı/profil/sunucu adları, IP ve e-posta adresleri, makine adı ve kişisel yollar çıkarılır. ";
        s += IssueToken.Available ? "Rapor GitHub'da herkese açık bir kayıt olarak gönderilir." : "Rapor tarayıcıda açılır; Gönder'e siz basarsınız.";
        if (mode == "auto") s += "\n" + (IssueToken.Available ? AutoWarning : NoTokenText);
        return s;
    }

    public static Action<string> Status;                    // short status / toast text in the main window
    public static Func<IEnumerable<VpnProfile>> Profiles;   // set by the main window (read only, may run on any thread)
    public static Func<string> PromptMode;                  // settings.CrashPrompt ("ask" / "auto" / "never")
    public static Func<bool> AutoConsent;                   // settings.CrashAutoConsent (confirmed public-issue warning)
    public static Action NeverAsk;                          // "Bir daha sorma": settings.CrashPrompt = "never" + save
    static readonly object gate = new object();
    static readonly List<string> users = new List<string>();
    static readonly HashSet<string> recorded = new HashSet<string>(StringComparer.Ordinal);
    static int recordsThisRun; static bool promptedThisRun;
    static ReportEnvironment env;

    public static string DefaultLogDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenVpnQuickSearch", "logs"); } }
    public static string LogDir { get; private set; }       // null: demo mode, nothing is written

    public static void Init(string logDir) { LogDir = logDir; }

    public static void Install()
    {
        try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); } catch { }
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        // No System.Threading.Tasks in this app, so no TaskScheduler.UnobservedTaskException hook.
    }

    // Detects versions and installed clients off the UI thread and logs the start line.
    public static void LogStartAsync(bool background)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { var e = ReportEnvironment.Detect(); env = e; AppLog.Info("app", "start " + e.Summary() + " background=" + background.ToString().ToLowerInvariant()); }
            catch { }
        });
    }

    static ReportEnvironment Env { get { var e = env; if (e == null) { e = ReportEnvironment.Detect(); env = e; } return e; } }

    // A saved credential's username seen in this run (Credential Manager entries are not enumerated).
    public static void RememberUser(string user)
    {
        if (string.IsNullOrEmpty(user)) return;
        lock (gate) if (!users.Contains(user) && users.Count < 500) users.Add(user);
    }

    public static LogSanitizer NewSanitizer()
    {
        var s = new LogSanitizer();
        try
        {
            var profiles = Profiles != null ? Profiles() : null;
            if (profiles != null)
                foreach (var p in profiles.ToList())
                {
                    s.AddProfile(p);
                    try { var key = VpnProviders.CredentialKey(p); if (key != p.Name) s.AddHost(key); } catch { }
                }
        }
        catch { }
        lock (gate) foreach (var u in users) s.AddUser(u);
        try
        {
            s.AddPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
            s.AddPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
            s.AddPathRoot(Path.GetTempPath(), "%TEMP%");
            s.AddPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~");
            s.AddUser(Environment.UserName);
            s.AddMachine(Environment.MachineName);
            var token = IssueToken.Value;
            if (token != null) s.AddSecret(token);   // never in a report, even if it reached a log line
            var domain = Environment.UserDomainName;
            if (!string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) s.AddDomain(domain);
        }
        catch { }
        return s;
    }

    public static ErrorReport Capture(string kind, Exception ex)
    {
        return ErrorReport.Build(kind, ErrorInfo.From(ex), Env, AppLog.Recent(ErrorReport.LogLines), NewSanitizer(), DateTime.UtcNow);
    }

    static bool CanWrite { get { return LogDir != null && !Demo.On && AppLog.Enabled; } }

    // Writes crash-<utc>.txt / error-<utc>.txt (sanitized) and prunes old ones. Returns the file name or null.
    static string WriteRecord(ErrorReport r, string prefix)
    {
        if (!CanWrite || r == null) return null;
        try
        {
            Directory.CreateDirectory(LogDir);
            var stamp = prefix + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var name = stamp + ".txt";
            for (var i = 2; File.Exists(Path.Combine(LogDir, name)) && i < 100; i++) name = stamp + "-" + i.ToString(CultureInfo.InvariantCulture) + ".txt";
            File.WriteAllText(Path.Combine(LogDir, name), r.ToRecord().Replace("\n", "\r\n"), new UTF8Encoding(false));
            var names = new DirectoryInfo(LogDir).GetFiles(prefix + "-*.txt").Select(f => f.Name).ToList();
            foreach (var old in LogRotation.RecordsToDelete(names, prefix, LogRotation.KeepRecords)) try { File.Delete(Path.Combine(LogDir, old)); } catch { }
            return name;
        }
        catch { return null; }
    }

    static void WriteMarker(string recordName)
    {
        if (!CanWrite || recordName == null) return;
        try { File.WriteAllText(Path.Combine(LogDir, CrashPromptLogic.MarkerName), CrashPromptLogic.MarkerText(recordName, DateTime.UtcNow)); } catch { }
    }

    static string ReadMarker()
    {
        if (LogDir == null) return null;
        try { var p = Path.Combine(LogDir, CrashPromptLogic.MarkerName); return File.Exists(p) ? File.ReadAllText(p) : null; } catch { return null; }
    }

    // After the prompt: "Sonra" keeps the marker (asked again at the next start, MaxOffers in all),
    // "Raporu aç" and "Bir daha sorma" remove it.
    static void SettleMarker(Choice choice)
    {
        if (choice != Choice.Later) { ClearMarker(); return; }
        var next = CrashPromptLogic.AfterLater(ReadMarker());
        if (next == null || LogDir == null) { ClearMarker(); return; }
        try { File.WriteAllText(Path.Combine(LogDir, CrashPromptLogic.MarkerName), next); } catch { }
    }

    static void ClearMarker()
    {
        if (LogDir == null) return;
        try { var p = Path.Combine(LogDir, CrashPromptLogic.MarkerName); if (File.Exists(p)) File.Delete(p); } catch { }
    }

    // First time this fingerprint is seen in this run (and at most 10 records per run).
    static bool FirstOfRun(string fingerprint)
    {
        lock (gate)
        {
            if (recordsThisRun >= 10 || !recorded.Add(fingerprint ?? "")) return false;
            recordsThisRun++;
            return true;
        }
    }

    // A caught exception around a provider / update call: logged with its stack (once per kind
    // per 10 minutes) and kept as an error record for "Son hatayı bildir". No dialog.
    public static void HandledError(string source, string what, Exception ex)
    {
        if (ex == null) return;
        try
        {
            var fp = ErrorFingerprint.Of(ErrorInfo.From(ex));
            AppLog.Limited(LogLevel.Error, "err:" + fp, source, what + " failed (" + fp + ")", ex);
            if (!CanWrite || !FirstOfRun(fp)) return;
            WriteRecord(Capture("error", ex), "error");
        }
        catch { }
    }

    static void OnThreadException(object sender, ThreadExceptionEventArgs e)
    {
        ErrorReport r = null;
        try
        {
            // fingerprint first: a repeating exception (e.g. in a paint handler) costs no report work
            var fp = ErrorFingerprint.Of(ErrorInfo.From(e.Exception));
            AppLog.Limited(LogLevel.Error, "crash:" + fp, "crash", "unhandled UI-thread exception (" + fp + ")", e.Exception);
            if (!FirstOfRun(fp)) return;
            r = Capture("crash", e.Exception);
            WriteMarker(WriteRecord(r, "crash"));
            AppLog.Flush();
        }
        catch { }
        // The app keeps running (as WinForms' own "Continue"); ask once per run.
        if (r == null || Demo.On || Mode() == "never") return;
        if (!AppLog.Enabled) return;   // "Hata kaydı tut" off: no record, no prompt, nothing is sent
        lock (gate) { if (promptedThisRun) return; promptedThisRun = true; }
        try
        {
            var owner = Form.ActiveForm;
            if (SendsAutomatically) { SettleMarker(Choice.Open); AppLog.Info("report", "sending the report automatically"); Deliver(owner, r, true); return; }
            var choice = Ask(owner, "Beklenmeyen bir hata oluştu; uygulama çalışmaya devam ediyor.\nHata raporunu GitHub'a göndermek ister misiniz?", r);
            SettleMarker(choice);
            Act(owner, choice, r);
        }
        catch { }
    }

    static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            if (ex == null) { AppLog.Error("crash", "unhandled non-exception object", null); AppLog.Flush(); return; }
            var r = Capture("crash", ex);
            AppLog.Error("crash", "unhandled exception (" + r.Fingerprint + ")" + (e.IsTerminating ? ", terminating" : ""), ex);
            if (FirstOfRun(r.Fingerprint)) WriteMarker(WriteRecord(r, "crash"));
            AppLog.Flush();
        }
        catch { }
    }

    // Program.Main's last-resort catch: same as an unhandled exception (the app is closing).
    public static void Fatal(Exception ex)
    {
        try
        {
            var r = Capture("crash", ex);
            AppLog.Error("crash", "fatal exception in Main (" + r.Fingerprint + ")", ex);
            if (FirstOfRun(r.Fingerprint)) WriteMarker(WriteRecord(r, "crash"));
            AppLog.Flush();
        }
        catch { }
    }

    static string Mode() { try { return PromptMode != null ? PromptMode() : "ask"; } catch { return "ask"; } }

    enum Choice { Open, Later, Never }

    // "auto" sends without a dialog, but only with an embedded token (else it behaves like "ask").
    static bool SendsAutomatically { get { return CrashPromptLogic.AutoSend(Mode(), Consent(), IssueToken.Available, Demo.On, AppLog.Enabled); } }

    static bool Consent() { try { return AutoConsent != null && AutoConsent(); } catch { return false; } }

    public const string SendLabel = "Raporu GitHub'a gönder", ShowLabel = "Raporu göster";

    static Choice Ask(IWin32Window owner, string question, ErrorReport r)
    {
        while (true)
        {
            var i = GlassDialog.ShowChoice(owner, question + "\n\n" + ReportContents + "\n" + DeliveryText, Caption,
                new[] { "Bir daha sorma", "Sonra", ShowLabel, SendLabel }, 3, 1, MessageBoxIcon.Warning);
            if (i == 2) { ReportPreview.Show(owner, r); continue; }
            return i == 3 ? Choice.Open : i == 0 ? Choice.Never : Choice.Later;
        }
    }

    static void Act(IWin32Window owner, Choice choice, ErrorReport r)
    {
        if (choice == Choice.Open) { Deliver(owner, r, false); return; }
        if (choice == Choice.Never)
        {
            AppLog.Info("report", "crash prompt turned off by the user");
            try { if (NeverAsk != null) NeverAsk(); } catch { }
            return;
        }
        AppLog.Info("report", "report postponed (asked again at the next start, at most " + CrashPromptLogic.MaxOffers.ToString(CultureInfo.InvariantCulture) + " times)");
    }

    static ErrorReport LoadRecord(string name)
    {
        if (LogDir == null || !LogRotation.IsRecordName(name)) return null;
        try
        {
            var p = Path.Combine(LogDir, name);
            if (!File.Exists(p)) return null;
            var r = ErrorReport.ParseRecord(File.ReadAllText(p, Encoding.UTF8));
            if (r != null) r.Body = NewSanitizer().Sanitize(r.Body); // idempotent; also covers names loaded since
            return r;
        }
        catch { return null; }
    }

    // At start (first time the panel is shown): a crash marker from the last session -> ask once.
    public static void OfferPendingCrash(IWin32Window owner)
    {
        if (LogDir == null || Demo.On) return;
        var text = ReadMarker();
        if (text == null) return;
        var name = CrashPromptLogic.ShouldOffer(text, Mode(), Demo.On, DateTime.UtcNow);
        if (name == null) { ClearMarker(); return; }
        var r = LoadRecord(name);
        if (r == null) { ClearMarker(); return; }
        lock (gate) promptedThisRun = true;
        if (SendsAutomatically)
        {
            ClearMarker();
            AppLog.Info("report", "sending the previous session's crash report automatically (" + (r.Fingerprint ?? "-") + ")");
            Deliver(owner, r, true);
            return;
        }
        AppLog.Info("report", "offering the report of the previous session's crash (" + (r.Fingerprint ?? "-") + ")");
        var choice = Ask(owner, "Önceki oturum beklenmedik şekilde kapandı. Hata raporunu GitHub'a göndermek ister misiniz?", r);
        SettleMarker(choice);
        Act(owner, choice, r);
    }

    // Settings > Hakkında > "Son hatayı bildir": the newest crash / error record, else a report with
    // the recent log lines only.
    public static void ReportLatest(IWin32Window owner)
    {
        if (Demo.On) { GlassDialog.Show(owner, "Demo modunda hata raporu açılmaz; tarayıcı açılmaz, hiçbir şey gönderilmez.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        ErrorReport r = null;
        try
        {
            if (LogDir != null && Directory.Exists(LogDir))
            {
                var newest = LogRotation.NewestRecord(new DirectoryInfo(LogDir).GetFiles("*.txt").Select(f => f.Name));
                if (newest != null) r = LoadRecord(newest);
            }
        }
        catch { }
        if (r == null) r = ErrorReport.Build("manual", null, Env, AppLog.Recent(ErrorReport.LogLines), NewSanitizer(), DateTime.UtcNow);
        var intro = r.Kind == "manual" ? "Kayıtlı bir hata bulunamadı. Son günlük satırlarıyla bir hata raporu hazırlandı." : "Son kaydedilen hata:\n" + r.Title;
        while (true)
        {
            var i = GlassDialog.ShowChoice(owner, intro + "\n\n" + ReportContents + "\n" + DeliveryText, Caption, new[] { "İptal", ShowLabel, SendLabel }, 2, 0, MessageBoxIcon.Question);
            if (i == 1) { ReportPreview.Show(owner, r); continue; }
            if (i == 2) Deliver(owner, r, false);
            return;
        }
    }

    // ---------- Direct send with the embedded token (modules\IssueSender.cs) ----------

    static readonly object limitsGate = new object();
    static string LimitsPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenVpnQuickSearch", "issue-limits.json"); } }

    static IssueLimitState LoadLimits()
    {
        try { var p = LimitsPath; return File.Exists(p) ? IssueLimits.FromJson(File.ReadAllText(p)) : new IssueLimitState(); }
        catch { return new IssueLimitState(); }
    }

    static void SaveLimits(IssueLimitState s)
    {
        // atomic: write .tmp, then replace (a crash mid-write must not reset the limits)
        var p = LimitsPath; var tmp = p + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(tmp, IssueLimits.ToJson(s));
            if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);
        }
        catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    // Title and body exactly as they go out: sanitized again with the names loaded now (idempotent),
    // body cut to 60 KB. "Raporu göster" shows the same text.
    public static void Outgoing(ErrorReport r, out string title, out string body)
    {
        var s = NewSanitizer();
        title = s.Sanitize(r.Title ?? "");
        body = GitHubIssueClient.Truncate(s.Sanitize(r.Body ?? ""));
    }

    static void Say(string text) { try { if (Status != null) Status(text); } catch { } }

    // Creates the issue (or one comment on an open duplicate) off the UI thread. Without a token, or
    // when sending fails / is limited: the browser flow (automatic sends only show a notice instead).
    // Logs status codes and issue numbers only: never the token, the request or the report.
    static void Deliver(IWin32Window owner, ErrorReport r, bool automatic)
    {
        if (Demo.On || r == null) return;
        var token = IssueToken.Value;
        if (token == null) { if (!automatic) OpenReport(owner, r); return; }
        if (checking) return;
        var ctx = SynchronizationContext.Current;
        string title, body;
        Outgoing(r, out title, out body);
        checking = true;
        if (!automatic && ctx != null) try { Application.UseWaitCursor = true; } catch { }
        Say("Hata raporu GitHub'a gönderiliyor…");
        ThreadPool.QueueUserWorkItem(delegate
        {
            SendResult res;
            var env = Env;   // registry / file checks the first time: not on the UI thread
            lock (limitsGate)
            {
                var limits = LoadLimits();
                res = GitHubIssueClient.Send(title, body, r.Fingerprint, env, token, GitHubIssueClient.HttpTransport, limits, DateTime.UtcNow);
                SaveLimits(limits);
            }
            Action done = delegate { Delivered(owner, r, res, automatic); };
            if (ctx == null) { checking = false; return; }
            try { ctx.Post(delegate { done(); }, null); } catch { checking = false; }
        });
    }

    static void Delivered(IWin32Window owner, ErrorReport r, SendResult res, bool automatic)
    {
        checking = false;
        try { Application.UseWaitCursor = false; } catch { }
        try
        {
            owner = Alive(owner);
            var num = "#" + res.Number.ToString(CultureInfo.InvariantCulture);
            if (res.Sent)
            {
                AppLog.Info("report", (res.Outcome == SendOutcome.Created ? "issue created " : "repeat comment on ") + num + " (status " + res.Status.ToString(CultureInfo.InvariantCulture) + ")");
                var text = res.Outcome == SendOutcome.Created ? "Hata kaydı " + num + " GitHub'da oluşturuldu." : "Bu hata zaten bildirilmiş (" + num + "); kayda bir \"tekrar\" notu eklendi.";
                Say(text);
                IssueNotice.Show(text, res.Url);
                return;
            }
            AppLog.Warn("report", "send not done: " + res.Outcome + (res.Status > 0 ? " (status " + res.Status.ToString(CultureInfo.InvariantCulture) + ")" : ""));
            string why;
            switch (res.Outcome)
            {
                case SendOutcome.Unauthorized: why = "Gönderme anahtarı geçersiz ya da iptal edilmiş; 24 saat denenmeyecek."; break;
                case SendOutcome.Stopped: why = "Gönderme 24 saatliğine durduruldu (anahtar reddedildi)."; break;
                case SendOutcome.Forbidden: why = "GitHub isteği reddetti ya da sınır aşıldı."; break;
                case SendOutcome.DailyLimit: why = "Bu bilgisayardan bugün en fazla " + IssueLimits.PerDay + " rapor gönderilebilir."; break;
                case SendOutcome.FingerprintLimit: why = "Bu hata son 24 saatte zaten gönderildi."; break;
                default: why = "GitHub'a ulaşılamadı."; break;
            }
            if (automatic) { Say("Hata raporu gönderilemedi: " + why); IssueNotice.Show("Hata raporu gönderilemedi. " + why + " \"Son hatayı bildir\" ile yeniden deneyebilirsiniz.", null); return; }
            Say("Hata raporu gönderilemedi; tarayıcıda açılıyor");
            GlassDialog.Show(owner, why + "\nRapor bunun yerine tarayıcıda açılacak; Gönder'e siz basarsınız.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            OpenReport(owner, r);
        }
        catch { }
    }

    // Duplicate check on a pool thread (read-only search for the fingerprint; failure = skip),
    // then the browser on the UI thread.
    static volatile bool checking;   // a duplicate check is running: further clicks are ignored

    static void OpenReport(IWin32Window owner, ErrorReport r)
    {
        if (Demo.On || r == null || checking) return;
        var ctx = SynchronizationContext.Current;
        if (ctx == null || r.Fingerprint == null) { FinishOpen(owner, r, null); return; }
        checking = true;
        try { Application.UseWaitCursor = true; } catch { }
        ThreadPool.QueueUserWorkItem(delegate
        {
            var dup = IssueLink.CheckDuplicate(r.Fingerprint, UpdateClient.GetText);
            // the UI may be gone by now (the app closed while the check ran)
            try { ctx.Post(delegate { FinishOpen(owner, r, dup); }, null); }
            catch { checking = false; }
        });
    }

    static IWin32Window Alive(IWin32Window owner)
    {
        var c = owner as Control;
        return c != null && (c.IsDisposed || !c.IsHandleCreated || !c.Visible) ? null : owner;
    }

    static void FinishOpen(IWin32Window owner, ErrorReport r, IssueRef dup)
    {
        checking = false;
        try { Application.UseWaitCursor = false; } catch { }
        try
        {
            owner = Alive(owner);
            if (dup != null && IssueLink.IsOurIssueUrl(dup.Url))
            {
                AppLog.Info("report", "already reported as #" + dup.Number.ToString(CultureInfo.InvariantCulture) + " (" + dup.State + ")");
                GlassDialog.Show(owner, "Bu hata zaten bildirilmiş (#" + dup.Number.ToString(CultureInfo.InvariantCulture) + ", " + (dup.Open ? "açık" : "kapalı") + ").\n" +
                    "GitHub'daki kayıt açılıyor; yeni bir bilginiz varsa oraya yorum olarak yazabilirsiniz.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenUrl(dup.Url);
                return;
            }
            bool truncated;
            var url = IssueLink.NewIssueUrl(r.Title, r.Body, IssueLink.MaxUrlLength, out truncated);
            if (truncated)
            {
                var copied = false;
                try { Clipboard.SetText(r.FullText); copied = true; } catch { }
                GlassDialog.Show(owner, copied
                    ? "Rapor tarayıcı adresine sığmadı; sayfada kısaltılmış hâli açılacak.\nTam rapor panoya kopyalandı: GitHub'da \"Temizlenmiş rapor\" alanını silip Ctrl+V ile yapıştırın."
                    : "Rapor tarayıcı adresine sığmadı; sayfada kısaltılmış hâli açılacak (pano kullanılamadı).", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            AppLog.Info("report", "new-issue page opened in the browser" + (truncated ? " (truncated, full report on the clipboard)" : ""));
            OpenUrl(url);
        }
        catch { }
    }

    static void OpenUrl(string url)
    {
        if (Demo.On || !IssueLink.IsOurIssueUrl(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public static void OpenLogFolder()
    {
        if (Demo.On || LogDir == null) return;
        try { Directory.CreateDirectory(LogDir); Process.Start("explorer.exe", "\"" + LogDir + "\""); } catch { }
    }
}
