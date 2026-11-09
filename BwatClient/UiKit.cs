/*
 * Bwat Client / UiKit.cs
 * Small custom controls that give the app a modern look (standard WinForms
 * controls look old-fashioned). All of them are drawn with GDI+.
 *
 *   ModernButton - rounded button with hover / pressed / disabled states
 *   InputBox     - rounded text box with a coloured border when focused
 *   CardPanel    - white rounded card with a soft shadow
 *   FancyList    - a clean table: header, rows, hover, selection, scrolling, avatars
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Bwat.Client
{
    // ======================================================================
    public class ModernButton : Control, IButtonControl
    {
        private bool hover, pressed;
        public Color Color = Theme.Brand;
        public bool Outline;           // transparent with a coloured border
        public float Radius = 10;

        public ModernButton(string text, Color color, int width, int height)
        {
            Theme.DoubleBuffer(this);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Text = text;
            Color = color;
            Size = new Size(width, height);
            Font = Theme.B(10.5f);
            Cursor = Cursors.Hand;
        }

        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.HQ(g);
            RectangleF r = new RectangleF(1, pressed ? 2 : 1, Width - 3, Height - 4);
            Color c = !Enabled ? Color.FromArgb(190, 186, 205) : pressed ? Theme.Mix(Color, Color.Black, 0.15f) : hover ? Theme.Mix(Color, Color.White, 0.12f) : Color;
            if (Outline)
            {
                if (hover && Enabled) Theme.FillRound(g, r, Radius, Theme.Alpha(c, 30));
                Theme.StrokeRound(g, r, Radius, c, 2);
                Theme.Center(g, Text, Font, c, r);
            }
            else
            {
                if (Enabled && !pressed) Theme.FillRound(g, new RectangleF(r.X, r.Y + 3, r.Width, r.Height), Radius, Theme.Mix(c, Color.Black, 0.25f));
                Theme.FillRound(g, r, Radius, c);
                Theme.Center(g, Text, Font, Color.White, r);
            }
        }
    }

    // ======================================================================
    public class InputBox : UserControl
    {
        private readonly TextBox box = new TextBox();
        private bool focused;

        public InputBox(int width, float fontSize)
        {
            Theme.DoubleBuffer(this);
            BackColor = Color.White;
            box.BorderStyle = BorderStyle.None;
            box.Font = Theme.F(fontSize);
            box.BackColor = Color.White;
            box.ForeColor = Theme.Text;
            Size = new Size(width, box.PreferredHeight + 20);
            box.Location = new Point(12, 10);
            box.Width = width - 24;
            box.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            box.GotFocus += delegate { focused = true; Invalidate(); };
            box.LostFocus += delegate { focused = false; Invalidate(); };
            Controls.Add(box);
            Cursor = Cursors.IBeam;
            Click += delegate { box.Focus(); };
        }

        public TextBox Box { get { return box; } }
        public override string Text { get { return box.Text; } set { box.Text = value; } }
        public bool Password { set { box.UseSystemPasswordChar = value; } }

        /// <summary>Turns the box into a multi-line text area of the given height.</summary>
        public void MakeMultiline(int height)
        {
            box.Multiline = true;
            box.ScrollBars = ScrollBars.Vertical;
            Height = height;
            box.Height = height - 20;
            box.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.HQ(e.Graphics);
            using (SolidBrush b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Surface)) e.Graphics.FillRectangle(b, ClientRectangle);
            RectangleF r = new RectangleF(1, 1, Width - 3, Height - 3);
            Theme.FillRound(e.Graphics, r, 9, Color.White);
            Theme.StrokeRound(e.Graphics, r, 9, focused ? Theme.Brand : Theme.Border, focused ? 2 : 1.4f);
        }
    }

    // ======================================================================
    public class CardPanel : Panel
    {
        public float Radius = 16;

        public CardPanel()
        {
            Theme.DoubleBuffer(this);
            Padding = new Padding(20);
            BackColor = Theme.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.HQ(e.Graphics);
            RectangleF r = new RectangleF(5, 3, Width - 11, Height - 11);
            Theme.Shadow(e.Graphics, r, Radius, 40);
            Theme.FillRound(e.Graphics, r, Radius, Color.White);
            base.OnPaint(e);
        }

        protected override void OnResize(EventArgs e) { Invalidate(); base.OnResize(e); }

        /// <summary>Adds a control that sits on the white part of the card.</summary>
        public T Put<T>(T c, int x, int y) where T : Control
        {
            c.Location = new Point(x, y);
            if (!(c is ModernButton) && !(c is InputBox)) c.BackColor = Color.White;
            Controls.Add(c);
            return c;
        }
    }

    // ======================================================================
    public class FancyList : Control
    {
        public class Column
        {
            public string Header;
            public int Width;              // -1 = take the remaining space
            public StringAlignment Align;
            public Column(string header, int width, StringAlignment align) { Header = header; Width = width; Align = align; }
        }

        public class Row
        {
            public string[] Cells;
            public object Tag;
            public Color ForeColor = Color.Empty;
            public bool Bold;
            public Color Badge = Color.Empty;  // colour of the rank / status badge in column 0
        }

        public readonly List<Column> Columns = new List<Column>();
        public readonly List<Row> Rows = new List<Row>();
        public int NameColumn = -1;           // this column shows player names in their own colour
        public bool BadgeFirstColumn;         // draw column 0 as a round badge (ranks)
        public string EmptyText = "Nothing to show yet.";
        public int RowHeight = 44;
        private const int HeaderHeight = 38;

        private int scroll;
        private int hover = -1;
        private int selected = -1;

        public event EventHandler SelectionChanged;
        public event EventHandler RowDoubleClick;

        public FancyList()
        {
            Theme.DoubleBuffer(this);
            BackColor = Color.White;
            Font = Theme.F(10.5f);
            SetStyle(ControlStyles.Selectable, true);
        }

        public FancyList AddColumn(string header, int width, StringAlignment align)
        {
            Columns.Add(new Column(header, width, align));
            return this;
        }

        public int SelectedIndex { get { return selected; } }

        public Row SelectedRow { get { return selected >= 0 && selected < Rows.Count ? Rows[selected] : null; } }

        public void SetRows(List<Row> rows)
        {
            Rows.Clear();
            Rows.AddRange(rows);
            selected = -1;
            hover = -1;
            ClampScroll();
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public void ClearSelection()
        {
            selected = -1;
            Invalidate();
        }

        private int MaxScroll { get { return Math.Max(0, Rows.Count * RowHeight - (Height - HeaderHeight)); } }
        private void ClampScroll() { scroll = Math.Max(0, Math.Min(scroll, MaxScroll)); }

        private int RowAt(int y)
        {
            if (y < HeaderHeight) return -1;
            int i = (y - HeaderHeight + scroll) / RowHeight;
            return i >= 0 && i < Rows.Count ? i : -1;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll -= e.Delta / 120 * RowHeight;
            ClampScroll();
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = RowAt(e.Y);
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int i = RowAt(e.Y);
            if (i >= 0 && i != selected)
            {
                selected = i;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (RowAt(e.Y) >= 0 && RowDoubleClick != null) RowDoubleClick(this, EventArgs.Empty);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnResize(EventArgs e) { ClampScroll(); Invalidate(); base.OnResize(e); }

        private int[] ColumnWidths()
        {
            int fixedTotal = 0, flexible = 0;
            foreach (Column c in Columns) { if (c.Width > 0) fixedTotal += c.Width; else flexible++; }
            int rest = Math.Max(40, Width - 24 - fixedTotal);
            int[] w = new int[Columns.Count];
            for (int i = 0; i < Columns.Count; i++) w[i] = Columns[i].Width > 0 ? Columns[i].Width : rest / Math.Max(1, flexible);
            return w;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.HQ(g);
            g.Clear(BackColor);
            int[] widths = ColumnWidths();

            // header
            using (SolidBrush b = new SolidBrush(Color.FromArgb(241, 245, 249))) g.FillRectangle(b, 0, 0, Width, HeaderHeight);
            using (Font hf = Theme.B(9f))
            {
                float x = 12;
                for (int c = 0; c < Columns.Count; c++)
                {
                    Theme.DrawText(g, Columns[c].Header.ToUpper(), hf, Theme.Muted, new RectangleF(x + 4, 0, widths[c] - 8, HeaderHeight), Columns[c].Align);
                    x += widths[c];
                }
            }
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, 0, HeaderHeight - 1, Width, HeaderHeight - 1);

            if (Rows.Count == 0)
            {
                using (Font f = Theme.F(10.5f)) Theme.Center(g, EmptyText, f, Theme.Muted, new RectangleF(0, HeaderHeight, Width, Math.Max(60, Height - HeaderHeight)));
                return;
            }

            g.SetClip(new Rectangle(0, HeaderHeight, Width, Height - HeaderHeight));
            using (Font normal = Theme.F(10.5f))
            using (Font bold = Theme.B(10.5f))
            using (Pen line = new Pen(Color.FromArgb(241, 245, 249)))
            {
                int first = scroll / RowHeight;
                for (int i = first; i < Rows.Count; i++)
                {
                    int y = HeaderHeight + i * RowHeight - scroll;
                    if (y > Height) break;
                    Row row = Rows[i];
                    if (i == selected) Theme.FillRound(g, new RectangleF(4, y + 3, Width - 8, RowHeight - 6), 8, Color.FromArgb(219, 234, 254));
                    else if (i == hover) Theme.FillRound(g, new RectangleF(4, y + 3, Width - 8, RowHeight - 6), 8, Color.FromArgb(241, 245, 249));
                    g.DrawLine(line, 12, y + RowHeight - 1, Width - 12, y + RowHeight - 1);

                    float x = 12;
                    for (int c = 0; c < Columns.Count && c < row.Cells.Length; c++)
                    {
                        RectangleF cell = new RectangleF(x + 4, y, widths[c] - 8, RowHeight);
                        string text = row.Cells[c];
                        if (c == 0 && BadgeFirstColumn)
                        {
                            Color badge = row.Badge != Color.Empty ? row.Badge : Color.FromArgb(226, 232, 240);
                            float d = RowHeight - 16;
                            RectangleF br = new RectangleF(cell.X, y + 8, d, d);
                            using (SolidBrush b = new SolidBrush(badge)) g.FillEllipse(b, br);
                            using (Font f = Theme.B(9.5f)) Theme.Center(g, text, f, row.Badge != Color.Empty ? Color.White : Theme.Text, br);
                        }
                        else
                        {
                            Color fc = row.ForeColor != Color.Empty ? row.ForeColor : Theme.Text;
                            if (c == NameColumn) fc = Theme.NameColor(text);
                            Theme.DrawText(g, text, row.Bold || c == NameColumn ? bold : normal, fc, cell, Columns[c].Align);
                        }
                        x += widths[c];
                    }
                }
            }
            g.ResetClip();

            // scroll bar
            if (MaxScroll > 0)
            {
                float viewH = Height - HeaderHeight;
                float barH = Math.Max(30, viewH * viewH / (Rows.Count * RowHeight));
                float barY = HeaderHeight + (viewH - barH) * scroll / MaxScroll;
                Theme.FillRound(g, new RectangleF(Width - 7, barY + 2, 4, barH - 4), 2, Color.FromArgb(203, 213, 225));
            }
        }
    }
}
