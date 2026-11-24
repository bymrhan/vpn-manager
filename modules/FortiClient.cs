using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// =====================================================================
// FortiClient VPN (7.x) integration.
// - Profiles: SSL VPN tunnels  HKLM\SOFTWARE\Fortinet\FortiClient\Sslvpn\Tunnels\<name> (Server = "host:port")
//             IPsec tunnels    HKLM\SOFTWARE\Fortinet\FortiClient\IPSec\Tunnels\<name>  (no gateway in registry)
//             plus per-user    HKCU\Software\Fortinet\FortiClient\Sslvpn\Tunnels\<name>
// - FortiClient 7.4 has no command-line connect (FortiVPN.exe only offers -h / -s scheduler),
//   so "connect" selects the tunnel (HKCU\...\FA_VPN\connection, the value FortiClient itself uses
//   for the last selected connection) and brings the FortiClient window up; the user presses Connect.
// - Status: the Fortinet virtual adapter being Up means a tunnel is connected; the selected
//   connection name tells which one.
// Only names and gateway addresses are read. Encrypted DATA* values and descriptions are never read.
// =====================================================================

internal static class VpnAdapters
{
    // Windows lists every filter driver bound to a NIC as its own pseudo-interface, e.g.
    // "Intel(R) Ethernet ...-Fortinet NDIS 6.0 LightWeight Filter-0000". They are not adapters.
    public static bool IsFilterLayer(string description)
    {
        if (string.IsNullOrEmpty(description)) return false;
        return description.IndexOf("LightWeight Filter", StringComparison.OrdinalIgnoreCase) >= 0
            || description.IndexOf("Packet Scheduler", StringComparison.OrdinalIgnoreCase) >= 0
            || description.IndexOf("Packet Driver", StringComparison.OrdinalIgnoreCase) >= 0
            || description.EndsWith("-0000", StringComparison.Ordinal);
    }
}

internal static class FortiStore
{
    public const string IdPrefixSsl = "forti-ssl:", IdPrefixIpsec = "forti-ipsec:";
    const string SslKey = @"SOFTWARE\Fortinet\FortiClient\Sslvpn\Tunnels", IpsecKey = @"SOFTWARE\Fortinet\FortiClient\IPSec\Tunnels";
    const string SelectionKey = @"Software\Fortinet\FortiClient\FA_VPN";

    public static bool IsForti(VpnProfile p) { return p != null && p.Id.StartsWith("forti-", StringComparison.Ordinal); }
    public static bool IsInstalled { get { return File.Exists(GuiPath); } }

