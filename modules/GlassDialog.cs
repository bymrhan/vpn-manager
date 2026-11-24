using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// =====================================================================
// Glass modal dialog (replaces the system MessageBox so every dialog uses the same material),
// and the toast notification that slides up over the list.
// =====================================================================
internal sealed class GlassDialog : Form
{
    static int S(float v) { return Ui.S(v); }
    readonly string text, caption; readonly MessageBoxIcon icon;
    readonly List<SButton> buttons = new List<SButton>();
    readonly Rectangle textRect; readonly Font bodyFont = SFonts.Nav;
    int fadeStep;
    readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer { Interval = 15 };
    Point home;

    GlassDialog(string text, string caption, MessageBoxButtons kind, MessageBoxIcon icon, int def)
    {
        this.defIndex = def;
        this.text = text ?? ""; this.caption = caption ?? ""; Text = this.caption; this.icon = icon;
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true; DoubleBuffered = true; BackColor = Ui.Bg; ForeColor = Ui.Text; Font = SFonts.Nav;

        var width = S(440); var textW = width - S(96);
        var textH = Math.Min(S(360), Math.Max(S(24), TextRenderer.MeasureText(this.text, bodyFont, new Size(textW, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height));
        textRect = new Rectangle(S(76), S(66), textW, textH);
        Size = new Size(width, textRect.Bottom + S(84));

        string[] labels; DialogResult[] results;
        switch (kind)
        {
            case MessageBoxButtons.OKCancel: labels = new[] { "Tamam", "İptal" }; results = new[] { DialogResult.OK, DialogResult.Cancel }; break;
            case MessageBoxButtons.YesNo: labels = new[] { "Evet", "Hayır" }; results = new[] { DialogResult.Yes, DialogResult.No }; break;
            case MessageBoxButtons.YesNoCancel: labels = new[] { "Evet", "Hayır", "İptal" }; results = new[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel }; break;
            default: labels = new[] { "Tamam" }; results = new[] { DialogResult.OK }; break;
        }
        var x = Width - S(22);
        for (var i = labels.Length - 1; i >= 0; i--)
        {
            var b = new SButton(labels[i], i == Math.Min(def, labels.Length - 1) ? SButtonKind.Primary : SButtonKind.Secondary) { Width = Math.Max(S(92), 0) };
            var r = results[i]; b.Click += delegate { DialogResult = r; };
            x -= b.Width; b.Location = new Point(x, Height - S(22) - b.Height); x -= S(8);
            Controls.Add(b); buttons.Insert(0, b);
        }
        CancelResult = results.Contains(DialogResult.Cancel) ? DialogResult.Cancel : results.Contains(DialogResult.No) ? DialogResult.No : DialogResult.OK;
        KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = CancelResult; e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && buttons.Count > 0) { buttons[Math.Min(defIndex, buttons.Count - 1)].InvokeClick(); e.Handled = true; }
        };
        fade.Tick += delegate
        {
            fadeStep++; var t = Ease.OutCubic(fadeStep / 11F);
            Opacity = t; Top = home.Y + (int)((1 - t) * S(12));
            if (fadeStep >= 11) { fade.Stop(); Opacity = 1; Top = home.Y; }
        };
    }

