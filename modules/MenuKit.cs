using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

// =====================================================================
// Shared vector icons + premium dropdown menus (rounded, icons, keycaps,
// section captions, status header card, danger items)
// =====================================================================

internal enum Glyph
{
    None, Backup, Restore, Import, Folder, Duplicates, History, Refresh, Settings, Keyboard, Exit,
    Connect, Disconnect, Star, Copy, Export, Delete, Dock, Open, Info, Palette, Lock, Server, Power, Panel,
    Plug, Layers, Search, ShieldLock
}

internal static class Glyphs
{
    static float S(float v) { return v * Ui.Scale; }

    // Glyphs that exist in the SVG asset library are drawn from it.
    static readonly Dictionary<Glyph, string> Map = new Dictionary<Glyph, string>
    {
        { Glyph.Backup, "backup" }, { Glyph.Restore, "restore" }, { Glyph.Import, "import" }, { Glyph.Folder, "folder" },
        { Glyph.Copy, "action-copy" }, { Glyph.Delete, "action-delete" }, { Glyph.Connect, "action-connect" }, { Glyph.Plug, "action-connect" },
        { Glyph.Disconnect, "action-disconnect" }, { Glyph.Power, "action-disconnect" }, { Glyph.Star, "action-pin" },
        { Glyph.Settings, "nav-general" }, { Glyph.Palette, "nav-appearance" }, { Glyph.Info, "nav-about" },
        { Glyph.Search, "search" }, { Glyph.ShieldLock, "app-logo" }
    };

