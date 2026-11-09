/*
 * Bwat Client / AdminForm.cs
 *
 * The admin side. Only a logged-in admin sees this screen, and the server checks
 * the role again for every admin request (the client is never trusted).
 *
 * Pages (left sidebar):
 *   Rooms        - create a quiz room (any name, up to 40 players), copy the link,
 *                  Open it to watch and control it, start it, cancel it
 *   Questions    - the question bank: add / edit / delete (CRUD); difficulty sets the time
 *   Past games   - every finished game and its final standings (saved in the database)
 *   Admins       - add admin accounts, activate / deactivate them
 *   Activity log - logins, failed logins, joins, admin actions and denied requests
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Bwat.Common;
using Message = Bwat.Common.Message;  // (WinForms also has a class called Message)

namespace Bwat.Client
{
    public class AdminForm : Form
    {
        private readonly List<Panel> pages = new List<Panel>();
        private readonly List<NavItem> navItems = new List<NavItem>();
        private Label lblStatusBar;

        // rooms
        private InputBox txtRoomName;
        private ComboBox cboRoomLevel;
        private NumericUpDown numPlayers, numQuestions, numStartIn;
        private FancyList lstRooms;

        // questions
        private ComboBox cboFilter, cboQLevel, cboDifficulty;
        private FancyList lstQuestions;
        private InputBox txtQuestion, txtAnswer;
        private Label lblEditing;
        private int editingQuestionId;

        // past games, admins, activity
        private FancyList lstGames, lstResults, lstAdmins, lstAudit;
        private Label lblResults;
        private InputBox txtNewUser, txtNewName, txtNewPass;

        public AdminForm()
        {
            Text = "Bwat - Admin (" + Session.AdminName + ")";
            ClientSize = new Size(1240, 780);
            MinimumSize = new Size(1080, 680);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = Theme.F(10f);
            BuildUi();

            Session.Connection.MessageReceived += OnMessage;
            Session.Connection.ListReceived += OnList;
            Shown += delegate { ShowPage(0); };
        }

        // ==================================================================
        //  Layout
        // ==================================================================

        private class NavItem : Control
        {
            public bool Selected;
            private bool hover;
            public NavItem(string text) { Theme.DoubleBuffer(this); Text = text; Height = 44; Cursor = Cursors.Hand; }
            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Theme.HQ(g);
                g.Clear(Theme.Ink);
                RectangleF r = new RectangleF(12, 3, Width - 24, Height - 6);
                if (Selected) Theme.FillRound(g, r, 8, Theme.Blue);
                else if (hover) Theme.FillRound(g, r, 8, Theme.CalcBody);
                using (Font f = Theme.B(11f)) Theme.DrawText(g, Text, f, Selected ? Color.White : Theme.MutedOnDark, new RectangleF(r.X + 16, r.Y, r.Width - 20, r.Height), StringAlignment.Near);
            }
        }

        private void BuildUi()
        {
            Panel side = new Panel();
            side.Dock = DockStyle.Left;
            side.Width = 220;
            side.BackColor = Theme.Ink;
            Label logo = Theme.MakeLabel("Bwat", 22f, true, Color.White);
            logo.Location = new Point(22, 20);
            side.Controls.Add(logo);
            Label role = Theme.MakeLabel("ADMIN  •  " + Session.AdminName, 9f, true, Theme.MutedOnDark);
            role.Location = new Point(25, 62);
            side.Controls.Add(role);
            string[] names = { "Rooms", "Questions", "Past games", "Admins", "Activity log" };
            for (int i = 0; i < names.Length; i++)
            {
                NavItem item = new NavItem(names[i]);
                item.Location = new Point(0, 100 + i * 48);
                item.Width = 220;
                int page = i;
                item.Click += delegate { ShowPage(page); };
                navItems.Add(item);
                side.Controls.Add(item);
            }
            ModernButton logout = new ModernButton("Log out", Theme.MutedOnDark, 172, 38);
            logout.Outline = true;
            logout.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            logout.Location = new Point(24, ClientSize.Height - 62);
            logout.Click += delegate { Close(); };
            side.Controls.Add(logout);

            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(18, 12, 18, 6);

            lblStatusBar = new Label();
            lblStatusBar.Dock = DockStyle.Bottom;
            lblStatusBar.Height = 30;
            lblStatusBar.TextAlign = ContentAlignment.MiddleLeft;
            lblStatusBar.Font = Theme.B(10f);
            lblStatusBar.ForeColor = Theme.MutedOnDark;
            lblStatusBar.Text = "Ready.";

            pages.Add(BuildRoomsPage());
            pages.Add(BuildQuestionsPage());
            pages.Add(BuildGamesPage());
            pages.Add(BuildAdminsPage());
            pages.Add(BuildAuditPage());
            foreach (Panel p in pages) { p.Dock = DockStyle.Fill; p.Visible = false; content.Controls.Add(p); }
            content.Controls.Add(lblStatusBar);

            Controls.Add(content);
            Controls.Add(side);
        }

        private static Panel Page(string title, string subtitle)
        {
            Panel p = new Panel();
            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 66;
            Label t = Theme.MakeLabel(title, 19f, true, Color.White);
            t.Location = new Point(2, 2);
            head.Controls.Add(t);
            Label s = Theme.MakeLabel(subtitle, 10f, false, Theme.MutedOnDark);
            s.Location = new Point(4, 40);
            head.Controls.Add(s);
            p.Controls.Add(head);
            return p;
        }

        /// <summary>Makes sure the page title stays on top of the docked cards.</summary>
        private static Panel Finish(Panel page)
        {
            foreach (Control c in page.Controls)
                if (c.Dock == DockStyle.Top && c.Height == 66) { c.SendToBack(); break; }
            return page;
        }

        private static FlowLayoutPanel ButtonRow()
        {
            FlowLayoutPanel row = new FlowLayoutPanel();
            row.Dock = DockStyle.Top;
            row.Height = 52;
            row.BackColor = Color.White;
            return row;
        }

        private static ModernButton Btn(string text, Color color, bool outline, EventHandler click)
        {
            ModernButton b = new ModernButton(text, color, Math.Max(84, text.Length * 8 + 26), 38);
            b.Outline = outline;
            b.Margin = new Padding(0, 0, 8, 0);
            b.Click += click;
            return b;
        }

        private static void Field(CardPanel card, string text, int x, int y)
        {
            card.Put(Theme.MakeLabel(text, 8.5f, true, Theme.Muted), x, y);
        }

        private Panel BuildRoomsPage()
        {
            Panel page = Page("Quiz rooms", "Create a room, copy the link and share it. Players open the link and type their name.");

            CardPanel form = new CardPanel();
            form.Dock = DockStyle.Left;
            form.Width = 350;
            form.Put(Theme.MakeLabel("New quiz room", 14f, true, Theme.Ink), 24, 20);
            int x = 24, y = 62;
            Field(form, "ROOM NAME (anything you like)", x, y);
            txtRoomName = form.Put(new InputBox(294, 11f), x, y + 18);
            txtRoomName.Text = "Friday maths quiz";
            y += 74;
            Field(form, "CLASS LEVEL", x, y);
            cboRoomLevel = form.Put(Theme.MakeCombo(Levels.All(), 294), x, y + 20);
            y += 62;
            Field(form, "MAX PLAYERS (2-40)", x, y); numPlayers = form.Put(Theme.MakeNumber(2, 40, 40, 120), x, y + 20);
            Field(form, "QUESTIONS", x + 160, y); numQuestions = form.Put(Theme.MakeNumber(1, 50, 10, 120), x + 160, y + 20);
            y += 62;
            Field(form, "STARTS IN (MINUTES)", x, y); numStartIn = form.Put(Theme.MakeNumber(0, 1440, 5, 120), x, y + 20);
            y += 66;
            form.Put(Theme.MakeLabel("5 marks per question - only the first correct\nanswer gets them. Time: easy 30 s, medium 45 s,\nhard 60 s. Questions go from easy to hard.", 9f, false, Theme.Muted), x, y);
            y += 66;
            ModernButton create = form.Put(new ModernButton("Create room", Theme.Blue, 294, 46), x, y);
            create.Click += delegate { CreateRoom(); };

            Panel gap = new Panel(); gap.Dock = DockStyle.Left; gap.Width = 10;

            CardPanel list = new CardPanel();
            list.Dock = DockStyle.Fill;
            list.Padding = new Padding(22, 18, 26, 26);
            FlowLayoutPanel buttons = ButtonRow();
            buttons.Controls.Add(Btn("Refresh", Theme.Blue, true, delegate { Session.Connection.Send(Cmd.RoomList); }));
            buttons.Controls.Add(Btn("Copy link", Theme.Blue, true, delegate { CopyLink(); }));
            buttons.Controls.Add(Btn("Open", Theme.Blue, false, delegate { OpenRoom(); }));
            buttons.Controls.Add(Btn("Start now", Theme.Green, false, delegate { RoomAction(Cmd.RoomStart); }));
            buttons.Controls.Add(Btn("Cancel", Theme.Red, false, delegate { RoomAction(Cmd.RoomCancel); }));
            lstRooms = new FancyList();
            lstRooms.Dock = DockStyle.Fill;
            lstRooms.EmptyText = "No rooms yet - create one on the left.";
            lstRooms.AddColumn("Code", 105, StringAlignment.Near).AddColumn("Room", -1, StringAlignment.Near)
                    .AddColumn("Status", 90, StringAlignment.Near).AddColumn("Players", 75, StringAlignment.Center)
                    .AddColumn("Starts", 110, StringAlignment.Near);
            lstRooms.RowDoubleClick += delegate { OpenRoom(); };
            list.Controls.Add(lstRooms);
            list.Controls.Add(buttons);

            page.Controls.Add(list);
            page.Controls.Add(gap);
            page.Controls.Add(form);
            return Finish(page);
        }

        private Panel BuildQuestionsPage()
        {
            Panel page = Page("Question bank", "Players type the answer. Several accepted answers can be separated by ;  (e.g. 15/4;3.75)");

            CardPanel listCard = new CardPanel();
            listCard.Dock = DockStyle.Fill;
            listCard.Padding = new Padding(22, 16, 26, 24);
            Panel filter = new Panel();
            filter.Dock = DockStyle.Top;
            filter.Height = 48;
            filter.BackColor = Color.White;
            Label l = Theme.MakeLabel("Show level", 10f, true, Theme.Ink);
            l.Location = new Point(0, 12);
            filter.Controls.Add(l);
            List<object> levels = new List<object>();
            levels.Add("All levels");
            levels.AddRange(Levels.All());
            cboFilter = Theme.MakeCombo(levels.ToArray(), 240);
            cboFilter.Location = new Point(90, 8);
            cboFilter.SelectedIndexChanged += delegate { Session.Connection.Send(Cmd.QuestionList, cboFilter.SelectedIndex); };
            filter.Controls.Add(cboFilter);
            lstQuestions = new FancyList();
            lstQuestions.Dock = DockStyle.Fill;
            lstQuestions.RowHeight = 38;
            lstQuestions.AddColumn("ID", 55, StringAlignment.Near).AddColumn("Level", 165, StringAlignment.Near)
                        .AddColumn("Question", -1, StringAlignment.Near).AddColumn("Answer", 100, StringAlignment.Near)
                        .AddColumn("Difficulty", 120, StringAlignment.Near).AddColumn("Played", 65, StringAlignment.Center)
                        .AddColumn("Solved", 70, StringAlignment.Center);
            lstQuestions.SelectionChanged += delegate { LoadSelectedQuestion(); };
            listCard.Controls.Add(lstQuestions);
            listCard.Controls.Add(filter);

            CardPanel editor = new CardPanel();
            editor.Dock = DockStyle.Bottom;
            editor.Height = 230;
            lblEditing = editor.Put(Theme.MakeLabel("New question", 13f, true, Theme.Ink), 24, 16);
            Field(editor, "QUESTION", 24, 52);
            txtQuestion = editor.Put(new InputBox(420, 11f), 24, 70);
            txtQuestion.MakeMultiline(128);
            Field(editor, "CORRECT ANSWER", 462, 52);
            txtAnswer = editor.Put(new InputBox(200, 11f), 462, 70);
            Field(editor, "LEVEL", 678, 52);
            cboQLevel = editor.Put(Theme.MakeCombo(Levels.All(), 210), 678, 74);
            Field(editor, "DIFFICULTY (sets the time)", 462, 128);
            cboDifficulty = editor.Put(Theme.MakeCombo(new object[] { "Auto (smart engine)", "Easy - 30 s", "Medium - 45 s", "Hard - 60 s" }, 200), 462, 148);
            editor.Put(Btn("New", Theme.Blue, true, delegate { ClearEditor(); }), 678, 142);
            editor.Put(Btn("Save", Theme.Green, false, delegate { SaveQuestion(); }), 780, 142);
            editor.Put(Btn("Delete", Theme.Red, false, delegate { DeleteQuestion(); }), 880, 142);

            Panel gap = new Panel(); gap.Dock = DockStyle.Bottom; gap.Height = 10;
            page.Controls.Add(listCard);
            page.Controls.Add(gap);
            page.Controls.Add(editor);
            return Finish(page);
        }

        private Panel BuildGamesPage()
        {
            Panel page = Page("Past games", "Every game is saved. Select a game to see everybody's final position.");
            CardPanel games = new CardPanel();
            games.Dock = DockStyle.Fill;
            games.Padding = new Padding(22, 18, 26, 24);
            FlowLayoutPanel buttons = ButtonRow();
            buttons.Controls.Add(Btn("Refresh", Theme.Blue, true, delegate { Session.Connection.Send(Cmd.GamesList); }));
            lstGames = new FancyList();
            lstGames.Dock = DockStyle.Fill;
            lstGames.AddColumn("Code", 105, StringAlignment.Near).AddColumn("Room", -1, StringAlignment.Near)
                    .AddColumn("Level", 160, StringAlignment.Near).AddColumn("Date", 150, StringAlignment.Near)
                    .AddColumn("Status", 95, StringAlignment.Near).AddColumn("Players", 70, StringAlignment.Center)
                    .AddColumn("Winner", 130, StringAlignment.Near);
            lstGames.SelectionChanged += delegate
            {
                FancyList.Row r = lstGames.SelectedRow;
                if (r != null) { lblResults.Text = "Final standings - " + r.Cells[1] + " (" + r.Cells[0] + ")"; Session.Connection.Send(Cmd.GameResults, r.Cells[0]); }
            };
            games.Controls.Add(lstGames);
            games.Controls.Add(buttons);

            CardPanel results = new CardPanel();
            results.Dock = DockStyle.Bottom;
            results.Height = 270;
            results.Padding = new Padding(22, 50, 26, 24);
            lblResults = results.Put(Theme.MakeLabel("Final standings - select a game above", 12f, true, Theme.Ink), 24, 16);
            lstResults = new FancyList();
            lstResults.Dock = DockStyle.Fill;
            lstResults.NameColumn = 1;
            lstResults.BadgeFirstColumn = true;
            lstResults.EmptyText = "Select a game above.";
            lstResults.AddColumn("#", 56, StringAlignment.Near).AddColumn("Name", -1, StringAlignment.Near)
                      .AddColumn("Marks", 90, StringAlignment.Far).AddColumn("Solved", 80, StringAlignment.Center)
                      .AddColumn("Note", 120, StringAlignment.Near);
            results.Controls.Add(lstResults);

            Panel gap = new Panel(); gap.Dock = DockStyle.Bottom; gap.Height = 10;
            page.Controls.Add(games);
            page.Controls.Add(gap);
            page.Controls.Add(results);
            return Finish(page);
        }

        private Panel BuildAdminsPage()
        {
            Panel page = Page("Admins", "Only admins have accounts. Players join with a name only.");
            CardPanel form = new CardPanel();
            form.Dock = DockStyle.Left;
            form.Width = 330;
            form.Put(Theme.MakeLabel("Add an admin", 14f, true, Theme.Ink), 24, 20);
            Field(form, "USERNAME", 24, 62); txtNewUser = form.Put(new InputBox(276, 11f), 24, 80);
            Field(form, "DISPLAY NAME", 24, 136); txtNewName = form.Put(new InputBox(276, 11f), 24, 154);
            Field(form, "PASSWORD (min 6)", 24, 210); txtNewPass = form.Put(new InputBox(276, 11f), 24, 228);
            txtNewPass.Password = true;
            form.Put(new ModernButton("Add admin", Theme.Blue, 276, 44), 24, 290).Click += delegate { CreateAdmin(); };

            Panel gap = new Panel(); gap.Dock = DockStyle.Left; gap.Width = 10;

            CardPanel list = new CardPanel();
            list.Dock = DockStyle.Fill;
            list.Padding = new Padding(22, 18, 26, 26);
            FlowLayoutPanel buttons = ButtonRow();
            buttons.Controls.Add(Btn("Refresh", Theme.Blue, true, delegate { Session.Connection.Send(Cmd.AdminList); }));
            buttons.Controls.Add(Btn("Activate", Theme.Green, false, delegate { SetAdminActive(true); }));
            buttons.Controls.Add(Btn("Deactivate", Theme.Red, false, delegate { SetAdminActive(false); }));
            lstAdmins = new FancyList();
            lstAdmins.Dock = DockStyle.Fill;
            lstAdmins.AddColumn("ID", 55, StringAlignment.Near).AddColumn("Username", 160, StringAlignment.Near)
                     .AddColumn("Display name", -1, StringAlignment.Near).AddColumn("Active", 70, StringAlignment.Center)
                     .AddColumn("Created", 160, StringAlignment.Near);
            list.Controls.Add(lstAdmins);
            list.Controls.Add(buttons);

            page.Controls.Add(list);
            page.Controls.Add(gap);
            page.Controls.Add(form);
            return Finish(page);
        }

        private Panel BuildAuditPage()
        {
            Panel page = Page("Activity log", "Logins, failed logins, players joining, admin actions and denied requests (newest first).");
            CardPanel card = new CardPanel();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(22, 18, 26, 26);
            FlowLayoutPanel buttons = ButtonRow();
            buttons.Controls.Add(Btn("Refresh", Theme.Blue, true, delegate { Session.Connection.Send(Cmd.AuditList); }));
            lstAudit = new FancyList();
            lstAudit.Dock = DockStyle.Fill;
            lstAudit.RowHeight = 36;
            lstAudit.AddColumn("Time", 170, StringAlignment.Near).AddColumn("Who", 220, StringAlignment.Near)
                    .AddColumn("Action", 150, StringAlignment.Near).AddColumn("Detail", -1, StringAlignment.Near);
            card.Controls.Add(lstAudit);
            card.Controls.Add(buttons);
            page.Controls.Add(card);
            return Finish(page);
        }

        private void ShowPage(int index)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Visible = i == index;
                navItems[i].Selected = i == index;
                navItems[i].Invalidate();
            }
            switch (index)
            {
                case 0: Session.Connection.Send(Cmd.RoomList); break;
                case 1: Session.Connection.Send(Cmd.QuestionList, cboFilter.SelectedIndex); break;
                case 2: Session.Connection.Send(Cmd.GamesList); break;
                case 3: Session.Connection.Send(Cmd.AdminList); break;
                case 4: Session.Connection.Send(Cmd.AuditList); break;
            }
        }

        private void Status(string text, bool error)
        {
            lblStatusBar.ForeColor = error ? Theme.Red : Theme.Green;
            lblStatusBar.Text = (error ? "✗  " : "✓  ") + text;
        }

        private static int ParseInt(string s) { int v; return int.TryParse(s, out v) ? v : 0; }

        // ==================================================================
        //  Rooms
        // ==================================================================

        private FancyList.Row SelectedRoom()
        {
            FancyList.Row r = lstRooms.SelectedRow;
            if (r == null) Status("Select a room in the list first.", true);
            return r;
        }

        private void CreateRoom()
        {
            string name = txtRoomName.Text.Trim();
            if (name.Length == 0) { Status("Give the room a name.", true); return; }
            Session.Connection.Send(Cmd.RoomCreate, name, cboRoomLevel.SelectedIndex + 1, (int)numPlayers.Value, (int)numQuestions.Value, (int)numStartIn.Value);
            Status("Creating room ...", false);
        }

        private void CopyLink()
        {
            FancyList.Row r = SelectedRoom();
            if (r == null) return;
            string link = (string)r.Tag;
            try { Clipboard.SetText(link); Status("Link copied: " + link, false); }
            catch (Exception) { Status("Link: " + link, false); }
        }

        private void OpenRoom()
        {
            FancyList.Row r = SelectedRoom();
            if (r != null) Session.Connection.Send(Cmd.Join, r.Cells[0]);
        }

        private void RoomAction(string command)
        {
            FancyList.Row r = SelectedRoom();
            if (r == null) return;
            if (command == Cmd.RoomCancel &&
                MessageBox.Show(this, "Cancel room " + r.Cells[0] + "?", "Cancel room", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            Session.Connection.Send(command, r.Cells[0]);
        }

        // ==================================================================
        //  Questions
        // ==================================================================

        private void LoadSelectedQuestion()
        {
            FancyList.Row row = lstQuestions.SelectedRow;
            if (row == null) return;
            string[] q = (string[])row.Tag;  // id|levelId|text|answer|difficulty|manual|asked|solved
            editingQuestionId = int.Parse(q[0]);
            cboQLevel.SelectedIndex = Math.Max(0, int.Parse(q[1]) - 1);
            txtQuestion.Text = q[2];
            txtAnswer.Text = q[3];
            cboDifficulty.SelectedIndex = q[5] == "1" ? int.Parse(q[4]) : 0;
            lblEditing.Text = "Editing question #" + editingQuestionId;
        }

        private void ClearEditor()
        {
            editingQuestionId = 0;
            txtQuestion.Text = "";
            txtAnswer.Text = "";
            cboDifficulty.SelectedIndex = 0;
            if (cboFilter.SelectedIndex > 0) cboQLevel.SelectedIndex = cboFilter.SelectedIndex - 1;
            lblEditing.Text = "New question";
            lstQuestions.ClearSelection();
            txtQuestion.Box.Focus();
        }

        private void SaveQuestion()
        {
            string q = txtQuestion.Text.Trim().Replace("\r", " ").Replace("\n", " ");
            string a = txtAnswer.Text.Trim();
            if (q.Length < 5) { Status("Write the question (at least 5 characters).", true); return; }
            if (a.Length == 0) { Status("Write the correct answer.", true); return; }
            Session.Connection.Send(Cmd.QuestionSave, editingQuestionId, cboQLevel.SelectedIndex + 1, q, a, cboDifficulty.SelectedIndex);
        }

        private void DeleteQuestion()
        {
            if (editingQuestionId == 0) { Status("Select a question to delete.", true); return; }
            if (MessageBox.Show(this, "Delete question #" + editingQuestionId + "?", "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Session.Connection.Send(Cmd.QuestionDelete, editingQuestionId);
        }

        // ==================================================================
        //  Admins
        // ==================================================================

        private void CreateAdmin()
        {
            if (txtNewUser.Text.Trim().Length < 3 || txtNewPass.Text.Length < 6) { Status("Username (3+ letters) and password (6+ characters) are needed.", true); return; }
            Session.Connection.Send(Cmd.AdminCreate, txtNewUser.Text.Trim(), txtNewName.Text.Trim().Length > 0 ? txtNewName.Text.Trim() : txtNewUser.Text.Trim(), txtNewPass.Text);
        }

        private void SetAdminActive(bool active)
        {
            FancyList.Row r = lstAdmins.SelectedRow;
            if (r == null) { Status("Select an admin first.", true); return; }
            Session.Connection.Send(Cmd.AdminSetActive, r.Cells[0], active ? "1" : "0");
        }

        // ==================================================================
        //  Server messages
        // ==================================================================

        private void OnMessage(Message m)
        {
            if (IsDisposed) return;
            if (m.Command == Cmd.RoomJoined && Visible)
            {
                AdminRoomForm room = new AdminRoomForm(m);   // watch and control a room
                room.FormClosed += delegate { if (!IsDisposed) { Show(); Session.Connection.Send(Cmd.RoomList); } };
                Hide();
                room.Show();
                return;
            }
            if (!Visible) return;
            if (m.Command == Cmd.Info) { Status(m.Get(0), false); return; }
            if (m.Command != Cmd.Ok && m.Command != Cmd.Error) return;

            string cmd = m.Get(0);
            bool ok = m.Command == Cmd.Ok;
            if (cmd == Cmd.RoomCreate && ok)
            {
                string link = m.Get(2);
                try { Clipboard.SetText(link); } catch (Exception) { }
                Session.Connection.Send(Cmd.RoomList);
                Status("Room " + m.Get(1) + " created - link copied to the clipboard.", false);
                MessageBox.Show(this, "Room created!\n\nShare this link with the players (it was copied to the clipboard):\n\n" + link +
                                      "\n\nPlayers can also type the code " + m.Get(1) + ".\nSelect the room and click 'Open' to watch and control it.",
                                "Room created", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Status(m.Get(1), !ok);
            if (!ok) return;
            if (cmd == Cmd.RoomStart || cmd == Cmd.RoomCancel) Session.Connection.Send(Cmd.RoomList);
            else if (cmd == Cmd.QuestionSave || cmd == Cmd.QuestionDelete) { ClearEditor(); Session.Connection.Send(Cmd.QuestionList, cboFilter.SelectedIndex); }
            else if (cmd == Cmd.AdminCreate) { txtNewUser.Text = ""; txtNewName.Text = ""; txtNewPass.Text = ""; Session.Connection.Send(Cmd.AdminList); }
            else if (cmd == Cmd.AdminSetActive) Session.Connection.Send(Cmd.AdminList);
        }

        private void OnList(string type, List<string[]> rows)
        {
            if (IsDisposed) return;
            List<FancyList.Row> items = new List<FancyList.Row>();
            if (type == ListType.Rooms)
            {
                // code|name|levelId|status|players|maxPlayers|startTime|questions|link
                foreach (string[] r in rows)
                {
                    FancyList.Row row = new FancyList.Row();
                    string start = r[6].Length >= 16 ? r[6].Substring(11, 5) + "  " + r[6].Substring(8, 2) + "/" + r[6].Substring(5, 2) : r[6];
                    row.Cells = new string[] { r[0], r[1] + "  •  " + r[7] + " Qs", r[3], r[4] + " / " + r[5], start };
                    row.Tag = r[8];
                    if (r[3] == "Waiting") { row.ForeColor = Theme.Blue; row.Bold = true; }
                    else if (r[3] == "Playing") { row.ForeColor = Theme.Green; row.Bold = true; }
                    else if (r[3] != "Finished") row.ForeColor = Theme.Muted;
                    items.Add(row);
                }
                lstRooms.SetRows(items);
            }
            else if (type == ListType.Questions)
            {
                // id|levelId|text|answer|difficulty|manual|asked|solved
                string[] names = { "", "Easy - 30 s", "Medium - 45 s", "Hard - 60 s" };
                foreach (string[] r in rows)
                {
                    int asked = ParseInt(r[6]), solved = ParseInt(r[7]), d = Math.Max(1, Math.Min(3, ParseInt(r[4])));
                    FancyList.Row row = new FancyList.Row();
                    row.Cells = new string[] { r[0], Levels.Name(ParseInt(r[1])), r[2], r[3].Split(';')[0],
                                               names[d] + (r[5] == "1" ? " *" : ""), r[6], asked == 0 ? "-" : (100 * solved / asked) + "%" };
                    row.Tag = r;
                    items.Add(row);
                }
                lstQuestions.SetRows(items);
            }
            else if (type == ListType.Games)
            {
                // code|name|levelId|date|status|players|winner|winnerMarks
                foreach (string[] r in rows)
                {
                    FancyList.Row row = new FancyList.Row();
                    row.Cells = new string[] { r[0], r[1], Levels.Name(ParseInt(r[2])), r[3], r[4], r[5], r[6] };
                    if (r[6] == "BONGO") row.ForeColor = Theme.Red;
                    items.Add(row);
                }
                lstGames.SetRows(items);
            }
            else if (type == ListType.GameResults)
            {
                // place|nickname|marks|solved|removed
                foreach (string[] r in rows)
                {
                    FancyList.Row row = new FancyList.Row();
                    int place = ParseInt(r[0]);
                    row.Cells = new string[] { place > 0 ? place.ToString() : "-", r[1], r[2], r[3], r[4] == "1" ? "removed" : "" };
                    bool scored = ParseInt(r[2]) > 0;   // medals only for players who scored
                    row.Badge = !scored ? Color.Empty : place == 1 ? Theme.Gold : place == 2 ? Theme.Silver : place == 3 ? Theme.Bronze : Color.Empty;
                    items.Add(row);
                }
                lstResults.SetRows(items);
            }
            else if (type == ListType.Admins)
            {
                // id|username|display|active|createdAt
                foreach (string[] r in rows)
                {
                    FancyList.Row row = new FancyList.Row();
                    row.Cells = new string[] { r[0], r[1], r[2], r[3] == "1" ? "Yes" : "No", r[4] };
                    if (r[3] != "1") row.ForeColor = Theme.Muted;
                    items.Add(row);
                }
                lstAdmins.SetRows(items);
            }
            else if (type == ListType.Audit)
            {
                foreach (string[] r in rows)
                {
                    FancyList.Row row = new FancyList.Row();
                    row.Cells = r;
                    if (r[2] == "LOGIN_FAILED" || r[2] == "DENIED") row.ForeColor = Theme.Red;
                    items.Add(row);
                }
                lstAudit.SetRows(items);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ServerConnection c = Session.Connection;
            if (c != null)
            {
                c.MessageReceived -= OnMessage;
                c.ListReceived -= OnList;
                if (c.IsConnected) c.Send(Cmd.Logout);
            }
            base.OnFormClosed(e);
        }
    }
}