    public static string GuiPath
    {
        get
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles") })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var p = Path.Combine(root, "Fortinet", "FortiClient", "FortiClient.exe");
                if (File.Exists(p)) return p;
            }
            return "";
        }
    }

    public static List<VpnProfile> Load()
    {
        var result = new List<VpnProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            AddTunnels(result, seen, Registry.LocalMachine, SslKey, IdPrefixSsl, "FortiClient SSL");
            AddTunnels(result, seen, Registry.CurrentUser, SslKey.Replace("SOFTWARE", "Software"), IdPrefixSsl, "FortiClient SSL");
            AddTunnels(result, seen, Registry.LocalMachine, IpsecKey, IdPrefixIpsec, "FortiClient IPsec");
        }
        catch { }
        return result;
    }

    static void AddTunnels(List<VpnProfile> list, HashSet<string> seen, RegistryKey hive, string path, string prefix, string source)
    {
        using (var key = OpenRead(hive, path))
        {
            if (key == null) return;
            foreach (var name in key.GetSubKeyNames())
            {
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(prefix + name)) continue;
                string server = "";
                using (var t = key.OpenSubKey(name)) if (t != null) server = Convert.ToString(t.GetValue("Server", "")).Trim();
                var p = new VpnProfile
                {
                    Id = prefix + name, Name = name, Host = server, Username = "", Source = source, LastConnected = 0
                };
                p.Initials = ProfileStore.MakeInitials(name);
                p.Keys = new[] { ProfileStore.SearchKey(p.Name), ProfileStore.SearchKey(p.Host), "FORTICLIENT FORTI", ProfileStore.SearchKey(source) };
                list.Add(p);
            }
        }
    }

    // 64-bit registry view: FortiClient is a 64-bit app and this tool may run as AnyCPU/32-bit.
    static RegistryKey OpenRead(RegistryKey hive, string path)
    {
        try
        {
            var baseKey = RegistryKey.OpenBaseKey(hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
            return baseKey.OpenSubKey(path);
        }
        catch { return null; }
    }

    public static string TunnelName(VpnProfile p)
    {
        if (p.Id.StartsWith(IdPrefixSsl, StringComparison.Ordinal)) return p.Id.Substring(IdPrefixSsl.Length);
        if (p.Id.StartsWith(IdPrefixIpsec, StringComparison.Ordinal)) return p.Id.Substring(IdPrefixIpsec.Length);
        return p.Name;
    }

    public static string SelectedTunnel()
    {
        try
        {
            using (var k = OpenRead(Registry.CurrentUser, SelectionKey))
                return k == null ? null : Convert.ToString(k.GetValue("connection", null));
        }
        catch { return null; }
    }

    // Selects the tunnel in FortiClient and brings its window to the front. Returns an error or null.
    public static string Open(VpnProfile p)
    {
        if (!IsInstalled) return "FortiClient bulunamadı.";
        var target = TunnelName(p);
        var current = SelectedTunnel();
        var sameTunnel = string.Equals(current, target, StringComparison.CurrentCultureIgnoreCase);
        var pids = GuiProcessIds();

        // FortiClient's GUI reads the selected connection only at start-up and does not expose its
        // controls to UI Automation, so switching tunnels while it is open means restarting the GUI.
        // Never do that while a FortiClient tunnel is up (the user must disconnect first).
        if (pids.Count > 0 && !sameTunnel && ConnectedTunnel() != null)
            return "FortiClient'ta şu an bir bağlantı açık. Başka tünele geçmek için önce FortiClient'ta bağlantıyı kesin.";

        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(SelectionKey)) k.SetValue("connection", target, RegistryValueKind.String);
        }
        catch (Exception ex) { return "FortiClient bağlantısı seçilemedi: " + ex.Message; }
        try
        {
            if (pids.Count > 0)
            {
                // Same tunnel already selected: just show the (possibly hidden) window.
                if (sameTunnel)
                {
                    if (!ShowExistingWindow(pids)) return "FortiClient zaten çalışıyor; penceresi bulunamadı. Bildirim alanındaki FortiClient simgesinden açın.";
                    return null;
                }
                // Different tunnel: close only the GUI (VPN services and the tray icon are separate
                // processes) and start it again so it picks up the new selection.
                if (!StopGui(pids)) return "FortiClient penceresi kapatılamadı; FortiClient'ı kapatıp tekrar deneyin.";
            }
            // Same as the Start menu shortcut: the GUI must start in its own folder, otherwise it
            // cannot find its configuration ("Cannot read properties of null (reading 'TraceLog')").
            using (Process.Start(new ProcessStartInfo { FileName = GuiPath, WorkingDirectory = Path.GetDirectoryName(GuiPath), UseShellExecute = true })) { }
            return null;
        }
        catch (Exception ex) { return "FortiClient açılamadı: " + ex.Message; }
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

    static HashSet<uint> GuiProcessIds()
    {
        var ids = new HashSet<uint>();
        var procs = Process.GetProcessesByName("FortiClient");
        try { foreach (var pr in procs) ids.Add((uint)pr.Id); }
        finally { foreach (var pr in procs) pr.Dispose(); }
        return ids;
    }

    // Ends the FortiClient GUI processes (FortiClient.exe only) and waits until they are gone.
    static bool StopGui(HashSet<uint> pids)
    {
        foreach (var id in pids)
        {
            try { using (var pr = Process.GetProcessById((int)id)) { if (!pr.HasExited) pr.Kill(); } }
            catch { } // already gone
        }
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            var left = Process.GetProcessesByName("FortiClient");
            var count = left.Length; foreach (var pr in left) pr.Dispose();
            if (count == 0) { System.Threading.Thread.Sleep(300); return true; } // let it release its single-instance lock
            System.Threading.Thread.Sleep(150);
        }
        return false;
    }

    // Finds FortiClient's top-level window (also when hidden to the tray) and shows it.
    static bool ShowExistingWindow(HashSet<uint> pids)
    {
        var found = FindMainWindow(pids);
        if (found == IntPtr.Zero) return false;
        ShowWindow(found, IsIconic(found) ? 9 : 5); // SW_RESTORE / SW_SHOW
        SetForegroundWindow(found);
        return true;
    }

    // FortiClient's main Chromium window, or IntPtr.Zero.
    internal static IntPtr FindMainWindow() { return FindMainWindow(GuiProcessIds()); }

    static IntPtr FindMainWindow(HashSet<uint> pids)
    {
        var found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid) || GetWindow(h, 4) != IntPtr.Zero) return true; // skip owned windows (GW_OWNER)
            var cls = new System.Text.StringBuilder(64); GetClassName(h, cls, 64);
            var title = new System.Text.StringBuilder(128); GetWindowText(h, title, 128);
            if (cls.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) && title.ToString().IndexOf("FortiClient", StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // Name of the connected FortiClient tunnel, or null. Costs ~50-100 ms (adapter enumeration):
    // callers poll it sparingly.
    public static string ConnectedTunnel()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var d = ni.Description ?? "";
                // Only FortiClient's own virtual adapters. FortiClient also adds a "Fortinet NDIS 6.0
                // LightWeight Filter" layer to every NIC (Wi-Fi, Ethernet); those are Up whenever the
                // PC is online and must not count as a VPN connection.
                if (ni.OperationalStatus == OperationalStatus.Up && !VpnAdapters.IsFilterLayer(d)
                    && (d.StartsWith("Fortinet SSL VPN Virtual Ethernet Adapter", StringComparison.OrdinalIgnoreCase)
                        || d.StartsWith("Fortinet Virtual Ethernet Adapter", StringComparison.OrdinalIgnoreCase)))
                    return SelectedTunnel() ?? "";
            }
        }
        catch { }
        return null;
    }
}

