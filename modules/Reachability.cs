using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// =====================================================================
// Server reachability (dots next to profiles).
// tcp: TcpClient connect with timeout. udp: DNS check, then ICMP ping.
// Background work never touches UI; Changed is raised on the UI context.
// =====================================================================

internal sealed class RemoteInfo
{
    public string Host;
    public int Port;
    public string Proto; // "udp" or "tcp"
}

internal enum ReachState { Unknown, Checking, Up, Down }

internal static class Reachability
{
    const int CacheSeconds = 120;
    const int MaxWorkers = 8;
    const int TcpTimeoutMs = 2500;
    const int PingTimeoutMs = 1500;
    const int NotifyIntervalMs = 250; // at most ~4 Changed events per second
    const int DefaultPort = 1194;

    sealed class Entry
    {
        public ReachState State = ReachState.Unknown;
        public DateTime CheckedUtc = DateTime.MinValue;
        public bool Queued; // waiting in queue or being probed
    }

    sealed class Candidate
    {
        public string Host, Port, Proto;
        public int Block; // -1 = top level, otherwise <connection> block index
    }

    static readonly object Gate = new object();          // entries, queue, workers, notify state
    static readonly object RemoteGate = new object();    // remote cache
    static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
    static readonly Queue<string> queue = new Queue<string>();
    static readonly Dictionary<string, string> hostHints = new Dictionary<string, string>();
    static int workers;

    static SynchronizationContext uiContext;
    static Timer notifyTimer;
    static bool notifyPending;

    static DateTime cacheStamp = DateTime.MinValue;
    static bool cacheValid;
    static Dictionary<string, RemoteInfo> remoteCache = new Dictionary<string, RemoteInfo>();

    public static event EventHandler Changed;

    public static void Init(SynchronizationContext ui)
    {
        lock (Gate) { uiContext = ui; }
    }

    public static ReachState Get(string profileId)
    {
        try
        {
            if (string.IsNullOrEmpty(profileId)) return ReachState.Unknown;
            lock (Gate)
            {
                Entry e;
                return entries.TryGetValue(profileId, out e) ? e.State : ReachState.Unknown;
            }
        }
        catch { return ReachState.Unknown; }
    }

    public static RemoteInfo GetRemote(string profileId)
    {
        try
        {
            if (string.IsNullOrEmpty(profileId)) return null;
            lock (RemoteGate)
            {
                EnsureRemoteCache();
                RemoteInfo r;
                if (remoteCache.TryGetValue(profileId, out r)) return r;
            }
            string hint;
            lock (Gate)
            {
                if (!hostHints.TryGetValue(profileId, out hint)) hint = null;
            }
            return FromHint(profileId, hint);
        }
        catch { return null; }
    }

    // Endpoint to test for a profile whose only address is its Host (FortiClient, Sophos Connect,
    // WatchGuard SSL VPN). "host:port" hints (SSL VPN gateways, WatchGuard Server entries) are
    // TCP/HTTPS endpoints; FortiClient and WatchGuard hosts without a port use 443; anything else
    // (e.g. a Sophos IPsec gateway) only gets the DNS + ping check.
    internal static RemoteInfo FromHint(string profileId, string hint)
    {
        if (string.IsNullOrEmpty(hint)) return null;
        hint = hint.Trim();
        var colon = hint.LastIndexOf(':'); int port;
        if (colon > 0 && hint.IndexOf(':') == colon && int.TryParse(hint.Substring(colon + 1), out port))
            return new RemoteInfo { Host = hint.Substring(0, colon), Port = port, Proto = "tcp" };
        var id = profileId ?? "";
        if (id.StartsWith("forti-", StringComparison.Ordinal) || id.StartsWith(WatchGuardStore.IdPrefix, StringComparison.Ordinal))
            return new RemoteInfo { Host = hint, Port = 443, Proto = "tcp" };
        return new RemoteInfo { Host = hint, Port = DefaultPort, Proto = "udp" };
    }

