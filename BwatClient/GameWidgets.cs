/*
 * Bwat Client / GameWidgets.cs
 *
 * The simple building blocks of the game screens (all drawn with GDI+):
 *
 *   QuestionBox   - the whiteboard at the top: question, timer, marks, tries and the player's BALLOON
 *   Calculator    - the answering tool (bottom left)
 *   ActiveBoard   - names and marks, re-sorted live (bottom right)
 *   StandingsView - everybody's position between questions, and the final results (top 3 or BONGO)
 *   BalloonGrid   - admin only: every player's balloon and tries
 *
 * The balloon: one per player per question.
 *   right answer -> it floats away     wrong answer -> it shrinks (3rd wrong: it pops)
 *   time out / somebody else was first -> it pops.  Next question -> a new balloon.
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Bwat.Common;

namespace Bwat.Client
{
    // ======================================================================
    //  Balloon drawing (shared by QuestionBox and BalloonGrid)
    // ======================================================================
    public static class BalloonPainter
    {
        /// <summary>Draws a balloon. state = BalloonState.*, age = milliseconds since the state changed.</summary>
        public static void Draw(Graphics g, float cx, float cy, float size, string state, int triesUsed, double age)
        {
            if (state == BalloonState.Popped)
            {
                if (age > 900) return;                      // gone
                float t = (float)Math.Min(1, age / 500);
                using (Pen p = new Pen(Theme.Alpha(Theme.Red, (int)(255 * (1 - t))), Math.Max(2, size / 22)))
                {
                    for (int i = 0; i < 10; i++)
                    {
                        double a = i * Math.PI / 5;
                        float r1 = size * (0.2f + 0.4f * t), r2 = size * (0.35f + 0.55f * t);
                        g.DrawLine(p, cx + (float)Math.Cos(a) * r1, cy + (float)Math.Sin(a) * r1, cx + (float)Math.Cos(a) * r2, cy + (float)Math.Sin(a) * r2);
                    }
                }
                using (Font f = Theme.B(Math.Max(8, size / 4.5f)))
                    Theme.Center(g, "POP!", f, Theme.Alpha(Theme.Red, (int)(255 * (1 - t * 0.7f))), new RectangleF(cx - size, cy - size / 3, size * 2, size / 1.5f));
                return;
            }

            float scale = triesUsed == 0 ? 1f : triesUsed == 1 ? 0.82f : 0.66f;
            Color color = Theme.Blue;
            int alpha = 255;
            if (state == BalloonState.Shrunk) color = Theme.Amber;
            if (state == BalloonState.Floated)
            {
                color = Theme.Green;
                scale = 1f;
                float up = (float)Math.Min(1, age / 1600);
                cy -= up * up * size * 3.2f;
                alpha = (int)(255 * Math.Max(0, 1 - Math.Max(0, up - 0.6f) / 0.4f));
                if (alpha <= 0) return;
            }
            else if (state == BalloonState.Shrunk && age < 300)
            {
                cx += (float)Math.Sin(age / 25) * 4;   // little shake on a wrong answer
            }

            float w = size * 0.8f * scale, h = size * scale;
            RectangleF body = new RectangleF(cx - w / 2, cy - h / 2, w, h);
            using (Pen str = new Pen(Theme.Alpha(Theme.Muted, alpha), 1.5f))
                g.DrawBezier(str, cx, body.Bottom + 5, cx - 8, body.Bottom + 18, cx + 8, body.Bottom + 30, cx, body.Bottom + 42 * scale);
            using (SolidBrush b = new SolidBrush(Theme.Alpha(color, alpha)))
            {
                g.FillEllipse(b, body);
                g.FillPolygon(b, new PointF[] { new PointF(cx, body.Bottom - 2), new PointF(cx - 6, body.Bottom + 6), new PointF(cx + 6, body.Bottom + 6) });
            }
            using (SolidBrush shine = new SolidBrush(Theme.Alpha(Color.White, alpha / 3)))
                g.FillEllipse(shine, body.X + w * 0.2f, body.Y + h * 0.14f, w * 0.24f, h * 0.3f);
        }
    }

    // ======================================================================
    public class QuestionBox : Control
    {
        public enum Mode { Message, Question, Result }

        private Mode mode = Mode.Message;
        private readonly Timer anim = new Timer();

        // message mode
        private string title = "", line1 = "", line2 = "";
        private DateTime countdownTo = DateTime.MinValue;

        // question mode
        private string header = "", question = "", difficulty = "";
        private int marks = 5, seconds = 30, timerRound = 1;
        private DateTime endsAt;
        public bool ShowBalloon = true;
        private string balloon = BalloonState.Full;
        private DateTime balloonChanged = DateTime.Now;
        private int triesUsed;
        private string answerKey = "";   // admins only

        // result mode
        private string resultTitle = "", resultLine = "";
        private Color resultColor = Theme.Green;

        public QuestionBox()
        {
            Theme.DoubleBuffer(this);
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            anim.Interval = 40;
            anim.Tick += delegate { Invalidate(); };
            anim.Start();
        }

        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

        public void ShowMessage(string title, string line1, string line2, DateTime countdownTo)
        {
            mode = Mode.Message;
            this.title = title;
            this.line1 = line1;
            this.line2 = line2;
            this.countdownTo = countdownTo;
        }

        public void ShowQuestion(int round, int total, string text, int secs, int questionMarks, int diff, int timer)
        {
            mode = Mode.Question;
            header = "Question " + round + " of " + total;
            question = text;
            seconds = Math.Max(1, secs);
            endsAt = DateTime.Now.AddSeconds(secs);
            marks = questionMarks;
            difficulty = diff >= 3 ? "Hard" : diff == 2 ? "Medium" : "Easy";
            timerRound = timer;
            answerKey = "";
            SetBalloon(BalloonState.Full, 0);
        }

        public void RestartTimer(int secs, int timer)
        {
            seconds = Math.Max(1, secs);
            endsAt = DateTime.Now.AddSeconds(secs);
            timerRound = timer;
        }

        public void SetAnswerKey(string answer) { answerKey = answer; }

        public void SetBalloon(string state, int tries)
        {
            if (state != balloon || tries != triesUsed) balloonChanged = DateTime.Now;
            balloon = state;
            triesUsed = tries;
        }

        public string BalloonNow { get { return balloon; } }

        public void ShowResult(string title, string line, Color color)
        {
            mode = Mode.Result;
            resultTitle = title;
            resultLine = line;
            resultColor = color;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.HQ(g);
            g.Clear(BackColor);
            RectangleF box = new RectangleF(1, 1, Width - 3, Height - 3);
            if (box.Width < 50 || box.Height < 50) return;
            Theme.FillRound(g, box, 16, Theme.Board);

            if (mode == Mode.Message)
            {
                using (Font f = Theme.B(26f)) Theme.Center(g, title, f, Theme.Ink, new RectangleF(box.X + 20, box.Y + box.Height * 0.16f, box.Width - 40, 50));
                string l1 = line1;
                if (countdownTo > DateTime.Now)
                {
                    int s = (int)Math.Ceiling((countdownTo - DateTime.Now).TotalSeconds);
                    l1 = "Starts in " + (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
                }
                using (Font f = Theme.B(16f)) Theme.Center(g, l1, f, Theme.Blue, new RectangleF(box.X + 20, box.Y + box.Height * 0.16f + 56, box.Width - 40, 34));
                using (Font f = Theme.F(12f)) Theme.Center(g, line2, f, Theme.Muted, new RectangleF(box.X + 30, box.Y + box.Height * 0.16f + 96, box.Width - 60, box.Height * 0.4f));
                return;
            }

            float balloonW = ShowBalloon ? Math.Min(170, box.Width * 0.2f) : 0;
            RectangleF main = new RectangleF(box.X + 24, box.Y + 14, box.Width - 48 - balloonW, box.Height - 28);

            // top line: question number, difficulty, marks, timer
            double left = Math.Max(0, (endsAt - DateTime.Now).TotalSeconds);
            using (Font f = Theme.B(11f))
            {
                Theme.DrawText(g, header + "   •   " + difficulty + "   •   " + marks + " marks", f, Theme.Muted, new RectangleF(main.X, main.Y, main.Width * 0.7f, 24), StringAlignment.Near);
                string timer = mode == Mode.Question ? ((int)Math.Ceiling(left)) + " s" + (timerRound > 1 ? "   (timer " + timerRound + " of 3)" : "") : "";
                Theme.DrawText(g, timer, f, left <= 5 && mode == Mode.Question ? Theme.Red : Theme.Ink, new RectangleF(main.X + main.Width * 0.5f, main.Y, main.Width * 0.5f, 24), StringAlignment.Far);
            }

            // the question
            float bottomSpace = mode == Mode.Result ? 86 : (answerKey.Length > 0 ? 50 : 26);
            RectangleF q = new RectangleF(main.X, main.Y + 30, main.Width, main.Height - 30 - bottomSpace);
            using (Font f = Theme.FitFont(g, question, 30f, 12f, true, q))
                Theme.Center(g, question, f, Theme.Ink, q);

            if (answerKey.Length > 0 && mode == Mode.Question)
                using (Font f = Theme.B(12f)) Theme.Center(g, "Answer (only you can see this):  " + answerKey, f, Theme.Green, new RectangleF(main.X, q.Bottom, main.Width, 24));

            // timer bar
            if (mode == Mode.Question)
            {
                float frac = (float)Math.Min(1, left / seconds);
                RectangleF bar = new RectangleF(main.X, main.Bottom - 8, main.Width, 8);
                Theme.FillRound(g, bar, 4, Theme.Border);
                Color c = frac > 0.5f ? Theme.Blue : frac > 0.2f ? Theme.Amber : Theme.Red;
                if (frac > 0) Theme.FillRound(g, new RectangleF(bar.X, bar.Y, bar.Width * frac, 8), 4, c);
            }
            else
            {
                using (Font f = Theme.B(24f)) Theme.Center(g, resultTitle, f, resultColor, new RectangleF(main.X, main.Bottom - 84, main.Width, 42));
                using (Font f = Theme.F(12.5f)) Theme.Center(g, resultLine, f, Theme.Ink, new RectangleF(main.X, main.Bottom - 42, main.Width, 30));
            }

            // the balloon and the tries
            if (ShowBalloon)
            {
                RectangleF area = new RectangleF(box.Right - balloonW - 10, box.Y + 10, balloonW, box.Height - 20);
                using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, area.X, area.Y + 10, area.X, area.Bottom - 10);
                float size = Math.Min(area.Width * 0.6f, area.Height * 0.42f);
                BalloonPainter.Draw(g, area.X + area.Width / 2, area.Y + area.Height * 0.38f, size, balloon, triesUsed,
                                    (DateTime.Now - balloonChanged).TotalMilliseconds);
                string status = balloon == BalloonState.Floated ? "You got it!" :
                                balloon == BalloonState.Popped ? "Popped" : "Tries left: " + (3 - triesUsed);
                using (Font f = Theme.B(10.5f)) Theme.Center(g, status, f, balloon == BalloonState.Popped ? Theme.Red : Theme.Ink, new RectangleF(area.X, area.Bottom - 54, area.Width, 22));
                for (int i = 0; i < 3; i++)
                {
                    RectangleF dot = new RectangleF(area.X + area.Width / 2 - 27 + i * 20, area.Bottom - 26, 14, 14);
                    using (SolidBrush b = new SolidBrush(i < 3 - triesUsed && balloon != BalloonState.Popped ? Theme.Blue : Theme.Border)) g.FillEllipse(b, dot);
                }
            }
        }
    }

    // ======================================================================
    public class Calculator : Control
    {
        public delegate void SubmitHandler(string answer);
        public event SubmitHandler Submitted;

        private class Key { public string Label; public RectangleF Rect; }
        private readonly List<Key> keys = new List<Key>();
        private readonly Dictionary<string, DateTime> flash = new Dictionary<string, DateTime>();
        private readonly Timer anim = new Timer();
        private string input = "";
        private bool enabledForInput;
        private string status = "Waiting for the question ...";
        private string hover;

        public Calculator()
        {
            Theme.DoubleBuffer(this);
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            anim.Interval = 60;
            anim.Tick += delegate { Invalidate(); };
            anim.Start();
        }

        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

        public void SetReady(bool ready, string statusText)
        {
            enabledForInput = ready;
            status = statusText;
            Invalidate();
        }

        public void ClearInput() { input = ""; Invalidate(); }

        public void PressKey(string k)
        {
            flash[k] = DateTime.Now;
            if (!enabledForInput) return;
            if (k == "C") input = "";
            else if (k == "⌫") { if (input.Length > 0) input = input.Substring(0, input.Length - 1); }
            else if (k == "=")
            {
                if (input.Trim().Length == 0) { status = "Type your answer first"; return; }
                if (Submitted != null) Submitted(input.Trim());
            }
            else if (input.Length < 14) input += k == "−" ? "-" : k;
            Invalidate();
        }

        private Key KeyAt(Point p)
        {
            foreach (Key k in keys) if (k.Rect.Contains(p)) return k;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Key k = KeyAt(e.Location);
            hover = k != null ? k.Label : null;
            Cursor = k != null && enabledForInput ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Key k = KeyAt(e.Location);
            if (k != null) PressKey(k.Label);
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.HQ(g);
            g.Clear(BackColor);
            RectangleF body = new RectangleF(1, 1, Width - 3, Height - 3);
            if (body.Width < 80 || body.Height < 120) return;
            Theme.FillRound(g, body, 16, Theme.CalcBody);

            // display
            RectangleF display = new RectangleF(body.X + 16, body.Y + 16, body.Width - 32, 78);
            Theme.FillRound(g, display, 10, Theme.Ink);
            using (Font f = Theme.F(10f)) Theme.DrawText(g, status, f, Theme.MutedOnDark, new RectangleF(display.X + 12, display.Y + 4, display.Width - 24, 22), StringAlignment.Near);
            string shown = input.Length == 0 ? "0" : input;
            bool cursor = enabledForInput && (DateTime.Now.Millisecond / 500) % 2 == 0;
            using (Font f = Theme.B(input.Length > 10 ? 20f : 28f))
                Theme.DrawText(g, shown + (cursor ? "|" : " "), f, enabledForInput ? Theme.Board : Theme.MutedOnDark,
                               new RectangleF(display.X + 10, display.Y + 24, display.Width - 20, display.Height - 26), StringAlignment.Far);

            // keys
            keys.Clear();
            RectangleF pad = new RectangleF(body.X + 16, display.Bottom + 14, body.Width - 32, body.Bottom - display.Bottom - 30);
            float gap = 8, kw = (pad.Width - gap * 3) / 4, kh = (pad.Height - gap * 4) / 5;
            string[,] grid = { { "C", "⌫", "/", "−" }, { "7", "8", "9", "=" }, { "4", "5", "6", "=" }, { "1", "2", "3", "=" }, { "0", "0", ".", "=" } };
            for (int row = 0; row < 5; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    string k = grid[row, col];
                    if ((k == "=" && row > 1) || (k == "0" && col == 1)) continue;
                    Key key = new Key();
                    key.Label = k;
                    key.Rect = new RectangleF(pad.X + col * (kw + gap), pad.Y + row * (kh + gap),
                                              k == "0" ? kw * 2 + gap : kw, k == "=" ? kh * 4 + gap * 3 : kh);
                    keys.Add(key);
                    DrawKey(g, key);
                }
            }
        }

        private void DrawKey(Graphics g, Key k)
        {
            Color face = k.Label == "=" ? Theme.KeyEquals : (k.Label == "C" || k.Label == "⌫") ? Theme.KeyAction : Theme.KeyNumber;
            DateTime t;
            bool pressed = flash.TryGetValue(k.Label, out t) && (DateTime.Now - t).TotalMilliseconds < 120;
            if (!enabledForInput) face = Theme.Mix(face, Theme.CalcBody, 0.55f);
            else if (pressed) face = Theme.Mix(face, Color.Black, 0.2f);
            else if (hover == k.Label) face = Theme.Mix(face, Color.White, 0.12f);
            Theme.FillRound(g, k.Rect, 10, face);
            using (Font f = Theme.B(k.Label == "=" ? 24f : Math.Max(12f, Math.Min(19f, k.Rect.Height * 0.36f))))
                Theme.Center(g, k.Label == "/" ? "÷" : k.Label, f, enabledForInput ? Color.White : Theme.MutedOnDark, k.Rect);
        }
    }

    // ======================================================================
    public class BoardEntry
    {
        public string Name;
        public int Marks;
        public bool Online = true;
        public int Rank;
    }

    /// <summary>Gives equal marks the same position (1, 1, 3 ...). The list must already be sorted by the server.</summary>
    public static class Ranking
    {
        public static void Apply(List<BoardEntry> list)
        {
            for (int i = 0; i < list.Count; i++)
                list[i].Rank = (i > 0 && list[i].Marks == list[i - 1].Marks) ? list[i - 1].Rank : i + 1;
        }
    }

    public class ActiveBoard : Control
    {
        private List<BoardEntry> entries = new List<BoardEntry>();
        public string Me;                     // highlighted name (the player using this client)
        public string Selected;               // admin: selected name (for Remove player)
        public bool Selectable;
        public event EventHandler SelectionChanged;
        private int scroll;
        private const int RowH = 44;
        private const int HeaderH = 46;

        public ActiveBoard()
        {
            Theme.DoubleBuffer(this);
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        public void SetEntries(List<BoardEntry> list)
        {
            entries = list;
            Ranking.Apply(entries);
            scroll = Math.Max(0, Math.Min(scroll, MaxScroll));
            Invalidate();
        }

        private int MaxScroll { get { return Math.Max(0, entries.Count * RowH - (Height - HeaderH - 10)); } }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120 * RowH));
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (Selectable && e.Y > HeaderH)
            {
                int i = (e.Y - HeaderH + scroll) / RowH;
                if (i >= 0 && i < entries.Count)
                {
                    Selected = entries[i].Name;
                    Invalidate();
                    if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.HQ(g);
            g.Clear(BackColor);
            RectangleF box = new RectangleF(1, 1, Width - 3, Height - 3);
            if (box.Width < 60) return;
            Theme.FillRound(g, box, 16, Theme.CalcBody);
            using (Font f = Theme.B(12f)) Theme.DrawText(g, "Active board", f, Color.White, new RectangleF(box.X + 16, box.Y + 8, box.Width - 32, 30), StringAlignment.Near);
            using (Font f = Theme.F(9.5f)) Theme.DrawText(g, entries.Count + (entries.Count == 1 ? " player" : " players"), f, Theme.MutedOnDark, new RectangleF(box.X + 16, box.Y + 8, box.Width - 32, 30), StringAlignment.Far);

            g.SetClip(new RectangleF(box.X, HeaderH, box.Width, box.Height - HeaderH - 6));
            using (Font name = Theme.B(11.5f))
            using (Font small = Theme.B(11f))
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    float y = HeaderH + i * RowH - scroll;
                    if (y + RowH < HeaderH || y > Height) continue;
                    BoardEntry en = entries[i];
                    RectangleF card = new RectangleF(box.X + 10, y, box.Width - 20, RowH - 6);
                    Theme.FillRound(g, card, 8, Theme.Card);
                    bool me = Me != null && string.Equals(en.Name, Me, StringComparison.OrdinalIgnoreCase);
                    bool sel = Selected != null && string.Equals(en.Name, Selected, StringComparison.OrdinalIgnoreCase);
                    Theme.StrokeRound(g, card, 8, sel ? Theme.Red : me ? Theme.Blue : Theme.Border, sel || me ? 2.5f : 1);
                    Theme.DrawText(g, en.Rank.ToString(), small, Theme.Muted, new RectangleF(card.X + 8, card.Y, 30, card.Height), StringAlignment.Near);
                    Theme.DrawText(g, en.Name + (me ? " (you)" : "") + (en.Online ? "" : "  - offline"), name,
                                   en.Online ? Theme.NameColor(en.Name) : Theme.Muted, new RectangleF(card.X + 40, card.Y, card.Width - 110, card.Height), StringAlignment.Near);
                    Theme.DrawText(g, en.Marks.ToString(), name, Theme.Ink, new RectangleF(card.Right - 70, card.Y, 60, card.Height), StringAlignment.Far);
                }
            }
            g.ResetClip();
        }
    }

    // ======================================================================
    public class StandingRow
    {
        public string Name;
        public int Marks, Gained, Rank, PreviousRank, Solved;
    }

    /// <summary>Everybody's position (between questions), or the final results with the top 3 / BONGO.</summary>
    public class StandingsView : Control
    {
        private List<StandingRow> rows = new List<StandingRow>();
        private string title = "", subtitle = "";
        private bool final, bongo;
        public string Me;

        public StandingsView()
        {
            Theme.DoubleBuffer(this);
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        public void ShowStandings(int round, int total, List<StandingRow> list)
        {
            rows = list;
            final = false;
            title = "Standings";
            subtitle = "after question " + round + " of " + total + "  -  the next question is coming";
            Invalidate();
        }

        public void ShowFinal(bool isBongo, List<StandingRow> list)
        {
            rows = list;
            final = true;
            bongo = isBongo;
            title = isBongo ? "BONGO!" : "Final results";
            subtitle = isBongo ? (list.Count > 0 && list[0].Marks == 0 ? "Nobody scored - everybody lost!" : "It's a draw for first place!") : "Well done everybody!";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.HQ(g);
            g.Clear(BackColor);
            RectangleF box = new RectangleF(1, 1, Width - 3, Height - 3);
            if (box.Width < 100 || box.Height < 100) return;
            Theme.FillRound(g, box, 16, Theme.Board);

            using (Font f = Theme.B(bongo && final ? 40f : 26f))
                Theme.Center(g, title, f, bongo && final ? Theme.Red : Theme.Ink, new RectangleF(box.X, box.Y + 10, box.Width, bongo && final ? 64 : 44));
            float y = box.Y + (bongo && final ? 74 : 54);
            using (Font f = Theme.F(12f)) Theme.Center(g, subtitle, f, Theme.Muted, new RectangleF(box.X, y, box.Width, 24));
            y += 36;

            // final results: the top three (when there is a winner)
            if (final && !bongo)
            {
                Color[] medal = { Theme.Gold, Theme.Silver, Theme.Bronze };
                int n = 0;
                while (n < 3 && n < rows.Count && rows[n].Marks > 0) n++;   // medals only for players who scored
                float w = Math.Min(240, (box.Width - 80) / 3), h = 110, gap = 20;
                float startX = box.X + (box.Width - (n * w + (n - 1) * gap)) / 2;
                for (int i = 0; i < n; i++)
                {
                    StandingRow r = rows[i];
                    RectangleF card = new RectangleF(startX + i * (w + gap), y + (i == 0 ? 0 : 14), w, h - (i == 0 ? 0 : 14));
                    Theme.FillRound(g, card, 12, Theme.Card);
                    Theme.StrokeRound(g, card, 12, medal[Math.Min(2, r.Rank - 1)], 3);
                    using (Font f = Theme.B(12f)) Theme.Center(g, Theme.Ordinal(r.Rank), f, medal[Math.Min(2, r.Rank - 1)], new RectangleF(card.X, card.Y + 6, card.Width, 24));
                    using (Font f = Theme.B(17f)) Theme.Center(g, r.Name, f, Theme.NameColor(r.Name), new RectangleF(card.X + 6, card.Y + 30, card.Width - 12, 34));
                    using (Font f = Theme.F(11f)) Theme.Center(g, r.Marks + " marks", f, Theme.Ink, new RectangleF(card.X, card.Bottom - 32, card.Width, 24));
                }
                if (n > 0) y += h + 20;
            }

            // everybody, in columns
            if (rows.Count == 0) return;
            float cellW = 250, cellH = 34;
            int cols = Math.Max(1, (int)((box.Width - 40) / cellW));
            int perCol = (int)Math.Ceiling(rows.Count / (double)cols);
            float maxRows = (box.Bottom - y - 14) / cellH;
            if (perCol > maxRows && maxRows >= 1) { cellH = Math.Max(20, (box.Bottom - y - 14) / perCol); }
            cellW = (box.Width - 40) / cols;
            using (Font f = Theme.B(cellH < 28 ? 9.5f : 11f))
            using (Font sf = Theme.F(cellH < 28 ? 8.5f : 9.5f))
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    StandingRow r = rows[i];
                    int col = i / perCol, row = i % perCol;
                    RectangleF cell = new RectangleF(box.X + 20 + col * cellW, y + row * cellH, cellW - 12, cellH - 4);
                    bool me = Me != null && string.Equals(r.Name, Me, StringComparison.OrdinalIgnoreCase);
                    Theme.FillRound(g, cell, 6, me ? ColorTranslator.FromHtml("#DBEAFE") : Theme.Card);
                    Theme.StrokeRound(g, cell, 6, Theme.Border, 1);
                    Theme.DrawText(g, Theme.Ordinal(r.Rank), f, Theme.Muted, new RectangleF(cell.X + 6, cell.Y, 40, cell.Height), StringAlignment.Near);
                    Theme.DrawText(g, r.Name, f, Theme.NameColor(r.Name), new RectangleF(cell.X + 48, cell.Y, cell.Width - 140, cell.Height), StringAlignment.Near);
                    string extra = final ? "" : r.Gained > 0 ? "+" + r.Gained : r.PreviousRank > r.Rank ? "▲" : r.PreviousRank < r.Rank ? "▼" : "";
                    Color ec = r.Gained > 0 || r.PreviousRank > r.Rank ? Theme.Green : Theme.Red;
                    Theme.DrawText(g, extra, sf, ec, new RectangleF(cell.Right - 92, cell.Y, 36, cell.Height), StringAlignment.Near);
                    Theme.DrawText(g, r.Marks.ToString(), f, Theme.Ink, new RectangleF(cell.Right - 56, cell.Y, 48, cell.Height), StringAlignment.Far);
                }
            }
        }
    }

    // ======================================================================
    public class BalloonCell
    {
        public string Name;
        public string State = BalloonState.Full;
        public int Tries;
        public bool Online = true;
        public DateTime Changed = DateTime.Now;
    }

    /// <summary>Admin view: every player's balloon and tries for the current question. Click a player to select.</summary>
    public class BalloonGrid : Control
    {
        private readonly List<BalloonCell> cells = new List<BalloonCell>();
        private readonly Timer anim = new Timer();
        public string Selected;
        public event EventHandler SelectionChanged;
        private readonly List<KeyValuePair<RectangleF, string>> hit = new List<KeyValuePair<RectangleF, string>>();

        public BalloonGrid()
        {
            Theme.DoubleBuffer(this);
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            anim.Interval = 60;
            anim.Tick += delegate { Invalidate(); };
            anim.Start();
        }

        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

        private BalloonCell Find(string name)
        {
            foreach (BalloonCell c in cells) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        /// <summary>Keeps the list of players in sync with the PLAYERS message.</summary>
        public void SetPlayers(List<BoardEntry> players)
        {
            List<BalloonCell> keep = new List<BalloonCell>();
            foreach (BoardEntry p in players)
            {
                BalloonCell c = Find(p.Name);
                if (c == null) { c = new BalloonCell(); c.Name = p.Name; }
                c.Online = p.Online;
                keep.Add(c);
            }
            keep.Sort(delegate(BalloonCell a, BalloonCell b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            cells.Clear();
            cells.AddRange(keep);
        }

        public void ResetAll()
        {
            foreach (BalloonCell c in cells) { c.State = BalloonState.Full; c.Tries = 0; c.Changed = DateTime.Now; }
        }

        public void SetBalloon(string name, string state, int tries)
        {
            BalloonCell c = Find(name);
            if (c == null) return;
            c.State = state;
            c.Tries = tries;
            c.Changed = DateTime.Now;
        }

        /// <summary>At the end of a question: the winner floats, everybody else pops.</summary>
        public void Finish(string winner)
        {
            foreach (BalloonCell c in cells)
            {
                string s = string.Equals(c.Name, winner, StringComparison.OrdinalIgnoreCase) ? BalloonState.Floated : BalloonState.Popped;
                if (c.State != s) { c.State = s; c.Changed = DateTime.Now; }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            foreach (KeyValuePair<RectangleF, string> h in hit)
            {
                if (h.Key.Contains(e.Location))
                {
                    Selected = h.Value;
                    if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                    break;
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.HQ(g);
            g.Clear(BackColor);
            RectangleF box = new RectangleF(1, 1, Width - 3, Height - 3);
            if (box.Width < 100 || box.Height < 60) return;
            Theme.FillRound(g, box, 16, Theme.CalcBody);
            using (Font f = Theme.B(12f)) Theme.DrawText(g, "Players' balloons  (click a player to select)", f, Color.White, new RectangleF(box.X + 16, box.Y + 8, box.Width - 32, 30), StringAlignment.Near);
            hit.Clear();
            if (cells.Count == 0) return;

            RectangleF area = new RectangleF(box.X + 10, box.Y + 44, box.Width - 20, box.Height - 52);
            int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(cells.Count * area.Width / area.Height)));
            int rowsN = (int)Math.Ceiling(cells.Count / (double)cols);
            float cw = area.Width / cols, ch = area.Height / Math.Max(1, rowsN);
            using (Font f = Theme.B(Math.Max(7f, Math.Min(10f, ch / 7))))
            {
                for (int i = 0; i < cells.Count; i++)
                {
                    BalloonCell c = cells[i];
                    RectangleF cell = new RectangleF(area.X + (i % cols) * cw + 3, area.Y + (i / cols) * ch + 3, cw - 6, ch - 6);
                    bool sel = Selected != null && string.Equals(Selected, c.Name, StringComparison.OrdinalIgnoreCase);
                    Theme.FillRound(g, cell, 8, Theme.Card);
                    Theme.StrokeRound(g, cell, 8, sel ? Theme.Red : Theme.Border, sel ? 2.5f : 1);
                    float size = Math.Min(cell.Width * 0.5f, cell.Height * 0.48f);
                    GraphicsState st = g.Save();
                    g.SetClip(cell);
                    BalloonPainter.Draw(g, cell.X + cell.Width / 2, cell.Y + cell.Height * 0.36f, size, c.State, c.Tries, (DateTime.Now - c.Changed).TotalMilliseconds);
                    g.Restore(st);
                    string label = c.Name + (c.Tries > 0 && c.State != BalloonState.Floated ? "  ✗" + c.Tries : "");
                    Theme.Center(g, label, f, c.Online ? Theme.NameColor(c.Name) : Theme.Muted, new RectangleF(cell.X + 2, cell.Bottom - Math.Max(16, ch * 0.24f), cell.Width - 4, Math.Max(16, ch * 0.22f)));
                    hit.Add(new KeyValuePair<RectangleF, string>(cell, c.Name));
                }
            }
        }
    }
}
