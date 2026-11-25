using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// =====================================================================
// Settings window: borderless, left navigation, card-based pages,
// custom toggles / segmented controls / stepper / hotkey capture,
// live theme preview tiles.
// =====================================================================

internal static class SFonts
{
    public static readonly Font PageTitle = new Font(Ui.FamilySemi, 15F);
    public static readonly Font RowTitle = new Font(Ui.FamilySemi, 9.75F);
    public static readonly Font RowDesc = new Font(Ui.Family, 8.75F);
    public static readonly Font Nav = new Font(Ui.Family, 9.75F);
    public static readonly Font NavBold = new Font(Ui.FamilySemi, 9.75F);
    public static readonly Font Button = new Font(Ui.FamilySemi, 9F);
    public static readonly Font Big = new Font(Ui.FamilySemi, 11.5F);
}

// Base for owner-drawn controls: double-buffered, transparent-feeling (paints parent's bg color).
internal abstract class SControl : Control
{
    protected bool hover, pressed;
    protected SControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }
    protected static int S(float v) { return Ui.S(v); }
    // Paints what is behind this control: the glass card it sits in (so specular and border line up), else the page color.
    protected void PaintBase(Graphics g)
    {
        int ox = Left, oy = Top; Control c = Parent;
        while (c != null && !(c is SCard) && !(c is SBackupHero)) { ox += c.Left; oy += c.Top; c = c.Parent; }
        var card = c as SCard; var heroBase = c as SBackupHero;
        if (card != null) { g.TranslateTransform(-ox, -oy); card.DrawBase(g); g.ResetTransform(); }
        else if (heroBase != null) { g.TranslateTransform(-ox, -oy); heroBase.DrawBase(g); g.ResetTransform(); }
        else g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
    }
    protected Color ParentBg { get { return Parent is SCard || Parent is SRow || Parent is SBackupHero ? Ui.Surface : Parent != null ? Parent.BackColor : Ui.Bg; } }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected void FocusRing(Graphics g, Rectangle r, float radius)
    {
        if (!Focused || !ShowFocusCues) return;
        Ui.DrawRound(g, Color.FromArgb(150, Ui.Accent), Rectangle.Inflate(r, S(2), S(2)), radius + S(2), S(1.5F));
    }
}

internal sealed class SToggle : SControl
{
    bool on; readonly Spring pos = new Spring(0, 520, 32); readonly Animator anim = new Animator();
    public event EventHandler CheckedChanged;
    public SToggle()
    {
        Size = new Size(S(46), S(26)); Cursor = Cursors.Hand; TabStop = true;
        anim.Tick = dt => { var m = pos.Step(dt); Invalidate(); return m; };
    }
    protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }
    public bool Checked
    {
        get { return on; }
        set { if (on == value) return; on = value; pos.Target = on ? 1 : 0; anim.Kick(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); }
    }
    public void SetInstant(bool value) { on = value; pos.Snap(value ? 1 : 0); Invalidate(); }
    protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; PaintBase(g);
        var r = new RectangleF(S(1), S(1), Width - S(3), Height - S(3));
        var p = Ease.Clamp01(pos.Value);
        Glass.Paint(g, r, r.Height / 2F, GlassKind.Secondary, 1.8F);
        if (p > 0.01F)
        {
            using (var path = Ui.Round(r, r.Height / 2F))
            using (var b = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), Color.FromArgb((int)(255 * p), Ui.Blend(Ui.Accent, Color.White, 0.16F)), Color.FromArgb((int)(255 * p), Ui.Accent), 90F)) g.FillPath(b, path);
        }
        // thumb stretches slightly while pressed, like a glass bead
        var d = r.Height - S(6); var stretch = pressed ? S(4) : 0;
        var x = r.X + S(3) + (r.Width - d - S(6) - stretch) * pos.Value;
        using (var sh = new SolidBrush(Color.FromArgb(46, 0, 0, 0))) g.FillEllipse(sh, x, r.Y + S(4), d + stretch, d);
        using (var path = Ui.Round(new RectangleF(x, r.Y + S(3), d + stretch, d), d / 2F)) using (var b = new LinearGradientBrush(new RectangleF(x, r.Y + S(2), d, d + 2), Color.White, Color.FromArgb(226, 230, 240), 90F)) g.FillPath(b, path);
        FocusRing(g, Rectangle.Round(r), r.Height / 2F);
    }
}

internal sealed class SSegmented : SControl
{
    string[] items = new string[0]; int sel, hot = -1;
    readonly Spring thumbX = new Spring(0, 520, 34), thumbW = new Spring(0, 520, 34); readonly Animator anim = new Animator();
    public event EventHandler Changed;
    public SSegmented(params string[] labels)
    {
        items = labels; Height = S(34); Cursor = Cursors.Hand; TabStop = true; AutoWidth();
        anim.Tick = dt => { var a = thumbX.Step(dt); var b = thumbW.Step(dt); Invalidate(); return a || b; };
        Snap();
    }
    protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }
    public int SelectedIndex { get { return sel; } set { value = Math.Max(0, Math.Min(items.Length - 1, value)); if (sel == value) return; sel = value; MoveThumb(); if (Changed != null) Changed(this, EventArgs.Empty); } }
    void AutoWidth()
    {
        using (var g = CreateGraphics()) { var w = items.Max(t => Ui.Measure(g, t, SFonts.Button).Width) + S(28); Width = w * items.Length + S(8); }
    }
    int SegWidth { get { return (Width - S(8)) / Math.Max(1, items.Length); } }
    void Snap() { thumbX.Snap(S(4) + sel * SegWidth); thumbW.Snap(SegWidth); }
    void MoveThumb() { thumbX.Target = S(4) + sel * SegWidth; thumbW.Target = SegWidth; anim.Kick(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Snap(); }
    protected override void OnMouseMove(MouseEventArgs e) { var i = Math.Min(items.Length - 1, Math.Max(0, (e.X - S(4)) / SegWidth)); if (i != hot) { hot = i; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = -1; base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); SelectedIndex = Math.Min(items.Length - 1, Math.Max(0, (e.X - S(4)) / SegWidth)); base.OnMouseDown(e); }
    protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Left) SelectedIndex--; else if (e.KeyCode == Keys.Right) SelectedIndex++; base.OnKeyDown(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; PaintBase(g);
        var r = new RectangleF(0, 0, Width, Height);
        Glass.Paint(g, r, Height / 2F, GlassKind.Secondary, 1.8F);
        var th = Height - S(8);
        var thumb = new RectangleF(thumbX.Value, S(4), thumbW.Value, th);
        Glass.Paint(g, thumb, th / 2F, GlassKind.Selected, 1F, 0.3F);
        for (var i = 0; i < items.Length; i++)
        {
            var sr = new Rectangle(S(4) + i * SegWidth, S(4), SegWidth, th);
            var sz = Ui.Measure(g, items[i], SFonts.Button);
            var cover = 1F - Math.Min(1F, Math.Abs((thumb.X + thumb.Width / 2F) - (sr.X + sr.Width / 2F)) / Math.Max(1F, SegWidth));
            var col = Ui.Blend(i == hot ? Ui.Text : Ui.Muted, Ui.Text, Ease.Clamp01(cover));
            TextRenderer.DrawText(g, items[i], SFonts.Button, new Point(sr.X + (sr.Width - sz.Width) / 2, sr.Y + (sr.Height - sz.Height) / 2), col, Ui.Flags);
        }
        FocusRing(g, Rectangle.Round(r), Height / 2F);
    }
}