    // Draws a 16-unit line icon centered in r.
    public static void Draw(Graphics g, Glyph glyph, RectangleF r, Color c)
    {
        if (glyph == Glyph.None) return;
        string an;
        if (Map.TryGetValue(glyph, out an) && Assets.Has(an)) { Assets.Draw(g, an, r, c); return; }
        var oldMode = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        var k = Math.Min(r.Width, r.Height) / 16F;
        float ox = r.X + (r.Width - 16 * k) / 2, oy = r.Y + (r.Height - 16 * k) / 2;
        Func<float, float, PointF> P = (x, y) => new PointF(ox + x * k, oy + y * k);
        Func<float, float, float, float, RectangleF> R = (x, y, w, h) => new RectangleF(ox + x * k, oy + y * k, w * k, h * k);
        using (var p = new Pen(c, Math.Max(1.2F, 1.45F * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        using (var b = new SolidBrush(c))
        {
            switch (glyph)
            {
                case Glyph.Backup: // archive box + down arrow
                    DrawRound(g, p, R(2, 3, 12, 3.5F), 1 * k);
                    g.DrawLines(p, new[] { P(3, 6.5F), P(3, 13), P(13, 13), P(13, 6.5F) });
                    g.DrawLine(p, P(8, 8), P(8, 11)); g.DrawLines(p, new[] { P(6.4F, 9.6F), P(8, 11.2F), P(9.6F, 9.6F) });
                    break;
                case Glyph.Restore: // counter-clockwise arrow
                    g.DrawArc(p, R(2.5F, 2.5F, 11, 11), 200, 290);
                    g.DrawLines(p, new[] { P(2.2F, 3.2F), P(2.6F, 6.6F), P(6, 6.2F) });
                    g.DrawLines(p, new[] { P(8, 5.5F), P(8, 8.2F), P(10, 9.6F) });
                    break;
                case Glyph.Import: // document + plus
                    g.DrawLines(p, new[] { P(9.5F, 1.8F), P(4, 1.8F), P(3, 2.8F), P(3, 13.2F), P(4, 14.2F), P(12, 14.2F), P(13, 13.2F), P(13, 5.3F), P(9.5F, 1.8F) });
                    g.DrawLines(p, new[] { P(9.5F, 1.8F), P(9.5F, 5.3F), P(13, 5.3F) });
                    g.DrawLine(p, P(8, 7.6F), P(8, 12)); g.DrawLine(p, P(5.8F, 9.8F), P(10.2F, 9.8F));
                    break;
                case Glyph.Folder:
                    g.DrawLines(p, new[] { P(1.8F, 4), P(1.8F, 12.5F), P(2.8F, 13.5F), P(13.2F, 13.5F), P(14.2F, 12.5F), P(14.2F, 6), P(13.2F, 5), P(8, 5), P(6.5F, 3), P(2.8F, 3), P(1.8F, 4) });
                    break;
                case Glyph.Duplicates:
                case Glyph.Copy:
                    DrawRound(g, p, R(5.5F, 5.5F, 8.5F, 8.5F), 1.6F * k);
                    g.DrawLines(p, new[] { P(10.5F, 3.5F), P(10.5F, 3), P(9.5F, 2), P(3, 2), P(2, 3), P(2, 9.5F), P(3, 10.5F), P(3.5F, 10.5F) });
                    break;
                case Glyph.History: // clock
                    g.DrawEllipse(p, R(2, 2, 12, 12));
                    g.DrawLines(p, new[] { P(8, 4.8F), P(8, 8), P(10.4F, 9.6F) });
                    break;
                case Glyph.Refresh:
                    g.DrawArc(p, R(2.5F, 2.5F, 11, 11), 300, 250);
                    g.DrawLines(p, new[] { P(13.6F, 2.6F), P(13.5F, 6), P(10.2F, 5.8F) });
                    break;
                case Glyph.Settings: // gear
                    g.DrawEllipse(p, R(5.6F, 5.6F, 4.8F, 4.8F));
                    for (var i = 0; i < 8; i++)
                    {
                        var a = i * Math.PI / 4;
                        g.DrawLine(p, P(8 + (float)Math.Cos(a) * 4.6F, 8 + (float)Math.Sin(a) * 4.6F), P(8 + (float)Math.Cos(a) * 6.6F, 8 + (float)Math.Sin(a) * 6.6F));
                    }
                    g.DrawEllipse(p, R(3.4F, 3.4F, 9.2F, 9.2F));
                    break;
                case Glyph.Keyboard:
                    DrawRound(g, p, R(1.5F, 4, 13, 8.5F), 1.5F * k);
                    foreach (var x in new[] { 4.2F, 6.7F, 9.3F, 11.8F }) g.FillEllipse(b, R(x - 0.7F, 6.3F, 1.4F, 1.4F));
                    g.DrawLine(p, P(5.2F, 10), P(10.8F, 10));
                    break;
                case Glyph.Exit:
                case Glyph.Power:
                    g.DrawArc(p, R(2.5F, 3, 11, 11), 300, 300);
                    g.DrawLine(p, P(8, 1.8F), P(8, 7.6F));
                    break;
                case Glyph.Connect: // play in circle
                    g.DrawEllipse(p, R(2, 2, 12, 12));
                    using (var path = new GraphicsPath()) { path.AddPolygon(new[] { P(6.6F, 5.3F), P(11, 8), P(6.6F, 10.7F) }); g.FillPath(b, path); }
                    break;
                case Glyph.Disconnect: // circle with slash
                    g.DrawEllipse(p, R(2, 2, 12, 12));
                    g.DrawLine(p, P(4.2F, 11.8F), P(11.8F, 4.2F));
                    break;
                case Glyph.Star:
                    g.DrawPolygon(p, Ui.Star(ox + 8 * k, oy + 8.4F * k, 6.6F * k));
                    break;
                case Glyph.Export: // tray + up arrow
                    g.DrawLines(p, new[] { P(2.5F, 9.5F), P(2.5F, 13), P(13.5F, 13), P(13.5F, 9.5F) });
                    g.DrawLine(p, P(8, 2.5F), P(8, 10)); g.DrawLines(p, new[] { P(5.2F, 5.2F), P(8, 2.4F), P(10.8F, 5.2F) });
                    break;
                case Glyph.Delete: // trash
                    g.DrawLine(p, P(2.5F, 4.2F), P(13.5F, 4.2F));
                    g.DrawLines(p, new[] { P(6, 4), P(6, 2.4F), P(10, 2.4F), P(10, 4) });
                    g.DrawLines(p, new[] { P(4, 4.5F), P(4.8F, 13.2F), P(11.2F, 13.2F), P(12, 4.5F) });
                    g.DrawLine(p, P(6.8F, 6.8F), P(6.8F, 10.8F)); g.DrawLine(p, P(9.2F, 6.8F), P(9.2F, 10.8F));
                    break;
                case Glyph.Dock:
                case Glyph.Panel: // window split in two
                    DrawRound(g, p, R(1.8F, 2.8F, 12.4F, 10.4F), 1.6F * k);
                    g.DrawLine(p, P(9.8F, 3), P(9.8F, 13));
                    break;
                case Glyph.Open: // window with title bar
                    DrawRound(g, p, R(1.8F, 2.8F, 12.4F, 10.4F), 1.6F * k);
                    g.DrawLine(p, P(2, 5.8F), P(14, 5.8F));
                    break;
                case Glyph.Info:
                    g.DrawEllipse(p, R(2, 2, 12, 12));
                    g.DrawLine(p, P(8, 7.4F), P(8, 11)); g.FillEllipse(b, R(7.25F, 4.4F, 1.5F, 1.5F));
                    break;
                case Glyph.Palette:
                    g.DrawArc(p, R(2, 2, 12, 12), 0, 360);
                    g.FillEllipse(b, R(4.6F, 6.2F, 1.8F, 1.8F)); g.FillEllipse(b, R(7.1F, 4.2F, 1.8F, 1.8F)); g.FillEllipse(b, R(9.7F, 6.2F, 1.8F, 1.8F));
                    g.FillEllipse(b, R(8.3F, 9.6F, 2.4F, 2.4F));
                    break;
                case Glyph.Lock:
                    DrawRound(g, p, R(3, 7, 10, 7), 1.4F * k);
                    g.DrawArc(p, R(5, 2.5F, 6, 8), 180, 180); g.DrawLine(p, P(5, 6.5F), P(5, 7)); g.DrawLine(p, P(11, 6.5F), P(11, 7));
                    g.DrawLine(p, P(8, 9.6F), P(8, 11.4F));
                    break;
                case Glyph.Plug: // plug tilted 45°, prongs to the top-right, cord to the bottom-left
                {
                    var st = g.Save();
                    g.TranslateTransform(ox + 8.6F * k, oy + 7.4F * k); g.RotateTransform(45);
                    using (var path = Ui.Round(new RectangleF(-3.4F * k, -2.6F * k, 6.8F * k, 5.6F * k), 1.6F * k)) g.DrawPath(p, path);
                    g.DrawLine(p, -1.6F * k, -2.6F * k, -1.6F * k, -5.6F * k);
                    g.DrawLine(p, 1.6F * k, -2.6F * k, 1.6F * k, -5.6F * k);
                    g.DrawLine(p, 0, 3F * k, 0, 5.2F * k);
                    g.Restore(st);
                    g.DrawLines(p, new[] { P(5.5F, 10.5F), P(3.2F, 12.8F), P(1.8F, 14.2F) });
                    break;
                }
                case Glyph.Layers:
                    g.DrawPolygon(p, new[] { P(8, 2), P(14.5F, 5.4F), P(8, 8.8F), P(1.5F, 5.4F) });
                    g.DrawLines(p, new[] { P(1.5F, 8.5F), P(8, 11.9F), P(14.5F, 8.5F) });
                    g.DrawLines(p, new[] { P(1.5F, 11.6F), P(8, 15), P(14.5F, 11.6F) });
                    break;
                case Glyph.Search:
                    g.DrawEllipse(p, R(2, 2, 9.5F, 9.5F));
                    g.DrawLine(p, P(10.2F, 10.2F), P(14.2F, 14.2F));
                    break;
                case Glyph.ShieldLock:
                    using (var path = new GraphicsPath())
                    {
                        path.AddLine(P(8, 1.2F), P(14, 3.4F)); path.AddLine(P(14, 3.4F), P(14, 7.6F));
                        path.AddBezier(P(14, 7.6F), P(14, 11.4F), P(11.2F, 13.8F), P(8, 15.1F));
                        path.AddBezier(P(8, 15.1F), P(4.8F, 13.8F), P(2, 11.4F), P(2, 7.6F));
                        path.AddLine(P(2, 7.6F), P(2, 3.4F)); path.CloseFigure();
                        g.DrawPath(p, path);
                    }
                    DrawRound(g, p, R(5.4F, 7.4F, 5.2F, 4.2F), 0.8F * k);
                    g.DrawArc(p, R(6.2F, 4.6F, 3.6F, 4.6F), 180, 180);
                    break;
                case Glyph.Server:
                    DrawRound(g, p, R(2, 2.5F, 12, 4.5F), 1.2F * k); DrawRound(g, p, R(2, 9, 12, 4.5F), 1.2F * k);
                    g.FillEllipse(b, R(4.1F, 4.1F, 1.4F, 1.4F)); g.FillEllipse(b, R(4.1F, 10.6F, 1.4F, 1.4F));
                    break;
            }
        }
        g.SmoothingMode = oldMode;
    }

    static void DrawRound(Graphics g, Pen p, RectangleF r, float radius) { using (var path = Ui.Round(r, radius)) g.DrawPath(p, path); }

    public static bool IsWindows11
    {
        get
        {
            try { var b = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "0") as string; int n; return int.TryParse(b, out n) && n >= 22000; }
            catch { return false; }
        }
    }
}

// Menu item with an icon, optional danger color, optional right-side badge.
internal sealed class MenuEntry : ToolStripMenuItem
{
    public Glyph Glyph { get; set; }
    public bool Danger { get; set; }
    public string Badge { get; set; }
    static Bitmap placeholder;

    public MenuEntry(string text, Glyph glyph, EventHandler click, string keys = null) : base(text, null, click)
    {
        Glyph = glyph;
        ShortcutKeyDisplayString = keys;
        // A blank image reserves the icon column; the renderer draws the vector glyph there.
        if (placeholder == null) placeholder = new Bitmap(1, 1);
        Image = placeholder;
        Padding = new Padding(Ui.S(2), Ui.S(8), Ui.S(2), Ui.S(8));
    }

    // Wide enough for text + badge + keycaps, never narrower than the menu's design width.
    public override Size GetPreferredSize(Size constrainingSize)
    {
        var s = base.GetPreferredSize(constrainingSize);
        var extra = 0;
        if (!string.IsNullOrEmpty(Badge)) extra += GText.Measure(Badge, Ui.KeyFont).Width + Ui.S(28);
        if (!string.IsNullOrEmpty(ShortcutKeyDisplayString)) foreach (var part in ShortcutKeyDisplayString.Split('+')) extra += GText.Measure(part, Ui.KeyFont).Width + Ui.S(16);
        return new Size(Math.Max(Ui.S(290), s.Width + extra + Ui.S(40)), Math.Max(s.Height, Ui.S(38)));
    }
}

// Small uppercase section caption (a disabled menu item, so it spans the full menu width).
internal sealed class MenuCaption : ToolStripMenuItem
{
    public MenuCaption(string text) : base(text) { Font = Ui.HeaderFont; Enabled = false; Padding = new Padding(0, Ui.S(4), 0, 0); }
    public override Size GetPreferredSize(Size constrainingSize) { var s = base.GetPreferredSize(constrainingSize); return new Size(s.Width, Ui.S(30)); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var p = MenuMotion.Progress(Owner, Owner != null ? Owner.Items.IndexOf(this) : 0);
        GText.Draw(e.Graphics, Text, Ui.SectionFont, new Point(Ui.S(18), Height - Ui.S(20) + (int)(Ui.S(6) * (1 - p))), GlassPaint.A(Ui.Faint, p));
    }
}

// Status tile at the top of a menu: colored dot (or provider chip), title, subtitle.
internal sealed class MenuStatusItem : ToolStripMenuItem
{
    public string Title, Subtitle; public Color Dot; public string Initials; public Color AvatarColor;
    public MenuStatusItem(string title, string subtitle, Color dot) { Title = title; Subtitle = subtitle; Dot = dot; Text = title; }

    public override Size GetPreferredSize(Size constrainingSize)
    {
        var w = Math.Max(GText.Measure(Title ?? "", Ui.NameFont).Width, GText.Measure(Subtitle ?? "", Ui.SmallFont).Width) + Ui.S(Initials != null ? 84 : 62);
        return new Size(Math.Min(Ui.S(380), Math.Max(Ui.S(290), w)), Ui.S(68));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var idx = Owner != null ? Owner.Items.IndexOf(this) : 0; var p = MenuMotion.Progress(Owner, idx);
        g.TranslateTransform(0, (1 - p) * Ui.S(8));
        var full = Owner != null ? Owner.Width - Bounds.Left - Ui.S(2) : Width;
        var r = new RectangleF(Ui.S(8), Ui.S(4), full - Ui.S(16), Height - Ui.S(10));
        Glass.Paint(g, r, Ui.S(14), GlassKind.Secondary, 1.6F * p);
        var x = r.X + Ui.S(14);
        var chip = new RectangleF(x, r.Y + (r.Height - Ui.S(34)) / 2F, Ui.S(34), Ui.S(34));
        Glass.Paint(g, chip, Ui.S(11), GlassKind.Secondary, 1.8F * p);
        if (Initials != null) GText.Draw(g, Initials, Ui.InitialsFont, Rectangle.Round(chip), GlassPaint.A(Ui.Text, p), true);
        else
        {
            var ds = Ui.S(10); var dr = new RectangleF(chip.X + (chip.Width - ds) / 2, chip.Y + (chip.Height - ds) / 2, ds, ds);
            using (var halo = new SolidBrush(Color.FromArgb((int)(60 * p), Dot))) g.FillEllipse(halo, RectangleF.Inflate(dr, Ui.S(4), Ui.S(4)));
            using (var b = new SolidBrush(GlassPaint.A(Dot, p))) g.FillEllipse(b, dr);
        }
        x = chip.Right + Ui.S(12);
        var w = (int)(r.Right - x - Ui.S(12));
        GText.Draw(g, Title ?? "", Ui.NameFont, new Rectangle((int)x, (int)r.Y + Ui.S(10), w, Ui.S(20)), GlassPaint.A(Ui.Text, p));
        GText.Draw(g, Subtitle ?? "", Ui.SmallFont, new Rectangle((int)x, (int)r.Y + Ui.S(31), w, Ui.S(18)), GlassPaint.A(Ui.Muted, p));
    }
}

// Opening animation for the floating menus: items fade and rise in with a short stagger.
internal static class MenuMotion
{
    static readonly Dictionary<ToolStrip, long> started = new Dictionary<ToolStrip, long>();
    static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    static readonly System.Windows.Forms.Timer timer = MakeTimer();

    static System.Windows.Forms.Timer MakeTimer()
    {
        var t = new System.Windows.Forms.Timer { Interval = 15 };
        t.Tick += delegate
        {
            var done = new List<ToolStrip>();
            foreach (var kv in started)
            {
                if (kv.Key.IsDisposed || !kv.Key.Visible || clock.ElapsedMilliseconds - kv.Value > 700) { done.Add(kv.Key); continue; }
                kv.Key.Invalidate();
            }
            foreach (var d in done) { started.Remove(d); if (!d.IsDisposed) d.Invalidate(); }
            if (started.Count == 0) t.Stop();
        };
        return t;
    }

    public static void Begin(ToolStrip m)
    {
        if (Motion.Reduced) return;
        started[m] = clock.ElapsedMilliseconds; timer.Start();
    }

    public static float Progress(ToolStrip m, int index)
    {
        long t0;
        if (m == null || Motion.Reduced || !started.TryGetValue(m, out t0)) return 1F;
        var t = (clock.ElapsedMilliseconds - t0 - Math.Min(index, 12) * 16) / 220F;
        return Ease.OutCubic(t);
    }
}

internal sealed class MenuRenderer : ToolStripRenderer
{
    static int S(float v) { return Ui.S(v); }
    readonly bool win11 = Glyphs.IsWindows11;

    // Floating glass panel: dense tint over the compositor blur (opaque when there is no blur).
    static Color PanelTint { get { return Backdrop.Active ? Ui.MenuTint : Ui.MenuSolid; } }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.CompositingMode = CompositingMode.SourceCopy; g.Clear(PanelTint); g.CompositingMode = CompositingMode.SourceOver;
        // a faint top light so the panel reads as glass, not as a flat sheet
        using (var b = new LinearGradientBrush(new Rectangle(0, -1, e.ToolStrip.Width, S(60)), Color.FromArgb(Themes.IsLight ? 120 : 20, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90F))
            g.FillRectangle(b, 0, 0, e.ToolStrip.Width, S(60));
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ui.Round(new RectangleF(0.5F, 0.5F, e.ToolStrip.Width - 1.5F, e.ToolStrip.Height - 1.5F), win11 ? S(8) : 1))
        using (var lb = new LinearGradientBrush(new Rectangle(0, -1, e.ToolStrip.Width, e.ToolStrip.Height + 2), Color.FromArgb(Themes.IsLight ? 230 : 90, 255, 255, 255), Color.FromArgb(Themes.IsLight ? 60 : 24, Themes.IsLight ? 0 : 255, Themes.IsLight ? 0 : 255, Themes.IsLight ? 0 : 255), 90F))
        using (var pen = new Pen(lb, 1F)) g.DrawPath(pen, path);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    // Items can be narrower than the dropdown; use the dropdown's width for right-aligned parts.
    static int FullWidth(ToolStripItem item) { return item.Owner != null ? item.Owner.Width - item.Bounds.Left - S(2) : item.Width; }

    static float P(ToolStripItem item) { return item.Owner == null ? 1F : MenuMotion.Progress(item.Owner, item.Owner.Items.IndexOf(item)); }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled || e.Item is MenuStatusItem || e.Item is MenuCaption) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var entry = e.Item as MenuEntry;
        var r = new RectangleF(S(6), 1, FullWidth(e.Item) - S(12), e.Item.Height - 2);
        if (entry != null && entry.Danger)
            using (var path = Ui.Round(r, S(11))) using (var b = new SolidBrush(Color.FromArgb(Themes.IsLight ? 36 : 70, Ui.Danger))) g.FillPath(b, path);
        else Glass.Paint(g, r, S(11), GlassKind.Selected, 1F, 0.3F);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        var entry = e.Item as MenuEntry;
        if (entry == null) return;
        var g = e.Graphics; var p = P(e.Item); g.SmoothingMode = SmoothingMode.AntiAlias;
        var c = !entry.Enabled ? Ui.Faint : entry.Danger ? Ui.Danger : entry.Selected ? Ui.Text : Ui.Muted;
        var chipD = S(26); var cy = e.Item.ContentRectangle.Y + e.Item.ContentRectangle.Height / 2F;
        var chip = new RectangleF(S(14), cy - chipD / 2F + (1 - p) * S(8), chipD, chipD);
        Glass.Paint(g, chip, S(8), GlassKind.Secondary, (entry.Selected ? 1.8F : 1.3F) * p);
        Glyphs.Draw(g, entry.Glyph, RectangleF.Inflate(chip, -S(5.5F), -S(5.5F)), GlassPaint.A(c, p));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var g = e.Graphics;
        if (e.Item is MenuStatusItem || e.Item is MenuCaption) return;
        var p = P(e.Item); var dy = (int)((1 - p) * S(8));
        var entry = e.Item as MenuEntry;
        var mi = e.Item as ToolStripMenuItem;
        var isShortcut = mi != null && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString) && e.Text == mi.ShortcutKeyDisplayString;
        var h = e.Item.ContentRectangle.Height; var top = e.Item.ContentRectangle.Y + dy;
        if (isShortcut)
        {
            // shortcut as small glass keycaps, right aligned
            var parts = e.Text.Split('+');
            var widths = new List<int>(); var total = 0;
            foreach (var part in parts) { var w = GText.Measure(part, Ui.KeyFont).Width + S(12); widths.Add(w); total += w + S(4); }
            var x = FullWidth(e.Item) - S(16) - total + S(4);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (var i = 0; i < parts.Length; i++)
            {
                var kr = new RectangleF(x, top + (h - S(20)) / 2F, widths[i], S(20));
                Glass.Paint(g, kr, S(6), GlassKind.Secondary, 1.6F * p);
                GText.Draw(g, parts[i], Ui.KeyFont, Rectangle.Round(kr), GlassPaint.A(Ui.Muted, p), true);
                x += widths[i] + S(4);
            }
            return;
        }
        var color = !e.Item.Enabled ? Ui.Faint : entry != null && entry.Danger ? Ui.Danger : Ui.Text;
        var tr = new Rectangle(S(52), top, FullWidth(e.Item) - S(52) - S(14), h);
        GText.Draw(g, e.Text, Ui.MenuFont, tr, GlassPaint.A(color, p));
        if (entry != null && !string.IsNullOrEmpty(entry.Badge))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bw = GText.Measure(entry.Badge, Ui.KeyFont).Width + S(16);
            var br = new RectangleF(FullWidth(e.Item) - S(16) - bw, top + (h - S(20)) / 2F, bw, S(20));
            Glass.Paint(g, br, S(10), GlassKind.Secondary, 1.8F * p);
            GText.Draw(g, entry.Badge, Ui.KeyFont, Rectangle.Round(br), GlassPaint.A(Ui.Text, p), true);
        }
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using (var p = new Pen(Color.FromArgb(Themes.IsLight ? 36 : 30, Themes.IsLight ? Color.Black : Color.White))) e.Graphics.DrawLine(p, S(18), y, e.Item.Width - S(18), y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Ui.Muted; base.OnRenderArrow(e); }
}

