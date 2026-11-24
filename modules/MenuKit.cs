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

    // Draws a 16-unit line icon centered in r.
    public static void Draw(Graphics g, Glyph glyph, RectangleF r, Color c)
    {
        if (glyph == Glyph.None) return;
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
        Padding = new Padding(Ui.S(2), Ui.S(6), Ui.S(2), Ui.S(6));
    }

    // Wide enough for text + badge + keycaps, never narrower than the menu's design width.
    public override Size GetPreferredSize(Size constrainingSize)
    {
        var s = base.GetPreferredSize(constrainingSize);
        var extra = 0;
        using (var g = Graphics.FromHwnd(IntPtr.Zero))
        {
            if (!string.IsNullOrEmpty(Badge)) extra += Ui.Measure(g, Badge, Ui.KeyFont).Width + Ui.S(28);
            if (!string.IsNullOrEmpty(ShortcutKeyDisplayString)) foreach (var part in ShortcutKeyDisplayString.Split('+')) extra += Ui.Measure(g, part, Ui.KeyFont).Width + Ui.S(14);
        }
        return new Size(Math.Max(Ui.S(280), s.Width + extra + Ui.S(16)), s.Height);
    }
}

// Small uppercase section caption (a disabled menu item, so it spans the full menu width).
internal sealed class MenuCaption : ToolStripMenuItem
{
    public MenuCaption(string text) : base(text) { Font = Ui.HeaderFont; Enabled = false; Padding = new Padding(0, Ui.S(4), 0, 0); }
    public override Size GetPreferredSize(Size constrainingSize) { var s = base.GetPreferredSize(constrainingSize); return new Size(s.Width, Ui.S(26)); }
    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, Text, Ui.HeaderFont, new Point(Ui.S(14), Height - Ui.S(17)), Ui.Faint, Ui.Flags);
    }
}

// Status card shown at the top of a menu: colored dot (or avatar), title, subtitle.
internal sealed class MenuStatusItem : ToolStripMenuItem
{
    public string Title, Subtitle; public Color Dot; public string Initials; public Color AvatarColor;
    public MenuStatusItem(string title, string subtitle, Color dot) { Title = title; Subtitle = subtitle; Dot = dot; Text = title; }

