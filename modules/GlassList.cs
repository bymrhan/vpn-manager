using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Windows.Forms;

// =====================================================================
// Compact profile list. Rows are plain until hovered or selected; then a glass plate appears.
// One selection plate and one hover plate glide between rows on springs (they are not per-row
// backgrounds), so moving the selection feels like a single piece of glass sliding along.
// Performance: one timer that runs only while something moves; idle cost is zero.
// =====================================================================
internal sealed class ProfileList : Control
{
    const float RowH = 58F, RowGap = 4F, GroupGap = 8F, HeaderH = 30F;
    const float SuccessDuration = 1.2F, FailDuration = 0.6F, AppearDuration = 0.36F;

    List<ListRow> rows = new List<ListRow>();
    readonly Dictionary<string, int> indexById = new Dictionary<string, int>();
    readonly Dictionary<string, float> appearAt = new Dictionary<string, float>();
    string[] terms = new string[0];
    string emptyTitle = "", emptyHint = "";
    int contentHeight, hoverIndex = -1, selectedIndex = -1, downIndex = -1, dragStartY;
    float dragStartScroll, lastAmbient, barAlpha, mouseX = -1;
    bool hoverStar, hoverPlug, downOnStar, downOnPlug, dragThumb, hoverBar, hoverReach;
    string connectingId, successId, failId, disconnectingId;
    float connectT0, successT0, failT0;

    readonly Spring scroll = new Spring(0, 260, 32), selY = new Spring(0, 520, 36), selH = new Spring(0, 520, 36), selA = new Spring(0, 400, 34);
    readonly Spring hovY = new Spring(0, 620, 40), hovH = new Spring(0, 620, 40), hovA = new Spring(0, 420, 36), plugPress = new Spring(0, 700, 30);
    readonly Animator anim = new Animator();
    readonly Dictionary<string, Spring> flip = new Dictionary<string, Spring>();   // FLIP: rows glide from their old position
    readonly Dictionary<string, float> pinPop = new Dictionary<string, float>();
    // rows that left the list (deleted / gone after a reload): fade and slide out where they were
    sealed class Ghost { public VpnProfile Profile; public int Y, Height; public float T0; }
    readonly List<Ghost> ghosts = new List<Ghost>();
    const float LeaveDuration = 0.28F;
    readonly ToolTip tip = new ToolTip();
    string tipText = "";

    public bool AnimateNext;   // set before a filter change to play the entrance animation
    public Func<bool> AllowAmbient = delegate { return true; };
    public void Wake() { anim.Kick(); }
    public Func<VpnProfile, bool> IsPinned = delegate { return false; };
    public Func<VpnProfile, ReachState> ReachOf = delegate { return ReachState.Unknown; };
    public Func<VpnProfile, string> LiveText = delegate { return null; };
    public event Action<VpnProfile> Activated, PinToggled, DisconnectRequested;
    public event Action<string> ConnectFailedId;
    public event Action<VpnProfile, Point> MenuRequested;
    // Buttons under the empty-state text (e.g. "Profil klasörünü seç…"); set before SetRows.
    public string[] EmptyActions = new string[0];
    public event Action<int> EmptyActionClicked;
    Rectangle[] emptyActionRects = new Rectangle[0];
    int hoverAction = -1;

