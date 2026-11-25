using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// =====================================================================
// Liquid Glass: one shared material system, backdrop (DWM) support,
// spring motion and alpha-correct text. Every glass surface in the app
// is painted through Glass.Paint so the look stays consistent.
// =====================================================================

internal enum GlassKind { Primary, Secondary, Floating, Selected }

// ---------------------------------------------------------------------
// Motion: spring integrator + reduced-motion switch
// ---------------------------------------------------------------------
internal static class Motion
{
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

    // True when the user turned animations off in Windows (Settings > Accessibility > Visual effects).
    public static bool Reduced { get; private set; }

    public static void Refresh()
    {
        try { bool on; if (SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out on, 0)) Reduced = !on; } catch { }
    }

    static Motion() { Refresh(); SystemEvents.UserPreferenceChanged += delegate { Refresh(); }; }
}

// Damped spring. Step() returns true while the value is still moving.
internal sealed class Spring
{
    public float Value, Target, Velocity;
    readonly float stiffness, damping;

    public Spring(float value, float stiffness = 420F, float damping = 30F) { Value = Target = value; this.stiffness = stiffness; this.damping = damping; }

    public bool Settled { get { return Math.Abs(Value - Target) < 0.002F && Math.Abs(Velocity) < 0.01F; } }

    public void Snap(float v) { Value = Target = v; Velocity = 0; }

    public bool Step(float dt)
    {
        if (Motion.Reduced) { var moved = Value != Target; Snap(Target); return moved; }
        if (Settled) { if (Value != Target) { Value = Target; Velocity = 0; return true; } return false; }
        if (dt > 0.05F) dt = 0.05F;
        // small fixed substeps keep the integration stable at low frame rates
        var n = Math.Max(1, (int)Math.Ceiling(dt / 0.004F)); var h = dt / n;
        for (var i = 0; i < n; i++)
        {
            var a = -stiffness * (Value - Target) - damping * Velocity;
            Velocity += a * h; Value += Velocity * h;
        }
        return true;
    }
}

// Shared 60 fps clock for spring animations. Stopped automatically when nothing animates.
internal sealed class Animator : IDisposable
{
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15 };
    readonly System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
    double last;
    public Func<float, bool> Tick;   // returns true while still animating
    public int Interval { get { return timer.Interval; } set { timer.Interval = value; } }

    public Animator() { timer.Tick += delegate { OnTick(); }; }
    public void Kick() { if (!timer.Enabled) { last = watch.Elapsed.TotalSeconds; timer.Start(); } }
    public void Stop() { timer.Stop(); }
    public bool Running { get { return timer.Enabled; } }
    public float Time { get { return (float)watch.Elapsed.TotalSeconds; } }
    void OnTick()
    {
        var now = watch.Elapsed.TotalSeconds; var dt = (float)(now - last); last = now;
        if (Tick == null || !Tick(dt)) timer.Stop();
    }
    public void Dispose() { timer.Dispose(); }
}

// ---------------------------------------------------------------------
// Backdrop: real blur from the Windows 11 compositor, controlled fallback elsewhere
// ---------------------------------------------------------------------
internal static class Backdrop
{
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int l, r, t, b; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [StructLayout(LayoutKind.Sequential)] struct ACCENT { public int State, Flags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] struct WCA { public int Attribute; public IntPtr Data; public int Size; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WCA data);

    public static readonly int Build = ReadBuild();

    // Acrylic through the accent API, clipped by the window region (rounded shape of any radius).
    public static bool ApplyShaped(IntPtr hwnd, bool dark)
    {
        try
        {
            var d = dark ? 1 : 0; DwmSetWindowAttribute(hwnd, 20, ref d, 4);
            if (Build < 17134) { Active = false; return false; }
            var none = 1; DwmSetWindowAttribute(hwnd, 38, ref none, 4); // DWMSBT_NONE: the compositor backdrop is the accent one
            var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            var accent = new ACCENT { State = 4 /* ACCENT_ENABLE_ACRYLICBLURBEHIND */, Flags = 2, GradientColor = 0x01000000, AnimationId = 0 };
            var size = Marshal.SizeOf(typeof(ACCENT)); var p = Marshal.AllocHGlobal(size);
            try { Marshal.StructureToPtr(accent, p, false); var data = new WCA { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = p, Size = size }; Active = SetWindowCompositionAttribute(hwnd, ref data) != 0; }
            finally { Marshal.FreeHGlobal(p); }
            return Active;
        }
        catch { Active = false; return false; }
    }