    public override Size GetPreferredSize(Size constrainingSize)
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero))
        {
            var w = Math.Max(Ui.Measure(g, Title ?? "", Ui.NameFont).Width, Ui.Measure(g, Subtitle ?? "", Ui.SmallFont).Width) + Ui.S(Initials != null ? 76 : 54);
            return new Size(Math.Min(Ui.S(380), Math.Max(Ui.S(270), w)), Ui.S(64));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var full = Owner != null ? Owner.Width - Bounds.Left - Ui.S(2) : Width;
        var r = new Rectangle(Ui.S(6), Ui.S(3), full - Ui.S(12), Height - Ui.S(8));
        Ui.FillRound(g, Ui.Bg, r, Ui.S(9));
        Ui.DrawRound(g, Ui.Border, r, Ui.S(9));
        var x = r.X + Ui.S(14);
        if (Initials != null)
        {
            var d = Ui.S(32); var av = new Rectangle(x, r.Y + (r.Height - d) / 2, d, d);
            Ui.FillRound(g, Ui.Blend(Ui.Bg, AvatarColor, 0.22F), av, Ui.S(9));
            var isz = Ui.Measure(g, Initials, Ui.InitialsFont);
            TextRenderer.DrawText(g, Initials, Ui.InitialsFont, new Point(av.X + (d - isz.Width) / 2, av.Y + (d - isz.Height) / 2), Themes.IsLight ? Ui.Blend(AvatarColor, Color.Black, 0.45F) : Ui.Blend(AvatarColor, Color.White, 0.25F), Ui.Flags);
            x = av.Right + Ui.S(12);
        }
        else
        {
            var ds = Ui.S(10); var dr = new Rectangle(x, r.Y + Ui.S(15), ds, ds);
            using (var halo = new SolidBrush(Color.FromArgb(50, Dot))) g.FillEllipse(halo, Rectangle.Inflate(dr, Ui.S(3), Ui.S(3)));
            using (var b = new SolidBrush(Dot)) g.FillEllipse(b, dr);
            x = dr.Right + Ui.S(12);
        }
        var w = r.Right - x - Ui.S(10);
        TextRenderer.DrawText(g, Title ?? "", Ui.NameFont, new Rectangle(x, r.Y + Ui.S(9), w, Ui.S(20)), Ui.Text, Ui.Flags | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, Subtitle ?? "", Ui.SmallFont, new Rectangle(x, r.Y + Ui.S(30), w, Ui.S(18)), Ui.Muted, Ui.Flags | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class MenuRenderer : ToolStripRenderer
{
    static int S(float v) { return Ui.S(v); }
    static readonly Color DangerColor = Color.FromArgb(232, 84, 100);
    readonly bool win11 = Glyphs.IsWindows11;

    protected override void Initialize(ToolStrip toolStrip) { base.Initialize(toolStrip); }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Ui.Surface); }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // Windows 11 draws a rounded DWM border (see MenuKit.Style); older Windows gets a flat 1px border.
        if (win11) return;
        using (var p = new Pen(Ui.BorderStrong)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    // Items can be narrower than the dropdown; use the dropdown's width for right-aligned parts.
    static int FullWidth(ToolStripItem item) { return item.Owner != null ? item.Owner.Width - item.Bounds.Left - S(2) : item.Width; }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled || e.Item is MenuStatusItem || e.Item is MenuCaption) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var entry = e.Item as MenuEntry;
        var r = new Rectangle(S(5), 1, FullWidth(e.Item) - S(10), e.Item.Height - 2);
        var fill = entry != null && entry.Danger ? Color.FromArgb(Themes.IsLight ? 30 : 46, DangerColor) : Ui.SurfaceHover;
        Ui.FillRound(g, fill, r, S(7));
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        var entry = e.Item as MenuEntry;
        if (entry == null) return;
        var c = !entry.Enabled ? Ui.Faint : entry.Danger ? DangerColor : entry.Selected ? Ui.Text : Ui.Muted;
        var size = S(16);
        var ir = e.ImageRectangle;
        var r = new RectangleF(ir.X + (ir.Width - size) / 2F + S(4), e.Item.ContentRectangle.Y + (e.Item.ContentRectangle.Height - size) / 2F, size, size);
        Glyphs.Draw(e.Graphics, entry.Glyph, r, c);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var g = e.Graphics;
        if (e.Item is MenuStatusItem) return;
        if (e.Item is MenuCaption)
        {
            TextRenderer.DrawText(g, e.Text, Ui.HeaderFont, new Point(S(16), e.TextRectangle.Y + S(2)), Ui.Faint, Ui.Flags);
            return;
        }
        var entry = e.Item as MenuEntry;
        var mi = e.Item as ToolStripMenuItem;
        var isShortcut = mi != null && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString) && e.Text == mi.ShortcutKeyDisplayString;
        if (isShortcut)
        {
            // Shortcut as keycaps, right aligned.
            var parts = e.Text.Split('+');
            var widths = new List<int>(); var total = 0;
            foreach (var part in parts) { var w = Ui.Measure(g, part, Ui.KeyFont).Width + S(10); widths.Add(w); total += w + S(3); }
            var x = FullWidth(e.Item) - S(14) - total + S(3);
            var y = e.Item.ContentRectangle.Y + (e.Item.ContentRectangle.Height - S(18)) / 2;
            for (var i = 0; i < parts.Length; i++) { Ui.DrawKeycap(g, parts[i], x, y); x += widths[i] + S(3); }
            return;
        }
        var color = !e.Item.Enabled ? Ui.Faint : entry != null && entry.Danger ? DangerColor : Ui.Text;
        var tr = e.TextRectangle; tr.X += S(6);
        TextRenderer.DrawText(g, e.Text, Ui.MenuFont, tr, color, Ui.Flags | TextFormatFlags.VerticalCenter);
        if (entry != null && !string.IsNullOrEmpty(entry.Badge))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bw = Ui.Measure(g, entry.Badge, Ui.KeyFont).Width + S(12);
            var br = new Rectangle(FullWidth(e.Item) - S(14) - bw, e.Item.ContentRectangle.Y + (e.Item.ContentRectangle.Height - S(18)) / 2, bw, S(18));
            Ui.FillRound(g, Color.FromArgb(Themes.IsLight ? 30 : 60, Ui.Accent), br, S(9));
            var bs = Ui.Measure(g, entry.Badge, Ui.KeyFont);
            TextRenderer.DrawText(g, entry.Badge, Ui.KeyFont, new Point(br.X + (bw - bs.Width) / 2, br.Y + (br.Height - bs.Height) / 2), Ui.Accent, Ui.Flags);
        }
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using (var p = new Pen(Ui.Border)) e.Graphics.DrawLine(p, S(12), y, e.Item.Width - S(12), y);
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
        m.Padding = new Padding(0, Ui.S(5), 0, Ui.S(5));
        m.DropShadowEnabled = true;
        m.HandleCreated += delegate { ApplyFrame(m); };
        m.Opening += delegate { ApplyFrame(m); };
        return m;
    }

    // Gives every item the same explicit width (the widest preferred width, min 290 px) so
    // full-width parts (status card, hover pill, badges, keycaps) are never clipped.
    public static void Fit(ToolStrip m)
    {
        var w = Ui.S(290);
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
            var corner = 2; NativeMethods.DwmSetWindowAttribute(m.Handle, 33, ref corner, 4);
            var border = Ui.BorderStrong.R | (Ui.BorderStrong.G << 8) | (Ui.BorderStrong.B << 16);
            NativeMethods.DwmSetWindowAttribute(m.Handle, 34, ref border, 4);
        }
        catch { }
    }
}