internal enum SButtonKind { Primary, Secondary, Ghost, Danger }

internal sealed class SButton : SControl
{
    public SButtonKind Kind; public Glyph Glyph;
    public SButton(string text, SButtonKind kind, Glyph glyph = Glyph.None)
    {
        Text = text; Kind = kind; Glyph = glyph; Cursor = Cursors.Hand; TabStop = true; Height = S(34);
        using (var g = CreateGraphics()) Width = Ui.Measure(g, text, SFonts.Button).Width + S(32) + (glyph != Glyph.None ? S(22) : 0);
    }
    public void InvokeClick() { OnClick(EventArgs.Empty); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyDown(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; PaintBase(g);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        var rf = new RectangleF(0.5F, 0.5F, Width - 1.5F, Height - 1.5F);
        Color text;
        var sc = pressed ? 0.97F : 1F; var rr = new RectangleF(rf.X + rf.Width * (1 - sc) / 2, rf.Y + rf.Height * (1 - sc) / 2, rf.Width * sc, rf.Height * sc);
        switch (Kind)
        {
            case SButtonKind.Primary:
                using (var path = Ui.Round(rr, rr.Height / 2F)) using (var b = new LinearGradientBrush(RectangleF.Inflate(rr, 1, 1), Ui.Blend(Ui.Accent, Color.White, hover ? 0.28F : 0.18F), pressed ? Ui.Blend(Ui.Accent, Color.Black, 0.12F) : Ui.Accent, 90F)) g.FillPath(b, path);
                Glass.Paint(g, rr, rr.Height / 2F, GlassKind.Secondary, 0.8F); text = Color.White; break;
            case SButtonKind.Danger:
                using (var path = Ui.Round(rr, rr.Height / 2F)) using (var b = new SolidBrush(hover ? Ui.Blend(Ui.Danger, Color.White, 0.12F) : Ui.Danger)) g.FillPath(b, path);
                Glass.Paint(g, rr, rr.Height / 2F, GlassKind.Secondary, 0.8F); text = Color.White; break;
            case SButtonKind.Ghost:
                if (hover) Glass.Paint(g, rr, rr.Height / 2F, GlassKind.Secondary, 1.8F); text = hover ? Ui.Text : Ui.Muted; break;
            default:
                Glass.Paint(g, rr, rr.Height / 2F, hover ? GlassKind.Selected : GlassKind.Secondary, hover ? 1F : 2F); text = Ui.Text; break;
        }
        if (!Enabled) { Glass.Paint(g, rr, rr.Height / 2F, GlassKind.Secondary, 1.5F); text = Ui.Faint; }
        var sz = Ui.Measure(g, Text, SFonts.Button);
        var total = sz.Width + (Glyph != Glyph.None ? S(22) : 0);
        var x = (Width - total) / 2;
        if (Glyph != Glyph.None) { Glyphs.Draw(g, Glyph, new RectangleF(x, (Height - S(16)) / 2F, S(16), S(16)), text); x += S(22); }
        TextRenderer.DrawText(g, Text, SFonts.Button, new Point(x, (Height - sz.Height) / 2), text, Ui.Flags);
        FocusRing(g, r, Height / 2F);
    }
}

internal sealed class SStepper : SControl
{
    int value = 10, min = 1, max = 100, hotPart = -1;
    public event EventHandler Changed;
    public SStepper() { Size = new Size(S(120), S(32)); TabStop = true; }
    public int Value { get { return value; } set { value = Math.Max(min, Math.Min(max, value)); if (this.value == value) return; this.value = value; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); } }
    int Part(Point p) { return p.X < Height ? 0 : p.X > Width - Height ? 2 : 1; }
    protected override void OnMouseMove(MouseEventArgs e) { var h = Part(e.Location); if (h != hotPart) { hotPart = h; Cursor = h == 1 ? Cursors.Default : Cursors.Hand; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { hotPart = -1; base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); var p = Part(e.Location); if (p == 0) Value--; else if (p == 2) Value++; base.OnMouseDown(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { Value += e.Delta > 0 ? 1 : -1; base.OnMouseWheel(e); }
    protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value++; else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value--; base.OnKeyDown(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; PaintBase(g);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Glass.Paint(g, new RectangleF(0, 0, Width, Height), Height / 2F, GlassKind.Secondary, 1.8F);
        for (var i = 0; i < 2; i++)
        {
            var br = i == 0 ? new Rectangle(S(3), S(3), Height - S(7), Height - S(7)) : new Rectangle(Width - Height + S(3), S(3), Height - S(7), Height - S(7));
            var enabled = i == 0 ? value > min : value < max;
            if (hotPart == (i == 0 ? 0 : 2) && enabled) Glass.Paint(g, new RectangleF(br.X, br.Y, br.Width, br.Height), br.Height / 2F, GlassKind.Selected, 1F);
            using (var p = new Pen(enabled ? Ui.Text : Ui.Faint, S(1.6F)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = br.X + br.Width / 2F, cy = br.Y + br.Height / 2F, k = S(4.5F);
                g.DrawLine(p, cx - k, cy, cx + k, cy);
                if (i == 1) g.DrawLine(p, cx, cy - k, cx, cy + k);
            }
        }
        var t = value.ToString(); var sz = Ui.Measure(g, t, SFonts.RowTitle);
        TextRenderer.DrawText(g, t, SFonts.RowTitle, new Point((Width - sz.Width) / 2, (Height - sz.Height) / 2), Ui.Text, Ui.Flags);
        FocusRing(g, r, Height / 2F);
    }
}

// Click, then press a key combination. Shows the current combination as keycaps.
internal sealed class SHotkey : SControl
{
    string value = "Ctrl+Alt+V", hint;
    public event EventHandler Changed;
    public SHotkey() { Size = new Size(S(200), S(34)); Cursor = Cursors.Hand; TabStop = true; }
    public string Value { get { return value; } set { this.value = value; Invalidate(); } }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
    protected override bool IsInputKey(Keys k) { return true; }
    protected override void OnLostFocus(EventArgs e) { hint = null; base.OnLostFocus(e); }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Focused) return base.ProcessCmdKey(ref msg, keyData);
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Escape || key == Keys.Tab) return base.ProcessCmdKey(ref msg, keyData);
        if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin) return true;
        if ((keyData & (Keys.Control | Keys.Alt)) == 0) { hint = "Ctrl veya Alt ile birlikte basın"; Invalidate(); return true; }
        var text = HotkeyText.Format(keyData); uint m, vk;
        if (string.IsNullOrEmpty(text) || !HotkeyText.TryParse(text, out m, out vk)) { hint = "Bu tuş kullanılamaz"; Invalidate(); return true; }
        hint = null; value = text; Invalidate();
        if (Changed != null) Changed(this, EventArgs.Empty);
        return true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; PaintBase(g);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Glass.Paint(g, new RectangleF(0, 0, Width, Height), Height / 2F, GlassKind.Secondary, hover ? 2.4F : 1.8F);
        if (Focused) Ui.DrawRound(g, Ui.Accent, r, Height / 2F, S(1.5F));
        if (Focused && hint != null)
        {
            TextRenderer.DrawText(g, hint, SFonts.RowDesc, new Rectangle(S(12), 0, Width - S(24), Height), Color.FromArgb(232, 120, 90), Ui.Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }
        var x = S(14); var y = (Height - S(18)) / 2;
        foreach (var part in (value ?? "").Split('+')) x += Ui.DrawKeycap(g, part, x, y) + S(4);
        var right = Focused ? "Yeni kısayola basın" : "Değiştir";
        var rs = Ui.Measure(g, right, SFonts.RowDesc);
        if (x + rs.Width + S(14) < Width) TextRenderer.DrawText(g, right, SFonts.RowDesc, new Point(Width - rs.Width - S(12), (Height - rs.Height) / 2), Focused ? Ui.Accent : Ui.Faint, Ui.Flags);
    }
}

// One setting: title + description on the left, editor control on the right.
internal sealed class SRow : SControl
{
    public string Title, Description; public Glyph Glyph; public Control Editor;
    bool stacked;
    public SRow(string title, string description, Control editor, Glyph glyph = Glyph.None)
    {
        Title = title; Description = description; Editor = editor; Glyph = glyph; Height = S(description == null ? 52 : 64);
        if (editor != null) Controls.Add(editor);
    }
    // Stacked: title and description on top, the editor below them (a wide editor such as a
    // three-way choice with long labels would leave no room for the text beside it).
    public bool Stacked
    {
        get { return stacked; }
        set { stacked = value; Height = S(value ? 106 : Description == null ? 52 : 64); PerformLayout(); Invalidate(); }
    }
    int TextLeft { get { return Glyph != Glyph.None ? S(18) + S(32) + S(14) : S(18); } }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Editor == null) return;
        if (stacked) Editor.Location = new Point(TextLeft, S(60));
        else Editor.Location = new Point(Width - S(18) - Editor.Width, (Height - Editor.Height) / 2);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; PaintBase(g);
        var x = S(18);
        if (Glyph != Glyph.None)
        {
            var ib = new Rectangle(x, stacked ? S(16) : (Height - S(32)) / 2, S(32), S(32));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Glass.Paint(g, ib, S(10), GlassKind.Secondary, 1.8F);
            Glyphs.Draw(g, Glyph, new RectangleF(ib.X + S(8), ib.Y + S(8), S(16), S(16)), Ui.Text);
            x = ib.Right + S(14);
        }
        var w = (Editor != null && !stacked ? Editor.Left - S(16) : Width - S(18)) - x;
        if (Description == null)
            TextRenderer.DrawText(g, Title, SFonts.RowTitle, new Rectangle(x, 0, w, Height), Ui.Text, Ui.Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        else
        {
            TextRenderer.DrawText(g, Title, SFonts.RowTitle, new Rectangle(x, S(13), w, S(20)), Ui.Text, Ui.Flags | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Description, SFonts.RowDesc, new Rectangle(x, S(34), w, S(18)), Ui.Muted, Ui.Flags | TextFormatFlags.EndEllipsis);
        }
    }
}

