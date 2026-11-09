/*
 * Bwat Client / Theme.cs
 * Colours, fonts and drawing helpers shared by every screen (one simple, consistent look).
 */
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Bwat.Client
{
    public static class Theme
    {
        // ---- the agreed colour scheme ----
        public static readonly Color Bg = ColorTranslator.FromHtml("#1E293B");          // app background (dark slate)
        public static readonly Color Board = ColorTranslator.FromHtml("#F8FAFC");       // question area (whiteboard)
        public static readonly Color Ink = ColorTranslator.FromHtml("#0F172A");         // text on the whiteboard
        public static readonly Color CalcBody = ColorTranslator.FromHtml("#334155");    // calculator body
        public static readonly Color KeyNumber = ColorTranslator.FromHtml("#475569");   // number keys
        public static readonly Color KeyAction = ColorTranslator.FromHtml("#EF4444");   // C and backspace
        public static readonly Color KeyEquals = ColorTranslator.FromHtml("#3B82F6");   // = (submit)
        public static readonly Color Card = Color.White;                                // player cards
        public static readonly Color Border = ColorTranslator.FromHtml("#E2E8F0");      // subtle gray borders

        // ---- a few helpers that fit the scheme ----
        public static readonly Color Muted = ColorTranslator.FromHtml("#64748B");       // secondary text on white
        public static readonly Color MutedOnDark = ColorTranslator.FromHtml("#94A3B8"); // secondary text on dark
        public static readonly Color Blue = KeyEquals;
        public static readonly Color Red = KeyAction;
        public static readonly Color Green = ColorTranslator.FromHtml("#22C55E");
        public static readonly Color Amber = ColorTranslator.FromHtml("#F59E0B");
        public static readonly Color Gold = ColorTranslator.FromHtml("#EAB308");
        public static readonly Color Silver = ColorTranslator.FromHtml("#94A3B8");
        public static readonly Color Bronze = ColorTranslator.FromHtml("#C2410C");

        // names used by the shared controls in UiKit.cs
        public static readonly Color Brand = Blue;
        public static readonly Color Text = Ink;
        public static readonly Color Surface = Bg;
        public static readonly Color Danger = Red;
        public static readonly Color Success = Green;

        /// <summary>Colours for player names (the same name always gets the same colour).</summary>
        public static readonly Color[] NameColors =
        {
            ColorTranslator.FromHtml("#2563EB"), ColorTranslator.FromHtml("#DC2626"), ColorTranslator.FromHtml("#16A34A"),
            ColorTranslator.FromHtml("#9333EA"), ColorTranslator.FromHtml("#EA580C"), ColorTranslator.FromHtml("#0891B2"),
            ColorTranslator.FromHtml("#DB2777"), ColorTranslator.FromHtml("#65A30D"), ColorTranslator.FromHtml("#4F46E5")
        };

        public const string FontName = "Segoe UI";
        public static Font F(float size) { return new Font(FontName, size, FontStyle.Regular); }
        public static Font B(float size) { return new Font(FontName, size, FontStyle.Bold); }

        public static Color NameColor(string name)
        {
            int sum = 0;
            foreach (char c in (name ?? "").ToLower()) sum = sum * 31 + c;
            return NameColors[Math.Abs(sum % NameColors.Length)];
        }

        public static string Num(int n) { return n.ToString("N0", CultureInfo.InvariantCulture); }

        public static string Ordinal(int n)
        {
            if (n % 100 >= 11 && n % 100 <= 13) return n + "th";
            switch (n % 10)
            {
                case 1: return n + "st";
                case 2: return n + "nd";
                case 3: return n + "rd";
                default: return n + "th";
            }
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                                  (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c); }

        // ---- shapes ----

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, RectangleF r, float radius, Color color)
        {
            using (GraphicsPath p = RoundRect(r, radius))
            using (SolidBrush b = new SolidBrush(color))
                g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, RectangleF r, float radius, Color color, float width)
        {
            using (GraphicsPath p = RoundRect(r, radius))
            using (Pen pen = new Pen(color, width))
                g.DrawPath(pen, p);
        }

        /// <summary>A soft drop shadow under a rounded card.</summary>
        public static void Shadow(Graphics g, RectangleF r, float radius, int strength)
        {
            for (int i = 1; i <= 3; i++)
            {
                RectangleF s = new RectangleF(r.X - i + 1, r.Y + i * 1.5f, r.Width + 2 * i - 2, r.Height + i);
                FillRound(g, s, radius + i, Color.FromArgb(Math.Max(1, strength / (i + 1)), 0, 0, 0));
            }
        }

        public static void DrawText(Graphics g, string text, Font font, Color color, RectangleF area, StringAlignment align)
        {
            using (StringFormat sf = new StringFormat())
            using (SolidBrush b = new SolidBrush(color))
            {
                sf.Alignment = align;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(text ?? "", font, b, area, sf);
            }
        }

        public static void Center(Graphics g, string text, Font font, Color color, RectangleF area)
        {
            using (StringFormat sf = new StringFormat())
            using (SolidBrush b = new SolidBrush(color))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisWord;
                g.DrawString(text ?? "", font, b, area, sf);
            }
        }

        /// <summary>Finds the biggest font size (between max and min) that makes the text fit in the area.</summary>
        public static Font FitFont(Graphics g, string text, float maxSize, float minSize, bool bold, RectangleF area)
        {
            for (float size = maxSize; size > minSize; size -= 1f)
            {
                Font f = bold ? B(size) : F(size);
                SizeF s = g.MeasureString(text ?? "", f, (int)Math.Max(10, area.Width));
                if (s.Height <= area.Height) return f;
                f.Dispose();
            }
            return bold ? B(minSize) : F(minSize);
        }

        public static void HQ(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        /// <summary>Turns on double buffering for any control so custom drawing does not flicker.</summary>
        public static void DoubleBuffer(Control c)
        {
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(c, true, null);
        }

        public static Label MakeLabel(string text, float size, bool bold, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = bold ? B(size) : F(size);
            l.ForeColor = color;
            l.AutoSize = true;
            l.BackColor = Color.Transparent;
            return l;
        }

        public static ComboBox MakeCombo(object[] items, int width)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.Font = F(10.5f);
            c.Width = width;
            c.Items.AddRange(items);
            if (items.Length > 0) c.SelectedIndex = 0;
            return c;
        }

        public static NumericUpDown MakeNumber(int min, int max, int value, int width)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = value;
            n.Font = F(11f);
            n.Width = width;
            n.BorderStyle = BorderStyle.FixedSingle;
            return n;
        }
    }
}
