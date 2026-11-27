using System;
using System.Drawing;
using System.Windows.Forms;

// Asks for a profile's new name: one text box, a line saying what the rename does for this VPN
// client, and the reason while the name cannot be used (NameRules.Check). Same look as
// FortiCredentialForm.
internal sealed class RenameDialog : Form
{
    readonly TextBox box = new TextBox();
    readonly Label reason = new Label();
    readonly SButton ok = new SButton("", SButtonKind.Primary), cancel = new SButton("", SButtonKind.Secondary);
    readonly Func<string, string> validate;

    // The cleaned name (may be empty where the rules allow it); set when the dialog ends with OK.
    public string Result { get; private set; }

    public RenameDialog(string currentName, string hint, Func<string, string> validate)
    {
        this.validate = validate;
        Text = "Yeniden adlandır";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; TopMost = true;
        BackColor = Ui.Bg; ForeColor = Ui.Text; Font = Ui.SubFont;
        ClientSize = new Size(Ui.S(400), Ui.S(212));

        var pad = Ui.S(20); var w = ClientSize.Width - pad * 2; var y = pad;
        var label = new Label { Text = "Profil adı", ForeColor = Ui.Muted, AutoSize = false };
        label.SetBounds(pad, y, w, Ui.S(18)); Controls.Add(label); y += Ui.S(19);
        box.BorderStyle = BorderStyle.FixedSingle; box.BackColor = Ui.Surface; box.ForeColor = Ui.Text; box.Font = Ui.InputFont;
        box.MaxLength = NameRules.MaxLength; box.Text = currentName ?? "";
        box.SetBounds(pad, y, w, Ui.S(28)); Controls.Add(box); y += Ui.S(36);

        var hintLabel = new Label { Text = hint ?? "", ForeColor = Ui.Muted, AutoSize = false };
        hintLabel.SetBounds(pad, y, w, Ui.S(34)); Controls.Add(hintLabel); y += Ui.S(36);
        reason.ForeColor = Ui.Danger; reason.AutoSize = false;
        reason.SetBounds(pad, y, w, Ui.S(18)); Controls.Add(reason);   // its own line: the reason must be readable in full

        ok.Text = "Kaydet"; cancel.Text = "Vazgeç";
        var bw = Ui.S(110); var bh = Ui.S(32); var by = ClientSize.Height - pad - bh;
        ok.SetBounds(ClientSize.Width - pad - bw, by, bw, bh);
        cancel.SetBounds(ClientSize.Width - pad - bw * 2 - Ui.S(8), by, bw, bh);
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
        cancel.DialogResult = DialogResult.Cancel;
        ok.Click += delegate { if (Recheck()) { Result = NameRules.Clean(box.Text); DialogResult = DialogResult.OK; } };
        box.TextChanged += delegate { Recheck(); };
        Shown += delegate { Activate(); box.Focus(); box.SelectAll(); };
        Recheck();
    }

    bool Recheck()
    {
        var why = validate != null ? validate(box.Text) : null;
        reason.Text = why ?? "";
        ok.Enabled = why == null;
        return why == null;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { var on = 1; NativeMethods.DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { } // dark title bar
    }
}
