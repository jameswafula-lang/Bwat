/*
 * Bwat Server / ConnectionHandler.cs
 *
 * The "slave" of the concurrent master/slave server (see Program.cs and the
 * lecture "Multi-Client Servers"). One ConnectionHandler object is created for
 * every client that connects, and its HandleConnection() method runs on its
 * own thread for as long as that client stays connected.
 *
 * ROLES (authorisation)
 *  - Guest (player): no account. May only JOIN a room with a nickname, ANSWER,
 *    LEAVE and PING.
 *  - Admin: must LOGIN with username + password (stored as a salted hash).
 *    Only admins may manage questions, rooms, admins and see the history.
 * Every request is checked here on the server - the client is never trusted.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Bwat.Common;

namespace Bwat.Server
{
    public class ConnectionHandler
    {
        private readonly Socket client;
        private NetworkStream ns;
        private ILineChannel channel;          // plain TCP or WebSocket (see NetChannels.cs)
        private readonly object writeLock = new object();
        private string remote;
        private volatile bool closed;

        private static int noOfCon = 0;
        private static readonly object countLock = new object();

        // Admins logged in right now (one session per admin account).
        private static readonly Dictionary<int, ConnectionHandler> online = new Dictionary<int, ConnectionHandler>();
        private static readonly object onlineLock = new object();

        // Failed admin logins, to slow down password guessing.
        private static readonly Dictionary<string, int> failedLogins = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> lockedUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private const int MaxFailedLogins = 5;
        private const int LockSeconds = 60;

        private static readonly Regex UsernamePattern = new Regex("^[A-Za-z0-9_]{3,20}$");
        private static readonly Regex NicknamePattern = new Regex("^[A-Za-z0-9 _.'-]{2,20}$");

        public UserRecord Admin;              // null unless an admin logged in
        private volatile GameRoom room;       // the room this client is in (or null)
        private string nickname;              // the player's name in that room

        public ConnectionHandler(Socket client)
        {
            this.client = client;
            IPEndPoint ep = client.RemoteEndPoint as IPEndPoint;
            remote = ep != null ? ep.Address + ":" + ep.Port : "unknown";
        }

        /// <summary>For an HTTP-polling session (HttpPolling.cs): the channel already exists, there is no socket.</summary>
        public ConnectionHandler(ILineChannel channel, string remote)
        {
            this.channel = channel;
            this.remote = remote;
        }

        private bool IsAdmin { get { return Admin != null; } }

        private string Who
        {
            get
            {
                if (Admin != null) return "admin " + Admin.Username + "@" + remote;
                if (nickname != null) return nickname + "@" + remote;
                return remote;
            }
        }

        // ==================================================================
        //  Main loop of the slave thread
        // ==================================================================

        public void HandleConnection()
        {
            int count;
            bool announced = false;   // web requests (Render's health check every 5 s, browsers) are not logged

            try
            {
                string first = null;
                if (channel == null)   // a real socket: find out what kind of client it is
                {
                    ns = new NetworkStream(client);
                    NetReader netReader = new NetReader(ns);

                    // WebSocket clients and the Bwat app speak first. An older TCP client may wait for WELCOME.
                    if (client.Poll(1500000, SelectMode.SelectRead))
                    {
                        first = netReader.ReadLine(16000);
                        if (first == null) return;
                    }

                    if (first != null && (first.StartsWith("GET ") || first.StartsWith("HEAD ") || first.StartsWith("POST ")))
                    {
                        string forwardedFor;
                        channel = HttpGate.Accept(first, netReader, ns, out forwardedFor);
                        if (forwardedFor != null) remote = forwardedFor + " (via proxy)";
                        if (channel == null) return;      // an ordinary web request or a /poll/ request was answered
                        first = null;
                    }
                    else channel = new TcpLineChannel(netReader, ns);
                }

                lock (countLock) count = ++noOfCon;
                announced = true;
                ServerLog.Good("New client connected from " + remote + " using " + channel.Kind + "  (clients online: " + count + ")");
                Send(Protocol.Build(Cmd.Welcome, "Welcome to the " + Protocol.AppName + " server " + Protocol.Version));

                string line = first;
                while (!closed && (line != null || (line = channel.ReadLine()) != null))
                {
                    string current = line;
                    line = null;
                    if (current.Trim().Length == 0) continue;
                    // A tunnel with "PROXY protocol" switched on sends one extra header line first - ignore it.
                    if (current.StartsWith("PROXY ")) { ServerLog.Warn("Ignored a PROXY protocol header from the tunnel: " + current); continue; }
                    if (current.Length > 4000) { SendError("?", "Request too long."); continue; }
                    Message msg = Protocol.Parse(current);
                    if (msg.Command != Cmd.Ping) ServerLog.Request(Who, Mask(msg));
                    try
                    {
                        Process(msg);
                    }
                    catch (Exception ex)
                    {
                        // One bad request must never stop the server.
                        ServerLog.Error("Error processing " + msg.Command + " from " + Who + ": " + ex.Message);
                        SendError(msg.Command, "The server could not complete the request: " + ex.Message);
                    }
                }
            }
            catch (IOException) { /* client vanished */ }
            catch (SocketException) { /* client vanished */ }
            catch (ObjectDisposedException) { /* connection already closed */ }
            catch (Exception ex)
            {
                ServerLog.Error("Unexpected error with " + Who + ": " + ex.Message);
            }
            finally
            {
                Cleanup();
                if (announced)
                {
                    lock (countLock) count = --noOfCon;
                    ServerLog.Warn("Client " + Who + " disconnected  (clients online: " + count + ")");
                }
            }
        }

        private void Cleanup()
        {
            GameRoom r = room;
            if (r != null) r.Leave(this);
            room = null;
            if (Admin != null)
            {
                lock (onlineLock)
                {
                    ConnectionHandler current;
                    if (online.TryGetValue(Admin.Id, out current) && current == this) online.Remove(Admin.Id);
                }
            }
            Close();
        }

        private void Close()
        {
            closed = true;
            try { if (ns != null) ns.Close(); } catch { }
            try { if (client != null) client.Close(); } catch { }
            PollChannel poll = channel as PollChannel;
            if (poll != null) poll.Close();
        }

        /// <summary>Sends one line to this client. Thread-safe: room threads also send through here.</summary>
        public void Send(string line)
        {
            if (closed || channel == null) return;
            try
            {
                lock (writeLock)
                {
                    channel.WriteLine(line);
                }
                if (!line.StartsWith("OK|PING")) ServerLog.Response(Who, line);
            }
            catch (Exception)
            {
                // The connection is broken. The read loop will notice and clean up.
                closed = true;
                try { if (client != null) client.Close(); } catch { }
            }
        }

        private void SendOk(string command, string message) { Send(Protocol.Build(Cmd.Ok, command, message)); }
        private void SendError(string command, string message) { Send(Protocol.Build(Cmd.Error, command, message)); }

        private void SendList(string type, List<string[]> rows)
        {
            Send(Protocol.Build(Cmd.ListBegin, type));
            foreach (string[] row in rows)
            {
                object[] fields = new object[row.Length + 1];
                fields[0] = type;
                for (int i = 0; i < row.Length; i++) fields[i + 1] = row[i];
                Send(Protocol.Build(Cmd.ListRow, fields));
            }
            Send(Protocol.Build(Cmd.ListEnd, type));
        }

        /// <summary>Called by a GameRoom when this client has joined it.</summary>
        public void JoinedRoom(GameRoom r, string name)
        {
            room = r;
            nickname = name;
        }

        /// <summary>Called by a GameRoom when the game ends or the player is removed (no lock: the room holds its lock).</summary>
        public void LeftRoom(GameRoom r)
        {
            if (room == r) room = null;
        }

        /// <summary>Hides passwords in the console log.</summary>
        private static string Mask(Message m)
        {
            if (m.Command == Cmd.Login || m.Command == Cmd.AdminCreate)
            {
                string[] f = (string[])m.Fields.Clone();
                int i = m.Command == Cmd.Login ? 1 : 2;
                if (f.Length > i) f[i] = "******";
                return new Message(m.Command, f).ToString();
            }
            return m.ToString();
        }

        // ==================================================================
        //  Request processing
        // ==================================================================

        private void Process(Message m)
        {
            // Requests anybody may send (players are guests)
            switch (m.Command)
            {
                case Cmd.Ping: SendOk(Cmd.Ping, "pong"); return;
                case Cmd.Login: DoLogin(m); return;
                case Cmd.Join: DoJoin(m); return;
                case Cmd.Leave: DoLeave(); return;
                case Cmd.Answer: DoAnswer(m); return;
            }

            // Everything else is for logged-in admins only (AUTHORISATION by role)
            if (!IsAdmin)
            {
                SendError(m.Command, "Permission denied: only a logged-in admin can do this.");
                Database.Audit(Who, "DENIED", m.Command);
                return;
            }

            switch (m.Command)
            {
                case Cmd.Logout: DoLogout(); return;
                case Cmd.QuestionList: DoQuestionList(m); return;
                case Cmd.QuestionSave: DoQuestionSave(m); return;
                case Cmd.QuestionDelete: DoQuestionDelete(m); return;
                case Cmd.RoomCreate: DoRoomCreate(m); return;
                case Cmd.RoomList: DoRoomList(); return;
                case Cmd.RoomStart: DoRoomStart(m); return;
                case Cmd.RoomCancel: DoRoomCancel(m); return;
                case Cmd.RoomSkip: DoRoomControl(m, Cmd.RoomSkip); return;
                case Cmd.RoomEnd: DoRoomControl(m, Cmd.RoomEnd); return;
                case Cmd.RoomKick: DoRoomControl(m, Cmd.RoomKick); return;
                case Cmd.AdminList: SendList(ListType.Admins, Database.ListAdmins()); return;
                case Cmd.AdminCreate: DoAdminCreate(m); return;
                case Cmd.AdminSetActive: DoAdminSetActive(m); return;
                case Cmd.GamesList: SendList(ListType.Games, Database.ListGames(100)); return;
                case Cmd.GameResults: SendList(ListType.GameResults, Database.GameResults(m.Get(0).Trim().ToUpper())); return;
                case Cmd.AuditList: SendList(ListType.Audit, Database.ListAudit(200)); return;
            }

            SendError(m.Command, "Unknown request '" + m.Command + "'.");
        }

        // ------------------------------------------------------------------
        //  Admin login
        // ------------------------------------------------------------------

        private void DoLogin(Message m)
        {
            if (IsAdmin) { SendError(Cmd.Login, "You are already logged in."); return; }
            if (room != null) { SendError(Cmd.Login, "Leave the quiz first."); return; }
            string username = m.Get(0).Trim();
            string password = m.Get(1);
            if (username.Length == 0 || password.Length == 0) { SendError(Cmd.Login, "Enter your username and password."); return; }

            lock (onlineLock)
            {
                DateTime until;
                if (lockedUntil.TryGetValue(username, out until) && until > DateTime.Now)
                {
                    int wait = (int)Math.Ceiling((until - DateTime.Now).TotalSeconds);
                    SendError(Cmd.Login, "Too many wrong passwords. Try again in " + wait + " seconds.");
                    return;
                }
            }

            UserRecord u = Database.GetAdmin(username);
            if (u == null || !PasswordHasher.Verify(password, u.Salt, u.PasswordHash))
            {
                RegisterFailedLogin(username);
                Database.Audit(username + "@" + remote, "LOGIN_FAILED", "");
                SendError(Cmd.Login, "Wrong username or password.");   // same message for both: do not reveal which usernames exist
                return;
            }
            if (!u.IsActive) { SendError(Cmd.Login, "This admin account has been deactivated."); return; }

            // One session per account: a new login takes over and the old connection is closed.
            ConnectionHandler old;
            lock (onlineLock)
            {
                failedLogins.Remove(username);
                lockedUntil.Remove(username);
                online.TryGetValue(u.Id, out old);
                online[u.Id] = this;
            }
            if (old != null && old != this)
            {
                old.Send(Protocol.Build(Cmd.Info, "You logged in on another computer. This session was closed."));
                GameRoom oldRoom = old.room;
                if (oldRoom != null) oldRoom.Leave(old);
                old.room = null;
                old.Close();
            }

            Admin = u;
            Database.Audit(Who, "LOGIN", "");
            Send(Protocol.Build(Cmd.LoginOk, u.Id, u.Username, u.DisplayName));
        }

        private void RegisterFailedLogin(string username)
        {
            lock (onlineLock)
            {
                int n;
                failedLogins.TryGetValue(username, out n);
                n++;
                failedLogins[username] = n;
                if (n >= MaxFailedLogins)
                {
                    lockedUntil[username] = DateTime.Now.AddSeconds(LockSeconds);
                    failedLogins[username] = 0;
                    ServerLog.Warn("Login for '" + username + "' locked for " + LockSeconds + "s after " + MaxFailedLogins + " wrong passwords.");
                }
            }
        }

        private void DoLogout()
        {
            Database.Audit(Who, "LOGOUT", "");
            GameRoom r = room;
            if (r != null) r.Leave(this);
            room = null;
            lock (onlineLock) online.Remove(Admin.Id);
            SendOk(Cmd.Logout, "Goodbye!");
            Admin = null;
        }

        // ------------------------------------------------------------------
        //  Joining and playing (guests)
        // ------------------------------------------------------------------

        private void DoJoin(Message m)
        {
            string code = m.Get(0).Trim().ToUpper();
            if (code.Length == 0) { SendError(Cmd.Join, "Enter the room link or code."); return; }
            if (room != null) { SendError(Cmd.Join, "You are already in room " + room.Code + "."); return; }

            GameRoom r = RoomManager.Find(code);
            if (r == null) { SendError(Cmd.Join, "No open quiz with the code '" + code + "'. Check the link with your admin."); return; }

            string error;
            if (IsAdmin)
            {
                error = r.JoinAdmin(this);   // admins watch and control the room
            }
            else
            {
                string name = Regex.Replace(m.Get(1).Trim(), "\\s+", " ");
                if (!NicknamePattern.IsMatch(name)) { SendError(Cmd.Join, "Your name must be 2-20 letters or digits (spaces, _ . ' - allowed)."); return; }
                error = r.JoinPlayer(this, name);
                if (error == null) Database.Audit(name + "@" + remote, "JOIN", code);
            }
            if (error != null) SendError(Cmd.Join, error);
        }

        private void DoLeave()
        {
            GameRoom r = room;
            if (r == null) { SendError(Cmd.Leave, "You are not in a quiz."); return; }
            r.Leave(this);
            room = null;
            SendOk(Cmd.Leave, "You left the quiz.");
        }

        private void DoAnswer(Message m)
        {
            GameRoom r = room;
            if (r == null) { SendError(Cmd.Answer, "You are not in a quiz."); return; }
            string answer = m.Get(1).Trim();
            if (answer.Length == 0) { SendError(Cmd.Answer, "Type your answer first."); return; }
            if (answer.Length > 30) { SendError(Cmd.Answer, "That answer is too long."); return; }
            string error = r.SubmitAnswer(this, m.GetInt(0, -1), answer);
            if (error != null) SendError(Cmd.Answer, error);
        }

        // ------------------------------------------------------------------
        //  Admin: questions
        // ------------------------------------------------------------------

        private void DoQuestionList(Message m)
        {
            List<string[]> rows = new List<string[]>();
            foreach (QuestionRecord q in Database.ListQuestions(m.GetInt(0, 0)))
                rows.Add(new string[] { q.Id.ToString(), q.LevelId.ToString(), q.Text, q.Answer, q.Difficulty.ToString(),
                                        q.DifficultyManual ? "1" : "0", q.TimesAsked.ToString(), q.TimesCorrect.ToString() });
            SendList(ListType.Questions, rows);
        }

        private void DoQuestionSave(Message m)
        {
            int id = m.GetInt(0, -1);
            int level = m.GetInt(1, 0);
            string text = m.Get(2).Trim();
            string answer = m.Get(3).Trim();
            int difficulty = m.GetInt(4, 0);

            if (id < 0) { SendError(Cmd.QuestionSave, "Invalid question id."); return; }
            if (!Levels.IsValid(level)) { SendError(Cmd.QuestionSave, "Choose a valid level."); return; }
            if (text.Length < 5 || text.Length > 300) { SendError(Cmd.QuestionSave, "The question must be 5 to 300 characters."); return; }
            if (answer.Length == 0 || answer.Length > 50) { SendError(Cmd.QuestionSave, "The answer must be 1 to 50 characters."); return; }
            if (difficulty < 0 || difficulty > 3) { SendError(Cmd.QuestionSave, "Difficulty must be Auto, Easy, Medium or Hard."); return; }

            if (id == 0)
            {
                int newId = Database.AddQuestion(level, text, answer, difficulty, Admin.Id);
                Database.Audit(Who, "QUESTION_ADD", "#" + newId + ": " + text);
                SendOk(Cmd.QuestionSave, "Question #" + newId + " added.");
            }
            else
            {
                if (!Database.UpdateQuestion(id, level, text, answer, difficulty)) { SendError(Cmd.QuestionSave, "Question #" + id + " was not found."); return; }
                Database.Audit(Who, "QUESTION_EDIT", "#" + id + ": " + text);
                SendOk(Cmd.QuestionSave, "Question #" + id + " updated.");
            }
        }

        private void DoQuestionDelete(Message m)
        {
            int id = m.GetInt(0, 0);
            if (!Database.DeleteQuestion(id)) { SendError(Cmd.QuestionDelete, "Question #" + id + " was not found."); return; }
            Database.Audit(Who, "QUESTION_DELETE", "#" + id);
            SendOk(Cmd.QuestionDelete, "Question #" + id + " deleted.");
        }

        // ------------------------------------------------------------------
        //  Admin: rooms
        // ------------------------------------------------------------------

        private void DoRoomCreate(Message m)
        {
            string error;
            string code = RoomManager.CreateRoom(Admin, m.Get(0), m.GetInt(1, 0), m.GetInt(2, 0), m.GetInt(3, 0), m.GetInt(4, -1), out error);
            if (code == null) { SendError(Cmd.RoomCreate, error); return; }
            Database.Audit(Who, "ROOM_CREATE", code + " " + m.Get(0));
            Send(Protocol.Build(Cmd.Ok, Cmd.RoomCreate, code, RoomManager.LinkFor(code)));
        }

        private void DoRoomList()
        {
            List<string[]> rows = Database.ListRooms(100);
            List<string[]> withLinks = new List<string[]>();
            foreach (string[] r in rows)
            {
                string[] row = new string[r.Length + 1];
                Array.Copy(r, row, r.Length);
                row[r.Length] = RoomManager.LinkFor(r[0]);
                withLinks.Add(row);
            }
            SendList(ListType.Rooms, withLinks);
        }

        private void DoRoomStart(Message m)
        {
            GameRoom r = RoomManager.Find(m.Get(0));
            if (r == null || r.Status != RoomStatus.Waiting) { SendError(Cmd.RoomStart, "That room is not waiting to start."); return; }
            if (r.PlayerCount < 2) { SendError(Cmd.RoomStart, "At least 2 players must join before the quiz can start."); return; }
            r.StartNow();
            Database.Audit(Who, "ROOM_START", r.Code);
            SendOk(Cmd.RoomStart, "Room " + r.Code + " is starting now.");
        }

        private void DoRoomCancel(Message m)
        {
            GameRoom r = RoomManager.Find(m.Get(0));
            if (r == null) { SendError(Cmd.RoomCancel, "That room is not open."); return; }
            r.Cancel();
            Database.Audit(Who, "ROOM_CANCEL", r.Code);
            SendOk(Cmd.RoomCancel, "Room " + r.Code + " cancelled.");
        }

        /// <summary>Skip a question, remove a player or end the game.</summary>
        private void DoRoomControl(Message m, string command)
        {
            GameRoom r = RoomManager.Find(m.Get(0));
            if (r == null) { SendError(command, "That room is not open."); return; }
            string error;
            string done;
            if (command == Cmd.RoomSkip) { error = r.Skip(); done = "Question skipped."; }
            else if (command == Cmd.RoomEnd) { error = r.EndGame(); done = "The quiz will end after this question."; }
            else { error = r.Kick(m.Get(1).Trim()); done = m.Get(1).Trim() + " was removed."; }
            if (error != null) { SendError(command, error); return; }
            Database.Audit(Who, command, r.Code + " " + m.Get(1));
            SendOk(command, done);
        }

        // ------------------------------------------------------------------
        //  Admin: admin accounts
        // ------------------------------------------------------------------

        private void DoAdminCreate(Message m)
        {
            string username = m.Get(0).Trim();
            string display = m.Get(1).Trim();
            string password = m.Get(2);
            if (!UsernamePattern.IsMatch(username)) { SendError(Cmd.AdminCreate, "Username must be 3-20 letters, digits or _."); return; }
            if (display.Length < 2 || display.Length > 30) { SendError(Cmd.AdminCreate, "Display name must be 2 to 30 characters."); return; }
            if (password.Length < 6 || password.Length > 50) { SendError(Cmd.AdminCreate, "Password must be 6 to 50 characters."); return; }
            int id = Database.CreateAdmin(username, display, password);
            if (id < 0) { SendError(Cmd.AdminCreate, "The username '" + username + "' is already taken."); return; }
            Database.Audit(Who, "ADMIN_CREATE", username);
            SendOk(Cmd.AdminCreate, "Admin '" + username + "' created.");
        }

        private void DoAdminSetActive(Message m)
        {
            int id = m.GetInt(0, 0);
            bool active = m.Get(1) == "1";
            if (id == Admin.Id) { SendError(Cmd.AdminSetActive, "You cannot deactivate yourself."); return; }
            if (!Database.SetAdminActive(id, active)) { SendError(Cmd.AdminSetActive, "Admin not found."); return; }
            Database.Audit(Who, active ? "ADMIN_ACTIVATE" : "ADMIN_DEACTIVATE", "#" + id);
            if (!active)
            {
                ConnectionHandler h;
                lock (onlineLock) online.TryGetValue(id, out h);
                if (h != null) { h.Send(Protocol.Build(Cmd.Info, "Your admin account was deactivated.")); h.Close(); }
            }
            SendOk(Cmd.AdminSetActive, active ? "Admin activated." : "Admin deactivated.");
        }
    }
}
