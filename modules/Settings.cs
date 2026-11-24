using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

// =====================================================================
// Settings: model, JSON store, themes, hotkey text, autostart, dialog
// =====================================================================

internal sealed class AppSettings
{
    public string Hotkey;               // "Ctrl+Alt+V"
    public bool AutoStart;
    public string BackupFolder;         // Documents\OpenVPN Yedekleri
    public string BackupFrequency;      // "off", "change", "daily", "weekly"
    public int BackupKeep;              // 1..100
    public DateTime LastBackupUtc;
    public string DockSide;             // "auto", "right", "left"
    public string Theme;                // "dark", "light", "system"
    public bool ReachabilityCheck;
    public bool ShowForti;              // list FortiClient VPN tunnels too

    public static AppSettings Defaults()
    {
        return new AppSettings
        {
            Hotkey = "Ctrl+Alt+V",
            AutoStart = false,
            BackupFolder = DefaultBackupFolder(),
            BackupFrequency = "daily",
            BackupKeep = 10,
            LastBackupUtc = DateTime.MinValue,
            DockSide = "auto",
            Theme = "dark",
            ReachabilityCheck = true,
            ShowForti = true
        };
    }

    public static string DefaultBackupFolder()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenVPN Yedekleri");
    }

    public AppSettings Clone()
    {
        return (AppSettings)MemberwiseClone();
    }

    // Brings every field into its allowed set or range. Invalid values fall back to the default.
    public void Normalize()
    {
        var d = Defaults();
        uint mods, vk;
        if (!HotkeyText.TryParse(Hotkey, out mods, out vk)) Hotkey = d.Hotkey;
        BackupFolder = string.IsNullOrWhiteSpace(BackupFolder) ? d.BackupFolder : BackupFolder.Trim();
        if (Array.IndexOf(new[] { "off", "change", "daily", "weekly" }, BackupFrequency) < 0) BackupFrequency = d.BackupFrequency;
        BackupKeep = Math.Max(1, Math.Min(100, BackupKeep));
        if (Array.IndexOf(new[] { "auto", "right", "left" }, DockSide) < 0) DockSide = d.DockSide;
        if (Array.IndexOf(new[] { "dark", "light", "system" }, Theme) < 0) Theme = d.Theme;
        if (LastBackupUtc.Kind == DateTimeKind.Unspecified) LastBackupUtc = DateTime.SpecifyKind(LastBackupUtc, DateTimeKind.Utc);
    }
}

internal static class SettingsStore
{
    // Test hook: when set, this file is used instead of %APPDATA%\OpenVpnQuickSearch\settings.json.
    internal static string PathOverride;

    public static string FilePath
    {
        get
        {
            if (!string.IsNullOrEmpty(PathOverride)) return PathOverride;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenVpnQuickSearch", "settings.json");
        }
    }

    // Missing, unreadable or corrupt file gives defaults. Missing or wrongly typed fields get their default.
    public static AppSettings Load()
    {
        var s = AppSettings.Defaults();
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return s;
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            if (d == null) return AppSettings.Defaults();

            s.Hotkey = Str(d, "Hotkey", s.Hotkey);
            s.AutoStart = Bool(d, "AutoStart", s.AutoStart);
            s.BackupFolder = Str(d, "BackupFolder", s.BackupFolder);
            s.BackupFrequency = Str(d, "BackupFrequency", s.BackupFrequency);
            s.BackupKeep = Int(d, "BackupKeep", s.BackupKeep);
            long ticks = Long(d, "LastBackupUtcTicks", 0);
            s.LastBackupUtc = ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
            s.DockSide = Str(d, "DockSide", s.DockSide);
            s.Theme = Str(d, "Theme", s.Theme);
            s.ReachabilityCheck = Bool(d, "ReachabilityCheck", s.ReachabilityCheck);
            s.ShowForti = Bool(d, "ShowForti", s.ShowForti);
        }
        catch
        {
            return AppSettings.Defaults();
        }
        s.Normalize();
        return s;
    }

    // Atomic: writes settings.json.tmp, then replaces the real file. Throws IOException on failure.
    public static void Save(AppSettings s)
    {
        if (s == null) throw new ArgumentNullException("s");
        var n = s.Clone();
        n.Normalize();

        var d = new Dictionary<string, object>();
        d["Hotkey"] = n.Hotkey;
        d["AutoStart"] = n.AutoStart;
        d["BackupFolder"] = n.BackupFolder;
        d["BackupFrequency"] = n.BackupFrequency;
        d["BackupKeep"] = n.BackupKeep;
        d["LastBackupUtcTicks"] = n.LastBackupUtc.Kind == DateTimeKind.Local ? n.LastBackupUtc.ToUniversalTime().Ticks : n.LastBackupUtc.Ticks;
        d["DockSide"] = n.DockSide;
        d["Theme"] = n.Theme;
        d["ReachabilityCheck"] = n.ReachabilityCheck;
        var json = new JavaScriptSerializer().Serialize(d);

        var path = FilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    static string Str(IDictionary<string, object> d, string k, string def)
    {
        object v;
        return (d.TryGetValue(k, out v) && v is string) ? (string)v : def;
    }

    static bool Bool(IDictionary<string, object> d, string k, bool def)
    {
        object v;
        return (d.TryGetValue(k, out v) && v is bool) ? (bool)v : def;
    }

    static int Int(IDictionary<string, object> d, string k, int def)
    {
        long v = Long(d, k, def);
        return v < int.MinValue || v > int.MaxValue ? def : (int)v;
    }

    static long Long(IDictionary<string, object> d, string k, long def)
    {
        object v;
        if (!d.TryGetValue(k, out v) || v == null) return def;
        if (v is int || v is long || v is decimal || v is double)
        {
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { }
        }
        return def;
    }
}

