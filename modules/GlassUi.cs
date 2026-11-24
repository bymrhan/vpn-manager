using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Windows.Forms;

// =====================================================================
// Liquid Glass controls: header buttons, floating search field, capsule
// segmented control, Dynamic Island connection capsule, profile list.
// All of them paint on the translucent window: they clear to transparent
// first (SourceCopy) and draw text through GText (alpha-correct).
// =====================================================================

internal enum HeaderGlyph { More, Close, Minimize }

internal static class GlassPaint
{
    // Starts a frame on a glass control: transparent clear (honours the clip), then normal blending.
    public static void Begin(Graphics g, Control c)
    {
        Glass.ChildBase(g, c);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    // Press feedback / hover glow helper: eases a value toward a target.
    public static void Style2(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; }
    public static Color A(Color c, float alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, (int)(alpha * c.A))), c); }
}

// ---------------------------------------------------------------------
// Header buttons (menu, minimize, close): tiny glass circles
// ---------------------------------------------------------------------
internal sealed class HeaderButton : Control
{
    readonly Spring hover = new Spring(0, 380, 28), press = new Spring(0, 520, 30);
    readonly Animator anim = new Animator();
    bool over, down;
    public HeaderGlyph Glyph { get; set; }
    public bool Solid;   // on opaque windows (settings): paint the parent color instead of the glass window tint

    public HeaderButton(HeaderGlyph glyph)
    {
        Glyph = glyph; Size = new Size(Ui.S(28), Ui.S(28)); Cursor = Cursors.Hand; TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true); SetStyle(ControlStyles.Selectable, false);
        anim.Tick = dt => { var a = hover.Step(dt); var b = press.Step(dt); Invalidate(); return a || b; };
        MouseEnter += delegate { over = true; hover.Target = 1; anim.Kick(); };
        MouseLeave += delegate { over = false; down = false; hover.Target = 0; press.Target = 0; anim.Kick(); };
        MouseDown += delegate { down = true; press.Target = 1; anim.Kick(); };
        MouseUp += delegate { down = false; press.Target = 0; anim.Kick(); };
    }

    protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; if (Solid && Parent != null) { g.Clear(Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias; } else GlassPaint.Begin(g, this);
        var sc = 1F - 0.1F * press.Value;
        var d = Math.Min(Width, Height) - 2F; var rect = new RectangleF((Width - d * sc) / 2F, (Height - d * sc) / 2F, d * sc, d * sc);
        var h = Ease.Clamp01(hover.Value);
        if (Glyph == HeaderGlyph.Close && h > 0.01F)
            using (var b = new SolidBrush(Color.FromArgb((int)(210 * h), Ui.Danger))) g.FillEllipse(b, rect);
        else Glass.Paint(g, rect, d / 2F, GlassKind.Secondary, h * 1.4F);
        var c = Ui.Blend(Ui.Muted, Glyph == HeaderGlyph.Close ? Color.White : Ui.Text, h);
        float cx = Width / 2F, cy = Height / 2F;
        if (Glyph == HeaderGlyph.More)
            using (var b = new SolidBrush(c)) { float r = Ui.S(1.5F), gap = Ui.S(4.6F); for (var i = -1; i <= 1; i++) g.FillEllipse(b, cx + i * gap - r, cy - r, r * 2, r * 2); }
        else
            using (var p = new Pen(c, Ui.S(1.5F)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                var k = Ui.S(4.2F);
                if (Glyph == HeaderGlyph.Minimize) g.DrawLine(p, cx - k, cy, cx + k, cy);
                else { k = Ui.S(3.8F); g.DrawLine(p, cx - k, cy - k, cx + k, cy + k); g.DrawLine(p, cx - k, cy + k, cx + k, cy - k); }
            }
    }
}

// ---------------------------------------------------------------------
// Floating search field (Spotlight style). The text box is a real TextBox, so the field is the
// one nearly opaque glass surface; everything around it stays translucent.
// ---------------------------------------------------------------------
internal sealed class SearchBox : Control
{
    // The TextBox is only the editing engine (keys, IME, clipboard, undo). It is 1x1 and parked outside
    // the field, because native controls paint without alpha and would show as bright patches on the
    // translucent window. The text, selection and caret are drawn here with the glass text path.
    public readonly TextBox Input = new TextBox();
    readonly Spring focus = new Spring(0, 360, 30), hoverSp = new Spring(0, 360, 30);
    readonly Animator anim = new Animator();
    readonly System.Windows.Forms.Timer blink = new System.Windows.Forms.Timer { Interval = 530 };
    bool focused, hoverClear, caretOn = true, dragging;
    int anchor, scrollX;
    string hotkey = "Ctrl+Alt+V";

