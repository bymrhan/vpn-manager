using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// =====================================================================
// Connection state: is an OpenVPN tunnel up right now, and which profile
// =====================================================================

internal sealed class ConnectionState
{
    public bool Connected;
    public string ProfileId, ProfileName;
    public DateTime SinceUtc;
}

// Polls cheaply: the VPN adapter status and the last EVENT in the newest
// OpenVPN Connect log. config.json and the log tail are re-read only when
// their size or write time changes.
internal sealed class ConnectionMonitor : IDisposable
{
    const int TailBytes = 64 * 1024;
    static readonly Regex EventRx = new Regex(@"EVENT: ([A-Z_]+)", RegexOptions.CultureInvariant);
    static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static readonly TimeSpan AdapterInterval = TimeSpan.FromSeconds(10);

    readonly Func<IList<VpnProfile>> getProfiles;
    readonly SynchronizationContext ui;
    ConnectionState current = new ConnectionState();
    bool disposed;

    // Cached observations.
    bool adapterUp; DateTime adapterCheckedUtc = DateTime.MinValue;
    bool logChanged;
    string logPath = ""; long logSize = -1; DateTime logTime = DateTime.MinValue; string lastEvent = "";
    long cfgSize = -1; DateTime cfgTime = DateTime.MinValue;
    string cfgProfileId = "", cfgProfileName = ""; long cfgConnectedAt;

    public event EventHandler Changed;

    public ConnectionMonitor(Func<IList<VpnProfile>> getProfiles)
    {
        this.getProfiles = getProfiles;
        ui = SynchronizationContext.Current; // Changed is raised on this thread
    }

    public ConnectionState Current { get { return current; } }

    public void Poll()
    {
        if (disposed) return;
        // A broken log or config must never break the search panel.
        try { PollCore(); } catch { }
    }

    void PollCore()
    {
        var ev = LastLogEvent();
        var stopped = ev == "DISCONNECTED" || ev == "PAUSE" || ev == "EXITING";
        // Adapter enumeration costs ~100 ms (many filter adapters), so it runs only when the log
        // changed or every 10 s; a stopped log event needs no adapter check at all.
        var now = DateTime.UtcNow;
        // OpenVPN Connect rewrites config.json on every connect/disconnect; it must name a connected
        // profile too. This makes a disconnect show up immediately instead of after the next
        // adapter check, and stops a stale or unrelated adapter from looking like a live session.
        var cfgChanged = RefreshConfig();
        var waitingForAdapter = cfgProfileId != "" && !adapterUp && now - adapterCheckedUtc >= TimeSpan.FromSeconds(2);
        if (!stopped && (logChanged || cfgChanged || waitingForAdapter || now - adapterCheckedUtc >= AdapterInterval))
        {
            adapterUp = VpnAdapterUp();
            adapterCheckedUtc = now;
        }
        var connected = !stopped && adapterUp && cfgProfileId != "";

        var prev = current;
        var id = ""; var name = ""; long connectedAt = 0;
        if (connected) { id = cfgProfileId; name = cfgProfileName; connectedAt = cfgConnectedAt; }

        var next = new ConnectionState { Connected = connected, ProfileId = id, ProfileName = name, SinceUtc = DateTime.MinValue };
        if (connected) next.SinceUtc = prev.Connected && prev.ProfileId == id ? prev.SinceUtc : SessionStart(connectedAt, now);

        if (prev.Connected && (!connected || prev.ProfileId != id)) AppendHistory(prev, now);

        var changed = next.Connected != prev.Connected || next.ProfileId != prev.ProfileId
            || next.ProfileName != prev.ProfileName || next.SinceUtc != prev.SinceUtc;
        current = next;
        if (changed) Raise();
    }

    static DateTime SessionStart(long connectedAtMs, DateTime now)
    {
        if (connectedAtMs > 0)
        {
            try
            {
                var t = Epoch.AddMilliseconds(connectedAtMs);
                if (t <= now.AddMinutes(1)) return t;
            }
            catch { }
        }
        return now;
    }

    void AppendHistory(ConnectionState session, DateTime endUtc)
    {
        if (session.SinceUtc == DateTime.MinValue) return;
        try
        {
            ConnectionHistory.Append(new HistoryEntry
            {
                ProfileId = session.ProfileId ?? "", ProfileName = session.ProfileName ?? "",
                StartUtc = session.SinceUtc, EndUtc = endUtc
            });
        }
        catch { }
    }

    void Raise()
    {
        if (ui == null) { FireChanged(); return; }
        ui.Post(delegate(object s) { if (!disposed) FireChanged(); }, null);
    }