internal static class Themes
{
    public static bool IsLight { get; private set; }

    // "light" = light, "system" = follow Windows apps setting, anything else = dark.
    public static void Apply(string theme)
    {
        var light = theme == "light" || (theme == "system" && SystemPrefersLight());
        if (light)
        {
            // Background, surface, hover, selection, border, strong border
            Set(Color.FromArgb(244, 246, 250), Color.FromArgb(255, 255, 255), Color.FromArgb(238, 241, 247),
                Color.FromArgb(255, 236, 218), Color.FromArgb(222, 227, 236), Color.FromArgb(198, 205, 218),
                // Brand, accent, accent2
                Color.FromArgb(22, 82, 184), Color.FromArgb(226, 112, 18), Color.FromArgb(200, 90, 10),
                // Text, muted, faint, gold, success
                Color.FromArgb(17, 24, 39), Color.FromArgb(92, 102, 122), Color.FromArgb(118, 127, 146),
                Color.FromArgb(180, 120, 0), Color.FromArgb(20, 138, 78));
        }
        else
        {
            // Deep navy + warm orange (same values as the Ui defaults).
            Set(Color.FromArgb(14, 18, 29), Color.FromArgb(23, 29, 43), Color.FromArgb(29, 36, 53),
                Color.FromArgb(52, 38, 32), Color.FromArgb(36, 44, 62), Color.FromArgb(55, 65, 88),
                Color.FromArgb(22, 82, 184), Color.FromArgb(247, 146, 50), Color.FromArgb(232, 108, 28),
                Color.FromArgb(240, 243, 248), Color.FromArgb(152, 162, 183), Color.FromArgb(102, 112, 134),
                Color.FromArgb(255, 194, 76), Color.FromArgb(70, 205, 130));
        }
        IsLight = light;
    }

    public static bool SystemPrefersLight()
    {
        try
        {
            var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return v == null || Convert.ToInt32(v, CultureInfo.InvariantCulture) != 0;
        }
        catch
        {
            return false;
        }
    }

    static void Set(Color bg, Color surface, Color surfaceHover, Color selectedFill, Color border, Color borderStrong,
        Color brand, Color accent, Color accent2, Color text, Color muted, Color faint, Color gold, Color success)
    {
        Ui.Bg = bg; Ui.Surface = surface; Ui.SurfaceHover = surfaceHover; Ui.SelectedFill = selectedFill;
        Ui.Border = border; Ui.BorderStrong = borderStrong;
        Ui.Brand = brand; Ui.Accent = accent; Ui.Accent2 = accent2;
        Ui.Text = text; Ui.Muted = muted; Ui.Faint = faint; Ui.Gold = gold; Ui.Success = success;
    }
}

internal static class HotkeyText
{
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8;

