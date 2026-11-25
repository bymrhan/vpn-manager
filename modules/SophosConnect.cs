using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;

// =====================================================================
// Sophos Connect integration, driven through its command line client sccli.exe
// (C:\Program Files (x86)\Sophos\Connect). The daemon scvpn.exe is a service; scgui.exe is only
// the tray GUI, so nothing here ever shows a window.
//   sccli list                      -> "Connections:" then one indented name per line, "  None" when empty
//   sccli get -n NAME -t config|dynamic ...
//   sccli enable -n NAME [-u USER -p PASS]   connects
//   sccli disable -n NAME                    disconnects (success when already disabled)
// Only the empty "list" output was verified on a real install (no profile there). The rest is an
// ASSUMED format, parsed defensively by the pure Parse* methods below (unit-tested with canned text):
//   - list: lines under "Connections:"; each non-empty line is a name; an optional bullet ("-", "*")
//     is stripped; "None" means no connections.
//   - get: "key: value", "key = value" or JSON-like "\"key\": \"value\"" lines. A key containing
//     gateway / server / host / remote / endpoint gives the host; a key "state"/"status"/
//     "connection state" whose value is exactly connected/established/up/active means connected.
// Anything unparsable yields empty results, never an exception.
// =====================================================================

internal static class SophosStore
{
    public const string IdPrefix = "sophos:";
    // Credential Manager target prefix (distinct from FortiClient's "OpenVpnQuickSearch:forti:").
    public const string CredentialPrefix = "OpenVpnQuickSearch:sophos:";
    const int TimeoutMs = 10000;

    public static bool IsSophos(VpnProfile p) { return p != null && p.Id != null && p.Id.StartsWith(IdPrefix, StringComparison.Ordinal); }
    public static bool IsInstalled { get { return CliPath.Length > 0; } }