    // DWMWA_SYSTEMBACKDROP_TYPE exists from Windows 11 22H2 (build 22621). Older systems use the fallback material.
    public static bool Supported { get { return Build >= 22621; } }

    // Set once a window actually got the compositor backdrop. Surfaces use it to pick their opacity.
    public static bool Active { get; private set; }

    static int ReadBuild()
    {
        try { int n; return int.TryParse(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "0") as string, out n) ? n : 0; }
        catch { return 0; }
    }

    // Acrylic (transient window) behind the whole window, dark/light title chrome, no DWM rounding
    // (the window shape comes from SetRoundedRegion so the radius can be larger than Windows' own).
    public static bool Apply(IntPtr hwnd, bool dark)
    {
        try
        {
            var d = dark ? 1 : 0; DwmSetWindowAttribute(hwnd, 20, ref d, 4);
            if (!Supported) { Active = false; return false; }
            var type = 3; // DWMSBT_TRANSIENTWINDOW = acrylic
            var ok = DwmSetWindowAttribute(hwnd, 38, ref type, 4) == 0;
            var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
            ok = ok && DwmExtendFrameIntoClientArea(hwnd, ref m) == 0;
            Active = ok;
            return ok;
        }
        catch { Active = false; return false; }
    }

    public static void SetCorner(IntPtr hwnd, int pref) { try { DwmSetWindowAttribute(hwnd, 33, ref pref, 4); var none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(hwnd, 34, ref none, 4); /* DWMWA_COLOR_NONE: no system border */ } catch { } }

    // Clips the window (and therefore the blur) to a rounded rectangle. Edges are re-drawn by Glass.WindowFrame.
    public static void SetRoundedRegion(Form f, int radius)
    {
        if (!f.IsHandleCreated || f.Width <= 0 || f.Height <= 0) return;
        var rgn = CreateRoundRectRgn(0, 0, f.Width + 1, f.Height + 1, radius * 2, radius * 2);
        SetWindowRgn(f.Handle, rgn, true); // the system owns the region from here on
    }
}

// ---------------------------------------------------------------------
// Taskbar button ("Görev çubuğunda göster"). The main window keeps WinForms' ShowInTaskbar = false
// (it is owned by WinForms' hidden taskbar-owner window, so it has no button). Instead of toggling
// ShowInTaskbar, which destroys and recreates the window handle (hotkey, WinEvent hooks and the
// DWM backdrop are bound to it), the WS_EX_APPWINDOW bit is flipped on the same handle: an owned
// window with that bit gets a taskbar button and an Alt+Tab entry. The shell reads the bit when the
// window is shown, so a visible window is hidden and shown again (without activation).
// ---------------------------------------------------------------------
internal enum TaskbarChange { None, StyleOnly, Reshow }

internal static class TaskbarButton
{
    public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    const int GWL_EXSTYLE = -20, SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNA = 8;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    // Whether the window should have a button. A demo run follows the stored setting only when a
    // settings file was passed (--settings=); otherwise it keeps today's behaviour (no button).
    public static bool Wanted(bool setting, bool demo, bool settingsFilePassed)
    {
        return setting && (!demo || settingsFilePassed);
    }

    // Extended style with / without a button. A button also needs WS_EX_TOOLWINDOW off; turning the
    // button off only drops WS_EX_APPWINDOW. Every other bit is kept.
    public static int ExStyle(int current, bool button)
    {
        return button ? (current | WS_EX_APPWINDOW) & ~WS_EX_TOOLWINDOW : current & ~WS_EX_APPWINDOW;
    }

    // What a live change needs: nothing (style already right), the new style only (hidden window:
    // the next Show picks it up), or the new style plus a hide / show cycle (visible window).
    public static TaskbarChange Plan(int current, bool button, bool visible)
    {
        if (ExStyle(current, button) == current) return TaskbarChange.None;
        return visible ? TaskbarChange.Reshow : TaskbarChange.StyleOnly;
    }

