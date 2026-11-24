using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// =====================================================================
// Duplicate profile detection and the "Yinelenen profiller" window.
// =====================================================================

internal static class DuplicateFinder
{
    // Trailing copy markers: " (1)", "(2)", " - Kopya", " copy", " - Copy (2)", " Kopya (3)".
    static readonly Regex CopyMarker = new Regex(
        @"(\s*\(\s*\d+\s*\)|\s*-\s*(kopya|copy)(\s*\(\s*\d+\s*\))?|\s+(kopya|copy)(\s*\(\s*\d+\s*\))?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly CompareInfo TrCompare = CultureInfo.GetCultureInfo("tr-TR").CompareInfo;

    // Profiles are duplicates when they share host + username (case-insensitive), or share a normalized name.
    // Overlapping matches are merged into one group. Groups hold at least two profiles.
    public static List<List<VpnProfile>> Find(IList<VpnProfile> profiles)
    {
        var result = new List<List<VpnProfile>>();
        if (profiles == null) return result;

        var items = profiles.Where(p => p != null).ToList();
        var parent = new int[items.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;

        var hostUserIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var p = items[i];
            var host = ProfileStore.SearchKey((p.Host ?? "").Trim());
            if (host.Length > 0)
                Join(parent, hostUserIndex, host + "\u0001" + ProfileStore.SearchKey((p.Username ?? "").Trim()), i);
            var name = NameKey(p.Name);
            if (name.Length > 0) Join(parent, nameIndex, name, i);
        }

        var buckets = new Dictionary<int, List<VpnProfile>>();
        for (var i = 0; i < items.Count; i++)
        {
            var root = Root(parent, i);
            List<VpnProfile> bucket;
            if (!buckets.TryGetValue(root, out bucket)) { bucket = new List<VpnProfile>(); buckets[root] = bucket; }
            bucket.Add(items[i]);
        }

        foreach (var bucket in buckets.Values)
        {
            if (bucket.Count < 2) continue;
            bucket.Sort(CompareMostRecent);
            result.Add(bucket);
        }
        result.Sort(CompareGroups);
        return result;
    }

    // Name with trailing copy markers and extra whitespace removed, in the same key form the search uses.
    public static string NameKey(string name)
    {
        var s = Regex.Replace(name ?? "", @"\s+", " ").Trim();
        for (; ; )
        {
            var t = CopyMarker.Replace(s, "").Trim();
            if (t == s) break;
            s = t;
        }
        return ProfileStore.SearchKey(s);
    }

    static void Join(int[] parent, Dictionary<string, int> index, string key, int i)
    {
        int j;
        if (index.TryGetValue(key, out j)) Union(parent, i, j);
        else index[key] = i;
    }

    static int Root(int[] parent, int x)
    {
        while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
        return x;
    }

    static void Union(int[] parent, int a, int b)
    {
        a = Root(parent, a); b = Root(parent, b);
        if (a != b) parent[b] = a;
    }

    static int CompareMostRecent(VpnProfile a, VpnProfile b)
    {
        if (a.LastConnected != b.LastConnected) return b.LastConnected.CompareTo(a.LastConnected);
        var byName = TrCompare.Compare(a.Name ?? "", b.Name ?? "", CompareOptions.IgnoreCase);
        return byName != 0 ? byName : string.CompareOrdinal(a.Id ?? "", b.Id ?? "");
    }

    static int CompareGroups(List<VpnProfile> a, List<VpnProfile> b)
    {
        var byName = TrCompare.Compare(a[0].Name ?? "", b[0].Name ?? "", CompareOptions.IgnoreCase);
        return byName != 0 ? byName : string.CompareOrdinal(a[0].Id ?? "", b[0].Id ?? "");
    }
}

internal sealed class DuplicateEntry
{
    public VpnProfile Profile;
    public bool Checked;
    public bool Removed;
}

internal sealed class DuplicateGroup
{
    public readonly List<DuplicateEntry> Entries = new List<DuplicateEntry>();
}

internal sealed class DuplicateRow
{
    public bool Header;
    public int Group, Entry, Top, Height;
}

// Custom-drawn, scrollable list of groups. Index 0 inside a group is the kept (most recent) profile.
internal sealed class DuplicateListView : Control
{
    public event EventHandler CheckedChanged;

