using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// =====================================================================
// Saved FortiClient credentials for tunnels whose gateway does not let FortiClient keep the
// password. Stored in Windows Credential Manager (generic credential, encrypted with the user's
// DPAPI key, readable only by this Windows user), target "OpenVpnQuickSearch:forti:<tunnel>".
// The password is never written to settings, logs, the status bar or any file of this app.
// =====================================================================

internal sealed class FortiCredential
{
    public string Username = "", Password = "";
}

internal static class FortiCredentialStore
{
    const string TargetPrefix = "OpenVpnQuickSearch:forti:";
    const int CredTypeGeneric = 1, CredPersistLocalMachine = 2, ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public int Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWrite(ref CREDENTIAL c, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredRead(string target, int type, int flags, out IntPtr c);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr p);

    // prefix: other providers pass their own (e.g. SophosStore.CredentialPrefix); Forti keeps the default.
    static string Target(string tunnel, string prefix) { return (prefix ?? TargetPrefix) + tunnel; }

    public static bool Has(string tunnel, string prefix = null)
    {
        IntPtr p;
        if (!CredRead(Target(tunnel, prefix), CredTypeGeneric, 0, out p)) return false;
        CredFree(p);
        return true;
    }

    public static FortiCredential Load(string tunnel, string prefix = null)
    {
        IntPtr p;
        if (!CredRead(Target(tunnel, prefix), CredTypeGeneric, 0, out p)) return null;
        try
        {
            var c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
            var pass = c.CredentialBlobSize > 0 ? Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2) : "";
            return new FortiCredential { Username = c.UserName ?? "", Password = pass };
        }
        finally { CredFree(p); }
    }

    // Returns an error message or null.
    public static string Save(string tunnel, FortiCredential cred, string prefix = null)
    {
        var bytes = Encoding.Unicode.GetBytes(cred.Password ?? "");
        var blob = Marshal.AllocCoTaskMem(Math.Max(1, bytes.Length));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL
            {
                Type = CredTypeGeneric, TargetName = Target(tunnel, prefix), Comment = "OpenVPN Hızlı Arama · FortiClient",
                CredentialBlobSize = bytes.Length, CredentialBlob = blob, Persist = CredPersistLocalMachine,
                UserName = cred.Username ?? ""
            };
            if (!CredWrite(ref c, 0)) return "Parola kaydedilemedi (hata " + Marshal.GetLastWin32Error() + ").";
            return null;
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
            var zero = new byte[Math.Max(1, bytes.Length)];
            Marshal.Copy(zero, 0, blob, zero.Length);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public static bool Delete(string tunnel, string prefix = null)
    {
        if (CredDelete(Target(tunnel, prefix), CredTypeGeneric, 0)) return true;
        return Marshal.GetLastWin32Error() == ErrorNotFound;
    }

    // FortiClient itself keeps the password (HKCU ...\Sslvpn\Tunnels\<name>, SavePass = 1).
    // Only the flag is read, never the encrypted values.
    public static bool FortiKeepsPassword(string tunnel)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Fortinet\FortiClient\Sslvpn\Tunnels\" + tunnel))
                return k != null && Convert.ToString(k.GetValue("SavePass", "0")) == "1";
        }
        catch { return false; }
    }
}

// Asks for the username and password of one FortiClient tunnel.
internal sealed class FortiCredentialForm : Form
{
    readonly TextBox user = new TextBox(), pass = new TextBox();
    readonly CheckBox remember = new CheckBox();
    readonly Button ok = new Button(), cancel = new Button();

    public FortiCredential Result { get; private set; }
    public bool Remember { get { return remember.Checked; } }

    public FortiCredentialForm(string tunnel, FortiCredential current, bool saveOnly = false)
    {
        Text = "FortiClient · " + tunnel;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true; TopMost = true;
        BackColor = Ui.Bg; ForeColor = Ui.Text; Font = Ui.SubFont;
        ClientSize = new Size(Ui.S(380), Ui.S(262));

        var pad = Ui.S(20); var w = ClientSize.Width - pad * 2; var y = pad;
        var title = new Label { Text = tunnel, Font = Ui.NameFont, ForeColor = Ui.Text, AutoSize = false, AutoEllipsis = true };
        title.SetBounds(pad, y, w, Ui.S(22)); Controls.Add(title); y += Ui.S(26);
        var hint = new Label { Text = saveOnly ? "Windows Kimlik Bilgileri'nde saklanır." : "Bağlanırken FortiClient'a bu bilgiler girilir.", ForeColor = Ui.Muted, AutoSize = false };
        hint.SetBounds(pad, y, w, Ui.S(18)); Controls.Add(hint); y += Ui.S(28);

        y = Field("Kullanıcı adı", user, pad, y, w);
        y = Field("Parola", pass, pad, y, w);
        pass.UseSystemPasswordChar = true;
        if (current != null) { user.Text = current.Username; pass.Text = current.Password; }

        remember.Text = "Bu bilgisayarda sakla (Windows Kimlik Bilgileri)";
        remember.Checked = true; remember.ForeColor = Ui.Muted; remember.AutoSize = false; remember.FlatStyle = FlatStyle.Flat;
        remember.SetBounds(pad, y, w, Ui.S(22)); Controls.Add(remember); y += Ui.S(34);
        remember.Visible = !saveOnly;

        Style(ok, saveOnly ? "Kaydet" : "Bağlan", true); Style(cancel, "Vazgeç", false);
        var bw = Ui.S(110); var bh = Ui.S(32);
        ok.SetBounds(ClientSize.Width - pad - bw, y, bw, bh);
        cancel.SetBounds(ClientSize.Width - pad - bw * 2 - Ui.S(8), y, bw, bh);
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
        ok.Click += delegate
        {
            if (pass.Text.Length == 0) { pass.Focus(); return; }
            Result = new FortiCredential { Username = user.Text.Trim(), Password = pass.Text };
            DialogResult = DialogResult.OK;
        };
        cancel.DialogResult = DialogResult.Cancel;
        Shown += delegate { Activate(); (user.Text.Length == 0 ? user : pass).Focus(); };
    }

    int Field(string label, TextBox box, int x, int y, int w)
    {
        var l = new Label { Text = label, ForeColor = Ui.Muted, AutoSize = false };
        l.SetBounds(x, y, w, Ui.S(18)); Controls.Add(l);
        box.BorderStyle = BorderStyle.FixedSingle; box.BackColor = Ui.Surface; box.ForeColor = Ui.Text; box.Font = Ui.InputFont;
        box.SetBounds(x, y + Ui.S(19), w, Ui.S(28)); Controls.Add(box);
        return y + Ui.S(56);
    }

    static void Style(Button b, string text, bool primary)
    {
        b.Text = text; b.FlatStyle = FlatStyle.Flat; b.Font = Ui.SubFont; b.Cursor = Cursors.Hand;
        b.BackColor = primary ? Ui.Brand : Ui.Surface; b.ForeColor = Ui.Text;
        b.FlatAppearance.BorderColor = primary ? Ui.Accent2 : Ui.BorderStrong;
        b.FlatAppearance.MouseOverBackColor = primary ? Ui.Accent : Ui.SurfaceHover;
        b.UseVisualStyleBackColor = false;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { var on = 1; NativeMethods.DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { } // dark title bar
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        pass.Text = ""; // drop the copy held by the text box
        base.OnFormClosed(e);
    }
}
