using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// =====================================================================
// First-run setup: detects OpenVPN Connect / FortiClient on this PC, optionally imports the
// bundled profile pack (profiller\ next to the exe), and sets the start-up options.
// Shown when no settings file exists yet, or with --setup.
// =====================================================================

internal static class ProfilePack
{
    internal static string FolderOverride; // tests
    public static string Folder { get { return FolderOverride ?? Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "profiller"); } }
    public static string OpenVpnZip { get { return Path.Combine(Folder, "OpenVPN-Profilleri.zip"); } }
    public static string FortiJson { get { return Path.Combine(Folder, "FortiClient-Tunelleri.json"); } }
    public static string FortiScript { get { return Path.Combine(Folder, "FortiClient-Ice-Aktar.ps1"); } }

    // Names in the OpenVPN pack (from manifest.json), or empty.
    public static List<string> OpenVpnNames()
    {
        var names = new List<string>();
        try
        {
            if (!File.Exists(OpenVpnZip)) return names;
            using (var zip = System.IO.Compression.ZipFile.OpenRead(OpenVpnZip))
            {
                var entry = zip.GetEntry("manifest.json");
                if (entry == null) return zip.Entries.Where(e => e.Name.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase)).Select(e => Path.GetFileNameWithoutExtension(e.Name)).ToList();
                using (var sr = new StreamReader(entry.Open()))
                {
                    var arr = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(sr.ReadToEnd()) as object[];
                    if (arr != null)
                        foreach (var o in arr)
                        {
                            var d = o as IDictionary<string, object>; object f;
                            if (d != null && d.TryGetValue("file", out f) && f != null) names.Add(Path.GetFileNameWithoutExtension(Convert.ToString(f)));
                        }
                }
            }
        }
        catch { }
        return names;
    }

    // Tunnel names in the FortiClient pack, or empty.
    public static List<string> FortiNames()
    {
        var names = new List<string>();
        try
        {
            if (!File.Exists(FortiJson)) return names;
            var arr = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FortiJson)) as object[];
            if (arr != null)
                foreach (var o in arr)
                {
                    var d = o as IDictionary<string, object>; object n;
                    if (d != null && d.TryGetValue("name", out n)) names.Add(Convert.ToString(n));
                }
        }
        catch { }
        return names;
    }

    public static void Delete()
    {
        try { ImportService.CleanupTemp(Folder); } catch { }
    }
}

internal sealed class SetupWizard : Form
{
    const int WM_NCHITTEST = 0x84, HTCLIENT = 1, HTCAPTION = 2, CS_DROPSHADOW = 0x20000;
    static int S(float v) { return Ui.S(v); }

    readonly AppSettings settings = SettingsStore.Load(); // keeps existing settings on reinstall
    readonly bool hadSettings = File.Exists(SettingsStore.FilePath);
    readonly SPage page;
    readonly SButton finish = new SButton("Kurulumu tamamla", SButtonKind.Primary), skip = new SButton("Atla", SButtonKind.Ghost);
    readonly SToggle importOvpn = new SToggle(), importForti = new SToggle(), autoStart = new SToggle(), desktop = new SToggle(), showForti = new SToggle(), deletePack = new SToggle();
    readonly SSegmented theme = new SSegmented("Koyu", "Açık", "Sistem", "Derin Mavi");
    string progress = ""; float progressValue = -1; bool running;
    readonly List<string> packOvpn = ProfilePack.OpenVpnNames(), packForti = ProfilePack.FortiNames();
    List<string> missingOvpn = new List<string>(), missingForti = new List<string>();
    readonly bool ovpnInstalled, fortiInstalled;