    // "Ctrl+Alt+V" -> modifiers and virtual-key code. Needs at least one modifier and exactly one key.
    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+');
        if (parts.Length < 2) return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            var m = ModifierOf(parts[i].Trim());
            if (m == 0 || (modifiers & m) != 0) return false;
            modifiers |= m;
        }

        Keys key;
        if (!ResolveKey(parts[parts.Length - 1].Trim(), out key)) return false;
        vk = (uint)key;
        return true;
    }

    // Keys.KeyData (e.g. from a KeyDown event) -> "Ctrl+Alt+V". Returns "" when no usable key is pressed.
    public static string Format(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (!IsUsableKey(key)) return "";
        var sb = new StringBuilder();
        if ((keyData & Keys.Control) != 0) sb.Append("Ctrl+");
        if ((keyData & Keys.Alt) != 0) sb.Append("Alt+");
        if ((keyData & Keys.Shift) != 0) sb.Append("Shift+");
        sb.Append(KeyName(key));
        return sb.ToString();
    }

    static uint ModifierOf(string t)
    {
        switch (t.ToLowerInvariant())
        {
            case "ctrl": case "control": return MOD_CONTROL;
            case "alt": return MOD_ALT;
            case "shift": return MOD_SHIFT;
            case "win": case "windows": return MOD_WIN;
            default: return 0;
        }
    }

    static bool ResolveKey(string t, out Keys key)
    {
        key = Keys.None;
        if (t.Length == 0) return false;
        var u = t.ToUpperInvariant();

        if (u.Length == 1 && ((u[0] >= 'A' && u[0] <= 'Z') || (u[0] >= '0' && u[0] <= '9')))
        {
            key = (Keys)(int)u[0]; // ASCII code equals the virtual-key code for letters and digits
            return true;
        }
        if (u.Length == 4 && u.StartsWith("NUM") && u[3] >= '0' && u[3] <= '9')
        {
            key = (Keys)(0x60 + (u[3] - '0'));
            return true;
        }
        switch (u)
        {
            case "ESC": case "ESCAPE": key = Keys.Escape; return true;
            case "ENTER": case "RETURN": key = Keys.Return; return true;
            case "BACKSPACE": case "BACK": key = Keys.Back; return true;
        }

        Keys parsed;
        if (!Enum.TryParse<Keys>(t, true, out parsed)) return false;
        if (!IsUsableKey(parsed)) return false;
        key = parsed;
        return true;
    }

    static bool IsUsableKey(Keys key)
    {
        var v = (int)key;
        if (v <= 0 || v > 0xFFFF) return false;
        switch (key)
        {
            case Keys.ShiftKey: case Keys.ControlKey: case Keys.Menu: case Keys.LWin: case Keys.RWin:
                return false;
        }
        return Enum.IsDefined(typeof(Keys), key);
    }

    static string KeyName(Keys key)
    {
        if (key >= Keys.A && key <= Keys.Z) return ((char)(int)key).ToString();
        if (key >= Keys.D0 && key <= Keys.D9) return ((char)(int)key).ToString();
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "Num" + ((int)key - (int)Keys.NumPad0).ToString();
        // Duplicate enum values (Next/PageDown, Prior/PageUp, Return/Enter) need explicit names.
        if (key == Keys.PageDown) return "PageDown";
        if (key == Keys.PageUp) return "PageUp";
        if (key == Keys.Return) return "Enter";
        if (key == Keys.Escape) return "Esc";
        if (key == Keys.Back) return "Backspace";
        return key.ToString();
    }
}

internal static class AutoStart
{
    // Test hook: full path of a .lnk file to use instead of the real Startup shortcut.
    internal static string PathOverride;

    public static string ShortcutPath
    {
        get
        {
            if (!string.IsNullOrEmpty(PathOverride)) return PathOverride;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "OpenVPN Hızlı Arama İzleyici.lnk");
        }
    }

    public static bool IsEnabled()
    {
        try { return File.Exists(ShortcutPath); }
        catch { return false; }
    }

    public static void SetEnabled(bool on)
    {
        var path = ShortcutPath;
        if (!on)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var exe = Application.ExecutablePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // WScript.Shell through late binding, so no extra reference is needed.
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("WScript.Shell kullanılamıyor.");
        var shell = Activator.CreateInstance(shellType);
        object sc = null;
        try
        {
            sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            var scType = sc.GetType();
            SetProp(scType, sc, "TargetPath", exe);
            SetProp(scType, sc, "Arguments", "--background");
            SetProp(scType, sc, "WorkingDirectory", Path.GetDirectoryName(exe) ?? "");
            SetProp(scType, sc, "IconLocation", exe + ",0");
            SetProp(scType, sc, "Description", "OpenVPN Hızlı Arama");
            scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
        finally
        {
            if (sc != null) Marshal.FinalReleaseComObject(sc);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    static void SetProp(Type t, object target, string name, object value)
    {
        t.InvokeMember(name, BindingFlags.SetProperty, null, target, new object[] { value });
    }
}

