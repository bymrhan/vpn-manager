using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// =====================================================================
// WatchGuard Mobile VPN with SSL integration (see docs\providers\watchguard-ssl.md).
// - Profiles: HKCU\Software\WatchGuard\SSLVPNClient\Settings, value Server = "host:port,host:port,"
//   (trailing comma), Username, UseSAML. One profile per Server entry. Nothing is started to list them.
// - The client (wgsslvpnc.exe, 32-bit native WTL/ATL GUI) has no connect command line; its only
//   switches are /noconnect (start in the tray only: verified, opens no window and connects nothing)
//   and /Uninstall. Connecting drives its login dialog (resource 129, standard Win32 controls) with
//   window messages, which work while the dialog is parked off-screen: server combo 1034, user name
//   edit 1000, password edit 1001 (ES_PASSWORD), Connect button 1005. The dialog is opened through
//   the tray menu command "Connect..." (32777) posted to the client's hidden main window.
// - Status: wgsslvpnc.log ("Requesting client configuration from <server>", "SSLVPN Connected",
//   "Connection Closed.", openvpn ">STATE:...,EXITING"), the client's TAP adapter and its openvpn.exe.
// The password is only sent to the password edit box; it is never read back, logged or stored here.
// =====================================================================

internal static class WatchGuardStore
{
    public const string IdPrefix = "watchguard-ssl:";
    // Credential Manager target prefix, key = server entry (distinct from Forti's and Sophos').
    public const string CredentialPrefix = "OpenVpnQuickSearch:watchguard:";
    public const string DisplayName = "WatchGuard SSL VPN";
    public const string ProcessName = "wgsslvpnc";
    const string SettingsKey = @"Software\WatchGuard\SSLVPNClient\Settings";
    const string InstallFolder = @"WatchGuard\WatchGuard Mobile VPN with SSL";

    public static bool IsWatchGuard(VpnProfile p) { return p != null && p.Id != null && p.Id.StartsWith(IdPrefix, StringComparison.Ordinal); }
    public static bool IsInstalled { get { return ClientPath.Length > 0; } }