internal static class MenuKit
{
    public static ContextMenuStrip Style(ContextMenuStrip m)
    {
        m.Renderer = new MenuRenderer();
        m.ShowImageMargin = true; m.ShowCheckMargin = false;
        m.ImageScalingSize = new Size(Ui.S(18), Ui.S(18));
        m.Font = Ui.MenuFont;
        m.Padding = new Padding(0, Ui.S(6), 0, Ui.S(6));
        m.DropShadowEnabled = !Backdrop.Supported;   // Windows 11 draws its own shadow around the backdrop panel
        m.HandleCreated += delegate { ApplyFrame(m); };
        m.Opening += delegate { ApplyFrame(m); MenuMotion.Begin(m); };
        return m;
    }

    // Gives every item the same explicit width (the widest preferred width, min 300 px) so
    // full-width parts (status tile, hover plate, badges, keycaps) are never clipped.
    public static void Fit(ToolStrip m)
    {
        var w = Ui.S(300);
        foreach (ToolStripItem it in m.Items) if (!(it is ToolStripSeparator)) w = Math.Max(w, it.GetPreferredSize(Size.Empty).Width);
        w = Math.Min(w, Ui.S(420));
        // WinForms needs both: MinimumSize widens the dropdown, explicit item sizes widen the items.
        m.MinimumSize = new Size(w + 2, 0);
        foreach (ToolStripItem it in m.Items)
        {
            var h = it.GetPreferredSize(Size.Empty).Height;
            it.AutoSize = false; it.Size = new Size(w, h);
        }
    }

    static void ApplyFrame(ToolStripDropDown m)
    {
        if (!m.IsHandleCreated) return;
        try
        {
            Backdrop.Apply(m.Handle, !Themes.IsLight);     // same compositor acrylic as the main window
            var corner = 2; NativeMethods.DwmSetWindowAttribute(m.Handle, 33, ref corner, 4);
            var border = Ui.MenuFrame.R | (Ui.MenuFrame.G << 8) | (Ui.MenuFrame.B << 16); NativeMethods.DwmSetWindowAttribute(m.Handle, 34, ref border, 4);   // COLORREF
        }
        catch { }
    }
}