    readonly List<DuplicateGroup> groups = new List<DuplicateGroup>();
    readonly List<DuplicateRow> rows = new List<DuplicateRow>();
    int contentHeight, offset, hoverRow = -1;

    static int S(float v) { return Ui.S(v); }
    int HeaderHeight { get { return S(34); } }
    int RowHeight { get { return S(60); } }
    int Gutter { get { return S(14); } }
    bool NeedsScroll { get { return contentHeight > ClientSize.Height; } }
    int CardRight { get { return ClientSize.Width - Gutter - (NeedsScroll ? S(10) : 0); } }

    public DuplicateListView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Ui.Bg; ForeColor = Ui.Text; TabStop = false;
    }

    public void SetGroups(IEnumerable<DuplicateGroup> source)
    {
        groups.Clear();
        groups.AddRange(source);
        rows.Clear();
        var top = 0;
        for (var gi = 0; gi < groups.Count; gi++)
        {
            rows.Add(new DuplicateRow { Header = true, Group = gi, Top = top, Height = HeaderHeight });
            top += HeaderHeight;
            for (var ei = 0; ei < groups[gi].Entries.Count; ei++)
            {
                rows.Add(new DuplicateRow { Group = gi, Entry = ei, Top = top, Height = RowHeight });
                top += RowHeight;
            }
        }
        contentHeight = top;
        hoverRow = -1;
        SetOffset(offset);
    }

    void SetOffset(int value)
    {
        var max = Math.Max(0, contentHeight - ClientSize.Height);
        offset = Math.Max(0, Math.Min(max, value));
        Invalidate();
    }

    int RowAt(Point p)
    {
        if (p.X > CardRight) return -1;
        var y = p.Y + offset;
        for (var i = 0; i < rows.Count; i++)
            if (y >= rows[i].Top && y < rows[i].Top + rows[i].Height) return i;
        return -1;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SetOffset(offset);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (CanFocus) Focus();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hoverRow = -1; Cursor = Cursors.Default; Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var idx = RowAt(e.Location);
        Cursor = idx >= 0 && !rows[idx].Header ? Cursors.Hand : Cursors.Default;
        if (idx != hoverRow) { hoverRow = idx; Invalidate(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetOffset(offset - e.Delta / 120 * S(40));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var idx = RowAt(e.Location);
        if (idx < 0 || rows[idx].Header) return;
        var entry = groups[rows[idx].Group].Entries[rows[idx].Entry];
        entry.Checked = !entry.Checked;
        Invalidate();
        var handler = CheckedChanged;
        if (handler != null) handler(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Ui.Bg);
        if (groups.Count == 0) { DrawEmpty(g); return; }

        var viewBottom = offset + ClientSize.Height;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Top + row.Height <= offset || row.Top >= viewBottom) continue;
            var y = row.Top - offset;
            if (row.Header) DrawHeader(g, row, y);
            else DrawEntry(g, row, y, i == hoverRow);
        }
        DrawScrollbar(g);
    }

    void DrawEmpty(Graphics g)
    {
        const string title = "Yinelenen profil bulunamadı";
        const string sub = "Aynı ada, sunucuya ve kullanıcıya sahip profil yok.";
        var ts = Ui.Measure(g, title, Ui.NameFont);
        var ss = Ui.Measure(g, sub, Ui.SubFont);
        var y = ClientSize.Height / 2 - S(24);
        TextRenderer.DrawText(g, title, Ui.NameFont, new Point((ClientSize.Width - ts.Width) / 2, y), Ui.Muted, Ui.Flags);
        TextRenderer.DrawText(g, sub, Ui.SubFont, new Point((ClientSize.Width - ss.Width) / 2, y + S(26)), Ui.Faint, Ui.Flags);
    }

