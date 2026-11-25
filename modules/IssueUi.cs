using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// =====================================================================
// Windows of the direct GitHub report: the full text that would be sent (read-only), and the small
// non-modal notice with the created issue's link.
// =====================================================================

// "Raporu göster": exactly the title and body that go to GitHub (sanitized again, as when sending).
internal sealed class ReportPreview : Form
{
    static int S(float v) { return Ui.S(v); }

    ReportPreview(string title, string body)
    {
        Text = "Gönderilecek rapor"; AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = false; KeyPreview = true;
        BackColor = Ui.Bg; ForeColor = Ui.Text; Font = SFonts.Nav;
        Size = new Size(S(720), S(560)); MinimumSize = new Size(S(480), S(360));
        var head = new Label
        {
            Text = "GitHub'a gidecek metnin tamamı. Kayıt herkese açık olur; müşteri adı, sunucu veya parola görürseniz göndermeyin. " +
                   "Aynı hata açık bir kayıtta zaten varsa yalnızca kısa bir \"Tekrar: sürüm, Windows\" yorumu eklenir; " +
                   "eski kayıt kapalı ya da kilitliyse yeni kaydın başına \"Önceki kayıt: #n\" satırı eklenir.",
            Dock = DockStyle.Top, Height = S(64),   // sized from the wrapped text below Padding = new Padding(S(12), S(10), S(12), 0), ForeColor = Ui.Text, BackColor = Ui.Bg
        };
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F), BackColor = Ui.Surface, ForeColor = Ui.Text, BorderStyle = BorderStyle.None,
            Text = ("Başlık: " + title + "\n\n" + body).Replace("\r\n", "\n").Replace("\n", "\r\n")
        };
        var bar = new Panel { Dock = DockStyle.Bottom, Height = S(56), BackColor = Ui.Bg };
        var close = new SButton("Kapat", SButtonKind.Primary) { Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
        close.Width = Math.Max(S(92), close.Width);
        close.Location = new Point(bar.Width - close.Width - S(16), (bar.Height - close.Height) / 2);
        close.Click += delegate { Close(); };
        bar.Controls.Add(close);
        bar.Resize += delegate { close.Location = new Point(bar.Width - close.Width - S(16), (bar.Height - close.Height) / 2); };
        Controls.Add(box); Controls.Add(bar); Controls.Add(head);
        Action fit = delegate
        {
            var w = Math.Max(S(100), ClientSize.Width - head.Padding.Horizontal);
            var h = TextRenderer.MeasureText(head.Text, head.Font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            head.Height = h + head.Padding.Vertical + S(8);
        };
        Resize += delegate { fit(); };
        fit();
        KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; } };
        Shown += delegate { box.SelectionStart = 0; box.SelectionLength = 0; close.Focus(); };
    }

    public static void Show(IWin32Window owner, ErrorReport r)
    {
        if (r == null) return;
        string title, body;
        CrashReporter.Outgoing(r, out title, out body);
        using (var f = new ReportPreview(title, body)) { if (owner != null) f.ShowDialog(owner); else f.ShowDialog(); }
    }
}

// Corner notice after a send: text, "Aç" (the issue) and "Kapat"; closes itself after 30 s.
internal sealed class IssueNotice : Form
{
    static int S(float v) { return Ui.S(v); }
    static IssueNotice current;
    readonly string text;
    readonly Timer life = new Timer { Interval = 30000 };

    IssueNotice(string text, string url)
    {
        this.text = text ?? "";
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; BackColor = Ui.Bg; ForeColor = Ui.Text; Font = SFonts.Nav; DoubleBuffered = true;
        var textH = TextRenderer.MeasureText(this.text, SFonts.RowDesc, new Size(S(330), 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        Size = new Size(S(380), S(30) + textH + S(56));
        var area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.Right - Width - S(16), area.Bottom - Height - S(16));
        var x = Width - S(16);
        var close = new SButton("Kapat", SButtonKind.Secondary);
        x -= close.Width; close.Location = new Point(x, Height - S(14) - close.Height); close.Click += delegate { Close(); };
        Controls.Add(close);
        if (IssueLink.IsOurIssueUrl(url))
        {
            var open = new SButton("Aç", SButtonKind.Primary, Glyph.Open);
            x -= open.Width + S(8); open.Location = new Point(x, close.Top);
            open.Click += delegate { if (!Demo.On) try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } Close(); };
            Controls.Add(open);
        }
        life.Tick += delegate { life.Stop(); Close(); };
        FormClosed += delegate { life.Dispose(); if (current == this) current = null; };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var panel = new RectangleF(S(2), S(2), Width - S(4), Height - S(4));
        Ui.FillRound(g, Ui.GlassPrimary, panel, S(12));
        Glass.Paint(g, panel, S(12), GlassKind.Primary, 1F);
        TextRenderer.DrawText(g, text, SFonts.RowDesc, new Rectangle(S(18), S(16), S(330), Height - S(70)), Ui.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    public static void Show(string text, string url)
    {
        if (Demo.On) return;
        try
        {
            if (current != null && !current.IsDisposed) current.Close();
            current = new IssueNotice(text, url);
            current.Show();
            current.life.Start();
        }
        catch { }
    }
}
