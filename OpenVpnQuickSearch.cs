using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// =====================================================================
// Data
// =====================================================================

internal sealed class VpnProfile
{
    public string Id, Name, Host, Username, Source, Initials;
    public long LastConnected;
    public string[] Keys; // search keys: name, host, username, source
}

internal static class ProfileStore
{
    public static string ConfigPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenVPN Connect", "config.json"); } }

    static string TextValue(IDictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }
    static long LongValue(IDictionary<string, object> d, string k) { object v; long n; return d.TryGetValue(k, out v) && long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out n) ? n : 0; }

    public static List<VpnProfile> Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path)) throw new FileNotFoundException("OpenVPN Connect profil deposu bulunamadı.", path);
        string text;
        // OpenVPN Connect may be writing the file at the same time; allow shared access.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();

        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        var root = js.DeserializeObject(text) as IDictionary<string, object>;
        if (root == null || !root.ContainsKey("persist:root")) throw new InvalidDataException("OpenVPN Connect profil deposu okunamadı.");
        var persisted = js.DeserializeObject(Convert.ToString(root["persist:root"])) as IDictionary<string, object>;
        if (persisted == null || !persisted.ContainsKey("status")) throw new InvalidDataException("OpenVPN Connect profil durumu bulunamadı.");
        var status = js.DeserializeObject(Convert.ToString(persisted["status"])) as IDictionary<string, object>;
        object raw;
        if (status == null || !status.TryGetValue("profiles", out raw)) return new List<VpnProfile>();
        var profiles = raw as IDictionary<string, object>;
        if (profiles == null) return new List<VpnProfile>();

        var result = new List<VpnProfile>();
        foreach (var pair in profiles)
        {
            var item = pair.Value as IDictionary<string, object>;
            if (item == null) continue;
            var name = TextValue(item, "profileDisplayName");
            if (string.IsNullOrWhiteSpace(name)) name = TextValue(item, "profileName");
            if (string.IsNullOrWhiteSpace(name)) name = TextValue(item, "name");
            var host = TextValue(item, "hostname");
            if (string.IsNullOrWhiteSpace(host)) host = TextValue(item, "remoteHost");
            var source = TextValue(item, "filePath");
            var id = TextValue(item, "id");
            if (string.IsNullOrWhiteSpace(id)) id = pair.Key;
            var p = new VpnProfile
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? "Adsız profil" : name.Trim(),
                Host = host.Trim(),
                Username = TextValue(item, "username").Trim(),
                Source = string.IsNullOrWhiteSpace(source) ? "" : SafeFileName(source),
                LastConnected = LongValue(item, "lastConnected")
            };
            p.Initials = MakeInitials(p.Name);
            p.Keys = new[] { SearchKey(p.Name), SearchKey(p.Host), SearchKey(p.Username), SearchKey(p.Source) };
            result.Add(p);
        }
        return result;
    }

    static string SafeFileName(string path) { try { return Path.GetFileName(path); } catch { return path; } }

    internal static string MakeInitials(string name)
    {
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        var words = new List<string>(); var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        // Names like "176.236.149.114 [Temiz]": use the words that start with a letter.
        var letterWords = words.Where(w => char.IsLetter(w[0])).ToList();
        if (letterWords.Count > 0) words = letterWords;
        else if (words.Count > 0) return "IP";
        if (words.Count == 0) return "?";
        if (words.Count == 1) return words[0].Substring(0, Math.Min(2, words[0].Length)).ToUpper(tr);
        return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpper(tr);
    }

    public static string SearchKey(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var b = new StringBuilder(value.Length);
        foreach (var c in value) b.Append(CharKey(c));
        return b.ToString();
    }

    // Per-character key, so match positions map back to the original text for highlighting.
    public static string CharKey(char c)
    {
        if (c == 'ı') c = 'i'; else if (c == 'İ') c = 'I';
        if (char.IsSurrogate(c)) return c.ToString();
        var n = c.ToString().Normalize(NormalizationForm.FormD);
        var b = new StringBuilder(1);
        foreach (var x in n) if (CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark) b.Append(char.ToUpperInvariant(x));
        return b.ToString();
    }

    // Every search term must match at least one field (name, host, username, source file).
    public static bool Matches(VpnProfile p, string[] terms)
    {
        foreach (var t in terms)
        {
            var hit = false;
            foreach (var k in p.Keys) if (k.Contains(t)) { hit = true; break; }
            if (!hit) return false;
        }
        return true;
    }

    // Lower is better: name prefix, then word start in name, then name, then host prefix, then anything.
    public static int Score(VpnProfile p, string[] terms)
    {
        var total = 0;
        foreach (var t in terms)
        {
            var name = p.Keys[0]; var i = name.IndexOf(t, StringComparison.Ordinal);
            if (i == 0) continue;
            if (i > 0 && !char.IsLetterOrDigit(name[i - 1])) { total += 1; continue; }
            if (i > 0) { total += 2; continue; }
            total += p.Keys[1].StartsWith(t, StringComparison.Ordinal) ? 3 : 4;
        }
        return total;
    }
}

internal static class PinStore
{
    static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenVpnQuickSearch", "pinned.txt"); } }
    public static HashSet<string> Load()
    {
        if (Demo.On) return Demo.Pins();
        try { if (File.Exists(FilePath)) return new HashSet<string>(File.ReadAllLines(FilePath, Encoding.UTF8).Where(l => l.Trim().Length > 0)); } catch { }
        return new HashSet<string>();
    }
    public static void Save(HashSet<string> ids)
    {
        if (Demo.On) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)); File.WriteAllLines(FilePath, ids.ToArray(), Encoding.UTF8); } catch { }
    }
}

// =====================================================================
// Native
// =====================================================================

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    internal delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("kernel32.dll")] internal static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();

    // Returns unused pages to Windows (shown as lower "Memory" in Task Manager). Used while hidden.
    internal static void TrimMemory()
    {
        try
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { }
    }
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    // Visible frame of a window (without the invisible resize borders Windows 10/11 adds).
    internal static bool GetFrame(IntPtr hWnd, out RECT r)
    {
        try { if (DwmGetWindowAttribute(hWnd, 9, out r, Marshal.SizeOf(typeof(RECT))) == 0 && r.Right > r.Left) return true; } catch { }
        return GetWindowRect(hWnd, out r);
    }
}

// App mark and .ico generation share one drawing so the icon matches the header.
internal static class IconArt
{
    // App mark: sapphire glass tile with the shield-lock logo from the asset library.
    public static void DrawMark(Graphics g, RectangleF r) { DrawMark(g, r, null); }

    // badge: null, "connected", "connecting", "error" (tray icon states)
    public static void DrawMark(Graphics g, RectangleF r, string badge)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ui.Round(r, r.Width * 0.26F))
        using (var b = new LinearGradientBrush(new RectangleF(r.X, r.Y, r.Width, r.Height + 1), Color.FromArgb(0x7F, 0xA3, 0xCF), Color.FromArgb(0x3C, 0x5C, 0x86), 90F)) g.FillPath(b, path);
        using (var path = Ui.Round(new RectangleF(r.X + 0.5F, r.Y + 0.5F, r.Width - 1, r.Height - 1), r.Width * 0.26F))
        using (var pen = new Pen(Color.FromArgb(120, 0xBF, 0xD4, 0xEE), Math.Max(1F, r.Width / 32F))) g.DrawPath(pen, path);
        var inset = r.Width * 0.2F;
        Assets.Draw(g, "app-logo", RectangleF.Inflate(r, -inset, -inset), Color.FromArgb(246, 249, 253), r.Width < 24 ? 1.5F : 1.2F);
        if (badge == null) return;
        var d = r.Width * 0.46F; var br = new RectangleF(r.Right - d, r.Bottom - d, d, d);
        var tone = badge == "connected" ? Color.FromArgb(0x64, 0xBD, 0x96) : badge == "error" ? Color.FromArgb(0xC4, 0x78, 0x78) : Color.FromArgb(0xC4, 0xA1, 0x6E);
        using (var b = new SolidBrush(Color.FromArgb(0x1C, 0x22, 0x2C))) g.FillEllipse(b, RectangleF.Inflate(br, d * 0.1F, d * 0.1F));
        using (var b = new SolidBrush(tone)) g.FillEllipse(b, br);
        var ic = RectangleF.Inflate(br, -d * 0.2F, -d * 0.2F);
        Assets.Draw(g, badge == "connected" ? "state-connected" : badge == "error" ? "state-error" : "state-connecting", ic, Color.FromArgb(0x1C, 0x22, 0x2C), 1.6F);
    }

    // Tray icon for a connection state (disposes nothing: caller owns the returned Icon and must DestroyIcon it).
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    public static void Release(Icon icon) { try { if (icon != null) { DestroyIcon(icon.Handle); icon.Dispose(); } } catch { } }
    public static Icon TrayIcon(string badge)
    {
        var size = SystemInformation.SmallIconSize.Width <= 16 ? 32 : 48;
        using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); DrawMark(g, new RectangleF(1, 1, size - 2, size - 2), badge); }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent); g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var pad = size <= 16 ? 0F : size * 0.04F;
            DrawMark(g, new RectangleF(pad, pad, size - 2 * pad - 0.5F, size - 2 * pad - 0.5F), null);
        }
        return bmp;
    }

    // Writes a multi-size .ico: 32-bit DIB entries, PNG for 256 px.
    public static void WriteIco(string path)
    {
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 256 };
        var images = new List<byte[]>();
        foreach (var s in sizes)
            using (var bmp = Render(s))
            using (var ms = new MemoryStream())
            {
                if (s == 256) bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                else
                {
                    var w = new BinaryWriter(ms);
                    w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                    for (var y = s - 1; y >= 0; y--) for (var x = 0; x < s; x++) { var c = bmp.GetPixel(x, y); w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A); }
                    var maskRow = ((s + 31) / 32) * 4; w.Write(new byte[maskRow * s]); w.Flush();
                }
                images.Add(ms.ToArray());
            }
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            var offset = 6 + 16 * sizes.Length;
            for (var i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32); w.Write(images[i].Length); w.Write(offset); offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }

    public static Icon Load(Size size)
    {
        try { using (var s = typeof(IconArt).Assembly.GetManifestResourceStream("app.ico")) if (s != null) return new Icon(s, size); } catch { }
        return null;
    }
}

// =====================================================================
// Theme
// =====================================================================

internal static class Ui
{
    public static float Scale = 1f;
    public static int S(float v) { return (int)Math.Round(v * Scale); }

    // Liquid Glass palette (set by Themes.Apply). Neutral surfaces, one system-blue accent.
    public static Color Bg = Color.FromArgb(24, 26, 34);
    public static Color Surface = Color.FromArgb(34, 37, 48);
    public static Color SurfaceHover = Color.FromArgb(44, 48, 62);
    public static Color SelectedFill = Color.FromArgb(38, 60, 100);
    public static Color Border = Color.FromArgb(58, 62, 78);
    public static Color BorderStrong = Color.FromArgb(84, 90, 110);
    public static Color Brand = Color.FromArgb(10, 100, 220);
    public static Color Accent = Color.FromArgb(10, 132, 255);
    public static Color Accent2 = Color.FromArgb(64, 156, 255);
    public static Color Text = Color.FromArgb(245, 246, 250);
    public static Color Muted = Color.FromArgb(166, 171, 186);
    public static Color Faint = Color.FromArgb(118, 124, 142);
    public static Color Gold = Color.FromArgb(255, 204, 0);
    public static Color Success = Color.FromArgb(48, 209, 88);
    public static Color Danger = Color.FromArgb(255, 69, 58);
    public static Color Warning = Color.FromArgb(255, 159, 10);
    public static Color Field = Color.FromArgb(0x26, 0x30, 0x3C);
    public static Color Highlight = Color.FromArgb(0x8B, 0xAE, 0xD9);
    public static Color GlassPrimary = Color.FromArgb(0x30, 0x39, 0x45), GlassSecondary = Color.FromArgb(0x26, 0x30, 0x3C), WindowBg = Color.FromArgb(0x1C, 0x22, 0x2C);

    // Segoe UI Variable (Windows 11) first, then Inter if installed, then Segoe UI.
    static string Pick(params string[] names)
    {
        try { var have = new HashSet<string>(new System.Drawing.Text.InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase); foreach (var n in names) if (have.Contains(n)) return n; } catch { }
        return names[names.Length - 1];
    }
    public static readonly string Family = Pick("Segoe UI Variable Text", "Inter", "Segoe UI");
    public static readonly string FamilySemi = Pick("Segoe UI Variable Text Semibold", "Inter SemiBold", "Segoe UI Semibold");
    public static readonly string FamilyMono = Pick("Segoe UI Variable Text", "Segoe UI");

    public static readonly Font TitleFont = new Font(FamilySemi, 12.5F);
    public static readonly Font NameFont = new Font(FamilySemi, 10.25F);
    public static readonly Font SubFont = new Font(Family, 9F);
    public static readonly Font SmallFont = new Font(Family, 8.25F);
    public static readonly Font HeaderFont = new Font(FamilySemi, 7.75F);
    public static readonly Font InitialsFont = new Font(FamilySemi, 9.25F);
    public static readonly Font AvatarFont = new Font(FamilySemi, 11F);
    public static readonly Font InputFont = new Font(Family, 11.5F);
    public static readonly Font KeyFont = new Font(FamilySemi, 7.5F);
    public static readonly Font KeyFontLarge = new Font(FamilySemi, 8.5F);
    public static readonly Font MenuFont = new Font(Family, 9.5F);
    public static readonly Font RowNameFont = new Font(FamilySemi, 10.5F);
    public static readonly Font SectionFont = new Font(FamilySemi, 7.75F);
    public static readonly Font SegFont = new Font(Family, 9F);
    public static readonly Font SegSelFont = new Font(FamilySemi, 9F);
    public static readonly Font IslandName = new Font(FamilySemi, 9.5F);
    public static readonly Font IslandMono = new Font(FamilyMono, 9F);

    public const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    // Low-saturation slate hues; the same name always gets the same one.
    static readonly Color[] AvatarColors =
    {
        Color.FromArgb(104, 136, 178), Color.FromArgb(128, 118, 172), Color.FromArgb(112, 104, 150), Color.FromArgb(96, 142, 160),
        Color.FromArgb(124, 150, 112), Color.FromArgb(166, 132, 108), Color.FromArgb(150, 110, 130), Color.FromArgb(104, 138, 136)
    };

    public static Color AvatarColor(string key)
    {
        unchecked { var h = 17; foreach (var c in key ?? "") h = h * 31 + c; return AvatarColors[(h & 0x7fffffff) % AvatarColors.Length]; }
    }

    public static Color Blend(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    public static void FillRound(Graphics g, Color c, RectangleF r, float radius) { using (var p = Round(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, p); }
    public static void DrawRound(Graphics g, Color c, RectangleF r, float radius, float width = 1F) { using (var p = Round(r, radius)) using (var pen = new Pen(c, width)) g.DrawPath(pen, p); }

    // Text measuring is one of the most expensive parts of painting; results are cached (bounded).
    static readonly Dictionary<string, Size> measureCache = new Dictionary<string, Size>();
    public static Size Measure(Graphics g, string text, Font f)
    {
        var key = f.Name + f.SizeInPoints.ToString(CultureInfo.InvariantCulture) + (int)f.Style + "\u0001" + text;
        Size s;
        if (measureCache.TryGetValue(key, out s)) return s;
        if (measureCache.Count > 3000) measureCache.Clear();
        s = TextRenderer.MeasureText(g, text, f, new Size(int.MaxValue, int.MaxValue), Flags);
        measureCache[key] = s;
        return s;
    }

    // Keycap such as "Esc" or "Ctrl+Alt+V": a small glass chip. Returns its width.
    public static int DrawKeycap(Graphics g, string text, int x, int y)
    {
        var sz = Measure(g, text, KeyFont);
        var r = new Rectangle(x, y, sz.Width + S(10), S(18));
        Glass.Paint(g, r, S(5), GlassKind.Secondary, 1.6F);
        GText.Draw(g, text, KeyFont, r, Muted, true);
        return r.Width;
    }

    public static PointF[] Star(float cx, float cy, float r)
    {
        var pts = new PointF[10];
        for (var i = 0; i < 10; i++)
        {
            var a = (-90 + i * 36) * Math.PI / 180; var rr = i % 2 == 0 ? r : r * 0.46F;
            pts[i] = new PointF(cx + (float)(Math.Cos(a) * rr), cy + (float)(Math.Sin(a) * rr));
        }
        return pts;
    }

    public static void DrawMagnifier(Graphics g, Color c, float x, float y, float size)
    {
        using (var p = new Pen(c, Math.Max(1.5F, size / 9F)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var d = size * 0.68F; g.DrawEllipse(p, x, y, d, d);
            g.DrawLine(p, x + d * 0.85F, y + d * 0.85F, x + size, y + size);
        }
    }

    public static string Relative(long value)
    {
        if (value <= 0) return "";
        DateTime t;
        try
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            t = (value > 100000000000L ? epoch.AddMilliseconds(value) : epoch.AddSeconds(value)).ToLocalTime();
        }
        catch { return ""; }
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "az önce";
        if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " dk önce";
        if (d.TotalHours < 24 && t.Date == DateTime.Today) return (int)d.TotalHours + " sa önce";
        if (t.Date == DateTime.Today.AddDays(-1)) return "dün";
        if (d.TotalDays < 7) return (int)Math.Ceiling(d.TotalDays) + " gün önce";
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        return t.Year == DateTime.Now.Year ? t.ToString("d MMM", tr) : t.ToString("d MMM yyyy", tr);
    }
}

internal sealed class DarkColors : ProfessionalColorTable
{
    public override Color MenuItemSelected { get { return Ui.SurfaceHover; } }
    public override Color MenuItemBorder { get { return Ui.SurfaceHover; } }
    public override Color MenuBorder { get { return Ui.BorderStrong; } }
    public override Color ToolStripDropDownBackground { get { return Ui.Surface; } }
    public override Color ImageMarginGradientBegin { get { return Ui.Surface; } }
    public override Color ImageMarginGradientMiddle { get { return Ui.Surface; } }
    public override Color ImageMarginGradientEnd { get { return Ui.Surface; } }
    public override Color SeparatorDark { get { return Ui.Border; } }
    public override Color SeparatorLight { get { return Ui.Surface; } }
    public override Color MenuItemSelectedGradientBegin { get { return Ui.SurfaceHover; } }
    public override Color MenuItemSelectedGradientEnd { get { return Ui.SurfaceHover; } }
    public override Color MenuItemPressedGradientBegin { get { return Ui.SelectedFill; } }
    public override Color MenuItemPressedGradientEnd { get { return Ui.SelectedFill; } }
}

internal sealed class DarkRenderer : ToolStripProfessionalRenderer
{
    public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Ui.Text : Ui.Faint; base.OnRenderItemText(e); }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Ui.Muted; base.OnRenderArrow(e); }
}