// Rounded surface that stacks rows with hairline dividers.
internal sealed class SCard : SControl
{
    readonly List<Control> rows = new List<Control>();
    public SCard() { Padding = new Padding(1); }
    public SCard Add(Control row) { rows.Add(row); Controls.Add(row); Relayout(); return this; }
    public void Relayout()
    {
        var y = S(1);
        foreach (var r in rows) { r.SetBounds(S(1), y, Width - S(2), r.Height); y += r.Height + 1; }
        Height = y + S(1);
        Invalidate();
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (rows.Count > 0 && rows[0].Width != Width - S(2)) Relayout(); }
    // Card surface: page color outside, dense glass inside (also drawn behind the rows by their own PaintBase).
    public void DrawBase(Graphics g)
    {
        g.Clear(Parent != null ? Parent.BackColor : Ui.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width, Height);
        Ui.FillRound(g, Ui.Surface, r, S(16));
        Glass.Paint(g, r, S(16), GlassKind.Primary, 1F);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; DrawBase(g);
        using (var p = new Pen(Color.FromArgb(Themes.IsLight ? 40 : 30, Themes.IsLight ? Color.Black : Color.White)))
            for (var i = 0; i < rows.Count - 1; i++) { var y = rows[i].Bottom; g.DrawLine(p, S(58), y, Width - S(18), y); }
    }
}

// Theme preview tile with a miniature of the panel.
internal sealed class SThemeTile : SControl
{
    public string Mode, Label; bool selected;
    public event EventHandler Picked;
    public SThemeTile(string mode, string label) { Mode = mode; Label = label; Size = new Size(S(146), S(134)); Cursor = Cursors.Hand; TabStop = true; }
    public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }
    protected override void OnClick(EventArgs e) { Focus(); if (Picked != null) Picked(this, EventArgs.Empty); base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyDown(e); }

    static void Mini(Graphics g, Rectangle r, bool light)
    {
        Color bg = light ? Color.FromArgb(246, 248, 252) : Color.FromArgb(17, 19, 34), surf = light ? Color.White : Color.FromArgb(26, 30, 47),
              line = light ? Color.FromArgb(222, 228, 238) : Color.FromArgb(52, 59, 84), text = light ? Color.FromArgb(40, 48, 66) : Color.FromArgb(200, 206, 222),
              muted = light ? Color.FromArgb(190, 198, 212) : Color.FromArgb(70, 78, 102), sel = light ? Color.FromArgb(218, 232, 255) : Color.FromArgb(24, 38, 76);
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
        var k = r.Width / 120F;
        Ui.FillRound(g, surf, new RectangleF(r.X + 8 * k, r.Y + 8 * k, r.Width - 16 * k, 12 * k), 4 * k);
        Ui.DrawRound(g, Color.FromArgb(82, 140, 255), new RectangleF(r.X + 8 * k, r.Y + 8 * k, r.Width - 16 * k, 12 * k), 4 * k);
        Color[] av = { Color.FromArgb(96, 150, 255), Color.FromArgb(56, 196, 166), Color.FromArgb(244, 154, 82) };
        for (var i = 0; i < 3; i++)
        {
            var y = r.Y + (28 + i * 18) * k;
            if (i == 0) Ui.FillRound(g, sel, new RectangleF(r.X + 6 * k, y - 2 * k, r.Width - 12 * k, 16 * k), 4 * k);
            Ui.FillRound(g, Color.FromArgb(light ? 60 : 90, av[i]), new RectangleF(r.X + 10 * k, y, 12 * k, 12 * k), 3 * k);
            Ui.FillRound(g, text, new RectangleF(r.X + 27 * k, y + 1.5F * k, 46 * k, 4 * k), 2 * k);
            Ui.FillRound(g, muted, new RectangleF(r.X + 27 * k, y + 7.5F * k, 30 * k, 3 * k), 1.5F * k);
        }
        using (var p = new Pen(line)) g.DrawLine(p, r.X, r.Bottom - 10 * k, r.Right, r.Bottom - 10 * k);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(ParentBg); g.SmoothingMode = SmoothingMode.AntiAlias;
        var outer = new Rectangle(0, 0, Width - 1, S(92));
        var pr = Rectangle.Inflate(outer, -S(5), -S(5));
        var state = g.Save();
        using (var clip = Ui.Round(pr, S(9))) g.SetClip(clip);
        if (Mode == "system")
        {
            Mini(g, pr, false);
            using (var path = new GraphicsPath()) { path.AddPolygon(new[] { new Point(pr.Right, pr.Top), new Point(pr.Right, pr.Bottom), new Point(pr.Left, pr.Bottom) }); g.SetClip(path, CombineMode.Intersect); Mini(g, pr, true); }
        }
        else Mini(g, pr, Mode == "light");
        g.Restore(state);
        Glass.Paint(g, outer, S(12), GlassKind.Secondary, 1F); if (selected) Ui.DrawRound(g, Ui.Accent, outer, S(12), S(2F));
        // radio + label
        var cy = outer.Bottom + S(22); var d = S(16); var rc = new Rectangle(S(4), cy - d / 2, d, d);
        using (var p = new Pen(selected ? Ui.Accent : Ui.Faint, S(1.5F))) g.DrawEllipse(p, rc);
        if (selected) using (var b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, Rectangle.Inflate(rc, -S(4), -S(4)));
        var ls = Ui.Measure(g, Label, SFonts.RowTitle);
        Assets.Draw(g, Mode == "dark" ? "theme-dark" : Mode == "light" ? "theme-light" : "theme-system", new RectangleF(rc.Right + S(6), cy - S(8), S(16), S(16)), selected ? Ui.Highlight : Ui.Muted);
        TextRenderer.DrawText(g, Label, SFonts.RowTitle, new Point(rc.Right + S(28), cy - ls.Height / 2), Ui.Text, Ui.Flags);
        FocusRing(g, outer, S(10));
    }
}

