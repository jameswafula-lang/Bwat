/*
 * Bwat Client / StartForm.cs
 *
 * The first screen.
 *   Left:  JOIN A QUIZ - paste the room link (or type the code) and your name/nickname.
 *          Players do not need an account (role: Guest).
 *   Right: ADMIN LOGIN - username + password (role: Admin).
 *
 * A room link looks like  bwat://192.168.1.20:8765/BWAT-4821
 * Clicking such a link opens Bwat with the link already filled in (see Program.cs).
 */
using System;
using System.Drawing;
using System.Windows.Forms;
using Bwat.Common;
using Message = Bwat.Common.Message;  // (WinForms also has a class called Message)

namespace Bwat.Client
{
    public class StartForm : Form
    {
        private InputBox txtLink, txtName, txtServer, txtUser, txtPass;
        private ModernButton btnJoin, btnLogin;
        private Label lblJoinStatus, lblLoginStatus;
        private bool waiting;
        private readonly Timer answerTimer = new Timer();

        public StartForm(string linkFromCommandLine)
        {
            Text = "Bwat - Live classroom quiz   (app version 2.2)";
            ClientSize = new Size(940, 580);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = Theme.F(10f);
            BuildUi();
            // If the server does not answer within 15 seconds, stop waiting and explain why.
            answerTimer.Interval = 15000;
            answerTimer.Tick += delegate
            {
                answerTimer.Stop();
                if (!waiting) return;
                string text = "No answer from the server. The connection was made, but the server did not reply. " +
                              "If you use a tunnel, check that it points to 127.0.0.1:8765 and that BwatServer is running.";
                Fail(btnLogin.Enabled ? lblJoinStatus : lblLoginStatus, text);
                if (Session.Connection != null) Session.Connection.Close();
                Session.Connection = null;
            };
            if (!string.IsNullOrEmpty(linkFromCommandLine)) txtLink.Text = linkFromCommandLine;
            Shown += delegate { if (txtLink.Text.Length > 0) txtName.Box.Focus(); else txtLink.Box.Focus(); };
        }

        private void BuildUi()
        {
            Label title = Theme.MakeLabel("Bwat", 30f, true, Color.White);
            title.Location = new Point(36, 22);
            Controls.Add(title);
            Label sub = Theme.MakeLabel("Live classroom quiz  -  type the answer, be the first to get it right!", 11f, false, Theme.MutedOnDark);
            sub.Location = new Point(40, 78);
            Controls.Add(sub);

            // ---------- join card ----------
            CardPanel join = new CardPanel();
            join.Bounds = new Rectangle(28, 118, 470, 430);
            Controls.Add(join);
            join.Put(Theme.MakeLabel("Join a quiz", 17f, true, Theme.Ink), 26, 22);
            join.Put(Theme.MakeLabel("No account needed - just your name.", 10f, false, Theme.Muted), 28, 58);
            join.Put(Theme.MakeLabel("ROOM LINK OR CODE", 8.5f, true, Theme.Muted), 26, 96);
            txtLink = join.Put(new InputBox(410, 11.5f), 26, 116);
            join.Put(Theme.MakeLabel("YOUR NAME OR NICKNAME", 8.5f, true, Theme.Muted), 26, 176);
            txtName = join.Put(new InputBox(410, 14f), 26, 196);
            txtName.Box.MaxLength = 20;
            btnJoin = join.Put(new ModernButton("Join", Theme.Blue, 410, 50), 26, 266);
            btnJoin.Font = Theme.B(13f);
            btnJoin.Click += delegate { Join(); };
            lblJoinStatus = join.Put(Theme.MakeLabel("", 10f, false, Theme.Red), 28, 328);
            lblJoinStatus.AutoSize = false;
            lblJoinStatus.Size = new Size(410, 96);
            lblJoinStatus.Font = Theme.F(9f);

            // ---------- admin card ----------
            CardPanel admin = new CardPanel();
            admin.Bounds = new Rectangle(516, 118, 396, 430);
            Controls.Add(admin);
            admin.Put(Theme.MakeLabel("Admin login", 17f, true, Theme.Ink), 26, 22);
            admin.Put(Theme.MakeLabel("Create rooms, manage questions, watch games.", 10f, false, Theme.Muted), 28, 58);
            admin.Put(Theme.MakeLabel("SERVER ADDRESS  (IP : port)", 8.5f, true, Theme.Muted), 26, 96);
            txtServer = admin.Put(new InputBox(336, 11f), 26, 116);
            txtServer.Text = Session.LoadServerAddress();
            admin.Put(Theme.MakeLabel("USERNAME", 8.5f, true, Theme.Muted), 26, 170);
            txtUser = admin.Put(new InputBox(336, 11f), 26, 190);
            admin.Put(Theme.MakeLabel("PASSWORD", 8.5f, true, Theme.Muted), 26, 244);
            txtPass = admin.Put(new InputBox(336, 11f), 26, 264);
            txtPass.Password = true;
            btnLogin = admin.Put(new ModernButton("Log in", Theme.Ink, 336, 44), 26, 322);
            btnLogin.Click += delegate { Login(); };
            lblLoginStatus = admin.Put(Theme.MakeLabel("", 10f, false, Theme.Red), 28, 374);
            lblLoginStatus.AutoSize = false;
            lblLoginStatus.Size = new Size(336, 54);
            lblLoginStatus.Font = Theme.F(8.5f);

            // Enter = join, unless the cursor is in the admin card
            AcceptButton = btnJoin;
            txtUser.Box.Enter += delegate { AcceptButton = btnLogin; };
            txtPass.Box.Enter += delegate { AcceptButton = btnLogin; };
            txtServer.Box.Enter += delegate { AcceptButton = btnLogin; };
            txtLink.Box.Enter += delegate { AcceptButton = btnJoin; };
            txtName.Box.Enter += delegate { AcceptButton = btnJoin; };
        }