// =====================================================================
// Presses Connect in the FortiClient window for SSL VPN tunnels.
// FortiClient 7.4 has no command-line or API connect for SSL VPN. Its Chromium window exposes
// its form through MSAA (FortiClient calls setAccessibilitySupportEnabled itself), so after
// FortiStore.Open() has selected the tunnel, this finds the "VPN Name" combo box, checks that it
// shows the wanted tunnel and that the password is saved, and presses Connect
// (accDoDefaultAction: no mouse, no focus change). The password is never read or typed: when it
// is not saved, the user types it in FortiClient and presses Connect.
// Verified on FortiClient 7.4.3 (English UI): combo value "Personal VPNs <tunnel>",
// check box "Save Password", push button "Connect".
// =====================================================================

internal static class FortiGui
{
    public enum Outcome { Pressed, Ready, NotReady, OtherTunnel, NeedsPassword, FillFailed }

    const int RoleCheckButton = 44, RoleComboBox = 46, RolePushButton = 43, RoleText = 42;
    const int StateUnavailable = 0x1, StateChecked = 0x10;
    static readonly string[] ConnectNames = { "Connect", "Bağlan" };
    static readonly string[] SavePasswordNames = { "Save Password", "Parolayı Kaydet", "Şifreyi Kaydet" };
    static readonly string[] UsernameNames = { "Username", "Kullanıcı Adı", "Kullanıcı adı" };
    static readonly string[] PasswordNames = { "Password", "Parola", "Şifre" };

