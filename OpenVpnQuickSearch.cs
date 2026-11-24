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
        try { if (File.Exists(FilePath)) return new HashSet<string>(File.ReadAllLines(FilePath, Encoding.UTF8).Where(l => l.Trim().Length > 0)); } catch { }
        return new HashSet<string>();
    }
    public static void Save(HashSet<string> ids)
    {
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
    public static void DrawMark(Graphics g, RectangleF r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ui.Round(r, r.Width * 0.28F))
        using (var b = new LinearGradientBrush(new RectangleF(r.X, r.Y, r.Width, r.Height + 1), Color.FromArgb(70, 132, 255), Ui.Brand, 60F)) g.FillPath(b, path);
        float k = r.Width / 34F, ox = r.X, oy = r.Y;
        Func<float, float, PointF> P = (px, py) => new PointF(ox + px * k, oy + py * k);
        using (var path = new GraphicsPath())
        {
            path.AddLine(P(17, 6.5F), P(26.5F, 9.8F));
            path.AddLine(P(26.5F, 9.8F), P(26.5F, 16.5F));
            path.AddBezier(P(26.5F, 16.5F), P(26.5F, 22.5F), P(22F, 26.2F), P(17, 28.3F));
            path.AddBezier(P(17, 28.3F), P(12F, 26.2F), P(7.5F, 22.5F), P(7.5F, 16.5F));
            path.AddLine(P(7.5F, 16.5F), P(7.5F, 9.8F));
            path.CloseFigure();
            using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
        }
        Ui.DrawMagnifier(g, Ui.Brand, ox + 12.3F * k, oy + 11.5F * k, 9.5F * k);
    }

    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent); g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var pad = size <= 16 ? 0F : size * 0.04F;
            DrawMark(g, new RectangleF(pad, pad, size - 2 * pad - 0.5F, size - 2 * pad - 0.5F));
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

    // Deep navy with a warm orange accent (see Themes.Apply for the light palette).
    public static Color Bg = Color.FromArgb(14, 18, 29);
    public static Color Surface = Color.FromArgb(23, 29, 43);
    public static Color SurfaceHover = Color.FromArgb(29, 36, 53);
    public static Color SelectedFill = Color.FromArgb(52, 38, 32);
    public static Color Border = Color.FromArgb(36, 44, 62);
    public static Color BorderStrong = Color.FromArgb(55, 65, 88);
    public static Color Brand = Color.FromArgb(22, 82, 184);
    public static Color Accent = Color.FromArgb(247, 146, 50);
    public static Color Accent2 = Color.FromArgb(232, 108, 28);
    public static Color Text = Color.FromArgb(240, 243, 248);
    public static Color Muted = Color.FromArgb(152, 162, 183);
    public static Color Faint = Color.FromArgb(102, 112, 134);
    public static Color Gold = Color.FromArgb(255, 194, 76);
    public static Color Success = Color.FromArgb(70, 205, 130);

    public static readonly Font TitleFont = new Font("Segoe UI Semibold", 12.5F);
    public static readonly Font NameFont = new Font("Segoe UI Semibold", 10.25F);
    public static readonly Font SubFont = new Font("Segoe UI", 9F);
    public static readonly Font SmallFont = new Font("Segoe UI", 8.25F);
    public static readonly Font HeaderFont = new Font("Segoe UI Semibold", 7.75F);
    public static readonly Font InitialsFont = new Font("Segoe UI Semibold", 9.25F);
    public static readonly Font InputFont = new Font("Segoe UI", 11F);
    public static readonly Font KeyFont = new Font("Segoe UI Semibold", 7.5F);
    public static readonly Font KeyFontLarge = new Font("Segoe UI Semibold", 9F);
    public static readonly Font MenuFont = new Font("Segoe UI", 9.5F);

    public const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    static readonly Color[] AvatarColors =
    {
        Color.FromArgb(96, 150, 255), Color.FromArgb(160, 120, 250), Color.FromArgb(56, 196, 166), Color.FromArgb(244, 154, 82),
        Color.FromArgb(236, 104, 136), Color.FromArgb(80, 188, 236), Color.FromArgb(160, 196, 80), Color.FromArgb(222, 132, 222)
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

    // Keycap such as "Esc" or "Ctrl+Alt+V". Returns its width.
    public static int DrawKeycap(Graphics g, string text, int x, int y)
    {
        var sz = Measure(g, text, KeyFont);
        var r = new Rectangle(x, y, sz.Width + S(10), S(18));
        var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        FillRound(g, Blend(Bg, Accent, 0.06F), r, S(4));
        DrawRound(g, Blend(Accent, Bg, 0.4F), r, S(4));
        g.SmoothingMode = old;
        TextRenderer.DrawText(g, text, KeyFont, new Point(x + S(5), y + (r.Height - sz.Height) / 2), Text, Flags);
        return r.Width;
    }

    // Decorative glowing curves are expensive (wide gradient strokes, ~20 ms), so they are
    // rendered once per size/theme into a bitmap and blitted afterwards (~0.1 ms).
    static readonly Dictionary<string, Bitmap> waveCache = new Dictionary<string, Bitmap>();

    public static void DrawWaves(Graphics g, Rectangle r, bool top)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var key = r.Width + "x" + r.Height + (top ? "t" : "b") + Accent.ToArgb() + (Themes.IsLight ? "L" : "D");
        Bitmap bmp;
        if (!waveCache.TryGetValue(key, out bmp))
        {
            if (waveCache.Count >= 6) { foreach (var b in waveCache.Values) b.Dispose(); waveCache.Clear(); }
            bmp = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var bg = Graphics.FromImage(bmp)) RenderWaves(bg, new Rectangle(0, 0, r.Width, r.Height), top);
            waveCache[key] = bmp;
        }
        g.DrawImageUnscaled(bmp, r.X, r.Y);
    }

    // Points along the main top wave (same geometry as RenderWaves), for the moving shimmer.
    public static PointF[] WavePoints(Rectangle r)
    {
        using (var path = new GraphicsPath())
        {
            float y0 = r.Y + r.Height * 0.95F, y1 = r.Y - r.Height * 0.05F;
            path.AddBezier(r.X + r.Width * 0.35F, y0, r.X + r.Width * 0.62F, y0, r.X + r.Width * 0.72F, y1 + r.Height * 0.25F, r.Right + 4, y1);
            path.Flatten(null, 0.5F);
            return path.PathPoints;
        }
    }

    static Bitmap glowSprite; static int glowKey;
    // Soft round glow (pre-rendered) used for moving highlights.
    public static Bitmap GlowSprite
    {
        get
        {
            var key = Accent.ToArgb() ^ S(28);
            if (glowSprite != null && glowKey == key) return glowSprite;
            if (glowSprite != null) glowSprite.Dispose();
            var d = S(28); glowSprite = new Bitmap(d, d, System.Drawing.Imaging.PixelFormat.Format32bppPArgb); glowKey = key;
            using (var g = Graphics.FromImage(glowSprite)) using (var path = new GraphicsPath())
            {
                path.AddEllipse(0, 0, d, d);
                using (var b = new PathGradientBrush(path) { CenterColor = Color.FromArgb(230, Blend(Accent, Color.White, 0.5F)), SurroundColors = new[] { Color.FromArgb(0, Accent) } }) g.FillPath(b, path);
            }
            return glowSprite;
        }
    }

    static void RenderWaves(Graphics g, Rectangle r, bool top)
    {
        var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        var strength = Themes.IsLight ? 0.55F : 1F;
        for (var n = 0; n < 2; n++)
        {
            using (var path = new GraphicsPath())
            {
                if (top)
                {
                    float y0 = r.Y + r.Height * (0.95F - n * 0.18F), y1 = r.Y - r.Height * 0.05F;
                    path.AddBezier(r.X + r.Width * (0.35F + n * 0.1F), y0, r.X + r.Width * 0.62F, y0, r.X + r.Width * 0.72F, y1 + r.Height * 0.25F, r.Right + 4, y1);
                }
                else
                {
                    float y0 = r.Bottom - r.Height * (0.08F + n * 0.12F);
                    path.AddBezier(r.X - 4, y0, r.X + r.Width * 0.35F, y0 + r.Height * 0.05F, r.X + r.Width * 0.62F, r.Y + r.Height * (0.55F - n * 0.2F), r.Right + 4, r.Y + r.Height * (0.18F - n * 0.1F));
                }
                var a0 = top ? 0 : 0; var bounds = path.GetBounds();
                for (var w = 3; w >= 0; w--)
                {
                    var alpha = (int)(strength * (w == 0 ? 150 - n * 50 : (26 - w * 5) * (1 - n * 0.4F)));
                    using (var br = new LinearGradientBrush(new RectangleF(bounds.X - 1, bounds.Y - 1, bounds.Width + 2, bounds.Height + 2), Color.FromArgb(a0, Accent), Color.FromArgb(Math.Max(0, alpha), Accent), LinearGradientMode.Horizontal))
                    using (var pen = new Pen(br, w == 0 ? S(1.3F) : S(1.3F + w * 2.4F)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawPath(pen, path);
                }
            }
        }
        g.SmoothingMode = old;
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

// =====================================================================
// Controls
// =====================================================================

internal enum HeaderGlyph { More, Close, Minimize }

internal sealed class HeaderButton : Control
{
    bool hover;
    public HeaderGlyph Glyph { get; set; }
    public HeaderButton(HeaderGlyph glyph)
    {
        Glyph = glyph; Size = new Size(Ui.S(36), Ui.S(32)); Cursor = Cursors.Hand; TabStop = false; BackColor = Ui.Bg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true); SetStyle(ControlStyles.Selectable, false);
        MouseEnter += delegate { hover = true; Invalidate(); }; MouseLeave += delegate { hover = false; Invalidate(); };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; if (BackColor.A == 255) g.Clear(BackColor);
        if (hover) Ui.FillRound(g, Glyph == HeaderGlyph.Close ? Color.FromArgb(196, 43, 28) : Ui.SurfaceHover, new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(8));
        var c = hover ? (Glyph == HeaderGlyph.Close ? Color.White : Ui.Text) : Ui.Muted;
        float cx = Width / 2F, cy = Height / 2F;
        if (Glyph == HeaderGlyph.More)
            using (var b = new SolidBrush(c)) { float r = Ui.S(1.8F), gap = Ui.S(6F); for (var i = -1; i <= 1; i++) g.FillEllipse(b, cx + i * gap - r, cy - r, r * 2, r * 2); }
        else
            using (var p = new Pen(c, Ui.S(1.5F)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                var k = Ui.S(6F);
                if (Glyph == HeaderGlyph.Minimize) g.DrawLine(p, cx - k, cy, cx + k, cy);
                else { k = Ui.S(5.5F); g.DrawLine(p, cx - k, cy - k, cx + k, cy + k); g.DrawLine(p, cx - k, cy + k, cx + k, cy - k); }
            }
    }
}

internal sealed class SearchBox : Control
{
    public readonly TextBox Input = new TextBox();
    readonly Label placeholder = new Label();
    bool focused, hoverClear, hover;
    string hotkey = "Ctrl+Alt+V";
    float glowAmt, hoverAmt; // animated focus / hover glow
    readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer { Interval = 16 };

    void AnimateGlow()
    {
        if (!fade.Enabled) fade.Start();
    }

    void FadeTick()
    {
        float tg = focused ? 1 : 0, th = hover ? 1 : 0;
        glowAmt += (tg - glowAmt) * 0.2F; hoverAmt += (th - hoverAmt) * 0.2F;
        if (Math.Abs(tg - glowAmt) < 0.01F && Math.Abs(th - hoverAmt) < 0.01F) { glowAmt = tg; hoverAmt = th; fade.Stop(); }
        Invalidate(false);
    }

    protected override void Dispose(bool disposing) { if (disposing) fade.Dispose(); base.Dispose(disposing); }

    public SearchBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Ui.Bg; Cursor = Cursors.IBeam; TabStop = false;
        Input.BorderStyle = BorderStyle.None; Input.Font = Ui.InputFont;
        placeholder.Text = "Firma, IP veya kullanıcı adı ara…"; placeholder.AutoSize = true; placeholder.Font = Ui.InputFont; placeholder.Cursor = Cursors.IBeam; placeholder.UseMnemonic = false;
        placeholder.Click += delegate { Input.Focus(); };
        fade.Tick += delegate { FadeTick(); };
        Input.GotFocus += delegate { focused = true; AnimateGlow(); };
        Input.LostFocus += delegate { focused = false; AnimateGlow(); };
        Input.TextChanged += delegate { placeholder.Visible = Input.TextLength == 0; Invalidate(); };
        MouseEnter += delegate { hover = true; AnimateGlow(); };
        Controls.Add(Input); Controls.Add(placeholder); placeholder.BringToFront();
        RefreshTheme();
    }

    public string Hotkey { get { return hotkey; } set { hotkey = value ?? ""; Invalidate(); } }

    public void RefreshTheme()
    {
        BackColor = Ui.Bg; Input.BackColor = FieldColor; Input.ForeColor = Ui.Text;
        placeholder.BackColor = FieldColor; placeholder.ForeColor = Ui.Faint; Invalidate(true);
    }

    static Color FieldColor { get { return Ui.Blend(Ui.Bg, Ui.Surface, 0.55F); } }

    Rectangle Box { get { var m = Ui.S(5); return new Rectangle(m, m, Width - 2 * m - 1, Height - 2 * m - 1); } }
    Rectangle ClearRect { get { var b = Box; var d = Ui.S(24); return new Rectangle(b.Right - Ui.S(14) - d, b.Y + (b.Height - d) / 2, d, d); } }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var b = Box; var x = b.X + Ui.S(48);
        Input.Location = new Point(x, b.Y + (b.Height - Input.Height) / 2);
        Input.Width = Math.Max(Ui.S(60), b.Right - x - Ui.S(130));
        placeholder.Location = new Point(x - Ui.S(1), b.Y + (b.Height - placeholder.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(BackColor);
        var b = Box; var radius = Ui.S(14);
        // soft orange glow; stronger while typing
        var glow = 0.35F + 0.2F * hoverAmt + 0.45F * glowAmt;
        for (var i = 3; i >= 1; i--)
        {
            var grow = Ui.S(i * 1.4F);
            using (var path = Ui.Round(new RectangleF(b.X - grow, b.Y - grow, b.Width + 2 * grow, b.Height + 2 * grow), radius + grow))
            using (var p = new Pen(Color.FromArgb((int)(glow * (34 - i * 8)), Ui.Accent), Ui.S(1.6F))) g.DrawPath(p, path);
        }
        using (var path = Ui.Round(b, radius))
        using (var br = new LinearGradientBrush(b, Ui.Blend(Ui.Bg, Ui.Surface, 0.75F), FieldColor, 90F)) g.FillPath(br, path);
        Ui.DrawRound(g, focused ? Ui.Accent : Ui.Blend(Ui.Accent, Ui.Bg, 0.45F), b, radius, focused ? 1.6F : 1.2F);
        // bright edge along the bottom, like a lit rim
        using (var lb = new LinearGradientBrush(new Rectangle(b.X, b.Bottom - 2, b.Width, 2), Color.FromArgb(0, Ui.Accent), Color.FromArgb((int)(140 + 90 * glowAmt), Ui.Accent), LinearGradientMode.Horizontal))
        using (var p = new Pen(lb, Ui.S(1.4F)))
            g.DrawLine(p, b.X + radius, b.Bottom, b.Right - radius, b.Bottom);
        Ui.DrawMagnifier(g, Ui.Accent, b.X + Ui.S(18), b.Y + (b.Height - Ui.S(18)) / 2F, Ui.S(18));

        if (Input.TextLength > 0)
        {
            var c = ClearRect;
            using (var br = new SolidBrush(hoverClear ? Ui.BorderStrong : Ui.Border)) g.FillEllipse(br, c);
            using (var p = new Pen(hoverClear ? Ui.Text : Ui.Muted, Ui.S(1.6F)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                var k = Ui.S(4.5F); float cx = c.X + c.Width / 2F, cy = c.Y + c.Height / 2F;
                g.DrawLine(p, cx - k, cy - k, cx + k, cy + k); g.DrawLine(p, cx - k, cy + k, cx + k, cy - k);
            }
        }
        else if (hotkey.Length > 0)
        {
            var label = hotkey.Replace("+", " + ");
            var w = Ui.Measure(g, label, Ui.KeyFontLarge).Width + Ui.S(20);
            var kr = new Rectangle(b.Right - Ui.S(14) - w, b.Y + (b.Height - Ui.S(28)) / 2, w, Ui.S(28));
            Ui.FillRound(g, Ui.Blend(Ui.Bg, Ui.Accent, 0.06F), kr, Ui.S(7));
            Ui.DrawRound(g, Ui.Blend(Ui.Accent, Ui.Bg, 0.35F), kr, Ui.S(7));
            var sz = Ui.Measure(g, label, Ui.KeyFontLarge);
            TextRenderer.DrawText(g, label, Ui.KeyFontLarge, new Point(kr.X + (kr.Width - sz.Width) / 2, kr.Y + (kr.Height - sz.Height) / 2), Ui.Text, Ui.Flags);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = Input.TextLength > 0 && ClearRect.Contains(e.Location);
        if (h != hoverClear) { hoverClear = h; Cursor = h ? Cursors.Hand : Cursors.IBeam; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { hover = false; hoverClear = false; AnimateGlow(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Input.TextLength > 0 && ClearRect.Contains(e.Location)) Input.Clear();
        Input.Focus();
        base.OnMouseDown(e);
    }
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

// Custom-drawn, smooth-scrolling profile list: card rows, section headers with icons,
// time pills, per-row connect button, orange accent, and an animation layer
// (row entrance, hover/selection fades, connect / success / failure / disconnect effects).
// Performance: a single 60 fps timer runs only while something animates, and it repaints
// only the rows that change; idle cost is zero.
internal sealed class ProfileList : Control
{
    static readonly Font RowName = new Font("Segoe UI Semibold", 10.75F);
    static readonly Font RowSub = new Font("Segoe UI", 9F);
    static readonly Font Pill = new Font("Segoe UI", 8.5F);
    static readonly Font Section = new Font("Segoe UI Semibold", 9.25F);
    static readonly Font AvatarFont = new Font("Segoe UI Semibold", 10.5F);
    static readonly Color Danger = Color.FromArgb(232, 72, 96);
    const float SuccessDuration = 1.7F, FailDuration = 0.75F, AppearDuration = 0.34F;

    List<ListRow> rows = new List<ListRow>();
    readonly Dictionary<string, int> indexById = new Dictionary<string, int>();
    string[] terms = new string[0];
    string emptyTitle = "", emptyHint = "";
    int contentHeight, hoverIndex = -1, selectedIndex = -1, downIndex = -1, targetScroll, dragStartY;
    float scroll, dragStartScroll, lastLivePaint;
    bool hoverStar, hoverPlug, downOnStar, dragThumb, hoverBar, scrolling;

    // animation state
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    readonly System.Windows.Forms.Timer frame = new System.Windows.Forms.Timer { Interval = 16 };
    readonly Dictionary<string, float> hoverAmt = new Dictionary<string, float>(), selAmt = new Dictionary<string, float>(), appearAt = new Dictionary<string, float>();
    string connectingId, successId, failId, disconnectingId;
    float connectT0, successT0, failT0, disconnectT0;
    public bool AnimateNext; // set before a filter change to play the entrance animation
    public Func<bool> AllowAmbient = delegate { return true; }; // breathing effects only while the user is looking
    public void Wake() { Kick(); }

    public Func<VpnProfile, bool> IsPinned = delegate { return false; };
    public Func<VpnProfile, ReachState> ReachOf = delegate { return ReachState.Unknown; };
    public Func<VpnProfile, string> LiveText = delegate { return null; }; // e.g. "Bağlı · 12 dk" for the connected profile
    public event Action<VpnProfile> Activated, PinToggled;
    public event Action<VpnProfile, Point> MenuRequested;

    public ProfileList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false; BackColor = Ui.Bg;
        frame.Tick += delegate { OnFrame(); };
    }

    static int S(float v) { return Ui.S(v); }
    float Now { get { return (float)clock.Elapsed.TotalSeconds; } }
    int MaxScroll { get { return Math.Max(0, contentHeight - Height); } }

    protected override void Dispose(bool disposing) { if (disposing) frame.Dispose(); base.Dispose(disposing); }

    // Let the borderless parent window receive resize hit-tests at its left/right edges.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084 && Parent != null)
        {
            var v = m.LParam.ToInt64();
            var p = Parent.PointToClient(new Point((short)(v & 0xffff), (short)((v >> 16) & 0xffff)));
            if (p.X < S(5) || p.X >= Parent.ClientSize.Width - S(5)) { m.Result = (IntPtr)(-1); return; }
        }
        base.WndProc(ref m);
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) Kick(); else frame.Stop(); }

    // ---------- public animation API ----------

    public void BeginConnect(string id) { connectingId = id; connectT0 = Now; failId = null; successId = null; Kick(); }
    public void ConnectSucceeded(string id) { if (connectingId == id) connectingId = null; successId = id; successT0 = Now; Kick(); }
    public void ConnectFailed(string id) { if (connectingId == id) connectingId = null; failId = id; failT0 = Now; Kick(); }
    public void CancelConnect(string id) { if (connectingId == id) connectingId = null; Kick(); Invalidate(); }
    public void BeginDisconnect(string id) { disconnectingId = id; disconnectT0 = Now; Kick(); }
    public void EndDisconnect() { disconnectingId = null; Kick(); Invalidate(); }
    public bool IsConnecting { get { return connectingId != null; } }

    public VpnProfile SelectedProfile { get { return selectedIndex >= 0 && selectedIndex < rows.Count ? rows[selectedIndex].Profile : null; } }

    public void SetRows(List<ListRow> newRows, string[] newTerms, string title, string hint, string keepId)
    {
        rows = newRows; terms = newTerms; emptyTitle = title; emptyHint = hint;
        indexById.Clear();
        var y = S(2);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.IsHeader) { r.Top = y + (y > S(2) ? S(10) : 0); r.Height = S(42); y = r.Top + r.Height; }
            else { r.Top = y; r.Height = S(70); y += r.Height + S(8); indexById[r.Profile.Id] = i; }
        }
        contentHeight = y + S(14);
        hoverIndex = -1; selectedIndex = -1;
        if (keepId != null) selectedIndex = rows.FindIndex(r => !r.IsHeader && r.Profile.Id == keepId);
        if (selectedIndex < 0)
        {
            selectedIndex = rows.FindIndex(r => !r.IsHeader);
            scroll = targetScroll = 0; scrolling = false;
        }
        else { targetScroll = Math.Min(targetScroll, MaxScroll); scroll = targetScroll; EnsureVisible(selectedIndex, false); }

        // forget animation state for rows that are gone (keeps the dictionaries small)
        Prune(hoverAmt); Prune(selAmt); Prune(appearAt);
        var sel = SelectedProfile; if (sel != null) selAmt[sel.Id] = 1;
        if (AnimateNext)
        {
            AnimateNext = false;
            var t = Now; var k = 0;
            foreach (var r in rows)
            {
                if (r.IsHeader) continue;
                if (r.Top - scroll > Height) break;
                appearAt[r.Profile.Id] = t + Math.Min(k++, 10) * 0.035F;
            }
        }
        Kick();
        Invalidate();
    }

    void Prune(Dictionary<string, float> d)
    {
        if (d.Count == 0) return;
        var dead = d.Keys.Where(id => !indexById.ContainsKey(id)).ToList();
        foreach (var id in dead) d.Remove(id);
    }

    public void MoveSelection(int delta)
    {
        if (rows.Count == 0) return;
        var profileRows = new List<int>();
        for (var i = 0; i < rows.Count; i++) if (!rows[i].IsHeader) profileRows.Add(i);
        if (profileRows.Count == 0) return;
        var pos = profileRows.IndexOf(selectedIndex);
        pos = pos < 0 ? 0 : Math.Max(0, Math.Min(profileRows.Count - 1, pos + delta));
        selectedIndex = profileRows[pos];
        EnsureVisible(selectedIndex, true);
        Kick();
    }

    void EnsureVisible(int index, bool animate)
    {
        if (index < 0) return;
        var r = rows[index];
        var top = r.Top - S(6);
        if (index > 0 && rows[index - 1].IsHeader) top = rows[index - 1].Top - S(2);
        var bottom = r.Top + r.Height + S(10);
        var t = targetScroll;
        if (top < t) t = top; else if (bottom > t + Height) t = bottom - Height;
        ScrollTo(t, animate);
    }

    void ScrollTo(int t, bool animate)
    {
        targetScroll = Math.Max(0, Math.Min(MaxScroll, t));
        if (!animate) { scroll = targetScroll; scrolling = false; Invalidate(); }
        else { scrolling = true; Kick(); }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); targetScroll = Math.Min(targetScroll, MaxScroll); scroll = Math.Min(scroll, MaxScroll); }

    // ---------- animation loop ----------

    void Kick() { if (Visible && IsHandleCreated && !frame.Enabled) { frame.Interval = 16; frame.Start(); } }

    void InvalidateRow(string id, int pad)
    {
        int i; if (id == null || !indexById.TryGetValue(id, out i)) return;
        var rr = RowRect(rows[i]); rr.Inflate(S(pad), S(pad));
        if (rr.Bottom >= 0 && rr.Top <= Height) Invalidate(rr);
    }

    static bool Step(Dictionary<string, float> d, string id, float target, float speed)
    {
        float v; d.TryGetValue(id, out v);
        if (Math.Abs(v - target) < 0.01F) { if (v != target) { d[id] = target; return true; } return false; }
        d[id] = v + (target - v) * speed;
        return true;
    }

    void OnFrame()
    {
        if (!Visible) { frame.Stop(); return; }
        var now = Now; var fast = false;

        if (scrolling)
        {
            var diff = targetScroll - scroll;
            if (Math.Abs(diff) < 0.6F) { scroll = targetScroll; scrolling = false; } else scroll += diff * 0.28F;
            Invalidate(); fast = true;
        }

        // hover / selection fades for the rows that have (or had) them
        var hovId = hoverIndex >= 0 && hoverIndex < rows.Count && !rows[hoverIndex].IsHeader ? rows[hoverIndex].Profile.Id : null;
        var selId = SelectedProfile != null ? SelectedProfile.Id : null;
        if (hovId != null && !hoverAmt.ContainsKey(hovId)) hoverAmt[hovId] = 0;
        if (selId != null && !selAmt.ContainsKey(selId)) selAmt[selId] = 0;
        foreach (var id in hoverAmt.Keys.ToList()) if (Step(hoverAmt, id, id == hovId ? 1 : 0, 0.3F)) { InvalidateRow(id, 4); fast = true; }
        foreach (var id in selAmt.Keys.ToList()) if (Step(selAmt, id, id == selId ? 1 : 0, 0.25F)) { InvalidateRow(id, 4); fast = true; }

        // entrance
        if (appearAt.Count > 0)
            foreach (var kv in appearAt.ToList())
            {
                if (now - kv.Value > AppearDuration + 0.05F) { appearAt.Remove(kv.Key); InvalidateRow(kv.Key, 20); }
                else { InvalidateRow(kv.Key, 20); fast = true; }
            }

        if (connectingId != null) { InvalidateRow(connectingId, 30); fast = true; }
        if (disconnectingId != null) { InvalidateRow(disconnectingId, 6); fast = true; }
        if (successId != null) { InvalidateRow(successId, 40); if (now - successT0 > SuccessDuration) successId = null; fast = true; }
        if (failId != null) { InvalidateRow(failId, 14); if (now - failT0 > FailDuration) failId = null; fast = true; }

        // gentle "breathing" of the connected row's status dot (20 fps is plenty)
        var live = false;
        if (AllowAmbient())
        foreach (var r in rows)
            if (!r.IsHeader && LiveText(r.Profile) != null)
            {
                live = true;
                if (now - lastLivePaint > 0.08F) InvalidateRow(r.Profile.Id, 4);
            }
        if (live && now - lastLivePaint > 0.08F) lastLivePaint = now;

        if (!fast && !live) frame.Stop();
        else frame.Interval = fast ? 16 : 80;
    }

    // ---------- Geometry ----------

    Rectangle RowRect(ListRow r) { return new Rectangle(S(12), r.Top - (int)Math.Round(scroll), Width - S(12) - S(18), r.Height); }
    Rectangle PlugRect(Rectangle rr) { var d = S(38); return new Rectangle(rr.Right - S(14) - d, rr.Y + (rr.Height - d) / 2, d, d); }
    Rectangle StarRect(Rectangle rr) { var p = PlugRect(rr); var d = S(24); return new Rectangle(p.X - S(6) - d, rr.Y + S(6), d, d); }

    Rectangle ThumbRect()
    {
        if (contentHeight <= Height || Height <= 0) return Rectangle.Empty;
        var h = Math.Max(S(40), (int)(Height * (Height / (float)contentHeight)));
        var y = MaxScroll == 0 ? 0 : (int)((Height - h) * (scroll / MaxScroll));
        var w = hoverBar || dragThumb ? S(5) : S(3);
        return new Rectangle(Width - S(8) - w / 2, y, w, h);
    }

    int HitRow(Point p)
    {
        var y = p.Y + (int)Math.Round(scroll);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!r.IsHeader && y >= r.Top && y < r.Top + r.Height && p.X >= S(12) && p.X < Width - S(18)) return i;
        }
        return -1;
    }

    // ---------- Mouse ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragThumb)
        {
            var th = ThumbRect().Height; var range = Height - th;
            if (range > 0) { scroll = Math.Max(0, Math.Min(MaxScroll, dragStartScroll + (e.Y - dragStartY) * (MaxScroll / (float)range))); targetScroll = (int)scroll; Invalidate(); }
            return;
        }
        var bar = contentHeight > Height && e.X >= Width - S(16);
        var i = bar ? -1 : HitRow(e.Location);
        var star = i >= 0 && StarRect(RowRect(rows[i])).Contains(e.Location);
        var plug = i >= 0 && PlugRect(RowRect(rows[i])).Contains(e.Location);
        if (i != hoverIndex || star != hoverStar || plug != hoverPlug)
        {
            // repaint only the rows involved, not the whole list
            if (hoverIndex >= 0 && hoverIndex < rows.Count && !rows[hoverIndex].IsHeader) InvalidateRow(rows[hoverIndex].Profile.Id, 4);
            if (i >= 0) InvalidateRow(rows[i].Profile.Id, 4);
            hoverIndex = i; hoverStar = star; hoverPlug = plug; Kick();
        }
        if (bar != hoverBar) { hoverBar = bar; Invalidate(new Rectangle(Width - S(16), 0, S(16), Height)); }
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (dragThumb) return;
        if (hoverIndex >= 0 && hoverIndex < rows.Count && !rows[hoverIndex].IsHeader) InvalidateRow(rows[hoverIndex].Profile.Id, 4);
        hoverIndex = -1; hoverStar = false; hoverPlug = false;
        if (hoverBar) { hoverBar = false; Invalidate(new Rectangle(Width - S(16), 0, S(16), Height)); }
        Kick();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var thumb = ThumbRect();
        if (e.Button == MouseButtons.Left && contentHeight > Height && e.X >= Width - S(16))
        {
            if (!thumb.Contains(e.Location)) { ScrollTo(e.Y < thumb.Y ? targetScroll - Height : targetScroll + Height, true); return; }
            dragThumb = true; dragStartY = e.Y; dragStartScroll = scroll; scrolling = false; Capture = true; return;
        }
        downIndex = HitRow(e.Location);
        downOnStar = downIndex >= 0 && StarRect(RowRect(rows[downIndex])).Contains(e.Location);
        if (downIndex >= 0 && e.Button == MouseButtons.Right)
        {
            selectedIndex = downIndex; Kick();
            if (MenuRequested != null) MenuRequested(rows[downIndex].Profile, e.Location);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragThumb) { dragThumb = false; Capture = false; Invalidate(); return; }
        if (e.Button != MouseButtons.Left) return;
        var i = HitRow(e.Location);
        if (i < 0 || i != downIndex) return;
        var p = rows[i].Profile;
        if (downOnStar && StarRect(RowRect(rows[i])).Contains(e.Location)) { if (PinToggled != null) PinToggled(p); return; }
        selectedIndex = i; Kick();
        if (Activated != null) Activated(p);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollTo(targetScroll - e.Delta * S(70) / 120 * 2, true);
    }

    // ---------- Paint ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        var clip = e.ClipRectangle;
        // decorative glow waves behind the bottom of the list (cached bitmap)
        var waves = new Rectangle(0, Height - S(90), Width, S(90));
        if (clip.IntersectsWith(waves)) Ui.DrawWaves(g, waves, false);
        if (rows.Count == 0) { PaintEmpty(g); return; }

        var now = Now;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var rr = RowRect(r);
            if (rr.Bottom < 0 || rr.Top > Height) continue;
            var area = Rectangle.Inflate(rr, S(40), S(40));
            if (!clip.IntersectsWith(area)) continue;
            if (r.IsHeader) { PaintHeader(g, r, rr); continue; }

            // entrance: slide up + fade in
            float appear = 1; float t0;
            if (appearAt.TryGetValue(r.Profile.Id, out t0)) appear = Ease.OutCubic((now - t0) / AppearDuration);
            if (appear <= 0) continue;
            if (appear < 1) rr.Offset(0, (int)(S(16) * (1 - appear)));
            PaintRow(g, r.Profile, rr, i == selectedIndex, i == hoverIndex, now);
            if (appear < 1)
                using (var b = new SolidBrush(Color.FromArgb((int)(255 * (1 - appear)), Ui.Bg))) g.FillRectangle(b, Rectangle.Inflate(rr, S(3), S(3)));
        }

        // soft fades at the scroll edges
        if (scroll > 1 && clip.Top < S(18))
            using (var b = new LinearGradientBrush(new Rectangle(0, 0, Width, S(18)), Ui.Bg, Color.FromArgb(0, Ui.Bg), 90F)) g.FillRectangle(b, 0, 0, Width, S(18) - 1);
        if (scroll < MaxScroll - 1 && clip.Bottom > Height - S(24))
            using (var b = new LinearGradientBrush(new Rectangle(0, Height - S(24), Width, S(24)), Color.FromArgb(0, Ui.Bg), Ui.Bg, 90F)) g.FillRectangle(b, 0, Height - S(24) + 1, Width, S(24));

        var thumb = ThumbRect();
        if (!thumb.IsEmpty && clip.Right > Width - S(16))
        {
            using (var p = new Pen(Color.FromArgb(60, Ui.Border), S(1))) g.DrawLine(p, thumb.X + thumb.Width / 2F, S(4), thumb.X + thumb.Width / 2F, Height - S(4));
            using (var b = new LinearGradientBrush(thumb, Ui.Accent, Ui.Accent2, 90F)) using (var path = Ui.Round(thumb, thumb.Width / 2F)) g.FillPath(b, path);
        }
    }

    void PaintHeader(Graphics g, ListRow r, Rectangle rr)
    {
        var x = rr.X + S(6); var cy = rr.Y + rr.Height / 2 + S(2);
        if (r.Icon != Glyph.None) { Glyphs.Draw(g, r.Icon, new RectangleF(x, cy - S(9), S(18), S(18)), Ui.Accent); x += S(28); }
        var sz = Ui.Measure(g, r.Header, Section);
        TextRenderer.DrawText(g, r.Header, Section, new Point(x, cy - sz.Height / 2), Ui.Text, Ui.Flags);
        var lx = x + sz.Width + S(14);
        if (lx < rr.Right - S(10))
            using (var b = new LinearGradientBrush(new Rectangle(lx, cy, rr.Right - lx, 1), Ui.BorderStrong, Color.FromArgb(0, Ui.BorderStrong), LinearGradientMode.Horizontal))
            using (var p = new Pen(b, 1)) g.DrawLine(p, lx, cy, rr.Right, cy);
    }

    static Color A(Color c, float alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, (int)(alpha * c.A))), c); }

    void PaintRow(Graphics g, VpnProfile p, Rectangle rr, bool selected, bool hover, float now)
    {
        var id = p.Id;
        var pinned = IsPinned(p);
        var connecting = id == connectingId;
        var disconnecting = id == disconnectingId;
        var success = id == successId ? (now - successT0) : -1F;
        var fail = id == failId ? (now - failT0) : -1F;
        var live = LiveText(p);
        var radius = S(12);
        float h, s; hoverAmt.TryGetValue(id, out h); if (!hoverAmt.ContainsKey(id) && hover) h = 1;
        if (!selAmt.TryGetValue(id, out s)) s = selected ? 1 : 0;
        if (connecting) s = 1;

        // failure: damped horizontal shake
        if (fail >= 0) rr.Offset((int)(Math.Sin(fail * 46) * S(7) * (1 - fail / FailDuration)), 0);

        // card base (fades between idle and hover)
        var idle = Ui.Blend(Ui.Bg, Ui.Surface, 0.8F);
        Ui.FillRound(g, Ui.Blend(idle, Ui.SurfaceHover, h), rr, radius);
        Ui.DrawRound(g, Ui.Blend(Ui.Border, Ui.BorderStrong, h), rr, radius);

        // selection (fades in/out)
        if (s > 0.01F)
        {
            using (var path = Ui.Round(rr, radius))
            using (var b = new LinearGradientBrush(rr, A(Ui.Blend(Ui.Surface, Ui.Accent, Themes.IsLight ? 0.14F : 0.26F), s), A(Ui.Blend(Ui.Surface, Ui.Accent, 0.04F), s), LinearGradientMode.Horizontal)) g.FillPath(b, path);
            using (var path = Ui.Round(new RectangleF(rr.X - S(1.5F), rr.Y - S(1.5F), rr.Width + S(3), rr.Height + S(3)), radius + S(1.5F)))
            using (var pen = new Pen(Color.FromArgb((int)(50 * s), Ui.Accent), S(2.5F))) g.DrawPath(pen, path);
            Ui.DrawRound(g, Color.FromArgb((int)(220 * s), Ui.Accent), rr, radius, S(1.3F));
            var barH = (rr.Height - S(32)) * Ease.OutCubic(s);
            Ui.FillRound(g, Ui.Accent, new RectangleF(rr.X + S(7), rr.Y + rr.Height / 2F - barH / 2, S(3.5F), barH), S(1.75F));
        }

        // connecting: light sweep across the card + indeterminate progress line at the bottom
        if (connecting)
        {
            var t = now - connectT0;
            var state = g.Save();
            using (var clipPath = Ui.Round(rr, radius)) g.SetClip(clipPath, CombineMode.Intersect);
            var band = S(110); var bx = rr.X - band + (rr.Width + 2 * band) * Ease.Frac(t / 1.5F);
            using (var b = new LinearGradientBrush(new RectangleF(bx - 1, rr.Y, band + 2, rr.Height), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
            {
                b.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0, Ui.Accent), Color.FromArgb(Themes.IsLight ? 34 : 46, Ui.Accent), Color.FromArgb(0, Ui.Accent) }, Positions = new[] { 0F, 0.5F, 1F } };
                g.FillRectangle(b, bx, rr.Y, band, rr.Height);
            }
            var seg = rr.Width * 0.3F; var sx = rr.X - seg + (rr.Width + seg) * Ease.Frac(t / 1.15F);
            using (var b = new LinearGradientBrush(new RectangleF(sx - 1, 0, seg + 2, 1), Color.FromArgb(0, Ui.Accent), Ui.Accent, LinearGradientMode.Horizontal))
                g.FillRectangle(b, sx, rr.Bottom - S(3), seg, S(2.5F));
            g.Restore(state);
        }

        // success: green flash over the card
        if (success >= 0)
        {
            var k = 1 - success / SuccessDuration;
            using (var path = Ui.Round(rr, radius)) using (var b = new SolidBrush(Color.FromArgb((int)(60 * k), Ui.Success))) g.FillPath(b, path);
            Ui.DrawRound(g, Color.FromArgb((int)(230 * k), Ui.Success), rr, radius, S(1.6F));
        }
        if (fail >= 0) Ui.DrawRound(g, Color.FromArgb((int)(230 * (1 - fail / FailDuration)), Danger), rr, radius, S(1.6F));

        // avatar: rounded square with a soft gradient in the profile's color
        var d = S(44); var av = new Rectangle(rr.X + S(20), rr.Y + (rr.Height - d) / 2, d, d);
        var forti = FortiStore.IsForti(p);
        var ac = forti ? Color.FromArgb(226, 64, 52) : Ui.AvatarColor(p.Name);
        using (var path = Ui.Round(av, S(11)))
        using (var b = new LinearGradientBrush(av, Ui.Blend(Ui.Bg, ac, Themes.IsLight ? 0.35F : 0.55F), Ui.Blend(Ui.Bg, ac, Themes.IsLight ? 0.18F : 0.28F), 45F)) g.FillPath(b, path);
        Ui.DrawRound(g, Color.FromArgb(Themes.IsLight ? 90 : 70, ac), av, S(11));
        var isz = Ui.Measure(g, p.Initials, AvatarFont);
        TextRenderer.DrawText(g, p.Initials, AvatarFont, new Point(av.X + (d - isz.Width) / 2, av.Y + (d - isz.Height) / 2),
            Themes.IsLight ? Ui.Blend(ac, Color.Black, 0.5F) : Ui.Blend(ac, Color.White, 0.6F), Ui.Flags);

        // status dot (green = connected/reachable, red = unreachable); the connected one breathes
        var reach = ReachOf(p);
        if (live != null || reach == ReachState.Up || reach == ReachState.Down)
        {
            var dot = live != null || reach == ReachState.Up ? Ui.Success : Danger;
            var ds = S(13); var dr = new RectangleF(av.Right - ds + S(4), av.Bottom - ds + S(4), ds, ds);
            if (live != null)
            {
                var ph = Ease.Frac(now / 1.8F);
                var grow = S(10) * Ease.OutCubic(ph);
                using (var b = new SolidBrush(Color.FromArgb((int)(110 * (1 - ph)), Ui.Success))) g.FillEllipse(b, dr.X - grow / 2, dr.Y - grow / 2, dr.Width + grow, dr.Height + grow);
            }
            using (var b = new SolidBrush(Ui.Blend(idle, Ui.SurfaceHover, h))) g.FillEllipse(b, dr);
            dr.Inflate(-S(2.5F), -S(2.5F));
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, dr);
        }

        // connect button
        var plug = PlugRect(rr);
        PaintPlug(g, plug, hover && hoverPlug, s, connecting, disconnecting, success, now);

        // time / state pill
        var dots = new string('.', (int)(now * 3) % 4);
        var pillText = connecting ? "Bağlanıyor" + dots : disconnecting ? "Kesiliyor" + dots : live ?? Ui.Relative(p.LastConnected);
        var sizeText = connecting ? "Bağlanıyor..." : disconnecting ? "Kesiliyor..." : pillText;
        var pillRight = plug.X - S(10);
        var pillLeft = pillRight;
        if (pillText.Length > 0)
        {
            var ts = Ui.Measure(g, sizeText, Pill);
            var pw = ts.Width + S(36);
            var pr = new Rectangle(pillRight - pw, rr.Y + (rr.Height - S(28)) / 2, pw, S(28));
            pillLeft = pr.X;
            var tone = connecting ? Ui.Accent : disconnecting ? Danger : live != null ? Ui.Success : Ui.Muted;
            Ui.FillRound(g, live != null || connecting || disconnecting ? Color.FromArgb(Themes.IsLight ? 26 : 34, tone) : Ui.Blend(Ui.Bg, Ui.Surface, 0.35F), pr, pr.Height / 2F);
            Ui.DrawRound(g, live != null || connecting || disconnecting ? Color.FromArgb(100, tone) : Ui.Border, pr, pr.Height / 2F);
            var ir = new RectangleF(pr.X + S(10), pr.Y + (pr.Height - S(14)) / 2F, S(14), S(14));
            if (connecting || disconnecting)
                using (var pen = new Pen(tone, S(1.8F)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, ir, now * 420 % 360, 260);
            else if (live != null)
                using (var b = new SolidBrush(Color.FromArgb((int)(160 + 95 * Math.Abs(Math.Sin(now * 2.2))), Ui.Success))) g.FillEllipse(b, ir.X + S(3), ir.Y + S(3), ir.Width - S(6), ir.Height - S(6));
            else Glyphs.Draw(g, Glyph.History, ir, tone);
            TextRenderer.DrawText(g, pillText, Pill, new Point(pr.X + S(28), pr.Y + (pr.Height - ts.Height) / 2), live != null || connecting || disconnecting ? tone : Ui.Muted, Ui.Flags);
        }

        var star = StarRect(rr);
        if (pinned || hover)
        {
            var sc = new PointF(star.X + star.Width / 2F, star.Y + star.Height / 2F);
            var pts = Ui.Star(sc.X, sc.Y, S(6.5F));
            if (pinned) using (var b = new SolidBrush(Ui.Gold)) g.FillPolygon(b, pts);
            else using (var pen = new Pen(Color.FromArgb((int)(255 * Math.Max(h, 0.3F)), hoverStar ? Ui.Gold : Ui.Faint), S(1.3F)) { LineJoin = LineJoin.Round }) g.DrawPolygon(pen, pts);
        }

        // text
        var x = av.Right + S(16);
        var textRight = Math.Min(pillLeft, (pinned || hover) ? star.X : pillLeft) - S(10);
        DrawHighlighted(g, p.Name, RowName, Ui.Text, new Rectangle(x, rr.Y + S(13), textRight - x, S(22)));
        var sub = forti ? p.Source + (p.Host.Length > 0 ? "  ·  " + p.Host : "") : p.Host.Length > 0 ? p.Host : "Sunucu adresi yok";
        if (p.Username.Length > 0) sub += "  ·  " + p.Username;
        DrawHighlighted(g, sub, RowSub, Ui.Muted, new Rectangle(x, rr.Y + S(38), pillLeft - S(10) - x, S(20)));
    }

    void PaintPlug(Graphics g, Rectangle plug, bool hot, float sel, bool connecting, bool disconnecting, float success, float now)
    {
        var icon = new RectangleF(plug.X + S(10), plug.Y + S(10), S(18), S(18));
        if (success >= 0)
        {
            // green button, check mark drawn in, ring burst and particles
            var c = new PointF(plug.X + plug.Width / 2F, plug.Y + plug.Height / 2F);
            var pop = Ease.OutBack(success / 0.35F);
            var grow = (pop - 1) * S(12); // pops slightly larger, then settles
            var br = RectangleF.Inflate(plug, grow, grow);
            using (var path = Ui.Round(br, S(9))) using (var b = new LinearGradientBrush(br, Ui.Blend(Ui.Success, Color.White, 0.15F), Ui.Blend(Ui.Success, Color.Black, 0.12F), 90F)) g.FillPath(b, path);
            var ck = Ease.OutCubic((success - 0.05F) / 0.35F);
            if (ck > 0) DrawCheck(g, c, S(8), ck);
            var rt = success / 0.75F;
            if (rt < 1)
            {
                var rad = plug.Width / 2F + S(34) * Ease.OutCubic(rt);
                using (var pen = new Pen(Color.FromArgb((int)(200 * (1 - rt)), Ui.Success), S(2.5F) * (1 - rt) + 0.8F)) g.DrawEllipse(pen, c.X - rad, c.Y - rad, rad * 2, rad * 2);
            }
            var pt = success / 0.9F;
            if (pt < 1)
                for (var i = 0; i < 12; i++)
                {
                    var a = (i * 30 + 15) * Math.PI / 180;
                    var dist = plug.Width / 2F + S(30) * Ease.OutCubic(pt) * (i % 2 == 0 ? 1 : 0.75F);
                    var size = S(4) * (1 - pt);
                    using (var b = new SolidBrush(Color.FromArgb((int)(230 * (1 - pt)), i % 3 == 0 ? Ui.Accent : Ui.Success)))
                        g.FillEllipse(b, c.X + (float)Math.Cos(a) * dist - size / 2, c.Y + (float)Math.Sin(a) * dist - size / 2, size, size);
                }
            return;
        }

        if (connecting)
        {
            var t = now;
            var c = new PointF(plug.X + plug.Width / 2F, plug.Y + plug.Height / 2F);
            // expanding pulse rings
            for (var k = 0; k < 2; k++)
            {
                var ph = Ease.Frac(t / 1.2F + k * 0.5F);
                var rad = plug.Width / 2F + S(20) * Ease.OutCubic(ph);
                using (var pen = new Pen(Color.FromArgb((int)(140 * (1 - ph)), Ui.Accent), S(1.6F))) g.DrawEllipse(pen, c.X - rad, c.Y - rad, rad * 2, rad * 2);
            }
            using (var path = Ui.Round(plug, S(9))) using (var b = new LinearGradientBrush(plug, Ui.Blend(Ui.Accent, Color.White, 0.12F), Ui.Accent2, 90F)) g.FillPath(b, path);
            // orbiting comet around the button
            var orbit = RectangleF.Inflate(plug, S(5), S(5)); var start = t * 330 % 360;
            for (var k = 0; k < 4; k++)
                using (var pen = new Pen(Color.FromArgb(230 - k * 55, Ui.Blend(Ui.Accent, Color.White, 0.35F)), S(2.4F) - k * S(0.4F)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, orbit, start - k * 16, 18);
            // the plug "pushes in" and out
            var nudge = (float)Math.Sin(t * 9) * S(1.4F);
            Glyphs.Draw(g, Glyph.Plug, new RectangleF(icon.X + nudge, icon.Y - nudge, icon.Width, icon.Height), Color.White);
            return;
        }

        if (disconnecting)
        {
            var pulse = (float)(0.5 + 0.5 * Math.Sin(now * 6));
            Ui.FillRound(g, Color.FromArgb((int)(30 + 30 * pulse), Danger), plug, S(9));
            Ui.DrawRound(g, Color.FromArgb((int)(140 + 100 * pulse), Danger), plug, S(9), S(1.4F));
            Glyphs.Draw(g, Glyph.Disconnect, icon, Danger);
            return;
        }

        // idle / hover, cross-faded with the selected (filled orange) look
        Ui.FillRound(g, hot ? Ui.Blend(Ui.Surface, Ui.Accent, 0.15F) : Ui.Blend(Ui.Bg, Ui.Surface, 0.5F), plug, S(9));
        Ui.DrawRound(g, hot ? Ui.Accent : Ui.BorderStrong, plug, S(9));
        if (sel > 0.01F)
            using (var path = Ui.Round(plug, S(9))) using (var b = new LinearGradientBrush(plug, A(Ui.Blend(Ui.Accent, Color.White, 0.12F), sel), A(Ui.Accent2, sel), 90F)) g.FillPath(b, path);
        Glyphs.Draw(g, Glyph.Plug, icon, sel > 0.5F ? Color.White : hot ? Ui.Accent : Ui.Text);
    }

    // Check mark drawn progressively (progress 0..1).
    static void DrawCheck(Graphics g, PointF c, float size, float progress)
    {
        var a = new PointF(c.X - size * 0.7F, c.Y + size * 0.05F); var b = new PointF(c.X - size * 0.15F, c.Y + size * 0.6F); var e = new PointF(c.X + size * 0.8F, c.Y - size * 0.55F);
        var l1 = Dist(a, b); var l2 = Dist(b, e); var len = (l1 + l2) * progress;
        using (var pen = new Pen(Color.White, Ui.S(2.4F)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            if (len <= l1) { g.DrawLine(pen, a, Lerp(a, b, len / l1)); return; }
            g.DrawLines(pen, new[] { a, b, Lerp(b, e, (len - l1) / l2) });
        }
    }
    static float Dist(PointF a, PointF b) { var dx = a.X - b.X; var dy = a.Y - b.Y; return (float)Math.Sqrt(dx * dx + dy * dy); }
    static PointF Lerp(PointF a, PointF b, float t) { return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t); }

    // Draws text with the parts that match the search terms in the accent color.
    void DrawHighlighted(Graphics g, string text, Font font, Color color, Rectangle bounds)
    {
        if (bounds.Width <= 0) return;
        var mask = MatchMask(text);
        var full = Ui.Measure(g, text, font);
        if (mask == null || full.Width > bounds.Width)
        {
            TextRenderer.DrawText(g, text, font, bounds, color, Ui.Flags | TextFormatFlags.EndEllipsis);
            return;
        }
        var x = bounds.X; var start = 0;
        while (start < text.Length)
        {
            var end = start; var on = mask[start];
            while (end < text.Length && mask[end] == on) end++;
            var seg = text.Substring(start, end - start);
            TextRenderer.DrawText(g, seg, font, new Point(x, bounds.Y), on ? Ui.Accent : color, Ui.Flags);
            x += Ui.Measure(g, seg, font).Width;
            start = end;
        }
    }

    bool[] MatchMask(string text)
    {
        if (terms.Length == 0 || string.IsNullOrEmpty(text)) return null;
        var key = new StringBuilder(); var map = new List<int>();
        for (var i = 0; i < text.Length; i++) foreach (var c in ProfileStore.CharKey(text[i])) { key.Append(c); map.Add(i); }
        var k = key.ToString(); var mask = new bool[text.Length]; var any = false;
        foreach (var t in terms)
        {
            var idx = k.IndexOf(t, StringComparison.Ordinal);
            while (idx >= 0) { for (var j = idx; j < idx + t.Length; j++) mask[map[j]] = true; any = true; idx = k.IndexOf(t, idx + t.Length, StringComparison.Ordinal); }
        }
        return any ? mask : null;
    }

    void PaintEmpty(Graphics g)
    {
        var cy = Math.Max(S(40), Height / 4);
        var d = S(64); var c = new Rectangle((Width - d) / 2, cy, d, d);
        Ui.FillRound(g, Ui.Surface, c, S(18)); Ui.DrawRound(g, Ui.Border, c, S(18));
        Ui.DrawMagnifier(g, Ui.Accent, c.X + S(21), c.Y + S(21), S(22));
        var y = c.Bottom + S(18);
        foreach (var line in new[] { emptyTitle, emptyHint })
        {
            if (string.IsNullOrEmpty(line)) continue;
            var font = line == emptyTitle ? RowName : RowSub;
            var rect = new Rectangle(S(24), y, Width - S(48), S(60));
            TextRenderer.DrawText(g, line, font, rect, line == emptyTitle ? Ui.Text : Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            y += TextRenderer.MeasureText(g, line, font, new Size(rect.Width, 1000), TextFormatFlags.WordBreak).Height + S(4);
        }
    }
}

// =====================================================================
// Main window: a borderless side panel that docks flush to OpenVPN Connect
// =====================================================================

internal sealed class SearchForm : Form
{
    const int WM_HOTKEY = 0x0312, WM_MOVING = 0x0216, WM_SIZING = 0x0214, WM_EXITSIZEMOVE = 0x0232, WM_NCHITTEST = 0x0084, HotkeyId = 0x5651;
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
    readonly HeaderButton moreButton = new HeaderButton(HeaderGlyph.More), minButton = new HeaderButton(HeaderGlyph.Minimize), closeButton = new HeaderButton(HeaderGlyph.Close);
    readonly System.Windows.Forms.Timer watcher = new System.Windows.Forms.Timer(), statusTimer = new System.Windows.Forms.Timer();
    // fadeTimer: window fade-in; ambient: header shimmer + breathing dots (only while visible); trimTimer: one-shot memory trim after hiding
    readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer { Interval = 15 }, ambient = new System.Windows.Forms.Timer { Interval = 100 }, trimTimer = new System.Windows.Forms.Timer { Interval = 4000 };
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    PointF[] shimmerPath; Rectangle shimmerArea, lastShimmer;
    string connectingProfileId; DateTime connectStarted, disconnectStarted = DateTime.MinValue, lastShowScan; int fadeStep;
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
    int HeaderHeight { get { return S(66); } }
    int ListTop { get { return S(136); } }
    int FooterHeight { get { return S(70); } }

    public SearchForm(bool background, EventWaitHandle activation, AppSettings appSettings)
    {
        backgroundMode = background; openVpnPath = FindOpenVpnPath();
        settings = appSettings ?? AppSettings.Defaults();
        var area = Screen.PrimaryScreen.WorkingArea;
        AutoScaleMode = AutoScaleMode.None;
        Text = "OpenVPN Hızlı Arama"; StartPosition = FormStartPosition.Manual; BackColor = Ui.Bg; ForeColor = Ui.Text; Font = Ui.SubFont;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; MaximizeBox = false; KeyPreview = true; DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        MinimumSize = new Size(S(360), S(360)); Size = new Size(S(430), Math.Min(S(720), area.Height));
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
        fadeTimer.Tick += delegate { fadeStep++; var o = Ease.OutCubic(fadeStep / 12F); if (fadeStep >= 12) { fadeTimer.Stop(); o = 1; } Opacity = o; };
        ambient.Tick += delegate { AmbientTick(); };
        trimTimer.Tick += delegate { trimTimer.Stop(); if (!Visible) NativeMethods.TrimMemory(); };
        statusTimer.Interval = 2600; statusTimer.Tick += delegate { statusTimer.Stop(); status = null; InvalidateFooter(); };

        // modules: server reachability, live connection status, drag & drop import
        Reachability.Init(SynchronizationContext.Current);
        Reachability.Changed += delegate { list.Invalidate(); };
        list.ReachOf = p => settings.ReachabilityCheck ? Reachability.Get(p.Id) : ReachState.Unknown;
        list.LiveText = LiveTextFor;
        list.AllowAmbient = () => Watched;
        monitor = new ConnectionMonitor(() => profiles);
        monitor.Changed += delegate { OnConnectionChanged(); };
        AllowDrop = true; list.AllowDrop = true;
        DragEnter += OnDragEnter; list.DragEnter += OnDragEnter;
        DragDrop += OnDragDrop; list.DragDrop += OnDragDrop;

        ReloadProfiles();
        monitor.Poll();

        hookProc = OnWinEvent;
        IntPtr h; lastFound = FindOpenVpnWindow(out h);
        if (lastFound) TrackOpenVpn(h);
        allowVisible = !backgroundMode || lastFound;
        noActivate = backgroundMode;
        PlaceWindow();

        var handle = Handle; // create handle now so hotkey and activation work while hidden
        foregroundHook = NativeMethods.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, hookProc, 0, 0, WINEVENT_SKIPOWNPROCESS);
        if (activation != null)
            activationWait = ThreadPool.RegisterWaitForSingleObject(activation, delegate
            {
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(delegate { manualOverride = true; ShowAndFocus(true); })); } catch { }
            }, null, -1, false);
        watcher.Start();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= CS_DROPSHADOW; return cp; }
    }

    // ---------- Layout ----------

    void BuildLayout()
    {
        foreach (var b in new[] { closeButton, minButton, moreButton }) { b.BackColor = Color.Transparent; b.Anchor = AnchorStyles.Top | AnchorStyles.Right; Controls.Add(b); }
        closeButton.Location = new Point(ClientSize.Width - S(10) - closeButton.Width, S(18));
        minButton.Location = new Point(closeButton.Left - S(4) - minButton.Width, S(18));
        moreButton.Location = new Point(minButton.Left - S(4) - moreButton.Width, S(18));
        closeButton.Click += delegate { HideForLater(); };
        minButton.Click += delegate { HideForLater(); };
        moreButton.Click += delegate { ShowMainMenu(); };

        search.Location = new Point(S(10), HeaderHeight); search.Size = new Size(ClientSize.Width - S(20), S(64));
        search.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        search.Hotkey = settings.Hotkey;
        Input.TextChanged += delegate { list.AnimateNext = true; ApplyFilter(null); };
        Controls.Add(search);

        list.Location = new Point(0, ListTop); list.Size = new Size(ClientSize.Width, ClientSize.Height - ListTop - FooterHeight);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.IsPinned = p => pins.Contains(p.Id);
        list.Activated += Connect;
        list.PinToggled += TogglePin;
        list.MenuRequested += ShowRowMenu;
        Controls.Add(list);
    }

    static readonly Font TitleFont = new Font("Segoe UI Semibold", 15F);
    static readonly Font PillFont = new Font("Segoe UI", 9F);
    static readonly Font FooterFont = new Font("Segoe UI", 9F);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var w = ClientSize.Width;

        // Only the regions inside the clip are painted (ambient effects invalidate small areas).
        var now = (float)clock.Elapsed.TotalSeconds;
        if (e.ClipRectangle.Top < HeaderHeight) PaintTop(g, e.ClipRectangle, w, now);
        var fyClip = ClientSize.Height - FooterHeight;
        if (e.ClipRectangle.Bottom > fyClip) PaintFooter(g, w);

        // edge: a thin seam toward OpenVPN when docked, a full outline when floating
        using (var p = new Pen(Ui.Border))
        {
            if (docked) { var sx2 = dockSide > 0 ? 0 : ClientSize.Width - 1; g.DrawLine(p, sx2, 0, sx2, ClientSize.Height); }
            else g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }

    void PaintTop(Graphics g, Rectangle clip, int w, float now)
    {
        var e = new { ClipRectangle = clip };
        // header: light streak, shield mark, divider, title, count pill
        var waveRect = new Rectangle((int)(w * 0.28F), -S(4), (int)(w * 0.72F), HeaderHeight - S(6));
        if (e.ClipRectangle.IntersectsWith(waveRect)) Ui.DrawWaves(g, waveRect, true);
        var mark = new RectangleF(S(16), S(17), S(34), S(34));
        using (var glow = new GraphicsPath())
        {
            glow.AddEllipse(mark.X - S(8), mark.Y - S(8), mark.Width + S(16), mark.Height + S(16));
            using (var pgb = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(Themes.IsLight ? 30 : 48, Ui.Accent), SurroundColors = new[] { Color.FromArgb(0, Ui.Accent) } }) g.FillPath(pgb, glow);
        }
        Glyphs.Draw(g, Glyph.ShieldLock, mark, Ui.Accent);
        using (var p = new Pen(Ui.BorderStrong)) g.DrawLine(p, S(62), S(22), S(62), S(46));
        var title = "Hızlı Arama";
        var ts = Ui.Measure(g, title, TitleFont);
        TextRenderer.DrawText(g, title, TitleFont, new Point(S(74), S(34) - ts.Height / 2), Ui.Text, Ui.Flags);
        if (subtitle.Length > 0)
        {
            var ps = Ui.Measure(g, subtitle, PillFont);
            var pr = new Rectangle(S(74) + ts.Width + S(12), S(34) - S(13), ps.Width + S(20), S(26));
            if (pr.Right < moreButton.Left - S(6))
            {
                Ui.FillRound(g, Ui.Blend(Ui.Bg, Ui.Surface, 0.6F), pr, S(8));
                Ui.DrawRound(g, Ui.BorderStrong, pr, S(8));
                TextRenderer.DrawText(g, subtitle, PillFont, new Point(pr.X + S(10), pr.Y + (pr.Height - ps.Height) / 2), Ui.Muted, Ui.Flags);
            }
        }
    }

    void PaintFooter(Graphics g, int w)
    {
        var now = (float)clock.Elapsed.TotalSeconds;
        // footer card: key hints on the left, VPN state on the right
        var fy = ClientSize.Height - FooterHeight;
        var card = new Rectangle(S(10), fy + S(8), w - S(20), FooterHeight - S(18));
        Ui.FillRound(g, Ui.Blend(Ui.Bg, Ui.Surface, 0.65F), card, S(12));
        Ui.DrawRound(g, Ui.Border, card, S(12));
        var cy = card.Y + card.Height / 2;

        string stateText; Color stateColor;
        if (connectedId != null || fortiConnectedId != null) { stateText = "Bağlı"; stateColor = Ui.Success; }
        else if (lastFound) { stateText = "VPN Hazır"; stateColor = Ui.Success; }
        else { stateText = "OpenVPN kapalı"; stateColor = Ui.Faint; }
        var ss = Ui.Measure(g, stateText, FooterFont);
        var sx = card.Right - S(14) - ss.Width;
        TextRenderer.DrawText(g, stateText, FooterFont, new Point(sx, cy - ss.Height / 2), stateColor, Ui.Flags);
        var pulse = Ease.Frac(now / 2F); var hr = S(8) + S(10) * Ease.OutCubic(pulse);
        if (stateColor != Ui.Faint) using (var halo = new SolidBrush(Color.FromArgb((int)(90 * (1 - pulse)), stateColor))) g.FillEllipse(halo, sx - S(12) - hr / 2F, cy - hr / 2F, hr, hr);
        using (var b = new SolidBrush(stateColor)) g.FillEllipse(b, sx - S(16), cy - S(4), S(8), S(8));
        var leftLimit = sx - S(28);

        if (status != null)
        {
            using (var p = new Pen(Ui.Success, S(2F)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(p, new[] { new PointF(card.X + S(16), cy), new PointF(card.X + S(20), cy + S(4)), new PointF(card.X + S(27), cy - S(4)) });
            TextRenderer.DrawText(g, status, FooterFont, new Rectangle(card.X + S(36), cy - S(9), leftLimit - card.X - S(36), S(18)), Ui.Text, Ui.Flags | TextFormatFlags.EndEllipsis);
        }
        else
        {
            var x = card.X + S(12); var first = true;
            foreach (var hint in new[] { new[] { "↑↓", "Seç" }, new[] { "Enter", "Bağlan" }, new[] { "Ctrl + P", "Sabitle" }, new[] { "Esc", "Gizle" } })
            {
                var kw = Ui.Measure(g, hint[0], Ui.KeyFontLarge).Width + S(14);
                var lw = Ui.Measure(g, hint[1], FooterFont).Width;
                var need = (first ? 0 : S(17)) + kw + S(7) + lw;
                if (x + need > leftLimit) break;
                if (!first) { using (var p = new Pen(Ui.Border)) g.DrawLine(p, x + S(8), cy - S(9), x + S(8), cy + S(9)); x += S(17); }
                var kr = new Rectangle(x, cy - S(12), kw, S(24));
                Ui.FillRound(g, Ui.Blend(Ui.Bg, Ui.Accent, 0.06F), kr, S(6));
                Ui.DrawRound(g, Ui.Blend(Ui.Accent, Ui.Bg, 0.35F), kr, S(6));
                var ks = Ui.Measure(g, hint[0], Ui.KeyFontLarge);
                TextRenderer.DrawText(g, hint[0], Ui.KeyFontLarge, new Point(kr.X + (kw - ks.Width) / 2, cy - ks.Height / 2), Ui.Text, Ui.Flags);
                x += kw + S(7);
                var ls = Ui.Measure(g, hint[1], FooterFont);
                TextRenderer.DrawText(g, hint[1], FooterFont, new Point(x, cy - ls.Height / 2), Ui.Muted, Ui.Flags);
                x += lw; first = false;
            }
        }
    }

    // Point on the header light streak for the moving highlight (sprite rectangle), or Empty.
    Rectangle ShimmerPoint(Rectangle waveRect, float now)
    {
        if (shimmerPath == null || shimmerArea != waveRect) { shimmerPath = Ui.WavePoints(waveRect); shimmerArea = waveRect; }
        if (shimmerPath.Length < 2) return Rectangle.Empty;
        var ph = Ease.Frac(now / 3.4F);
        if (ph > 0.75F) return Rectangle.Empty; // short pause between passes
        var idx = (int)((ph / 0.75F) * (shimmerPath.Length - 1));
        var p = shimmerPath[idx]; var d = S(28);
        return new Rectangle((int)p.X - d / 2, (int)p.Y - d / 2, d, d);
    }


    // True while the panel or OpenVPN is the foreground window (the user is looking at them).
    bool Watched
    {
        get
        {
            if (!Visible || WindowState == FormWindowState.Minimized) return false;
            var fg = NativeMethods.GetForegroundWindow();
            return fg == Handle || (ovpnHwnd != IntPtr.Zero && fg == ovpnHwnd) || ContainsFocus;
        }
    }

    void WakeAmbient() { if (Visible && Watched) { if (!ambient.Enabled) ambient.Start(); list.Wake(); } }

    // ~15 fps while watched: repaint only the small areas that move (shimmer, logo glow, status dot).
    void AmbientTick()
    {
        if (!Watched) { ambient.Stop(); Invalidate(); return; }
        var now = (float)clock.Elapsed.TotalSeconds; var w = ClientSize.Width;
        var waveRect = new Rectangle((int)(w * 0.28F), -S(4), (int)(w * 0.72F), HeaderHeight - S(6));
        // header is static now (no shimmer / logo pulse); only the footer status dot pulses
        var fy = ClientSize.Height - FooterHeight;
        Invalidate(new Rectangle(w - S(150), fy + S(8), S(140), FooterHeight - S(18)));     // status dot pulse
    }

    void InvalidateHeader() { Invalidate(new Rectangle(0, 0, ClientSize.Width, HeaderHeight)); }
    void InvalidateFooter() { Invalidate(new Rectangle(0, ClientSize.Height - FooterHeight, ClientSize.Width, FooterHeight)); }

    void SetStatus(string text) { status = text; statusTimer.Stop(); statusTimer.Start(); InvalidateFooter(); }

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
        tray = new NotifyIcon { Icon = IconArt.Load(SystemInformation.SmallIconSize) ?? Icon ?? SystemIcons.Application, Text = "OpenVPN Hızlı Arama", ContextMenuStrip = trayMenu, Visible = true };
        tray.MouseClick += delegate(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (Visible && WindowState != FormWindowState.Minimized) HideForLater(); else { manualOverride = true; ShowAndFocus(true); }
        };
    }

    // Status card for the top of the menus: connection + last backup.
    MenuStatusItem StatusCard()
    {
        var connected = ConnectedProfile();
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
        if (!FortiStore.IsForti(p)) rowMenu.Items.Add(Item(".ovpn olarak dışa aktar…", Glyph.Export, delegate { ExportProfile(p); }));
        if (!FortiStore.IsForti(p)) rowMenu.Items.Add(Danger("Profili sil…", Glyph.Delete, delegate { DeleteProfile(p); }));
        MenuKit.Fit(rowMenu);
        rowMenu.Show(list, at);
    }

    // ---------- Connection status ----------

    VpnProfile ConnectedProfile()
    {
        if (connectedId == null) return null;
        return profiles.FirstOrDefault(p => p.Id == connectedId);
    }

    string LiveTextFor(VpnProfile p)
    {
        if (fortiConnectedId != null && p.Id == fortiConnectedId) return "Bağlı · " + Since(fortiSince);
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

    List<VpnProfile> OpenVpnProfiles { get { return profiles.Where(p => !FortiStore.IsForti(p)).ToList(); } }

    // ---------- FortiClient ----------

    string fortiConnectedId, fortiConnectingId; DateTime fortiSince, fortiConnectStart; bool fortiPolling, fortiOpening;

    void ConnectForti(VpnProfile p)
    {
        if (p.Id == fortiConnectedId) { list.ConnectSucceeded(p.Id); SetStatus("\"" + p.Name + "\" zaten bağlı"); return; }
        if (fortiOpening) return;
        // SSL tunnels whose gateway does not let FortiClient keep the password: use the credential
        // saved in Windows Credential Manager, or ask for it once.
        FortiCredential cred = null;
        var tunnel = FortiStore.TunnelName(p);
        if (p.Id.StartsWith(FortiStore.IdPrefixSsl, StringComparison.Ordinal) && !FortiCredentialStore.FortiKeepsPassword(tunnel))
        {
            try { cred = FortiCredentialStore.Load(tunnel); } catch { cred = null; }
            if (cred == null) cred = AskFortiCredential(p, null);
        }
        fortiOpening = true;
        fortiConnectingId = p.Id; fortiConnectStart = DateTime.UtcNow;
        list.BeginConnect(p.Id);
        SetStatus("FortiClient'ta \"" + p.Name + "\" seçiliyor…");
        // Open() may restart the FortiClient window (a few seconds), so it runs off the UI thread.
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err;
            try { err = FortiStore.Open(p); } catch (Exception ex) { err = ex.Message; }
            var outcome = FortiGui.Outcome.NotReady;
            if (err == null) outcome = PressFortiConnect(tunnel, cred);
            if (cred != null) cred.Password = "";
            UiInvoke(delegate
            {
                fortiOpening = false;
                if (err != null) { if (fortiConnectingId == p.Id) fortiConnectingId = null; list.ConnectFailed(p.Id); SetStatus(err); return; }
                if (outcome == FortiGui.Outcome.Pressed) SetStatus("FortiClient \"" + p.Name + "\" bağlanıyor…");
                else if (outcome == FortiGui.Outcome.NeedsPassword) SetStatus("FortiClient'ta \"" + p.Name + "\" seçildi — parolayı girip Connect'e basın");
                else if (outcome == FortiGui.Outcome.FillFailed) SetStatus("FortiClient formu doldurulamadı — \"" + p.Name + "\" için parolayı elle girin");
                else SetStatus("FortiClient'ta \"" + p.Name + "\" seçildi — Connect'e basın");
            });
        });
    }

    // Runs on a worker thread after FortiStore.Open(): waits up to 20 s for FortiClient's form to
    // show the selected tunnel, then presses Connect when the password is saved. Any other outcome
    // leaves the window to the user, as before.
    static FortiGui.Outcome PressFortiConnect(string tunnel, FortiCredential cred)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var outcome = FortiGui.Outcome.NotReady;
        while (DateTime.UtcNow < deadline)
        {
            string detail;
            try { outcome = FortiGui.TryConnect(tunnel, cred, true, out detail); }
            catch { outcome = FortiGui.Outcome.NotReady; }
            if (outcome == FortiGui.Outcome.Pressed || outcome == FortiGui.Outcome.NeedsPassword || outcome == FortiGui.Outcome.FillFailed) return outcome;
            Thread.Sleep(500);
        }
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
            try { name = FortiStore.ConnectedTunnel(); } catch { }
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
        if (fortiConnectingId != null && (DateTime.UtcNow - fortiConnectStart).TotalSeconds > 120)
        {
            list.CancelConnect(fortiConnectingId); fortiConnectingId = null;
        }
        if (id == fortiConnectedId) return;
        fortiConnectedId = id;
        if (id != null) fortiSince = DateTime.UtcNow;
        var sel = list.SelectedProfile;
        ApplyFilter(sel != null ? sel.Id : null);
        if (id != null && id == fortiConnectingId) { fortiConnectingId = null; list.ConnectSucceeded(id); }
        var p2 = profiles.FirstOrDefault(p => p.Id == id);
        if (p2 != null) SetStatus("\"" + p2.Name + "\" (FortiClient) bağlandı");
        InvalidateFooter();
    }

    void OnConnectionChanged()
    {
        var c = monitor.Current;
        var id = c != null && c.Connected ? c.ProfileId : null;
        if (id == connectedId) return;
        connectedId = id;
        var selected = list.SelectedProfile;
        ApplyFilter(selected != null ? selected.Id : null);
        if (id == null) list.EndDisconnect();
        if (id != null && id == connectingProfileId)
        {
            // the profile we asked for is up: play the success animation
            connectingProfileId = null;
            list.ConnectSucceeded(id);
            if (!docked) { var t = new System.Windows.Forms.Timer { Interval = 1800 }; t.Tick += delegate { t.Stop(); t.Dispose(); if (!docked) HideForLater(); }; t.Start(); }
        }
        if (id != null && c.ProfileName != null) SetStatus("\"" + c.ProfileName + "\" bağlandı");
        tray.Text = id != null && c.ProfileName != null ? Trim63("OpenVPN Hızlı Arama · Bağlı: " + c.ProfileName) : "OpenVPN Hızlı Arama";
        InvalidateFooter();
    }

    static void ClearMenu(ToolStrip m) { var old = new List<ToolStripItem>(); foreach (ToolStripItem it in m.Items) old.Add(it); m.Items.Clear(); foreach (var it in old) it.Dispose(); }

    static string Trim63(string s) { return s.Length > 63 ? s.Substring(0, 62) + "…" : s; }

    void Disconnect()
    {
        string err;
        var id = connectedId;
        if (ConnectionControl.Disconnect(out err)) { SetStatus("Bağlantı kesiliyor…"); if (id != null) list.BeginDisconnect(id); disconnectStarted = DateTime.UtcNow; }
        else MessageBox.Show(this, err ?? "Bağlantı kesilemedi.", "OpenVPN Hızlı Arama", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    else MessageBox.Show(this, "Yedek alınamadı:\n" + error, "OpenVPN Hızlı Arama", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                settings.LastBackupUtc = DateTime.UtcNow; SaveSettings();
                if (openSettings != null) openSettings.SetLastBackup(settings.LastBackupUtc, count);
                SetStatus(count + " profil yedeklendi · " + Path.GetFileName(zip));
                if (!silent && tray != null) tray.ShowBalloonTip(4000, "Yedek alındı", count + " profil yedeklendi.\n" + zip, ToolTipIcon.Info);
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
        if (MessageBox.Show(this, info, "VPN parolalarını yedekle", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
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
            catch (Exception ex) { MessageBox.Show(this, "Dışa aktarılamadı:\n" + ex.Message, "OpenVPN Hızlı Arama", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
        catch (Exception ex) { ImportService.CleanupTemp(tmp); MessageBox.Show(this, "Yedek okunamadı:\n" + ex.Message, "OpenVPN Hızlı Arama", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
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
            MessageBox.Show(this, "Seçilen " + files.Count + " profilin hepsi OpenVPN Connect'te zaten var.", "İçe aktarma", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var ask = files.Count + " profil bulundu. " + fresh + " tanesi OpenVPN Connect'e eklenecek, " + (files.Count - fresh) + " tanesi zaten var ve atlanacak.\n\n" +
                  "Kayıtlı parolalar yedeğe dahil değildir; ilk bağlantıda parola sorulabilir.\n\nDevam edilsin mi?";
        if (MessageBox.Show(this, ask, "İçe aktarma", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
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
                if (error != null) { MessageBox.Show(this, "İçe aktarma başarısız:\n" + error, "İçe aktarma", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                var msg = r.Imported + " profil eklendi, " + r.Skipped + " atlandı, " + r.Failed + " başarısız.";
                var details = r.Messages != null && r.Messages.Count > 0 ? "\n\n" + string.Join("\n", r.Messages.Take(15).ToArray()) : "";
                MessageBox.Show(this, msg + details, "İçe aktarma tamamlandı", MessageBoxButtons.OK, r.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
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
        if (MessageBox.Show(this, "\"" + p.Name + "\" profili OpenVPN Connect'ten kalıcı olarak silinecek.\nBu işlem geri alınamaz (yedeğiniz varsa geri yükleyebilirsiniz).\n\nDevam edilsin mi?",
            "Profili sil", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var err = RemoveProfile(p);
        if (err != null) MessageBox.Show(this, "Profil silinemedi:\n" + err, "Profili sil", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    void ShowSettings()
    {
        if (openSettings != null) { openSettings.Activate(); return; }
        if (!Visible) { manualOverride = true; ShowAndFocus(true); }
        settings.AutoStart = AutoStart.IsEnabled();
        using (var f = new SettingsForm(settings))
        {
            f.ProfileCount = profiles.Count;
            f.BackupNow = delegate { BackupNow(false); };
            f.OpenBackupFolder = OpenBackupFolder;
            f.Icon = Icon;
            openSettings = f;
            DialogResult dr;
            try { dr = f.ShowDialog(this); } finally { openSettings = null; }
            if (dr != DialogResult.OK || f.Result == null) { ApplyTheme(); return; } // undo live theme preview
            var r = f.Result;
            r.LastBackupUtc = settings.LastBackupUtc; // a backup may have run while the window was open
            var old = settings;
            settings = r;
            SaveSettings();
            if (r.AutoStart != AutoStart.IsEnabled()) try { AutoStart.SetEnabled(r.AutoStart); } catch (Exception ex) { SetStatus("Başlangıç ayarı değiştirilemedi: " + ex.Message); }
            if (r.Hotkey != old.Hotkey) { RegisterAppHotkey(true); search.Hotkey = r.Hotkey; }
            ApplyTheme();
            if (r.DockSide != old.DockSide) { autoSide = 0; lastRect = new NativeMethods.RECT(); docked = false; Sync(true); }
            if (r.ReachabilityCheck && !old.ReachabilityCheck) Reachability.CheckAll(profiles, false);
            if (r.ShowForti != old.ShowForti) { if (!r.ShowForti) fortiConnectedId = null; ReloadProfiles(true); }
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
        BackColor = Ui.Bg; ForeColor = Ui.Text;
        search.RefreshTheme();
        list.BackColor = Ui.Bg; search.Hotkey = settings.Hotkey;
        ApplyFrameStyle();
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
        MessageBox.Show(this,
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
            var on = Themes.IsLight ? 0 : 1; NativeMethods.DwmSetWindowAttribute(Handle, 20, ref on, 4);
            var corner = docked ? 3 : 2; // Windows 11: small rounding when docked, normal when floating
            NativeMethods.DwmSetWindowAttribute(Handle, 33, ref corner, 4);
            var border = Ui.Border.R | (Ui.Border.G << 8) | (Ui.Border.B << 16);
            NativeMethods.DwmSetWindowAttribute(Handle, 34, ref border, 4);
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
            if (Visible && ContainsFocus) HideForLater();
            else { manualOverride = true; ShowAndFocus(true); }
            return;
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
        // Alt+F4 only hides; shutdown, logoff, Task Manager and "Çıkış" really close.
        if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideForLater(); }
    }

    void Cleanup()
    {
        watcher.Stop(); statusTimer.Stop(); ambient.Stop(); fadeTimer.Stop(); trimTimer.Stop();
        if (monitor != null) { monitor.Dispose(); monitor = null; }
        if (processHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(processHook); processHook = IntPtr.Zero; }
        if (foregroundHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
        if (activationWait != null) { activationWait.Unregister(null); activationWait = null; }
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
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
            Opacity = 0; fadeStep = 0; fadeTimer.Start();
            list.AnimateNext = true; var sel = list.SelectedProfile; ApplyFilter(sel != null ? sel.Id : null);
            noActivate = !activate; Show(); noActivate = false;
            UpdateDockState();
            WakeAmbient();
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (activate) { Activate(); NativeMethods.SetForegroundWindow(Handle); Input.Focus(); Input.SelectAll(); }
    }

    void HideForLater()
    {
        manualOverride = false; Hide(); ambient.Stop(); fadeTimer.Stop(); Opacity = 1;
        trimTimer.Stop(); trimTimer.Start(); // give memory back a few seconds after hiding
    }

    // ---------- Profiles and filtering ----------

    void ReloadProfiles(bool force = false)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(ProfileStore.ConfigPath); } catch { stamp = DateTime.MinValue; }
        List<VpnProfile> loaded;
        try { loaded = ProfileStore.Load(); if (settings.ShowForti) loaded.AddRange(FortiStore.Load()); }
        catch (Exception ex)
        {
            // Keep the old list; configStamp is not updated, so the next check retries.
            loadError = ex.Message;
            if (profiles.Count == 0) ApplyFilter(null);
            return;
        }
        configStamp = stamp; loadError = null;
        var signature = string.Join("\u0002", loaded.Select(p => p.Id + "\u0001" + p.Name + "\u0001" + p.Host + "\u0001" + p.Username + "\u0001" + p.Source + "\u0001" + p.LastConnected).ToArray());
        if (!force && signature == profileSignature) return;
        profileSignature = signature; profiles = loaded;
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
        if (terms.Length == 0)
        {
            var live = profiles.Where(p => p.Id == connectedId || p.Id == fortiConnectedId).ToList();
            var liveIds = new HashSet<string>(live.Select(p => p.Id));
            var pinned = profiles.Where(p => pins.Contains(p.Id) && !liveIds.Contains(p.Id)).OrderBy(p => p.Name, byName).ToList();
            var recent = profiles.Where(p => !pins.Contains(p.Id) && !liveIds.Contains(p.Id) && p.LastConnected > 0).OrderByDescending(p => p.LastConnected).Take(5).ToList();
            var used = new HashSet<string>(live.Concat(pinned).Concat(recent).Select(p => p.Id));
            var rest = profiles.Where(p => !used.Contains(p.Id) && !FortiStore.IsForti(p)).OrderBy(p => p.Name, byName).ToList();
            var fortiRest = profiles.Where(p => !used.Contains(p.Id) && FortiStore.IsForti(p)).OrderBy(p => p.Name, byName).ToList();
            AddGroup(rows, "BAĞLI", live, Glyph.Plug);
            AddGroup(rows, "SABİTLENENLER", pinned, Glyph.Star);
            AddGroup(rows, "SON BAĞLANILANLAR", recent, Glyph.History);
            AddGroup(rows, fortiRest.Count > 0 ? "OPENVPN PROFİLLERİ" : used.Count > 0 ? "TÜM PROFİLLER" : null, rest);
            AddGroup(rows, "FORTICLIENT VPN", fortiRest, Glyph.ShieldLock);
            shownCount = profiles.Count;
        }
        else
        {
            var hits = profiles.Where(p => ProfileStore.Matches(p, terms))
                .OrderBy(p => ProfileStore.Score(p, terms)).ThenByDescending(p => pins.Contains(p.Id)).ThenByDescending(p => p.LastConnected).ThenBy(p => p.Name, byName).ToList();
            AddGroup(rows, hits.Count == 1 ? "1 SONUÇ" : hits.Count + " SONUÇ", hits, Glyph.Search);
            shownCount = hits.Count;
        }

        string title = "", hint = "";
        if (rows.Count == 0)
        {
            if (loadError != null) { title = "Profiller okunamadı"; hint = loadError; }
            else if (profiles.Count == 0) { title = "Kayıtlı profil yok"; hint = "OpenVPN Connect'e profil ekleyin; liste kendiliğinden güncellenir."; }
            else { title = "Sonuç bulunamadı"; hint = "\"" + Input.Text.Trim() + "\" ile eşleşen profil yok. Farklı bir kelime deneyin."; }
        }
        list.SetRows(rows, terms, title, hint, keepId);

        subtitle = loadError != null && profiles.Count == 0 ? "profil okunamadı"
            : terms.Length == 0 ? profiles.Count + " profil"
            : shownCount + " / " + profiles.Count;
        InvalidateHeader();
    }

    void TogglePin(VpnProfile p)
    {
        if (p == null) return;
        var pinned = !pins.Contains(p.Id);
        if (pinned) pins.Add(p.Id); else pins.Remove(p.Id);
        PinStore.Save(pins);
        ApplyFilter(p.Id);
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
        if (FortiStore.IsForti(p)) { ConnectForti(p); return; }
        if (p.Id == connectedId) { list.ConnectSucceeded(p.Id); SetStatus("\"" + p.Name + "\" zaten bağlı"); return; }
        if (p.Id == connectingProfileId) return;
        if (!File.Exists(openVpnPath)) { list.ConnectFailed(p.Id); MessageBox.Show(this, "OpenVPN Connect bulunamadı.", Text); return; }
        try
        {
            var arg = "--connect-shortcut=" + p.Id;
            if (p.Id.IndexOf(' ') >= 0) arg = "\"" + arg + "\"";
            using (Process.Start(new ProcessStartInfo { FileName = openVpnPath, Arguments = arg, UseShellExecute = true })) { }
        }
        catch (Exception ex) { list.ConnectFailed(p.Id); MessageBox.Show(this, "Bağlantı başlatılamadı:\n" + ex.Message, Text); return; }
        connectingProfileId = p.Id; connectStarted = DateTime.UtcNow;
        list.BeginConnect(p.Id);
        SetStatus("\"" + p.Name + "\" bağlanıyor…");
    }

    void CheckConnectTimeout()
    {
        if (connectingProfileId != null && (DateTime.UtcNow - connectStarted).TotalSeconds > ConnectTimeoutSeconds)
        {
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
            if (hwnd != IntPtr.Zero && hwnd == ovpnHwnd) { Sync(false); if (docked && Visible) RaiseAbove(); }
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
        if ((busyConnecting || tick % 4 == 0) && monitor != null) monitor.Poll();
        if (busyConnecting) CheckConnectTimeout();
        if (tick % 4 == 0) CheckConfigChanged();
        if (tick % 120 == 20) CheckAutoBackup();
        if (settings.ReachabilityCheck && tick % 1200 == 0) Reachability.CheckAll(profiles, true);
        if (!Visible && tick % 600 == 300) NativeMethods.TrimMemory(); // every 5 min while hidden
        if (settings.ShowForti && (fortiConnectingId != null ? tick % 4 == 2 : tick % 20 == 10) && (Visible || fortiConnectedId != null) && profiles.Any(FortiStore.IsForti)) PollForti();

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
        if (rising && !Visible) ShowAndFocus(false);
        else if (falling && Visible && !manualOverride && !userMoved) HideForLater();
        UpdateDockState();
    }

    void UpdateDockState()
    {
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
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
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
    const string MutexName = "Local\\OpenVpnQuickSearch.SingleInstance", EventName = "Local\\OpenVpnQuickSearch.Activate";
    const int ASFW_ANY = -1;

    [STAThread]
    static int Main(string[] args)
    {
        var background = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
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
                try { NativeMethods.SetProcessDPIAware(); } catch { }
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.Scale = Math.Max(1F, g.DpiX / 96F);
                using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset, EventName))
                {
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    // First run (no settings yet) or --setup: scan this PC, import the bundled pack, pick options.
                    var firstRun = !File.Exists(SettingsStore.FilePath) || args.Any(a => a.Equals("--setup", StringComparison.OrdinalIgnoreCase));
                    if (firstRun && !background)
                    {
                        Themes.Apply("dark");
                        using (var wizard = new SetupWizard()) Application.Run(wizard);
                    }
                    var settings = SettingsStore.Load();
                    Themes.Apply(settings.Theme); // before any control is created, so every control picks up the palette
                    Application.Run(new SearchForm(background, activation, settings));
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (!background) MessageBox.Show(ex.Message, "OpenVPN Hızlı Arama");
            return 1;
        }
    }
}