    public SetupWizard()
    {
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen;
        Text = "OpenVPN Hızlı Arama — Kurulum"; DoubleBuffered = true; KeyPreview = true; BackColor = Ui.Bg;
        Size = new Size(S(640), Math.Min(S(700), Screen.PrimaryScreen.WorkingArea.Height - S(40)));
        Icon = IconArt.Load(new Size(32, 32)) ?? Icon;
        SetStyle(ControlStyles.ResizeRedraw, true);

        ovpnInstalled = File.Exists(OpenVpnExe);
        fortiInstalled = FortiStore.IsInstalled;
        var existingOvpn = ExistingOpenVpnNames();
        var existingForti = new HashSet<string>(FortiStore.Load().Where(p => p.Id.StartsWith(FortiStore.IdPrefixSsl)).Select(p => p.Name), StringComparer.CurrentCultureIgnoreCase);
        missingOvpn = packOvpn.Where(n => !existingOvpn.Contains(n)).ToList();
        missingForti = packForti.Where(n => !existingForti.Contains(n)).ToList();

        page = new SPage("Hoş geldiniz", "Bu cihazdaki VPN uygulamaları tarandı. Ayarları seçip kurulumu tamamlayın.") { BackColor = Ui.Bg };
        page.SetBounds(0, S(64), Width, Height - S(64) - S(72));
        page.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(page);

        // detected apps
        var ovpnProfiles = existingOvpn.Count;
        var fortiTunnels = FortiStore.Load().Count;
        page.Add(new SCard(), caption: "ALGILANAN VPN UYGULAMALARI")
            .Add(new SRow("OpenVPN Connect", ovpnInstalled ? "Kurulu · bu cihazda " + ovpnProfiles + " profil" : "Bulunamadı — openvpn.net/client adresinden kurabilirsiniz", null, Glyph.Server))
            .Add(new SRow("FortiClient VPN", fortiInstalled ? "Kurulu · bu cihazda " + fortiTunnels + " tünel" : "Bulunamadı (isteğe bağlı)", null, Glyph.ShieldLock));

        // bundled profile pack
        if (packOvpn.Count > 0 || packForti.Count > 0)
        {
            var card = page.Add(new SCard(), caption: "PROFİL PAKETİ");
            if (packOvpn.Count > 0)
            {
                importOvpn.SetInstant(ovpnInstalled && missingOvpn.Count > 0); importOvpn.Enabled = ovpnInstalled && missingOvpn.Count > 0;
                card.Add(new SRow("OpenVPN profillerini içe aktar", "Pakette " + packOvpn.Count + " · bu cihazda eksik " + missingOvpn.Count + (ovpnInstalled ? "" : " · önce OpenVPN Connect kurulmalı"), importOvpn, Glyph.Import));
            }
            if (packForti.Count > 0)
            {
                importForti.SetInstant(fortiInstalled && missingForti.Count > 0); importForti.Enabled = fortiInstalled && missingForti.Count > 0;
                card.Add(new SRow("FortiClient tünellerini içe aktar", "Pakette " + packForti.Count + " · eksik " + missingForti.Count + " · yönetici izni ister", importForti, Glyph.ShieldLock));
            }
            deletePack.SetInstant(true);
            card.Add(new SRow("Aktarımdan sonra paketi sil", "Paket özel anahtarlar içerir; bu cihazda bırakılmaması önerilir.", deletePack, Glyph.Delete));
        }

        // options
        autoStart.SetInstant(hadSettings ? AutoStart.IsEnabled() : true); desktop.SetInstant(!hadSettings); showForti.SetInstant(hadSettings ? settings.ShowForti : fortiInstalled);
        theme.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "dark", "light", "system", "ocean" }, settings.Theme));
        page.Add(new SCard(), caption: "AYARLAR")
            .Add(new SRow("Windows açılışında başlat", "OpenVPN açılınca panel kendiliğinden yanına gelir.", autoStart, Glyph.Power))
            .Add(new SRow("Masaüstü kısayolu", "\"OpenVPN Hızlı Arama\" kısayolu oluşturulur.", desktop, Glyph.Open))
            .Add(new SRow("FortiClient profillerini göster", null, showForti, Glyph.ShieldLock))
            .Add(new SRow("Tema", null, theme, Glyph.Palette));

        finish.Width = Math.Max(finish.Width, S(170));
        finish.Location = new Point(Width - S(24) - finish.Width, Height - S(72) + (S(72) - finish.Height) / 2);
        skip.Location = new Point(finish.Left - S(10) - skip.Width, finish.Top);
        finish.Anchor = skip.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        Controls.Add(finish); Controls.Add(skip);
        finish.Click += delegate { Run(); };
        // Shown via Application.Run, so DialogResult does not auto-close; call Close() explicitly.
        skip.Click += delegate { if (!running) { SaveBasics(); Close(); } };
        KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape && !running) { SaveBasics(); Close(); } };
    }

    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= CS_DROPSHADOW; return cp; } }
    internal static bool NoActivate; // tests
    protected override bool ShowWithoutActivation { get { return NoActivate; } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { var c = 2; NativeMethods.DwmSetWindowAttribute(Handle, 33, ref c, 4); var b = Ui.BorderStrong.R | (Ui.BorderStrong.G << 8) | (Ui.BorderStrong.B << 16); NativeMethods.DwmSetWindowAttribute(Handle, 34, ref b, 4); } catch { }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && m.Result.ToInt32() == HTCLIENT)
        {
            var v = m.LParam.ToInt64();
            var p = PointToClient(new Point((short)(v & 0xffff), (short)((v >> 16) & 0xffff)));
            if (p.Y < S(64)) m.Result = (IntPtr)HTCAPTION;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        Glyphs.Draw(g, Glyph.ShieldLock, new RectangleF(S(22), S(15), S(32), S(32)), Ui.Accent);
        TextRenderer.DrawText(g, "OpenVPN Hızlı Arama", SFonts.Big, new Point(S(66), S(14)), Ui.Text, Ui.Flags);
        TextRenderer.DrawText(g, "İlk kurulum", SFonts.RowDesc, new Point(S(67), S(36)), Ui.Muted, Ui.Flags);

        var fy = Height - S(72);
        using (var p = new Pen(Ui.Border)) g.DrawLine(p, 0, fy, Width, fy);
        if (progress.Length > 0)
        {
            var x = S(24); var w = skip.Left - S(24) - x;
            TextRenderer.DrawText(g, progress, SFonts.RowDesc, new Rectangle(x, fy + S(18), w, S(18)), Ui.Text, Ui.Flags | TextFormatFlags.EndEllipsis);
            if (progressValue >= 0)
            {
                var bar = new Rectangle(x, fy + S(44), w, S(5));
                Ui.FillRound(g, Ui.Surface, bar, S(2.5F));
                if (progressValue > 0) Ui.FillRound(g, Ui.Accent, new RectangleF(bar.X, bar.Y, Math.Max(S(5), bar.Width * progressValue), bar.Height), S(2.5F));
            }
        }
        using (var p = new Pen(Ui.BorderStrong)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }

    void SetProgress(string text, float value) { progress = text; progressValue = value; Invalidate(new Rectangle(0, Height - S(72), Width, S(72))); }

    static string OpenVpnExe
    {
        get
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles") })
                if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "OpenVPN Connect", "OpenVPNConnect.exe"))) return Path.Combine(root, "OpenVPN Connect", "OpenVPNConnect.exe");
            return "";
        }
    }

    static HashSet<string> ExistingOpenVpnNames()
    {
        try { return new HashSet<string>(ProfileStore.Load().Select(p => p.Name), StringComparer.CurrentCultureIgnoreCase); }
        catch { return new HashSet<string>(StringComparer.CurrentCultureIgnoreCase); } // OpenVPN never started yet
    }

    void SaveBasics()
    {
        settings.Theme = new[] { "dark", "light", "system", "ocean" }[theme.SelectedIndex];
        settings.ShowForti = showForti.Checked;
        settings.AutoStart = autoStart.Checked;
        try { SettingsStore.Save(settings); } catch { }
        try { AutoStart.SetEnabled(autoStart.Checked); } catch { }
        if (desktop.Checked) try { CreateDesktopShortcut(); } catch { }
    }

    static void CreateDesktopShortcut()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OpenVPN Hızlı Arama.lnk");
        var t = Type.GetTypeFromProgID("WScript.Shell"); var shell = Activator.CreateInstance(t);
        var lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
        var lt = lnk.GetType();
        lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Application.ExecutablePath });
        lt.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
        lt.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Application.ExecutablePath + ",0" });
        lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "OpenVPN ve FortiClient profillerini hızlıca ara ve bağlan" });
        lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
    }

    void Run()
    {
        if (running) return;
        running = true; finish.Enabled = false; skip.Enabled = false; finish.Invalidate(); skip.Invalidate();
        SaveBasics();
        var doOvpn = importOvpn.Checked && missingOvpn.Count > 0;
        var doForti = importForti.Checked && missingForti.Count > 0;
        var removePack = deletePack.Checked;
        ThreadPool.QueueUserWorkItem(delegate
        {
            var summary = new List<string>();
            if (doOvpn) summary.Add(ImportOpenVpn());
            if (doForti) summary.Add(ImportForti());
            if (removePack && (doOvpn || doForti)) ProfilePack.Delete();
            BeginInvoke(new Action(delegate
            {
                running = false;
                SetProgress("Kurulum tamamlandı.", 1);
                if (summary.Count > 0) GlassDialog.Show(this, string.Join("\n\n", summary.ToArray()), "Kurulum tamamlandı", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close(); // Application.Run: DialogResult alone does not close the window
            }));
        });
    }

    void Progress(string text, float value) { try { BeginInvoke(new Action(delegate { SetProgress(text, value); })); } catch { } }

    // Imports the missing OpenVPN profiles one by one, then verifies them against OpenVPN's own list.
    string ImportOpenVpn()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "OvpnQS-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            List<string> pinnedNames;
            var files = ImportService.ExtractBackup(ProfilePack.OpenVpnZip, tmp, out pinnedNames);
            var existing = ExistingOpenVpnNames();
            var usernames = ImportService.ReadUsernames(ProfilePack.OpenVpnZip); // pre-fills user name; no passwords
            var todo = files.Where(f => !existing.Contains(Path.GetFileNameWithoutExtension(f))).ToList();
            if (todo.Count == 0) return "OpenVPN: tüm profiller zaten mevcut.";

            // OpenVPN Connect must be running (and past its first-run dialogs) to accept imports.
            if (Process.GetProcessesByName("OpenVPNConnect").Length == 0)
            {
                Progress("OpenVPN Connect başlatılıyor…", 0);
                using (Process.Start(new ProcessStartInfo { FileName = OpenVpnExe, Arguments = "--accept-gdpr --skip-startup-dialogs", UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(OpenVpnExe) })) { }
                for (var i = 0; i < 40 && !File.Exists(ProfileStore.ConfigPath); i++) Thread.Sleep(500);
                Thread.Sleep(3000);
            }

            for (var i = 0; i < todo.Count; i++)
            {
                Progress("OpenVPN profili içe aktarılıyor (" + (i + 1) + "/" + todo.Count + "): " + Path.GetFileNameWithoutExtension(todo[i]), i / (float)todo.Count);
                ImportService.ImportFiles(new[] { todo[i] }, existing, usernames);
            }
            Progress("OpenVPN profilleri doğrulanıyor…", 1);
            Thread.Sleep(3000);

            var now = ExistingOpenVpnNames();
            var ok = todo.Count(f => now.Contains(Path.GetFileNameWithoutExtension(f)));
            var failed = todo.Where(f => !now.Contains(Path.GetFileNameWithoutExtension(f))).Select(Path.GetFileNameWithoutExtension).ToList();

            // restore pins from the pack
            if (pinnedNames != null && pinnedNames.Count > 0)
                try
                {
                    var pins = PinStore.Load();
                    foreach (var p in ProfileStore.Load()) if (pinnedNames.Contains(p.Name, StringComparer.CurrentCultureIgnoreCase)) pins.Add(p.Id);
                    PinStore.Save(pins);
                }
                catch { }

            var msg = "OpenVPN: " + ok + " / " + todo.Count + " profil içe aktarıldı.";
            if (failed.Count > 0) msg += "\nAktarılamayanlar: " + string.Join(", ", failed.Take(8).ToArray()) + (failed.Count > 8 ? " …" : "") + "\n(Menüden \"Yedekten geri yükle\" ile tekrar deneyebilirsiniz.)";
            return msg;
        }
        catch (Exception ex) { return "OpenVPN profilleri içe aktarılamadı: " + ex.Message; }
        finally { ImportService.CleanupTemp(tmp); }
    }

    // FortiClient keeps SSL VPN tunnels under HKLM, so the bundled script runs elevated (UAC prompt).
    string ImportForti()
    {
        Progress("FortiClient tünelleri içe aktarılıyor (yönetici izni isteniyor)…", -1);
        try
        {
            using (var p = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + ProfilePack.FortiScript + "\" -Json \"" + ProfilePack.FortiJson + "\"",
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            }))
            {
                if (p != null) p.WaitForExit(60000);
            }
        }
        catch (System.ComponentModel.Win32Exception) { return "FortiClient: yönetici izni verilmediği için tüneller aktarılmadı."; }
        catch (Exception ex) { return "FortiClient tünelleri aktarılamadı: " + ex.Message; }

        var have = new HashSet<string>(FortiStore.Load().Select(x => x.Name), StringComparer.CurrentCultureIgnoreCase);
        var ok = missingForti.Count(n => have.Contains(n));
        return "FortiClient: " + ok + " / " + missingForti.Count + " tünel eklendi. Kullanıcı adı ve parolanızı ilk bağlantıda FortiClient'a girin.";
    }
}