    // Applies the button state to an existing handle; the handle itself is never recreated.
    // A window that was in the foreground is shown activated again, any other one without activation.
    public static TaskbarChange Apply(IntPtr hwnd, bool button, bool visible)
    {
        if (hwnd == IntPtr.Zero) return TaskbarChange.None;
        var current = GetWindowLong(hwnd, GWL_EXSTYLE);
        var plan = Plan(current, button, visible);
        if (plan == TaskbarChange.None) return plan;
        var active = GetForegroundWindow() == hwnd;
        if (plan == TaskbarChange.Reshow) ShowWindow(hwnd, SW_HIDE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ExStyle(current, button));
        if (plan == TaskbarChange.Reshow) ShowWindow(hwnd, active ? SW_SHOW : SW_SHOWNA);
        return plan;
    }

    public static bool Has(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_APPWINDOW) != 0;
    }
}

// ---------------------------------------------------------------------
// Glass material
// ---------------------------------------------------------------------
internal static class Glass
{
    static bool Light { get { return Themes.IsLight; } }
    static Color W(int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), 255, 255, 255); }
    static Color K(int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), 0, 0, 0); }

    // Window tint over the compositor blur: Graphite/Slate (#1C222C) in dark.
    public static Color WindowTint { get { return Light ? Color.FromArgb(Backdrop.Active ? 178 : 255, 232, 237, 244) : Color.FromArgb(Backdrop.Active ? 212 : 255, 0x1C, 0x22, 0x2C); } }

    // Surface tints use the approved glass colors: Primary #303945, Secondary #26303C.
    static Color Tint(GlassKind kind, float amount)
    {
        int a; Color c;
        var solid = !Backdrop.Active;
        switch (kind)
        {
            case GlassKind.Primary:   c = Ui.GlassPrimary;   a = Light ? 175 : 168; break;
            case GlassKind.Secondary: c = Ui.GlassSecondary; a = Light ? 120 : 118; break;
            case GlassKind.Floating:  c = Light ? Color.FromArgb(246, 249, 253) : Color.FromArgb(0x2A, 0x33, 0x3F); a = Light ? 226 : 224; break;
            default:                  c = Light ? Color.FromArgb(214, 226, 241) : Color.FromArgb(0x3A, 0x4B, 0x60); a = Light ? 200 : 190; break;
        }
        if (solid) a = Math.Max(a, kind == GlassKind.Secondary ? 150 : 235);
        return Color.FromArgb((int)(a * Ease.Clamp01(amount)), c);
    }

    // One call paints a complete glass surface. amount fades the whole material in/out (0..1);
    // sheen (0..1) is the horizontal position of the moving reflection, or -1 for none.
    public static void Paint(Graphics g, RectangleF r, float radius, GlassKind kind, float amount = 1F, float sheen = -1F, bool flat = false)
    {
        if (amount <= 0.01F || r.Width < 2 || r.Height < 2) return;
        var oldS = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (!flat)
        using (var path = Ui.Round(r, radius))
        {
            // 1. translucent tint
            using (var b = new SolidBrush(Tint(kind, amount))) g.FillPath(b, path);

            var state = g.Save();
            g.SetClip(path, CombineMode.Intersect);

            // 2. subtle saturation: a faint accent wash (selected surfaces carry more of it)
            var wash = kind == GlassKind.Selected ? (Light ? 40 : 58) : kind == GlassKind.Primary ? (Light ? 6 : 10) : 0;
            if (wash > 0)
                using (var b = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb((int)(wash * amount), Ui.Highlight), Color.FromArgb(0, Ui.Highlight), LinearGradientMode.ForwardDiagonal))
                    g.FillRectangle(b, r);

            // 3. specular highlight along the top edge (light catching the rim)
            var hl = Math.Min(r.Height * 0.55F, Ui.S(44));
            using (var b = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, hl + 1), W((int)((Light ? 170 : (kind == GlassKind.Secondary ? 26 : 42)) * amount)), W(0), LinearGradientMode.Vertical))
                g.FillRectangle(b, r.X, r.Y, r.Width, hl);

            // 4. dynamic reflection: a soft diagonal band that follows the pointer / selection
            if (sheen >= 0 && kind != GlassKind.Secondary)
            {
                var bw = Math.Max(Ui.S(60), r.Width * 0.38F);
                var cx = r.X - bw * 0.3F + (r.Width + bw * 0.6F) * Ease.Clamp01(sheen);
                using (var b = new LinearGradientBrush(new RectangleF(cx - bw / 2, r.Y, bw, r.Height), Color.Transparent, Color.Transparent, 0F))
                {
                    b.InterpolationColors = new ColorBlend { Colors = new[] { W(0), W((int)((Light ? 120 : 26) * amount)), W(0) }, Positions = new[] { 0F, 0.5F, 1F } };
                    g.FillRectangle(b, cx - bw / 2, r.Y, bw, r.Height);
                }
            }
            g.Restore(state);
        }

        // 5. thin translucent border: bright on top, faint toward the bottom (optical depth)
        var br = new RectangleF(r.X + 0.5F, r.Y + 0.5F, r.Width - 1F, r.Height - 1F);
        using (var path = Ui.Round(br, Math.Max(1F, radius - 0.5F)))
        using (var lb = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.Transparent, Color.Transparent, LinearGradientMode.Vertical))
        {
            lb.InterpolationColors = new ColorBlend
            {
                Colors = Light ? new[] { W((int)(240 * amount)), W((int)(120 * amount)), K((int)(24 * amount)) } : new[] { W((int)(96 * amount)), W((int)(26 * amount)), W((int)(14 * amount)) },
                Positions = new[] { 0F, 0.45F, 1F }
            };
            using (var pen = new Pen(lb, 1F)) g.DrawPath(pen, path);
        }
        g.SmoothingMode = oldS;
    }

    // Soft ambient shadow below a floating surface (cheap stacked strokes; no per-frame blur).
    public static void Shadow(Graphics g, RectangleF r, float radius, int spread, int strength)
    {
        var oldS = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        for (var i = spread; i >= 1; i--)
        {
            var grow = i; var a = (int)(strength * Math.Pow(1 - (i - 1F) / spread, 2.2) / spread * 1.6);
            if (a <= 0) continue;
            using (var p = Ui.Round(new RectangleF(r.X - grow, r.Y - grow + spread * 0.45F, r.Width + 2 * grow, r.Height + 2 * grow), radius + grow))
            using (var b = new SolidBrush(K(a))) g.FillPath(b, p);
        }
        g.SmoothingMode = oldS;
    }

    // Layered window material: slate tint, a diagonal light sweep from the top-left, a soft top glow
    // and a darker lower-right. Child controls repaint the same layers at their own offset so the
    // window reads as one continuous sheet of glass.
    static void Layers(Graphics g, Size size, bool solidGradient)
    {
        var rect = new Rectangle(0, 0, size.Width, Math.Max(2, size.Height));
        if (Backdrop.Active)
        {
            using (var b = new SolidBrush(WindowTint)) g.FillRectangle(b, rect);
        }
        else
            using (var b = new LinearGradientBrush(rect, Light ? Color.FromArgb(242, 246, 251) : Color.FromArgb(0x2A, 0x33, 0x40), Light ? Color.FromArgb(222, 229, 238) : Color.FromArgb(0x18, 0x1E, 0x27), 62F))
                g.FillRectangle(b, rect);
        // light sweep
        using (var b = new LinearGradientBrush(rect, W(Light ? 90 : 30), W(0), 38F)) g.FillRectangle(b, rect);
        // soft top glow
        var glow = new Rectangle(-size.Width / 4, -size.Height / 3, size.Width * 3 / 2, size.Height * 2 / 3);
        using (var p = new GraphicsPath())
        {
            p.AddEllipse(glow);
            using (var pb = new PathGradientBrush(p) { CenterColor = W(Light ? 80 : 24), SurroundColors = new[] { W(0) } }) g.FillPath(pb, p);
        }
        // lower-right depth
        using (var b = new LinearGradientBrush(rect, K(0), K(Light ? 14 : 46), 62F)) g.FillRectangle(b, rect);
    }

    public static void WindowBase(Graphics g, Rectangle bounds, float radius)
    {
        var old = g.CompositingMode; var oldS = g.SmoothingMode;
        g.CompositingMode = CompositingMode.SourceCopy; g.Clear(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceOver; g.SmoothingMode = SmoothingMode.AntiAlias;
        Layers(g, bounds.Size, false);
        g.CompositingMode = old; g.SmoothingMode = oldS;
    }

    // Child controls: same window layers, drawn at the control's offset inside the form.
    public static void ChildBase(Graphics g, Control c)
    {
        var f = c.FindForm(); var off = f != null ? f.PointToClient(c.PointToScreen(Point.Empty)) : Point.Empty;
        var size = f != null ? f.ClientSize : c.Size;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceOver;
        g.TranslateTransform(-off.X, -off.Y);
        Layers(g, size, false);
        g.ResetTransform();
    }

    // Outer window edge: 1 px specular rim, bright at the top-left, fading to the lower-right. The window
    // shape itself comes from Windows (DWM rounding), so the edge is antialiased by the system.
    public static void WindowFrame(Graphics g, Rectangle bounds, float radius)
    {
        var oldS = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ui.Round(new RectangleF(0.5F, 0.5F, bounds.Width - 1.5F, bounds.Height - 1.5F), radius))
        using (var lb = new LinearGradientBrush(new Rectangle(0, 0, bounds.Width, bounds.Height), Color.Transparent, Color.Transparent, 45F))
        {
            lb.InterpolationColors = new ColorBlend { Colors = Light ? new[] { W(255), W(150), K(40) } : new[] { Color.FromArgb(170, Ui.Highlight), Color.FromArgb(44, Ui.Highlight), Color.FromArgb(70, Ui.Highlight) }, Positions = new[] { 0F, 0.5F, 1F } };
            using (var pen = new Pen(lb, 1F)) g.DrawPath(pen, path);
        }
        using (var path = Ui.Round(new RectangleF(1.5F, 1.5F, bounds.Width - 3.5F, bounds.Height - 3.5F), Math.Max(1F, radius - 1F)))
        using (var lb = new LinearGradientBrush(new Rectangle(0, 0, bounds.Width, bounds.Height), W(Light ? 120 : 26), W(0), 45F))
        using (var pen = new Pen(lb, 1F)) g.DrawPath(pen, path);
        g.SmoothingMode = oldS;
    }
}