    DialogResult CancelResult; readonly int defIndex;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Backdrop.SetCorner(Handle, 2); var d = Themes.IsLight ? 0 : 1; NativeMethods.DwmSetWindowAttribute(Handle, 20, ref d, 4); } catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e); home = Location;
        if (!Motion.Reduced) { Opacity = 0; Top = home.Y + S(12); fadeStep = 0; fade.Start(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var panel = new RectangleF(S(8), S(8), Width - S(16), Height - S(16));
        Ui.FillRound(g, Ui.GlassPrimary, panel, S(14));
        Glass.Paint(g, panel, S(14), GlassKind.Primary, 1F);
        string name; Color tone;
        switch (icon)
        {
            case MessageBoxIcon.Warning: name = "state-error"; tone = Ui.Warning; break;
            case MessageBoxIcon.Error: name = "state-error"; tone = Ui.Danger; break;
            case MessageBoxIcon.Question: name = "nav-about"; tone = Ui.Highlight; break;
            default: name = "nav-about"; tone = Ui.Highlight; break;
        }
        var chip = new RectangleF(S(26), S(26), S(36), S(36));
        Glass.Paint(g, chip, S(11), GlassKind.Secondary, 1.8F);
        Assets.Draw(g, name, RectangleF.Inflate(chip, -S(8), -S(8)), tone);
        TextRenderer.DrawText(g, caption, SFonts.Big, new Point(S(76), S(28)), Ui.Text, Ui.Flags);
        TextRenderer.DrawText(g, text, bodyFont, textRect, Ui.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        Glass.WindowFrame(g, ClientRectangle, S(8));
    }

    protected override void Dispose(bool disposing) { if (disposing) fade.Dispose(); base.Dispose(disposing); }

    // ---- MessageBox-compatible entry points ----
    public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        return ShowCore(owner, text, caption, buttons, icon, 0);
    }
    static DialogResult ShowCore(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, int def)
    {
        using (var d = new GlassDialog(text, caption, buttons, icon, def)) return owner != null ? d.ShowDialog(owner) : d.ShowDialog();
    }
    public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def) { return ShowCore(owner, text, caption, buttons, icon, def == MessageBoxDefaultButton.Button2 ? 1 : def == MessageBoxDefaultButton.Button3 ? 2 : 0); }
    public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons) { return Show(owner, text, caption, buttons, MessageBoxIcon.None); }
    public static DialogResult Show(IWin32Window owner, string text, string caption) { return Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None); }
    public static DialogResult Show(IWin32Window owner, string text) { return Show(owner, text, "", MessageBoxButtons.OK, MessageBoxIcon.None); }
    public static DialogResult Show(string text, string caption) { return Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None); }
}

// Toast: a floating glass capsule that slides up from the footer, then away.
internal sealed class ToastLayer : Control
{
    string message = ""; string iconName = "notification"; Color tone;
    readonly Spring slide = new Spring(1, 300, 26); readonly Animator anim = new Animator();
    readonly System.Windows.Forms.Timer hold = new System.Windows.Forms.Timer { Interval = 2800 };
    int baseTop; static readonly Font F = new Font(Ui.Family, 9F);

    public ToastLayer()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Opaque, true); SetStyle(ControlStyles.Selectable, false);
        Visible = false; TabStop = false; tone = Ui.Success;
        anim.Tick = dt =>
        {
            var m = slide.Step(dt);
            Top = baseTop + (int)(slide.Value * Ui.S(24));
            if (!m && slide.Target >= 1F) Visible = false;
            return m;
        };
        hold.Tick += delegate { hold.Stop(); slide.Target = 1F; anim.Kick(); };
    }

    protected override void Dispose(bool disposing) { if (disposing) { anim.Dispose(); hold.Dispose(); } base.Dispose(disposing); }

    // Shows text; the capsule is centered above the footer of its parent.
    public void ShowToast(string text, bool problem)
    {
        message = text ?? ""; iconName = problem ? "state-error" : "state-connected"; tone = problem ? Ui.Warning : Ui.Success;
        var w = Math.Min(Parent.ClientSize.Width - Ui.S(24), GText.Measure(message, F).Width + Ui.S(70)); var h = Ui.S(42);
        Size = new Size(w, h);
        baseTop = Parent.ClientSize.Height - Ui.S(38) - h - Ui.S(6);
        Left = (Parent.ClientSize.Width - w) / 2;
        if (!Visible) { slide.Snap(1F); Top = baseTop + Ui.S(24); Visible = true; BringToFront(); }
        slide.Target = 0F; anim.Kick(); Invalidate();
        hold.Stop(); hold.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; GlassPaint.Begin(g, this);
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        Glass.Paint(g, r, Height / 2F, GlassKind.Floating, 1F, 0.4F);
        Assets.Draw(g, iconName, new RectangleF(Ui.S(16), (Height - Ui.S(18)) / 2F, Ui.S(18), Ui.S(18)), tone, 1.1F);
        GText.Draw(g, message, F, new Rectangle(Ui.S(44), 0, Width - Ui.S(58), Height), Ui.Text);
    }
}