// Backup status hero: big icon, last backup, count, actions.
internal sealed class SBackupHero : SControl
{
    public string Title = "", Subtitle = "";
    public readonly SButton Backup = new SButton("Şimdi yedekle", SButtonKind.Primary, Glyph.Backup);
    public readonly SButton Folder = new SButton("Klasörü aç", SButtonKind.Secondary, Glyph.Folder);
    public SBackupHero() { Height = S(118); Controls.Add(Backup); Controls.Add(Folder); }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Backup.Location = new Point(S(84), Height - S(16) - Backup.Height);
        Folder.Location = new Point(Backup.Right + S(8), Backup.Top);
    }
    public void DrawBase(Graphics g)
    {
        g.Clear(Parent != null ? Parent.BackColor : Ui.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var b = new LinearGradientBrush(r, Ui.Blend(Ui.Surface, Ui.Accent, Themes.IsLight ? 0.08F : 0.16F), Ui.Surface, 0F))
        using (var path = Ui.Round(r, S(16))) g.FillPath(b, path);
        Glass.Paint(g, r, S(16), GlassKind.Primary, 1F);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; DrawBase(g);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        var ib = new Rectangle(S(20), S(18), S(48), S(48));
        using (var b = new LinearGradientBrush(ib, Ui.Blend(Ui.Accent, Color.White, 0.2F), Ui.Accent, 90F)) using (var path = Ui.Round(ib, S(15))) g.FillPath(b, path);
        Glass.Paint(g, ib, S(15), GlassKind.Secondary, 0.9F);
        Glyphs.Draw(g, Glyph.Backup, new RectangleF(ib.X + S(12), ib.Y + S(12), S(24), S(24)), Color.White);
        TextRenderer.DrawText(g, Title, SFonts.Big, new Rectangle(S(84), S(17), Width - S(100), S(24)), Ui.Text, Ui.Flags | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, Subtitle, SFonts.RowDesc, new Rectangle(S(84), S(42), Width - S(100), S(18)), Ui.Muted, Ui.Flags | TextFormatFlags.EndEllipsis);
    }
}

// Inline note with an icon (info / warning).
internal sealed class SNote : SControl
{
    public string Text2; public Glyph Glyph;
    public SNote(string text, Glyph glyph) { Text2 = text; Glyph = glyph; Height = S(50); }
    public void SetText(string text) { Text2 = text; OnSizeChanged(EventArgs.Empty); Invalidate(); }
    // Height follows the wrapped text at the current width (theme, DPI and font can change it).
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= S(80)) return;
        var textH = TextRenderer.MeasureText(Text2 ?? "", SFonts.RowDesc, new Size(Width - S(56), int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak).Height;
        var h = Math.Max(S(50), textH + S(24));
        if (h != Height) Height = h;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent != null ? Parent.BackColor : Ui.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        var tint = Ui.Warning;
        Ui.FillRound(g, Ui.Surface, r, S(14));
        Ui.FillRound(g, Color.FromArgb(Themes.IsLight ? 22 : 30, tint), r, S(14));
        Glass.Paint(g, new RectangleF(0, 0, Width, Height), S(14), GlassKind.Secondary, 1.2F);
        Glyphs.Draw(g, Glyph, new RectangleF(S(14), (Height - S(18)) / 2F, S(18), S(18)), tint);
        TextRenderer.DrawText(g, Text2, SFonts.RowDesc, new Rectangle(S(44), S(6), Width - S(56), Height - S(12)), Ui.Text, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
    }
}

// Read-only keycap display (for the shortcuts list).
internal sealed class SKeys : SControl
{
    public SKeys(string keys) { Text = keys; Height = S(22); using (var g = CreateGraphics()) Width = Text.Split('+').Sum(k => Ui.Measure(g, k, Ui.KeyFont).Width + S(14)) + S(2); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; PaintBase(g);
        var x = 0; foreach (var k in Text.Split('+')) x += Ui.DrawKeycap(g, k, x, (Height - S(18)) / 2) + S(4);
    }
}

// Scrollable page body.
internal sealed class SPage : Panel
{
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idlist);
    // Own slim scrollbar instead of the native one: the native bar is a square strip in the system
    // colors that breaks the rounded glass window edge.
    int scrollY, contentH, dragOffset = -1;
    public string Title, Subtitle;
    public SPage(string title, string subtitle)
    {
        Title = title; Subtitle = subtitle; AutoScroll = false; DoubleBuffered = true; ClientSizeChanged += delegate { PerformLayout(); Invalidate(); };
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    int Gutter { get { return Ui.S(30); } }
    readonly List<Control> stack = new List<Control>(); readonly Dictionary<Control, int> gaps = new Dictionary<Control, int>();
    bool stacking;
    public T Add<T>(T c, int gapAfter = 14, string caption = null) where T : Control
    {
        if (caption != null) { var cap = new SCaption(caption); stack.Add(cap); gaps[cap] = 0; Controls.Add(cap); }
        stack.Add(c); gaps[c] = gapAfter; Controls.Add(c);
        c.SizeChanged += delegate { if (!stacking) PerformLayout(); };
        PerformLayout();
        return c;
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (stacking) return;
        stacking = true;
        try
        {
            var yy = Ui.S(82) - scrollY; var w = ClientSize.Width - 2 * Gutter;
            foreach (var c in stack)
            {
                var x = c is SCaption ? Gutter + Ui.S(4) : Gutter;
                c.SetBounds(x, yy, c is SCaption ? w : w, c.Height);
                var card = c as SCard; if (card != null) card.Relayout();
                yy += c.Height + Ui.S(gaps[c]);
            }
            contentH = yy + scrollY + Ui.S(10);
            var max = MaxScroll;
            if (scrollY > max) { scrollY = max; stacking = false; OnLayout(e); return; }
        }
        finally { stacking = false; }
        Invalidate();
    }

    int MaxScroll { get { return Math.Max(0, contentH - ClientSize.Height); } }

    public void ScrollBy(int dy)
    {
        var n = Math.Max(0, Math.Min(MaxScroll, scrollY + dy));
        if (n == scrollY) return;
        scrollY = n; PerformLayout(); Invalidate(true);
    }

    public void ScrollToTop() { scrollY = 0; PerformLayout(); Invalidate(true); }

    Rectangle ThumbRect()
    {
        if (MaxScroll <= 0) return Rectangle.Empty;
        var track = ClientSize.Height - Ui.S(24);
        var th = Math.Max(Ui.S(36), (int)((long)track * ClientSize.Height / contentH));
        var y = Ui.S(12) + (int)((long)(track - th) * scrollY / MaxScroll);
        return new Rectangle(ClientSize.Width - Ui.S(10), y, Ui.S(5), th);
    }

    protected override void OnMouseWheel(MouseEventArgs e) { ScrollBy(-e.Delta / 120 * Ui.S(64)); base.OnMouseWheel(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var t = ThumbRect();
        if (t.IsEmpty || e.Button != MouseButtons.Left) return;
        var hit = Rectangle.Inflate(t, Ui.S(5), Ui.S(2));
        if (hit.Contains(e.Location)) { dragOffset = e.Y - t.Y; Capture = true; }
        else if (e.X >= ClientSize.Width - Ui.S(18)) ScrollBy(e.Y < t.Y ? -ClientSize.Height + Ui.S(60) : ClientSize.Height - Ui.S(60));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragOffset < 0) return;
        var track = ClientSize.Height - Ui.S(24); var t = ThumbRect();
        var room = track - t.Height; if (room <= 0) return;
        var y = Math.Max(0, Math.Min(room, e.Y - dragOffset - Ui.S(12)));
        var n = (int)((long)MaxScroll * y / room);
        if (n != scrollY) { scrollY = n; PerformLayout(); Invalidate(true); }
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (dragOffset >= 0) { dragOffset = -1; Capture = false; } }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor);
        var off = -scrollY;
        TextRenderer.DrawText(g, Title, SFonts.PageTitle, new Point(Gutter, Ui.S(26) + off), Ui.Text, Ui.Flags);
        TextRenderer.DrawText(g, Subtitle, SFonts.RowDesc, new Point(Gutter + Ui.S(1), Ui.S(56) + off), Ui.Muted, Ui.Flags);
        var t = ThumbRect();
        if (!t.IsEmpty)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Ui.Round(new RectangleF(t.X, t.Y, t.Width, t.Height), t.Width / 2F)) using (var b = new SolidBrush(Color.FromArgb(dragOffset >= 0 ? 150 : 90, Ui.Muted))) g.FillPath(b, path);
        }
    }
}

