// the app mark and the controls the window is built from. none of these are
// real windows controls, they're just draw calls, so nothing here can take
// focus - MainForm hit-tests the same rectangles instead.
//
// all fills are flat. depth comes from contrast, not gradients or glow.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Archon
{
    // real windows controls here, not drawn ones, because this needs a caret
    // and selection. returns null on cancel so "" still means something.
    static class TextPrompt
    {
        public static string Show(IWin32Window owner, string title, string prompt, string initial)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = title;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.BackColor = Skin.Base;
                dialog.ForeColor = Skin.TextHi;

                // we called SetProcessDPIAware so nothing scales this dialog
                // for us. scale the numbers by hand or it clips at 125%+.
                dialog.AutoScaleMode = AutoScaleMode.Font;
                dialog.AutoScaleDimensions = new SizeF(6f, 13f);

                using (Graphics g = dialog.CreateGraphics())
                {
                    float s = g.DpiX / 96f;

                    dialog.ClientSize = new Size(Px(340, s), Px(120, s));

                    Label label = new Label();
                    label.Text = prompt;
                    label.ForeColor = Skin.TextMid;
                    label.AutoSize = false;
                    label.SetBounds(Px(14, s), Px(12, s), Px(312, s), Px(34, s));
                    dialog.Controls.Add(label);

                    TextBox box = new TextBox();
                    box.Text = initial == null ? "" : initial;
                    box.BorderStyle = BorderStyle.FixedSingle;
                    box.BackColor = Skin.Sunken;
                    box.ForeColor = Skin.TextHi;
                    box.MaxLength = 40;
                    box.SetBounds(Px(14, s), Px(48, s), Px(312, s), Px(24, s));
                    box.SelectAll();
                    dialog.Controls.Add(box);

                    Button ok = new Button();
                    ok.Text = "Rename";
                    ok.DialogResult = DialogResult.OK;
                    ok.FlatStyle = FlatStyle.Flat;
                    ok.BackColor = Skin.CardLit;
                    ok.SetBounds(Px(170, s), Px(84, s), Px(74, s), Px(26, s));
                    dialog.Controls.Add(ok);

                    Button cancel = new Button();
                    cancel.Text = "Cancel";
                    cancel.DialogResult = DialogResult.Cancel;
                    cancel.FlatStyle = FlatStyle.Flat;
                    cancel.BackColor = Skin.Card;
                    cancel.SetBounds(Px(252, s), Px(84, s), Px(74, s), Px(26, s));
                    dialog.Controls.Add(cancel);

                    dialog.AcceptButton = ok;
                    dialog.CancelButton = cancel;

                    return dialog.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
                }
            }
        }

        private static int Px(float logical, float scale)
        {
            return (int)Math.Round(logical * scale);
        }
    }

    // drawn not loaded, so the same shape works at 16px in the tray and 22px
    // in the title bar without two assets drifting apart
    static class AppMark
    {
        public static void Draw(Graphics g, RectangleF box)
        {
            float s = Math.Min(box.Width, box.Height);
            float side = s * 0.72f;
            float step = s * 0.14f;
            float radius = side * 0.2f;

            // back to front, each pane a step lighter than the one behind
            Pane(g, new RectangleF(box.X + step * 2f, box.Y, side, side), radius,
                 Skin.Rgb(0x2C, 0x2C, 0x2C), Color.Empty);
            Pane(g, new RectangleF(box.X + step, box.Y + step, side, side), radius,
                 Skin.Rgb(0x40, 0x40, 0x40), Color.Empty);

            RectangleF front = new RectangleF(box.X, box.Y + step * 2f, side, side);
            Pane(g, front, radius, Color.White, Skin.Rgb(0x18, 0x18, 0x18));
        }

        private static void Pane(Graphics g, RectangleF r, float radius, Color fill, Color edge)
        {
            Skin.Fill(g, r, radius, fill);
            if (edge.A > 0) Skin.Stroke(g, r, radius, edge, 1.2f);
        }

        public static Icon MakeIcon(int px)
        {
            using (Bitmap bmp = new Bitmap(px, px))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    float pad = px * 0.06f;
                    Draw(g, new RectangleF(pad, pad, px - pad * 2f, px - pad * 2f));
                }

                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                }
                finally
                {
                    // GetHicon hands us an os handle to own, leaks one per
                    // icon without this
                    Native.DestroyIcon(h);
                }
            }
        }
    }

    enum Btn
    {
        Primary,
        Secondary,
        Quiet
    }

    static class Widgets
    {
        public static void Button(Graphics g, RectangleF r, string label, Btn kind,
                                  float hover, bool enabled, float radius)
        {
            if (kind == Btn.Primary)
            {
                // everything else on screen is grey so solid white is enough
                Color fill = Skin.Mix(Color.White, Skin.Rgb(0xD8, 0xD8, 0xD8), hover);
                Color text = Color.Black;
                if (!enabled)
                {
                    fill = Skin.Rgb(0x35, 0x35, 0x35);
                    text = Skin.TextFaint;
                }

                Skin.Surface(g, r, radius, fill, Color.Empty);
                Skin.Text(g, label, Skin.SmallMed, text, r, Skin.Centre);
                return;
            }

            if (kind == Btn.Quiet)
            {
                if (hover > 0.01f) Skin.Fill(g, r, radius, Skin.A(Color.White, (int)(16 * hover)));
                Skin.Text(g, label, Skin.SmallMed,
                          Skin.Mix(Skin.TextLo, Skin.TextHi, hover), r, Skin.Centre);
                return;
            }

            Color body = Skin.Mix(Skin.Card, Skin.CardLit, hover);
            if (!enabled) body = Skin.Mix(body, Skin.Base, 0.55f);

            Skin.Surface(g, r, radius, body, Skin.Mix(Skin.Line, Skin.Rgb(0x38, 0x38, 0x38), hover));
            Skin.Text(g, label, Skin.SmallMed,
                      enabled ? Skin.Mix(Skin.TextMid, Skin.TextHi, hover) : Skin.TextFaint,
                      r, Skin.Centre);
        }

        // three discs, not an ellipsis glyph - that never centres properly
        // on a button this small
        public static void More(Graphics g, RectangleF r, float hover, float radius)
        {
            Color body = Skin.Mix(Skin.Card, Skin.CardLit, hover);
            Skin.Surface(g, r, radius, body, Skin.Mix(Skin.Line, Skin.Rgb(0x38, 0x38, 0x38), hover));

            Color dot = Skin.Mix(Skin.TextLo, Skin.TextHi, hover);
            float cy = r.Y + r.Height / 2f;
            float cx = r.X + r.Width / 2f;
            for (int i = -1; i <= 1; i++)
                g.FillEllipse(Skin.Br(dot), cx + i * 5f - 1.5f, cy - 1.5f, 3f, 3f);
        }

        public static void Pill(Graphics g, RectangleF r, string label, bool active)
        {
            Color edge = active ? Skin.A(Color.White, 130) : Skin.A(Color.White, 30);
            Color fill = active ? Skin.A(Color.White, 20) : Skin.A(Color.White, 10);
            Color text = active ? Skin.TextHi : Skin.TextMid;

            Skin.Fill(g, r, r.Height / 2f, fill);
            Skin.Stroke(g, r, r.Height / 2f, edge, 1f);
            Skin.Text(g, label, Skin.Micro, text,
                      new RectangleF(r.X, r.Y - 0.5f, r.Width, r.Height), Skin.Centre);
        }

        // caller animates the knob position
        public static void Switch(Graphics g, RectangleF r, float on)
        {
            float radius = r.Height / 2f;

            Color track = Skin.Mix(Skin.Rgb(0x24, 0x24, 0x24), Color.White, on);
            Skin.Surface(g, r, radius, track, Skin.Mix(Skin.Line, Skin.Rgb(0x2A, 0x2A, 0x2A), on));

            float pad = 3f;
            float d = r.Height - pad * 2f;
            float x = r.X + pad + (r.Width - d - pad * 2f) * on;
            RectangleF knob = new RectangleF(x, r.Y + pad, d, d);

            Skin.Fill(g, knob, d / 2f, Skin.Mix(Skin.Rgb(0xEA, 0xEA, 0xEA), Skin.Rgb(0x12, 0x12, 0x12), on));
        }

        // the selected pill slides between segments instead of blinking.
        // MainForm registers its hit spots off the same cell maths, keep both
        // in step or the tabs stop lining up with what you click.
        public static void Segmented(Graphics g, RectangleF r, string[] labels,
                                     float position, int hovered, float radius)
        {
            Skin.Surface(g, r, radius, Skin.Rgb(0x0D, 0x0D, 0x0D), Skin.Line);

            float pad = 3f;
            float seg = (r.Width - pad * 2f) / labels.Length;
            RectangleF pill = new RectangleF(r.X + pad + seg * position, r.Y + pad, seg, r.Height - pad * 2f);

            Skin.Surface(g, pill, radius - 2f, Skin.Rgb(0x28, 0x28, 0x28), Skin.A(Color.White, 20));

            for (int i = 0; i < labels.Length; i++)
            {
                RectangleF cell = new RectangleF(r.X + pad + seg * i, r.Y, seg, r.Height);
                float near = 1f - Math.Min(1f, Math.Abs(position - i));
                Color c = Skin.Mix(i == hovered ? Skin.TextMid : Skin.TextLo, Skin.TextHi, near);
                Skin.Text(g, labels[i], Skin.SmallMed, c, cell, Skin.Centre);
            }
        }

        // real headshot if we've got one, initial-letter circle if not
        public static void Avatar(Graphics g, RectangleF r, string initial, float active, Image photo)
        {
            if (photo != null)
            {
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddEllipse(r);
                    Region prevClip = g.Clip;
                    g.SetClip(p, CombineMode.Intersect);
                    g.DrawImage(photo, r);
                    g.Clip = prevClip;
                    prevClip.Dispose();

                    g.DrawPath(Skin.Pn(Skin.A(Color.White, active > 0.5f ? 90 : 26), active > 0.5f ? 1.4f : 1f), p);
                }
                return;
            }

            Color fill = Skin.Mix(Skin.Rgb(0x26, 0x26, 0x26), Color.White, active);

            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(r);
                g.FillPath(Skin.Br(fill), p);
                g.DrawPath(Skin.Pn(Skin.A(Color.White, 26), 1f), p);
            }

            Skin.Text(g, initial, Skin.BodyMed, active > 0.5f ? Color.Black : Skin.TextHi,
                      new RectangleF(r.X, r.Y - 0.5f, r.Width, r.Height), Skin.Centre);
        }

        // glyphs are drawn so they stay sharp at any scale
        public static void WindowButton(Graphics g, RectangleF r, bool close, float hover)
        {
            if (hover > 0.01f) Skin.Fill(g, r, 7f, Skin.A(Color.White, (int)(18 * hover)));

            Color c = Skin.Mix(Skin.TextLo, Skin.TextHi, hover);
            float cx = r.X + r.Width / 2f;
            float cy = r.Y + r.Height / 2f;

            if (close)
            {
                float a = 4.5f;
                g.DrawLine(Skin.Pn(c, 1.3f), cx - a, cy - a, cx + a, cy + a);
                g.DrawLine(Skin.Pn(c, 1.3f), cx + a, cy - a, cx - a, cy + a);
            }
            else
            {
                g.DrawLine(Skin.Pn(c, 1.3f), cx - 5f, cy, cx + 5f, cy);
            }
        }

        // dimmer than WindowButton at rest on purpose, this isn't a window control
        public static void InfoButton(Graphics g, RectangleF r, float hover)
        {
            if (hover > 0.01f) Skin.Fill(g, r, 7f, Skin.A(Color.White, (int)(14 * hover)));

            Color c = Skin.Mix(Skin.TextFaint, Skin.TextHi, hover);
            float cx = r.X + r.Width / 2f;
            float cy = r.Y + r.Height / 2f;
            float radius = 6.5f;

            g.DrawEllipse(Skin.Pn(c, 1.2f), cx - radius, cy - radius, radius * 2f, radius * 2f);
            g.FillEllipse(Skin.Br(c), cx - 0.9f, cy - 3.6f, 1.8f, 1.8f);
            g.DrawLine(Skin.Pn(c, 1.3f), cx, cy - 1.1f, cx, cy + 3.4f);
        }
    }
}
