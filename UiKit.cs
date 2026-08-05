// palette, type scale, drawing primitives. nothing in here knows about roblox.
//
// everything is in logical 96dpi units and MainForm applies one ScaleTransform.
// that's why the fonts are in PIXELS - a point font would get scaled twice on a
// high dpi screen and look wrong.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Archon
{
    static class Skin
    {
        public static readonly Color Base = Rgb(0x0A, 0x0A, 0x0A);
        public static readonly Color Sunken = Rgb(0x0C, 0x0C, 0x0C);

        public static readonly Color Card = Rgb(0x15, 0x15, 0x15);
        public static readonly Color CardLit = Rgb(0x1C, 0x1C, 0x1C);

        public static readonly Color Line = Rgb(0x26, 0x26, 0x26);
        public static readonly Color LineSoft = Rgb(0x19, 0x19, 0x19);

        public static readonly Color TextHi = Rgb(0xF5, 0xF5, 0xF5);
        public static readonly Color TextMid = Rgb(0x9C, 0x9C, 0x9C);
        public static readonly Color TextLo = Rgb(0x6C, 0x6C, 0x6C);
        public static readonly Color TextFaint = Rgb(0x48, 0x48, 0x48);

        public static readonly Color Accent = TextHi;
        public static readonly Color AccentDeep = Rgb(0xAF, 0xAF, 0xAF);

        public static Color Rgb(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        public static Color A(Color c, int alpha)
        {
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            return Color.FromArgb(alpha, c);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(
                (int)(a.A + ((b.A - a.A) * t)),
                (int)(a.R + ((b.R - a.R) * t)),
                (int)(a.G + ((b.G - a.G) * t)),
                (int)(a.B + ((b.B - a.B) * t)));
        }


        private static readonly string[] SemiBold =
            { "Segoe UI Variable Display Semib", "Segoe UI Semibold", "Segoe UI" };
        private static readonly string[] Regular =
            { "Segoe UI Variable Text", "Segoe UI" };
        private static readonly string[] Monospace =
            { "Cascadia Mono", "Consolas", "Courier New" };

        public static readonly Font H1 = Pick(SemiBold, 31f);
        public static readonly Font H2 = Pick(SemiBold, 19f);
        public static readonly Font H3 = Pick(SemiBold, 14.5f);
        public static readonly Font Body = Pick(Regular, 13f);
        public static readonly Font BodyMed = Pick(SemiBold, 13f);
        public static readonly Font Small = Pick(Regular, 11.5f);
        public static readonly Font SmallMed = Pick(SemiBold, 11.5f);
        public static readonly Font Micro = Pick(SemiBold, 9.5f);
        public static readonly Font Num = Pick(SemiBold, 13.5f);
        public static readonly Font Mono = Pick(Monospace, 11.5f);

        private static HashSet<string> _installed;

        // gdi+ doesn't throw on a missing font family, it just silently gives
        // you Microsoft Sans Serif, so check the list instead of trying them
        private static Font Pick(string[] families, float px)
        {
            if (_installed == null)
            {
                _installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (InstalledFontCollection all = new InstalledFontCollection())
                    foreach (FontFamily f in all.Families) _installed.Add(f.Name);
            }

            for (int i = 0; i < families.Length; i++)
                if (_installed.Contains(families[i]))
                    return new Font(families[i], px, FontStyle.Regular, GraphicsUnit.Pixel);

            return new Font(FontFamily.GenericSansSerif, px, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        // OnPaint runs 60x a second while anything animates, so hold onto the
        // brushes and pens instead of newing them up every call.
        //
        // the keys come from Mix() with animated values in them, so the key
        // space is basically every argb the animation passes through. capped
        // and dumped wholesale when it fills - rebuilding a few hundred beats
        // growing forever in something that sits in the tray all day.
        private const int MaxCached = 512;

        private static readonly Dictionary<int, SolidBrush> Brushes = new Dictionary<int, SolidBrush>();
        private static readonly Dictionary<long, Pen> Pens = new Dictionary<long, Pen>();

        public static SolidBrush Br(Color c)
        {
            SolidBrush b;
            int key = c.ToArgb();
            if (Brushes.TryGetValue(key, out b)) return b;

            if (Brushes.Count >= MaxCached)
            {
                foreach (SolidBrush old in Brushes.Values) old.Dispose();
                Brushes.Clear();
            }

            b = new SolidBrush(c);
            Brushes[key] = b;
            return b;
        }

        public static Pen Pn(Color c, float w)
        {
            Pen p;

            // width in the low bits, colour shifted well clear. don't drop the
            // shift back to 12, that collides once a width hits 128.
            long key = ((long)c.ToArgb() << 20) ^ (long)(w * 32f);
            if (Pens.TryGetValue(key, out p)) return p;

            if (Pens.Count >= MaxCached)
            {
                foreach (Pen old in Pens.Values) old.Dispose();
                Pens.Clear();
            }

            p = new Pen(c, w);
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            Pens[key] = p;
            return p;
        }


        public static readonly StringFormat Left = Fmt(StringAlignment.Near, StringAlignment.Center);
        public static readonly StringFormat Centre = Fmt(StringAlignment.Center, StringAlignment.Center);
        public static readonly StringFormat Right = Fmt(StringAlignment.Far, StringAlignment.Center);
        public static readonly StringFormat Para = Paragraph();

        private static StringFormat Fmt(StringAlignment h, StringAlignment v)
        {
            StringFormat f = new StringFormat(StringFormat.GenericTypographic);
            f.Alignment = h;
            f.LineAlignment = v;
            f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            f.Trimming = StringTrimming.EllipsisCharacter;
            return f;
        }

        private static StringFormat Paragraph()
        {
            StringFormat f = new StringFormat(StringFormat.GenericTypographic);
            f.Alignment = StringAlignment.Near;
            f.LineAlignment = StringAlignment.Near;
            f.FormatFlags |= StringFormatFlags.NoClip;
            f.Trimming = StringTrimming.Word;
            return f;
        }

        public static void Text(Graphics g, string s, Font f, Color c, RectangleF r, StringFormat fmt)
        {
            if (string.IsNullOrEmpty(s)) return;
            g.DrawString(s, f, Br(c), r, fmt);
        }

        public static float Width(Graphics g, string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            return g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width;
        }

        // drawn a char at a time because gdi+ has no letter spacing and small
        // caps look cramped without it
        public static void Tracked(Graphics g, string s, Font f, Color c, float x, float y, float tracking)
        {
            if (string.IsNullOrEmpty(s)) return;
            SolidBrush b = Br(c);
            for (int i = 0; i < s.Length; i++)
            {
                string ch = s.Substring(i, 1);
                g.DrawString(ch, f, b, x, y, StringFormat.GenericTypographic);
                x += Advance(g, ch, f) + tracking;
            }
        }

        public static float TrackedWidth(Graphics g, string s, Font f, float tracking)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            float w = 0f;
            for (int i = 0; i < s.Length; i++) w += Advance(g, s.Substring(i, 1), f) + tracking;
            return w - tracking;
        }

        // gdi+ measures a bare space as zero wide, so tracked text collapses
        // at every gap unless we handle it
        private static float Advance(Graphics g, string ch, Font f)
        {
            if (ch == " ") return f.Size * 0.27f;
            return g.MeasureString(ch, f, PointF.Empty, StringFormat.GenericTypographic).Width;
        }


        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            if (d <= 0f || d > r.Width || d > r.Height)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        public static RectangleF Inset(RectangleF r, float by)
        {
            return new RectangleF(r.X + by, r.Y + by, r.Width - by * 2f, r.Height - by * 2f);
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color c)
        {
            using (GraphicsPath p = Round(r, radius)) g.FillPath(Br(c), p);
        }

        public static void Stroke(Graphics g, RectangleF r, float radius, Color c, float w)
        {
            using (GraphicsPath p = Round(Inset(r, w / 2f), radius - w / 2f))
                g.DrawPath(Pn(c, w), p);
        }

        public static void Surface(Graphics g, RectangleF r, float radius, Color fill, Color border)
        {
            if (r.Width <= 1f || r.Height <= 1f) return;
            Fill(g, r, radius, fill);
            if (border.A > 0) Stroke(g, r, radius, border, 1f);
        }

        // fades at both ends instead of just stopping
        public static void Divider(Graphics g, float x, float y, float w, Color c)
        {
            if (w <= 2f) return;
            using (LinearGradientBrush b = new LinearGradientBrush(
                       new RectangleF(x, y - 1f, w, 3f), c, c, 0f))
            {
                ColorBlend blend = new ColorBlend(4);
                blend.Colors = new Color[] { A(c, 0), c, c, A(c, 0) };
                blend.Positions = new float[] { 0f, 0.04f, 0.9f, 1f };
                b.InterpolationColors = blend;
                g.FillRectangle(b, x, y, w, 1f);
            }
        }

        public static void Bar(Graphics g, RectangleF track, float amount, Color fill)
        {
            Fill(g, track, track.Height / 2f, A(Color.White, 12));
            float w = track.Width * Math.Max(0.02f, Math.Min(1f, amount));
            Fill(g, new RectangleF(track.X, track.Y, w, track.Height), track.Height / 2f, fill);
        }

        public static void Dot(Graphics g, float cx, float cy, float d, Color c)
        {
            g.FillEllipse(Br(c), cx - d / 2f, cy - d / 2f, d, d);
        }

        // cpu% sparkline, oldest on the left. every point is a real sample the
        // caller measured, don't ever fake these.
        public static void Trend(Graphics g, RectangleF r, float radius, IList<int> samples, bool live)
        {
            if (r.Width <= 2f || r.Height <= 2f) return;

            Fill(g, r, radius, Rgb(0x0A, 0x0A, 0x0A));

            GraphicsState state = g.Save();
            using (GraphicsPath clip = Round(r, radius))
            {
                g.SetClip(clip, CombineMode.Intersect);

                float padX = r.Width * 0.06f;
                float top = r.Y + r.Height * 0.18f;
                float bottom = r.Bottom - r.Height * 0.18f;

                // 50% guideline so the values have something to sit against
                float midY = top + (bottom - top) * 0.5f;
                g.DrawLine(Pn(A(Color.White, live ? 16 : 8), 1f),
                           r.X + padX, midY, r.Right - padX, midY);

                Color line = live ? TextHi : TextLo;
                int n = samples == null ? 0 : samples.Count;

                if (n == 1)
                {
                    float y = bottom - (bottom - top) * (Clamp01(samples[0]));
                    g.DrawLine(Pn(line, 1.6f), r.X + padX, y, r.Right - padX, y);
                    g.FillEllipse(Br(line), r.Right - padX - 2f, y - 2f, 4f, 4f);
                }
                else if (n > 1)
                {
                    PointF[] pts = new PointF[n];
                    for (int i = 0; i < n; i++)
                    {
                        float t = i / (float)(n - 1);
                        float x = r.X + padX + (r.Width - padX * 2f) * t;
                        float y = bottom - (bottom - top) * Clamp01(samples[i]);
                        pts[i] = new PointF(x, y);
                    }

                    using (GraphicsPath trace = new GraphicsPath())
                    {
                        trace.AddLines(pts);
                        g.DrawPath(Pn(line, 1.6f), trace);
                    }

                    // dot on the newest one so it stands out from the trend
                    g.FillEllipse(Br(line), pts[n - 1].X - 2f, pts[n - 1].Y - 2f, 4f, 4f);
                }
            }
            g.Restore(state);

            Stroke(g, r, radius, Line, 1f);
        }

        private static float Clamp01(int percent)
        {
            if (percent < 0) return 0f;
            if (percent > 100) return 1f;
            return percent / 100f;
        }
    }

    // hovers and selections ease instead of snapping. values live here by
    // name, the form ticks them and repaints while anything's still moving.
    class Motion
    {
        private readonly Dictionary<string, float> _now = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _want = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _rate = new Dictionary<string, float>();

        public float Of(string id, float target) { return Of(id, target, 0.22f); }

        public float Of(string id, float target, float rate)
        {
            _want[id] = target;
            _rate[id] = rate;

            float v;
            if (!_now.TryGetValue(id, out v)) { v = target; _now[id] = v; }
            return v;
        }

        // returns true while anything's still moving.
        // the list is reused - it only exists so the loop can write into _now
        // while iterating, and newing one 60x a second is a lot of garbage.
        private readonly List<string> _scratch = new List<string>();

        public bool Tick()
        {
            bool moving = false;

            _scratch.Clear();
            foreach (string key in _now.Keys) _scratch.Add(key);

            foreach (string k in _scratch)
            {
                float target;
                if (!_want.TryGetValue(k, out target)) continue;

                float v = _now[k];
                float d = target - v;
                if (Math.Abs(d) < 0.0015f)
                {
                    if (v != target) { _now[k] = target; moving = true; }
                    continue;
                }
                _now[k] = v + d * _rate[k];
                moving = true;
            }
            return moving;
        }
    }
}
