using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

// =====================================================================
// Central vector asset library. Every icon is an SVG file in assets\ (24x24 viewBox, stroke icons
// with currentColor), embedded in the exe as a resource. The loader below parses the SVG subset the
// library uses (path M/L/H/V/C/Z, circle, rect, line, polyline, polygon) into GraphicsPath objects once,
// then draws them at any size and in any color, so icons stay sharp at every DPI.
// Raster images are never used for small icons.
// =====================================================================
internal static class Assets
{
    sealed class Shape { public GraphicsPath Path; public bool Fill, Stroke; public float Width; public float Opacity = 1F; }
    sealed class Icon { public List<Shape> Shapes = new List<Shape>(); public float Box = 24F; }

    static readonly Dictionary<string, Icon> cache = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
    static readonly Regex Token = new Regex(@"[MmLlHhVvCcSsZz]|-?\d*\.?\d+(?:e-?\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool Has(string name) { return Get(name) != null; }

    // Draws asset `name` centered in r, scaled to fit, in color c (currentColor). strokeScale thickens/thins lines.
    public static void Draw(Graphics g, string name, RectangleF r, Color c, float strokeScale = 1F, bool fillAll = false)
    {
        var icon = Get(name);
        if (icon == null || r.Width <= 0 || r.Height <= 0) return;
        var k = Math.Min(r.Width, r.Height) / icon.Box;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(r.X + (r.Width - icon.Box * k) / 2F, r.Y + (r.Height - icon.Box * k) / 2F);
        g.ScaleTransform(k, k);
        foreach (var s in icon.Shapes)
        {
            var col = s.Opacity >= 1F ? c : Color.FromArgb((int)(c.A * s.Opacity), c);
            if (s.Fill || (fillAll && s.Stroke)) using (var b = new SolidBrush(Color.FromArgb((int)(col.A * (s.Fill ? 1F : 0.9F)), col))) g.FillPath(b, s.Path);
            if (s.Stroke) using (var p = new Pen(col, s.Width * strokeScale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) g.DrawPath(p, s.Path);
        }
        g.Restore(state);
    }

    static Icon Get(string name)
    {
        Icon icon;
        if (cache.TryGetValue(name, out icon)) return icon;
        icon = Load(name);
        cache[name] = icon;
        return icon;
    }

    static string ReadSvg(string name)
    {
        try
        {
            using (var s = typeof(Assets).Assembly.GetManifestResourceStream(name + ".svg"))
                if (s != null) using (var rd = new StreamReader(s)) return rd.ReadToEnd();
        }
        catch { }
        // development fallback: assets folder next to the exe or the sources
        string here = null; try { here = Path.GetDirectoryName(typeof(Assets).Assembly.Location); } catch { }
        foreach (var dir in new[] { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "assets"), here == null ? "" : Path.Combine(here, "assets"), here == null ? "" : Path.Combine(here, "..", "assets") })
        {
            try { var f = Path.Combine(dir, name + ".svg"); if (File.Exists(f)) return File.ReadAllText(f); } catch { }
        }
        return null;
    }

    static Icon Load(string name)
    {
        var text = ReadSvg(name);
        if (text == null) return null;
        try
        {
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(text);
            var root = doc.DocumentElement; var icon = new Icon();
            var vb = root.GetAttribute("viewBox");
            if (!string.IsNullOrEmpty(vb)) { var p = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries); if (p.Length == 4) icon.Box = Num(p[2]); }
            var rootW = Num(root.GetAttribute("stroke-width"), 1.75F);
            foreach (XmlNode n in root.ChildNodes)
            {
                var e = n as XmlElement; if (e == null) continue;
                var path = new GraphicsPath(FillMode.Winding);
                switch (e.LocalName)
                {
                    case "path": ParsePath(path, e.GetAttribute("d")); break;
                    case "circle": { var cx = Num(e.GetAttribute("cx")); var cy = Num(e.GetAttribute("cy")); var r = Num(e.GetAttribute("r")); path.AddEllipse(cx - r, cy - r, 2 * r, 2 * r); break; }
                    case "rect": { var x = Num(e.GetAttribute("x")); var y = Num(e.GetAttribute("y")); var w = Num(e.GetAttribute("width")); var h = Num(e.GetAttribute("height")); var rx = Num(e.GetAttribute("rx")); using (var rp = Ui.Round(new RectangleF(x, y, w, h), rx)) path.AddPath(rp, false); break; }
                    case "line": path.AddLine(Num(e.GetAttribute("x1")), Num(e.GetAttribute("y1")), Num(e.GetAttribute("x2")), Num(e.GetAttribute("y2"))); break;
                    case "polyline": case "polygon":
                        {
                            var nums = e.GetAttribute("points").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries); var pts = new List<PointF>();
                            for (var i = 0; i + 1 < nums.Length; i += 2) pts.Add(new PointF(Num(nums[i]), Num(nums[i + 1])));
                            if (pts.Count > 1) path.AddLines(pts.ToArray());
                            if (e.LocalName == "polygon") path.CloseFigure();
                            break;
                        }
                    default: continue;
                }
                var fill = e.GetAttribute("fill");
                icon.Shapes.Add(new Shape
                {
                    Path = path, Fill = fill == "currentColor", Stroke = e.GetAttribute("stroke") != "none" && fill != "currentColor" || e.GetAttribute("stroke") == "currentColor",
                    Width = Num(e.GetAttribute("stroke-width"), rootW), Opacity = Num(e.GetAttribute("opacity"), 1F)
                });
            }
            return icon;
        }
        catch { return null; }
    }

    static float Num(string s, float def = 0F)
    {
        float v; return !string.IsNullOrEmpty(s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
    }

    // Path data: M m L l H h V v C c S s Z z (absolute and relative).
    static void ParsePath(GraphicsPath gp, string d)
    {
        if (string.IsNullOrEmpty(d)) return;
        var t = new List<string>(); foreach (Match m in Token.Matches(d)) t.Add(m.Value);
        int i = 0; char cmd = 'M'; PointF cur = PointF.Empty, start = PointF.Empty, lastCtl = PointF.Empty; var haveCtl = false;
        Func<float> N = () => float.Parse(t[i++], CultureInfo.InvariantCulture);
        while (i < t.Count)
        {
            if (char.IsLetter(t[i][0])) cmd = t[i++][0];
            var rel = char.IsLower(cmd); var ox = rel ? cur.X : 0F; var oy = rel ? cur.Y : 0F;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M': { var p = new PointF(ox + N(), oy + N()); gp.StartFigure(); cur = start = p; cmd = rel ? 'l' : 'L'; haveCtl = false; break; }
                case 'L': { var p = new PointF(ox + N(), oy + N()); gp.AddLine(cur, p); cur = p; haveCtl = false; break; }
                case 'H': { var p = new PointF(ox + N(), cur.Y); gp.AddLine(cur, p); cur = p; haveCtl = false; break; }
                case 'V': { var p = new PointF(cur.X, oy + N()); gp.AddLine(cur, p); cur = p; haveCtl = false; break; }
                case 'C': { var c1 = new PointF(ox + N(), oy + N()); var c2 = new PointF(ox + N(), oy + N()); var p = new PointF(ox + N(), oy + N()); gp.AddBezier(cur, c1, c2, p); cur = p; lastCtl = c2; haveCtl = true; break; }
                case 'S': { var c1 = haveCtl ? new PointF(2 * cur.X - lastCtl.X, 2 * cur.Y - lastCtl.Y) : cur; var c2 = new PointF(ox + N(), oy + N()); var p = new PointF(ox + N(), oy + N()); gp.AddBezier(cur, c1, c2, p); cur = p; lastCtl = c2; haveCtl = true; break; }
                case 'Z': gp.CloseFigure(); cur = start; haveCtl = false; break;
                default: return;
            }
        }
    }
}