    void FireChanged()
    {
        var h = Changed;
        if (h != null) h(this, EventArgs.Empty);
    }

    // Any enabled adapter whose description names OpenVPN, the DCO driver, Wintun or the TAP-Windows driver.
    static bool VpnAdapterUp()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var d = ni.Description ?? "";
            if (VpnAdapters.IsFilterLayer(d)) continue; // "<adapter>-WFP ... Filter-0000" etc. are not adapters
            if (d.IndexOf("OpenVPN", StringComparison.OrdinalIgnoreCase) >= 0
                || d.IndexOf("Data Channel Offload", StringComparison.OrdinalIgnoreCase) >= 0
                || d.IndexOf("Wintun", StringComparison.OrdinalIgnoreCase) >= 0
                || d.IndexOf("TAP-Windows", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    internal static string LogDirectoryOverride; // tests only

    static string LogDirectory
    {
        get { return LogDirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenVPN Connect", "log"); }
    }

    // Last "EVENT: X" marker in the newest *.log, read from the file tail only when it changed.
    string LastLogEvent()
    {
        logChanged = false;
        var dir = LogDirectory;
        if (!Directory.Exists(dir)) return "";
        FileInfo newest = null;
        foreach (var f in new DirectoryInfo(dir).GetFiles("*.log"))
        {
            // OpenVPN Connect keeps ovpn.log open for the whole session. NTFS does not update the
            // directory entry of an open file, so the size and write time from the enumeration stay
            // stale and new events would go unseen. Refresh reads them from the file itself.
            f.Refresh();
            if (newest == null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;
        }
        if (newest == null) return "";
        if (newest.FullName == logPath && newest.Length == logSize && newest.LastWriteTimeUtc == logTime) return lastEvent;
        logChanged = true;

        string text;
        using (var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var start = Math.Max(0L, fs.Length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            var read = 0;
            while (read < buf.Length)
            {
                var n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            text = Encoding.UTF8.GetString(buf, 0, read);
        }
        var matches = EventRx.Matches(text);
        lastEvent = matches.Count > 0 ? matches[matches.Count - 1].Groups[1].Value : "";
        logPath = newest.FullName; logSize = newest.Length; logTime = newest.LastWriteTimeUtc;
        return lastEvent;
    }

    // Reads status.connectedProfile (falls back to lastAction.payload when it says CONNECTED)
    // from config.json, only when the file changed.
    bool RefreshConfig()
    {
        var path = ProfileStore.ConfigPath;
        if (!File.Exists(path)) { var had = cfgProfileId != ""; cfgProfileId = ""; return had; }
        var fi = new FileInfo(path);
        if (fi.Length == cfgSize && fi.LastWriteTimeUtc == cfgTime) return false;

        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();

        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        var root = Obj(js, js.DeserializeObject(text));
        var pr = Obj(js, Get(root, "persist:root"));
        var st = Obj(js, Get(pr, "status"));

        var id = ""; var dname = "";
        var cp = Obj(js, Get(st, "connectedProfile"));
        if (cp != null)
        {
            id = Text(cp, "id");
            dname = Text(cp, "profileDisplayName");
            if (dname == "") dname = Text(cp, "profileName");
        }
        if (id == "")
        {
            var la = Obj(js, Get(pr, "lastAction"));
            var pl = Obj(js, Get(la, "payload"));
            if (pl != null && Text(pl, "status") == "CONNECTED") id = Text(pl, "profileId");
        }

        var name = "";
        if (id != "")
        {
            try
            {
                var list = getProfiles == null ? null : getProfiles();
                if (list != null)
                    foreach (var p in list) if (p.Id == id) { name = p.Name; break; }
            }
            catch { }
        }
        if (name == "") name = dname;

        cfgProfileId = id; cfgProfileName = name;
        cfgConnectedAt = Long(st, "connectedAt");
        cfgSize = fi.Length; cfgTime = fi.LastWriteTimeUtc;
        return true;
    }

    static object Get(IDictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
    static IDictionary<string, object> Obj(JavaScriptSerializer js, object raw)
    {
        var s = raw as string;
        if (s != null) raw = js.DeserializeObject(s);
        return raw as IDictionary<string, object>;
    }
    static string Text(IDictionary<string, object> d, string k) { var v = Get(d, k); return v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
    static long Long(IDictionary<string, object> d, string k) { long n; return long.TryParse(Text(d, k), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Changed = null;
    }
}

// =====================================================================
// Disconnect command
// =====================================================================

// Verified in resources\app.asar (OpenVPN Connect 3.9.x): flags.DISCONNECT = "disconnect-shortcut",
// parseCommandLineArgs treats "--disconnect-shortcut" as autoDisconnect (hasSwitch). The running
// instance receives the second launch's argv. Not runtime-tested: state changes are not performed here.
internal static class ConnectionControl
{
    public static bool Disconnect(out string error)
    {
        error = null;
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN Connect", "OpenVPNConnect.exe");
        if (!File.Exists(exe)) { error = "OpenVPN Connect uygulaması bulunamadı."; return false; }
        if (Process.GetProcessesByName("OpenVPNConnect").Length == 0)
        {
            error = "OpenVPN Connect çalışmıyor; bağlantı zaten kapalı olabilir.";
            return false;
        }
        try
        {
            var psi = new ProcessStartInfo(exe, "--disconnect-shortcut") { UseShellExecute = false, CreateNoWindow = true };
            var p = Process.Start(psi);
            if (p != null) p.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            error = "Bağlantı kesme komutu gönderilemedi: " + ex.Message;
            return false;
        }
    }
}

// =====================================================================
// History
// =====================================================================

internal sealed class HistoryEntry
{
    public string ProfileId, ProfileName;
    public DateTime StartUtc, EndUtc;
}

// Store: %APPDATA%\OpenVpnQuickSearch\history.csv (UTF-8 with BOM, ';', ISO UTC times).
internal static class ConnectionHistory
{
    internal static string FilePathOverride; // tests only

    static readonly object gate = new object();
    static readonly UTF8Encoding Utf8Bom = new UTF8Encoding(true);
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    const string StoreHeader = "ProfileId;ProfileName;StartUtc;EndUtc";

    public static string FilePath
    {
        get
        {
            return FilePathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenVpnQuickSearch", "history.csv");
        }
    }

    public static void Append(HistoryEntry e)
    {
        var line = Join(e.ProfileId, e.ProfileName,
            e.StartUtc.ToString("o", CultureInfo.InvariantCulture), e.EndUtc.ToString("o", CultureInfo.InvariantCulture));
        lock (gate)
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path)) File.WriteAllText(path, StoreHeader + "\r\n", Utf8Bom);
            File.AppendAllText(path, line + "\r\n", Utf8);
        }
    }

    public static List<HistoryEntry> Load()
    {
        var list = new List<HistoryEntry>();
        var path = FilePath;
        if (!File.Exists(path)) return list;
        string[] lines;
        lock (gate) lines = File.ReadAllLines(path, Encoding.UTF8);
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0 || line.StartsWith("ProfileId;", StringComparison.Ordinal)) continue;
            var f = SplitCsv(line);
            if (f.Count < 4) continue;
            DateTime s, en;
            if (!DateTime.TryParse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out s)) continue;
            if (!DateTime.TryParse(f[3], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out en)) continue;
            list.Add(new HistoryEntry { ProfileId = f[0], ProfileName = f[1], StartUtc = s, EndUtc = en });
        }
        return list;
    }

    // Newest first, local time, Turkish headers, minutes as whole numbers (no decimal separator issues in Excel).
    public static void ExportCsv(string path)
    {
        var list = Load();
        list.Sort(delegate(HistoryEntry a, HistoryEntry b) { return b.StartUtc.CompareTo(a.StartUtc); });
        var sb = new StringBuilder();
        sb.Append("Profil;Başlangıç;Bitiş;Süre (dk)\r\n");
        foreach (var e in list)
        {
            var minutes = Math.Max(0, (int)Math.Floor((e.EndUtc - e.StartUtc).TotalMinutes));
            sb.Append(Join(e.ProfileName,
                e.StartUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                e.EndUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                minutes.ToString(CultureInfo.InvariantCulture)));
            sb.Append("\r\n");
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString(), Utf8Bom);
    }

    static string Join(params string[] values)
    {
        var parts = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i] ?? "";
            parts[i] = v.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
        return string.Join(";", parts);
    }

    static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ';') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }
}

