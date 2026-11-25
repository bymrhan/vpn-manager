using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
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
    public string Presence;             // "tray", "both" (tray + taskbar button while shown), "taskbar" (button while running, no tray icon); PresenceMode
    public string BackupFolder;        // Documents\OpenVPN Yedekleri
    public string BackupFrequency;      // "off", "change", "daily", "weekly"
    public int BackupKeep;              // 1..100
    public DateTime LastBackupUtc;
    public string DockSide;             // "auto", "right", "left"
    public string Theme;                // "dark", "light", "system", "ocean" (Derin Mavi); Themes.Ids
    public bool ReachabilityCheck;
    public bool ShowForti;              // list FortiClient VPN tunnels too
    public bool ShowSophos;             // list Sophos Connect connections too (when installed)
    public bool ShowWatchGuard;         // list WatchGuard SSL VPN servers too (when installed)
    public bool ShowFrequent;           // "SIK KULLANILANLAR" group under the recent ones (missing key = on)
    public string UpdateMode;           // "ask" (tell the user), "auto" (install silently when idle), "off" (manual check only)
    public string UpdateInterval;       // "startup", "daily", "weekly"
    public DateTime LastUpdateCheckUtc; // last successful update check
    public string SkippedVersion;       // "2.1.0" the user chose to skip; "" = none
    public bool LogEnabled;             // app log + crash / error records in %APPDATA%\OpenVpnQuickSearch\logs
    public string CrashPrompt;          // "ask" (offer the GitHub report after a crash), "auto" (send without asking; needs the embedded token), "never"
    public bool CrashAutoConsent;       // the one-time public-issue warning for "auto" was confirmed
    public string ProfileFolder;        // 2.1.2: extra .ovpn folder picked by hand ("" = none; ProfileSources)
    public List<string> ExtraProfileFiles; // 2.1.2: .ovpn files picked by hand ("elle eklenen")

    public static AppSettings Defaults()
    {
        return new AppSettings
        {
            Hotkey = "Ctrl+Alt+V",
            AutoStart = false,
            Presence = PresenceMode.Tray,
            BackupFolder = DefaultBackupFolder(),
            BackupFrequency = "daily",
            BackupKeep = 10,
            LastBackupUtc = DateTime.MinValue,
            DockSide = "auto",
            Theme = "dark",
            ReachabilityCheck = true,
            ShowForti = true,
            ShowSophos = true,
            ShowWatchGuard = true,
            ShowFrequent = true,
            UpdateMode = "ask",
            UpdateInterval = "daily",
            LastUpdateCheckUtc = DateTime.MinValue,
            SkippedVersion = "",
            LogEnabled = true,
            CrashPrompt = "ask",
            CrashAutoConsent = false,
            ProfileFolder = "",
            ExtraProfileFiles = new List<string>()
        };
    }

    public static string DefaultBackupFolder()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenVPN Yedekleri");
    }

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.ExtraProfileFiles = ExtraProfileFiles != null ? new List<string>(ExtraProfileFiles) : new List<string>(); // no shared list
        return c;
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
        Presence = PresenceMode.Normalize(Presence);
        if (Array.IndexOf(new[] { "auto", "right", "left" }, DockSide) < 0) DockSide = d.DockSide;
        if (!Themes.IsValid(Theme)) Theme = d.Theme;   // "dark", "light", "system", "ocean"
        if (LastBackupUtc.Kind == DateTimeKind.Unspecified) LastBackupUtc = DateTime.SpecifyKind(LastBackupUtc, DateTimeKind.Utc);
        if (Array.IndexOf(new[] { "ask", "auto", "off" }, UpdateMode) < 0) UpdateMode = d.UpdateMode;
        if (Array.IndexOf(new[] { "startup", "daily", "weekly" }, UpdateInterval) < 0) UpdateInterval = d.UpdateInterval;
        if (LastUpdateCheckUtc.Kind == DateTimeKind.Unspecified) LastUpdateCheckUtc = DateTime.SpecifyKind(LastUpdateCheckUtc, DateTimeKind.Utc);
        SkippedVersion = UpdateLogic.NormalizeTag(SkippedVersion) ?? "";
        if (CrashPrompt != "ask" && CrashPrompt != "auto" && CrashPrompt != "never") CrashPrompt = d.CrashPrompt;
        if (CrashPrompt != "auto") CrashAutoConsent = false;   // the consent belongs to the auto mode only
        ProfileFolder = string.IsNullOrWhiteSpace(ProfileFolder) ? "" : ProfileFolder.Trim();
        var files = new List<string>();
        if (ExtraProfileFiles != null)
            foreach (var f in ExtraProfileFiles)
                if (!string.IsNullOrWhiteSpace(f) && files.Count < 500 && !files.Contains(f.Trim(), StringComparer.OrdinalIgnoreCase)) files.Add(f.Trim());
        ExtraProfileFiles = files;
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
            // "Presence" (2.1.1+); a 2.1.0 file has only the bool "ShowInTaskbar" (true = "both").
            // A present but invalid "Presence" becomes "tray" in Normalize.
            s.Presence = d.ContainsKey("Presence") ? Str(d, "Presence", PresenceMode.Tray)
                       : PresenceMode.FromLegacy(Bool(d, "ShowInTaskbar", false));
            s.BackupFolder = Str(d, "BackupFolder", s.BackupFolder);
            s.BackupFrequency = Str(d, "BackupFrequency", s.BackupFrequency);
            s.BackupKeep = Int(d, "BackupKeep", s.BackupKeep);
            long ticks = Long(d, "LastBackupUtcTicks", 0);
            s.LastBackupUtc = ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
            s.DockSide = Str(d, "DockSide", s.DockSide);
            s.Theme = Str(d, "Theme", s.Theme);
            s.ReachabilityCheck = Bool(d, "ReachabilityCheck", s.ReachabilityCheck);
            s.ShowForti = Bool(d, "ShowForti", s.ShowForti);
            s.ShowSophos = Bool(d, "ShowSophos", s.ShowSophos);
            s.ShowWatchGuard = Bool(d, "ShowWatchGuard", s.ShowWatchGuard);
            s.ShowFrequent = Bool(d, "ShowFrequent", s.ShowFrequent);
            s.UpdateMode = Str(d, "UpdateMode", s.UpdateMode);
            s.UpdateInterval = Str(d, "UpdateInterval", s.UpdateInterval);
            long uticks = Long(d, "LastUpdateCheckUtcTicks", 0);
            s.LastUpdateCheckUtc = uticks > 0 && uticks <= DateTime.MaxValue.Ticks ? new DateTime(uticks, DateTimeKind.Utc) : DateTime.MinValue;
            s.SkippedVersion = Str(d, "SkippedVersion", s.SkippedVersion);
            s.LogEnabled = Bool(d, "LogEnabled", s.LogEnabled);
            s.CrashPrompt = Str(d, "CrashPrompt", s.CrashPrompt);
            s.CrashAutoConsent = Bool(d, "CrashAutoConsent", s.CrashAutoConsent);
            s.ProfileFolder = Str(d, "ProfileFolder", s.ProfileFolder);         // 2.1.2; missing = ""
            s.ExtraProfileFiles = StrList(d, "ExtraProfileFiles");              // 2.1.2; missing or wrong type = empty
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
        d["Presence"] = n.Presence;   // the legacy "ShowInTaskbar" key is read, no longer written
        d["BackupFolder"] = n.BackupFolder;
        d["BackupFrequency"] = n.BackupFrequency;
        d["BackupKeep"] = n.BackupKeep;
        d["LastBackupUtcTicks"] = n.LastBackupUtc.Kind == DateTimeKind.Local ? n.LastBackupUtc.ToUniversalTime().Ticks : n.LastBackupUtc.Ticks;
        d["DockSide"] = n.DockSide;
        d["Theme"] = n.Theme;
        d["ReachabilityCheck"] = n.ReachabilityCheck;
        d["ShowForti"] = n.ShowForti;
        d["ShowSophos"] = n.ShowSophos;
        d["ShowWatchGuard"] = n.ShowWatchGuard;
        d["ShowFrequent"] = n.ShowFrequent;
        d["UpdateMode"] = n.UpdateMode;
        d["UpdateInterval"] = n.UpdateInterval;
        d["LastUpdateCheckUtcTicks"] = n.LastUpdateCheckUtc.Kind == DateTimeKind.Local ? n.LastUpdateCheckUtc.ToUniversalTime().Ticks : n.LastUpdateCheckUtc.Ticks;
        d["SkippedVersion"] = n.SkippedVersion;
        d["LogEnabled"] = n.LogEnabled;
        d["CrashPrompt"] = n.CrashPrompt;
        d["CrashAutoConsent"] = n.CrashAutoConsent;
        d["ProfileFolder"] = n.ProfileFolder;
        d["ExtraProfileFiles"] = n.ExtraProfileFiles.ToArray();
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

    // Strings of a JSON array; anything else (missing, wrong type) gives an empty list.
    static List<string> StrList(IDictionary<string, object> d, string k)
    {
        var list = new List<string>();
        object v;
        if (!d.TryGetValue(k, out v) || v == null || v is string) return list;
        var items = v as System.Collections.IEnumerable;
        if (items == null || v is IDictionary<string, object>) return list;
        foreach (var x in items) { var s = x as string; if (s != null) list.Add(s); }
        return list;
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

    // Stored theme ids (AppSettings.Theme). Order = Settings > Görünüm tiles and the setup wizard's segments.
    public const string Ocean = "ocean";
    public static readonly string[] Ids = { "dark", "light", "system", Ocean };

    public static bool IsValid(string theme) { return Array.IndexOf(Ids, theme) >= 0; }
    // Tile / segment index of a stored id (unknown ids show as the default, Glass Dark) and back.
    public static int IndexOf(string theme) { return Math.Max(0, Array.IndexOf(Ids, theme)); }
    public static string FromIndex(int index) { return index >= 0 && index < Ids.Length ? Ids[index] : "dark"; }

    // "light" = light, "system" = follow Windows apps setting (light or Glass Dark), "ocean" = Derin Mavi
    // (a dark theme: only the palette differs), anything else = dark.
    public static void Apply(string theme)
    {
        var light = theme == "light" || (theme == "system" && SystemPrefersLight());
        if (light)
        {
            // Liquid Glass Light: pale slate glass, same sapphire accent family
            Set(Color.FromArgb(230, 235, 242), Color.FromArgb(250, 252, 255), Color.FromArgb(238, 242, 248),
                Color.FromArgb(214, 226, 241), Color.FromArgb(206, 214, 225), Color.FromArgb(176, 188, 204),
                Color.FromArgb(60, 98, 146), Color.FromArgb(66, 104, 152), Color.FromArgb(83, 123, 169),
                Color.FromArgb(28, 34, 44), Color.FromArgb(78, 91, 108), Color.FromArgb(112, 125, 142),
                Color.FromArgb(160, 120, 56), Color.FromArgb(52, 142, 104));
            Ui.Danger = Color.FromArgb(176, 80, 80); Ui.Warning = Color.FromArgb(160, 120, 56); Ui.Field = Color.FromArgb(255, 255, 255);
            Ui.Highlight = Color.FromArgb(66, 104, 152);
            Ui.GlassPrimary = Color.FromArgb(250, 252, 255); Ui.GlassSecondary = Color.FromArgb(238, 242, 248); Ui.WindowBg = Color.FromArgb(230, 235, 242);
            Ui.GlassFloating = Color.FromArgb(246, 249, 253); Ui.GlassSelected = Color.FromArgb(214, 226, 241);
            Ui.WindowTop = Color.FromArgb(242, 246, 251); Ui.WindowBottom = Color.FromArgb(222, 229, 238);
            Ui.MenuTint = Color.FromArgb(214, 248, 249, 253); Ui.MenuSolid = Color.FromArgb(250, 251, 254); Ui.MenuFrame = Color.FromArgb(0xCC, 0xD2, 0xD8);
        }
        else if (theme == Ocean)
        {
            // Derin Mavi: deep navy / blue-slate glass, soft blue rims, teal-green connected state.
            // Values measured from the reference panel (docs/plans/theme-report.md has the table).
            OceanPalette();
        }
        else
        {
            // Liquid Glass Dark (default): graphite / slate. Values are the approved palette.
            Set(Color.FromArgb(0x1C, 0x22, 0x2C), Color.FromArgb(0x30, 0x39, 0x45), Color.FromArgb(0x3A, 0x45, 0x53),
                Color.FromArgb(0x2C, 0x40, 0x58), Color.FromArgb(0x3C, 0x48, 0x57), Color.FromArgb(0x58, 0x66, 0x77),
                Color.FromArgb(0x53, 0x7B, 0xA9), Color.FromArgb(0x53, 0x7B, 0xA9), Color.FromArgb(0x8B, 0xAE, 0xD9),
                Color.FromArgb(0xE5, 0xEA, 0xF1), Color.FromArgb(0xA1, 0xAF, 0xBF), Color.FromArgb(0x7E, 0x8C, 0x9D),
                Color.FromArgb(0xC4, 0xA1, 0x6E), Color.FromArgb(0x64, 0xBD, 0x96));
            Ui.Danger = Color.FromArgb(0xC4, 0x78, 0x78); Ui.Warning = Color.FromArgb(0xC4, 0xA1, 0x6E); Ui.Field = Color.FromArgb(0x26, 0x30, 0x3C);
            Ui.Highlight = Color.FromArgb(0x8B, 0xAE, 0xD9);
            Ui.GlassPrimary = Color.FromArgb(0x30, 0x39, 0x45); Ui.GlassSecondary = Color.FromArgb(0x26, 0x30, 0x3C); Ui.WindowBg = Color.FromArgb(0x1C, 0x22, 0x2C);
            Ui.GlassFloating = Color.FromArgb(0x2A, 0x33, 0x3F); Ui.GlassSelected = Color.FromArgb(0x3A, 0x4B, 0x60);
            Ui.WindowTop = Color.FromArgb(0x2A, 0x33, 0x40); Ui.WindowBottom = Color.FromArgb(0x18, 0x1E, 0x27);
            Ui.MenuTint = Color.FromArgb(200, 22, 25, 36); Ui.MenuSolid = Color.FromArgb(30, 33, 46); Ui.MenuFrame = Color.FromArgb(0x46, 0x4C, 0x50);
        }
        IsLight = light;
    }

    static void OceanPalette()
    {
        Set(C(0x1A212C), C(0x232A37), C(0x2B3443),          // bg, surface, surface hover
            C(0x2E3E52), C(0x2C3541), C(0x4E5E72),          // selected fill, border, strong border
            C(0x5980AD), C(0x5980AD), C(0x8BAED9),          // brand, accent, accent 2
            C(0xEDEDED), C(0xCDCDCD), C(0x94A3B7),          // text, muted, faint
            C(0xB7976B), C(0x65C299));                      // gold, success
        Ui.Danger = C(0xD88A8E); Ui.Warning = C(0xB7976B); Ui.Field = C(0x303A47);
        Ui.Highlight = C(0x8BAED9);
        Ui.GlassPrimary = C(0x263140); Ui.GlassSecondary = C(0x192431); Ui.WindowBg = C(0x172231);
        Ui.GlassFloating = C(0x303A47); Ui.GlassSelected = C(0x293A4C);
        Ui.WindowTop = C(0x16263A); Ui.WindowBottom = C(0x162030);
        Ui.MenuTint = Color.FromArgb(200, C(0x141A24)); Ui.MenuSolid = C(0x1C2330); Ui.MenuFrame = C(0x3A4656);
    }

    static Color C(int rgb) { return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }

    // WCAG 2.x relative luminance and contrast ratio (1..21) of two opaque colors.
    public static double Luminance(Color c)
    {
        Func<int, double> lin = v => { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); };
        return 0.2126 * lin(c.R) + 0.7152 * lin(c.G) + 0.0722 * lin(c.B);
    }

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
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