    public static string CliPath
    {
        get
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var p = Path.Combine(root, "Sophos", "Connect", "sccli.exe");
                if (File.Exists(p)) return p;
            }
            return "";
        }
    }

    public static string ConnectionName(VpnProfile p)
    {
        if (p == null) return "";
        if (p.Id != null && p.Id.StartsWith(IdPrefix, StringComparison.Ordinal)) return p.Id.Substring(IdPrefix.Length);
        return p.Name ?? "";
    }

    // ---- pure parsing ------------------------------------------------

    // Names of the connections in `sccli list` output. Empty for "None", empty or unrecognised text.
    public static List<string> ParseList(string text)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(text)) return names;
        var inList = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!inList)
            {
                if (line.StartsWith("Connections", StringComparison.OrdinalIgnoreCase) && line.EndsWith(":", StringComparison.Ordinal)) inList = true;
                continue;
            }
            // Indented lines belong to the list; an unindented "Word:" line starts another section.
            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0]) && line.EndsWith(":", StringComparison.Ordinal)) break;
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) line = line.Substring(2).Trim();
            if (line.Length == 0 || string.Equals(line, "None", StringComparison.OrdinalIgnoreCase)) continue;
            if (!names.Contains(line)) names.Add(line);
        }
        return names;
    }

    // key/value pairs from `sccli get` output ("k: v", "k = v", "\"k\": \"v\","). Keys are lower-cased.
    public static List<KeyValuePair<string, string>> ParsePairs(string text)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(text)) return pairs;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().TrimEnd(',');
            var i = line.IndexOfAny(new[] { ':', '=' });
            if (i <= 0) continue;
            var key = line.Substring(0, i).Trim().Trim('"').ToLowerInvariant();
            var val = line.Substring(i + 1).Trim().Trim('"').Trim();
            if (key.Length > 0 && val.Length > 0) pairs.Add(new KeyValuePair<string, string>(key, val));
        }
        return pairs;
    }

    // Gateway of the connection from `sccli get -t config` output, or "".
    public static string ParseHost(string text)
    {
        foreach (var kv in ParsePairs(text))
        {
            var k = kv.Key;
            if (k.IndexOf("gateway", StringComparison.Ordinal) >= 0 || k.IndexOf("server", StringComparison.Ordinal) >= 0
                || k.IndexOf("host", StringComparison.Ordinal) >= 0 || k.IndexOf("remote", StringComparison.Ordinal) >= 0
                || k.IndexOf("endpoint", StringComparison.Ordinal) >= 0)
                return kv.Value;
        }
        return "";
    }

    // True when `sccli get` output says the connection is up. "Disconnected" and friends do not count.
    public static bool ParseConnected(string text)
    {
        foreach (var kv in ParsePairs(text))
        {
            if (kv.Key != "state" && kv.Key != "status" && kv.Key != "connection state" && kv.Key != "connection status" && kv.Key != "connectionstate") continue;
            var v = kv.Value.ToLowerInvariant();
            if (v == "connected" || v == "established" || v == "up" || v == "active" || v == "online") return true;
        }
        return false;
    }

    // True when the `sccli get` output has a state/status key at all (connected or not).
    public static bool HasState(string text)
    {
        foreach (var kv in ParsePairs(text))
            if (kv.Key == "state" || kv.Key == "status" || kv.Key == "connection state" || kv.Key == "connection status" || kv.Key == "connectionstate") return true;
        return false;
    }

    // The adapter is only consulted when sccli's output was not understood for any connection;
    // a clear "Disconnected" must not be overridden by an adapter that happens to be Up.
    public static bool UseAdapterFallback(bool anyStateUnderstood) { return !anyStateUnderstood; }

    // Builds the profile the same way FortiStore.Load does.
    public static VpnProfile MakeProfile(string name, string host)
    {
        var p = new VpnProfile { Id = IdPrefix + name, Name = name, Host = host ?? "", Username = "", Source = "", LastConnected = 0 };
        p.Initials = ProfileStore.MakeInitials(name);
        p.Keys = new[] { ProfileStore.SearchKey(p.Name), ProfileStore.SearchKey(p.Host), "SOPHOS CONNECT SOPHOS" };
        return p;
    }

    // ---- sccli --------------------------------------------------------

    public static List<VpnProfile> Load()
    {
        var result = new List<VpnProfile>();
        try
        {
            if (!IsInstalled) return result;
            string o, e;
            if (!Run(new[] { "list" }, out o, out e)) return result;
            foreach (var name in ParseList(o))
            {
                string cfg, err;
                var host = Run(new[] { "get", "-n", name, "-t", "config" }, out cfg, out err) ? ParseHost(cfg) : "";
                result.Add(MakeProfile(name, host));
            }
        }
        catch { }
        return result;
    }

    // Connects through "sccli enable". cred (optional, saved under CredentialPrefix) supplies -u/-p;
    // the password is only passed to sccli and never logged. Returns an error message or null.
    public static string Connect(VpnProfile p, FortiCredential cred)
    {
        if (!IsInstalled) return "Sophos Connect bulunamadı.";
        var name = ConnectionName(p);
        if (name.Length == 0) return "Sophos bağlantı adı boş.";
        var args = new List<string> { "enable", "-n", name };
        if (cred != null && !string.IsNullOrEmpty(cred.Password))
        {
            if (!string.IsNullOrEmpty(cred.Username)) { args.Add("-u"); args.Add(cred.Username); }
            args.Add("-p"); args.Add(cred.Password);
        }
        string o, e;
        try { if (!Run(args.ToArray(), out o, out e)) return "Sophos bağlantısı başlatılamadı" + FirstLine(e, o); }
        catch (Exception ex) { return "Sophos bağlantısı başlatılamadı: " + ex.Message; }
        return null;
    }

    // Disconnects through "sccli disable". Returns an error message or null.
    public static string Disconnect(string name)
    {
        if (!IsInstalled) return "Sophos Connect bulunamadı.";
        if (string.IsNullOrEmpty(name)) return "Sophos bağlantı adı boş.";
        string o, e;
        try { if (!Run(new[] { "disable", "-n", name }, out o, out e)) return "Sophos bağlantısı kesilemedi" + FirstLine(e, o); }
        catch (Exception ex) { return "Sophos bağlantısı kesilemedi: " + ex.Message; }
        return null;
    }

    // Name of the connected Sophos connection, or null. Safe to call from a worker thread.
    public static string ConnectedName()
    {
        string name;
        return TryConnectedName(out name) ? name : null;
    }

    // False when the status could not be read (sccli failed or timed out): the caller keeps what it
    // showed before. True with name = connected connection, "" (up, ambiguous) or null (none).
    // Asks sccli for each connection's state; when the output is not understood, falls back to the
    // Sophos virtual adapter being Up (then the only connection, or "" when ambiguous).
    public static bool TryConnectedName(out string name)
    {
        name = null;
        try
        {
            if (!IsInstalled) return true;
            string o, e;
            if (!Run(new[] { "list" }, out o, out e)) return false;
            var names = ParseList(o);
            if (names.Count == 0) return true;
            bool understood = false, failed = false;
            foreach (var n in names)
            {
                string d, err;
                if (!Run(new[] { "get", "-n", n, "-t", "dynamic" }, out d, out err)) { failed = true; continue; }
                if (ParseConnected(d)) { name = n; return true; }
                if (HasState(d)) understood = true;
            }
            if (UseAdapterFallback(understood) && AdapterUp()) { name = names.Count == 1 ? names[0] : ""; return true; }
            return !failed;
        }
        catch { name = null; return false; }
    }

    public static bool AdapterUp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var d = ni.Description ?? ""; var n = ni.Name ?? "";
                if (ni.OperationalStatus != OperationalStatus.Up || VpnAdapters.IsFilterLayer(d)) continue;
                if (d.IndexOf("Sophos", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Sophos", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }
        catch { }
        return false;
    }

    static string FirstLine(string a, string b)
    {
        foreach (var s in new[] { a, b })
        {
            if (string.IsNullOrEmpty(s)) continue;
            foreach (var l in s.Replace("\r", "").Split('\n')) { var t = l.Trim(); if (t.Length > 0) return ": " + (t.Length > 120 ? t.Substring(0, 120) : t); }
        }
        return ".";
    }

    // Runs sccli hidden with a 10 s timeout. True when it exited with code 0. Arguments are quoted;
    // they are never written to any log.
    static bool Run(string[] args, out string stdout, out string stderr)
    {
        stdout = ""; stderr = "";
        var psi = new ProcessStartInfo
        {
            FileName = CliPath, Arguments = JoinArgs(args), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using (var pr = new Process { StartInfo = psi })
        {
            var so = new StringBuilder(); var se = new StringBuilder();
            pr.OutputDataReceived += (s, ev) => { if (ev.Data != null) lock (so) so.AppendLine(ev.Data); };
            pr.ErrorDataReceived += (s, ev) => { if (ev.Data != null) lock (se) se.AppendLine(ev.Data); };
            pr.Start();
            pr.BeginOutputReadLine(); pr.BeginErrorReadLine();
            if (!pr.WaitForExit(TimeoutMs))
            {
                try { pr.Kill(); } catch { }
                stderr = "zaman aşımı";
                return false;
            }
            pr.WaitForExit(); // flush the async readers
            lock (so) stdout = so.ToString();
            lock (se) stderr = se.ToString();
            return pr.ExitCode == 0;
        }
    }

    internal static string JoinArgs(string[] args)
    {
        var sb = new StringBuilder();
        foreach (var a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Quote(a));
        }
        return sb.ToString();
    }

    // CommandLineToArgvW-compatible quoting.
    internal static string Quote(string a)
    {
        if (a == null) a = "";
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in a)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(c); slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }
}