// =====================================================================
// History window
// =====================================================================

internal sealed class HistoryForm : Form
{
    sealed class Total { public string Name; public int Count; public TimeSpan Span; }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

    readonly List<HistoryEntry> entries;
    readonly List<Total> totals = new List<Total>();
    bool showTotals;
    readonly Panel totalsPanel = new Panel();
    readonly ListView list = new ListView();
    readonly ImageList rowHeight = new ImageList();
    readonly Label empty = new Label();
    readonly Button exportButton = new Button();
    readonly Button closeButton = new Button();

    public HistoryForm()
    {
        entries = ConnectionHistory.Load();
        entries.Sort(delegate(HistoryEntry a, HistoryEntry b) { return b.StartUtc.CompareTo(a.StartUtc); });
        BuildTotals();

        Text = "Bağlantı geçmişi";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        BackColor = Ui.Bg; ForeColor = Ui.Text; Font = Ui.SubFont;
        ClientSize = new Size(Ui.S(560), Ui.S(540));
        KeyPreview = true;

        rowHeight.ImageSize = new Size(1, Ui.S(28));
        // Layout reads these flags, not Visible: Visible reports false until the form is shown.
        showTotals = totals.Count > 0;
        totalsPanel.BackColor = Ui.Bg; totalsPanel.Paint += PaintTotals;
        totalsPanel.Visible = showTotals;
        Controls.Add(totalsPanel);

        list.View = View.Details; list.OwnerDraw = true; list.FullRowSelect = true; list.MultiSelect = false;
        list.HideSelection = false; list.HeaderStyle = ColumnHeaderStyle.Nonclickable; list.BorderStyle = BorderStyle.None;
        list.BackColor = Ui.Surface; list.ForeColor = Ui.Text; list.Font = Ui.SubFont; list.SmallImageList = rowHeight;
        list.Columns.Add("Profil"); list.Columns.Add("Başlangıç"); list.Columns.Add("Süre");
        list.DrawColumnHeader += DrawHeader; list.DrawItem += DrawRow; list.DrawSubItem += DrawCell;
        foreach (var e in entries)
        {
            var item = new ListViewItem(e.ProfileName ?? "");
            item.SubItems.Add(e.StartUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
            item.SubItems.Add(FormatDuration(e.EndUtc - e.StartUtc));
            list.Items.Add(item);
        }
        list.Visible = entries.Count > 0;
        Controls.Add(list);

        empty.Text = "Henüz kayıtlı bağlantı yok.\r\nVPN bağlandığında ve kesildiğinde burada görünür.";
        empty.ForeColor = Ui.Muted; empty.Font = Ui.SubFont; empty.TextAlign = ContentAlignment.MiddleCenter;
        empty.Visible = entries.Count == 0;
        empty.BackColor = Ui.Surface;
        list.BackColor = Ui.Surface;
        Controls.Add(empty);

        StyleButton(exportButton, "CSV olarak dışa aktar", false);
        StyleButton(closeButton, "Kapat", true);
        exportButton.Enabled = entries.Count > 0;
        exportButton.Click += delegate { ExportCsvClick(); };
        closeButton.Click += delegate { Close(); };
        Controls.Add(exportButton); Controls.Add(closeButton);
        AcceptButton = null; CancelButton = closeButton;
        KeyDown += delegate(object s, KeyEventArgs k) { if (k.KeyCode == Keys.Escape) Close(); };

        LayoutAll();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutAll();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var on = 1; NativeMethods.DwmSetWindowAttribute(Handle, 20, ref on, 4); // dark title bar
        }
        catch { }
        try { SetWindowTheme(list.Handle, "DarkMode_Explorer", null); } catch { } // dark scrollbars
    }