internal sealed class ListRow
{
    public string Header; public Glyph Icon; public VpnProfile Profile; public int Top, Height;
    public bool IsHeader { get { return Profile == null; } }
}

internal static class Ease
{
    public static float Clamp01(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
    public static float OutCubic(float t) { t = Clamp01(t); var u = 1 - t; return 1 - u * u * u; }
    public static float OutBack(float t) { t = Clamp01(t); const float c1 = 1.70158F, c3 = c1 + 1; var u = t - 1; return 1 + c3 * u * u * u + c1 * u * u; }
    public static float Frac(float v) { return v - (float)Math.Floor(v); }
}

// =====================================================================
// Main window: a borderless side panel that docks flush to OpenVPN Connect
// =====================================================================

internal sealed class SearchForm : Form
{
    const int WM_HOTKEY = 0x0312, WM_MOVING = 0x0216, WM_SIZING = 0x0214, WM_EXITSIZEMOVE = 0x0232, WM_NCHITTEST = 0x0084, HotkeyId = 0x5651;
    const int WM_SIZE = 0x0005, SIZE_MINIMIZED = 1;
    const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    const uint EVENT_SYSTEM_FOREGROUND = 0x3, EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const uint WINEVENT_SKIPOWNPROCESS = 0x2;
    const int CS_DROPSHADOW = 0x20000;
    const int ConnectTimeoutSeconds = 45;
    static readonly Font HeaderTitleFont = new Font("Segoe UI Semibold", 10F);

    readonly bool backgroundMode; readonly string openVpnPath;
    readonly SearchBox search = new SearchBox();
    readonly ProfileList list = new ProfileList();
    readonly Segmented segmented = new Segmented();
    readonly ConnectionIsland island = new ConnectionIsland();
    int providerFilter; string islandError; DateTime islandErrorUntil = DateTime.MinValue; bool fortiAuth;
    readonly HeaderButton moreButton = new HeaderButton(HeaderGlyph.More), minButton = new HeaderButton(HeaderGlyph.Minimize), closeButton = new HeaderButton(HeaderGlyph.Close);
    readonly System.Windows.Forms.Timer watcher = new System.Windows.Forms.Timer(), statusTimer = new System.Windows.Forms.Timer();
    // fadeTimer: window fade-in; ambient: header shimmer + breathing dots (only while visible); trimTimer: one-shot memory trim after hiding
    readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer { Interval = 15 }, ambient = new System.Windows.Forms.Timer { Interval = 100 }, trimTimer = new System.Windows.Forms.Timer { Interval = 4000 };
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    PointF[] shimmerPath; Rectangle shimmerArea, lastShimmer;
    string connectingProfileId; DateTime connectStarted, disconnectStarted = DateTime.MinValue, lastShowScan; int fadeStep;
    bool ovpnWindowBack; // the OpenVPN window minimized for the current command was already brought back (or need not be)
    readonly ContextMenuStrip mainMenu = new ContextMenuStrip(), trayMenu = new ContextMenuStrip(), rowMenu = new ContextMenuStrip();
    readonly NativeMethods.WinEventProc hookProc; // kept as a field so the GC never collects the callback
    NotifyIcon tray;
    List<VpnProfile> profiles = new List<VpnProfile>();
    HashSet<string> pins = PinStore.Load();
    RegisteredWaitHandle activationWait;
    NativeMethods.RECT lastRect;
    IntPtr ovpnHwnd, processHook, foregroundHook;
    uint hookedPid;
    DateTime configStamp = DateTime.MinValue;
    string loadError, subtitle = "", status, profileSignature = "";
    int tick, shownCount, dockSide = 1, floatHeight;
    bool manualOverride, lastFound, userMoved, exiting, allowVisible, noActivate, placed, hotkeyRegistered, docked, busy;
    AppSettings settings;
    ConnectionMonitor monitor;
    string connectedId; // profile id currently connected (from ConnectionMonitor)
    SettingsForm openSettings;

    static int S(float v) { return Ui.S(v); }
    TextBox Input { get { return search.Input; } }
    int HeaderHeight { get { return S(58); } }
    int SearchTop { get { return S(58); } }
    int SegTop { get { return S(118); } }
    int ListTop { get { return S(160); } }
    int FooterHeight { get { return S(38); } }