    public static void CheckAll(IEnumerable<VpnProfile> profiles, bool force)
    {
        try
        {
            if (profiles == null) return;
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                foreach (var p in profiles)
                {
                    if (p == null || string.IsNullOrEmpty(p.Id)) continue;
                    if (!string.IsNullOrEmpty(p.Host)) hostHints[p.Id] = p.Host;
                    Entry e;
                    if (!entries.TryGetValue(p.Id, out e)) { e = new Entry(); entries[p.Id] = e; }
                    if (e.Queued) continue;
                    if (!force && e.CheckedUtc != DateTime.MinValue && (now - e.CheckedUtc).TotalSeconds < CacheSeconds) continue;
                    e.Queued = true;
                    e.State = ReachState.Checking;
                    queue.Enqueue(p.Id);
                }
                while (queue.Count > 0 && workers < MaxWorkers)
                {
                    workers++;
                    var t = new Thread(Worker) { IsBackground = true, Name = "ReachabilityWorker" };
                    t.Start();
                }
            }
            NotifyChanged();
        }
        catch { }
    }

    static void Worker()
    {
        while (true)
        {
            string id;
            lock (Gate)
            {
                if (queue.Count == 0) { workers--; return; }
                id = queue.Dequeue();
            }

            var result = ReachState.Unknown;
            try { result = Probe(GetRemote(id)); }
            catch { result = ReachState.Unknown; }

            lock (Gate)
            {
                Entry e;
                if (entries.TryGetValue(id, out e))
                {
                    e.State = result;
                    e.CheckedUtc = DateTime.UtcNow;
                    e.Queued = false;
                }
            }
            NotifyChanged();
        }
    }

    static ReachState Probe(RemoteInfo r)
    {
        if (r == null || string.IsNullOrEmpty(r.Host)) return ReachState.Unknown;

        IPAddress[] addrs;
        try { addrs = Dns.GetHostAddresses(r.Host); }
        catch { return ReachState.Down; } // name does not resolve
        if (addrs == null || addrs.Length == 0) return ReachState.Down;

        if (r.Proto == "tcp") return ProbeTcp(r.Host, r.Port);
        return ProbeUdp(addrs);
    }

    static ReachState ProbeTcp(string host, int port)
    {
        TcpClient client = null;
        try
        {
            client = new TcpClient();
            var ar = client.BeginConnect(host, port, null, null);
            var ok = ar.AsyncWaitHandle.WaitOne(TcpTimeoutMs);
            ar.AsyncWaitHandle.Close();
            if (!ok) return ReachState.Down;
            client.EndConnect(ar);
            return ReachState.Up;
        }
        catch { return ReachState.Down; }
        finally { if (client != null) client.Close(); }
    }

    static ReachState ProbeUdp(IPAddress[] addrs)
    {
        IPAddress target = null;
        foreach (var a in addrs) if (a.AddressFamily == AddressFamily.InterNetwork) { target = a; break; }
        if (target == null) target = addrs[0];
        try
        {
            using (var ping = new Ping())
            {
                var reply = ping.Send(target, PingTimeoutMs);
                return reply != null && reply.Status == IPStatus.Success ? ReachState.Up : ReachState.Unknown;
            }
        }
        catch { return ReachState.Unknown; } // ICMP is often filtered; not proof of Down
    }

    static void NotifyChanged()
    {
        lock (Gate)
        {
            if (notifyPending) return;
            notifyPending = true;
            if (notifyTimer == null) notifyTimer = new Timer(OnNotifyTimer, null, NotifyIntervalMs, Timeout.Infinite);
            else notifyTimer.Change(NotifyIntervalMs, Timeout.Infinite);
        }
    }

    static void OnNotifyTimer(object state)
    {
        SynchronizationContext ctx;
        lock (Gate)
        {
            notifyPending = false;
            ctx = uiContext;
        }
        try
        {
            if (ctx != null) ctx.Post(RaiseChanged, null);
            else RaiseChanged(null);
        }
        catch { }
    }

    static void RaiseChanged(object state)
    {
        var h = Changed;
        if (h == null) return;
        try { h(null, EventArgs.Empty); } catch { }
    }

    // ---------------------------------------------------------------
    // Remote cache, keyed by the OpenVPN Connect config.json write time.
    // Must be called while holding RemoteGate.
    // ---------------------------------------------------------------

    static void EnsureRemoteCache()
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(ProfileStore.ConfigPath); }
        catch { stamp = DateTime.MinValue; }
        if (cacheValid && stamp == cacheStamp) return;

        var map = new Dictionary<string, RemoteInfo>();
        try { ParseConfig(map); }
        catch { map.Clear(); }
        remoteCache = map;
        cacheStamp = stamp;
        cacheValid = true;
    }

    static void ParseConfig(Dictionary<string, RemoteInfo> map)
    {
        var path = ProfileStore.ConfigPath;
        if (!File.Exists(path)) return;
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();

        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        var root = js.DeserializeObject(text) as IDictionary<string, object>;
        if (root == null || !root.ContainsKey("persist:root")) return;
        var persisted = js.DeserializeObject(Convert.ToString(root["persist:root"])) as IDictionary<string, object>;
        if (persisted == null || !persisted.ContainsKey("status")) return;
        var status = js.DeserializeObject(Convert.ToString(persisted["status"])) as IDictionary<string, object>;
        object raw;
        if (status == null || !status.TryGetValue("profiles", out raw)) return;
        var profiles = raw as IDictionary<string, object>;
        if (profiles == null) return;

        foreach (var pair in profiles)
        {
            try
            {
                var item = pair.Value as IDictionary<string, object>;
                if (item == null) continue;
                var id = TextOf(item, "id");
                if (string.IsNullOrEmpty(id)) id = pair.Key;

                // 1) .ovpn text with remote lines, 2) structured profileConfig.remoteHost/remotePort/remoteProto
                // (what OpenVPN Connect 3.9 stores for saved profiles), 3) profile hostname.
                var ovpn = FindOvpnText(item);
                var info = ovpn != null ? ParseOvpn(ovpn) : new RemoteInfo { Port = DefaultPort, Proto = "udp" };
                if (string.IsNullOrEmpty(info.Host))
                {
                    var pc = item.ContainsKey("profileConfig") ? item["profileConfig"] as IDictionary<string, object> : null;
                    if (pc != null)
                    {
                        info.Host = TextOf(pc, "remoteHost").Trim();
                        info.Port = ParsePort(TextOf(pc, "remotePort").Trim());
                        info.Proto = NormalizeProto(TextOf(pc, "remoteProto").Trim());
                    }
                }
                if (string.IsNullOrEmpty(info.Host))
                {
                    var hostname = TextOf(item, "hostname");
                    if (string.IsNullOrWhiteSpace(hostname)) hostname = TextOf(item, "remoteHost");
                    info.Host = hostname.Trim();
                }
                if (!string.IsNullOrEmpty(info.Host)) map[id] = info;
            }
            catch { }
        }
    }

    static string TextOf(IDictionary<string, object> d, string k)
    {
        object v;
        return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : "";
    }

    // The .ovpn text lives in one of these fields; pick the one that contains remote lines.
    static string FindOvpnText(IDictionary<string, object> item)
    {
        string[] names = { "profileConfig", "config", "mergedConfig" };
        foreach (var n in names)
        {
            var s = TextOf(item, n);
            if (s.IndexOf("remote", StringComparison.OrdinalIgnoreCase) >= 0) return s;
        }
        return null;
    }

    // Parses remote / proto / port directives (top level, <connection> blocks).
    // The first remote line in file order wins; missing values fall back to
    // the block value, then the top-level value, then udp / 1194.
    static RemoteInfo ParseOvpn(string text)
    {
        string globalProto = null, globalPort = null;
        var blockProto = new Dictionary<int, string>();
        var blockPort = new Dictionary<int, string>();
        var candidates = new List<Candidate>();
        var block = -1;
        var blockCount = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            if (line == "<connection>") { block = blockCount++; continue; }
            if (line == "</connection>") { block = -1; continue; }

            var tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 2) continue;
            var key = tok[0].ToLowerInvariant();

            if (key == "remote")
            {
                var c = new Candidate { Block = block, Host = StripBrackets(tok[1]) };
                for (var i = 2; i < tok.Length; i++)
                {
                    var t = tok[i];
                    if (IsAllDigits(t)) { if (c.Port == null) c.Port = t; }
                    else if (IsProtoWord(t)) { if (c.Proto == null) c.Proto = t; }
                }
                candidates.Add(c);
            }
            else if (key == "proto")
            {
                if (block < 0) { if (globalProto == null) globalProto = tok[1]; }
                else if (!blockProto.ContainsKey(block)) blockProto[block] = tok[1];
            }
            else if (key == "port")
            {
                if (block < 0) { if (globalPort == null) globalPort = tok[1]; }
                else if (!blockPort.ContainsKey(block)) blockPort[block] = tok[1];
            }
        }

        var result = new RemoteInfo();
        string proto = null, port = null;
        if (candidates.Count > 0)
        {
            var c = candidates[0];
            result.Host = c.Host;
            proto = c.Proto;
            port = c.Port;
            if (c.Block >= 0)
            {
                if (proto == null) proto = BlockValue(blockProto, c.Block);
                if (port == null) port = BlockValue(blockPort, c.Block);
            }
        }
        if (proto == null) proto = globalProto;
        if (port == null) port = globalPort;
        result.Proto = NormalizeProto(proto);
        result.Port = ParsePort(port);
        return result;
    }

    static string BlockValue(Dictionary<int, string> d, int key)
    {
        string v;
        return d.TryGetValue(key, out v) ? v : null;
    }

    static string StripBrackets(string host)
    {
        if (host.Length >= 2 && host[0] == '[' && host[host.Length - 1] == ']') return host.Substring(1, host.Length - 2);
        return host;
    }

    static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s) if (c < '0' || c > '9') return false;
        return true;
    }

    static bool IsProtoWord(string s)
    {
        switch (s.ToLowerInvariant())
        {
            case "udp": case "udp4": case "udp6":
            case "tcp": case "tcp4": case "tcp6":
            case "tcp-client": case "tcp-server":
                return true;
            default:
                return false;
        }
    }

    static string NormalizeProto(string p)
    {
        if (string.IsNullOrEmpty(p)) return "udp";
        var s = p.ToLowerInvariant();
        if (s.StartsWith("tcp", StringComparison.Ordinal)) return "tcp";
        return "udp";
    }

    static int ParsePort(string s)
    {
        int port;
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port <= 0 || port > 65535) return DefaultPort;
        return port;
    }
}