    void BuildTotals()
    {
        var map = new Dictionary<string, Total>();
        foreach (var e in entries)
        {
            var key = string.IsNullOrEmpty(e.ProfileId) ? (e.ProfileName ?? "") : e.ProfileId;
            Total t;
            if (!map.TryGetValue(key, out t)) { t = new Total { Name = e.ProfileName ?? "" }; map[key] = t; }
            t.Count++;
            t.Span += e.EndUtc - e.StartUtc;
        }
        totals.AddRange(map.Values.OrderByDescending(t => t.Span).Take(5));
    }

    void LayoutAll()
    {
        var pad = Ui.S(20);
        var w = ClientSize.Width - pad * 2;
        var btnH = Ui.S(34);
        var btnTop = ClientSize.Height - pad - btnH;
        var top = pad;
        if (showTotals)
        {
            var h = Ui.S(34) + Math.Min(5, totals.Count) * Ui.S(22) + Ui.S(8);
            totalsPanel.SetBounds(pad, top, w, h);
            top += h + Ui.S(14);
        }
        var listBottom = btnTop - Ui.S(14);
        list.SetBounds(pad, top, w, Math.Max(0, listBottom - top));
        empty.SetBounds(pad, top, w, Math.Max(0, listBottom - top));
        if (list.Columns.Count == 3)
        {
            var startW = Ui.S(140); var durW = Ui.S(110);
            list.Columns[0].Width = Math.Max(Ui.S(120), w - startW - durW);
            list.Columns[1].Width = startW; list.Columns[2].Width = durW;
        }
        var bw = Ui.S(180);
        exportButton.SetBounds(pad, btnTop, Ui.S(190), btnH);
        closeButton.SetBounds(ClientSize.Width - pad - bw, btnTop, bw, btnH);
    }