    public static string ClientPath
    {
        get
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var p = Path.Combine(root, InstallFolder, "wgsslvpnc.exe");
                if (File.Exists(p)) return p;
            }
            return "";
        }
    }

    public static string LogPath
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WatchGuard", "Mobile VPN", "wgsslvpnc.log"); }
    }

    // The registry Server entry ("host:port") a profile stands for.
    public static string ServerOf(VpnProfile p)
    {
        if (p == null) return "";
        if (p.Id != null && p.Id.StartsWith(IdPrefix, StringComparison.Ordinal)) return p.Id.Substring(IdPrefix.Length);
        return p.Host ?? "";
    }

    // ---- pure parsing ------------------------------------------------

    // Entries of the registry Server value "host:port,host:port," in order, trimmed, without
    // empty or repeated (case-insensitive) entries.
    public static List<string> ParseServers(string value)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(value)) return list;
        foreach (var raw in value.Split(new[] { ',', ';' }))
        {
            var s = raw.Trim();
            if (s.Length == 0) continue;
            var dup = false;
            foreach (var e in list) if (string.Equals(e, s, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
            if (!dup) list.Add(s);
        }
        return list;
    }

    // "vpn.example:443" -> "vpn.example"; "[fd00::1]:443" -> "fd00::1"; entries without a port stay.
    public static string HostOf(string server)
    {
        if (string.IsNullOrEmpty(server)) return "";
        var s = server.Trim();
        if (s.StartsWith("[", StringComparison.Ordinal))
        {
            var close = s.IndexOf(']');
            return close > 0 ? s.Substring(1, close - 1) : s;
        }
        var i = s.LastIndexOf(':');
        if (i <= 0 || s.IndexOf(':') != i) return s; // no port, or a bare IPv6 address
        for (var k = i + 1; k < s.Length; k++) if (!char.IsDigit(s[k])) return s;
        return s.Substring(0, i);
    }

    // "WatchGuard SSL VPN" for a single server, "WatchGuard SSL VPN · host" when there are several.
    public static string ProfileName(string server, int serverCount)
    {
        if (serverCount <= 1) return DisplayName;
        return DisplayName + " · " + HostOf(server);
    }

    public static VpnProfile MakeProfile(string server, string username, int serverCount)
    {
        var name = ProfileName(server, serverCount);
        var p = new VpnProfile { Id = IdPrefix + server, Name = name, Host = server ?? "", Username = username ?? "", Source = "", LastConnected = 0 };
        p.Initials = ProfileStore.MakeInitials(serverCount <= 1 ? DisplayName : HostOf(server));
        p.Keys = new[] { ProfileStore.SearchKey(p.Name), ProfileStore.SearchKey(p.Host), "WATCHGUARD SSL VPN WATCHGUARD" };
        return p;
    }

    // True when what the client shows (combo text / item, log entry) is this server: the full
    // "host:port" entry or only its host, case-insensitive.
    public static bool ServerMatches(string shown, string server)
    {
        if (string.IsNullOrEmpty(shown) || string.IsNullOrEmpty(server)) return false;
        shown = shown.Trim(); server = server.Trim();
        if (string.Equals(shown, server, StringComparison.OrdinalIgnoreCase)) return true;
        var host = HostOf(server);
        if (host != server) return string.Equals(shown, host, StringComparison.OrdinalIgnoreCase); // shown without the port
        return string.Equals(HostOf(shown), server, StringComparison.OrdinalIgnoreCase);           // entry without a port
    }

    // The registry entry a logged server stands for, or null.
    public static string MatchServer(string logged, IList<string> servers)
    {
        if (string.IsNullOrEmpty(logged) || servers == null) return null;
        foreach (var s in servers) if (string.Equals(s, logged.Trim(), StringComparison.OrdinalIgnoreCase)) return s;
        foreach (var s in servers) if (ServerMatches(logged, s)) return s;
        return null;
    }

    public sealed class LogState
    {
        public bool Known, Connected;
        public string Server = "";
    }

    // Last connection state recorded in (a tail of) wgsslvpnc.log. Lines look like
    // "2026-01-01T10:00:00.000 <message>" and end with "\r\r\n". Unknown text gives Known = false.
    public static LogState ParseLog(string text)
    {
        var st = new LogState();
        if (string.IsNullOrEmpty(text)) return st;
        const string Requesting = "Requesting client configuration from ";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ');
            if (line.Length == 0) continue;
            var i = line.IndexOf(Requesting, StringComparison.Ordinal);
            if (i >= 0) { st.Known = true; st.Connected = false; st.Server = line.Substring(i + Requesting.Length).Trim(); continue; }
            if (line.IndexOf("SSLVPN Connected", StringComparison.Ordinal) >= 0
                || (line.IndexOf(">STATE:", StringComparison.Ordinal) >= 0 && line.IndexOf(",CONNECTED,SUCCESS", StringComparison.Ordinal) >= 0))
            { st.Known = true; st.Connected = true; continue; }
            if (line.IndexOf("Launching WatchGuard Mobile VPN with SSL client", StringComparison.Ordinal) >= 0)
            { st.Known = true; st.Connected = false; st.Server = ""; continue; }
            if (line.IndexOf("Connection Closed.", StringComparison.Ordinal) >= 0
                || line.IndexOf("OnEndSession", StringComparison.Ordinal) >= 0
                || line.IndexOf("Disconnected!", StringComparison.Ordinal) >= 0
                || (line.IndexOf(">STATE:", StringComparison.Ordinal) >= 0
                    && (line.IndexOf(",EXITING,", StringComparison.Ordinal) >= 0 || line.IndexOf(",RECONNECTING,", StringComparison.Ordinal) >= 0
                        || line.IndexOf(",CONNECTED,FAILED", StringComparison.Ordinal) >= 0)))
            { st.Known = true; st.Connected = false; continue; }
        }
        return st;
    }

    // Connected = the client runs, and the log says connected while the tunnel (TAP adapter or the
    // client's openvpn.exe) is up. Without a readable log, the adapter alone decides.
    public static bool Decide(bool clientRunning, bool adapterUp, bool openVpnRunning, LogState log)
    {
        if (!clientRunning) return false; // exiting the client closes its connection
        if (log != null && log.Known) return log.Connected && (adapterUp || openVpnRunning);
        return adapterUp;
    }

    // WatchGuard installs the plain "TAP-Windows Adapter V9" (tap0901); OpenVPN Connect's is
    // "TAP-Windows Adapter V9 for OpenVPN Connect" and must not count.
    public static bool IsClientTapAdapter(string description)
    {
        if (string.IsNullOrEmpty(description) || VpnAdapters.IsFilterLayer(description)) return false;
        var d = description.Trim();
        return string.Equals(d, "TAP-Windows Adapter V9", StringComparison.OrdinalIgnoreCase)
            || d.StartsWith("TAP-Windows Adapter V9 #", StringComparison.OrdinalIgnoreCase);
    }

    // ---- registry ----------------------------------------------------

    static void ReadSettings(out string server, out string username, out bool saml)
    {
        server = ""; username = ""; saml = false;
        using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            if (k == null) return;
            server = Convert.ToString(k.GetValue("Server", ""));
            username = Convert.ToString(k.GetValue("Username", "")).Trim();
            saml = Convert.ToString(k.GetValue("UseSAML", "0")) == "1";
        }
    }

    public static List<string> Servers()
    {
        try { string s, u; bool saml; ReadSettings(out s, out u, out saml); return ParseServers(s); }
        catch { return new List<string>(); }
    }

    // One profile per Server entry. Reads the registry only; never starts the client.
    public static List<VpnProfile> Load()
    {
        var result = new List<VpnProfile>();
        try
        {
            if (!IsInstalled) return result;
            string s, user; bool saml;
            ReadSettings(out s, out user, out saml);
            var servers = ParseServers(s);
            foreach (var server in servers) result.Add(MakeProfile(server, user, servers.Count));
        }
        catch { }
        return result;
    }

    // ---- status ------------------------------------------------------

    public static bool ClientRunning
    {
        get
        {
            var procs = Process.GetProcessesByName(ProcessName);
            try { return procs.Length > 0; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
    }

    // Registry entry of the connected server, "" when connected but the server is not known, or
    // null. Cheap while the client is not running (one process lookup); safe on a worker thread.
    public static string ConnectedServer()
    {
        try
        {
            if (!IsInstalled || !ClientRunning) return null;
            var adapter = AdapterUp();
            var ovpn = OpenVpnRunning();
            if (!adapter && !ovpn) return null;
            var log = ParseLog(ReadLogTail(256 * 1024));
            if (!Decide(true, adapter, ovpn, log)) return null;
            var servers = Servers();
            var m = MatchServer(log.Server, servers);
            if (m != null) return m;
            if (servers.Count == 1) return servers[0];
            return log.Server ?? "";
        }
        catch { return null; }
    }

    public static bool AdapterUp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                if (ni.OperationalStatus == OperationalStatus.Up && IsClientTapAdapter(ni.Description)) return true;
        }
        catch { }
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    // The openvpn.exe that WatchGuard's service starts (from the client's install folder).
    public static bool OpenVpnRunning()
    {
        var procs = Process.GetProcessesByName("openvpn");
        try
        {
            foreach (var p in procs)
            {
                var h = OpenProcess(0x1000, false, p.Id); // PROCESS_QUERY_LIMITED_INFORMATION
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new StringBuilder(1024); var n = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref n) && sb.ToString().IndexOf(InstallFolder, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                finally { CloseHandle(h); }
            }
        }
        catch { }
        finally { foreach (var p in procs) p.Dispose(); }
        return false;
    }

    static string ReadLogTail(int bytes)
    {
        try
        {
            var path = LogPath;
            if (!File.Exists(path)) return "";
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var len = fs.Length;
                var start = Math.Max(0, len - bytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[len - start];
                var read = 0;
                while (read < buf.Length) { var r = fs.Read(buf, read, buf.Length - read); if (r <= 0) break; read += r; }
                return Encoding.Default.GetString(buf, 0, read);
            }
        }
        catch { return ""; }
    }

    // ---- connect / disconnect ----------------------------------------

    public enum ConnectOutcome { Pressed, AlreadyConnected, ShownToUser, Failed }

    // Connects and returns an error / instruction message, or null when Connect was pressed or the
    // server is already connected. Blocks for up to ~25 s: call it from a worker thread.
    public static string Connect(VpnProfile p, FortiCredential cred)
    {
        string message;
        var o = Connect(p, cred, out message);
        return o == ConnectOutcome.Pressed || o == ConnectOutcome.AlreadyConnected ? null : message;
    }

    // cred (optional, Credential Manager under CredentialPrefix + server): user name and password
    // typed into the client's dialog. Without a password the client's own remembered password is
    // used; when it has none, the dialog is shown to the user (ShownToUser) with the server chosen.
    public static ConnectOutcome Connect(VpnProfile p, FortiCredential cred, out string message)
    {
        message = null;
        try
        {
            if (!IsInstalled) { message = "WatchGuard Mobile VPN with SSL bulunamadı."; return ConnectOutcome.Failed; }
            var server = ServerOf(p);
            if (server.Length == 0) { message = "WatchGuard sunucusu boş."; return ConnectOutcome.Failed; }
            var current = ConnectedServer();
            if (current != null)
            {
                if (current.Length > 0 && string.Equals(current, server, StringComparison.OrdinalIgnoreCase)) return ConnectOutcome.AlreadyConnected;
                message = "WatchGuard'da şu an başka bir bağlantı açık. Önce o bağlantıyı kesin.";
                return ConnectOutcome.Failed;
            }
            string regServer, regUser; bool saml;
            ReadSettings(out regServer, out regUser, out saml);
            var user = cred != null && !string.IsNullOrEmpty(cred.Username) ? cred.Username : (p.Username ?? "");
            if (saml)
            {
                var err = WatchGuardGui.Open(server, user);
                message = err ?? "WatchGuard SAML ile giriş istiyor: açılan WatchGuard penceresinden bağlanın.";
                return err == null ? ConnectOutcome.ShownToUser : ConnectOutcome.Failed;
            }
            return WatchGuardGui.Connect(server, user, cred == null ? null : cred.Password, out message);
        }
        catch (Exception ex) { message = "WatchGuard bağlantısı başlatılamadı: " + ex.Message; return ConnectOutcome.Failed; }
    }

    // Opens the client's login dialog on screen with the profile's server chosen (no Connect).
    public static string Open(VpnProfile p)
    {
        if (!IsInstalled) return "WatchGuard Mobile VPN with SSL bulunamadı.";
        try { return WatchGuardGui.Open(ServerOf(p), p == null ? "" : p.Username); }
        catch (Exception ex) { return "WatchGuard açılamadı: " + ex.Message; }
    }

    // Disconnects the client's connection (tray command, then "Exit" as a fallback). Returns an error
    // or null. Blocks for up to ~25 s: call it from a worker thread.
    public static string Disconnect()
    {
        if (!IsInstalled) return "WatchGuard Mobile VPN with SSL bulunamadı.";
        try { return WatchGuardGui.Disconnect(); }
        catch (Exception ex) { return "WatchGuard bağlantısı kesilemedi: " + ex.Message; }
    }
}

