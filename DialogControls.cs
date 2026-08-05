// colours and controls for AddAccountForm, the one real dialog in the app.
// the main window paints itself instead, see UiKit.cs / Widgets.cs.
// palette is kept in step with UiKit.cs by hand.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Archon
{
    static class Theme
    {
        public static readonly Color Bg = Skin.Base;
        public static readonly Color Card = Skin.Card;
        public static readonly Color CardHover = Skin.CardLit;
        public static readonly Color Border = Skin.Line;

        public static readonly Color Accent = Color.White;
        public static readonly Color AccentHover = Skin.Rgb(0xD8, 0xD8, 0xD8);

        // no red anywhere. errors read as brighter text against the grey.
        public static readonly Color Danger = Skin.TextHi;

        public static readonly Color TextHi = Skin.TextHi;
        public static readonly Color TextMid = Skin.TextMid;
        public static readonly Color TextLo = Skin.TextLo;

        public static readonly Font H1 = new Font("Segoe UI", 15f, FontStyle.Bold);
        public static readonly Font H2 = new Font("Segoe UI Semibold", 13.5f);
        public static readonly Font Body = new Font("Segoe UI", 9f);
        public static readonly Font BodyBold = new Font("Segoe UI Semibold", 9f);
        public static readonly Font Small = new Font("Segoe UI", 8.25f);
        public static readonly Font Label = new Font("Segoe UI Semibold", 8.25f);
        public static readonly Font Button = new Font("Segoe UI Semibold", 10.5f);

        // inset by a pixel so the border doesn't get clipped
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            r = new Rectangle(r.X, r.Y, Math.Max(1, r.Width - 1), Math.Max(1, r.Height - 1));

            int d = radius * 2;
            if (d <= 0 || d > r.Width || d > r.Height)
            {
                path.AddRectangle(r);
                return path;
            }

            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)(a.R + ((b.R - a.R) * t)),
                (int)(a.G + ((b.G - a.G) * t)),
                (int)(a.B + ((b.B - a.B) * t)));
        }
    }

    enum ButtonKind
    {
        Primary,
        Ghost
    }

    class FlatBtn : Button
    {
        private bool _hover;
        private bool _down;
        private ButtonKind _kind = ButtonKind.Ghost;

        public FlatBtn()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = Theme.Button;
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        public ButtonKind Kind
        {
            get { return _kind; }
            set { _kind = value; Invalidate(); }
        }

        public int Radius = 8;

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            Rectangle r = ClientRectangle;
            Color fill, text;

            if (_kind == ButtonKind.Primary)
            {
                fill = _hover ? Theme.AccentHover : Theme.Accent;
                text = Color.Black;
            }
            else
            {
                fill = _hover ? Theme.CardHover : Theme.Card;
                text = Theme.TextMid;
            }

            if (_down) fill = Theme.Mix(fill, Color.Black, 0.18);

            if (!Enabled)
            {
                fill = Theme.Mix(fill, Theme.Bg, 0.55);
                text = Theme.Mix(text, Theme.Bg, 0.5);
            }

            using (GraphicsPath path = Theme.Round(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                if (_kind == ButtonKind.Ghost)
                    using (Pen p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }

            // VerticalCenter is ignored without SingleLine
            TextRenderer.DrawText(g, Text, Font, r, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    class Card : Panel
    {
        public int Radius = 10;
        public Color Fill = Theme.Card;
        public Color Stroke = Theme.Border;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            using (GraphicsPath path = Theme.Round(ClientRectangle, Radius))
            {
                using (SolidBrush b = new SolidBrush(Fill)) g.FillPath(b, path);
                using (Pen p = new Pen(Stroke)) g.DrawPath(p, path);
            }
            base.OnPaint(e);
        }
    }

    // a TextBox can't draw its own border, so put a borderless one inside a
    // Card and let that draw it
    class FieldBox : Card
    {
        private readonly TextBox _input;

        public FieldBox()
        {
            Radius = 8;
            Padding = new Padding(10, 0, 10, 0);

            _input = new TextBox();
            _input.BorderStyle = BorderStyle.None;
            _input.BackColor = Theme.Card;
            _input.ForeColor = Theme.TextHi;
            _input.Font = Theme.Body;
            _input.Dock = DockStyle.Fill;
            _input.TextChanged += delegate { OnTextChanged(EventArgs.Empty); };
            Controls.Add(_input);
        }

        public override string Text
        {
            get { return _input.Text; }
            set { _input.Text = value; }
        }

        public bool UseSystemPasswordChar
        {
            get { return _input.UseSystemPasswordChar; }
            set { _input.UseSystemPasswordChar = value; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int h = _input.PreferredHeight;
            Padding = new Padding(10, Math.Max(0, (Height - h) / 2), 10, 0);
        }
    }
}