    public SearchForm(bool background, EventWaitHandle activation, AppSettings appSettings)
    {
        backgroundMode = background; openVpnPath = FindOpenVpnPath();
        settings = appSettings ?? AppSettings.Defaults();
        presence = PresenceWanted; // read by CreateParams when the handle is created below, and by BuildMenus (tray icon)
        var area = Screen.PrimaryScreen.WorkingArea;
        AutoScaleMode = AutoScaleMode.None;
        Text = "OpenVPN Hızlı Arama"; StartPosition = FormStartPosition.Manual; BackColor = Color.Black; ForeColor = Ui.Text; Font = Ui.SubFont;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; MaximizeBox = false; KeyPreview = true; DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        MinimumSize = new Size(S(380), S(360)); Size = new Size(S(520), Math.Min(S(720), area.Height));
        floatHeight = Height;
        Icon = IconArt.Load(new Size(32, 32)) ?? Icon;

        BuildLayout(); BuildMenus();
        FormClosing += OnFormClosingHandler;
        FormClosed += delegate { Cleanup(); };
        KeyDown += OnFormKeyDown;
        Shown += delegate { if (!noActivate) Input.Focus(); };
        VisibleChanged += delegate { if (Visible) WakeAmbient(); else ambient.Stop(); };
        Activated += delegate { KeepOpenVpnBelow(); WakeAmbient(); };

        watcher.Interval = 500; watcher.Tick += WatcherTick;
        // no window fade: Opacity would make the window layered and drop the compositor blur; the list and island animate instead
        ambient.Tick += delegate { AmbientTick(); };
        trimTimer.Tick += delegate { trimTimer.Stop(); if (!OnScreen) NativeMethods.TrimMemory(); };
        statusTimer.Interval = 2600; statusTimer.Tick += delegate { statusTimer.Stop(); status = null; InvalidateFooter(); };

        // modules: server reachability, live connection status, drag & drop import
        Reachability.Init(SynchronizationContext.Current);
        Reachability.Changed += delegate { list.Invalidate(); };
        list.ReachOf = p => Demo.On ? Demo.Reach(p.Id) : settings.ReachabilityCheck ? Reachability.Get(p.Id) : ReachState.Unknown;
        list.LiveText = LiveTextFor;
        list.AllowAmbient = () => Watched;
        monitor = new ConnectionMonitor(() => profiles);
        monitor.Changed += delegate { OnConnectionChanged(); };
        AllowDrop = true; list.AllowDrop = true;
        DragEnter += OnDragEnter; list.DragEnter += OnDragEnter;
        DragDrop += OnDragDrop; list.DragDrop += OnDragDrop;

        // diagnostics: names the report sanitizer must remove, the crash prompt setting, and the
        // offer of last session's crash report the first time the panel is shown
        CrashReporter.Profiles = () => profiles;
        CrashReporter.PromptMode = () => settings.CrashPrompt;
        CrashReporter.Status = SetStatus;   // result of a direct GitHub send
        CrashReporter.AutoConsent = () => settings.CrashAutoConsent;
        CrashReporter.NeverAsk = delegate { UiInvoke(delegate { settings.CrashPrompt = "never"; SaveSettings(); }); };
        Shown += delegate { BeginInvoke(new Action(OfferCrashWhenOnScreen)); };

        ReloadProfiles();
        if (Demo.On) DemoConnect(Demo.ConnectedName); else monitor.Poll();
        if (Demo.On && Demo.Query != null) Input.Text = Demo.Query;
        if (Demo.On && Demo.SettingsPage >= 0) Shown += delegate { BeginInvoke(new Action(ShowSettings)); };

        Microsoft.Win32.SystemEvents.UserPreferenceChanged += delegate { if (settings.Theme == "system" && IsHandleCreated && !IsDisposed) BeginInvoke(new Action(ApplyTheme)); };
        hookProc = OnWinEvent;
        IntPtr h; lastFound = FindOpenVpnWindow(out h);
        if (lastFound) TrackOpenVpn(h);
        // Taskbar mode keeps the window (and so its button) for the whole run: a --background start
        // shows it minimized instead of keeping it hidden (PresenceMode.StartsMinimized).
        var startMinimized = PresenceMode.StartsMinimized(presence, backgroundMode, lastFound);
        allowVisible = !backgroundMode || lastFound || startMinimized;
        noActivate = backgroundMode;
        PlaceWindow();

        var handle = Handle; // create handle now so hotkey and activation work while hidden
        // Not shown yet: WinForms only records the state; the first Show uses SW_SHOWMINIMIZED, which
        // does not take the focus from a background start. The normal bounds are the ones just placed.
        if (startMinimized) WindowState = FormWindowState.Minimized;
        foregroundHook = NativeMethods.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, hookProc, 0, 0, WINEVENT_SKIPOWNPROCESS);
        if (activation != null)
            activationWait = ThreadPool.RegisterWaitForSingleObject(activation, delegate
            {
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(delegate { manualOverride = true; ShowAndFocus(true); })); } catch { }
            }, null, -1, false);
        watcher.Start();
    }

    // ShowInTaskbar stays false (changing it recreates the handle). The "Uygulama nerede görünsün"
    // setting (PresenceMode) is the WS_EX_APPWINDOW bit (button) and, in taskbar mode, WS_MINIMIZEBOX
    // (modules\Glass.cs TaskbarButton): set here for the handle created at startup and for any
    // WinForms style refresh, flipped live on the same handle by ApplyPresence.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle = TaskbarButton.ExStyle(cp.ExStyle, PresenceMode.Button(presence));
            cp.Style = TaskbarButton.Style(cp.Style, PresenceMode.HideMinimizes(presence));
            return cp;
        }
    }

    string presence = PresenceMode.Tray; // the mode in effect (PresenceMode.Effective)
    string PresenceWanted { get { return PresenceMode.Effective(settings.Presence, Demo.On, !string.IsNullOrEmpty(SettingsStore.PathOverride)); } }

    // Minimized to the taskbar button (taskbar mode; native state, WinForms' WindowState can lag).
    bool Iconic { get { return IsHandleCreated && TaskbarButton.Iconic(Handle); } }
    // Shown and not minimized: what "the panel is open" means in every mode.
    bool OnScreen { get { return Visible && !Iconic; } }

    // After "Kaydet": same handle, so the hotkey, the WinEvent hooks and the DWM backdrop stay bound.
    void ApplyPresence()
    {
        var mode = PresenceWanted;
        if (mode == presence) return;
        presence = mode;
        if (tray != null) tray.Visible = PresenceMode.TrayIcon(mode);
        if (!IsHandleCreated) return;
        var minimizes = PresenceMode.HideMinimizes(mode);
        // leaving taskbar mode while minimized: without a permanent button the window must not stay
        // minimized (it would be left as a bare caption stub), so it comes back first
        if (!minimizes && Visible && Iconic) TaskbarButton.Restore(Handle, false);
        TaskbarButton.SetMinimizeBox(Handle, minimizes);
        TaskbarButton.Apply(Handle, PresenceMode.Button(mode), Visible);
        // entering taskbar mode while hidden: the button exists for the whole run, so show it minimized
        if (minimizes && !Visible) MinimizeToTaskbar();
    }

    // ---------- Layout ----------

    // Filter bar segments in order (null = "Tümü"); providerFilter indexes this list.
    List<VpnKind?> segmentKinds = VpnProviders.Segments(false, false, false);

    void BuildLayout()
    {
        foreach (var b in new[] { closeButton, minButton, moreButton }) { b.Anchor = AnchorStyles.Top | AnchorStyles.Right; Controls.Add(b); }
        var by = S(15);
        closeButton.Location = new Point(ClientSize.Width - S(14) - closeButton.Width, by);
        minButton.Location = new Point(closeButton.Left - S(6) - minButton.Width, by);
        moreButton.Location = new Point(minButton.Left - S(6) - moreButton.Width, by);
        closeButton.Click += delegate { HideForLater(); };
        minButton.Click += delegate { HideForLater(); };
        moreButton.Click += delegate { ShowMainMenu(); };

        // Dynamic Island: between the app mark and the window buttons
        island.Location = new Point(TitleRight + S(6), S(11)); island.Size = new Size(moreButton.Left - S(8) - (TitleRight + S(6)), S(36));
        island.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        island.AllowAmbient = () => Watched;
        island.DisconnectClicked += delegate { DisconnectCurrent(); };
        Controls.Add(island);

        search.Location = new Point(S(10), SearchTop); search.Size = new Size(ClientSize.Width - S(20), S(54));
        search.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        search.Hotkey = settings.Hotkey;
        Input.TextChanged += delegate { list.AnimateNext = true; ApplyFilter(null); };
        Controls.Add(search);

        segmented.Location = new Point(S(16), SegTop); segmented.Size = new Size(ClientSize.Width - S(32), S(34));
        segmented.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        segmented.Changed += delegate(int i) { providerFilter = i; list.AnimateNext = true; var sel = list.SelectedProfile; ApplyFilter(sel != null ? sel.Id : null); };
        Controls.Add(segmented);
        RefreshSegments();

        list.Location = new Point(0, ListTop); list.Size = new Size(ClientSize.Width, ClientSize.Height - ListTop - FooterHeight);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.IsPinned = p => pins.Contains(p.Id);
        list.Activated += Connect;
        list.PinToggled += TogglePin;
        list.MenuRequested += ShowRowMenu;
        list.DisconnectRequested += delegate(VpnProfile p)
        {
            if (p.Id == connectedId) Disconnect();
            else if (p.Id == fortiConnectedId) DisconnectForti(p);
            else { var x = ExtFor(p); if (x != null && p.Id == x.ConnectedId) DisconnectExt(x, p); }
        };
        list.ConnectFailedId += delegate(string id) { islandError = "Bağlantı kurulamadı"; islandErrorUntil = DateTime.UtcNow.AddSeconds(5); UpdateIsland(); };
        Controls.Add(list);
        Controls.Add(toast); toast.BringToFront();
    }

    // FortiClient segment only exists while FortiClient profiles are enabled; Sophos and WatchGuard
    // segments while that client is enabled and installed (also with no connection: the list then
    // says how to add one), or has rows (demo).
    // The selected provider stays selected when other segments come and go.
    void RefreshSegments()
    {
        var current = VpnProviders.SegmentAt(segmentKinds, providerFilter);
        segmentKinds = VpnProviders.Segments(settings.ShowForti,
            VpnProviders.ShowsExtSegment(settings.ShowSophos, sophosInstalled, profiles.Count(SophosStore.IsSophos)),
            VpnProviders.ShowsExtSegment(settings.ShowWatchGuard, watchGuardInstalled, profiles.Count(WatchGuardStore.IsWatchGuard)));
        providerFilter = VpnProviders.IndexOf(segmentKinds, current);
        segmented.Items = VpnProviders.SegmentNames(segmentKinds);
        segmented.Selected = providerFilter;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Glass.WindowBase(e.Graphics, ClientRectangle, WindowRadius);
    }

    float WindowRadius { get { return docked ? S(4) : S(8); } }   // Windows 11 corner radii (DWM rounds and antialiases the window itself)

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var w = ClientSize.Width;
        // app mark: sapphire glass tile with the shield-lock logo, then the product name
        var mark = new RectangleF(S(14), S(12), S(34), S(34));
        if (e.ClipRectangle.IntersectsWith(new Rectangle(0, 0, TitleRight, HeaderHeight)))
        {
            using (var path = Ui.Round(mark, S(11))) using (var br = new LinearGradientBrush(RectangleF.Inflate(mark, 1, 1), Ui.Blend(Ui.Accent, Ui.Highlight, 0.45F), Ui.Blend(Ui.Accent, Color.Black, 0.2F), 90F)) g.FillPath(br, path);
            Glass.Paint(g, mark, S(11), GlassKind.Secondary, 0.9F);
            Assets.Draw(g, "app-logo", RectangleF.Inflate(mark, -S(7), -S(7)), Color.FromArgb(240, 245, 252), 1.15F);
            GText.Draw(g, "VPN Manager", HeaderTitleFont2, new Point(S(58), S(29) - GText.Measure("VPN Manager", HeaderTitleFont2).Height / 2), Ui.Text);
        }
        var fyClip = ClientSize.Height - FooterHeight;
        if (e.ClipRectangle.Bottom > fyClip) PaintFooter(g, w);
        Glass.WindowFrame(g, ClientRectangle, WindowRadius);
    }

    static readonly Font FooterFont = new Font(Ui.Family, 8.5F);
    static readonly Font HeaderTitleFont2 = new Font(Ui.FamilySemi, 11.5F);
    int TitleRight { get { return S(58) + GText.Measure("VPN Manager", HeaderTitleFont2).Width + S(8); } }

    // Footer: quiet keyboard hints; a status message takes their place for a moment.
    void PaintFooter(Graphics g, int w)
    {
        var fy = ClientSize.Height - FooterHeight;
        var cy = fy + FooterHeight / 2 - S(2);
        var lineY = fy + S(2);
        using (var p = new Pen(Color.FromArgb(Themes.IsLight ? 40 : 28, Themes.IsLight ? Color.Black : Color.White), 1F)) g.DrawLine(p, S(18), lineY, w - S(18), lineY);
        if (false)
        {
            using (var p = new Pen(Ui.Success, S(1.8F)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(p, new[] { new PointF(S(20), cy), new PointF(S(24), cy + S(4)), new PointF(S(31), cy - S(4)) });
            GText.Draw(g, status, FooterFont, new Rectangle(S(40), cy - S(10), w - S(60), S(20)), Ui.Text);
            return;
        }
        var x = S(18); var first = true;
        foreach (var hint in new[] { new[] { "↑↓", "Seç" }, new[] { "Enter", "Bağlan" }, new[] { "Ctrl P", "Sabitle" }, new[] { "Esc", "Gizle" } })
        {
            var kw = GText.Measure(hint[0], Ui.KeyFontLarge).Width + S(14);
            var lw = GText.Measure(hint[1], FooterFont).Width;
            var need = (first ? 0 : S(14)) + kw + S(6) + lw;
            if (x + need > w - S(14)) break;
            if (!first) x += S(14);
            var kr = new RectangleF(x, cy - S(11), kw, S(22));
            Glass.Paint(g, kr, S(7), GlassKind.Secondary, 1.5F);
            GText.Draw(g, hint[0], Ui.KeyFontLarge, Rectangle.Round(kr), Ui.Muted, true);
            x += kw + S(6);
            GText.Draw(g, hint[1], FooterFont, new Rectangle(x, cy - S(10), lw, S(20)), Ui.Faint);
            x += lw; first = false;
        }
    }

    // True while the panel or OpenVPN is the foreground window (the user is looking at them).
    bool Watched
    {
        get
        {
            if (!OnScreen || WindowState == FormWindowState.Minimized) return false;
            var fg = NativeMethods.GetForegroundWindow();
            return fg == Handle || (ovpnHwnd != IntPtr.Zero && fg == ovpnHwnd) || ContainsFocus;
        }
    }

    void WakeAmbient() { if (Visible && Watched) list.Wake(); }

    void AmbientTick() { }

    void InvalidateHeader() { Invalidate(new Rectangle(0, 0, ClientSize.Width, HeaderHeight)); }
    void InvalidateFooter() { Invalidate(new Rectangle(0, ClientSize.Height - FooterHeight, ClientSize.Width, FooterHeight)); }

    readonly ToastLayer toast = new ToastLayer();
    static readonly string[] ProblemWords = { "kurulamadı", "alınamadı", "bulunamadı", "doğrulanamadı", "kullanılıyor", "oluşturulamadı", "silinemedi", "okunamadı", "kopyalanamadı", "başarısız", "formu doldurulamadı" };
    void SetStatus(string text)
    {
        status = text; UpdateIsland();
        if (!string.IsNullOrEmpty(text) && toast.Parent != null) toast.ShowToast(text, ProblemWords.Any(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    // ---------- Connection island ----------

    static bool IsAuthEvent(string ev) { return ev == "AUTH" || ev == "GET_CONFIG" || ev == "ASSIGN_IP" || ev == "ADD_ROUTES" || ev == "AUTH_PENDING"; }

    // Maps the app's real connection state onto the island. Called whenever something may have changed; cheap.
    void UpdateIsland()
    {
        if (island == null) return;
        var live = ConnectedProfile() ?? FortiConnectedProfile() ?? ExtConnectedProfile();
        UpdateTrayIcon(live != null ? "connected" : list.IsConnecting ? "connecting" : islandError != null && DateTime.UtcNow < islandErrorUntil ? "error" : null);
        if (list.IsDisconnecting) { island.SetState(IslandState.Disconnecting, "Bağlantı kesiliyor…", "", DateTime.MinValue); return; }
        if (islandError != null && DateTime.UtcNow < islandErrorUntil) { island.SetState(IslandState.Error, islandError, "", DateTime.MinValue); return; }
        islandError = null;
        if (list.IsConnecting)
        {
            var auth = fortiAuth || (monitor != null && IsAuthEvent(monitor.LastEvent));
            island.SetState(auth ? IslandState.Authenticating : IslandState.Connecting, auth ? "Kimlik doğrulanıyor…" : "Bağlanıyor…", "", DateTime.MinValue);
            return;
        }
        if (live != null)
        {
            var ext = ExtFor(live);
            var since = live.Id == fortiConnectedId ? fortiSince : ext != null && live.Id == ext.ConnectedId ? ext.Since : (monitor != null ? monitor.Current.SinceUtc : DateTime.MinValue);
            island.SetState(IslandState.Connected, live.Name, "", since);
            return;
        }
        island.SetState(IslandState.Disconnected, "Bağlı değil", "", DateTime.MinValue);
    }

    string trayBadge = "?"; Icon trayIcon;
    void UpdateTrayIcon(string badge)
    {
        if (tray == null || badge == trayBadge) return;
        trayBadge = badge;
        try { var old = trayIcon; trayIcon = IconArt.TrayIcon(badge); tray.Icon = trayIcon; IconArt.Release(old); } catch { }
    }

    // Demo mode: pretend a profile is connected (no process is started, nothing is changed on this PC).
    void DemoConnect(string name, bool announce = false)
    {
        fortiConnectedId = null;
        var p = string.IsNullOrEmpty(name) ? null : profiles.FirstOrDefault(x => x.Name.IndexOf(name, StringComparison.CurrentCultureIgnoreCase) >= 0);
        if (p != null) { fortiConnectedId = p.Id; fortiSince = DateTime.UtcNow.AddMinutes(-47); if (announce) SetStatus("\"" + p.Name + "\" bağlandı"); }
        var sel = list.SelectedProfile; ApplyFilter(sel != null ? sel.Id : null);
        InvalidateFooter(); UpdateIsland();
    }

    void DisconnectCurrent()
    {
        if (Demo.On) { DemoConnect(null); return; }
        if (connectedId != null) { Disconnect(); return; }
        var f = FortiConnectedProfile();
        if (f != null) { DisconnectForti(f); return; }
        foreach (var x in Exts) { var e = ExtProfile(x); if (e != null) { DisconnectExt(x, e); return; } }
    }

    // ---------- Menus ----------

    ContextMenuStrip Styled(ContextMenuStrip m) { return MenuKit.Style(m); }

    static MenuEntry Item(string text, Glyph glyph, EventHandler click, string keys = null) { return new MenuEntry(text, glyph, click, keys); }
    static MenuEntry Danger(string text, Glyph glyph, EventHandler click) { return new MenuEntry(text, glyph, click) { Danger = true }; }
    static ToolStripItem Caption(string text) { return new MenuCaption(text); }

    void BuildMenus()
    {
        Styled(mainMenu); Styled(trayMenu); Styled(rowMenu);
        trayMenu.Opening += delegate { BuildTrayMenu(); };
        BuildTrayMenu();
        tray = new NotifyIcon { Icon = IconArt.Load(SystemInformation.SmallIconSize) ?? Icon ?? SystemIcons.Application, Text = "OpenVPN Hızlı Arama", ContextMenuStrip = trayMenu, Visible = PresenceMode.TrayIcon(presence) };
        tray.MouseClick += delegate(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (OnScreen) HideForLater(); else { manualOverride = true; ShowAndFocus(true); }
        };
    }

    // Status card for the top of the menus: connection + last backup.
    MenuStatusItem StatusCard()
    {
        var connected = ConnectedProfile() ?? FortiConnectedProfile() ?? ExtConnectedProfile();
        var backup = settings.LastBackupUtc > DateTime.MinValue.AddYears(1) ? "Son yedek: " + Ui.Relative((long)(settings.LastBackupUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds) : "Henüz yedek alınmadı";
        if (connected != null)
            return new MenuStatusItem(connected.Name, (LiveTextFor(connected) ?? "Bağlı") + "  ·  " + backup, Ui.Success);
        return new MenuStatusItem("Bağlı değil", profiles.Count + " profil  ·  " + backup, Ui.Faint);
    }

    // Tray menu: quick connect to pinned + recent profiles without opening the panel.
    void BuildTrayMenu()
    {
        ClearMenu(trayMenu);
        trayMenu.Items.Add(StatusCard());
        var connected = ConnectedProfile();
        if (connected != null) trayMenu.Items.Add(Danger("Bağlantıyı kes", Glyph.Disconnect, delegate { Disconnect(); }));
        var fortiLive = FortiConnectedProfile();
        if (fortiLive != null) trayMenu.Items.Add(Danger("FortiClient bağlantısını kes", Glyph.Disconnect, delegate { DisconnectForti(fortiLive); }));
        AddExtDisconnectItems(trayMenu);
        trayMenu.Items.Add(Item("Paneli aç", Glyph.Open, delegate { manualOverride = true; ShowAndFocus(true); }, settings.Hotkey));
        var quick = profiles.Where(p => pins.Contains(p.Id)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Concat(profiles.Where(p => !pins.Contains(p.Id) && p.LastConnected > 0).OrderByDescending(p => p.LastConnected).Take(5))
            .Where(p => connected == null || p.Id != connected.Id).Take(10).ToList();
        if (quick.Count > 0)
        {
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(Caption("HIZLI BAĞLAN"));
            foreach (var p in quick)
            {
                var prof = p;
                var it = Item(p.Name, pins.Contains(p.Id) ? Glyph.Star : Glyph.Connect, delegate { Connect(prof); });
                if (p.LastConnected > 0 && !pins.Contains(p.Id)) it.Badge = Ui.Relative(p.LastConnected);
                trayMenu.Items.Add(it);
            }
        }
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(Item("Şimdi yedekle", Glyph.Backup, delegate { BackupNow(false); }));
        trayMenu.Items.Add(Item("Ayarlar", Glyph.Settings, delegate { ShowSettings(); }));
        trayMenu.Items.Add(Item("Çıkış", Glyph.Power, delegate { ExitApp(); }));
        MenuKit.Fit(trayMenu);
    }

    void ShowMainMenu()
    {
        ClearMenu(mainMenu);
        mainMenu.Items.Add(StatusCard());
        if (ConnectedProfile() != null) mainMenu.Items.Add(Danger("Bağlantıyı kes", Glyph.Disconnect, delegate { Disconnect(); }));
        var fortiOn = FortiConnectedProfile();
        if (fortiOn != null) mainMenu.Items.Add(Danger("FortiClient bağlantısını kes", Glyph.Disconnect, delegate { DisconnectForti(fortiOn); }));
        AddExtDisconnectItems(mainMenu);

        mainMenu.Items.Add(Caption("YEDEKLEME"));
        mainMenu.Items.Add(new MenuEntry("Şimdi yedekle", Glyph.Backup, delegate { BackupNow(false); }) { Badge = profiles.Count + " profil" });
        mainMenu.Items.Add(Item("Yedekten geri yükle…", Glyph.Restore, delegate { PickAndRestore(); }));
        mainMenu.Items.Add(Item(".ovpn dosyası içe aktar…", Glyph.Import, delegate { PickAndImport(); }));
        mainMenu.Items.Add(Item("Yedek klasörünü aç", Glyph.Folder, delegate { OpenBackupFolder(); }));
        mainMenu.Items.Add(Item("VPN parolalarını yedekle (Windows)…", Glyph.Lock, delegate { BackupCredentials(); }));

        mainMenu.Items.Add(Caption("PROFİLLER"));
        var dupes = DuplicateFinder.Find(OpenVpnProfiles).Count;
        mainMenu.Items.Add(new MenuEntry("Yinelenen profiller…", Glyph.Duplicates, delegate { ShowDuplicates(); }) { Badge = dupes > 0 ? dupes + " grup" : null });
        mainMenu.Items.Add(Item("Bağlantı geçmişi…", Glyph.History, delegate { ShowHistory(); }));
        mainMenu.Items.Add(Item("Sunucuları yeniden test et", Glyph.Server, delegate { Reachability.CheckAll(profiles, true); SetStatus("Sunucular test ediliyor…"); }));
        mainMenu.Items.Add(Item("Profilleri yenile", Glyph.Refresh, delegate { ReloadProfiles(true); SetStatus(profiles.Count + " profil yüklendi"); }));

        mainMenu.Items.Add(new ToolStripSeparator());
        if (userMoved && ovpnHwnd != IntPtr.Zero) mainMenu.Items.Add(Item("OpenVPN'in yanına yapıştır", Glyph.Dock, delegate { userMoved = false; Sync(true); }));
        mainMenu.Items.Add(Item("Ayarlar", Glyph.Settings, delegate { ShowSettings(); }, "Ctrl+,"));
        mainMenu.Items.Add(Item("Kısayollar", Glyph.Keyboard, delegate { ShowShortcuts(); }));
        mainMenu.Items.Add(Item("Çıkış", Glyph.Power, delegate { ExitApp(); }));
        MenuKit.Fit(mainMenu);
        mainMenu.Show(moreButton, new Point(moreButton.Width, moreButton.Height + S(4)), ToolStripDropDownDirection.BelowLeft);
    }

    void ShowRowMenu(VpnProfile p, Point at)
    {
        ClearMenu(rowMenu);
        var head = new MenuStatusItem(p.Name, (p.Host.Length > 0 ? p.Host : "Sunucu adresi yok") + (p.Username.Length > 0 ? "  ·  " + p.Username : ""), Ui.Faint)
        { Initials = p.Initials, AvatarColor = Ui.AvatarColor(p.Name) };
        rowMenu.Items.Add(head);
        if (p.Id == connectedId) rowMenu.Items.Add(Danger("Bağlantıyı kes", Glyph.Disconnect, delegate { Disconnect(); }));
        else if (p.Id == fortiConnectedId) rowMenu.Items.Add(Danger("Bağlantıyı kes", Glyph.Disconnect, delegate { DisconnectForti(p); }));
        else if (ExtFor(p) != null && p.Id == ExtFor(p).ConnectedId) rowMenu.Items.Add(Danger("Bağlantıyı kes", Glyph.Disconnect, delegate { DisconnectExt(ExtFor(p), p); }));
        else rowMenu.Items.Add(Item("Bağlan", Glyph.Connect, delegate { Connect(p); }, "Enter"));
        rowMenu.Items.Add(Item(pins.Contains(p.Id) ? "Sabitlemeyi kaldır" : "Sabitle", Glyph.Star, delegate { TogglePin(p); }, "Ctrl+P"));
        rowMenu.Items.Add(Caption("KOPYALA"));
        var copyHost = Item("Sunucu adresi", Glyph.Copy, delegate { Copy(p.Host, "Sunucu adresi kopyalandı"); }); copyHost.Enabled = p.Host.Length > 0; rowMenu.Items.Add(copyHost);
        var copyUser = Item("Kullanıcı adı", Glyph.Copy, delegate { Copy(p.Username, "Kullanıcı adı kopyalandı"); }); copyUser.Enabled = p.Username.Length > 0; rowMenu.Items.Add(copyUser);
        rowMenu.Items.Add(Item("Profil adı", Glyph.Copy, delegate { Copy(p.Name, "Profil adı kopyalandı"); }));
        rowMenu.Items.Add(new ToolStripSeparator());
        if (p.Id.StartsWith(FortiStore.IdPrefixSsl, StringComparison.Ordinal))
        {
            var hasCred = false;
            try { hasCred = FortiCredentialStore.Has(FortiStore.TunnelName(p)); } catch { }
            rowMenu.Items.Add(Item(hasCred ? "Kayıtlı parolayı değiştir…" : "Parolayı kaydet…", Glyph.Settings, delegate { EditFortiCredential(p); }));
            if (hasCred) rowMenu.Items.Add(Danger("Kayıtlı parolayı sil", Glyph.Delete, delegate { DeleteFortiCredential(p); }));
        }
        if (ExtFor(p) != null && !Demo.On)
        {
            var hasCred = false;
            try { hasCred = FortiCredentialStore.Has(VpnProviders.CredentialKey(p), VpnProviders.CredentialPrefix(ExtFor(p).Kind)); } catch { }
            rowMenu.Items.Add(Item(hasCred ? "Kayıtlı parolayı değiştir…" : "Parolayı kaydet…", Glyph.Settings, delegate { EditExtCredential(p); }));
            if (hasCred) rowMenu.Items.Add(Danger("Kayıtlı parolayı sil", Glyph.Delete, delegate { DeleteExtCredential(p); }));
        }
        if (VpnProviders.IsOpenVpn(p)) rowMenu.Items.Add(Item(".ovpn olarak dışa aktar…", Glyph.Export, delegate { ExportProfile(p); }));
        if (VpnProviders.IsOpenVpn(p)) rowMenu.Items.Add(Danger("Profili sil…", Glyph.Delete, delegate { DeleteProfile(p); }));
        MenuKit.Fit(rowMenu);
        rowMenu.Show(list, at);
    }

    // ---------- Connection status ----------

    VpnProfile FortiConnectedProfile()
    {
        if (fortiConnectedId == null) return null;
        return profiles.FirstOrDefault(p => p.Id == fortiConnectedId);
    }

    VpnProfile ConnectedProfile()
    {
        if (connectedId == null) return null;
        return profiles.FirstOrDefault(p => p.Id == connectedId);
    }

    string LiveTextFor(VpnProfile p)
    {
        if (fortiConnectedId != null && p.Id == fortiConnectedId) return "Bağlı · " + Since(fortiSince);
        var ext = ExtFor(p);
        if (ext != null && p.Id == ext.ConnectedId) return "Bağlı · " + Since(ext.Since);
        if (connectedId == null || p.Id != connectedId || monitor == null) return null;
        var since = monitor.Current.SinceUtc;
        if (since == DateTime.MinValue || since > DateTime.UtcNow) return "Bağlı";
        return "Bağlı · " + Since(since);
    }

    static string Since(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalMinutes < 1 ? "şimdi" : d.TotalHours < 1 ? (int)d.TotalMinutes + " dk" : (int)d.TotalHours + " sa " + d.Minutes + " dk";
    }

    List<VpnProfile> OpenVpnProfiles { get { return profiles.Where(VpnProviders.IsOpenVpn).ToList(); } }

    // ---------- FortiClient ----------

    // Forti sessions started since this app started: profile id -> start (Unix ms).
    readonly Dictionary<string, long> fortiSeen = new Dictionary<string, long>(StringComparer.Ordinal);

    // Writes a finished FortiClient session to the connection history (same file as OpenVPN).
    void RecordFortiSession(string id, DateTime startUtc, DateTime endUtc)
    {
        var p = profiles.FirstOrDefault(x => x.Id == id);
        try
        {
            ConnectionHistory.Append(new HistoryEntry { ProfileId = id, ProfileName = p != null ? p.Name : FortiStore.TunnelName(new VpnProfile { Id = id, Name = id }), StartUtc = startUtc, EndUtc = endUtc });
        }
        catch { }
    }

    string fortiConnectedId, fortiConnectingId, fortiDisconnectingId; DateTime fortiSince, fortiConnectStart, fortiDisconnectStart; bool fortiPolling, fortiOpening;

    // Shows FortiClient's window (it may be closed to the tray) and presses its Disconnect button.
    // OnFortiState finishes the animation when the Fortinet adapter goes down.
    void DisconnectForti(VpnProfile p)
    {
        if (Demo.On) { DemoConnect(null); return; } // demo rows: never touch the real FortiClient
        if (p == null || fortiDisconnectingId != null) return;
        fortiDisconnectingId = p.Id; fortiDisconnectStart = DateTime.UtcNow;
        AppLog.Info("forti", "disconnect requested");
        list.BeginDisconnect(p.Id);
        SetStatus("FortiClient \"" + p.Name + "\" kesiliyor…");
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err = null;
            var outcome = FortiGui.Outcome.NotReady;
            try
            {
                err = FortiStore.ShowGui(true);
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (err == null && DateTime.UtcNow < deadline)
                {
                    string detail;
                    outcome = FortiGui.TryDisconnect(true, out detail);
                    if (outcome == FortiGui.Outcome.Pressed) break;
                    Thread.Sleep(500);
                }
            }
            catch (Exception ex) { err = ex.Message; CrashReporter.HandledError("forti", "disconnect", ex); }
            UiInvoke(delegate
            {
                if (outcome == FortiGui.Outcome.Pressed) AppLog.Info("forti", "disconnect outcome: pressed");
                else AppLog.Warn("forti", "disconnect outcome: " + (err != null ? "failed" : "button not found"));
                if (outcome == FortiGui.Outcome.Pressed) { ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(4000); FortiStore.CloseParkedGui(); }); return; }
                QuietWindow.Reveal(true);
                fortiDisconnectingId = null; list.EndDisconnect();
                SetStatus(err ?? "FortiClient'ta Disconnect bulunamadı — bağlantıyı FortiClient'tan kesin");
            });
        });
    }

    void ConnectForti(VpnProfile p)
    {
        if (p.Id == fortiConnectedId) { list.ConnectSucceeded(p.Id); SetStatus("\"" + p.Name + "\" zaten bağlı"); return; }
        if (fortiOpening) return;
        // SSL tunnels whose gateway does not let FortiClient keep the password: use the credential
        // saved in Windows Credential Manager, or ask for it once.
        FortiCredential cred = null;
        var tunnel = FortiStore.TunnelName(p);
        var ssl = p.Id.StartsWith(FortiStore.IdPrefixSsl, StringComparison.Ordinal);
        if (ssl && !FortiCredentialStore.FortiKeepsPassword(tunnel))
        {
            try { cred = FortiCredentialStore.Load(tunnel); } catch { cred = null; }
            if (cred == null) cred = AskFortiCredential(p, null);
        }
        if (cred != null) CrashReporter.RememberUser(cred.Username);
        AppLog.Info("forti", "connect requested (" + (ssl ? "ssl" : "ipsec") + (cred != null ? ", saved credential" : "") + ")");
        fortiOpening = true;
        fortiConnectingId = p.Id; fortiConnectStart = DateTime.UtcNow;
        list.BeginConnect(p.Id);
        SetStatus("FortiClient'ta \"" + p.Name + "\" seçiliyor…");
        // Open() may restart the FortiClient window (a few seconds), so it runs off the UI thread.
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err;
            try { err = FortiStore.Open(p, true); } catch (Exception ex) { err = ex.Message; CrashReporter.HandledError("forti", "open tunnel", ex); }
            var outcome = FortiGui.Outcome.NotReady;
            if (err == null) outcome = PressFortiConnect(tunnel, cred);
            if (cred != null) cred.Password = "";
            UiInvoke(delegate
            {
                fortiOpening = false;
                if (err != null) AppLog.Warn("forti", "connect outcome: open failed");
                else AppLog.Info("forti", "connect outcome: " + outcome);
                if (outcome != FortiGui.Outcome.Pressed) QuietWindow.Reveal(true); // the user has to act in FortiClient
                if (err != null) { if (fortiConnectingId == p.Id) fortiConnectingId = null; list.ConnectFailed(p.Id); SetStatus(err); return; }
                if (outcome == FortiGui.Outcome.Pressed) { fortiAuth = true; SetStatus("FortiClient \"" + p.Name + "\" bağlanıyor…"); }
                else if (outcome == FortiGui.Outcome.NeedsPassword) SetStatus("FortiClient'ta \"" + p.Name + "\" seçildi — parolayı girip Connect'e basın");
                else if (outcome == FortiGui.Outcome.FillFailed) SetStatus("FortiClient formu doldurulamadı — \"" + p.Name + "\" için parolayı elle girin");
                else SetStatus("FortiClient'ta \"" + p.Name + "\" seçildi — Connect'e basın");
            });
        });
    }

    // FortiClient automation steps (formerly forti.log): now lines of the app log with the "forti"
    // source tag. Outcome only: never the tunnel name, the username or the password.
    static void FortiTrace(string line) { AppLog.Info("forti", line); }

    // FortiGui's detail without anything identifying: "seçili: <tunnel>" loses the name, quotes go.
    static string TraceDetail(string detail)
    {
        if (string.IsNullOrEmpty(detail)) return "";
        if (detail.StartsWith("seçili", StringComparison.Ordinal)) return " (tunnel selected)";
        return " (" + LogSanitizer.StripQuoted(detail) + ")";
    }

    // Runs on a worker thread after FortiStore.Open(): waits up to 20 s for FortiClient's form to
    // show the selected tunnel, then presses Connect when the password is saved. Any other outcome
    // leaves the window to the user, as before.
    static FortiGui.Outcome PressFortiConnect(string tunnel, FortiCredential cred)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var outcome = FortiGui.Outcome.NotReady;
        string lastDetail = "";
        while (DateTime.UtcNow < deadline)
        {
            string detail = "";
            try { outcome = FortiGui.TryConnect(tunnel, cred, true, out detail); }
            catch (Exception ex) { outcome = FortiGui.Outcome.NotReady; detail = ex.GetType().Name; }
            if (outcome == FortiGui.Outcome.Pressed || outcome == FortiGui.Outcome.NeedsPassword || outcome == FortiGui.Outcome.FillFailed)
            {
                FortiTrace("connect form: " + outcome + TraceDetail(detail));
                return outcome;
            }
            lastDetail = detail;
            Thread.Sleep(500);
        }
        FortiTrace("connect form: " + outcome + " after 20 s" + TraceDetail(lastDetail));
        return outcome;
    }

    // Asks for a tunnel's username and password; saves them when the user keeps "Bu bilgisayarda
    // sakla" ticked. Returns null when cancelled (FortiClient then asks, as before).
    FortiCredential AskFortiCredential(VpnProfile p, FortiCredential current)
    {
        using (var f = new FortiCredentialForm(FortiStore.TunnelName(p), current))
        {
            if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return null;
            if (f.Remember)
            {
                var err = FortiCredentialStore.Save(FortiStore.TunnelName(p), f.Result);
                if (err != null) SetStatus(err);
            }
            return f.Result;
        }
    }

    void EditFortiCredential(VpnProfile p)
    {
        FortiCredential current = null;
        try { current = FortiCredentialStore.Load(FortiStore.TunnelName(p)); } catch { }
        if (current != null) current.Password = ""; // never show the saved password again
        var c = AskFortiCredentialSaveOnly(p, current);
        if (c != null) SetStatus("\"" + p.Name + "\" için parola kaydedildi");
    }

    FortiCredential AskFortiCredentialSaveOnly(VpnProfile p, FortiCredential current)
    {
        using (var f = new FortiCredentialForm(FortiStore.TunnelName(p), current, true))
        {
            if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return null;
            var err = FortiCredentialStore.Save(FortiStore.TunnelName(p), f.Result);
            if (err != null) { SetStatus(err); return null; }
            f.Result.Password = "";
            return f.Result;
        }
    }

    void DeleteFortiCredential(VpnProfile p)
    {
        if (FortiCredentialStore.Delete(FortiStore.TunnelName(p))) SetStatus("\"" + p.Name + "\" için kayıtlı parola silindi");
        else SetStatus("Kayıtlı parola silinemedi");
    }

    // Adapter enumeration takes ~50-100 ms, so it runs on a worker thread and only every 10 s
    // (every 2 s while a FortiClient connection is being started).
    void PollForti()
    {
        if (fortiPolling) return;
        fortiPolling = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            string name = null;
            try { name = FortiStore.ConnectedTunnel(); }
            catch (Exception ex) { AppLog.Limited(LogLevel.Warn, "poll:forti", "forti", "poll failed: " + ex.GetType().Name, null); }
            UiInvoke(delegate { fortiPolling = false; OnFortiState(name); });
        });
    }

    void OnFortiState(string tunnel)
    {
        string id = null;
        if (!string.IsNullOrEmpty(tunnel))
        {
            var match = profiles.Where(FortiStore.IsForti).FirstOrDefault(p => string.Equals(FortiStore.TunnelName(p), tunnel, StringComparison.CurrentCultureIgnoreCase));
            if (match != null) id = match.Id;
        }
        if (fortiConnectingId != null && id == null && (DateTime.UtcNow - fortiConnectStart).TotalSeconds > 25 && QuietWindow.IsParked) QuietWindow.Reveal(true); // password / token prompt or an error
        if (fortiConnectingId != null && (DateTime.UtcNow - fortiConnectStart).TotalSeconds > 120)
        {
            AppLog.Warn("forti", "connect outcome: not confirmed in 120 s");
            list.CancelConnect(fortiConnectingId); fortiConnectingId = null;
        }
        if (fortiDisconnectingId != null && (id == null || (DateTime.UtcNow - fortiDisconnectStart).TotalSeconds > 60))
        {
            if (id == null)
            {
                var gone = profiles.FirstOrDefault(p => p.Id == fortiDisconnectingId);
                if (gone != null) SetStatus("\"" + gone.Name + "\" (FortiClient) bağlantısı kesildi");
            }
            fortiDisconnectingId = null; list.EndDisconnect();
        }
        if (id == fortiConnectedId) return;
        AppLog.Info("forti", id != null ? "state: connected" + (id == fortiConnectingId ? " (requested)" : "") : "state: disconnected");
        var now = DateTime.UtcNow;
        if (fortiConnectedId != null) RecordFortiSession(fortiConnectedId, fortiSince, now);
        fortiConnectedId = id;
        if (id != null)
        {
            fortiSince = now;
            var ms = (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            fortiSeen[id] = ms;
            var live = profiles.FirstOrDefault(p => p.Id == id);
            if (live != null) live.LastConnected = ms;
        }
        var sel = list.SelectedProfile;
        ApplyFilter(sel != null ? sel.Id : null);
        if (id != null) ThreadPool.QueueUserWorkItem(delegate { FortiStore.CloseParkedGui(); });
        if (id != null && id == fortiConnectingId) { fortiConnectingId = null; list.ConnectSucceeded(id); }
        fortiAuth = fortiConnectingId != null && fortiAuth;
        var p2 = profiles.FirstOrDefault(p => p.Id == id);
        if (p2 != null) SetStatus("\"" + p2.Name + "\" (FortiClient) bağlandı");
        InvalidateFooter(); UpdateIsland();
    }

    // ---------- Sophos Connect / WatchGuard SSL VPN ----------
    // Both follow the FortiClient flow with one shared code path: the connect / disconnect command
    // runs on the thread pool (sccli, or WatchGuard's dialog driven off-screen, up to ~25 s), the
    // connected profile is found by polling on a worker thread (PollExt), and OnExtState moves the
    // list animation, island, history and "last connected" time. Demo mode never gets here.

    sealed class ExtVpn
    {
        public VpnKind Kind;
        public string ConnectedId, ConnectingId, DisconnectingId;
        public DateTime Since, ConnectStart, DisconnectStart, LastPoll = DateTime.MinValue;
        public DateTime CommandDone = DateTime.MinValue; // last connect / disconnect command finished (polls started before it are stale)
        public int NoneStreak;                          // consecutive "none connected" polls while a row is shown connected
        public bool Polling, Busy, PollSoon;
        public readonly Dictionary<string, long> Seen = new Dictionary<string, long>(StringComparer.Ordinal); // id -> start (Unix ms) since app start
        public string Label { get { return VpnProviders.Label(Kind); } }
        public string Log { get { return Kind == VpnKind.Sophos ? "sophos" : "watchguard"; } } // app log source tag
        public bool InFlight { get { return ConnectingId != null || DisconnectingId != null || Busy; } }
    }

    readonly ExtVpn sophos = new ExtVpn { Kind = VpnKind.Sophos }, watchGuard = new ExtVpn { Kind = VpnKind.WatchGuard };
    bool sophosInstalled, watchGuardInstalled, installChanged; // refreshed by ReloadProfiles (file checks)

    IEnumerable<ExtVpn> Exts { get { return new[] { sophos, watchGuard }; } }

    ExtVpn ExtFor(VpnProfile p)
    {
        var k = VpnProviders.KindOf(p);
        return k == VpnKind.Sophos ? sophos : k == VpnKind.WatchGuard ? watchGuard : null;
    }

    bool ExtEnabled(ExtVpn x)
    {
        if (Demo.On) return false;
        return x.Kind == VpnKind.Sophos ? settings.ShowSophos && sophosInstalled : settings.ShowWatchGuard && watchGuardInstalled;
    }

    VpnProfile ExtProfile(ExtVpn x)
    {
        if (x.ConnectedId == null) return null;
        return profiles.FirstOrDefault(p => p.Id == x.ConnectedId);
    }

    VpnProfile ExtConnectedProfile() { return ExtProfile(sophos) ?? ExtProfile(watchGuard); }

    void AddExtDisconnectItems(ContextMenuStrip menu)
    {
        foreach (var x in Exts)
        {
            var live = ExtProfile(x); var ext = x;
            if (live != null) menu.Items.Add(Danger(x.Label + " bağlantısını kes", Glyph.Disconnect, delegate { DisconnectExt(ext, live); }));
        }
    }

    // Writes a finished Sophos / WatchGuard session to the connection history (same file as OpenVPN).
    void RecordExtSession(string id, DateTime startUtc, DateTime endUtc)
    {
        var p = profiles.FirstOrDefault(x => x.Id == id);
        try { ConnectionHistory.Append(new HistoryEntry { ProfileId = id, ProfileName = p != null ? p.Name : id, StartUtc = startUtc, EndUtc = endUtc }); }
        catch { }
    }

    void ConnectExt(ExtVpn x, VpnProfile p)
    {
        if (Demo.On) { DemoConnect(p.Name, true); return; }
        if (p.Id == x.ConnectedId) { list.ConnectSucceeded(p.Id); SetStatus("\"" + p.Name + "\" zaten bağlı"); return; }
        if (x.Busy || x.ConnectingId == p.Id) return;
        // Saved credential (Credential Manager, provider's own prefix), or ask once. Cancel = connect
        // without: Sophos tries the connection as configured, WatchGuard uses its own remembered
        // password or shows its dialog to the user.
        var prefix = VpnProviders.CredentialPrefix(x.Kind);
        FortiCredential cred = null;
        try { cred = FortiCredentialStore.Load(VpnProviders.CredentialKey(p), prefix); } catch { cred = null; }
        if (cred == null) cred = AskExtCredential(x, p, string.IsNullOrEmpty(p.Username) ? null : new FortiCredential { Username = p.Username, Password = "" });
        if (cred != null) CrashReporter.RememberUser(cred.Username);
        AppLog.Info(x.Log, "connect requested" + (cred != null ? " (with credential)" : ""));
        x.Busy = true; x.ConnectingId = p.Id; x.ConnectStart = DateTime.UtcNow;
        list.BeginConnect(p.Id);
        SetStatus(x.Label + " \"" + p.Name + "\" bağlanıyor…");
        ThreadPool.QueueUserWorkItem(delegate
        {
            string msg = null;
            var outcome = WatchGuardStore.ConnectOutcome.Failed;
            try
            {
                if (x.Kind == VpnKind.Sophos)
                {
                    msg = SophosStore.Connect(p, cred); // sccli: no window of its own
                    outcome = msg == null ? WatchGuardStore.ConnectOutcome.Pressed : WatchGuardStore.ConnectOutcome.Failed;
                }
                else outcome = WatchGuardStore.Connect(p, cred, out msg); // parks its own windows off-screen
            }
            catch (Exception ex) { msg = ex.Message; outcome = WatchGuardStore.ConnectOutcome.Failed; CrashReporter.HandledError(x.Log, "connect", ex); }
            finally { if (cred != null) cred.Password = ""; }
            UiInvoke(delegate { OnExtConnectResult(x, p, outcome, msg); });
        });
    }

    void OnExtConnectResult(ExtVpn x, VpnProfile p, WatchGuardStore.ConnectOutcome outcome, string msg)
    {
        x.Busy = false; x.PollSoon = true; x.CommandDone = DateTime.UtcNow;
        if (outcome == WatchGuardStore.ConnectOutcome.Failed) AppLog.Warn(x.Log, "connect outcome: Failed");
        else AppLog.Info(x.Log, "connect outcome: " + outcome);
        if (outcome == WatchGuardStore.ConnectOutcome.Failed)
        {
            if (x.ConnectingId == p.Id) x.ConnectingId = null;
            list.ConnectFailed(p.Id);
            SetStatus(string.IsNullOrEmpty(msg) ? x.Label + " bağlantısı başlatılamadı" : msg);
            return;
        }
        if (outcome == WatchGuardStore.ConnectOutcome.AlreadyConnected) { OnExtState(x, VpnProviders.CredentialKey(p)); return; }
        // Pressed: wait for the poll to see it up. ShownToUser: the client's window is on screen for
        // the user (password / SAML); the row keeps "connecting" until it is up or the attempt expires.
        if (outcome == WatchGuardStore.ConnectOutcome.ShownToUser) SetStatus(string.IsNullOrEmpty(msg) ? x.Label + " penceresinde \"" + p.Name + "\" için bağlanın" : msg);
        else SetStatus(x.Label + " \"" + p.Name + "\" bağlanıyor…");
    }

    void DisconnectExt(ExtVpn x, VpnProfile p)
    {
        if (Demo.On) { DemoConnect(null); return; }
        if (p == null || x.DisconnectingId != null) return;
        x.DisconnectingId = p.Id; x.DisconnectStart = DateTime.UtcNow;
        AppLog.Info(x.Log, "disconnect requested");
        list.BeginDisconnect(p.Id);
        SetStatus(x.Label + " \"" + p.Name + "\" kesiliyor…");
        var name = SophosStore.ConnectionName(p);
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err;
            try
            {
                if (x.Kind == VpnKind.Sophos) err = SophosStore.Disconnect(name);
                else err = WatchGuardStore.Disconnect();
            }
            catch (Exception ex) { err = ex.Message; CrashReporter.HandledError(x.Log, "disconnect", ex); }
            UiInvoke(delegate
            {
                if (err == null) AppLog.Info(x.Log, "disconnect outcome: command ok");
                else AppLog.Warn(x.Log, "disconnect outcome: failed");
                x.PollSoon = true; x.CommandDone = DateTime.UtcNow; // a poll started before this must not mark the row connected again
                if (err == null)
                {
                    // sccli disable only succeeds once the connection is down; WatchGuard is confirmed by the next poll
                    if (x.Kind == VpnKind.Sophos && x.DisconnectingId == p.Id) OnExtState(x, null);
                    return;
                }
                x.DisconnectingId = null; list.EndDisconnect();
                SetStatus(err);
            });
        });
    }

    // Asks for a connection's username and password; saves them when "Bu bilgisayarda sakla" stays ticked.
    FortiCredential AskExtCredential(ExtVpn x, VpnProfile p, FortiCredential current)
    {
        var key = VpnProviders.CredentialKey(p);
        using (var f = new FortiCredentialForm(key, current, false, x.Label, "Bağlanırken " + x.Label + " bu bilgileri kullanır."))
        {
            if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return null;
            if (f.Remember)
            {
                var err = FortiCredentialStore.Save(key, f.Result, VpnProviders.CredentialPrefix(x.Kind));
                if (err != null) SetStatus(err);
            }
            return f.Result;
        }
    }

    void EditExtCredential(VpnProfile p)
    {
        var x = ExtFor(p); if (x == null) return;
        var key = VpnProviders.CredentialKey(p); var prefix = VpnProviders.CredentialPrefix(x.Kind);
        FortiCredential current = null;
        try { current = FortiCredentialStore.Load(key, prefix); } catch { }
        if (current != null) current.Password = ""; // never show the saved password again
        else if (!string.IsNullOrEmpty(p.Username)) current = new FortiCredential { Username = p.Username, Password = "" };
        using (var f = new FortiCredentialForm(key, current, true, x.Label))
        {
            if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
            var err = FortiCredentialStore.Save(key, f.Result, prefix);
            f.Result.Password = "";
            SetStatus(err ?? "\"" + p.Name + "\" için parola kaydedildi");
        }
    }

    void DeleteExtCredential(VpnProfile p)
    {
        var x = ExtFor(p); if (x == null) return;
        if (FortiCredentialStore.Delete(VpnProviders.CredentialKey(p), VpnProviders.CredentialPrefix(x.Kind))) SetStatus("\"" + p.Name + "\" için kayıtlı parola silindi");
        else SetStatus("Kayıtlı parola silinemedi");
    }

    // Called from the 500 ms watcher. Sophos: sccli spawns a process per connection, so at most every
    // 10 s; WatchGuard: every 2 s while a command is in flight, otherwise every 10 s. Only while the
    // panel is visible or something of that provider is connected / in flight (like FortiClient).
    void MaybePollExt(ExtVpn x)
    {
        if (x.Polling || !ExtEnabled(x)) return;
        if (!OnScreen && x.ConnectedId == null && !x.InFlight) return;
        if (!profiles.Any(p => ExtFor(p) == x)) return;
        if (!VpnProviders.DuePoll(x.Kind, x.InFlight, x.PollSoon, (DateTime.UtcNow - x.LastPoll).TotalSeconds)) return;
        var started = DateTime.UtcNow;
        x.Polling = true; x.PollSoon = false; x.LastPoll = started;
        ThreadPool.QueueUserWorkItem(delegate
        {
            string key = null; var ok = false;
            try { ok = x.Kind == VpnKind.Sophos ? SophosStore.TryConnectedName(out key) : WatchGuardStore.TryConnectedServer(out key); }
            catch (Exception ex) { ok = false; key = null; AppLog.Limited(LogLevel.Warn, "poll-ex:" + x.Log, x.Log, "poll failed: " + ex.GetType().Name, null); }
            if (!ok) AppLog.Limited(LogLevel.Warn, "poll:" + x.Log, x.Log, "poll returned no state", null);
            UiInvoke(delegate { x.Polling = false; x.LastPoll = DateTime.UtcNow; OnExtPoll(x, ok, key, started); });
        });
    }

    // A failed poll, a stale one (started before the last command finished) and a single "none"
    // while a row is shown connected keep the shown state (VpnProviders.UsePoll); only the
    // give-up timers still run then.
    void OnExtPoll(ExtVpn x, bool ok, string key, DateTime started)
    {
        if (VpnProviders.UsePoll(ok, key, x.ConnectedId != null, x.DisconnectingId != null, started < x.CommandDone, ref x.NoneStreak) == VpnProviders.PollUse.Apply)
        {
            OnExtState(x, key);
            return;
        }
        var now = DateTime.UtcNow;
        if (x.ConnectingId != null && x.ConnectingId != x.ConnectedId && !x.Busy && (now - x.ConnectStart).TotalSeconds > VpnProviders.ConnectGiveUpSeconds)
        {
            AppLog.Warn(x.Log, "connect outcome: not confirmed in time");
            list.CancelConnect(x.ConnectingId); x.ConnectingId = null;
            SetStatus(x.Label + " bağlantısı doğrulanamadı — " + x.Label + " penceresini kontrol edin");
        }
        if (x.DisconnectingId != null && (now - x.DisconnectStart).TotalSeconds > VpnProviders.DisconnectGiveUpSeconds) { x.DisconnectingId = null; list.EndDisconnect(); }
        UpdateIsland();
    }

    // key: connection name (Sophos) / server entry (WatchGuard) that is up, "" = up but unnamed, null = none.
    void OnExtState(ExtVpn x, string key)
    {
        x.NoneStreak = 0;
        var owned = profiles.Where(p => ExtFor(p) == x).ToList();
        var id = VpnProviders.ResolveConnectedId(key, owned, VpnProviders.CredentialKey, x.ConnectedId, x.ConnectingId);
        var now = DateTime.UtcNow;
        if (x.ConnectingId != null && id != x.ConnectingId && !x.Busy && (now - x.ConnectStart).TotalSeconds > VpnProviders.ConnectGiveUpSeconds)
        {
            AppLog.Warn(x.Log, "connect outcome: not confirmed in time");
            list.CancelConnect(x.ConnectingId); x.ConnectingId = null;
            SetStatus(x.Label + " bağlantısı doğrulanamadı — " + x.Label + " penceresini kontrol edin");
        }
        if (x.DisconnectingId != null && (id == null || (now - x.DisconnectStart).TotalSeconds > VpnProviders.DisconnectGiveUpSeconds))
        {
            if (id == null)
            {
                var gone = profiles.FirstOrDefault(p => p.Id == x.DisconnectingId);
                if (gone != null) SetStatus("\"" + gone.Name + "\" (" + x.Label + ") bağlantısı kesildi");
            }
            x.DisconnectingId = null; list.EndDisconnect();
        }
        if (id == x.ConnectedId) { UpdateIsland(); return; }
        AppLog.Info(x.Log, id != null ? "state: connected" + (id == x.ConnectingId ? " (requested)" : "") : "state: disconnected");
        if (x.ConnectedId != null) RecordExtSession(x.ConnectedId, x.Since, now);
        x.ConnectedId = id;
        if (id != null)
        {
            x.Since = now;
            var ms = (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            x.Seen[id] = ms;
            var live = profiles.FirstOrDefault(p => p.Id == id);
            if (live != null) live.LastConnected = ms;
        }
        var sel = list.SelectedProfile;
        ApplyFilter(sel != null ? sel.Id : null);
        if (id != null && id == x.ConnectingId) { x.ConnectingId = null; list.ConnectSucceeded(id); }
        var p2 = profiles.FirstOrDefault(p => p.Id == id);
        if (p2 != null) SetStatus("\"" + p2.Name + "\" (" + x.Label + ") bağlandı");
        InvalidateFooter(); UpdateIsland();
    }

    // Provider switched off in Settings: its rows go away, so does its state (like FortiClient).
    void ForgetExt(ExtVpn x)
    {
        if (x.ConnectingId != null) list.CancelConnect(x.ConnectingId);
        if (x.DisconnectingId != null) list.EndDisconnect();
        x.ConnectedId = null; x.ConnectingId = null; x.DisconnectingId = null; x.NoneStreak = 0;
    }

    // Sophos connections come from sccli (a process per call): listed on a worker thread and
    // cached; ReloadProfiles merges the cache, and a changed list reloads the profiles once.
    List<KeyValuePair<string, string>> sophosCache = new List<KeyValuePair<string, string>>(); // name, host
    bool sophosLoading; DateTime sophosLoadedUtc = DateTime.MinValue;

    void RefreshSophosAsync()
    {
        if (sophosLoading || Demo.On) return;
        sophosLoading = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            var next = new List<KeyValuePair<string, string>>();
            try { foreach (var p in SophosStore.Load()) next.Add(new KeyValuePair<string, string>(SophosStore.ConnectionName(p), p.Host)); }
            catch (Exception ex) { AppLog.Limited(LogLevel.Warn, "list:sophos", "sophos", "connection list failed: " + ex.GetType().Name, null); }
            UiInvoke(delegate
            {
                sophosLoading = false; sophosLoadedUtc = DateTime.UtcNow;
                var same = next.Count == sophosCache.Count;
                for (var i = 0; same && i < next.Count; i++) same = next[i].Key == sophosCache[i].Key && next[i].Value == sophosCache[i].Value;
                if (same) return;
                sophosCache = next;
                ReloadProfiles();
            });
        });
    }

    void OnConnectionChanged()
    {
        var c = monitor.Current;
        var id = c != null && c.Connected ? c.ProfileId : null;
        if (id == connectedId) return;
        AppLog.Info("openvpn", id != null ? "state: connected" + (id == connectingProfileId ? " (requested)" : "") : "state: disconnected");
        connectedId = id;
        var selected = list.SelectedProfile;
        ApplyFilter(selected != null ? selected.Id : null);
        if (id == null) list.EndDisconnect();
        if (id == null && disconnectStarted != DateTime.MinValue) QuietWindow.ForgetMinimized(); // disconnected: OpenVPN stays minimized
        if (id != null && id == connectingProfileId)
        {
            QuietWindow.ForgetMinimized(); // connected without the user: OpenVPN stays minimized
            // the profile we asked for is up: play the success animation
            connectingProfileId = null;
            list.ConnectSucceeded(id);
            if (!docked) { var t = new System.Windows.Forms.Timer { Interval = 1800 }; t.Tick += delegate { t.Stop(); t.Dispose(); if (!docked) HideForLater(); }; t.Start(); }
        }
        if (id != null && c.ProfileName != null) SetStatus("\"" + c.ProfileName + "\" bağlandı");
        tray.Text = id != null && c.ProfileName != null ? Trim63("OpenVPN Hızlı Arama · Bağlı: " + c.ProfileName) : "OpenVPN Hızlı Arama";
        InvalidateFooter(); UpdateIsland();
    }

    static void ClearMenu(ToolStrip m) { var old = new List<ToolStripItem>(); foreach (ToolStripItem it in m.Items) old.Add(it); m.Items.Clear(); foreach (var it in old) it.Dispose(); }

    static string Trim63(string s) { return s.Length > 63 ? s.Substring(0, 62) + "…" : s; }

    void Disconnect()
    {
        string err;
        var id = connectedId;
        if (ConnectionControl.Disconnect(out err)) { AppLog.Info("openvpn", "disconnect requested"); SetStatus("Bağlantı kesiliyor…"); if (id != null) list.BeginDisconnect(id); disconnectStarted = DateTime.UtcNow; ovpnWindowBack = false; }
        else { AppLog.Warn("openvpn", "disconnect request failed"); GlassDialog.Show(this, err ?? "Bağlantı kesilemedi.", "OpenVPN Hızlı Arama", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // ---------- Backup / restore / import ----------

    string BackupFolder { get { return string.IsNullOrWhiteSpace(settings.BackupFolder) ? BackupService.DefaultFolder : settings.BackupFolder; } }

    void BackupNow(bool silent)
    {
        if (busy) return;
        busy = true;
        var folder = BackupFolder; var pinCopy = new List<string>(pins); var keep = Math.Max(1, settings.BackupKeep);
        if (!silent) SetStatus("Yedekleniyor…");
        ThreadPool.QueueUserWorkItem(delegate
        {
            string zip = null, error = null; int count = 0;
            try { zip = BackupService.CreateBackup(folder, pinCopy, out count); BackupService.Prune(folder, keep); }
            catch (Exception ex) { error = ex.Message; }
            UiInvoke(delegate
            {
                busy = false;
                if (error != null)
                {
                    if (silent) SetStatus("Otomatik yedek alınamadı: " + error);
                    else GlassDialog.Show(this, "Yedek alınamadı:\n" + error, "OpenVPN Hızlı Arama", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                settings.LastBackupUtc = DateTime.UtcNow; SaveSettings();
                if (openSettings != null) openSettings.SetLastBackup(settings.LastBackupUtc, count);
                SetStatus(count + " profil yedeklendi · " + Path.GetFileName(zip));
                if (!silent && tray != null && tray.Visible) tray.ShowBalloonTip(4000, "Yedek alındı", count + " profil yedeklendi.\n" + zip, ToolTipIcon.Info);
            });
        });
    }

    void CheckAutoBackup()
    {
        if (busy) return;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(ProfileStore.ConfigPath); } catch { return; }
        if (BackupService.AutoBackupDue(settings.LastBackupUtc, settings.BackupFrequency, stamp)) BackupNow(true);
    }

    void OpenBackupFolder()
    {
        try { Directory.CreateDirectory(BackupFolder); Process.Start("explorer.exe", "\"" + BackupFolder + "\""); }
        catch (Exception ex) { SetStatus("Klasör açılamadı: " + ex.Message); }
    }

    // OpenVPN Connect (and FortiClient) keep saved passwords in Windows Credential Manager, not in
    // the profile files. Windows' own credwiz.exe exports them to a password-protected .crd file;
    // we launch that wizard rather than touching the stored secrets ourselves.
    void BackupCredentials()
    {
        var info = "Kayıtlı VPN parolalarınız profil dosyalarında değil, Windows Kimlik Bilgileri Yöneticisi'nde tutulur.\n\n" +
                   "Şimdi açılacak Windows sihirbazında \"Kimlik bilgilerini yedekle\" ile parolalarınızı, kendi belirlediğiniz bir parolayla şifreli bir .crd dosyasına yedekleyebilirsiniz. Aynı sihirbaz başka bir bilgisayarda \"Geri yükle\" ile içeri alır.\n\nDevam edilsin mi?";
        if (GlassDialog.Show(this, info, "VPN parolalarını yedekle", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        try { Process.Start(new ProcessStartInfo { FileName = "credwiz.exe", UseShellExecute = true }); }
        catch (Exception ex)
        {
            try { Process.Start(new ProcessStartInfo { FileName = "control.exe", Arguments = "/name Microsoft.CredentialManager", UseShellExecute = true }); }
            catch { SetStatus("Windows Kimlik Bilgileri Yöneticisi açılamadı: " + ex.Message); }
        }
    }

    void ExportProfile(VpnProfile p)
    {
        using (var dlg = new SaveFileDialog { Filter = "OpenVPN profili (*.ovpn)|*.ovpn", FileName = SafeName(p.Name) + ".ovpn", Title = "Profili dışa aktar" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { BackupService.ExportProfile(p.Id, dlg.FileName); SetStatus("\"" + p.Name + "\" dışa aktarıldı"); }
            catch (Exception ex) { GlassDialog.Show(this, "Dışa aktarılamadı:\n" + ex.Message, "OpenVPN Hızlı Arama", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "profil" : s;
    }

    void PickAndRestore()
    {
        using (var dlg = new OpenFileDialog { Filter = "OpenVPN Hızlı Arama yedeği (*.zip)|*.zip", Title = "Yedekten geri yükle", InitialDirectory = Directory.Exists(BackupFolder) ? BackupFolder : "" })
            if (dlg.ShowDialog(this) == DialogResult.OK) RestoreBackup(dlg.FileName);
    }

    void PickAndImport()
    {
        using (var dlg = new OpenFileDialog { Filter = "OpenVPN profili (*.ovpn)|*.ovpn", Multiselect = true, Title = ".ovpn dosyası içe aktar" })
            if (dlg.ShowDialog(this) == DialogResult.OK) ImportOvpns(dlg.FileNames, null, null);
    }

    void RestoreBackup(string zip)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "OvpnQS-" + Guid.NewGuid().ToString("N"));
        List<string> files, pinnedNames;
        try { files = ImportService.ExtractBackup(zip, tmp, out pinnedNames); }
        catch (Exception ex) { ImportService.CleanupTemp(tmp); GlassDialog.Show(this, "Yedek okunamadı:\n" + ex.Message, "OpenVPN Hızlı Arama", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        ImportOvpns(files, tmp, pinnedNames, ImportService.ReadUsernames(zip));
    }

    // Imports .ovpn files (from a backup when tempDir is set) on a worker thread; existing names are skipped.
    void ImportOvpns(IList<string> files, string tempDir, List<string> pinnedNames) { ImportOvpns(files, tempDir, pinnedNames, null); }

    void ImportOvpns(IList<string> files, string tempDir, List<string> pinnedNames, IDictionary<string, string> usernames)
    {
        if (busy) { if (tempDir != null) ImportService.CleanupTemp(tempDir); return; }
        var existing = new HashSet<string>(OpenVpnProfiles.Select(p => p.Name), StringComparer.CurrentCultureIgnoreCase);
        var fresh = files.Count(f => !existing.Contains(Path.GetFileNameWithoutExtension(f)));
        if (fresh == 0)
        {
            if (tempDir != null) ImportService.CleanupTemp(tempDir);
            GlassDialog.Show(this, "Seçilen " + files.Count + " profilin hepsi OpenVPN Connect'te zaten var.", "İçe aktarma", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var ask = files.Count + " profil bulundu. " + fresh + " tanesi OpenVPN Connect'e eklenecek, " + (files.Count - fresh) + " tanesi zaten var ve atlanacak.\n\n" +
                  "Kayıtlı parolalar yedeğe dahil değildir; ilk bağlantıda parola sorulabilir.\n\nDevam edilsin mi?";
        if (GlassDialog.Show(this, ask, "İçe aktarma", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            if (tempDir != null) ImportService.CleanupTemp(tempDir);
            return;
        }
        busy = true; SetStatus("İçe aktarılıyor… (" + fresh + " profil)");
        ThreadPool.QueueUserWorkItem(delegate
        {
            ImportResult r = null; string error = null;
            try { r = ImportService.ImportFiles(files, existing, usernames); }
            catch (Exception ex) { error = ex.Message; }
            finally { if (tempDir != null) ImportService.CleanupTemp(tempDir); }
            UiInvoke(delegate
            {
                busy = false;
                ReloadProfiles(true);
                if (pinnedNames != null && pinnedNames.Count > 0)
                {
                    foreach (var p in profiles) if (pinnedNames.Contains(p.Name, StringComparer.CurrentCultureIgnoreCase)) pins.Add(p.Id);
                    PinStore.Save(pins); ApplyFilter(null);
                }
                if (error != null) { GlassDialog.Show(this, "İçe aktarma başarısız:\n" + error, "İçe aktarma", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                var msg = r.Imported + " profil eklendi, " + r.Skipped + " atlandı, " + r.Failed + " başarısız.";
                var details = r.Messages != null && r.Messages.Count > 0 ? "\n\n" + string.Join("\n", r.Messages.Take(15).ToArray()) : "";
                GlassDialog.Show(this, msg + details, "İçe aktarma tamamlandı", MessageBoxButtons.OK, r.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            });
        });
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effect = files != null && files.Any(f => f.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void OnDragDrop(object sender, DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null) return;
        var zip = files.FirstOrDefault(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var ovpns = files.Where(f => f.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase)).ToList();
        // Defer so the drag source (Explorer) is not blocked by our dialogs.
        BeginInvoke(new Action(delegate
        {
            if (ovpns.Count > 0) ImportOvpns(ovpns, null, null);
            else if (zip != null) RestoreBackup(zip);
        }));
    }

    void DeleteProfile(VpnProfile p)
    {
        if (GlassDialog.Show(this, "\"" + p.Name + "\" profili OpenVPN Connect'ten kalıcı olarak silinecek.\nBu işlem geri alınamaz (yedeğiniz varsa geri yükleyebilirsiniz).\n\nDevam edilsin mi?",
            "Profili sil", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var err = RemoveProfile(p);
        if (err != null) GlassDialog.Show(this, "Profil silinemedi:\n" + err, "Profili sil", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else { SetStatus("\"" + p.Name + "\" silindi"); ReloadSoon(); }
    }

    static string RemoveProfile(VpnProfile p)
    {
        string err;
        return ImportService.RemoveProfile(p.Id, out err) ? null : (err ?? "Bilinmeyen hata");
    }

    // OpenVPN Connect writes config.json shortly after a change; reload a few times.
    void ReloadSoon()
    {
        var t = new System.Windows.Forms.Timer { Interval = 1200 }; var n = 0;
        t.Tick += delegate { ReloadProfiles(true); if (++n >= 3) { t.Stop(); t.Dispose(); } };
        t.Start();
    }

    void ShowDuplicates()
    {
        var groups = DuplicateFinder.Find(OpenVpnProfiles);
        using (var dlg = new DuplicatesForm(groups, RemoveProfile))
        {
            dlg.ShowDialog(this);
            if (dlg.RemovedCount > 0) { SetStatus(dlg.RemovedCount + " yinelenen profil silindi"); ReloadSoon(); }
        }
    }

    void ShowHistory() { using (var f = new HistoryForm()) f.ShowDialog(this); }

    // ---------- Settings / theme / hotkey ----------

    void SaveSettings() { try { SettingsStore.Save(settings); } catch (Exception ex) { SetStatus("Ayarlar kaydedilemedi: " + ex.Message); } }

    // ---------- Updates (modules\Updater.cs) ----------
    // One schedule inside the 500 ms watcher, no extra polling thread: the first check about 30 s
    // after start, then once a minute "is a check due?" (UpdateLogic.IsDue). The network work runs
    // on the thread pool. Never in demo mode. A failed automatic check is silent.
    //   ask : the UpdateNotice offers Şimdi güncelle / Sonra / Bu sürümü atla.
    //   auto: download + verify in the background, install once the app is idle (UpdateLogic.IsIdle).
    //   off : only "Şimdi denetle" in the settings.
    // A copy outside the install dir only notifies (release page).

    bool updateChecking, updateDownloading, updateCheckedThisRun;
    DateTime updateFailUtc = DateTime.MinValue;
    ReleaseInfo updateRelease;          // newest offered release found in this run
    string updatePackage;               // verified, extracted package waiting to be installed
    bool updateUserAsked;               // "Şimdi güncelle": install once nothing is in flight, restart in the foreground
    string updateBadVersion;            // a version whose download / verification failed: not retried this run
    UpdateNotice updateNotice;

    bool IsInstalledCopy { get { return UpdateLogic.IsInstalledCopy(Application.ExecutablePath, UpdateLogic.InstallDir); } }

    void UpdateTick()
    {
        if (exiting || tick < 60) return;                                    // ~30 s after start
        if (updatePackage != null && tick % 10 == 0) InstallPending();       // every 5 s while a package waits
        if (tick % 120 != 60) return;                                        // once a minute
        if (settings.UpdateMode == "off" || updateChecking || updateDownloading || updatePackage != null) return;
        if (!UpdateLogic.IsDue(settings.UpdateInterval, settings.LastUpdateCheckUtc, updateFailUtc, updateCheckedThisRun, DateTime.UtcNow)) return;
        CheckForUpdates(false);
    }

    void CheckForUpdates(bool manual)
    {
        if (Demo.On) { ReportUpdate("Demo modunda güncelleme denetlenmez"); return; }
        if (updateChecking) return;
        updateChecking = true;
        AppLog.Info("update", "check started (" + (manual ? "manual" : "scheduled") + ")");
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err; var r = UpdateClient.FetchLatest(out err);
            if (!manual) UpdateClient.CleanupOld();
            UiInvoke(delegate { OnUpdateChecked(r, err, manual); });
        });
    }

    void ReportUpdate(string text)
    {
        if (openSettings != null) openSettings.SetUpdateResult(text, settings.LastUpdateCheckUtc);
    }

    void OnUpdateChecked(ReleaseInfo r, string err, bool manual)
    {
        updateChecking = false; updateCheckedThisRun = true;
        if (r == null)
        {
            AppLog.Warn("update", "check failed: " + LogSanitizer.StripQuoted(err ?? "unknown"));
            if (!manual) { updateFailUtc = DateTime.UtcNow; return; }   // silent; retried at the next due time
            ReportUpdate("Denetlenemedi: " + (err ?? "bilinmeyen hata"));
            SetStatus("Güncelleme denetlenemedi");
            return;
        }
        updateFailUtc = DateTime.MinValue;
        settings.LastUpdateCheckUtc = DateTime.UtcNow; SaveSettings();
        var current = UpdateLogic.CurrentVersion;
        if (!UpdateLogic.ShouldOffer(r, current, settings.SkippedVersion, manual))
        {
            AppLog.Info("update", "check: up to date (current " + current + ", latest " + r.Version + ")");
            if (manual) { ReportUpdate("Güncel (" + current + ")"); SetStatus("Uygulama güncel"); }
            return;
        }
        updateRelease = r;
        AppLog.Info("update", "check: version " + r.Version + " available (mode " + settings.UpdateMode + ")");
        if (openSettings != null) openSettings.ReleaseNotesUrl = r.HtmlUrl;
        if (manual) ReportUpdate("Sürüm " + r.Version + " hazır");
        // A version whose install failed in the last 24 h (marker left by kur.ps1) is not installed automatically again.
        if (!manual && settings.UpdateMode == "auto" && IsInstalledCopy && r.CanInstall && r.Version != updateBadVersion &&
            !UpdateLogic.FailureMarkerBlocks(UpdateClient.ReadFailureMarker(UpdateLogic.InstallDir), r.Version, DateTime.UtcNow))
        {
            StartUpdateDownload(r, false);
            return;
        }
        ShowUpdateNotice(r);
    }

    void ShowUpdateNotice(ReleaseInfo r)
    {
        if (exiting) return;
        if (updateNotice != null && !updateNotice.IsDisposed) updateNotice.Close();
        var n = new UpdateNotice(r, UpdateLogic.CurrentVersion, IsInstalledCopy);
        n.UpdateNow += delegate { StartUpdateDownload(r, true); };
        n.Skip += delegate { settings.SkippedVersion = r.Version; SaveSettings(); SetStatus("Sürüm " + r.Version + " atlandı"); };
        n.Notes += delegate { UpdateClient.OpenReleasePage(r.HtmlUrl); };
        n.FormClosed += delegate { if (updateNotice == n) updateNotice = null; n.Dispose(); };
        updateNotice = n;
        n.Show();
    }

    // userAsked: "Şimdi güncelle" (install right after verification, restart in the foreground).
    // Otherwise (auto mode) the package waits for an idle moment and the app restarts with --background.
    void StartUpdateDownload(ReleaseInfo r, bool userAsked)
    {
        if (updateDownloading || Demo.On) return;
        if (updatePackage != null) { if (userAsked) { updateUserAsked = true; InstallPending(); } return; }
        updateDownloading = true;
        if (userAsked) SetStatus("Sürüm " + r.Version + " indiriliyor…");
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err; var pkg = UpdateClient.Download(r, out err);
            UiInvoke(delegate
            {
                updateDownloading = false;
                if (pkg == null) AppLog.Warn("update", "download / verification failed: " + LogSanitizer.StripQuoted(err));
                else AppLog.Info("update", "package " + r.Version + " downloaded and verified");
                if (pkg == null)
                {
                    updateBadVersion = r.Version;
                    SetStatus("Güncelleme başarısız: " + err);
                    ReportUpdate("Güncelleme başarısız: " + err);
                    return;
                }
                updatePackage = pkg; updateUserAsked = userAsked;
                InstallPending();
            });
        });
    }

    // Nothing in flight that an exit would cut short, and no client window parked off-screen by us.
    // A connected VPN is fine (the clients run on their own).
    bool UpdateBusy
    {
        get
        {
            return busy || list.IsConnecting || list.IsDisconnecting || connectingProfileId != null || disconnectStarted != DateTime.MinValue ||
                   fortiConnectingId != null || fortiDisconnectingId != null || Exts.Any(x => x.InFlight) || updateChecking ||
                   QuietWindow.IsParked || WatchGuardGui.AnyParked; // a client window is still off-screen
        }
    }

    // Any other window of ours open (settings, dialogs, history, password prompt); the notice does not count.
    bool UpdateDialogOpen
    {
        get { return openSettings != null || Application.OpenForms.Cast<Form>().Any(f => f != this && f != updateNotice && f.Visible); }
    }

    // User asked ("Şimdi güncelle"): install once no connect / disconnect is in flight and no dialog
    // (e.g. the settings window) is open, then restart in the foreground.
    // Auto mode: install only when idle (UpdateLogic.IsIdle), then restart with --background.
    void InstallPending()
    {
        if (updatePackage == null || exiting || updateInstalling) return;
        if (updateUserAsked)
        {
            if (UpdateBusy || UpdateDialogOpen) { ReportUpdate("Güncelleme, pencereler kapanınca kurulacak"); return; }
            InstallUpdate(false);
            return;
        }
        if (settings.UpdateMode != "auto") return;
        if (!UpdateLogic.IsIdle(OnScreen, UpdateClient.IdleMilliseconds(), UpdateBusy, UpdateDialogOpen)) return;
        InstallUpdate(true);
    }

    bool updateInstalling; // StartInstall is waiting for the helper on a worker thread

    void InstallUpdate(bool background)
    {
        if (updatePackage == null || exiting || updateInstalling) return;
        if (!IsInstalledCopy) { updatePackage = null; return; }   // guarded before download; never replace a portable copy
        // StartInstall waits up to 10 s for kur.ps1 to report ready: off the UI thread.
        updateInstalling = true;
        var pkg = updatePackage; var version = updateRelease != null ? updateRelease.Version : null;
        var pid = Process.GetCurrentProcess().Id;
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err = null; var ok = false;
            try { ok = UpdateClient.StartInstall(pkg, UpdateLogic.InstallDir, version, pid, background, out err); }
            catch (Exception ex) { ok = false; err = ex.Message; CrashReporter.HandledError("update", "start install", ex); }
            UiInvoke(delegate
            {
                updateInstalling = false;
                if (ok) { AppLog.Info("update", "installer started for " + (version ?? "?") + ", exiting"); AppLog.Flush(); }
                else AppLog.Warn("update", "install start failed: " + LogSanitizer.StripQuoted(err));
                if (!ok)
                {
                    updatePackage = null; updateBadVersion = version;
                    SetStatus("Güncelleme başlatılamadı: " + err);
                    return;
                }
                // kur.ps1 -Update waits for this process to end, copies the new files and starts the app again.
                ExitApp();
            });
        });
    }

    void ShowSettings()
    {
        if (openSettings != null) { openSettings.Activate(); return; }
        if (!OnScreen) { manualOverride = true; ShowAndFocus(true); }
        settings.AutoStart = AutoStart.IsEnabled();
        using (var f = new SettingsForm(settings))
        {
            f.ProfileCount = profiles.Count;
            f.BackupNow = delegate { BackupNow(false); };
            f.OpenBackupFolder = OpenBackupFolder;
            f.CheckUpdates = delegate { CheckForUpdates(true); };
            f.OpenLogFolder = CrashReporter.OpenLogFolder;
            f.ReportLastError = CrashReporter.ReportLatest;
            if (updateRelease != null) f.ReleaseNotesUrl = updateRelease.HtmlUrl;
            f.Icon = Icon;
            openSettings = f;
            var promptBefore = settings.CrashPrompt;   // "Bir daha sorma" may be chosen while the window is open
            DialogResult dr;
            try { dr = f.ShowDialog(this); } finally { openSettings = null; }
            if (dr != DialogResult.OK || f.Result == null) { ApplyTheme(); return; } // undo live theme preview
            var r = f.Result;
            r.LastBackupUtc = settings.LastBackupUtc; // a backup may have run while the window was open
            r.LastUpdateCheckUtc = settings.LastUpdateCheckUtc; r.SkippedVersion = settings.SkippedVersion; // same for an update check
            if (settings.CrashPrompt != promptBefore && r.CrashPrompt == promptBefore) { r.CrashPrompt = settings.CrashPrompt; r.CrashAutoConsent = settings.CrashAutoConsent; } // kept, not undone by the window's copy
            var old = settings;
            settings = r;
            SaveSettings();
            if (r.AutoStart != AutoStart.IsEnabled()) try { AutoStart.SetEnabled(r.AutoStart); } catch (Exception ex) { SetStatus("Başlangıç ayarı değiştirilemedi: " + ex.Message); }
            if (r.LogEnabled != old.LogEnabled)
            {
                if (!r.LogEnabled) AppLog.Info("app", "logging turned off by the user");
                AppLog.Enabled = r.LogEnabled;
                if (r.LogEnabled) AppLog.Info("app", "logging turned on by the user");
            }
            if (r.Hotkey != old.Hotkey) { RegisterAppHotkey(true); search.Hotkey = r.Hotkey; }
            if (r.Presence != old.Presence) ApplyPresence();
            ApplyTheme();
            if (r.DockSide != old.DockSide) { autoSide = 0; lastRect = new NativeMethods.RECT(); docked = false; Sync(true); }
            if (r.ReachabilityCheck && !old.ReachabilityCheck) Reachability.CheckAll(profiles, false);
            if (r.ShowForti != old.ShowForti) { if (!r.ShowForti) fortiConnectedId = null; ReloadProfiles(true); }
            if (r.ShowSophos != old.ShowSophos || r.ShowWatchGuard != old.ShowWatchGuard)
            {
                if (!r.ShowSophos) ForgetExt(sophos);
                if (!r.ShowWatchGuard) ForgetExt(watchGuard);
                ReloadProfiles(true);
            }
            list.Invalidate();
            SetStatus("Ayarlar kaydedildi");
        }
    }

    void RegisterAppHotkey(bool report)
    {
        if (!IsHandleCreated) return;
        if (hotkeyRegistered) { NativeMethods.UnregisterHotKey(Handle, HotkeyId); hotkeyRegistered = false; }
        uint mods, vk;
        if (!HotkeyText.TryParse(settings.Hotkey, out mods, out vk)) { mods = MOD_CONTROL | MOD_ALT; vk = (uint)Keys.V; }
        hotkeyRegistered = NativeMethods.RegisterHotKey(Handle, HotkeyId, mods | MOD_NOREPEAT, vk);
        if (report) SetStatus(hotkeyRegistered ? "Kısayol: " + settings.Hotkey : settings.Hotkey + " başka bir uygulama tarafından kullanılıyor");
    }

    void ApplyTheme()
    {
        Themes.Apply(settings.Theme);
        ForeColor = Ui.Text;
        search.RefreshTheme();
        search.Hotkey = settings.Hotkey;
        RefreshSegments();
        ApplyFrameStyle();
        UpdateIsland();
        Invalidate(true);
    }

    void UiInvoke(Action a)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
    }

    void Copy(string text, string message)
    {
        try { Clipboard.SetText(text); SetStatus(message); } catch { SetStatus("Panoya kopyalanamadı"); }
    }

    void ShowShortcuts()
    {
        GlassDialog.Show(this,
            settings.Hotkey + "\tPaneli aç / gizle (her yerden)\n" +
            "↑ ↓  PgUp PgDn\tProfil seç\n" +
            "Enter\t\tSeçili profile bağlan\n" +
            "Ctrl+P\t\tSeçili profili sabitle / kaldır\n" +
            "Ctrl+F\t\tAramaya dön\n" +
            "Esc\t\tAramayı temizle, sonra gizle\n\n" +
            "Panel OpenVPN Connect penceresine yapışık çalışır ve onunla birlikte hareket eder.\n" +
            "Üst şeritten sürükleyerek ayırabilir, OpenVPN'in kenarına bırakarak yeniden yapıştırabilirsiniz.\n" +
            "Profile sağ tıklayarak sunucu adresini veya kullanıcı adını kopyalayabilirsiniz.", "Kısayollar");
    }

    // ---------- Window lifecycle ----------

    protected override bool ShowWithoutActivation { get { return noActivate; } }

    protected override void SetVisibleCore(bool value)
    {
        // Background mode starts hidden without flashing the window.
        if (!allowVisible) { value = false; if (!IsHandleCreated) CreateHandle(); }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterAppHotkey(false);
        ApplyFrameStyle();
    }

    void ApplyFrameStyle()
    {
        if (!IsHandleCreated) return;
        try
        {
            Backdrop.Apply(Handle, !Themes.IsLight);       // compositor acrylic on Windows 11 22H2+, opaque fallback material elsewhere
            Backdrop.SetCorner(Handle, docked ? 3 : 2);    // DWM rounding: antialiased edge and native shadow
        }
        catch { }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (hotkeyRegistered) { NativeMethods.UnregisterHotKey(Handle, HotkeyId); hotkeyRegistered = false; }
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            if (OnScreen && ContainsFocus) HideForLater();
            else { manualOverride = true; ShowAndFocus(true); }
            return;
        }
        if (m.Msg == WM_SIZE)
        {
            // Minimized to / restored from the taskbar button (taskbar mode: Esc, the header buttons,
            // the button itself, Win+D ...). Restore work runs after WinForms has updated WindowState.
            var minimized = m.WParam.ToInt32() == SIZE_MINIMIZED;
            if (minimized && !wasIconic) { wasIconic = true; ambient.Stop(); trimTimer.Stop(); trimTimer.Start(); }
            else if (!minimized && wasIconic) { wasIconic = false; BeginInvoke(new Action(OnRestored)); }
        }
        if (m.Msg == WM_NCHITTEST) { base.WndProc(ref m); if (m.Result.ToInt32() == HTCLIENT) m.Result = (IntPtr)HitTest(m.LParam); return; }
        if (m.Msg == WM_MOVING) userMoved = true;               // user dragged the panel off OpenVPN
        if (m.Msg == WM_SIZING && !docked) floatHeight = Height;
        base.WndProc(ref m);
        if (m.Msg == WM_EXITSIZEMOVE) OnMoveFinished();
    }

    int HitTest(IntPtr lParam)
    {
        var v = lParam.ToInt64();
        var p = PointToClient(new Point((short)(v & 0xffff), (short)((v >> 16) & 0xffff)));
        int g = S(6), w = ClientSize.Width, h = ClientSize.Height;
        bool left = p.X < g, right = p.X >= w - g, top = p.Y < g, bottom = p.Y >= h - g;
        if (docked)
        {
            // Docked: height follows OpenVPN, only the outer edge resizes.
            if (dockSide > 0 && right) return HTRIGHT;
            if (dockSide < 0 && left) return HTLEFT;
        }
        else
        {
            if (top && left) return HTTOPLEFT; if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT; if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT; if (right) return HTRIGHT; if (top) return HTTOP; if (bottom) return HTBOTTOM;
        }
        return p.Y < HeaderHeight ? HTCAPTION : HTCLIENT;
    }

    // Dropping the panel next to OpenVPN docks it again.
    void OnMoveFinished()
    {
        if (!userMoved || ovpnHwnd == IntPtr.Zero) return;
        NativeMethods.RECT r;
        if (!NativeMethods.GetFrame(ovpnHwnd, out r)) return;
        var snap = S(40);
        var overlap = Bottom > r.Top && Top < r.Bottom;
        var toRight = Math.Abs(Left - r.Right) < snap; var toLeft = Math.Abs(Right - r.Left) < snap;
        if (overlap && (toRight || toLeft))
        {
            if (settings.DockSide == "auto") autoSide = toRight ? 1 : -1; // remember the side the user picked
            userMoved = false; lastRect = new NativeMethods.RECT(); docked = false; Sync(false);
        }
        else if (!docked) floatHeight = Height;
    }

    void OnFormClosingHandler(object sender, FormClosingEventArgs e)
    {
        // Tray / both: Alt+F4 only hides. Taskbar mode: closing the window (Alt+F4, the taskbar's
        // "Pencereyi kapat") exits like any application. Shutdown, logoff (WM_ENDSESSION),
        // Task Manager and "Çıkış" really close in every mode.
        if (exiting || e.CloseReason != CloseReason.UserClosing) return;
        if (PresenceMode.CloseExits(presence)) { exiting = true; AppLog.Info("app", "window closed by the user (taskbar mode): exiting"); return; }
        e.Cancel = true; HideForLater();
    }

    void Cleanup()
    {
        watcher.Stop(); statusTimer.Stop(); ambient.Stop(); fadeTimer.Stop(); trimTimer.Stop();
        if (monitor != null) { monitor.Dispose(); monitor = null; }
        if (processHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(processHook); processHook = IntPtr.Zero; }
        if (foregroundHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
        if (activationWait != null) { activationWait.Unregister(null); activationWait = null; }
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        // Client windows parked off-screen (FortiClient, WatchGuard) go back on screen: their watchers
        // run on pool threads that end with this process, so nothing else would move them back.
        try { QuietWindow.Reveal(false); } catch { }
        try { WatchGuardGui.RevealAllParked(); } catch { }
    }

    void ExitApp() { exiting = true; Close(); }

    void ShowAndFocus(bool activate)
    {
        if (exiting) return;
        allowVisible = true;
        if (!Visible)
        {
            userMoved = false; PlaceWindow();
            // fade in, and let the rows slide in
            list.AnimateNext = true; var sel = list.SelectedProfile; ApplyFilter(sel != null ? sel.Id : null);
            noActivate = !activate; Show(); noActivate = false;
            UpdateDockState();
            WakeAmbient();
        }
        if (Iconic)
        {
            // Minimized to the taskbar button: opening the panel docks it again, like showing it from
            // the tray (OnRestored finishes once the restore went through).
            userMoved = false;
            TaskbarButton.Restore(Handle, activate);
        }
        else if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (activate) { Activate(); NativeMethods.SetForegroundWindow(Handle); Input.Focus(); Input.SelectAll(); }
    }

    // "Hide" in every sense of the app (Esc, header buttons, hotkey, tray click, auto-hide after a
    // connect or when OpenVPN's window goes away): hidden to the tray, or minimized to the taskbar
    // button in taskbar mode (PresenceMode.HideMinimizes).
    void HideForLater()
    {
        manualOverride = false;
        if (PresenceMode.HideMinimizes(presence)) MinimizeToTaskbar(); else Hide();
        ambient.Stop();
        trimTimer.Stop(); trimTimer.Start(); // give memory back a few seconds after hiding
    }

    void MinimizeToTaskbar()
    {
        if (exiting) return;
        allowVisible = true;
        if (!Visible) { WindowState = FormWindowState.Minimized; noActivate = true; Show(); noActivate = false; return; }
        if (!Iconic) WindowState = FormWindowState.Minimized;
    }

    // Back from the taskbar button (its click, the hotkey, a second launch, OpenVPN reappearing).
    bool wasIconic, crashOfferPending;
    void OnRestored()
    {
        if (exiting || IsDisposed || !OnScreen) return;
        list.AnimateNext = true; var sel = list.SelectedProfile; ApplyFilter(sel != null ? sel.Id : null);
        UpdateDockState();
        WakeAmbient();
        if (crashOfferPending) { crashOfferPending = false; CrashReporter.OfferPendingCrash(this); }
    }

    // Last session's crash report is offered the first time the panel is on screen; a window that
    // starts minimized (taskbar mode, --background) waits for its first restore, so the question is
    // never a dialog owned by a minimized (and therefore hidden) window.
    void OfferCrashWhenOnScreen()
    {
        if (OnScreen) CrashReporter.OfferPendingCrash(this); else crashOfferPending = true;
    }

    // ---------- Profiles and filtering ----------

    void ReloadProfiles(bool force = false)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(ProfileStore.ConfigPath); } catch { stamp = DateTime.MinValue; }
        List<VpnProfile> loaded;
        try
        {
            loaded = Demo.On ? Demo.Profiles() : ProfileStore.Load();
            if (!Demo.On && settings.ShowForti)
            {
                var forti = FortiStore.Load();
                Dictionary<string, long> starts;
                try { starts = ConnectionHistory.LastStarts(ConnectionHistory.Load()); } catch { starts = new Dictionary<string, long>(); }
                foreach (var kv in fortiSeen) { long old; if (!starts.TryGetValue(kv.Key, out old) || kv.Value > old) starts[kv.Key] = kv.Value; }
                FortiStore.ApplyLastConnected(forti, starts);
                loaded.AddRange(forti);
            }
            // Sophos Connect (cached sccli list, refreshed on a worker) and WatchGuard (registry only).
            var wasInstalled = sophosInstalled + "," + watchGuardInstalled;
            sophosInstalled = !Demo.On && SophosStore.IsInstalled;
            watchGuardInstalled = !Demo.On && WatchGuardStore.IsInstalled;
            installChanged = wasInstalled != sophosInstalled + "," + watchGuardInstalled;
            var extra = new List<VpnProfile>();
            if (ExtEnabled(sophos))
            {
                foreach (var kv in sophosCache) extra.Add(SophosStore.MakeProfile(kv.Key, kv.Value));
                if (force || (DateTime.UtcNow - sophosLoadedUtc).TotalMinutes > 5) RefreshSophosAsync();
            }
            if (ExtEnabled(watchGuard)) extra.AddRange(WatchGuardStore.Load());
            if (extra.Count > 0)
            {
                Dictionary<string, long> starts;
                try { starts = ConnectionHistory.LastStarts(ConnectionHistory.Load()); } catch { starts = new Dictionary<string, long>(); }
                foreach (var x in Exts)
                    foreach (var kv in x.Seen) { long old; if (!starts.TryGetValue(kv.Key, out old) || kv.Value > old) starts[kv.Key] = kv.Value; }
                VpnProviders.ApplyLastConnected(extra, starts);
                loaded.AddRange(extra);
            }
        }
        catch (Exception ex)
        {
            // Keep the old list; configStamp is not updated, so the next check retries.
            loadError = ex.Message;
            if (profiles.Count == 0) ApplyFilter(null);
            return;
        }
        configStamp = stamp; loadError = null;
        var signature = string.Join("\u0002", loaded.Select(p => p.Id + "\u0001" + p.Name + "\u0001" + p.Host + "\u0001" + p.Username + "\u0001" + p.Source + "\u0001" + p.LastConnected).ToArray());
        if (!force && signature == profileSignature) { if (installChanged) { RefreshSegments(); ApplyFilter(list.SelectedProfile != null ? list.SelectedProfile.Id : null); } return; }
        profileSignature = signature; profiles = loaded;
        RefreshSegments();
        var selected = list.SelectedProfile;
        ApplyFilter(selected != null ? selected.Id : null);
        if (settings.ReachabilityCheck) Reachability.CheckAll(profiles, false);
    }

    void CheckConfigChanged()
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(ProfileStore.ConfigPath); } catch { return; }
        if (stamp != configStamp) ReloadProfiles();
    }

    static void AddGroup(List<ListRow> rows, string title, IEnumerable<VpnProfile> items, Glyph icon = Glyph.Layers)
    {
        var first = true;
        foreach (var p in items)
        {
            if (first && title != null) rows.Add(new ListRow { Header = title, Icon = icon });
            first = false;
            rows.Add(new ListRow { Profile = p });
        }
    }

    void ApplyFilter(string keepId)
    {
        var terms = ProfileStore.SearchKey(Input.Text).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        var byName = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), true);
        var rows = new List<ListRow>();
        var segment = VpnProviders.SegmentAt(segmentKinds, providerFilter);
        var pool = segment.HasValue ? profiles.Where(p => VpnProviders.InSegment(p, segment)).ToList() : profiles;
        if (terms.Length == 0)
        {
            var live = pool.Where(p => p.Id == connectedId || p.Id == fortiConnectedId || p.Id == sophos.ConnectedId || p.Id == watchGuard.ConnectedId).ToList();
            var liveIds = new HashSet<string>(live.Select(p => p.Id));
            var pinned = pool.Where(p => pins.Contains(p.Id) && !liveIds.Contains(p.Id)).OrderBy(p => p.Name, byName).ToList();
            var recent = pool.Where(p => !pins.Contains(p.Id) && !liveIds.Contains(p.Id) && p.LastConnected > 0).OrderByDescending(p => p.LastConnected).Take(5).ToList();
            var used = new HashSet<string>(live.Concat(pinned).Concat(recent).Select(p => p.Id));
            var rest = pool.Where(p => !used.Contains(p.Id) && VpnProviders.IsOpenVpn(p)).OrderBy(p => p.Name, byName).ToList();
            var others = new[] { VpnKind.Forti, VpnKind.Sophos, VpnKind.WatchGuard }
                .Select(k => pool.Where(p => !used.Contains(p.Id) && VpnProviders.KindOf(p) == k).OrderBy(p => p.Name, byName).ToList()).ToList();
            AddGroup(rows, "BAĞLI", live, Glyph.Plug);
            AddGroup(rows, "SABİTLENENLER", pinned, Glyph.Star);
            AddGroup(rows, "SON KULLANILAN", recent, Glyph.History);
            AddGroup(rows, others.Any(o => o.Count > 0) ? VpnProviders.GroupTitle(VpnKind.OpenVpn) : used.Count > 0 ? "TÜM PROFİLLER" : null, rest);
            foreach (var bucket in others) if (bucket.Count > 0) AddGroup(rows, VpnProviders.GroupTitle(VpnProviders.KindOf(bucket[0])), bucket, Glyph.ShieldLock);
            shownCount = pool.Count;
        }
        else
        {
            var hits = pool.Where(p => ProfileStore.Matches(p, terms))
                .OrderBy(p => ProfileStore.Score(p, terms)).ThenByDescending(p => pins.Contains(p.Id)).ThenByDescending(p => p.LastConnected).ThenBy(p => p.Name, byName).ToList();
            AddGroup(rows, hits.Count == 1 ? "1 SONUÇ" : hits.Count + " SONUÇ", hits, Glyph.Search);
            shownCount = hits.Count;
        }

        string title = "", hint = "";
        var emptySegment = VpnProviders.EmptySegmentText(segment);
        if (rows.Count == 0)
        {
            if (pool.Count == 0 && emptySegment != null) { title = emptySegment[0]; hint = emptySegment[1]; }
            else if (loadError != null) { title = "Profiller okunamadı"; hint = loadError; }
            else if (profiles.Count == 0) { title = "Kayıtlı profil yok"; hint = "OpenVPN Connect'e profil ekleyin; liste kendiliğinden güncellenir."; }
            else { title = "Sonuç bulunamadı"; hint = Input.Text.Trim().Length > 0 ? "\"" + Input.Text.Trim() + "\" ile eşleşen profil yok. Farklı bir kelime deneyin." : "Bu filtrede profil yok."; }
        }
        list.SetRows(rows, terms, title, hint, keepId);

        subtitle = loadError != null && profiles.Count == 0 ? "profil okunamadı"
            : terms.Length == 0 ? pool.Count + " profil"
            : shownCount + " / " + pool.Count;
        InvalidateHeader();
    }

    void TogglePin(VpnProfile p)
    {
        if (p == null) return;
        var pinned = !pins.Contains(p.Id);
        if (pinned) pins.Add(p.Id); else pins.Remove(p.Id);
        PinStore.Save(pins);
        ApplyFilter(p.Id); list.PulsePin(p.Id);
        SetStatus(pinned ? "\"" + p.Name + "\" sabitlendi" : "\"" + p.Name + "\" sabitlemesi kaldırıldı");
    }

    void ResetSearch() { Input.Text = ""; Input.Focus(); }

    // ---------- Keyboard ----------

    void OnFormKeyDown(object sender, KeyEventArgs e)
    {
        var handled = true;
        if (e.KeyCode == Keys.Escape) { if (Input.TextLength > 0) ResetSearch(); else HideForLater(); }
        else if (e.Control && e.KeyCode == Keys.F) { Input.Focus(); Input.SelectAll(); }
        else if (e.Control && e.KeyCode == Keys.P) TogglePin(list.SelectedProfile);
        else if (e.Control && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D5) { segmented.Selected = e.KeyCode - Keys.D1; }
        else if (e.Control && e.KeyCode == Keys.Oemcomma) BeginInvoke(new Action(ShowSettings));
        else if (e.KeyCode == Keys.Down) list.MoveSelection(1);
        else if (e.KeyCode == Keys.Up) list.MoveSelection(-1);
        else if (e.KeyCode == Keys.PageDown) list.MoveSelection(5);
        else if (e.KeyCode == Keys.PageUp) list.MoveSelection(-5);
        else if (e.KeyCode == Keys.Enter) Connect(list.SelectedProfile);
        else handled = false;
        if (handled) { e.Handled = true; e.SuppressKeyPress = true; if (!Input.Focused && Visible) Input.Focus(); }
    }

    // ---------- Connect ----------

    // Starts the connection and plays the "connecting" animation until ConnectionMonitor reports
    // the profile as connected (success animation) or ConnectTimeout passes (failure shake).
    void Connect(VpnProfile p)
    {
        if (p == null) return;
        if (Demo.On) { DemoConnect(p.Name, true); return; }
        if (FortiStore.IsForti(p)) { ConnectForti(p); return; }
        var ext = ExtFor(p);
        if (ext != null) { ConnectExt(ext, p); return; }
        if (p.Id == connectedId) { list.ConnectSucceeded(p.Id); SetStatus("\"" + p.Name + "\" zaten bağlı"); return; }
        if (p.Id == connectingProfileId) return;
        if (!File.Exists(openVpnPath)) { AppLog.Warn("openvpn", "connect: OpenVPN Connect not found"); list.ConnectFailed(p.Id); GlassDialog.Show(this, "OpenVPN Connect bulunamadı.", Text); return; }
        try
        {
            var arg = "--connect-shortcut=" + p.Id;
            if (p.Id.IndexOf(' ') >= 0) arg = "\"" + arg + "\"";
            QuietWindow.KeepProcessInBackground("OpenVPNConnect", 12);
            using (Process.Start(new ProcessStartInfo { FileName = openVpnPath, Arguments = arg, UseShellExecute = true })) { }
        }
        catch (Exception ex) { CrashReporter.HandledError("openvpn", "connect start", ex); list.ConnectFailed(p.Id); GlassDialog.Show(this, "Bağlantı başlatılamadı:\n" + ex.Message, Text); return; }
        AppLog.Info("openvpn", "connect requested");
        connectingProfileId = p.Id; connectStarted = DateTime.UtcNow; ovpnWindowBack = false;
        list.BeginConnect(p.Id);
        SetStatus("\"" + p.Name + "\" bağlanıyor…");
    }

    void CheckConnectTimeout()
    {
        // OpenVPN Connect is kept minimized while the command progresses. Still not there after a few
        // seconds: it probably waits for the user (password, static / MFA challenge), so the window
        // we minimized comes back to the front. A window the user minimized is never touched.
        if (!ovpnWindowBack)
        {
            var now = DateTime.UtcNow;
            var due = connectingProfileId != null ? QuietWindow.ShouldRestoreOpenVpnWindow((now - connectStarted).TotalSeconds, false)
                    : disconnectStarted != DateTime.MinValue && QuietWindow.ShouldRestoreOpenVpnWindow((now - disconnectStarted).TotalSeconds, connectedId == null);
            if (due)
            {
                ovpnWindowBack = true;
                if (QuietWindow.RestoreMinimized()) SetStatus("OpenVPN Connect sizden bilgi bekliyor olabilir — penceresine bakın");
            }
        }
        if (connectingProfileId != null && (DateTime.UtcNow - connectStarted).TotalSeconds > ConnectTimeoutSeconds)
        {
            AppLog.Warn("openvpn", "connect outcome: not confirmed in " + ConnectTimeoutSeconds + " s");
            list.ConnectFailed(connectingProfileId); connectingProfileId = null;
            SetStatus("Bağlantı doğrulanamadı — OpenVPN Connect penceresini kontrol edin");
        }
        if (disconnectStarted != DateTime.MinValue && (DateTime.UtcNow - disconnectStarted).TotalSeconds > 20) { disconnectStarted = DateTime.MinValue; list.EndDisconnect(); }
    }

    // ---------- OpenVPN window tracking ----------

    void TrackOpenVpn(IntPtr h)
    {
        ovpnHwnd = h;
        uint pid; NativeMethods.GetWindowThreadProcessId(h, out pid);
        if (pid == hookedPid && processHook != IntPtr.Zero) return;
        if (processHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(processHook);
        // Live events from the OpenVPN process: minimize, show/hide, destroy, move/resize.
        processHook = NativeMethods.SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, hookProc, pid, 0, 0);
        hookedPid = pid;
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (exiting) return;
        if (ev == EVENT_SYSTEM_FOREGROUND)
        {
            if (hwnd != IntPtr.Zero && hwnd == ovpnHwnd) { Sync(false); if (docked && OnScreen) RaiseAbove(); }
            WakeAmbient();
            return;
        }
        // Another OpenVPN window became visible (e.g. reopened from the tray): rescan right away.
        if (ev == 0x8002 && idObject == 0 && hwnd != ovpnHwnd)
        {
            var now = DateTime.UtcNow;
            if ((now - lastShowScan).TotalMilliseconds > 300) { lastShowScan = now; Sync(true); }
            return;
        }
        if (hwnd != ovpnHwnd || idObject != 0) return;
        if (ev == EVENT_OBJECT_LOCATIONCHANGE || ev == EVENT_SYSTEM_MINIMIZESTART || ev == EVENT_SYSTEM_MINIMIZESTART + 1 || ev == EVENT_OBJECT_DESTROY || ev == 0x8002 || ev == 0x8003)
            Sync(false);
    }

    // Put the panel directly above OpenVPN in the z-order (without taking focus), so other apps
    // covering OpenVPN also cover the panel and OpenVPN coming to the front brings the panel along.
    void RaiseAbove()
    {
        if (ovpnHwnd == IntPtr.Zero || !IsHandleCreated) return;
        var above = NativeMethods.GetWindow(ovpnHwnd, 3); // GW_HWNDPREV
        if (above == Handle) return;
        NativeMethods.SetWindowPos(Handle, above == IntPtr.Zero ? IntPtr.Zero : above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    // When the panel is clicked, pull OpenVPN up right behind it so the pair stays together.
    void KeepOpenVpnBelow()
    {
        if (docked && ovpnHwnd != IntPtr.Zero)
            NativeMethods.SetWindowPos(ovpnHwnd, Handle, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    // 500 ms heartbeat: window tracking every tick, connection status every 2 s,
    // config reload every 2 s, auto backup check every minute, server re-test every 10 minutes.
    void WatcherTick(object sender, EventArgs e)
    {
        tick++;
        var busyConnecting = connectingProfileId != null || disconnectStarted != DateTime.MinValue;
        // poll the connection fast while a connect/disconnect is in flight, otherwise every 2 s
        if (!Demo.On && (busyConnecting || tick % 4 == 0) && monitor != null) monitor.Poll();
        if (busyConnecting) CheckConnectTimeout();
        UpdateIsland();
        if (tick % 4 == 0) CheckConfigChanged();
        if (!Demo.On && tick % 120 == 20) CheckAutoBackup();
        if (settings.ReachabilityCheck && tick % 1200 == 0) Reachability.CheckAll(profiles, true);
        if (!OnScreen && tick % 600 == 300) NativeMethods.TrimMemory(); // every 5 min while hidden / minimized
        if (!Demo.On && settings.ShowForti && (fortiConnectingId != null || fortiDisconnectingId != null ? tick % 4 == 2 : tick % 20 == 10) && (OnScreen || fortiConnectedId != null) && profiles.Any(FortiStore.IsForti)) PollForti();
        if (!Demo.On) { MaybePollExt(sophos); MaybePollExt(watchGuard); }
        if (!Demo.On && tick % 600 == 450 && OnScreen && ExtEnabled(sophos)) RefreshSophosAsync(); // new / removed Sophos connections, every 5 min
        if (!Demo.On) UpdateTick();

        // The process scan (~8 ms, allocates) only runs when the tracked window is gone, and at most every 2 s.
        // Otherwise the cached handle is checked (microseconds); WinEvent hooks cover moves in between.
        // Rescan (every 2 s) whenever the tracked window is gone OR not showing: OpenVPN Connect
        // has several top-level windows, and when it comes back from the tray the visible one can
        // be a different handle than the one tracked before.
        var showing = ovpnHwnd != IntPtr.Zero && NativeMethods.IsWindow(ovpnHwnd) && NativeMethods.IsWindowVisible(ovpnHwnd) && !NativeMethods.IsIconic(ovpnHwnd);
        Sync(!showing && tick % 4 == 0);
    }

    // full = rediscover the OpenVPN window (process scan); otherwise use the tracked handle (cheap, for hook events).
    void Sync(bool full)
    {
        if (exiting) return;
        bool found; IntPtr h;
        if (full) { found = FindOpenVpnWindow(out h); if (found && h != ovpnHwnd) TrackOpenVpn(h); }
        else { h = ovpnHwnd; found = h != IntPtr.Zero && NativeMethods.IsWindow(h) && NativeMethods.IsWindowVisible(h) && !NativeMethods.IsIconic(h); }

        var rising = found && !lastFound;
        var falling = !found && lastFound;
        lastFound = found;

        // React only to OpenVPN appearing/disappearing, so Esc and the close button stay respected.
        // "Hidden" includes minimized to the taskbar button (taskbar mode).
        if (rising && !OnScreen) ShowAndFocus(false);
        else if (falling && OnScreen && !manualOverride && !userMoved) HideForLater();
        UpdateDockState();
    }

    void UpdateDockState()
    {
        // minimized (taskbar mode, the system or another tool): leave the restore bounds alone until it
        // comes back (OnRestored docks it then); a minimized window reports off-screen bounds
        if (Iconic || WindowState == FormWindowState.Minimized) return;
        var h = ovpnHwnd;
        var nowDocked = Visible && !userMoved && h != IntPtr.Zero && lastFound;
        NativeMethods.RECT r;
        if (nowDocked && NativeMethods.GetFrame(h, out r) && (!docked || !SameRect(r, lastRect))) DockTo(h, r);
        if (nowDocked == docked) return;
        docked = nowDocked;
        if (docked && !ContainsFocus) RaiseAbove();
        if (!docked && floatHeight > 0) Height = floatHeight;
        ApplyFrameStyle(); Invalidate();
    }

    void PlaceWindow()
    {
        IntPtr h; NativeMethods.RECT r;
        if (FindOpenVpnWindow(out h) && NativeMethods.GetFrame(h, out r)) { DockTo(h, r); return; }
        var area = (Demo.On ? Screen.PrimaryScreen : Screen.FromPoint(Cursor.Position)).WorkingArea;
        if (!placed || !area.IntersectsWith(Bounds))
            SetBounds(area.Left + (area.Width - Width) / 2, area.Top + Math.Max(0, (area.Height - floatHeight) / 2), Width, Math.Min(floatHeight, area.Height));
        placed = true;
    }

    // Flush against OpenVPN's visible frame, same top and height, so the two read as one window.
    void DockTo(IntPtr h, NativeMethods.RECT r)
    {
        lastRect = r;
        var area = Screen.FromHandle(h).WorkingArea;
        var height = Math.Max(MinimumSize.Height, r.Bottom - r.Top);
        // Preferred side (settings / automatic); fall back to the other side when there is no room.
        var preferLeft = PreferredSide(r, area) < 0;
        var x = preferLeft ? r.Left - Width : r.Right; dockSide = preferLeft ? -1 : 1;
        if (!preferLeft && x + Width > area.Right) { x = r.Left - Width; dockSide = -1; }
        if (preferLeft && x < area.Left) { x = r.Right; dockSide = 1; }
        if (x < area.Left) { x = area.Left; dockSide = 1; }
        if (x + Width > area.Right) { x = area.Right - Width; }
        var y = Math.Max(area.Top, Math.Min(r.Top, area.Bottom - height));
        placed = true;
        if (Left != x || Top != y || Height != height) SetBounds(x, y, Width, height);
    }

    // -1 = left of OpenVPN, +1 = right.
    // "auto": the side the user last snapped the panel to; otherwise the side facing the middle of
    // the screen (OpenVPN on the right half -> panel on its left). A 10% band around the middle keeps
    // the panel from flipping back and forth while OpenVPN is dragged across it.
    int PreferredSide(NativeMethods.RECT r, Rectangle area)
    {
        if (settings.DockSide == "left") return -1;
        if (settings.DockSide == "right") return 1;
        if (autoSide != 0) return autoSide;
        var center = (r.Left + r.Right) / 2; var mid = area.Left + area.Width / 2; var band = area.Width / 10;
        if (docked)
        {
            if (dockSide > 0 && center < mid + band) return 1;
            if (dockSide < 0 && center > mid - band) return -1;
        }
        return center > mid ? -1 : 1;
    }

    int autoSide; // set when the user drags the panel onto one side of OpenVPN (auto mode only)

    static bool SameRect(NativeMethods.RECT a, NativeMethods.RECT b) { return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom; }

    static bool FindOpenVpnWindow(out IntPtr h)
    {
        h = IntPtr.Zero;
        if (Demo.On) return false;   // demo mode never docks to a real OpenVPN window
        var processes = Process.GetProcessesByName("OpenVPNConnect");
        try
        {
            foreach (var p in processes)
            {
                try
                {
                    var w = p.MainWindowHandle;
                    if (w != IntPtr.Zero && NativeMethods.IsWindowVisible(w) && !NativeMethods.IsIconic(w)) { h = w; return true; }
                }
                catch { }
            }
        }
        finally { foreach (var p in processes) p.Dispose(); }
        return false;
    }

    static string FindOpenVpnPath()
    {
        var roots = new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetEnvironmentVariable("ProgramFiles(x86)") };
        foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var p = Path.Combine(root, "OpenVPN Connect", "OpenVPNConnect.exe");
            if (File.Exists(p)) return p;
        }
        return "";
    }
}

internal static class Program
{
    static float devScale;
    static string MutexName = "Local\\OpenVpnQuickSearch.SingleInstance", EventName = "Local\\OpenVpnQuickSearch.Activate";
    const int ASFW_ANY = -1;

    [STAThread]
    static int Main(string[] args)
    {
        var background = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        // Development switches: a separate instance name and settings file let a test build run next to the installed one.
        foreach (var a in args)
        {
            if (a.StartsWith("--instance=", StringComparison.OrdinalIgnoreCase)) { var n = a.Substring(11); MutexName += "." + n; EventName += "." + n; }
            else if (a.StartsWith("--settings=", StringComparison.OrdinalIgnoreCase)) SettingsStore.PathOverride = a.Substring(11);
            else if (a.Equals("--demo", StringComparison.OrdinalIgnoreCase)) Demo.On = true;
            else if (a.StartsWith("--demo-connected=", StringComparison.OrdinalIgnoreCase)) { Demo.On = true; Demo.ConnectedName = a.Substring(17); }
            else if (a.StartsWith("--demo-search=", StringComparison.OrdinalIgnoreCase)) { Demo.On = true; Demo.Query = a.Substring(14); }
            else if (a.StartsWith("--demo-settings=", StringComparison.OrdinalIgnoreCase)) { Demo.On = true; int.TryParse(a.Substring(16), out Demo.SettingsPage); }
            else if (a.StartsWith("--scale=", StringComparison.OrdinalIgnoreCase)) float.TryParse(a.Substring(8), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out devScale);
        }
        try
        {
            if (args.Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase)))
            {
                Console.OutputEncoding = Encoding.UTF8;
                foreach (var p in ProfileStore.Load()) Console.WriteLine("{0}\t{1}\t{2}", p.Name, p.Host, p.Username);
                return 0;
            }
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // Already running: let it take the foreground and ask it to show itself.
                    if (!background)
                    {
                        NativeMethods.AllowSetForegroundWindow(ASFW_ANY);
                        try { using (var signal = EventWaitHandle.OpenExisting(EventName)) signal.Set(); } catch { }
                    }
                    return 0;
                }
                // Diagnostics (README "Hata raporları ve günlükler"): the app log and crash capture.
                // Demo mode writes nothing (no log folder) and never opens the browser.
                var logDir = Demo.On ? null : CrashReporter.DefaultLogDir;
                CrashReporter.Init(logDir);
                AppLog.Init(logDir, SettingsStore.Load().LogEnabled);
                CrashReporter.Install();   // before any control exists (SetUnhandledExceptionMode)
                try { NativeMethods.SetProcessDPIAware(); } catch { }
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.Scale = devScale > 0 ? devScale : Math.Max(1F, g.DpiX / 96F);
                CrashReporter.LogStartAsync(background);
                using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset, EventName))
                {
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    // First run (no settings yet) or --setup: scan this PC, import the bundled pack, pick options.
                    var firstRun = !File.Exists(SettingsStore.FilePath) || args.Any(a => a.Equals("--setup", StringComparison.OrdinalIgnoreCase));
                    if (firstRun && !background)
                    {
                        Themes.Apply(SettingsStore.Load().Theme); // stored theme (first run: default); the wizard previews changes live
                        using (var wizard = new SetupWizard()) Application.Run(wizard);
                    }
                    var settings = SettingsStore.Load();
                    AppLog.Enabled = settings.LogEnabled;
                    Themes.Apply(settings.Theme); // before any control is created, so every control picks up the palette
                    Application.Run(new SearchForm(background, activation, settings));
                }
                AppLog.Info("app", "exit");
                AppLog.Flush();
                GC.KeepAlive(mutex);
            }
            return 0;
        }
        catch (Exception ex)
        {
            CrashReporter.Fatal(ex);
            if (!background) MessageBox.Show(ex.Message, "OpenVPN Hızlı Arama");
            return 1;
        }
    }
}