// =====================================================================
// Drives wgsslvpnc.exe's windows with plain Win32 messages. The login dialog (resource 129 in
// wgsslvpncENU.dll, a #32770 dialog) is found by its control ids, not by its title or language.
// Windows that this class opens are parked off-screen (never activated) and the previous
// foreground window keeps the focus; a window the user already had on screen is not moved.
// =====================================================================

internal static class WatchGuardGui
{
    // Login dialog controls (dialog 129).
    public const int IdServer = 1034, IdUsername = 1000, IdPassword = 1001, IdRemember = 1018, IdConnect = 1005, IdCancel = 2, IdSaml = 32785;
    // "Connecting" status dialog (dialog 206).
    public const int IdStatusOperation = 1007, IdStatusText = 1008;
    // Tray menu (menu 201): "Connect..." (shown as "Disconnect" while connected), "Exit".
    public const int CmdConnect = 32777, CmdExit = 32779;
    const int IdYes = 6, IdNo = 7, MessageBoxText = 0xFFFF;
    const string HiddenWindowTitle = "WatchGuard Mobile VPN Hidden Window!";
    const string TrayWindowClass = "TrayNotifyIconClass";

    const uint WM_SETTEXT = 0x000C, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, WM_COMMAND = 0x0111;
    const uint CB_GETCOUNT = 0x0146, CB_SETCURSEL = 0x014E, CB_FINDSTRINGEXACT = 0x0158;
    const int CBN_SELCHANGE = 1, CBN_EDITCHANGE = 5, BN_CLICKED = 0;
    const uint SMTO_ABORTIFHUNG = 0x2, Timeout = 3000;
    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    const int Off = -32000;

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr h, int id);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    static readonly object gate = new object();
    static readonly Dictionary<IntPtr, RECT> parked = new Dictionary<IntPtr, RECT>();

    // WM_COMMAND wParam: control id in the low word, notification code in the high word.
    internal static IntPtr MakeWParam(int id, int code) { return new IntPtr(((code & 0xFFFF) << 16) | (id & 0xFFFF)); }

    // ---- connect ----

    public static WatchGuardStore.ConnectOutcome Connect(string server, string user, string password, out string message)
    {
        message = null;
        var prev = GetForegroundWindow();
        bool userHadIt; string err;
        var dlg = EnsureLoginDialog(true, prev, out userHadIt, out err);
        if (dlg == IntPtr.Zero) { message = err; return WatchGuardStore.ConnectOutcome.Failed; }

        if (!SelectServer(dlg, server)) return ShowToUser(dlg, "WatchGuard penceresinde sunucu seçilemedi; sunucuyu seçip Connect'e basın.", out message);
        if (!string.IsNullOrEmpty(user) && !SetAndVerify(dlg, IdUsername, user))
            return ShowToUser(dlg, "WatchGuard penceresine kullanıcı adı yazılamadı; bilgileri girip Connect'e basın.", out message);
        var pw = GetDlgItem(dlg, IdPassword);
        if (!string.IsNullOrEmpty(password))
        {
            if (pw == IntPtr.Zero || !SetText(pw, password)) return ShowToUser(dlg, "WatchGuard penceresine parola yazılamadı; parolayı girip Connect'e basın.", out message);
        }
        else if (pw == IntPtr.Zero || TextLength(pw) <= 0)
            return ShowToUser(dlg, "WatchGuard parolası gerekli: açılan pencerede parolayı girip Connect'e basın.", out message);
        var connect = GetDlgItem(dlg, IdConnect);
        if (connect == IntPtr.Zero || !IsWindowEnabled(connect)) return ShowToUser(dlg, "WatchGuard'da Connect düğmesi kullanılamıyor.", out message);

        var before = OnScreenDialogs();
        if (!userHadIt) BabysitAfterPress(dlg, prev, before, 90);
        // WM_COMMAND/BN_CLICKED straight to the dialog: no mouse, no focus, works off-screen.
        if (!PostMessage(dlg, WM_COMMAND, MakeWParam(IdConnect, BN_CLICKED), connect)) { message = "WatchGuard'da Connect'e basılamadı."; return WatchGuardStore.ConnectOutcome.Failed; }
        return WatchGuardStore.ConnectOutcome.Pressed;
    }

    // Opens the login dialog on screen with the server chosen and the user name filled.
    public static string Open(string server, string user)
    {
        var prev = GetForegroundWindow();
        bool userHadIt; string err;
        var dlg = EnsureLoginDialog(false, prev, out userHadIt, out err);
        if (dlg == IntPtr.Zero) return err;
        SelectServer(dlg, server);
        if (!string.IsNullOrEmpty(user)) SetAndVerify(dlg, IdUsername, user);
        Reveal(dlg, true);
        return null;
    }

    static WatchGuardStore.ConnectOutcome ShowToUser(IntPtr dlg, string text, out string message)
    {
        Reveal(dlg, true);
        message = text;
        return WatchGuardStore.ConnectOutcome.ShownToUser;
    }

    // The client's login dialog, opened (and parked when quiet) if needed. Starts the client with
    // /noconnect when it is not running (it then only puts its icon in the tray).
    static IntPtr EnsureLoginDialog(bool quiet, IntPtr prev, out bool userHadIt, out string err)
    {
        userHadIt = false; err = null;
        var pids = ClientPids();
        if (pids.Count == 0)
        {
            var exe = WatchGuardStore.ClientPath;
            using (Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "/noconnect", WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true })) { }
            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until && FindHiddenWindow(pids = ClientPids()) == IntPtr.Zero) Thread.Sleep(150);
        }
        var dlg = FindLoginDialog(pids);
        if (dlg != IntPtr.Zero)
        {
            userHadIt = OnScreen(dlg);
            return dlg; // an open (or hidden) dialog: its controls work as they are
        }
        var hidden = FindHiddenWindow(pids);
        if (hidden == IntPtr.Zero) { err = "WatchGuard istemcisi başlamadı (gizli pencere bulunamadı)."; return IntPtr.Zero; }

        // Tray menu "Connect..." opens the dialog. The hidden main window handles the tray menu;
        // the tray helper window is tried second.
        var targets = new List<IntPtr> { hidden };
        var tray = FindTopWindow(pids, TrayWindowClass, null);
        if (tray != IntPtr.Zero) targets.Add(tray);
        foreach (var target in targets)
        {
            PostMessage(target, WM_COMMAND, MakeWParam(CmdConnect, 0), IntPtr.Zero);
            var until = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < until)
            {
                dlg = FindLoginDialog(pids);
                if (dlg != IntPtr.Zero && IsWindowVisible(dlg))
                {
                    if (quiet) { Park(dlg); RestoreFocus(prev); }
                    return dlg;
                }
                Thread.Sleep(20);
            }
            if (dlg != IntPtr.Zero) return dlg;
        }
        err = "WatchGuard giriş penceresi açılamadı.";
        return IntPtr.Zero;
    }

    // Server combo: an existing item ("host:port" or "host") is selected, otherwise the text is set.
    static bool SelectServer(IntPtr dlg, string server)
    {
        var combo = GetDlgItem(dlg, IdServer);
        if (combo == IntPtr.Zero) return false;
        if (WatchGuardStore.ServerMatches(GetText(combo), server)) return true;
        foreach (var candidate in new[] { server, WatchGuardStore.HostOf(server) })
        {
            IntPtr idx;
            if (SendMessageTimeout(combo, CB_FINDSTRINGEXACT, new IntPtr(-1), candidate, SMTO_ABORTIFHUNG, Timeout, out idx) == IntPtr.Zero) continue;
            if (idx.ToInt64() < 0) continue;
            IntPtr r;
            SendMessageTimeout(combo, CB_SETCURSEL, idx, IntPtr.Zero, SMTO_ABORTIFHUNG, Timeout, out r);
            SendMessageTimeout(dlg, WM_COMMAND, MakeWParam(IdServer, CBN_SELCHANGE), combo, SMTO_ABORTIFHUNG, Timeout, out r);
            if (WatchGuardStore.ServerMatches(GetText(combo), server)) return true;
        }
        SetText(combo, server);
        IntPtr rr;
        SendMessageTimeout(dlg, WM_COMMAND, MakeWParam(IdServer, CBN_EDITCHANGE), combo, SMTO_ABORTIFHUNG, Timeout, out rr);
        return WatchGuardStore.ServerMatches(GetText(combo), server);
    }

    static bool SetAndVerify(IntPtr dlg, int id, string value)
    {
        var h = GetDlgItem(dlg, id);
        if (h == IntPtr.Zero) return false;
        if (string.Equals(GetText(h).Trim(), value.Trim(), StringComparison.CurrentCultureIgnoreCase)) return true;
        SetText(h, value);
        return string.Equals(GetText(h).Trim(), value.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    // After Connect: keeps the client's "connecting" status window off-screen, and shows the user
    // anything that needs them (the login dialog coming back after a failure, an error box).
    static void BabysitAfterPress(IntPtr loginDlg, IntPtr prev, HashSet<IntPtr> before, int seconds)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                var end = DateTime.UtcNow.AddSeconds(seconds);
                var nextCheck = DateTime.UtcNow.AddSeconds(3);
                var loginGone = false;
                while (DateTime.UtcNow < end)
                {
                    var pids = ClientPids();
                    if (pids.Count == 0) break;
                    if (!IsWindow(loginDlg) || !IsWindowVisible(loginDlg)) loginGone = true;
                    var stop = false;
                    foreach (var h in TopDialogs(pids))
                    {
                        if (!IsWindowVisible(h) || before.Contains(h)) continue;
                        if (IsStatusDialog(h)) { if (OnScreen(h)) { Park(h); RestoreFocus(prev); } }
                        else if (IsLoginDialog(h)) { if (h != loginDlg || loginGone) { Reveal(h, true); stop = true; } }
                        else if (!IsParked(h)) { Reveal(h, true); stop = true; } // error / question box: the user decides
                    }
                    RestoreHiddenParked();
                    if (stop) break;
                    if (DateTime.UtcNow >= nextCheck)
                    {
                        if (WatchGuardStore.ConnectedServer() != null) break;
                        nextCheck = DateTime.UtcNow.AddSeconds(2);
                    }
                    Thread.Sleep(50);
                }
                RestoreHiddenParked();
            }
            catch { }
        });
    }

    // ---- disconnect ----

    public static string Disconnect()
    {
        var pids = ClientPids();
        if (pids.Count == 0 || WatchGuardStore.ConnectedServer() == null) return null;
        var hidden = FindHiddenWindow(pids);
        if (hidden == IntPtr.Zero) return "WatchGuard istemci penceresi bulunamadı.";
        var prev = GetForegroundWindow();
        var before = OnScreenDialogs();

        // 1) Tray "Disconnect" (the "Connect..." item while connected).
        PostMessage(hidden, WM_COMMAND, MakeWParam(CmdConnect, 0), IntPtr.Zero);
        if (WaitDisconnected(pids, before, prev, false, 12)) return null;
        // 2) Tray "Exit": the client closes its connection; its "active connections" question is answered Yes.
        PostMessage(hidden, WM_COMMAND, MakeWParam(CmdExit, 0), IntPtr.Zero);
        if (WaitDisconnected(pids, before, prev, true, 12)) return null;
        return "WatchGuard bağlantısı kesilemedi; WatchGuard simgesinden Disconnect'i seçin.";
    }

    static bool WaitDisconnected(HashSet<uint> pids, HashSet<IntPtr> before, IntPtr prev, bool confirmExit, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        var nextCheck = DateTime.UtcNow;
        while (DateTime.UtcNow < end)
        {
            foreach (var h in TopDialogs(pids))
            {
                if (!IsWindowVisible(h) || before.Contains(h)) continue;
                if (IsLoginDialog(h))
                {
                    // The command opened the login dialog instead of disconnecting: close it again.
                    Park(h); RestoreFocus(prev);
                    PostMessage(h, WM_COMMAND, MakeWParam(IdCancel, BN_CLICKED), GetDlgItem(h, IdCancel));
                }
                else if (IsStatusDialog(h)) { if (OnScreen(h)) { Park(h); RestoreFocus(prev); } }
                else if (confirmExit && IsExitQuestion(h))
                {
                    Park(h); RestoreFocus(prev);
                    PostMessage(h, WM_COMMAND, MakeWParam(IdYes, BN_CLICKED), GetDlgItem(h, IdYes));
                }
            }
            RestoreHiddenParked();
            if (DateTime.UtcNow >= nextCheck)
            {
                if (WatchGuardStore.ConnectedServer() == null) { RestoreHiddenParked(); return true; }
                nextCheck = DateTime.UtcNow.AddMilliseconds(700);
            }
            Thread.Sleep(50);
        }
        return false;
    }

    // ---- window lookup ----

    static HashSet<uint> ClientPids()
    {
        var ids = new HashSet<uint>();
        var procs = Process.GetProcessesByName(WatchGuardStore.ProcessName);
        try { foreach (var p in procs) ids.Add((uint)p.Id); }
        finally { foreach (var p in procs) p.Dispose(); }
        return ids;
    }

    static IntPtr FindHiddenWindow(HashSet<uint> pids) { return FindTopWindow(pids, null, HiddenWindowTitle); }

    static IntPtr FindTopWindow(HashSet<uint> pids, string cls, string title)
    {
        var found = IntPtr.Zero;
        if (pids.Count == 0) return found;
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid)) return true;
            if (cls != null && ClassOf(h) != cls) return true;
            if (title != null && TitleOf(h) != title) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    static List<IntPtr> TopDialogs(HashSet<uint> pids)
    {
        var list = new List<IntPtr>();
        if (pids.Count == 0) return list;
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pids.Contains(pid) && ClassOf(h) == "#32770") list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static IntPtr FindLoginDialog(HashSet<uint> pids)
    {
        foreach (var h in TopDialogs(pids)) if (IsLoginDialog(h)) return h;
        return IntPtr.Zero;
    }

    static bool IsLoginDialog(IntPtr h)
    {
        return GetDlgItem(h, IdConnect) != IntPtr.Zero && GetDlgItem(h, IdServer) != IntPtr.Zero && GetDlgItem(h, IdPassword) != IntPtr.Zero;
    }

    static bool IsStatusDialog(IntPtr h)
    {
        return GetDlgItem(h, IdStatusOperation) != IntPtr.Zero && GetDlgItem(h, IdStatusText) != IntPtr.Zero && GetDlgItem(h, IdConnect) == IntPtr.Zero;
    }

    // The client's "There are active connections that will be close if you exit" Yes/No box.
    static bool IsExitQuestion(IntPtr h)
    {
        if (GetDlgItem(h, IdYes) == IntPtr.Zero || GetDlgItem(h, IdNo) == IntPtr.Zero) return false;
        var t = GetText(GetDlgItem(h, MessageBoxText)).ToLowerInvariant();
        return t.IndexOf("exit", StringComparison.Ordinal) >= 0 || t.IndexOf("quitter", StringComparison.Ordinal) >= 0;
    }

    static HashSet<IntPtr> OnScreenDialogs()
    {
        var set = new HashSet<IntPtr>();
        foreach (var h in TopDialogs(ClientPids())) if (OnScreen(h)) set.Add(h);
        return set;
    }

    static bool OnScreen(IntPtr h)
    {
        RECT r;
        return IsWindowVisible(h) && !IsIconic(h) && GetWindowRect(h, out r) && r.Left > Off + 1000;
    }

    static string ClassOf(IntPtr h) { var s = new StringBuilder(64); GetClassName(h, s, 64); return s.ToString(); }
    static string TitleOf(IntPtr h) { var s = new StringBuilder(128); GetWindowText(h, s, 128); return s.ToString(); }

    // WM_GETTEXT through SendMessageTimeout (works across processes; never used on the password box).
    static string GetText(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        var sb = new StringBuilder(512);
        IntPtr r;
        if (SendMessageTimeout(h, WM_GETTEXT, new IntPtr(sb.Capacity), sb, SMTO_ABORTIFHUNG, Timeout, out r) == IntPtr.Zero) return "";
        return sb.ToString();
    }

    static int TextLength(IntPtr h)
    {
        IntPtr r;
        if (SendMessageTimeout(h, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, Timeout, out r) == IntPtr.Zero) return -1;
        return (int)r.ToInt64();
    }

    static bool SetText(IntPtr h, string value)
    {
        IntPtr r;
        return SendMessageTimeout(h, WM_SETTEXT, IntPtr.Zero, value ?? "", SMTO_ABORTIFHUNG, Timeout, out r) != IntPtr.Zero && r != IntPtr.Zero;
    }

    // ---- parking ----

    static bool IsParked(IntPtr h) { lock (gate) return parked.ContainsKey(h); }

    static void Park(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) return;
        lock (gate)
        {
            if (parked.ContainsKey(h)) { if (r.Left > Off + 1000) Move(h, Off, Off); return; }
            if (r.Left <= Off + 1000) return;
            parked[h] = r;
        }
        Move(h, Off, Off);
    }

    // Moves a parked window back (or centres a stray off-screen one); activate brings it to the front.
    static void Reveal(IntPtr h, bool activate)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return;
        RECT home; bool known;
        lock (gate) { known = parked.TryGetValue(h, out home); parked.Remove(h); }
        RECT now;
        GetWindowRect(h, out now);
        if (!known || home.Left <= Off + 1000)
        {
            if (now.Left > Off + 1000) home = now;
            else
            {
                var a = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                var w = now.Right - now.Left; var ht = now.Bottom - now.Top;
                home = new RECT { Left = a.Left + Math.Max(0, (a.Width - w) / 2), Top = a.Top + Math.Max(0, (a.Height - ht) / 2) };
            }
        }
        Move(h, home.Left, home.Top);
        if (activate)
        {
            if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
            SetForegroundWindow(h);
        }
    }

    // A parked window the client has hidden again goes back home while hidden, so it does not
    // reappear off-screen when the user opens it from the tray later.
    static void RestoreHiddenParked()
    {
        var list = new List<KeyValuePair<IntPtr, RECT>>();
        lock (gate) foreach (var kv in parked) list.Add(kv);
        foreach (var kv in list)
        {
            if (!IsWindow(kv.Key)) { lock (gate) parked.Remove(kv.Key); continue; }
            if (IsWindowVisible(kv.Key)) continue;
            Move(kv.Key, kv.Value.Left, kv.Value.Top);
            lock (gate) parked.Remove(kv.Key);
        }
    }

    static bool Move(IntPtr h, int x, int y) { return SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); }

    static void RestoreFocus(IntPtr prev)
    {
        try { if (prev != IntPtr.Zero && IsWindow(prev) && GetForegroundWindow() != prev) SetForegroundWindow(prev); } catch { }
    }
}