    public SearchBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true); SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.IBeam; TabStop = false;
        Input.BorderStyle = BorderStyle.None; Input.Font = Ui.InputFont; Input.Size = new Size(1, 1); Input.Location = new Point(-2000, -2000);
        anim.Tick = dt => { var a = focus.Step(dt); var b = hoverSp.Step(dt); Invalidate(); return a || b; };
        Input.GotFocus += delegate { focused = true; focus.Target = 1; caretOn = true; blink.Start(); anim.Kick(); };
        Input.LostFocus += delegate { focused = false; focus.Target = 0; blink.Stop(); anim.Kick(); };
        Input.TextChanged += delegate { Invalidate(); };
        Input.KeyDown += delegate { ResetCaret(); BeginInvoke(new Action(Invalidate)); };
        Input.KeyUp += delegate { Invalidate(); };
        blink.Tick += delegate { caretOn = !caretOn; Invalidate(TextArea); };
        MouseEnter += delegate { hoverSp.Target = 1; anim.Kick(); };
        Controls.Add(Input);
        RefreshTheme();
    }

    protected override void Dispose(bool disposing) { if (disposing) { anim.Dispose(); blink.Dispose(); } base.Dispose(disposing); }

    void ResetCaret() { caretOn = true; if (focused) { blink.Stop(); blink.Start(); } }

    public string Hotkey { get { return hotkey; } set { hotkey = value ?? ""; Invalidate(); } }
    public void RefreshTheme() { Input.BackColor = Ui.Field; Input.ForeColor = Ui.Text; Invalidate(true); }

    Rectangle Box { get { var m = Ui.S(4); return new Rectangle(m, m, Width - 2 * m, Height - 2 * m); } }
    Rectangle ClearRect { get { var b = Box; var d = Ui.S(22); return new Rectangle(b.Right - Ui.S(14) - d, b.Y + (b.Height - d) / 2, d, d); } }
    Rectangle TextArea { get { var b = Box; var x = b.X + Ui.S(46); return new Rectangle(x, b.Y, Math.Max(Ui.S(60), b.Right - x - Ui.S(128)), b.Height); } }

    int WidthOf(int chars) { var t = Input.Text; if (chars <= 0) return 0; return GText.Measure(t.Substring(0, Math.Min(chars, t.Length)), Ui.InputFont).Width; }

    int IndexAt(int x)
    {
        var t = Input.Text; var rel = x - TextArea.X + scrollX;
        if (rel <= 0) return 0;
        var lo = 0; var hi = t.Length;
        while (lo < hi) { var mid = (lo + hi) / 2; if (WidthOf(mid + 1) - (WidthOf(mid + 1) - WidthOf(mid)) / 2 <= rel) lo = mid + 1; else hi = mid; }
        return lo;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; GlassPaint.Begin(g, this);
        var b = Box; var radius = (float)Ui.S(14); var f = Ease.Clamp01(focus.Value);
        for (var i = 3; i >= 1; i--)
        {
            var grow = Ui.S(i * 1.6F) * f;
            if (grow < 0.3F) continue;
            using (var path = Ui.Round(new RectangleF(b.X - grow, b.Y - grow, b.Width + 2 * grow, b.Height + 2 * grow), radius + grow))
            using (var p = new Pen(Color.FromArgb((int)(f * (62 - i * 15)), Ui.Accent), Ui.S(1.6F))) g.DrawPath(p, path);
        }
        // a firm glass field: dense tint so typing stays readable on any wallpaper
        Glass.Paint(g, b, radius, GlassKind.Floating, 1F, 0.18F + 0.5F * Ease.Clamp01(hoverSp.Value));
        if (f > 0.01F) using (var path = Ui.Round(new RectangleF(b.X + 0.5F, b.Y + 0.5F, b.Width - 1, b.Height - 1), radius))
            using (var p = new Pen(Color.FromArgb((int)(190 * f), Ui.Accent), Ui.S(1.3F))) g.DrawPath(p, path);
        Assets.Draw(g, "search", new RectangleF(b.X + Ui.S(16), b.Y + (b.Height - Ui.S(20)) / 2F, Ui.S(20), Ui.S(20)), Ui.Blend(Ui.Muted, Ui.Highlight, f));

        // text, selection, caret (clipped to the text area; scrolled so the caret stays visible)
        var ta = TextArea; var text = Input.Text;
        var selStart = Math.Min(Input.SelectionStart, text.Length); var selLen = Math.Min(Input.SelectionLength, text.Length - selStart);
        var caret = selLen > 0 && anchor == selStart ? selStart + selLen : selLen > 0 && anchor == selStart + selLen ? selStart : selStart + selLen;
        var cx = WidthOf(caret);
        if (cx - scrollX > ta.Width - Ui.S(4)) scrollX = cx - ta.Width + Ui.S(4);
        if (cx - scrollX < 0) scrollX = cx;
        if (scrollX < 0 || WidthOf(text.Length) <= ta.Width) scrollX = 0;
        var state = g.Save(); g.SetClip(ta, CombineMode.Intersect);
        var th = GText.Measure("Ag", Ui.InputFont).Height; var ty = ta.Y + (ta.Height - th) / 2;
        if (text.Length == 0) GText.Draw(g, "Firma, IP veya kullanıcı adı ara…", Ui.InputFont, new Point(ta.X, ty), Ui.Faint);
        else
        {
            if (selLen > 0)
                using (var br = new SolidBrush(Color.FromArgb(Themes.IsLight ? 70 : 110, Ui.Accent))) g.FillRectangle(br, ta.X - scrollX + WidthOf(selStart), ty - Ui.S(1), WidthOf(selStart + selLen) - WidthOf(selStart), th + Ui.S(2));
            GText.Draw(g, text, Ui.InputFont, new Point(ta.X - scrollX, ty), Ui.Text);
        }
        if (focused && caretOn && selLen == 0)
            using (var br = new SolidBrush(Ui.Accent)) g.FillRectangle(br, ta.X - scrollX + cx, ty - Ui.S(1), Math.Max(1, Ui.S(1.5F)), th + Ui.S(2));
        g.Restore(state);

        if (text.Length > 0)
        {
            var c = ClearRect;
            using (var br = new SolidBrush(hoverClear ? Color.FromArgb(90, Ui.Muted) : Color.FromArgb(54, Ui.Muted))) g.FillEllipse(br, c);
            using (var p = new Pen(hoverClear ? Ui.Text : Ui.Muted, Ui.S(1.5F)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                var k = Ui.S(3.6F); float ccx = c.X + c.Width / 2F, ccy = c.Y + c.Height / 2F;
                g.DrawLine(p, ccx - k, ccy - k, ccx + k, ccy + k); g.DrawLine(p, ccx - k, ccy + k, ccx + k, ccy - k);
            }
        }
        else if (hotkey.Length > 0)
        {
            var label = hotkey.Replace("+", " ");
            var sz = GText.Measure(label, Ui.KeyFontLarge); var w = sz.Width + Ui.S(18);
            var kr = new RectangleF(b.Right - Ui.S(14) - w, b.Y + (b.Height - Ui.S(24)) / 2F, w, Ui.S(24));
            Glass.Paint(g, kr, kr.Height / 2F, GlassKind.Secondary, 1.6F);
            GText.Draw(g, label, Ui.KeyFontLarge, Rectangle.Round(kr), Ui.Muted, true);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = Input.TextLength > 0 && ClearRect.Contains(e.Location);
        if (h != hoverClear) { hoverClear = h; Invalidate(); }
        Cursor = h ? Cursors.Hand : Cursors.IBeam;
        if (dragging)
        {
            var i = IndexAt(e.X);
            Input.Select(Math.Min(anchor, i), Math.Abs(i - anchor)); ResetCaret(); Invalidate();
        }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { hoverSp.Target = 0; hoverClear = false; anim.Kick(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Input.Focus();
        if (Input.TextLength > 0 && ClearRect.Contains(e.Location)) Input.Clear();
        else if (e.Button == MouseButtons.Left && TextArea.Contains(e.Location))
        {
            anchor = IndexAt(e.X); Input.Select(anchor, 0); dragging = true; Capture = true; ResetCaret(); Invalidate();
        }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Capture = false; base.OnMouseUp(e); }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { if (TextArea.Contains(e.Location)) { Input.SelectAll(); anchor = 0; Invalidate(); } base.OnMouseDoubleClick(e); }
}

// ---------------------------------------------------------------------
// Capsule segmented control: the selected glass thumb glides between segments on a spring.
// ---------------------------------------------------------------------
internal sealed class Segmented : Control
{
    string[] items = new string[0]; int selected;
    readonly Spring thumbX = new Spring(0, 520, 34), thumbW = new Spring(0, 520, 34), squish = new Spring(0, 600, 32);
    readonly Animator anim = new Animator();
    int hoverIdx = -1;
    public event Action<int> Changed;

    public Segmented()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true); SetStyle(ControlStyles.Selectable, false); Cursor = Cursors.Hand; TabStop = false;
        anim.Tick = dt => { var a = thumbX.Step(dt); var b = thumbW.Step(dt); var c = squish.Step(dt); Invalidate(); return a || b || c; };
    }

    protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

    public string[] Items { get { return items; } set { items = value ?? new string[0]; if (selected >= items.Length) selected = 0; SnapThumb(); Invalidate(); } }
    public int Selected
    {
        get { return selected; }
        set { if (value < 0 || value >= items.Length || value == selected) return; selected = value; MoveThumb(true); if (Changed != null) Changed(selected); }
    }

    Rectangle Track { get { return new Rectangle(0, 0, Width, Height); } }
    float SegW { get { return items.Length == 0 ? 0 : (Width - 2F * Pad) / items.Length; } }
    float Pad { get { return Ui.S(3); } }

    void SnapThumb() { thumbX.Snap(Pad + selected * SegW); thumbW.Snap(SegW); }
    void MoveThumb(bool animate) { thumbX.Target = Pad + selected * SegW; thumbW.Target = SegW; if (!animate) SnapThumb(); anim.Kick(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); SnapThumb(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; GlassPaint.Begin(g, this);
        var track = new RectangleF(0, 0, Width, Height);
        Glass.Paint(g, track, Height / 2F, GlassKind.Secondary, 1F);
        if (items.Length == 0) return;
        var s = 1F - 0.035F * Ease.Clamp01(squish.Value);
        var tw = thumbW.Value * s; var th = (Height - 2 * Pad) * s;
        var thumb = new RectangleF(thumbX.Value + (thumbW.Value - tw) / 2F, (Height - th) / 2F, tw, th);
        Glass.Paint(g, thumb, th / 2F, GlassKind.Selected, 1F, 0.3F);
        for (var i = 0; i < items.Length; i++)
        {
            var seg = new Rectangle((int)(Pad + i * SegW), 0, (int)SegW, Height);
            // label brightness follows how much of the thumb covers the segment
            var cover = 1F - Math.Min(1F, Math.Abs((thumb.X + thumb.Width / 2F) - (seg.X + seg.Width / 2F)) / Math.Max(1F, SegW));
            var col = Ui.Blend(i == hoverIdx ? Ui.Text : Ui.Muted, Ui.Text, Ease.Clamp01(cover));
            GText.Draw(g, items[i], i == selected ? Ui.SegSelFont : Ui.SegFont, seg, col, true);
        }
    }

    int Hit(Point p) { if (items.Length == 0 || Width <= 0) return -1; var i = (int)((p.X - Pad) / SegW); return i < 0 || i >= items.Length ? -1 : i; }
    protected override void OnMouseMove(MouseEventArgs e) { var i = Hit(e.Location); if (i != hoverIdx) { hoverIdx = i; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { hoverIdx = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { squish.Target = 1; anim.Kick(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        squish.Target = 0; anim.Kick();
        var i = Hit(e.Location); if (e.Button == MouseButtons.Left && i >= 0) Selected = i;
        base.OnMouseUp(e);
    }
}

// ---------------------------------------------------------------------
// Dynamic Connection Island: a glass capsule that morphs between states.
// ---------------------------------------------------------------------
internal enum IslandState { Disconnected, Connecting, Authenticating, Connected, Disconnecting, Error }

internal sealed class ConnectionIsland : Control
{
    readonly Spring width = new Spring(0, 330, 27), content = new Spring(1, 500, 34), pulse = new Spring(0, 300, 30), press = new Spring(0, 600, 32);
    readonly Animator anim = new Animator();
    IslandState state = IslandState.Disconnected, shown = IslandState.Disconnected;
    string title = "Bağlı değil", detail = ""; DateTime since = DateTime.MinValue;
    bool hotDisconnect, ambientOn;
    readonly System.Windows.Forms.Timer clockTick = new System.Windows.Forms.Timer { Interval = 1000 };
    public event Action DisconnectClicked;
    public Func<bool> AllowAmbient = delegate { return true; };

    public ConnectionIsland()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true); SetStyle(ControlStyles.Selectable, false); TabStop = false;
        anim.Tick = dt => { var a = width.Step(dt); var b = content.Step(dt); var c = press.Step(dt); var live = ambientOn && AllowAmbient(); Invalidate(); anim.Interval = (a || b || c) ? 15 : 80; return a || b || c || live; };
        clockTick.Tick += delegate { if (state == IslandState.Connected && Visible && AllowAmbient()) Invalidate(); };
        SetState(IslandState.Disconnected, "Bağlı değil", "", DateTime.MinValue);
    }

    protected override void Dispose(bool disposing) { if (disposing) { anim.Dispose(); clockTick.Dispose(); } base.Dispose(disposing); }

    public IslandState State { get { return state; } }

    float CompactWidth { get { return Ui.S(168); } }
    float Target(IslandState s) { return s == IslandState.Connected ? Math.Max(CompactWidth, Width - Ui.S(2)) : s == IslandState.Error ? Math.Min(Width - Ui.S(2), Ui.S(232)) : s == IslandState.Disconnected ? CompactWidth : Math.Min(Width - Ui.S(2), Ui.S(208)); }

    public void SetState(IslandState s, string text, string sub, DateTime sinceUtc)
    {
        var changed = s != state || text != title || sub != detail;
        var textChanged = s != state || text != title;
        state = s; title = text ?? ""; detail = sub ?? ""; since = sinceUtc;
        width.Target = Target(s);
        if (width.Value <= 0) width.Snap(width.Target);
        if (textChanged) { content.Value = 0; content.Velocity = 0; content.Target = 1; }
        shown = s; ambientOn = s == IslandState.Connecting || s == IslandState.Authenticating || s == IslandState.Disconnecting || s == IslandState.Connected;
        if (s == IslandState.Connected) clockTick.Start(); else clockTick.Stop();
        if (s == IslandState.Connected || s == IslandState.Error) pulse.Value = 1F; pulse.Target = 0;
        if (changed) { anim.Kick(); Invalidate(); }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); width.Target = Target(state); if (width.Value <= 0) width.Snap(width.Target); anim.Kick(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) anim.Kick(); }

    Color Tone
    {
        get
        {
            switch (state)
            {
                case IslandState.Connected: return Ui.Success;
                case IslandState.Error: return Ui.Danger;
                case IslandState.Connecting: case IslandState.Authenticating: return Ui.Accent;
                case IslandState.Disconnecting: return Ui.Warning;
                default: return Ui.Faint;
            }
        }
    }

    RectangleF Capsule { get { var w = Math.Min(Width - 1F, Math.Max(Ui.S(60), width.Value)); var h = Height - Ui.S(2); return new RectangleF((Width - w) / 2F, (Height - h) / 2F, w, h); } }
    RectangleF StopRect { get { var c = Capsule; var d = c.Height - Ui.S(10); return new RectangleF(c.Right - Ui.S(5) - d, c.Y + Ui.S(5), d, d); } }

    protected override void WndProc(ref Message m)
    {
        // Everything except the disconnect button is the window's drag handle.
        if (m.Msg == 0x0084)
        {
            var v = m.LParam.ToInt64(); var p = PointToClient(new Point((short)(v & 0xffff), (short)((v >> 16) & 0xffff)));
            if (!(state == IslandState.Connected && StopRect.Contains(p))) { m.Result = (IntPtr)(-1); return; }
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; GlassPaint.Begin(g, this);
        var c = Capsule; var r = c.Height / 2F; var tone = Tone; var now = anim.Time;
        var squash = 1F - 0.02F * press.Value; var cc = new RectangleF(c.X + c.Width * (1 - squash) / 2F, c.Y, c.Width * squash, c.Height);
        Glass.Paint(g, cc, r, GlassKind.Floating, 1F, 0.5F);
        if (state != IslandState.Disconnected)
        {
            // soft tinted glow inside the capsule, in the state's color
            var ga = (int)((state == IslandState.Connected ? 30 : 22) + 10 * pulse.Value);
            using (var path = Ui.Round(cc, r)) { var st = g.Save(); g.SetClip(path, CombineMode.Intersect);
                using (var b = new LinearGradientBrush(cc, Color.FromArgb(ga, tone), Color.FromArgb(0, tone), LinearGradientMode.Horizontal)) g.FillRectangle(b, cc);
                g.Restore(st); }
        }

        var ca = Ease.Clamp01(content.Value);
        var pad = Ui.S(14); var cy = cc.Y + cc.Height / 2F;
        // status marker: an icon whose SHAPE says the state (check, spinner, lock, alert, slash)
        var md = Ui.S(16); var mx = cc.X + pad;
        var mr = new RectangleF(mx, cy - md / 2F, md, md);
        var stateIcon = state == IslandState.Connected ? "state-connected" : state == IslandState.Error ? "state-error" : state == IslandState.Authenticating ? "state-authenticating" : state == IslandState.Disconnecting ? "state-disconnecting" : state == IslandState.Connecting ? "state-connecting" : "state-disconnected";
        if (state == IslandState.Connecting || state == IslandState.Disconnecting)
        {
            var st = g.Save(); g.TranslateTransform(mr.X + md / 2F, mr.Y + md / 2F); g.RotateTransform(now * 360F % 360F); g.TranslateTransform(-md / 2F, -md / 2F);
            Assets.Draw(g, "state-connecting", new RectangleF(0, 0, md, md), GlassPaint.A(tone, 0.3F + 0.7F * ca), 1.15F); g.Restore(st);
        }
        else
        {
            if (state == IslandState.Connected && !Motion.Reduced)
            {
                var ph = Ease.Frac(now / 2.2F); var grow = Ui.S(14) * Ease.OutCubic(ph);
                using (var p = new Pen(Color.FromArgb((int)(80 * (1 - ph)), tone), 1F)) g.DrawEllipse(p, mr.X - grow / 2F, mr.Y - grow / 2F, md + grow, md + grow);
            }
            Assets.Draw(g, stateIcon, mr, GlassPaint.A(tone, 0.4F + 0.6F * ca), 1.15F);
        }

        var tx = (int)(mr.Right + Ui.S(10));
        var stopW = state == IslandState.Connected ? (int)(StopRect.Width + Ui.S(10)) : 0;
        var rightLimit = (int)(cc.Right - pad) - stopW;
        if (state == IslandState.Connected)
        {
            var timer = Elapsed();
            var tw = GText.Measure(timer, Ui.IslandMono).Width;
            GText.Draw(g, timer, Ui.IslandMono, new Rectangle(rightLimit - tw, (int)cc.Y, tw, (int)cc.Height), GlassPaint.A(Ui.Muted, ca), false, true);
            var nameW = rightLimit - tw - Ui.S(10) - tx;
            GText.Draw(g, title, Ui.IslandName, new Rectangle(tx, (int)cc.Y, Math.Max(10, nameW), (int)cc.Height), GlassPaint.A(Ui.Text, ca));
            // disconnect action
            var sr = StopRect;
            Glass.Paint(g, sr, sr.Width / 2F, hotDisconnect ? GlassKind.Selected : GlassKind.Secondary, hotDisconnect ? 1F : 1.6F);
            if (hotDisconnect) using (var b = new SolidBrush(Color.FromArgb(120, Ui.Danger))) g.FillEllipse(b, sr);
            Assets.Draw(g, "action-disconnect", RectangleF.Inflate(sr, -Ui.S(6.5F), -Ui.S(6.5F)), hotDisconnect ? Ui.Text : Ui.Danger);
        }
        else
        {
            var area = new Rectangle(tx, (int)cc.Y, Math.Max(10, (int)(cc.Right - pad) - tx), (int)cc.Height);
            var label = title;
            GText.Draw(g, label, Ui.IslandName, area, GlassPaint.A(state == IslandState.Disconnected ? Ui.Muted : Ui.Text, ca));
        }
    }

    string Elapsed()
    {
        if (since == DateTime.MinValue || since > DateTime.UtcNow) return "00:00:00";
        var d = DateTime.UtcNow - since;
        return string.Format("{0:00}:{1:00}:{2:00}", (int)d.TotalHours, d.Minutes, d.Seconds);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = state == IslandState.Connected && StopRect.Contains(e.Location);
        if (h != hotDisconnect) { hotDisconnect = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { if (hotDisconnect) { hotDisconnect = false; Invalidate(); } base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (hotDisconnect) { press.Target = 1; anim.Kick(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        press.Target = 0; anim.Kick();
        if (e.Button == MouseButtons.Left && state == IslandState.Connected && StopRect.Contains(e.Location) && DisconnectClicked != null) DisconnectClicked();
        base.OnMouseUp(e);
    }
}

// ---------------------------------------------------------------------
// Provider icons (small, monochrome)
// ---------------------------------------------------------------------
internal static class ProviderIcon
{
    public static void Draw(Graphics g, bool forti, RectangleF r, Color c) { Assets.Draw(g, forti ? "provider-forti" : "provider-openvpn", r, c); }
}