// ---------------------------------------------------------------------
// Alpha-correct text. GDI text (TextRenderer) writes no alpha, so on the translucent window
// it would vanish or look dirty; GDI+ grayscale antialiasing keeps the alpha channel right.
// ---------------------------------------------------------------------
internal static class GText
{
    static readonly StringFormat Fmt = MakeFormat();
    static StringFormat MakeFormat()
    {
        var f = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoClip };
        f.Trimming = StringTrimming.None; return f;
    }

    static readonly Dictionary<string, Size> cache = new Dictionary<string, Size>();
    static Graphics measureG;

    public static Size Measure(string text, Font f)
    {
        if (string.IsNullOrEmpty(text)) return new Size(0, (int)Math.Ceiling(f.GetHeight(96F) * 0.99F));
        var key = f.Name + f.SizeInPoints.ToString(CultureInfo.InvariantCulture) + (int)f.Style + "\u0001" + text;
        Size s; if (cache.TryGetValue(key, out s)) return s;
        if (cache.Count > 3000) cache.Clear();
        if (measureG == null) { measureG = Graphics.FromHwnd(IntPtr.Zero); measureG.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; }
        var sz = measureG.MeasureString(text, f, int.MaxValue, Fmt);
        s = new Size((int)Math.Ceiling(sz.Width), (int)Math.Ceiling(sz.Height));
        cache[key] = s; return s;
    }

    public static Size Measure(Graphics g, string text, Font f) { return Measure(text, f); }

    public static void Draw(Graphics g, string text, Font f, Point at, Color c)
    {
        if (string.IsNullOrEmpty(text)) return;
        var old = g.TextRenderingHint; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var b = new SolidBrush(c)) g.DrawString(text, f, b, at.X, at.Y, Fmt);
        g.TextRenderingHint = old;
    }

    // Draws inside a rectangle: vertically centered, optional horizontal centering/right alignment, ellipsis on overflow.
    public static void Draw(Graphics g, string text, Font f, Rectangle r, Color c, bool center = false, bool right = false)
    {
        if (string.IsNullOrEmpty(text) || r.Width <= 0 || r.Height <= 0) return;
        var sz = Measure(text, f);
        if (sz.Width > r.Width) text = Ellipsize(text, f, r.Width);
        sz = Measure(text, f);
        var x = center ? r.X + (r.Width - sz.Width) / 2 : right ? r.Right - sz.Width : r.X;
        Draw(g, text, f, new Point(x, r.Y + (r.Height - sz.Height) / 2), c);
    }

    public static string Ellipsize(string text, Font f, int width)
    {
        if (Measure(text, f).Width <= width) return text;
        var lo = 0; var hi = text.Length;
        while (lo < hi) { var mid = (lo + hi + 1) / 2; if (Measure(text.Substring(0, mid) + "…", f).Width <= width) lo = mid; else hi = mid - 1; }
        return lo <= 0 ? "…" : text.Substring(0, lo).TrimEnd() + "…";
    }
}