        // ------------------------------------------------------------------

        private bool Connect(string host, int port, Label status)
        {
            status.ForeColor = Theme.Muted;
            status.Text = port == Protocol.SecurePort
                ? "Connecting to the online server " + host + " ... (this can take up to a minute if it was asleep)"
                : "Connecting to " + host + ":" + port + " ...";
            Refresh();
            string error;
            if (!Session.EnsureConnected(host, port, OnMessage, OnDisconnected, out error))
            {
                status.ForeColor = Theme.Red;
                status.Text = error;
                return false;
            }
            return true;
        }

        private void Join()
        {
            if (waiting) return;
            string host, code; int port;
            if (!Protocol.ParseLink(txtLink.Text, out host, out port, out code))
            {
                Fail(lblJoinStatus, "Paste the room link from your admin (bwat://...) or type the room code.");
                return;
            }
            string name = txtName.Text.Trim();
            if (name.Length < 2) { Fail(lblJoinStatus, "Type your name or nickname (at least 2 letters)."); return; }

            if (host == null)
            {
                // Only a code was typed: use the server address from the admin card.
                if (!Session.ParseAddress(txtServer.Text, out host, out port)) { Fail(lblJoinStatus, "You typed only a code - also fill in the server address on the right."); return; }
                if (host == "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    // 127.0.0.1 means "this computer" - only right if the server runs on this same PC.
                    if (!Connect(host, port, lblJoinStatus))
                    {
                        Fail(lblJoinStatus, "You typed only the room code. Please paste the FULL link your admin sent (it starts with bwat://), " +
                                            "or type the server address in the box on the right.");
                        return;
                    }
                }
            }
            if (!Connect(host, port, lblJoinStatus)) return;
            waiting = true;
            btnJoin.Enabled = false;
            lblJoinStatus.Text = "Joining " + code + " ...";
            Session.Connection.Send(Cmd.Join, code, name);
            answerTimer.Stop();
            answerTimer.Start();
        }

        private void Login()
        {
            if (waiting) return;
            string host; int port;
            if (!Session.ParseAddress(txtServer.Text, out host, out port)) { Fail(lblLoginStatus, "Enter the server address, e.g. 192.168.1.20:8765"); return; }
            if (txtUser.Text.Trim().Length == 0 || txtPass.Text.Length == 0) { Fail(lblLoginStatus, "Enter your username and password."); return; }
            if (!Connect(host, port, lblLoginStatus)) return;
            Session.SaveServerAddress(txtServer.Text.Trim());
            waiting = true;
            btnLogin.Enabled = false;
            lblLoginStatus.Text = "Logging in ...";
            Session.Connection.Send(Cmd.Login, txtUser.Text.Trim(), txtPass.Text);
            answerTimer.Stop();
            answerTimer.Start();
        }

        private void Fail(Label l, string text)
        {
            answerTimer.Stop();
            waiting = false;
            btnJoin.Enabled = true;
            btnLogin.Enabled = true;
            l.ForeColor = Theme.Red;
            l.Text = text;
        }

        private void OnMessage(Message m)
        {
            if (!Visible) return;   // another window is active
            if (m.Command == Cmd.RoomJoined)
            {
                answerTimer.Stop();
                waiting = false;
                btnJoin.Enabled = true;
                lblJoinStatus.Text = "";
                PlayerGameForm game = new PlayerGameForm(m);
                game.FormClosed += delegate { Show(); Activate(); };
                Hide();
                game.Show();
            }
            else if (m.Command == Cmd.LoginOk)
            {
                answerTimer.Stop();
                waiting = false;
                btnLogin.Enabled = true;
                lblLoginStatus.Text = "";
                txtPass.Text = "";
                Session.AdminId = m.GetInt(0, 0);
                Session.AdminUsername = m.Get(1);
                Session.AdminName = m.Get(2);
                AdminForm admin = new AdminForm();
                admin.FormClosed += delegate { Session.ClearAdmin(); Show(); Activate(); };
                Hide();
                admin.Show();
            }
            else if (m.Command == Cmd.Error && m.Get(0) == Cmd.Join) Fail(lblJoinStatus, m.Get(1));
            else if (m.Command == Cmd.Error && m.Get(0) == Cmd.Login) Fail(lblLoginStatus, m.Get(1));
        }

        /// <summary>The connection broke: close every other window and come back here.</summary>
        private void OnDisconnected(string reason)
        {
            waiting = false;
            btnJoin.Enabled = true;
            btnLogin.Enabled = true;
            if (Session.Connection != null)
            {
                Session.Connection.MessageReceived -= OnMessage;
                Session.Connection.Disconnected -= OnDisconnected;
                Session.Connection.Close();
            }
            Session.Connection = null;
            Session.ClearAdmin();
            for (int i = Application.OpenForms.Count - 1; i >= 0; i--)
            {
                Form f = Application.OpenForms[i];
                if (f != this) f.Close();
            }
            Show();
            MessageBox.Show(this, reason + "\n\nCheck that the server is running and try again.", "Connection lost",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (Session.Connection != null) Session.Connection.Close();
            base.OnFormClosed(e);
        }
    }
}