    public ProfileList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        anim.Tick = OnFrame;
        // tooltip in the same material
        tip.OwnerDraw = true;
        tip.Popup += delegate(object o, PopupEventArgs e) { var sz = GText.Measure(tip.GetToolTip(e.AssociatedControl), Ui.SmallFont); e.ToolTipSize = new Size(sz.Width + Ui.S(22), sz.Height + Ui.S(14)); };
        tip.Draw += delegate(object o, DrawToolTipEventArgs e)
        {
            var g = e.Graphics; g.Clear(Ui.WindowBg); GlassPaint.Style2(g);
            var r = new RectangleF(0.5F, 0.5F, e.Bounds.Width - 1.5F, e.Bounds.Height - 1.5F);
            Ui.FillRound(g, Ui.GlassPrimary, r, Ui.S(9)); Glass.Paint(g, r, Ui.S(9), GlassKind.Primary, 1F);
            GText.Draw(g, e.ToolTipText, Ui.SmallFont, Rectangle.Round(r), Ui.Text, true);
        };
    }

    static int S(float v) { return Ui.S(v); }
    int MaxScroll { get { return Math.Max(0, contentHeight - Height); } }
    float Scroll { get { return scroll.Value; } }

    protected override void Dispose(bool disposing) { if (disposing) { anim.Dispose(); tip.Dispose(); } base.Dispose(disposing); }

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

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) anim.Kick(); else anim.Stop(); }

    // ---------- public animation API ----------

    public void PulsePin(string id) { pinPop[id] = anim.Time; anim.Kick(); }

    public void BeginConnect(string id) { connectingId = id; connectT0 = anim.Time; failId = null; successId = null; anim.Kick(); }
    public void ConnectSucceeded(string id) { if (connectingId == id) connectingId = null; successId = id; successT0 = anim.Time; anim.Kick(); }
    public void ConnectFailed(string id) { if (connectingId == id) connectingId = null; failId = id; failT0 = anim.Time; anim.Kick(); if (ConnectFailedId != null) ConnectFailedId(id); }
    public void CancelConnect(string id) { if (connectingId == id) connectingId = null; anim.Kick(); Invalidate(); }
    public void BeginDisconnect(string id) { disconnectingId = id; anim.Kick(); }
    public void EndDisconnect() { disconnectingId = null; anim.Kick(); Invalidate(); }
    public bool IsConnecting { get { return connectingId != null; } }
    public bool IsDisconnecting { get { return disconnectingId != null; } }
    public string ConnectingId { get { return connectingId; } }
    public string DisconnectingId { get { return disconnectingId; } }

    public VpnProfile SelectedProfile { get { return selectedIndex >= 0 && selectedIndex < rows.Count ? rows[selectedIndex].Profile : null; } }

    public void SetRows(List<ListRow> newRows, string[] newTerms, string title, string hint, string keepId)
    {
        var oldTops = new Dictionary<string, int>();
        foreach (var o in rows) if (!o.IsHeader) oldTops[o.Profile.Id] = o.Top;
        var oldRows = rows; var oldScroll = (int)Math.Round(Scroll);
        rows = newRows; terms = newTerms; emptyTitle = title; emptyHint = hint;
        indexById.Clear();
        var y = S(2);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.IsHeader) { r.Top = y + (y > S(2) ? S(GroupGap) : 0); r.Height = S(HeaderH); y = r.Top + r.Height; }
            else { r.Top = y; r.Height = S(RowH); y += r.Height + S(RowGap); indexById[r.Profile.Id] = i; }
        }
        contentHeight = y + S(10);
        if (!AnimateNext && !Motion.Reduced && oldTops.Count > 0)
            foreach (var r in rows)
            {
                int ot; if (r.IsHeader || !oldTops.TryGetValue(r.Profile.Id, out ot) || ot == r.Top) continue;
                var sp = new Spring(ot - r.Top, 330, 26); sp.Target = 0; flip[r.Profile.Id] = sp;
            }
        else flip.Clear();
        if (!AnimateNext && !Motion.Reduced)
        {
            foreach (var o in oldRows)
                if (!o.IsHeader && !indexById.ContainsKey(o.Profile.Id) && o.Top - oldScroll < Height && o.Top + o.Height - oldScroll > 0)
                    ghosts.Add(new Ghost { Profile = o.Profile, Y = o.Top - oldScroll, Height = o.Height, T0 = anim.Time });
        }
        else ghosts.Clear();
        hoverIndex = -1; selectedIndex = -1;
        if (keepId != null) selectedIndex = rows.FindIndex(r => !r.IsHeader && r.Profile.Id == keepId);
        var keep = selectedIndex >= 0;
        if (!keep) { selectedIndex = rows.FindIndex(r => !r.IsHeader); scroll.Snap(0); }
        else { scroll.Target = Math.Min(scroll.Target, MaxScroll); scroll.Snap(scroll.Target); EnsureVisible(selectedIndex, false); }

        // the selection plate glides to the new row when the row list changes, instead of jumping
        if (selectedIndex >= 0)
        {
            var sr = rows[selectedIndex];
            if (selA.Value < 0.05F || !keep) { selY.Snap(sr.Top); selH.Snap(sr.Height); }
            selY.Target = sr.Top; selH.Target = sr.Height; selA.Target = 1;
        }
        else selA.Target = 0;
        hovA.Target = 0;

        var dead = appearAt.Keys.Where(id => !indexById.ContainsKey(id)).ToList();
        foreach (var id in dead) appearAt.Remove(id);
        if (AnimateNext && !Motion.Reduced)
        {
            AnimateNext = false;
            var t = anim.Time; var k = 0;
            foreach (var r in rows)
            {
                if (r.IsHeader) continue;
                if (r.Top - Scroll > Height) break;
                appearAt[r.Profile.Id] = t + Math.Min(k++, 9) * 0.03F;
            }
        }
        AnimateNext = false;
        anim.Kick(); Invalidate();
    }

    public void MoveSelection(int delta)
    {
        if (rows.Count == 0) return;
        var profileRows = new List<int>();
        for (var i = 0; i < rows.Count; i++) if (!rows[i].IsHeader) profileRows.Add(i);
        if (profileRows.Count == 0) return;
        var pos = profileRows.IndexOf(selectedIndex);
        pos = pos < 0 ? 0 : Math.Max(0, Math.Min(profileRows.Count - 1, pos + delta));
        SelectRow(profileRows[pos]);
    }

    void SelectRow(int index)
    {
        selectedIndex = index;
        var r = rows[index]; selY.Target = r.Top; selH.Target = r.Height; selA.Target = 1;
        EnsureVisible(index, true); anim.Kick();
    }

    void EnsureVisible(int index, bool animate)
    {
        if (index < 0) return;
        var r = rows[index];
        var top = r.Top - S(6);
        if (index > 0 && rows[index - 1].IsHeader) top = rows[index - 1].Top - S(2);
        var bottom = r.Top + r.Height + S(10);
        var t = (int)scroll.Target;
        if (top < t) t = top; else if (bottom > t + Height) t = bottom - Height;
        ScrollTo(t, animate);
    }

    void ScrollTo(int t, bool animate)
    {
        scroll.Target = Math.Max(0, Math.Min(MaxScroll, t));
        if (!animate) { scroll.Snap(scroll.Target); Invalidate(); } else { barAlpha = 1; anim.Kick(); }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); scroll.Target = Math.Min(scroll.Target, MaxScroll); scroll.Value = Math.Min(scroll.Value, MaxScroll); }

    // ---------- animation loop ----------

    bool OnFrame(float dt)
    {
        if (!Visible) return false;
        var now = anim.Time; var moving = false;
        if (scroll.Step(dt)) moving = true;
        if (selA.Step(dt) | selY.Step(dt) | selH.Step(dt)) moving = true;
        if (hovA.Step(dt) | hovY.Step(dt) | hovH.Step(dt)) moving = true;
        if (plugPress.Step(dt)) moving = true;
        foreach (var kv in flip.ToList()) { if (kv.Value.Step(dt)) moving = true; else flip.Remove(kv.Key); }
        foreach (var kv in pinPop.ToList()) { if (now - kv.Value > 0.5F) pinPop.Remove(kv.Key); moving = true; }
        if (ghosts.Count > 0) { ghosts.RemoveAll(gh => now - gh.T0 > LeaveDuration); moving = true; }
        if (barAlpha > 0 && !dragThumb && !hoverBar && scroll.Settled) { barAlpha = Math.Max(0, barAlpha - dt * 1.4F); moving = true; }

        foreach (var kv in appearAt.ToList()) { if (now - kv.Value > AppearDuration + 0.05F) appearAt.Remove(kv.Key); moving = true; }
        if (connectingId != null || disconnectingId != null) moving = true;
        if (successId != null) { if (now - successT0 > SuccessDuration) successId = null; moving = true; }
        if (failId != null) { if (now - failT0 > FailDuration) failId = null; moving = true; }

        // breathing status dot of the connected row: ~15 fps is plenty
        var live = false; var liveRect = Rectangle.Empty;
        if (AllowAmbient()) foreach (var r in rows) if (!r.IsHeader && LiveText(r.Profile) != null) { live = true; liveRect = Rectangle.Inflate(RowRect(r), S(10), S(10)); break; }
        if (moving) { Invalidate(); anim.Interval = 15; return true; }
        // ambient only repaints the connected row itself, ~12 fps
        if (live) { anim.Interval = 80; if (now - lastAmbient > 0.07F) { lastAmbient = now; Invalidate(liveRect); } return true; }
        return false;
    }

    // ---------- Geometry ----------

    int RowX { get { return S(10); } }
    int RowW { get { return Width - S(10) - S(14); } }
    Rectangle RowRect(ListRow r) { return new Rectangle(RowX, r.Top - (int)Math.Round(Scroll), RowW, r.Height); }
    Rectangle PlugRect(Rectangle rr) { var d = S(34); return new Rectangle(rr.Right - S(12) - d, rr.Y + (rr.Height - d) / 2, d, d); }
    Rectangle StarRect(Rectangle rr) { var p = PlugRect(rr); var d = S(24); return new Rectangle(p.X - S(4) - d, rr.Y + (rr.Height - d) / 2, d, d); }

    Rectangle ThumbRect()
    {
        if (contentHeight <= Height || Height <= 0) return Rectangle.Empty;
        var h = Math.Max(S(36), (int)(Height * (Height / (float)contentHeight)));
        var y = MaxScroll == 0 ? 0 : (int)((Height - h) * (Scroll / MaxScroll));
        var w = hoverBar || dragThumb ? S(5) : S(3);
        return new Rectangle(Width - S(7) - w / 2, y + S(2), w, h - S(4));
    }

    int HitRow(Point p)
    {
        var y = p.Y + (int)Math.Round(Scroll);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!r.IsHeader && y >= r.Top && y < r.Top + r.Height && p.X >= RowX && p.X < RowX + RowW) return i;
        }
        return -1;
    }

    Rectangle ReachRect(Rectangle rr, Rectangle textArea) { return new Rectangle(textArea.X - S(1), rr.Y + S(31), S(14), S(18)); }

    // ---------- Mouse ----------

    void SetHover(int i)
    {
        if (i == hoverIndex) return;
        hoverIndex = i;
        if (i >= 0) { var r = rows[i]; if (hovA.Value < 0.05F) { hovY.Snap(r.Top); hovH.Snap(r.Height); } hovY.Target = r.Top; hovH.Target = r.Height; hovA.Target = 1; }
        else hovA.Target = 0;
        anim.Kick();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        mouseX = e.X;
        if (rows.Count == 0)
        {
            var a = EmptyActionAt(e.Location);
            if (a != hoverAction) { hoverAction = a; Invalidate(); }
            Cursor = a >= 0 ? Cursors.Hand : Cursors.Default;
            return;
        }
        if (dragThumb)
        {
            var th = ThumbRect().Height; var range = Height - th;
            if (range > 0) { scroll.Snap(Math.Max(0, Math.Min(MaxScroll, dragStartScroll + (e.Y - dragStartY) * (MaxScroll / (float)range)))); Invalidate(); }
            return;
        }
        var bar = contentHeight > Height && e.X >= Width - S(16);
        var i = bar ? -1 : HitRow(e.Location);
        bool star = false, plug = false, reach = false;
        if (i >= 0)
        {
            var rr = RowRect(rows[i]);
            star = StarRect(rr).Contains(e.Location); plug = PlugRect(rr).Contains(e.Location);
            var textX = rr.X + S(12) + S(36) + S(14);
            reach = ReachOf(rows[i].Profile) != ReachState.Unknown && ReachRect(rr, new Rectangle(textX, rr.Y, 10, rr.Height)).Contains(e.Location);
        }
        if (star != hoverStar || plug != hoverPlug || reach != hoverReach) { hoverStar = star; hoverPlug = plug; hoverReach = reach; Invalidate(); }
        SetHover(i);
        if (reach && i >= 0)
        {
            var st = ReachOf(rows[i].Profile);
            var text = st == ReachState.Up ? "Sunucu erişilebilir" : st == ReachState.Down ? "Sunucuya ulaşılamıyor" : "Sunucu test ediliyor…";
            if (text != tipText) { tipText = text; tip.Show(text, this, e.X + S(12), e.Y + S(18), 1800); }
        }
        else if (tipText.Length > 0) { tipText = ""; tip.Hide(this); }
        if (bar != hoverBar) { hoverBar = bar; barAlpha = bar ? 1 : barAlpha; anim.Kick(); }
        if (!dragThumb && Math.Abs(hovA.Value) > 0.01F) Invalidate(); // sheen follows the pointer
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (dragThumb) return;
        SetHover(-1); hoverStar = hoverPlug = hoverBar = hoverReach = false; mouseX = -1; tipText = ""; tip.Hide(this);
        anim.Kick(); Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var thumb = ThumbRect();
        if (e.Button == MouseButtons.Left && contentHeight > Height && e.X >= Width - S(16))
        {
            if (!thumb.Contains(e.Location)) { ScrollTo((int)(e.Y < thumb.Y ? scroll.Target - Height : scroll.Target + Height), true); return; }
            dragThumb = true; dragStartY = e.Y; dragStartScroll = Scroll; Capture = true; return;
        }
        downIndex = HitRow(e.Location);
        downOnStar = downIndex >= 0 && StarRect(RowRect(rows[downIndex])).Contains(e.Location);
        downOnPlug = downIndex >= 0 && PlugRect(RowRect(rows[downIndex])).Contains(e.Location);
        if (downOnPlug) { plugPress.Target = 1; anim.Kick(); }
        if (downIndex >= 0 && e.Button == MouseButtons.Right)
        {
            SelectRow(downIndex);
            if (MenuRequested != null) MenuRequested(rows[downIndex].Profile, e.Location);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        plugPress.Target = 0; anim.Kick();
        if (dragThumb) { dragThumb = false; Capture = false; Invalidate(); return; }
        if (e.Button != MouseButtons.Left) return;
        if (rows.Count == 0)
        {
            var a = EmptyActionAt(e.Location);
            if (a >= 0 && EmptyActionClicked != null) EmptyActionClicked(a);
            return;
        }
        var i = HitRow(e.Location);
        if (i < 0 || i != downIndex) return;
        var p = rows[i].Profile; var rr = RowRect(rows[i]);
        if (downOnStar && StarRect(rr).Contains(e.Location)) { if (PinToggled != null) PinToggled(p); return; }
        SelectRow(i);
        if (downOnPlug && PlugRect(rr).Contains(e.Location) && LiveText(p) != null && p.Id != disconnectingId) { if (DisconnectRequested != null) DisconnectRequested(p); return; }
        if (Activated != null) Activated(p);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollTo((int)(scroll.Target - e.Delta * S(64) / 120F), true);
    }

    // ---------- Paint ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; GlassPaint.Begin(g, this);
        var clip = e.ClipRectangle;
        if (rows.Count == 0) { PaintEmpty(g); return; }
        var now = anim.Time; var sc = (int)Math.Round(Scroll);

        // glass plates first (under the row content)
        var sheen = mouseX < 0 ? 0.3F : Ease.Clamp01(mouseX / Math.Max(1F, Width));
        if (hovA.Value > 0.01F && hoverIndex != selectedIndex)
            Glass.Paint(g, new RectangleF(RowX, hovY.Value - sc, RowW, hovH.Value), S(12), GlassKind.Secondary, Ease.Clamp01(hovA.Value));
        if (selA.Value > 0.01F && selectedIndex >= 0)
{
                var sel = new RectangleF(RowX, selY.Value - sc, RowW, selH.Value);
                Glass.Paint(g, sel, S(12), GlassKind.Selected, Ease.Clamp01(selA.Value), hoverIndex == selectedIndex ? sheen : 0.28F);
                using (var pen = new Pen(Color.FromArgb((int)(140 * Ease.Clamp01(selA.Value)), Ui.Highlight), 1F)) g.DrawPath(pen, Ui.Round(new RectangleF(sel.X + 0.5F, sel.Y + 0.5F, sel.Width - 1, sel.Height - 1), S(12)));
            }
        if (hovA.Value > 0.01F && hoverIndex == selectedIndex) { /* selected plate already shows the pointer sheen */ }

        foreach (var gh in ghosts)
        {
            var k = Ease.OutCubic((now - gh.T0) / LeaveDuration);
            var gr = new Rectangle(RowX - (int)(S(28) * k), gh.Y, RowW, gh.Height);
            try { PaintRow(g, gh.Profile, gr, false, false, now, 1 - k); } catch (ArgumentException) { }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var rr = RowRect(r);
            if (rr.Bottom < 0 || rr.Top > Height) continue;
            if (!clip.IntersectsWith(Rectangle.Inflate(rr, S(30), S(30)))) continue;
            if (r.IsHeader) { PaintHeader(g, r, rr); continue; }
            float appear = 1, t0;
            if (appearAt.TryGetValue(r.Profile.Id, out t0)) appear = Ease.OutCubic((now - t0) / AppearDuration);
            if (appear <= 0) continue;
            if (appear < 1) rr.Offset(0, (int)(S(12) * (1 - appear)));
            Spring fl; if (flip.TryGetValue(r.Profile.Id, out fl)) rr.Offset(0, (int)Math.Round(fl.Value));
            try { PaintRow(g, r.Profile, rr, i == selectedIndex, i == hoverIndex, now, appear); } catch (ArgumentException) { } // a bad value in one frame must never break the whole list
        }

        var thumb = ThumbRect();
        if (!thumb.IsEmpty && barAlpha > 0.02F)
            using (var b = new SolidBrush(GlassPaint.A(Color.FromArgb(Themes.IsLight ? 110 : 150, Ui.Text), Ease.Clamp01(barAlpha) * (hoverBar || dragThumb ? 1F : 0.7F)))) using (var path = Ui.Round(thumb, thumb.Width / 2F)) g.FillPath(b, path);
    }

    void PaintHeader(Graphics g, ListRow r, Rectangle rr)
    {
        var x = rr.X + S(10); var cy = rr.Bottom - S(12);
        GText.Draw(g, r.Header, Ui.SectionFont, new Point(x, cy - GText.Measure(r.Header, Ui.SectionFont).Height / 2), Ui.Muted);
    }

    static Color Fade(Color c, float a) { return a >= 1 ? c : Color.FromArgb((int)(c.A * Math.Max(0, a)), c); }

    // Row = a slim glass card: avatar tile (initials, deterministic muted hue) with a small provider badge,
    // name + secondary line, status text with an icon, pin star and a sapphire glass action button.
    void PaintRow(Graphics g, VpnProfile p, Rectangle rr, bool selected, bool hover, float now, float appear)
    {
        var id = p.Id;
        var pinned = IsPinned(p);
        var connecting = id == connectingId; var disconnecting = id == disconnectingId;
        var success = id == successId ? (now - successT0) : -1F; var fail = id == failId ? (now - failT0) : -1F;
        var live = LiveText(p);
        var providerIcon = VpnProviders.Asset(VpnProviders.KindOf(p));
        if (fail >= 0) rr.Offset((int)(Math.Sin(fail * 44) * S(6) * Math.Max(0F, 1 - fail / FailDuration)), 0);
        var active = selected || hover;
        var radius = S(12);

        // idle cards are always visible as quiet glass; hover and selection plates are painted by OnPaint
        if (!active) Glass.Paint(g, rr, radius, GlassKind.Secondary, 0.8F * appear);
        if (success >= 0)
        {
            var k = Math.Max(0F, 1 - success / SuccessDuration);
            using (var path = Ui.Round(rr, radius)) using (var b = new SolidBrush(Color.FromArgb((int)(50 * k), Ui.Success))) g.FillPath(b, path);
            Ui.DrawRound(g, Color.FromArgb((int)(190 * k), Ui.Success), rr, radius, 1F);
        }
        if (fail >= 0) Ui.DrawRound(g, Color.FromArgb((int)(210 * Math.Max(0F, 1 - fail / FailDuration)), Ui.Danger), rr, radius, 1F);

        // avatar tile
        var d = S(40); var av = new RectangleF(rr.X + S(10), rr.Y + (rr.Height - d) / 2F, d, d);
        var hue = Ui.AvatarColor(p.Name);
        var letters = !string.IsNullOrEmpty(p.Initials) && char.IsLetter(p.Initials[0]) && !string.IsNullOrEmpty(p.Name) && char.IsLetter(p.Name.TrimStart()[0]);
        using (var path = Ui.Round(av, S(12)))
        using (var b = new LinearGradientBrush(RectangleF.Inflate(av, 1, 1), Color.FromArgb((int)((Themes.IsLight ? 150 : 150) * appear), Ui.Blend(hue, Color.White, 0.1F)), Color.FromArgb((int)((Themes.IsLight ? 110 : 96) * appear), Ui.Blend(hue, Color.Black, 0.15F)), 90F)) g.FillPath(b, path);
        Glass.Paint(g, av, S(12), GlassKind.Secondary, 1.1F * appear);
        if (letters) GText.Draw(g, p.Initials, Ui.AvatarFont, Rectangle.Round(av), Fade(Themes.IsLight ? Ui.Blend(hue, Color.Black, 0.5F) : Ui.Blend(hue, Color.White, 0.62F), appear), true);
        else Assets.Draw(g, providerIcon, RectangleF.Inflate(av, -S(9), -S(9)), Fade(Ui.Blend(hue, Themes.IsLight ? Color.Black : Color.White, 0.5F), appear));
        if (live != null)
            using (var pen = new Pen(Color.FromArgb(170, Ui.Success), 1F)) g.DrawPath(pen, Ui.Round(av, S(12)));

        // server reachability badge on the avatar corner: its own shape (check / cross / clock), independent of the connection state
        var reach = ReachOf(p);
        if (reach != ReachState.Unknown)
        {
            var bd = S(16); var badge = new RectangleF(av.Right - bd + S(4), av.Bottom - bd + S(4), bd, bd);
            var rc = reach == ReachState.Up ? Ui.Success : reach == ReachState.Down ? Ui.Danger : Ui.Faint;
            using (var b = new SolidBrush(Fade(Ui.Blend(Ui.GlassPrimary, Color.Black, 0.35F), appear))) g.FillEllipse(b, badge);
            using (var pen = new Pen(Color.FromArgb((int)(110 * appear), rc), 1F)) g.DrawEllipse(pen, badge.X + 0.5F, badge.Y + 0.5F, badge.Width - 1, badge.Height - 1);
            Assets.Draw(g, reach == ReachState.Up ? "reach-up" : reach == ReachState.Down ? "reach-down" : "reach-checking", RectangleF.Inflate(badge, -S(3F), -S(3F)), Fade(rc, appear), 1.3F);
        }

        if (connecting || disconnecting)
            using (var pen = new Pen(Fade(connecting ? Ui.Highlight : Ui.Warning, appear), 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, RectangleF.Inflate(av, S(3), S(3)), now * 400 % 360, 90);
        if (success >= 0 && success < 0.8F)
        {
            var rad = S(22) * Ease.OutCubic(success / 0.8F);
            using (var pen = new Pen(Color.FromArgb((int)(150 * (1 - success / 0.8F)), Ui.Success), 1.5F)) g.DrawPath(pen, Ui.Round(RectangleF.Inflate(av, rad / 2, rad / 2), S(12) + rad / 2));
        }

        // right cluster: action button, pin star, status
        var plug = PlugRect(rr);
        var plugVisible = active || live != null || connecting || disconnecting;
        if (plugVisible) PaintPlug(g, plug, hover && hoverPlug, selected, connecting, disconnecting, live != null, now, appear);

        var star = StarRect(rr);
        var starVisible = pinned || active || pinPop.ContainsKey(id);
        if (starVisible)
        {
            float pop; var scale = 1F;
            if (pinPop.TryGetValue(id, out pop)) scale = 1F + 0.55F * (float)Math.Sin(Math.Min(1F, (now - pop) / 0.45F) * Math.PI);
            var sr = RectangleF.Inflate(new RectangleF(star.X, star.Y, star.Width, star.Height), -S(3F) + (1 - scale) * S(5F), -S(3F) + (1 - scale) * S(5F));
            Assets.Draw(g, "action-pin", sr, Fade(pinned ? Ui.Warning : (hover && hoverStar ? Ui.Warning : Ui.Faint), appear), 1F, pinned);
        }

        var dots = new string('.', (int)(now * 3) % 4);
        var status = connecting ? "Bağlanıyor" + dots : disconnecting ? "Kesiliyor" + dots : live ?? Ui.Relative(p.LastConnected);
        var statusFont = Ui.SubFont; var statusColor = connecting ? Ui.Highlight : disconnecting ? Ui.Warning : live != null ? Ui.Success : Ui.Faint;
        var right = (starVisible ? star.X : plugVisible ? plug.X : plug.Right) - S(8);
        var iconW = live != null || connecting || disconnecting ? S(18) : 0;
        var sw = status.Length > 0 ? GText.Measure(connecting ? "Bağlanıyor..." : disconnecting ? "Kesiliyor..." : status, statusFont).Width : 0;
        if (status.Length > 0) GText.Draw(g, status, statusFont, new Rectangle(right - sw, rr.Y, sw, rr.Height), Fade(statusColor, appear), false, true);
        if (iconW > 0)
        {
            // state is carried by a shape as well as a color: check = connected, spinner = in progress
            var ir = new RectangleF(right - sw - S(18), rr.Y + (rr.Height - S(14)) / 2F, S(14), S(14));
            if (live != null && !disconnecting) Assets.Draw(g, "state-connected", ir, Fade(Ui.Success, appear), 1.2F);
            else
            {
                var st = g.Save(); g.TranslateTransform(ir.X + ir.Width / 2, ir.Y + ir.Height / 2); g.RotateTransform(now * 360F % 360F); g.TranslateTransform(-ir.Width / 2, -ir.Height / 2);
                Assets.Draw(g, "state-connecting", new RectangleF(0, 0, ir.Width, ir.Height), Fade(connecting ? Ui.Highlight : Ui.Warning, appear), 1.2F); g.Restore(st);
            }
        }

        // texts
        var x = (int)(av.Right + S(14));
        var textRight = right - sw - iconW - S(10);
        DrawHighlighted(g, p.Name, Ui.RowNameFont, Fade(Ui.Text, appear), new Rectangle(x, rr.Y + S(10), Math.Max(10, textRight - x), S(20)));
        var sub = VpnProviders.RowSubLine(p);
        var sx = x;
        // provider logo before the provider name: [logo] Provider · server · user
        ProviderLogo(g, VpnProviders.KindOf(p), new RectangleF(sx, rr.Y + S(29.5F), S(20), S(20)), appear);
        sx += S(26);
        DrawHighlighted(g, sub, Ui.SubFont, Fade(Ui.Muted, appear), new Rectangle(sx, rr.Y + S(31), Math.Max(10, right - sw - iconW - S(10) - sx), S(18)));
    }

    // Provider logos: the vendors' marks supplied by the user (assets\provider-*.png, embedded), clipped to a
    // rounded tile with a thin glass rim so they sit on the dark surface. Falls back to a drawn glyph tile.
    static readonly Dictionary<VpnKind, Image> logoCache = new Dictionary<VpnKind, Image>();
    static Image LogoImage(VpnKind kind)
    {
        Image img;
        if (logoCache.TryGetValue(kind, out img)) return img;
        try { using (var s = typeof(ProfileList).Assembly.GetManifestResourceStream(VpnProviders.Asset(kind) + ".png")) if (s != null) img = Image.FromStream(s); }
        catch { img = null; }
        logoCache[kind] = img;
        return img;
    }

    internal static void ProviderLogo(Graphics g, VpnKind kind, RectangleF r, float appear)
    {
        var img = LogoImage(kind);
        using (var path = Ui.Round(r, r.Width * 0.23F))
        {
            if (img != null)
            {
                var st = g.Save();
                g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SetClip(path, CombineMode.Intersect);
                var attrs = new System.Drawing.Imaging.ImageAttributes();
                attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = Math.Max(0F, Math.Min(1F, appear)) });
                g.DrawImage(img, Rectangle.Round(r), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
                attrs.Dispose();
                g.Restore(st);
            }
            else
            {
                using (var b = new SolidBrush(Fade(Ui.Blend(Ui.GlassPrimary, Color.White, 0.2F), appear))) g.FillPath(b, path);
                Assets.Draw(g, VpnProviders.Asset(kind), RectangleF.Inflate(r, -r.Width * 0.17F, -r.Width * 0.17F), Fade(Ui.Text, appear), 1.35F);
            }
            using (var pen = new Pen(Color.FromArgb((int)(90 * appear), Color.White), 1F)) g.DrawPath(pen, path);
        }
    }

    // Sapphire glass action button: calm blue glass instead of a bright accent.
    void PaintPlug(Graphics g, Rectangle plug, bool hot, bool selected, bool connecting, bool disconnecting, bool live, float now, float appear)
    {
        var sc = 1F - 0.12F * (hot ? Ease.Clamp01(plugPress.Value) : 0F);
        var rect = new RectangleF(plug.X + plug.Width * (1 - sc) / 2F, plug.Y + plug.Height * (1 - sc) / 2F, plug.Width * sc, plug.Height * sc);
        var icon = RectangleF.Inflate(rect, -S(8F), -S(8F));
        if (live && !disconnecting)
        {
            // connected: disconnect (power) button, muted error tone
            Glass.Paint(g, rect, rect.Width / 2F, GlassKind.Secondary, hot ? 2.2F : 1.4F);
            if (hot) using (var b = new SolidBrush(Color.FromArgb(120, Ui.Danger))) g.FillEllipse(b, rect);
            Assets.Draw(g, "action-disconnect", icon, hot ? Ui.Text : Fade(Ui.Danger, appear));
            return;
        }
        if (connecting || disconnecting)
        {
            Glass.Paint(g, rect, rect.Width / 2F, GlassKind.Secondary, 1.4F);
            Assets.Draw(g, "action-connect", icon, Fade(connecting ? Ui.Highlight : Ui.Warning, appear));
            return;
        }
        // sapphire: Highlight -> Accent, thin specular rim, dim icon
        var top = Ui.Blend(Ui.Accent, Ui.Highlight, hot ? 0.7F : selected ? 0.55F : 0.4F);
        var bottom = Ui.Blend(Ui.Accent, Color.Black, hot ? 0.0F : 0.12F);
        var strength = selected || hot ? 1F : 0.7F;
        using (var b = new LinearGradientBrush(RectangleF.Inflate(rect, 1, 1), Color.FromArgb((int)(235 * strength), top), Color.FromArgb((int)(235 * strength), bottom), 90F)) g.FillEllipse(b, rect);
        Glass.Paint(g, rect, rect.Width / 2F, GlassKind.Secondary, 0.9F);
        using (var pen = new Pen(Color.FromArgb((int)(110 * strength), Ui.Highlight), 1F)) g.DrawEllipse(pen, rect.X + 0.5F, rect.Y + 0.5F, rect.Width - 1, rect.Height - 1);
        Assets.Draw(g, "action-connect", icon, Themes.IsLight ? Color.White : Color.FromArgb(selected || hot ? 245 : 205, Ui.Text));
    }

    // Draws text with the parts that match the search terms in the accent color.
    void DrawHighlighted(Graphics g, string text, Font font, Color color, Rectangle bounds)
    {
        if (bounds.Width <= 0) return;
        var mask = MatchMask(text);
        var full = GText.Measure(text, font);
        if (mask == null || full.Width > bounds.Width) { GText.Draw(g, text, font, bounds, color); return; }
        var x = bounds.X; var y = bounds.Y + (bounds.Height - full.Height) / 2; var start = 0;
        while (start < text.Length)
        {
            var end = start; var on = mask[start];
            while (end < text.Length && mask[end] == on) end++;
            var seg = text.Substring(start, end - start);
            GText.Draw(g, seg, font, new Point(x, y), on ? (Themes.IsLight ? Ui.Accent : Ui.Highlight) : color);
            x += GText.Measure(seg, font).Width;
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
        var cy = Math.Max(S(30), Height / 4);
        var d = S(60); var c = new RectangleF((Width - d) / 2F, cy, d, d);
        Glass.Paint(g, c, S(20), GlassKind.Primary, 1F, 0.4F);
        Ui.DrawMagnifier(g, Ui.Muted, c.X + S(19), c.Y + S(19), S(22));
        var y = (int)c.Bottom + S(16);
        foreach (var line in new[] { emptyTitle, emptyHint })
        {
            if (string.IsNullOrEmpty(line)) continue;
            var font = line == emptyTitle ? Ui.RowNameFont : Ui.SubFont;
            var maxW = Width - S(60);
            var words = line.Split(' '); var cur = "";
            var lines = new List<string>();
            foreach (var w in words) { var t = cur.Length == 0 ? w : cur + " " + w; if (GText.Measure(t, font).Width > maxW && cur.Length > 0) { lines.Add(cur); cur = w; } else cur = t; }
            if (cur.Length > 0) lines.Add(cur);
            foreach (var l in lines) { var h = GText.Measure(l, font).Height; GText.Draw(g, l, font, new Rectangle(S(30), y, maxW, h), line == emptyTitle ? Ui.Text : Ui.Muted, true); y += h + S(2); }
            y += S(4);
        }
        PaintEmptyActions(g, y + S(8));
    }

    // Glass pill buttons, side by side when they fit, else stacked; centered.
    void PaintEmptyActions(Graphics g, int y)
    {
        var actions = EmptyActions ?? new string[0];
        var rects = new Rectangle[actions.Length];
        if (actions.Length > 0)
        {
            var h = S(34); var gap = S(10);
            var widths = actions.Select(a => GText.Measure(a, Ui.SubFont).Width + S(32)).ToArray();
            var row = widths.Sum() + gap * (actions.Length - 1) <= Width - S(40);
            var x = (Width - (row ? widths.Sum() + gap * (actions.Length - 1) : 0)) / 2;
            for (var i = 0; i < actions.Length; i++)
            {
                rects[i] = row ? new Rectangle(x, y, widths[i], h) : new Rectangle((Width - widths[i]) / 2, y + i * (h + gap), widths[i], h);
                if (row) x += widths[i] + gap;
                var r = new RectangleF(rects[i].X + 0.5F, rects[i].Y + 0.5F, rects[i].Width - 1, rects[i].Height - 1);
                Glass.Paint(g, r, h / 2F, i == hoverAction ? GlassKind.Selected : GlassKind.Secondary, i == hoverAction ? 1F : 1.6F);
                GText.Draw(g, actions[i], Ui.SubFont, rects[i], i == hoverAction ? Ui.Text : Ui.Highlight, true);
            }
        }
        emptyActionRects = rects;
    }

    int EmptyActionAt(Point p)
    {
        var rects = emptyActionRects;
        if (EmptyActions == null || rects.Length != EmptyActions.Length) return -1;
        for (var i = 0; i < rects.Length; i++) if (rects[i].Contains(p)) return i;
        return -1;
    }
}