    void DrawHeader(Graphics g, DuplicateRow row, int y)
    {
        var group = groups[row.Group];
        var countText = group.Entries.Count + " profil";
        var countSize = Ui.Measure(g, countText, Ui.SmallFont);
        var label = ("GRUP " + (row.Group + 1) + " · " + group.Entries[0].Profile.Name).ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
        var labelRect = new Rectangle(Gutter, y + S(12), Math.Max(S(20), CardRight - Gutter - countSize.Width - S(12)), S(18));
        TextRenderer.DrawText(g, label, Ui.HeaderFont, labelRect, Ui.Muted, Ui.Flags | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, countText, Ui.SmallFont, new Point(CardRight - countSize.Width, y + S(13)), Ui.Faint, Ui.Flags);
    }

    void DrawEntry(Graphics g, DuplicateRow row, int y, bool hover)
    {
        var entry = groups[row.Group].Entries[row.Entry];
        var p = entry.Profile;
        var keep = row.Entry == 0;
        var fill = hover ? Ui.SurfaceHover : Ui.Surface;

        var card = new Rectangle(Gutter, y + S(3), CardRight - Gutter, row.Height - S(6));
        var cardF = new RectangleF(card.X, card.Y, card.Width, card.Height);
        Ui.FillRound(g, fill, cardF, S(8));
        Ui.DrawRound(g, Ui.Border, cardF, S(8));

        // Checkbox
        var box = new RectangleF(card.X + S(12), card.Y + (card.Height - S(16)) / 2F, S(16), S(16));
        if (entry.Checked)
        {
            Ui.FillRound(g, Ui.Accent, box, S(4));
            using (var pen = new Pen(Color.White, S(2)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[]
                {
                    new PointF(box.X + S(4), box.Y + S(8.5F)),
                    new PointF(box.X + S(7), box.Y + S(11.5F)),
                    new PointF(box.X + S(12), box.Y + S(5))
                });
            }
        }
        else
        {
            Ui.FillRound(g, Ui.Bg, box, S(4));
            Ui.DrawRound(g, Ui.BorderStrong, box, S(4));
        }

        // Avatar
        var av = new Rectangle((int)box.Right + S(12), card.Y + (card.Height - S(34)) / 2, S(34), S(34));
        using (var b = new SolidBrush(Ui.AvatarColor(p.Name ?? ""))) g.FillEllipse(b, av);
        TextRenderer.DrawText(g, p.Initials ?? "?", Ui.InitialsFont, av, Color.White,
            Ui.Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // Right side: "Korunacak" badge (top) and last connected time (bottom)
        var right = card.Right - S(12);
        var badgeW = 0;
        Size badgeText = Size.Empty;
        if (keep)
        {
            badgeText = Ui.Measure(g, "Korunacak", Ui.SmallFont);
            badgeW = badgeText.Width + S(16);
        }
        var timeText = Ui.Relative(p.LastConnected);
        var timeSize = string.IsNullOrEmpty(timeText) ? Size.Empty : Ui.Measure(g, timeText, Ui.SmallFont);
        var rightW = Math.Max(badgeW, timeSize.Width);
        var textX = av.Right + S(12);
        var textW = Math.Max(S(40), right - rightW - S(10) - textX);

        if (keep)
        {
            var br = new RectangleF(right - badgeW, card.Y + S(9), badgeW, S(18));
            Ui.FillRound(g, Ui.Blend(fill, Ui.Success, 0.18F), br, S(9));
            Ui.DrawRound(g, Ui.Blend(fill, Ui.Success, 0.55F), br, S(9));
            TextRenderer.DrawText(g, "Korunacak", Ui.SmallFont, Rectangle.Round(br), Ui.Success,
                Ui.Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (timeSize.Width > 0)
            TextRenderer.DrawText(g, timeText, Ui.SmallFont,
                new Rectangle(right - timeSize.Width, card.Y + S(31), timeSize.Width, S(16)), Ui.Faint, Ui.Flags);

        // Name and "host · user"
        TextRenderer.DrawText(g, p.Name ?? "", Ui.NameFont, new Rectangle(textX, card.Y + S(8), textW, S(20)),
            Ui.Text, Ui.Flags | TextFormatFlags.EndEllipsis);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(p.Host)) parts.Add(p.Host);
        if (!string.IsNullOrEmpty(p.Username)) parts.Add(p.Username);
        var sub = parts.Count > 0 ? string.Join(" · ", parts) : "Sunucu bilgisi yok";
        TextRenderer.DrawText(g, sub, Ui.SubFont, new Rectangle(textX, card.Y + S(30), textW, S(18)),
            Ui.Muted, Ui.Flags | TextFormatFlags.EndEllipsis);
    }

    void DrawScrollbar(Graphics g)
    {
        if (!NeedsScroll) return;
        var h = ClientSize.Height;
        var trackX = ClientSize.Width - S(8);
        var thumbH = Math.Min(h - S(8), Math.Max(S(30), (int)((float)h * h / contentHeight)));
        var max = contentHeight - h;
        var thumbY = S(4) + (int)((float)(h - S(8) - thumbH) * offset / max);
        Ui.FillRound(g, Ui.BorderStrong, new RectangleF(trackX, thumbY, S(4), thumbH), S(2));
    }
}

internal sealed class DuplicatesForm : Form
{
    static readonly Color Danger = Color.FromArgb(214, 62, 74), DangerHover = Color.FromArgb(232, 84, 96);

    readonly List<DuplicateGroup> groups = new List<DuplicateGroup>();
    readonly Func<VpnProfile, string> remove;
    readonly DuplicateListView list = new DuplicateListView();
    readonly Label summary = new Label();
    readonly Panel footer = new Panel();
    readonly Button deleteButton = new Button(), closeButton = new Button();
    int removedCount;

    static int S(float v) { return Ui.S(v); }

    public int RemovedCount { get { return removedCount; } }

    // groups: each inner list is one set of duplicates, most recently used first (as DuplicateFinder.Find returns).
    // remove: deletes one profile; returns null on success or an error message.
    public DuplicatesForm(List<List<VpnProfile>> sourceGroups, Func<VpnProfile, string> removeProfile)
    {
        remove = removeProfile;
        if (sourceGroups != null)
        {
            foreach (var members in sourceGroups)
            {
                if (members == null) continue;
                var group = new DuplicateGroup();
                foreach (var p in members)
                {
                    if (p == null) continue;
                    group.Entries.Add(new DuplicateEntry { Profile = p, Checked = group.Entries.Count > 0 });
                }
                if (group.Entries.Count > 1) groups.Add(group);
            }
        }

        Text = "Yinelenen profiller";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg; ForeColor = Ui.Text; Font = Ui.SubFont;
        KeyPreview = true;
        Size = new Size(S(520), S(560));
        Icon = IconArt.Load(new Size(32, 32)) ?? Icon;
        KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };

        list.Dock = DockStyle.Fill;
        list.CheckedChanged += delegate { UpdateButtons(); };

        summary.Dock = DockStyle.Top;
        summary.Height = S(62);
        summary.Padding = new Padding(S(18), S(16), S(18), 0);
        summary.BackColor = Ui.Bg; summary.ForeColor = Ui.Muted; summary.Font = Ui.SubFont;

        footer.Dock = DockStyle.Bottom;
        footer.Height = S(64);
        footer.BackColor = Ui.Bg;
        footer.Paint += delegate(object sender, PaintEventArgs e)
        {
            using (var pen = new Pen(Ui.Border)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        footer.Resize += delegate { LayoutFooter(); };

        StyleButton(deleteButton, Danger, DangerHover, Color.White, Ui.NameFont, S(180));
        deleteButton.Click += delegate { DeleteChecked(); };
        StyleButton(closeButton, Ui.Surface, Ui.SurfaceHover, Ui.Text, Ui.MenuFont, S(96));
        closeButton.FlatAppearance.BorderColor = Ui.BorderStrong;
        closeButton.Text = "Kapat";
        closeButton.Click += delegate { Close(); };
        footer.Controls.Add(deleteButton);
        footer.Controls.Add(closeButton);

        // Fill first so Top and Bottom docks take their space.
        Controls.Add(list);
        Controls.Add(footer);
        Controls.Add(summary);

        list.SetGroups(groups);
        UpdateButtons();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var on = 1;
            NativeMethods.DwmSetWindowAttribute(Handle, 20, ref on, 4);
        }
        catch { }
    }

    static void StyleButton(Button b, Color back, Color hover, Color fore, Font font, int width)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = back;
        b.FlatAppearance.MouseOverBackColor = hover;
        b.FlatAppearance.MouseDownBackColor = hover;
        b.BackColor = back; b.ForeColor = fore; b.Font = font;
        b.Size = new Size(width, S(34));
        b.Cursor = Cursors.Hand;
        b.TabStop = true;
    }

    void LayoutFooter()
    {
        closeButton.Location = new Point(footer.ClientSize.Width - S(18) - closeButton.Width, S(15));
        deleteButton.Location = new Point(closeButton.Left - S(10) - deleteButton.Width, S(15));
    }

    void UpdateButtons()
    {
        var total = 0; var selected = 0;
        foreach (var g in groups)
        {
            total += g.Entries.Count;
            foreach (var e in g.Entries) if (e.Checked) selected++;
        }
        summary.Text = groups.Count == 0 ? "" :
            groups.Count + " grup, " + total + " yinelenen profil bulundu. Her grupta en son kullanılan profil korunur.";
        deleteButton.Text = "Seçilenleri sil (" + selected + ")";
        deleteButton.Enabled = selected > 0;
        deleteButton.BackColor = selected > 0 ? Danger : Ui.Surface;
        deleteButton.FlatAppearance.BorderColor = selected > 0 ? Danger : Ui.Border;
        deleteButton.ForeColor = selected > 0 ? Color.White : Ui.Faint;
    }

    static bool IsRemoved(DuplicateEntry e) { return e.Removed; }

    void DeleteChecked()
    {
        var selected = new List<DuplicateEntry>();
        foreach (var g in groups)
            foreach (var e in g.Entries) if (e.Checked) selected.Add(e);
        if (selected.Count == 0) return;

        var answer = GlassDialog.Show(this,
            selected.Count + " profil OpenVPN Connect'ten kalıcı olarak silinecek. Devam edilsin mi?",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        var ok = 0;
        var errors = new List<string>();
        foreach (var entry in selected)
        {
            string error;
            try { error = remove == null ? "Silme işlemi kullanılamıyor." : remove(entry.Profile); }
            catch (Exception ex) { error = ex.Message; }

            if (string.IsNullOrEmpty(error)) { ok++; entry.Removed = true; }
            else errors.Add(entry.Profile.Name + ": " + error);
        }
        removedCount += ok;

        foreach (var g in groups) g.Entries.RemoveAll(IsRemoved);
        groups.RemoveAll(g => g.Entries.Count < 2);
        list.SetGroups(groups);
        UpdateButtons();

        if (errors.Count == 0)
        {
            GlassDialog.Show(this, ok + " profil silindi.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var shown = errors.Take(8).ToList();
        var text = ok + " profil silindi, " + errors.Count + " profil silinemedi:\n\n" + string.Join("\n", shown);
        if (errors.Count > shown.Count) text += "\n...";
        GlassDialog.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