    void StyleButton(Button b, string text, bool primary)
    {
        b.Text = text; b.FlatStyle = FlatStyle.Flat; b.Font = Ui.SubFont; b.Cursor = Cursors.Hand;
        b.BackColor = primary ? Ui.Brand : Ui.Surface; b.ForeColor = Ui.Text;
        b.FlatAppearance.BorderColor = primary ? Ui.Accent2 : Ui.BorderStrong;
        b.FlatAppearance.MouseOverBackColor = primary ? Ui.Accent : Ui.SurfaceHover;
        b.FlatAppearance.MouseDownBackColor = Ui.SelectedFill;
        b.UseVisualStyleBackColor = false;
    }

    void PaintTotals(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var box = new RectangleF(0, 0, totalsPanel.Width - 1, totalsPanel.Height - 1);
        Ui.FillRound(g, Ui.Surface, box, Ui.S(10));
        Ui.DrawRound(g, Ui.Border, box, Ui.S(10));
        var x = Ui.S(14); var y = Ui.S(9);
        TextRenderer.DrawText(g, "Profil bazında toplam (ilk 5)", Ui.HeaderFont, new Point(x, y), Ui.Muted, Ui.Flags);
        y += Ui.S(24);
        var rightX = totalsPanel.Width - Ui.S(14);
        foreach (var t in totals.Take(5))
        {
            var name = t.Name.Length == 0 ? "Adsız profil" : t.Name;
            var right = t.Count + " bağlantı · " + FormatDuration(t.Span);
            var rs = Ui.Measure(g, right, Ui.SubFont);
            TextRenderer.DrawText(g, right, Ui.SubFont, new Point(rightX - rs.Width, y), Ui.Muted, Ui.Flags);
            var nameW = rightX - rs.Width - Ui.S(20) - x;
            TextRenderer.DrawText(g, name, Ui.SubFont, new Rectangle(x, y, nameW, Ui.S(20)), Ui.Text, Ui.Flags | TextFormatFlags.EndEllipsis);
            y += Ui.S(22);
        }
    }

    void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var b = new SolidBrush(Ui.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
        TextRenderer.DrawText(e.Graphics, e.Header.Text, Ui.HeaderFont,
            new Rectangle(e.Bounds.X + Ui.S(10), e.Bounds.Y, e.Bounds.Width - Ui.S(12), e.Bounds.Height), Ui.Muted,
            Ui.Flags | TextFormatFlags.VerticalCenter);
    }

    void DrawRow(object sender, DrawListViewItemEventArgs e)
    {
        var selected = (e.State & ListViewItemStates.Selected) != 0;
        using (var b = new SolidBrush(selected ? Ui.SelectedFill : Ui.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
        using (var pen = new Pen(Ui.Border)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    void DrawCell(object sender, DrawListViewSubItemEventArgs e)
    {
        var color = e.ColumnIndex == 0 ? Ui.Text : Ui.Muted;
        var rect = new Rectangle(e.Bounds.X + Ui.S(10), e.Bounds.Y, e.Bounds.Width - Ui.S(14), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, e.ColumnIndex == 0 ? Ui.NameFont : Ui.SubFont, rect, color,
            Ui.Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    void ExportCsvClick()
    {
        using (var dlg = new SaveFileDialog())
        {
            dlg.Title = "CSV olarak dışa aktar";
            dlg.Filter = "CSV dosyası (*.csv)|*.csv";
            dlg.FileName = "baglanti-gecmisi.csv";
            dlg.AddExtension = true;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                ConnectionHistory.ExportCsv(dlg.FileName);
                MessageBox.Show(this, "Bağlantı geçmişi dışa aktarıldı:\r\n" + dlg.FileName, "Bağlantı geçmişi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Dışa aktarılamadı: " + ex.Message, "Bağlantı geçmişi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return Math.Max(0, (int)t.TotalSeconds) + " sn";
        if (t.TotalHours >= 1) return (int)t.TotalHours + " sa " + t.Minutes + " dk";
        return t.Minutes + " dk";
    }
}
