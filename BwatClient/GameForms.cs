/*
 * Bwat Client / GameForms.cs
 *
 * PlayerGameForm - what a player sees (layout from the design sketch):
 *     +------------------------------------------+
 *     |  QUESTION (whiteboard) ........ balloon   |
 *     +------------------------+-----------------+
 *     |  CALCULATOR            |  ACTIVE BOARD    |
 *     +------------------------+-----------------+
 *   Between questions the standings of everybody cover the screen; at the end the
 *   final results (top 3 or BONGO).
 *
 * AdminRoomForm - what an admin sees when watching a room: the question WITH the
 *   answer, every player's balloon, the active board, and the controls
 *   Start now / Skip question / Remove player / End game / Copy link.
 *
 * Both are THIN clients: they only show what the server says. The server decides
 * whether an answer is right and who gets the marks.
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Bwat.Common;
using Message = Bwat.Common.Message;  // (WinForms also has a class called Message)

namespace Bwat.Client
{
    /// <summary>Shared message parsing for both game windows.</summary>
    public static class GameMessages
    {
        public static List<BoardEntry> Players(Message m)
        {
            // PLAYERS|count|(nickname|marks|online)*count   (already sorted by the server)
            List<BoardEntry> list = new List<BoardEntry>();
            int n = m.GetInt(0, 0);
            for (int i = 0; i < n; i++)
            {
                BoardEntry e = new BoardEntry();
                e.Name = m.Get(1 + i * 3);
                e.Marks = m.GetInt(2 + i * 3, 0);
                e.Online = m.Get(3 + i * 3) == "1";
                list.Add(e);
            }
            return list;
        }

        public static List<StandingRow> Standings(Message m)
        {
            // STANDINGS|round|total|count|(nickname|marks|gained|rank|previousRank)*count
            List<StandingRow> rows = new List<StandingRow>();
            int n = m.GetInt(2, 0);
            for (int i = 0; i < n; i++)
            {
                int b = 3 + i * 5;
                StandingRow r = new StandingRow();
                r.Name = m.Get(b);
                r.Marks = m.GetInt(b + 1, 0);
                r.Gained = m.GetInt(b + 2, 0);
                r.Rank = m.GetInt(b + 3, i + 1);
                r.PreviousRank = m.GetInt(b + 4, r.Rank);
                rows.Add(r);
            }
            return rows;
        }

        public static List<StandingRow> Final(Message m)
        {
            // FINAL_RESULTS|bongo|count|(nickname|marks|solved|place)*count
            List<StandingRow> rows = new List<StandingRow>();
            int n = m.GetInt(1, 0);
            for (int i = 0; i < n; i++)
            {
                int b = 2 + i * 4;
                StandingRow r = new StandingRow();
                r.Name = m.Get(b);
                r.Marks = m.GetInt(b + 1, 0);
                r.Solved = m.GetInt(b + 2, 0);
                r.Rank = m.GetInt(b + 3, i + 1);
                r.PreviousRank = r.Rank;
                rows.Add(r);
            }
            return rows;
        }
    }

    // ======================================================================
    public class PlayerGameForm : Form
    {
        private readonly string code, nickname, link;
        private string status;
        private bool finished;
        private int round;

        private readonly Label lblRoom, lblMe, lblInfo;
        private readonly ModernButton btnLeave;
        private readonly QuestionBox question;
        private readonly Calculator calculator;
        private readonly ActiveBoard board;
        private readonly StandingsView standings;
        private readonly Timer infoTimer = new Timer();

        public PlayerGameForm(Message joined)
        {
            // ROOM_JOINED|code|name|levelId|questionCount|secondsToStart|watching|status|nickname|link
            code = joined.Get(0);
            nickname = joined.Get(7);
            link = joined.Get(8);
            status = joined.Get(6);
            Session.Nickname = nickname;

            Text = "Bwat - " + joined.Get(1);
            ClientSize = new Size(1100, 760);
            MinimumSize = new Size(900, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = Theme.F(10f);
            KeyPreview = true;

            lblRoom = Theme.MakeLabel(joined.Get(1) + "   •   " + code + "   •   " + Levels.Name(joined.GetInt(2, 1)), 12f, true, Color.White);
            lblRoom.Location = new Point(18, 16);
            Controls.Add(lblRoom);
            lblInfo = Theme.MakeLabel("", 10.5f, true, Theme.Amber);
            Controls.Add(lblInfo);
            lblMe = Theme.MakeLabel(nickname + "  •  0 marks", 12f, true, Color.White);
            Controls.Add(lblMe);
            btnLeave = new ModernButton("Leave", Theme.Red, 100, 36);
            btnLeave.Outline = true;
            btnLeave.Click += delegate { Close(); };
            Controls.Add(btnLeave);

            question = new QuestionBox();
            calculator = new Calculator();
            calculator.Submitted += OnSubmit;
            board = new ActiveBoard();
            board.Me = nickname;
            standings = new StandingsView();
            standings.Me = nickname;
            standings.Visible = false;
            Controls.Add(standings);
            Controls.Add(question);
            Controls.Add(calculator);
            Controls.Add(board);

            infoTimer.Interval = 4500;
            infoTimer.Tick += delegate { lblInfo.Text = ""; infoTimer.Stop(); };

            if (status == "Playing")
                question.ShowMessage("Welcome back, " + nickname + "!", "The quiz is running", "The next question will appear here.", DateTime.MinValue);
            else
                question.ShowMessage("Hi " + nickname + ", you're in!", "Waiting for the quiz to start",
                                     "5 marks per question - only the FIRST correct answer gets them. You have 3 tries.\nInvite friends with this link:  " + link,
                                     DateTime.Now.AddSeconds(joined.GetInt(4, 0)));
            calculator.SetReady(false, "Waiting for the first question ...");

            Session.Connection.MessageReceived += OnMessage;
            DoLayout();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); DoLayout(); }

        private void DoLayout()
        {
            if (question == null) return;
            int pad = 16, top = 60;
            int w = ClientSize.Width - pad * 2, h = ClientSize.Height - top - pad;
            btnLeave.Location = new Point(ClientSize.Width - pad - btnLeave.Width, 11);
            lblMe.Location = new Point(btnLeave.Left - lblMe.PreferredWidth - 20, 18);
            lblInfo.Location = new Point(lblRoom.Right + 30, 19);
            int qh = (int)(h * 0.42);
            question.Bounds = new Rectangle(pad, top, w, qh);
            int calcW = Math.Max(360, Math.Min(560, (int)(w * 0.58)));
            calculator.Bounds = new Rectangle(pad, top + qh + pad, calcW, h - qh - pad);
            board.Bounds = new Rectangle(pad + calcW + pad, top + qh + pad, w - calcW - pad, h - qh - pad);
            standings.Bounds = new Rectangle(pad, top, w, h);
        }

        private void Info(string text)
        {
            lblInfo.Text = text;
            infoTimer.Stop();
            infoTimer.Start();
        }

        private void ShowStandingsOverlay(bool on)
        {
            standings.Visible = on;
            if (on) standings.BringToFront();
        }

        private void OnSubmit(string answer)
        {
            calculator.SetReady(false, "Checking ...");
            if (!Session.Connection.Send(Cmd.Answer, round, answer)) calculator.SetReady(true, "Not connected to the server");
        }

        private void OnMessage(Message m)
        {
            if (IsDisposed) return;
            switch (m.Command)
            {
                case Cmd.Players:
                {
                    List<BoardEntry> list = GameMessages.Players(m);
                    board.SetEntries(list);
                    foreach (BoardEntry e in list)
                        if (string.Equals(e.Name, nickname, StringComparison.OrdinalIgnoreCase))
                            lblMe.Text = nickname + "  •  " + e.Marks + " marks  •  " + Theme.Ordinal(e.Rank);
                    DoLayout();
                    break;
                }
                case Cmd.Info: Info(m.Get(0)); break;

                case Cmd.GameStart:
                    status = "Playing";
                    question.ShowMessage("Get ready!", m.Get(0) + " questions  •  " + m.Get(1) + " marks each",
                                         "Only the FIRST correct answer gets the marks. You have 3 tries per question.", DateTime.MinValue);
                    break;

                case Cmd.Question:
                    // QUESTION|round|total|text|seconds|marks|difficulty|timerRound
                    ShowStandingsOverlay(false);
                    round = m.GetInt(0, 1);
                    question.ShowQuestion(round, m.GetInt(1, 1), m.Get(2), m.GetInt(3, 30), m.GetInt(4, 5), m.GetInt(5, 1), m.GetInt(6, 1));
                    calculator.ClearInput();
                    calculator.SetReady(true, "Type your answer and press =");
                    Activate();
                    break;

                case Cmd.AnswerWrong:
                {
                    int left = m.GetInt(1, 0);
                    calculator.ClearInput();
                    if (left > 0)
                    {
                        question.SetBalloon(BalloonState.Shrunk, 3 - left);
                        calculator.SetReady(true, "Wrong! Tries left: " + left);
                    }
                    else
                    {
                        question.SetBalloon(BalloonState.Popped, 3);
                        calculator.SetReady(false, "Your balloon popped - wait for the next question");
                    }
                    break;
                }

                case Cmd.TimerRestart:
                    question.RestartTimer(m.GetInt(1, 30), m.GetInt(2, 2));
                    Info("Nobody has got it yet - the timer restarts (" + m.Get(2) + " of 3)");
                    break;

                case Cmd.RoundResult:
                {
                    // ROUND_RESULT|round|winner|answer|marks|seconds
                    string winner = m.Get(1), answer = m.Get(2);
                    calculator.SetReady(false, "Next question coming ...");
                    if (string.Equals(winner, nickname, StringComparison.OrdinalIgnoreCase))
                    {
                        question.SetBalloon(BalloonState.Floated, 0);
                        question.ShowResult("You got it!  +" + m.Get(3) + " marks", "Answer: " + answer + "   •   " + m.Get(4) + " s", Theme.Green);
                    }
                    else
                    {
                        if (question.BalloonNow != BalloonState.Popped) question.SetBalloon(BalloonState.Popped, 3);
                        if (winner.Length > 0) question.ShowResult(winner + " got it first!", "The answer was " + answer, Theme.Blue);
                        else question.ShowResult("BONGO!", "Nobody got it. The answer was " + answer, Theme.Red);
                    }
                    break;
                }

                case Cmd.Standings:
                    standings.ShowStandings(m.GetInt(0, 0), m.GetInt(1, 0), GameMessages.Standings(m));
                    ShowStandingsOverlay(true);
                    break;

                case Cmd.FinalResults:
                    finished = true;
                    standings.ShowFinal(m.Get(0) == "1", GameMessages.Final(m));
                    ShowStandingsOverlay(true);
                    btnLeave.Text = "Close";
                    break;

                case Cmd.Kicked:
                    finished = true;
                    MessageBox.Show(this, m.Get(0), "Removed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Close();
                    break;

                case Cmd.RoomCancelled:
                    finished = true;
                    ShowStandingsOverlay(false);
                    question.ShowMessage("Quiz closed", "", m.Get(0), DateTime.MinValue);
                    calculator.SetReady(false, "The quiz is closed");
                    btnLeave.Text = "Close";
                    break;

                case Cmd.Error:
                    if (m.Get(0) == Cmd.Answer)
                    {
                        bool canRetry = question.BalloonNow != BalloonState.Popped && m.Get(1).IndexOf("closed", StringComparison.OrdinalIgnoreCase) < 0;
                        calculator.SetReady(canRetry, m.Get(1));
                    }
                    break;
            }
        }

        /// <summary>Typing on the real keyboard works like pressing the calculator keys.</summary>
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            char ch = e.KeyChar;
            if (char.IsDigit(ch)) calculator.PressKey(ch.ToString());
            else if (ch == '.' || ch == ',') calculator.PressKey(".");
            else if (ch == '-') calculator.PressKey("−");
            else if (ch == '/') calculator.PressKey("/");
            else if (ch == '\b') calculator.PressKey("⌫");
            else if (ch == '\r' || ch == '=') calculator.PressKey("=");
            else if (ch == (char)27) calculator.PressKey("C");
            else { base.OnKeyPress(e); return; }
            e.Handled = true;
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Enter) { calculator.PressKey("="); return true; }
            if (keyData == Keys.Delete) { calculator.PressKey("C"); return true; }
            return base.ProcessDialogKey(keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!finished && status == "Playing" && e.CloseReason == CloseReason.UserClosing &&
                MessageBox.Show(this, "The quiz is still running. You can come back with the same link and name and keep your marks.\n\nLeave anyway?",
                                "Leave the quiz?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            infoTimer.Stop();
            ServerConnection c = Session.Connection;
            if (c != null)
            {
                c.MessageReceived -= OnMessage;
                if (c.IsConnected && !finished) c.Send(Cmd.Leave);
            }
            base.OnFormClosed(e);
        }
    }

    // ======================================================================
    public class AdminRoomForm : Form
    {
        private readonly string code, link;
        private bool finished, started;
        private int playerCount;

        private readonly Label lblRoom, lblStatus;
        private readonly FlowLayoutPanel buttons;
        private readonly QuestionBox question;
        private readonly BalloonGrid grid;
        private readonly ActiveBoard board;
        private readonly StandingsView standings;
        private DateTime startsAt;

        public AdminRoomForm(Message joined)
        {
            // ROOM_JOINED|code|name|levelId|questionCount|secondsToStart|watching|status|nickname|link
            code = joined.Get(0);
            link = joined.Get(8);
            startsAt = DateTime.Now.AddSeconds(joined.GetInt(4, 0));

            Text = "Bwat - Admin - " + joined.Get(1) + " (" + code + ")";
            ClientSize = new Size(1240, 800);
            MinimumSize = new Size(1000, 660);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = Theme.F(10f);

            lblRoom = Theme.MakeLabel(joined.Get(1) + "   •   " + code + "   •   " + Levels.Name(joined.GetInt(2, 1)) + "   •   " + joined.Get(3) + " questions", 12f, true, Color.White);
            lblRoom.Location = new Point(18, 14);
            Controls.Add(lblRoom);
            Label lblLink = Theme.MakeLabel("Link:  " + link, 10f, false, Theme.MutedOnDark);
            lblLink.Location = new Point(20, 40);
            Controls.Add(lblLink);

            buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.BackColor = Theme.Bg;
            buttons.Height = 44;
            buttons.Controls.Add(Btn("Copy link", Theme.Blue, true, delegate { CopyLink(); }));
            buttons.Controls.Add(Btn("Start now", Theme.Green, false, delegate { RoomCommand(Cmd.RoomStart, null); }));
            buttons.Controls.Add(Btn("Skip question", Theme.Amber, false, delegate { RoomCommand(Cmd.RoomSkip, null); }));
            buttons.Controls.Add(Btn("Remove player", Theme.Red, true, delegate { Kick(); }));
            buttons.Controls.Add(Btn("End game", Theme.Red, false, delegate { RoomCommand(Cmd.RoomEnd, "End the quiz now and show the final results?"); }));
            buttons.Controls.Add(Btn("Close", Theme.MutedOnDark, true, delegate { Close(); }));
            Controls.Add(buttons);

            lblStatus = Theme.MakeLabel("", 10.5f, true, Theme.Amber);
            Controls.Add(lblStatus);

            question = new QuestionBox();
            question.ShowBalloon = false;
            grid = new BalloonGrid();
            grid.SelectionChanged += delegate { board.Selected = grid.Selected; board.Invalidate(); };
            board = new ActiveBoard();
            board.Selectable = true;
            board.SelectionChanged += delegate { grid.Selected = board.Selected; };
            standings = new StandingsView();
            standings.Visible = false;
            Controls.Add(standings);
            Controls.Add(question);
            Controls.Add(grid);
            Controls.Add(board);

            started = joined.Get(6) == "Playing";
            if (started)
                question.ShowMessage("Quiz in progress", "", "The next question will appear here.", DateTime.MinValue);
            else
                ShowLobby();

            Session.Connection.MessageReceived += OnMessage;
            DoLayout();
        }

        private void ShowLobby()
        {
            question.ShowMessage("Waiting for players  -  " + playerCount + " joined", "Starting soon",
                                 "Share this link (or the code " + code + "):\n" + link + "\nClick 'Start now' when everybody is in.", startsAt);
        }

        private static ModernButton Btn(string text, Color color, bool outline, EventHandler click)
        {
            ModernButton b = new ModernButton(text, color, Math.Max(96, text.Length * 8 + 28), 38);
            b.Outline = outline;
            b.Margin = new Padding(0, 0, 8, 0);
            b.Click += click;
            return b;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); DoLayout(); }

        private void DoLayout()
        {
            if (question == null) return;
            int pad = 16, top = 116;
            int w = ClientSize.Width - pad * 2, h = ClientSize.Height - top - pad;
            buttons.Bounds = new Rectangle(pad, 66, w, 44);
            int qh = (int)(h * 0.36);
            question.Bounds = new Rectangle(pad, top, w, qh);
            int boardW = Math.Max(280, Math.Min(380, (int)(w * 0.3)));
            grid.Bounds = new Rectangle(pad, top + qh + pad, w - boardW - pad, h - qh - pad - 26);
            board.Bounds = new Rectangle(pad + w - boardW, top + qh + pad, boardW, h - qh - pad - 26);
            lblStatus.Location = new Point(pad + 2, ClientSize.Height - pad - 20);
            standings.Bounds = new Rectangle(pad, top, w - boardW - pad, h - 26);
        }

        private void Status(string text, bool error)
        {
            lblStatus.ForeColor = error ? Theme.Red : Theme.Green;
            lblStatus.Text = text;
        }

        private void CopyLink()
        {
            try { Clipboard.SetText(link); Status("Link copied - share it with the players.", false); }
            catch (Exception) { Status("Link: " + link, false); }
        }

        private void RoomCommand(string command, string confirm)
        {
            if (confirm != null && MessageBox.Show(this, confirm, "Bwat", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Session.Connection.Send(command, code);
        }

        private void Kick()
        {
            string name = grid.Selected ?? board.Selected;
            if (string.IsNullOrEmpty(name)) { Status("Click a player first (in the balloons or on the active board).", true); return; }
            if (MessageBox.Show(this, "Remove " + name + " from the quiz?", "Remove player", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Session.Connection.Send(Cmd.RoomKick, code, name);
        }

        private void OnMessage(Message m)
        {
            if (IsDisposed) return;
            switch (m.Command)
            {
                case Cmd.Players:
                {
                    List<BoardEntry> list = GameMessages.Players(m);
                    playerCount = list.Count;
                    board.SetEntries(list);
                    grid.SetPlayers(list);
                    break;
                }
                case Cmd.Info: Status(m.Get(0), false); break;
                case Cmd.GameStart:
                    question.ShowMessage("Get ready!", m.Get(0) + " questions", "The players see a 'Get ready' screen.", DateTime.MinValue);
                    break;
                case Cmd.Question:
                    standings.Visible = false;
                    question.ShowQuestion(m.GetInt(0, 1), m.GetInt(1, 1), m.Get(2), m.GetInt(3, 30), m.GetInt(4, 5), m.GetInt(5, 1), m.GetInt(6, 1));
                    grid.ResetAll();
                    break;
                case Cmd.AnswerKey: question.SetAnswerKey(m.Get(1)); break;
                case Cmd.Balloon: grid.SetBalloon(m.Get(0), m.Get(1), m.GetInt(2, 0)); break;
                case Cmd.TimerRestart:
                    question.RestartTimer(m.GetInt(1, 30), m.GetInt(2, 2));
                    Status("Nobody has got it yet - timer " + m.Get(2) + " of 3", false);
                    break;
                case Cmd.RoundResult:
                {
                    string winner = m.Get(1);
                    grid.Finish(winner);
                    if (winner.Length > 0) question.ShowResult(winner + " got it first!  +" + m.Get(3) + " marks", "Answer: " + m.Get(2) + "   •   " + m.Get(4) + " s", Theme.Green);
                    else question.ShowResult("BONGO!", "Nobody got it. The answer was " + m.Get(2), Theme.Red);
                    break;
                }
                case Cmd.Standings:
                    standings.ShowStandings(m.GetInt(0, 0), m.GetInt(1, 0), GameMessages.Standings(m));
                    standings.Visible = true;
                    standings.BringToFront();
                    break;
                case Cmd.FinalResults:
                    finished = true;
                    standings.ShowFinal(m.Get(0) == "1", GameMessages.Final(m));
                    standings.Visible = true;
                    standings.BringToFront();
                    Status("The quiz is finished. The results are saved in 'Past games'.", false);
                    break;
                case Cmd.RoomCancelled:
                    finished = true;
                    question.ShowMessage("Room closed", "", m.Get(0), DateTime.MinValue);
                    break;
                case Cmd.Ok:
                    if (m.Get(0) == Cmd.RoomSkip || m.Get(0) == Cmd.RoomKick || m.Get(0) == Cmd.RoomEnd || m.Get(0) == Cmd.RoomStart)
                    {
                        Status(m.Get(1), false);
                        if (m.Get(0) == Cmd.RoomKick) { grid.Selected = null; board.Selected = null; }
                    }
                    break;
                case Cmd.Error:
                    if (m.Get(0) == Cmd.RoomSkip || m.Get(0) == Cmd.RoomKick || m.Get(0) == Cmd.RoomEnd || m.Get(0) == Cmd.RoomStart)
                        Status(m.Get(1), true);
                    break;
            }
            if (m.Command == Cmd.GameStart || m.Command == Cmd.Question) started = true;
            if (m.Command == Cmd.Players && !started && !finished) ShowLobby();   // keep the lobby text up to date
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ServerConnection c = Session.Connection;
            if (c != null)
            {
                c.MessageReceived -= OnMessage;
                if (c.IsConnected && !finished) c.Send(Cmd.Leave);
            }
            base.OnFormClosed(e);
        }
    }
}