    [DllImport("oleacc.dll")]
    static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible acc);
    [DllImport("oleacc.dll")]
    static extern int AccessibleChildren(Accessibility.IAccessible parent, int start, int count, [Out] object[] children, out int obtained);

    sealed class Form { public Accessibility.IAccessible Combo, SavePassword, Connect, Username, Password; }

    // One attempt. With press=false nothing is changed (Ready means Connect would be pressed).
    public static Outcome TryConnect(string tunnel, bool press, out string detail) { return TryConnect(tunnel, null, press, out detail); }

    // With cred: when FortiClient does not keep the password, fills the username (if given) and
    // the password into the form, then presses Connect.
    public static Outcome TryConnect(string tunnel, FortiCredential cred, bool press, out string detail)
    {
        detail = "";
        var hwnd = FortiStore.FindMainWindow();
        if (hwnd == IntPtr.Zero) { detail = "FortiClient penceresi yok"; return Outcome.NotReady; }
        Accessibility.IAccessible root;
        var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71"); // IID_IAccessible
        if (AccessibleObjectFromWindow(hwnd, 0xFFFFFFFC, ref iid, out root) != 0 || root == null) { detail = "erişilebilirlik nesnesi alınamadı"; return Outcome.NotReady; }

        var form = new Form();
        var visited = 0;
        Walk(root, 0, form, ref visited);
        if (form.Combo == null || form.Connect == null) { detail = "form henüz yüklenmedi (" + visited + " nesne)"; return Outcome.NotReady; }

        var shown = Value(form.Combo);
        detail = "seçili: " + shown;
        if (!ShowsTunnel(shown, tunnel)) return Outcome.OtherTunnel;
        if ((State(form.Connect) & StateUnavailable) != 0) { detail += ", Connect devre dışı"; return Outcome.NotReady; }
        var fortiKeeps = form.SavePassword != null && (State(form.SavePassword) & StateChecked) != 0;
        if (!fortiKeeps)
        {
            if (cred == null || string.IsNullOrEmpty(cred.Password) || form.Password == null) return Outcome.NeedsPassword;
            if (!press) return Outcome.Ready;
            try
            {
                if (!string.IsNullOrEmpty(cred.Username) && form.Username != null && Value(form.Username) != cred.Username)
                {
                    form.Username.set_accValue(0, cred.Username);
                    if (Value(form.Username) != cred.Username) { detail += ", kullanıcı adı yazılamadı"; return Outcome.FillFailed; }
                }
                form.Password.set_accValue(0, cred.Password);
            }
            catch (Exception ex) { detail += ", alan doldurulamadı: " + ex.GetType().Name; return Outcome.FillFailed; }
            System.Threading.Thread.Sleep(150); // let the page see the input events before Connect
            form.Connect.accDoDefaultAction(0);
            return Outcome.Pressed;
        }
        if (!press) return Outcome.Ready;
        form.Connect.accDoDefaultAction(0);
        return Outcome.Pressed;
    }

    // The combo shows "<group> <tunnel>", e.g. "Personal VPNs SANBOR-YENİ".
    internal static bool ShowsTunnel(string shown, string tunnel)
    {
        if (string.IsNullOrEmpty(shown) || string.IsNullOrEmpty(tunnel)) return false;
        shown = shown.Trim(); tunnel = tunnel.Trim();
        if (string.Equals(shown, tunnel, StringComparison.CurrentCultureIgnoreCase)) return true;
        return shown.Length > tunnel.Length
            && shown.EndsWith(tunnel, StringComparison.CurrentCultureIgnoreCase)
            && shown[shown.Length - tunnel.Length - 1] == ' ';
    }

    static void Walk(Accessibility.IAccessible node, int depth, Form form, ref int visited)
    {
        if (depth > 30 || visited > 2000) return;
        int count;
        try { count = node.accChildCount; } catch { return; }
        if (count <= 0) return;
        var kids = new object[count];
        int got;
        if (AccessibleChildren(node, 0, count, kids, out got) != 0) return;
        for (var i = 0; i < got; i++)
        {
            var kid = kids[i] as Accessibility.IAccessible;
            if (kid == null) continue;
            visited++;
            var role = Role(kid);
            if (role == RoleComboBox && form.Combo == null && NameOf(kid) == "VPN Name") form.Combo = kid;
            else if (role == RolePushButton && form.Connect == null && Array.IndexOf(ConnectNames, NameOf(kid)) >= 0) form.Connect = kid;
            else if (role == RoleCheckButton && form.SavePassword == null && Array.IndexOf(SavePasswordNames, NameOf(kid)) >= 0) form.SavePassword = kid;
            else if (role == RoleText && form.Username == null && Array.IndexOf(UsernameNames, NameOf(kid)) >= 0) form.Username = kid;
            else if (role == RoleText && form.Password == null && Array.IndexOf(PasswordNames, NameOf(kid)) >= 0) form.Password = kid;
            Walk(kid, depth + 1, form, ref visited);
        }
    }

    static int Role(Accessibility.IAccessible a) { try { return Convert.ToInt32(a.get_accRole(0)); } catch { return 0; } }
    static int State(Accessibility.IAccessible a) { try { return Convert.ToInt32(a.get_accState(0)); } catch { return 0; } }
    static string NameOf(Accessibility.IAccessible a) { try { return (a.get_accName(0) ?? "").Trim(); } catch { return ""; } }
    static string Value(Accessibility.IAccessible a) { try { return a.get_accValue(0) ?? ""; } catch { return ""; } }
}