internal sealed class SCaption : Control
{
    public SCaption(string text) { Text = text; Height = Ui.S(24); Width = Ui.S(300); SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
        TextRenderer.DrawText(e.Graphics, Text, Ui.HeaderFont, new Point(0, Ui.S(4)), Ui.Faint, Ui.Flags);
    }
}

internal sealed class SettingsForm : Form
{
    const int WM_NCHITTEST = 0x84, HTCLIENT = 1, HTCAPTION = 2, CS_DROPSHADOW = 0x20000;
    static int S(float v) { return Ui.S(v); }

    readonly AppSettings original;
    AppSettings work;
    public AppSettings Result { get; private set; }
    int profileCount;
    public int ProfileCount { get { return profileCount; } set { profileCount = value; SetLastBackup(original.LastBackupUtc, -1); } }
    public Action BackupNow, OpenBackupFolder;

    readonly string[] navTitles = { "Genel", "Yedekleme", "Görünüm", "Hakkında" };
    readonly Glyph[] navGlyphs = { Glyph.Settings, Glyph.Backup, Glyph.Palette, Glyph.Info };
    readonly List<SPage> pages = new List<SPage>();
    int nav, navHover = -1;
    readonly HeaderButton close = new HeaderButton(HeaderGlyph.Close);
    readonly SButton save = new SButton("Kaydet", SButtonKind.Primary), cancel = new SButton("İptal", SButtonKind.Secondary);
    readonly List<SThemeTile> tiles = new List<SThemeTile>();
    SBackupHero hero; SRow folderRow;
    readonly Panel content = new Panel();

    int NavWidth { get { return S(212); } }
    int FooterHeight { get { return S(66); } }

