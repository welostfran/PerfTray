// Lucide-Icons (https://lucide.dev, ISC-Lizenz, siehe THIRD_PARTY_NOTICES.md) - dieselben Icons, die shadcn/ui verwendet.
// Die SVG-Pfade sind aus lucide-static 1.54.0 uebernommen und werden hier mit GDI+ gezeichnet.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace PerfTray
{
    static class Lucide
    {
        static readonly Dictionary<string, string[]> Data = new Dictionary<string, string[]>
        {
            { "house", new[] {
                "M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8",
                "M3 10a2 2 0 0 1 .709-1.528l7-6a2 2 0 0 1 2.582 0l7 6A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" } },
            { "download", new[] {
                "M12 15V3",
                "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4",
                "m7 10 5 5 5-5" } },
            { "file-text", new[] {
                "M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2z",
                "M14 2v5a1 1 0 0 0 1 1h5",
                "M10 9H8", "M16 13H8", "M16 17H8" } },
            { "image", new[] {
                Rect(3, 3, 18, 18, 2), Circle(9, 9, 2),
                "m21 15-3.086-3.086a2 2 0 0 0-2.828 0L6 21" } },
            { "monitor", new[] {
                Rect(2, 3, 20, 14, 2), "M8 21H16", "M12 17V21" } },
            { "music", new[] {
                "M9 18V5l12-2v13", Circle(6, 18, 3), Circle(18, 16, 3) } },
            { "video", new[] {
                "m16 13 5.223 3.482a.5.5 0 0 0 .777-.416V7.87a.5.5 0 0 0-.752-.432L16 10.5",
                Rect(2, 6, 14, 12, 2) } },
            { "folder", new[] {
                "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z" } },
            { "trash-2", new[] {
                "M10 11v6", "M14 11v6",
                "M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6",
                "M3 6h18",
                "M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" } },
            { "hard-drive", new[] {
                "M10 16h.01",
                "M2.212 11.577a2 2 0 0 0-.212.896V18a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-5.527a2 2 0 0 0-.212-.896L18.55 5.11A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z",
                "M21.946 12.013H2.054",
                "M6 16h.01" } },
        };

        static readonly Dictionary<string, GraphicsPath> cache = new Dictionary<string, GraphicsPath>();

        // Zeichnet das Icon (24x24-Raster, Strichstaerke 2 wie im Original) in das Zielrechteck
        public static void Draw(Graphics g, string name, RectangleF dest, Color color)
        {
            GraphicsPath gp = Get(name);
            float s = Math.Min(dest.Width, dest.Height) / 24f;
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(dest.X, dest.Y);
            g.ScaleTransform(s, s);
            using (var pen = new Pen(color, 2f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawPath(pen, gp);
            }
            g.Restore(state);
        }

        public static Bitmap ToBitmap(string name, int size, Color color)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
                Draw(g, name, new RectangleF(0, 0, size, size), color);
            return bmp;
        }

        static GraphicsPath Get(string name)
        {
            GraphicsPath gp;
            if (cache.TryGetValue(name, out gp)) return gp;
            gp = new GraphicsPath();
            string[] parts;
            if (!Data.TryGetValue(name, out parts)) parts = Data["folder"];
            foreach (var d in parts) AddSvgPath(gp, d);
            cache[name] = gp;
            return gp;
        }

        // ---------------- SVG-Grundformen als Pfad ----------------

        static string Rect(float x, float y, float w, float h, float r)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "M{0} {1}h{2}a{3} {3} 0 0 1 {3} {3}v{4}a{3} {3} 0 0 1 -{3} {3}h-{2}a{3} {3} 0 0 1 -{3} -{3}v-{4}a{3} {3} 0 0 1 {3} -{3}z",
                x + r, y, w - 2 * r, r, h - 2 * r);
        }

        static string Circle(float cx, float cy, float r)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "M{0} {1}a{2} {2} 0 1 0 {3} 0a{2} {2} 0 1 0 -{3} 0z", cx - r, cy, r, 2 * r);
        }

        // ---------------- Minimaler SVG-Pfad-Parser ----------------

        static List<string> Tokenize(string d)
        {
            var toks = new List<string>();
            int i = 0;
            while (i < d.Length)
            {
                char c = d[i];
                if (char.IsLetter(c)) { toks.Add(c.ToString()); i++; continue; }
                if (c == ' ' || c == ',' || c == '\t' || c == '\n' || c == '\r') { i++; continue; }
                int start = i;
                bool dot = false;
                if (c == '-' || c == '+') i++;
                while (i < d.Length)
                {
                    char ch = d[i];
                    if (char.IsDigit(ch)) { i++; continue; }
                    if (ch == '.' && !dot) { dot = true; i++; continue; }
                    if ((ch == 'e' || ch == 'E') && i + 1 < d.Length) { i++; if (d[i] == '-' || d[i] == '+') i++; continue; }
                    break;
                }
                toks.Add(d.Substring(start, i - start));
            }
            return toks;
        }

        static void AddSvgPath(GraphicsPath gp, string d)
        {
            var t = Tokenize(d);
            int i = 0;
            char cmd = ' ', prev = ' ';
            PointF cur = PointF.Empty, start = PointF.Empty, lastCtrl = PointF.Empty;
            Func<float> num = () => float.Parse(t[i++], CultureInfo.InvariantCulture);

            while (i < t.Count)
            {
                if (char.IsLetter(t[i][0])) cmd = t[i++][0];
                else if (cmd == ' ' || char.ToUpper(cmd) == 'Z') break; // ungueltig
                bool rel = char.IsLower(cmd);
                char C = char.ToUpper(cmd);
                float ox = rel ? cur.X : 0, oy = rel ? cur.Y : 0;
                switch (C)
                {
                    case 'M':
                        {
                            var p = new PointF(ox + num(), oy + num());
                            gp.StartFigure();
                            cur = start = p;
                            cmd = rel ? 'l' : 'L'; // weitere Koordinaten = Linien
                            break;
                        }
                    case 'L':
                        {
                            var p = new PointF(ox + num(), oy + num());
                            gp.AddLine(cur, p); cur = p;
                            break;
                        }
                    case 'H':
                        {
                            var p = new PointF(ox + num(), cur.Y);
                            gp.AddLine(cur, p); cur = p;
                            break;
                        }
                    case 'V':
                        {
                            var p = new PointF(cur.X, oy + num());
                            gp.AddLine(cur, p); cur = p;
                            break;
                        }
                    case 'C':
                        {
                            var c1 = new PointF(ox + num(), oy + num());
                            var c2 = new PointF(ox + num(), oy + num());
                            var p = new PointF(ox + num(), oy + num());
                            gp.AddBezier(cur, c1, c2, p);
                            lastCtrl = c2; cur = p;
                            break;
                        }
                    case 'S':
                        {
                            var c1 = (prev == 'C' || prev == 'S') ? new PointF(2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y) : cur;
                            var c2 = new PointF(ox + num(), oy + num());
                            var p = new PointF(ox + num(), oy + num());
                            gp.AddBezier(cur, c1, c2, p);
                            lastCtrl = c2; cur = p;
                            break;
                        }
                    case 'Q':
                        {
                            var q = new PointF(ox + num(), oy + num());
                            var p = new PointF(ox + num(), oy + num());
                            gp.AddBezier(cur, Lerp(cur, q, 2f / 3), Lerp(p, q, 2f / 3), p);
                            cur = p;
                            break;
                        }
                    case 'A':
                        {
                            float rx = num(), ry = num(), rot = num();
                            bool large = num() != 0, sweep = num() != 0;
                            var p = new PointF(ox + num(), oy + num());
                            AddArc(gp, cur, rx, ry, rot, large, sweep, p);
                            cur = p;
                            break;
                        }
                    case 'Z':
                        gp.CloseFigure();
                        cur = start;
                        break;
                    default:
                        return; // nicht unterstuetzt
                }
                prev = C;
            }
        }

        static PointF Lerp(PointF a, PointF b, float t)
        {
            return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        // SVG-Bogen -> Bezier-Kurven (SVG-Spezifikation, Anhang F.6.5)
        static void AddArc(GraphicsPath gp, PointF p0, float rxf, float ryf, float angleDeg, bool large, bool sweep, PointF p1)
        {
            double rx = Math.Abs(rxf), ry = Math.Abs(ryf);
            if (rx == 0 || ry == 0 || (p0.X == p1.X && p0.Y == p1.Y)) { gp.AddLine(p0, p1); return; }
            double phi = angleDeg * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dx = (p0.X - p1.X) / 2.0, dy = (p0.Y - p1.Y) / 2.0;
            double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
            double lam = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
            if (lam > 1) { rx *= Math.Sqrt(lam); ry *= Math.Sqrt(lam); }
            double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
            double den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
            double co = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den));
            if (large == sweep) co = -co;
            double cxp = co * rx * y1 / ry, cyp = -co * ry * x1 / rx;
            double cx = cos * cxp - sin * cyp + (p0.X + p1.X) / 2.0;
            double cy = sin * cxp + cos * cyp + (p0.Y + p1.Y) / 2.0;
            double th1 = Angle(1, 0, (x1 - cxp) / rx, (y1 - cyp) / ry);
            double dth = Angle((x1 - cxp) / rx, (y1 - cyp) / ry, (-x1 - cxp) / rx, (-y1 - cyp) / ry);
            if (!sweep && dth > 0) dth -= 2 * Math.PI;
            else if (sweep && dth < 0) dth += 2 * Math.PI;

            int segs = Math.Max(1, (int)Math.Ceiling(Math.Abs(dth) / (Math.PI / 2) - 1e-9));
            double delta = dth / segs, k = 4.0 / 3 * Math.Tan(delta / 4);
            Func<double, double, PointF> map = (u, v) => new PointF(
                (float)(cx + rx * u * cos - ry * v * sin),
                (float)(cy + rx * u * sin + ry * v * cos));
            PointF prevPt = p0;
            for (int s = 0; s < segs; s++)
            {
                double a1 = th1 + s * delta, a2 = a1 + delta;
                double c1 = Math.Cos(a1), s1 = Math.Sin(a1), c2 = Math.Cos(a2), s2 = Math.Sin(a2);
                PointF q1 = map(c1 - k * s1, s1 + k * c1);
                PointF q2 = map(c2 + k * s2, s2 - k * c2);
                PointF q3 = s == segs - 1 ? p1 : map(c2, s2);
                gp.AddBezier(prevPt, q1, q2, q3);
                prevPt = q3;
            }
        }

        static double Angle(double ux, double uy, double vx, double vy)
        {
            return Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        }
    }
}