    public SettingsForm(AppSettings current)
    {
        original = (current ?? AppSettings.Defaults()).Clone(); original.Normalize();
        work = original.Clone();
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; KeyPreview = true; DoubleBuffered = true; Text = "Ayarlar";
        Size = new Size(S(780), S(600)); Font = SFonts.Nav;
        SetStyle(ControlStyles.ResizeRedraw, true);

        content.SetBounds(NavWidth, 0, Width - NavWidth, Height - FooterHeight);
        Controls.Add(content);
        BuildGeneral(); BuildBackup(); BuildAppearance(); BuildAbout();
        foreach (var p in pages) { p.Dock = DockStyle.Fill; content.Controls.Add(p); }

        close.Solid = true; close.Location = new Point(Width - close.Width - S(14), S(12)); Controls.Add(close); close.BringToFront();
        close.Click += delegate { DialogResult = DialogResult.Cancel; };
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
        save.Click += delegate { Save(); };
        save.Width = Math.Max(save.Width, S(116)); cancel.Width = Math.Max(cancel.Width, S(96));
        save.Location = new Point(Width - S(24) - save.Width, Height - FooterHeight + (FooterHeight - save.Height) / 2);
        cancel.Location = new Point(save.Left - S(10) - cancel.Width, save.Top);
        Controls.Add(cancel); Controls.Add(save);

        KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.S) { Save(); e.Handled = true; }
        };
        FormClosed += delegate { if (Result == null) Themes.Apply(original.Theme); };
        ShowPage(0);
        ApplyColors();
    }

    protected override CreateParams CreateParams { get { var cp = base.CreateParams; return cp; } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyFrame();
    }

    void ApplyFrame()
    {
        try
        {
            var d = Themes.IsLight ? 0 : 1; NativeMethods.DwmSetWindowAttribute(Handle, 20, ref d, 4);
            Backdrop.SetCorner(Handle, 2);
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && m.Result.ToInt32() == HTCLIENT)
        {
            var v = m.LParam.ToInt64();
            var p = PointToClient(new Point((short)(v & 0xffff), (short)((v >> 16) & 0xffff)));
            if (p.Y < S(18) || (p.X < NavWidth && p.Y < S(72))) m.Result = (IntPtr)HTCAPTION;
        }
    }

    // ---------- Pages ----------

    SPage NewPage(string title, string subtitle)
    {
        var p = new SPage(title, subtitle) { Size = content.ClientSize, BackColor = Ui.Bg };
        return p;
    }

    void BuildGeneral()
    {
        var page = NewPage("Genel", "Kısayol, başlangıç ve panel davranışı");
        var hk = new SHotkey { Value = work.Hotkey };
        hk.Changed += delegate { work.Hotkey = hk.Value; Dirty(); };
        page.Add(new SCard(), caption: "KISAYOL").Add(new SRow("Paneli aç / gizle", "Her uygulamadan çalışır", hk, Glyph.Keyboard));

        var auto = new SToggle(); auto.SetInstant(work.AutoStart);
        auto.CheckedChanged += delegate { work.AutoStart = auto.Checked; Dirty(); };
        var presence = new SSegmented("Tepsi", "Tepsi + düğme", "Görev çubuğu") { SelectedIndex = Math.Max(0, Array.IndexOf(PresenceMode.All, work.Presence)) };
        var presenceRow = new SRow("Uygulama nerede görünsün", PresenceText(work.Presence), presence, Glyph.Layers) { Stacked = true };
        presence.Changed += delegate { work.Presence = PresenceMode.All[presence.SelectedIndex]; presenceRow.Description = PresenceText(work.Presence); presenceRow.Invalidate(); Dirty(); };
        var side = new SSegmented("Otomatik", "Sol", "Sağ") { SelectedIndex = work.DockSide == "left" ? 1 : work.DockSide == "right" ? 2 : 0 };
        side.Changed += delegate { work.DockSide = new[] { "auto", "left", "right" }[side.SelectedIndex]; Dirty(); };
        var reach = new SToggle(); reach.SetInstant(work.ReachabilityCheck);
        reach.CheckedChanged += delegate { work.ReachabilityCheck = reach.Checked; Dirty(); };
        var forti = new SToggle(); forti.SetInstant(work.ShowForti);
        forti.CheckedChanged += delegate { work.ShowForti = forti.Checked; Dirty(); };
        var card = page.Add(new SCard(), caption: "DAVRANIŞ")
            .Add(new SRow("Windows açılışında başlat", "Oturum açılınca arka planda başlar, OpenVPN açılınca görünür.", auto, Glyph.Power))
            .Add(presenceRow)
            .Add(new SRow("Panel tarafı", "Yapışılacak taraf", side, Glyph.Panel))
            .Add(new SRow("Sunucu erişim testi", "Profil avatarında yeşil / kırmızı durum noktası.", reach, Glyph.Server))
            .Add(new SRow("FortiClient profilleri", "FortiClient VPN tünellerini de listele.", forti, Glyph.ShieldLock));
        // Sophos / WatchGuard toggles only for clients found on this PC (demo: always, nothing is read).
        if (Demo.On || SophosStore.IsInstalled)
        {
            var sophos = new SToggle(); sophos.SetInstant(work.ShowSophos);
            sophos.CheckedChanged += delegate { work.ShowSophos = sophos.Checked; Dirty(); };
            card.Add(new SRow("Sophos Connect profilleri", "Sophos Connect bağlantılarını da listele.", sophos, Glyph.ShieldLock));
        }
        if (Demo.On || WatchGuardStore.IsInstalled)
        {
            var wg = new SToggle(); wg.SetInstant(work.ShowWatchGuard);
            wg.CheckedChanged += delegate { work.ShowWatchGuard = wg.Checked; Dirty(); };
            card.Add(new SRow("WatchGuard profilleri", "WatchGuard SSL VPN sunucularını da listele.", wg, Glyph.ShieldLock));
        }
        pages.Add(page);
    }

    void BuildBackup()
    {
        var page = NewPage("Yedekleme", "Profillerinizi otomatik ve güvenli şekilde yedekleyin");
        hero = page.Add(new SBackupHero());
        hero.Backup.Click += delegate { if (BackupNow != null) { hero.Title = "Yedekleniyor…"; hero.Invalidate(); BackupNow(); } };
        hero.Folder.Click += delegate { if (OpenBackupFolder != null) OpenBackupFolder(); };
        SetLastBackup(original.LastBackupUtc, -1);

        var freq = new SSegmented("Kapalı", "Değişince", "Günlük", "Haftalık") { SelectedIndex = Array.IndexOf(new[] { "off", "change", "daily", "weekly" }, work.BackupFrequency) };
        freq.Changed += delegate { work.BackupFrequency = new[] { "off", "change", "daily", "weekly" }[freq.SelectedIndex]; Dirty(); };
        var keep = new SStepper { Value = work.BackupKeep };
        keep.Changed += delegate { work.BackupKeep = keep.Value; Dirty(); };
        var browse = new SButton("Değiştir", SButtonKind.Secondary, Glyph.Folder);
        folderRow = new SRow("Yedek klasörü", ShortPath(work.BackupFolder), browse, Glyph.Folder);
        browse.Click += delegate { PickFolder(); };
        page.Add(new SCard(), caption: "OTOMATİK YEDEK")
            .Add(new SRow("Sıklık", null, freq, Glyph.History))
            .Add(new SRow("Saklanacak yedek sayısı", "Daha eski yedekler otomatik silinir.", keep, Glyph.Duplicates))
            .Add(folderRow);
        page.Add(new SNote("Yedekler .ovpn içindeki özel anahtarları içerir; güvenli bir yerde saklayın. Kayıtlı parolalar yedeğe dahil edilmez.", Glyph.Lock));
        pages.Add(page);
    }

    void BuildAppearance()
    {
        var page = NewPage("Görünüm", "Tema anında önizlenir; kaydetmezseniz geri alınır");
        var holder = page.Add(new Panel { Height = S(140) }, caption: "TEMA");
        holder.BackColor = Ui.Bg;
        var modes = new[] { "dark", "light", "system" }; var labels = new[] { "Glass Dark", "Glass Light", "Sistem" };
        for (var i = 0; i < 3; i++)
        {
            var t = new SThemeTile(modes[i], labels[i]) { Location = new Point(i * S(160), 0), Selected = work.Theme == modes[i] };
            t.Picked += delegate(object s, EventArgs e)
            {
                var tile = (SThemeTile)s; work.Theme = tile.Mode;
                foreach (var x in tiles) x.Selected = x == tile;
                Themes.Apply(work.Theme); ApplyColors(); Dirty();
                if (Owner != null) Owner.Invalidate(true);
            };
            tiles.Add(t); holder.Controls.Add(t);
        }
        page.Add(new SNote("Sistem: Windows'un uygulama teması ayarını izler (Ayarlar › Kişiselleştirme › Renkler).", Glyph.Info));
        pages.Add(page);
    }

    void BuildAbout()
    {
        var page = NewPage("Hakkında", "OpenVPN Hızlı Arama · sürüm " + UpdateLogic.CurrentVersion);

        // Updates: current version + last check + "Şimdi denetle", mode, interval, release notes.
        checkUpdates = new SButton("Şimdi denetle", SButtonKind.Secondary, Glyph.Refresh);
        checkUpdates.Click += delegate
        {
            if (CheckUpdates == null) return;
            SetUpdateStatus("Denetleniyor…");
            CheckUpdates();
        };
        updateRow = new SRow("Sürüm " + UpdateLogic.CurrentVersion, LastCheckText(original.LastUpdateCheckUtc), checkUpdates, Glyph.Info);
        var modes = new[] { "ask", "auto", "off" };
        var mode = new SSegmented("Sor", "Otomatik", "Kapalı") { SelectedIndex = Math.Max(0, Array.IndexOf(modes, work.UpdateMode)) };
        var modeRow = new SRow("Güncellemeler", ModeText(work.UpdateMode), mode, Glyph.Refresh);
        mode.Changed += delegate { work.UpdateMode = modes[mode.SelectedIndex]; modeRow.Description = ModeText(work.UpdateMode); modeRow.Invalidate(); Dirty(); };
        var intervals = new[] { "startup", "daily", "weekly" };
        var interval = new SSegmented("Açılışta", "Günlük", "Haftalık") { SelectedIndex = Math.Max(0, Array.IndexOf(intervals, work.UpdateInterval)) };
        interval.Changed += delegate { work.UpdateInterval = intervals[interval.SelectedIndex]; Dirty(); };
        var notes = new SButton("Aç", SButtonKind.Secondary, Glyph.Open);
        notes.Click += delegate { UpdateClient.OpenReleasePage(ReleaseNotesUrl); };
        page.Add(new SCard(), caption: "GÜNCELLEME")
            .Add(updateRow)
            .Add(modeRow)
            .Add(new SRow("Denetleme sıklığı", "Profil bilgisi gönderilmez", interval, Glyph.History))
            .Add(new SRow("Sürüm notları", "GitHub sürüm sayfası", notes, Glyph.Open));

        // Diagnostics: app log on/off, crash prompt, log folder, report the last error (README
        // "Hata raporları ve günlükler"). Nothing is sent by the app; the report opens in the browser.
        var logToggle = new SToggle(); logToggle.SetInstant(work.LogEnabled);
        logToggle.CheckedChanged += delegate { work.LogEnabled = logToggle.Checked; Dirty(); };
        // "Otomatik" only in a build with a token and after a one-time public-issue warning; a stored
        // "auto" that no longer qualifies is shown (and kept) as "Sor".
        var promptModes = new[] { "ask", "auto", "never" };
        if (work.CrashPrompt == "auto" && !(IssueToken.Available && work.CrashAutoConsent)) { work.CrashPrompt = original.CrashPrompt = "ask"; work.CrashAutoConsent = original.CrashAutoConsent = false; }
        var prompt = new SSegmented("Sor", "Otomatik", "Kapalı") { SelectedIndex = Math.Max(0, Array.IndexOf(promptModes, work.CrashPrompt)) };
        var promptRow = new SRow("Çökmeden sonra rapor", CrashPromptText(work.CrashPrompt), prompt, Glyph.Info);
        var note = new SNote(CrashReporter.SummaryText(work.CrashPrompt), Glyph.Lock);
        var settingPrompt = false;
        prompt.Changed += delegate
        {
            if (settingPrompt) return;
            var picked = promptModes[prompt.SelectedIndex];
            var confirmed = false; var declined = false;
            if (picked == "auto" && !IssueToken.Available)
            {
                GlassDialog.Show(this, "Bu sürümde gönderme anahtarı yok; Otomatik seçilemez ve rapor tarayıcıda açılır.", "Otomatik gönderim", MessageBoxButtons.OK, MessageBoxIcon.Information);
                declined = true;
            }
            else if (picked == "auto")
            {
                confirmed = (work.CrashPrompt == "auto" && work.CrashAutoConsent) ||
                    GlassDialog.Show(this, CrashReporter.AutoWarning + "\n\nOtomatik kipte rapor, çökmeden sonra size sorulmadan GitHub'da herkese açık bir kayıt olarak oluşturulur. " +
                        "Bunu kabul ediyor musunuz?", "Otomatik gönderim", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
                declined = !confirmed;
            }
            bool consent;
            // Cancelled / not possible: back to the mode chosen before (work still holds it).
            if (declined) consent = work.CrashAutoConsent;
            else work.CrashPrompt = CrashPromptLogic.StoredMode(picked, IssueToken.Available, confirmed, out consent);
            work.CrashAutoConsent = consent;
            if (work.CrashPrompt != picked) { settingPrompt = true; prompt.SelectedIndex = Array.IndexOf(promptModes, work.CrashPrompt); settingPrompt = false; }
            promptRow.Description = CrashPromptText(work.CrashPrompt); promptRow.Invalidate();
            note.SetText(CrashReporter.SummaryText(work.CrashPrompt));
            Dirty();
        };
        var openLogs = new SButton("Aç", SButtonKind.Secondary, Glyph.Folder);
        openLogs.Click += delegate { if (OpenLogFolder != null) OpenLogFolder(); };
        var report = new SButton("Aç", SButtonKind.Secondary, Glyph.Open);
        report.Click += delegate { if (ReportLastError != null) ReportLastError(this); };
        page.Add(new SCard(), caption: "TANILAMA")
            .Add(new SRow("Hata kaydı tut", "Uygulama günlüğü ve hata kayıtları; parola yazılmaz", logToggle, Glyph.History))
            .Add(promptRow)
            .Add(new SRow("Günlük klasörünü aç", ShortPath(CrashReporter.DefaultLogDir), openLogs, Glyph.Folder))
            .Add(new SRow("Son hatayı bildir", IssueToken.Available ? "Temizlenmiş raporu GitHub'a gönderir (önce gösterir)" : "Temizlenmiş raporu tarayıcıda GitHub'da açar", report, Glyph.Open));
        page.Add(note);

        var dataDir = Path.GetDirectoryName(SettingsStore.FilePath);
        var openData = new SButton("Aç", SButtonKind.Secondary, Glyph.Folder);
        openData.Click += delegate { try { Directory.CreateDirectory(dataDir); Process.Start("explorer.exe", "\"" + dataDir + "\""); } catch { } };
        page.Add(new SCard(), caption: "VERİLER")
            .Add(new SRow("Ayarlar, geçmiş ve sabitlenenler", ShortPath(dataDir), openData, Glyph.Folder))
            .Add(new SRow("OpenVPN Connect", OpenVpnInfo(), null, Glyph.Server));
        var card = page.Add(new SCard(), caption: "KISAYOLLAR");
        card.Add(new SRow("Paneli aç / gizle", null, new SKeys(original.Hotkey), Glyph.Keyboard))
            .Add(new SRow("Profil seç", null, new SKeys("↑+↓"), Glyph.None))
            .Add(new SRow("Seçili profile bağlan", null, new SKeys("Enter"), Glyph.None))
            .Add(new SRow("Sabitle / kaldır", null, new SKeys("Ctrl+P"), Glyph.None))
            .Add(new SRow("Ayarlar", null, new SKeys("Ctrl+,"), Glyph.None))
            .Add(new SRow("Aramayı temizle / gizle", null, new SKeys("Esc"), Glyph.None));
        pages.Add(page);
    }

    public void SetLastBackup(DateTime utc, int count)
    {
        if (hero == null) return;
        if (utc <= DateTime.MinValue.AddYears(1)) { hero.Title = "Henüz yedek alınmadı"; }
        else
        {
            var local = utc.ToLocalTime();
            var day = local.Date == DateTime.Today ? "bugün" : local.Date == DateTime.Today.AddDays(-1) ? "dün" : local.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
            hero.Title = "Son yedek " + day + " " + local.ToString("HH:mm");
        }
        hero.Subtitle = (count >= 0 ? count : profileCount) + " profil  ·  " + ShortPath(work.BackupFolder);
        hero.Invalidate();
    }

    // ---------- Updates ----------

    SButton checkUpdates; SRow updateRow;
    public Action CheckUpdates;                                   // main form runs the check (off the UI thread)
    public string ReleaseNotesUrl = UpdateLogic.ReleasesPage;      // newest release page once a check found one
    public Action OpenLogFolder;                                  // diagnostics: open %APPDATA%\OpenVpnQuickSearch\logs
    public Action<IWin32Window> ReportLastError;                  // diagnostics: "Son hatayı bildir" (owner = this window)

    // One line under "Çökmeden sonra rapor"; the note below carries the full warning.
    static string CrashPromptText(string mode)
    {
        if (mode == "auto") return IssueToken.Available ? "Otomatik: sormadan gönderir (herkese açık)" : "Otomatik: anahtar yok, Sor gibi";
        if (mode == "never") return "Kapalı: sormaz";
        return "Sor: göndermeden önce sorar";
    }

    internal static string PresenceText(string mode)
    {
        return mode == PresenceMode.Taskbar ? "Görev çubuğu: düğme uygulama açıkken hep durur, tepsi simgesi gizlenir."
             : mode == PresenceMode.Both ? "Tepsi + düğme: tepsi simgesi; pencere açıkken görev çubuğunda düğme."
             : "Tepsi: simge tepside durur; gizlenince görev çubuğunda görünmez.";
    }

    static string ModeText(string mode)
    {
        return mode == "auto" ? "Otomatik: boştayken sessizce kurar"
             : mode == "off" ? "Kapalı: yalnızca elle denetim"
             : "Sor: haber verir, siz seçersiniz";
    }

    static string LastCheckText(DateTime utc)
    {
        if (utc <= DateTime.MinValue.AddYears(1)) return "Henüz denetlenmedi";
        var local = utc.ToLocalTime();
        var day = local.Date == DateTime.Today ? "bugün" : local.Date == DateTime.Today.AddDays(-1) ? "dün" : local.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
        return "Son denetim " + day + " " + local.ToString("HH:mm");
    }

    // Result of a check: "Güncel", "Sürüm X hazır", "Denetlenemedi: …" (shown under the version).
    public void SetUpdateStatus(string text) { if (updateRow == null) return; updateRow.Description = text; updateRow.Invalidate(); }
    public void SetUpdateResult(string text, DateTime lastCheckUtc)
    {
        SetUpdateStatus(lastCheckUtc > DateTime.MinValue.AddYears(1) ? text + "  ·  " + LastCheckText(lastCheckUtc) : text);
    }

    static string OpenVpnInfo()
    {
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var exe = Path.Combine(root, "OpenVPN Connect", "OpenVPNConnect.exe");
            if (!File.Exists(exe)) continue;
            try { return "Kurulu · sürüm " + FileVersionInfo.GetVersionInfo(exe).ProductVersion; } catch { return "Kurulu"; }
        }
        return "Bulunamadı";
    }

    static string ShortPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path.Substring(home.Length) : path;
    }

    void PickFolder()
    {
        using (var dlg = new FolderBrowserDialog { Description = "Yedeklerin kaydedileceği klasörü seçin", SelectedPath = work.BackupFolder, ShowNewFolderButton = true })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            work.BackupFolder = dlg.SelectedPath; folderRow.Description = ShortPath(dlg.SelectedPath); folderRow.Invalidate();
            SetLastBackup(original.LastBackupUtc, -1); Dirty();
        }
    }

    // ---------- Chrome ----------

    protected override void OnShown(EventArgs e) { base.OnShown(e); if (Demo.On && Demo.SettingsPage > 0 && Demo.SettingsPage < pages.Count) ShowPage(Demo.SettingsPage); }

    void ShowPage(int i)
    {
        nav = i;
        for (var k = 0; k < pages.Count; k++) pages[k].Visible = k == i;
        pages[i].ScrollToTop();
        Invalidate(new Rectangle(0, 0, NavWidth, Height));
    }

    // Wheel over any control of the window scrolls the visible page (pages have no native scrollbar).
    protected override void OnMouseWheel(MouseEventArgs e) { if (nav >= 0 && nav < pages.Count) pages[nav].ScrollBy(-e.Delta / 120 * S(64)); base.OnMouseWheel(e); }

    Rectangle NavRect(int i) { return new Rectangle(S(18), S(84) + i * S(42), NavWidth - S(36), S(36)); }

        protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = -1; for (var i = 0; i < navTitles.Length; i++) if (NavRect(i).Contains(e.Location)) h = i;
        if (h != navHover) { navHover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(new Rectangle(0, 0, NavWidth, Height)); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (navHover >= 0) { navHover = -1; Invalidate(); } }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        for (var i = 0; i < navTitles.Length; i++) if (NavRect(i).Contains(e.Location)) { ShowPage(i); return; }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Tab)) { ShowPage((nav + 1) % pages.Count); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Tab)) { ShowPage((nav + pages.Count - 1) % pages.Count); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    bool IsDirty
    {
        get
        {
            return work.Hotkey != original.Hotkey || work.AutoStart != original.AutoStart || work.Presence != original.Presence || work.BackupFolder != original.BackupFolder ||
                   work.BackupFrequency != original.BackupFrequency || work.BackupKeep != original.BackupKeep || work.DockSide != original.DockSide ||
                   work.Theme != original.Theme || work.ReachabilityCheck != original.ReachabilityCheck || work.ShowForti != original.ShowForti ||
                   work.ShowSophos != original.ShowSophos || work.ShowWatchGuard != original.ShowWatchGuard ||
                   work.UpdateMode != original.UpdateMode || work.UpdateInterval != original.UpdateInterval ||
                   work.LogEnabled != original.LogEnabled || work.CrashPrompt != original.CrashPrompt || work.CrashAutoConsent != original.CrashAutoConsent;
        }
    }

    void Dirty() { save.Enabled = IsDirty; save.Invalidate(); Invalidate(new Rectangle(NavWidth, Height - FooterHeight, Width - NavWidth, FooterHeight)); }

    void ApplyColors()
    {
        BackColor = Ui.Bg; content.BackColor = Ui.Bg; close.BackColor = Ui.Bg;
        foreach (var p in pages) { p.BackColor = Ui.Bg; foreach (Control c in p.Controls) if (c is Panel) c.BackColor = Ui.Bg; }
        ApplyFrame();
        Dirty();
        Invalidate(true);
    }

    void Save()
    {
        uint m, vk;
        if (!HotkeyText.TryParse(work.Hotkey, out m, out vk)) { ShowPage(0); GlassDialog.Show(this, "Kısayol geçersiz.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        try { Directory.CreateDirectory(work.BackupFolder); }
        catch (Exception ex) { ShowPage(1); GlassDialog.Show(this, "Yedek klasörü kullanılamıyor:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        work.Normalize();
        Result = work.Clone();
        DialogResult = DialogResult.OK;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;

        // navigation rail: a floating glass panel on the window background
        var rail = new RectangleF(S(8), S(8), NavWidth - S(14), Height - S(16));
        Ui.FillRound(g, Ui.Surface, rail, S(18));
        Glass.Paint(g, rail, S(18), GlassKind.Primary, 1F);
        IconArt.DrawMark(g, new RectangleF(S(24), S(24), S(30), S(30)));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, "Ayarlar", SFonts.Big, new Point(S(64), S(23)), Ui.Text, Ui.Flags);
        TextRenderer.DrawText(g, "Hızlı Arama", SFonts.RowDesc, new Point(S(65), S(43)), Ui.Muted, Ui.Flags);
        for (var i = 0; i < navTitles.Length; i++)
        {
            var r = NavRect(i); var sel = i == nav;
            if (sel) Glass.Paint(g, r, r.Height / 2F, GlassKind.Selected, 1F, 0.3F);
            else if (i == navHover) Glass.Paint(g, r, r.Height / 2F, GlassKind.Secondary, 2F);
            Glyphs.Draw(g, navGlyphs[i], new RectangleF(r.X + S(14), r.Y + (r.Height - S(17)) / 2F, S(17), S(17)), sel ? Ui.Text : Ui.Muted);
            var ts = Ui.Measure(g, navTitles[i], sel ? SFonts.NavBold : SFonts.Nav);
            TextRenderer.DrawText(g, navTitles[i], sel ? SFonts.NavBold : SFonts.Nav, new Point(r.X + S(42), r.Y + (r.Height - ts.Height) / 2), sel ? Ui.Text : Ui.Muted, Ui.Flags);
        }
        TextRenderer.DrawText(g, "Ctrl+Tab ile sekmeler arasında geçin", Ui.SmallFont, new Rectangle(S(20), Height - S(40), NavWidth - S(30), S(20)), Ui.Faint, Ui.Flags | TextFormatFlags.EndEllipsis);

        // footer
        var fy = Height - FooterHeight;
        using (var b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, NavWidth, fy, Width - NavWidth, FooterHeight);
        using (var p = new Pen(Color.FromArgb(Themes.IsLight ? 40 : 30, Themes.IsLight ? Color.Black : Color.White))) g.DrawLine(p, NavWidth + S(10), fy, Width - S(22), fy);
        if (IsDirty)
        {
            var dy = fy + FooterHeight / 2;
            using (var b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, NavWidth + S(28), dy - S(4), S(8), S(8));
            TextRenderer.DrawText(g, "Kaydedilmemiş değişiklikler", SFonts.RowDesc, new Point(NavWidth + S(44), dy - S(8)), Ui.Muted, Ui.Flags);
        }
        Glass.WindowFrame(g, ClientRectangle, S(8));
    }
}

