/*
 * Bwat Server / GameRoom.cs
 *
 * One GameRoom = one live quiz. Each room runs on its OWN thread (Run), while
 * the clients' ConnectionHandler threads call Join / Leave / SubmitAnswer and
 * the admin controls (Skip, Kick, End) at any moment.
 *
 * SYNCHRONISATION
 * All shared game state is protected by one lock object (sync).
 *  - Handler threads change state inside lock(sync) and call Monitor.PulseAll(sync)
 *    to wake the room thread (e.g. "somebody solved it").
 *  - The room thread waits with Monitor.Wait(sync, timeout), which releases the
 *    lock while waiting, so players can still answer.
 * Answers are checked one at a time inside the lock, so if two players send the
 * right answer at almost the same moment, the one that REACHED THE SERVER first
 * wins - never both.
 *
 * GAME RULES
 *  - Players join with a nickname (up to 40 players per room).
 *  - Everybody gets the same question and types the answer on the calculator.
 *  - The time depends on the difficulty: easy 30 s, medium 45 s, hard 60 s.
 *  - Each player has a balloon and 3 tries per question:
 *        wrong answer -> the balloon shrinks; the 3rd wrong answer pops it.
 *  - The FIRST correct answer gets 5 marks; that balloon floats away and the
 *    question ends. Everybody else's balloon pops (0 marks).
 *  - If nobody has got it right when the time runs out, the timer restarts
 *    (at most 2 times). After the 3rd time-out every balloon pops: "BONGO!".
 *  - After every question: the standings of ALL players.
 *  - At the end: the top three - or "BONGO!" if everybody has 0 or there is a
 *    draw for first place.
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Bwat.Common;

namespace Bwat.Server
{
    public class PlayerState
    {
        public int RoomPlayerId;               // row in the RoomPlayers table
        public string Nickname;
        public ConnectionHandler Connection;   // null while the player is disconnected
        public int JoinOrder;
        public int Marks;
        public int Solved;
        public long TotalSolveMs;              // used only for sorting players with equal marks
        public int LastGained;
        public int Rank;
        public int PreviousRank;
        public int TriesUsed;                  // this question (max 3)
        public string Balloon = BalloonState.Full;

        public bool Connected { get { return Connection != null; } }
    }

    public class GameRoom
    {
        public const int MarksPerQuestion = 5;
        public const int TriesPerQuestion = 3;
        public const int TimerRounds = 3;            // first timer + 2 restarts
        public const int ResultSeconds = 5;
        public const int StandingsSeconds = 7;
        public const int GetReadySeconds = 4;
        public const int MaxExtraWaitMinutes = 10;

        private readonly object sync = new object();
        private readonly RoomRecord record;
        private readonly string link;
        private readonly List<PlayerState> players = new List<PlayerState>();
        private readonly List<ConnectionHandler> admins = new List<ConnectionHandler>();
        private readonly List<string> removedNames = new List<string>();   // players the admin removed
        private Thread thread;
        private int joinCounter;

        private string status;
        private bool startNow, cancelled, endNow, skipNow;

        // state of the current question
        private int round;
        private readonly int totalQuestions;
        private QuestionRecord currentQuestion;
        private int seconds, timerRound;
        private bool roundOpen;
        private DateTime roundStart, roundDeadline;
        private PlayerState roundWinner;
        private int winnerMs;
        private readonly List<object[]> attempts = new List<object[]>();   // saved to the database after the question

        public GameRoom(RoomRecord record, string link)
        {
            this.record = record;
            this.link = link;
            this.status = record.Status;
            this.totalQuestions = record.Questions.Count;
        }

        public string Code { get { return record.Code; } }
        public string Name { get { return record.Name; } }
        public string Link { get { return link; } }
        public string Status { get { lock (sync) return status; } }
        public int PlayerCount { get { lock (sync) return players.Count; } }

        public void Start()
        {
            thread = new Thread(new ThreadStart(Run));
            thread.IsBackground = true;
            thread.Name = "Room " + record.Code;
            thread.Start();
        }

        // ==================================================================
        //  Called by ConnectionHandler threads
        // ==================================================================

        /// <summary>A player joins with a nickname. Returns an error message, or null on success.</summary>
        public string JoinPlayer(ConnectionHandler handler, string nickname)
        {
            lock (sync)
            {
                bool open = status == RoomStatus.Waiting || status == RoomStatus.Playing;
                if (!open) return "This quiz is already " + status.ToLower() + ".";
                foreach (string r in removedNames)
                    if (string.Equals(r, nickname, StringComparison.OrdinalIgnoreCase)) return "The admin removed this player from the quiz.";

                PlayerState existing = FindPlayer(nickname);
                if (existing != null)
                {
                    if (existing.Connected) return "The name '" + nickname + "' is already taken in this room. Choose another name.";
                    // RECONNECT: same nickname, old connection gone -> continue with the same marks.
                    existing.Connection = handler;
                    handler.JoinedRoom(this, existing.Nickname);
                    SendJoined(handler, false, existing.Nickname);
                    BroadcastPlayers();
                    Broadcast(Protocol.Build(Cmd.Info, existing.Nickname + " is back"));
                    if (roundOpen) SendCurrentQuestion(handler, existing);
                    ServerLog.Game("[" + Code + "] " + existing.Nickname + " reconnected.");
                    return null;
                }

                if (status == RoomStatus.Playing) return "This quiz has already started. You can only join before the start time.";
                if (players.Count >= record.MaxPlayers) return "This room is full (" + record.MaxPlayers + " players).";

                PlayerState p = new PlayerState();
                p.Nickname = nickname;
                p.Connection = handler;
                p.JoinOrder = ++joinCounter;
                p.RoomPlayerId = Database.AddRoomPlayer(record.Id, nickname);
                players.Add(p);

                handler.JoinedRoom(this, nickname);
                SendJoined(handler, false, nickname);
                BroadcastPlayers();
                if (DateTime.Now >= record.StartTime && players.Count < 2)
                    handler.Send(Protocol.Build(Cmd.Info, "Waiting for at least 2 players - the quiz starts as soon as another player joins."));
                ServerLog.Game("[" + Code + "] " + nickname + " joined (" + players.Count + "/" + record.MaxPlayers + ").");
                Monitor.PulseAll(sync);
                return null;
            }
        }

        /// <summary>A logged-in admin watches the room (sees the answer and every balloon).</summary>
        public string JoinAdmin(ConnectionHandler handler)
        {
            lock (sync)
            {
                if (status != RoomStatus.Waiting && status != RoomStatus.Playing) return "This room is " + status.ToLower() + ".";
                if (!admins.Contains(handler)) admins.Add(handler);
                handler.JoinedRoom(this, null);
                SendJoined(handler, true, "");
                handler.Send(PlayersLine());
                if (roundOpen)
                {
                    SendCurrentQuestion(handler, null);
                    foreach (PlayerState p in players) handler.Send(BalloonLine(p));
                }
                return null;
            }
        }

        /// <summary>Called when a client leaves the room or its connection drops.</summary>
        public void Leave(ConnectionHandler handler)
        {
            int deleteId = 0;
            lock (sync)
            {
                if (admins.Remove(handler)) return;
                PlayerState p = FindPlayer(handler);
                if (p == null) return;

                if (status == RoomStatus.Waiting)
                {
                    players.Remove(p);
                    deleteId = p.RoomPlayerId;
                    BroadcastPlayers();
                }
                else if (status == RoomStatus.Playing)
                {
                    p.Connection = null;   // keep the marks - the player may come back with the same name
                    Broadcast(Protocol.Build(Cmd.Info, p.Nickname + " disconnected"));
                    BroadcastPlayers();
                    CheckEveryoneOut();
                }
                ServerLog.Game("[" + Code + "] " + p.Nickname + " left / disconnected.");
            }
            if (deleteId > 0) Database.DeleteRoomPlayer(deleteId);
        }

        /// <summary>Checks a typed answer. Returns an error message, or null if the answer was checked.</summary>
        public string SubmitAnswer(ConnectionHandler handler, int answerRound, string answer)
        {
            lock (sync)
            {
                PlayerState p = FindPlayer(handler);
                if (p == null) return "Only players can answer.";
                if (status != RoomStatus.Playing) return "The quiz is not running.";
                if (!roundOpen || answerRound != round) return "Too late - this question is closed.";
                if (p.TriesUsed >= TriesPerQuestion) return "Your balloon has popped - wait for the next question.";

                int ms = (int)(DateTime.Now - roundStart).TotalMilliseconds;
                bool correct = AnswerChecker.IsCorrect(answer, currentQuestion.Answer);
                attempts.Add(new object[] { p.RoomPlayerId, answer, correct, ms });
                p.TriesUsed++;

                if (correct)
                {
                    // FIRST correct answer: 5 marks, the balloon floats away and the question closes.
                    roundWinner = p;
                    winnerMs = ms;
                    p.Balloon = BalloonState.Floated;
                    roundOpen = false;
                    ServerLog.Game("[" + Code + "] Q" + round + ": " + p.Nickname + " solved it in " + (ms / 1000.0).ToString("0.0") + " s (" + answer + ").");
                    Monitor.PulseAll(sync);
                }
                else
                {
                    int left = TriesPerQuestion - p.TriesUsed;
                    p.Balloon = left > 0 ? BalloonState.Shrunk : BalloonState.Popped;
                    p.Connection.Send(Protocol.Build(Cmd.AnswerWrong, round, left));
                    CheckEveryoneOut();
                }
                SendToAdmins(BalloonLine(p));
                return null;
            }
        }

        public void StartNow() { lock (sync) { startNow = true; Monitor.PulseAll(sync); } }

        public void Cancel() { lock (sync) { cancelled = true; roundOpen = false; Monitor.PulseAll(sync); } }

        /// <summary>Admin: end the current question with no winner.</summary>
        public string Skip()
        {
            lock (sync)
            {
                if (!roundOpen) return "There is no question running right now.";
                skipNow = true;
                roundOpen = false;
                Monitor.PulseAll(sync);
                return null;
            }
        }

        /// <summary>Admin: stop after the current question and show the final results.</summary>
        public string EndGame()
        {
            lock (sync)
            {
                if (status != RoomStatus.Playing) return "The quiz is not running.";
                endNow = true;
                roundOpen = false;
                Monitor.PulseAll(sync);
                return null;
            }
        }

        /// <summary>Admin: remove a player from the room.</summary>
        public string Kick(string nickname)
        {
            int deleteId = 0, saveId = 0;
            lock (sync)
            {
                PlayerState p = FindPlayer(nickname);
                if (p == null) return "There is no player called '" + nickname + "' in this room.";
                players.Remove(p);
                removedNames.Add(p.Nickname);
                if (p.Connected)
                {
                    p.Connection.Send(Protocol.Build(Cmd.Kicked, "The admin removed you from the quiz."));
                    p.Connection.LeftRoom(this);
                }
                if (status == RoomStatus.Waiting) deleteId = p.RoomPlayerId; else saveId = p.RoomPlayerId;
                Broadcast(Protocol.Build(Cmd.Info, p.Nickname + " was removed by the admin"));
                BroadcastPlayers();
                CheckEveryoneOut();
                ServerLog.Warn("[" + Code + "] " + nickname + " removed by the admin.");
                if (saveId > 0) Database.SaveRoomPlayer(saveId, p.Marks, p.Solved, 0, true);
            }
            if (deleteId > 0) Database.DeleteRoomPlayer(deleteId);
            return null;
        }

        // ==================================================================
        //  The room thread
        // ==================================================================

        private void Run()
        {
            try
            {
                if (!WaitForStart()) return;
                for (int i = 0; i < record.Questions.Count; i++)
                {
                    lock (sync)
                    {
                        if (cancelled || endNow) break;
                        if (ConnectedCount() == 0) { ServerLog.Warn("[" + Code + "] Everybody disconnected."); break; }
                    }
                    bool last = i == record.Questions.Count - 1;
                    PlayOneQuestion(record.Questions[i]);
                    if (!last && !IsStopping()) ShowStandings();
                }
                FinishGame();
            }
            catch (Exception ex)
            {
                ServerLog.Error("[" + Code + "] Game error: " + ex.Message);
                EndRoom(RoomStatus.Interrupted, "The quiz stopped because of a server error.");
            }
            finally
            {
                RoomManager.Remove(this);
            }
        }

        /// <summary>Waits for the start time (or Start Now). Returns false if the quiz will not be played.</summary>
        private bool WaitForStart()
        {
            lock (sync)
            {
                while (!startNow && !cancelled && DateTime.Now < record.StartTime)
                {
                    int ms = (int)Math.Min(1000, (record.StartTime - DateTime.Now).TotalMilliseconds);
                    if (ms > 0) Monitor.Wait(sync, ms);
                }
                // Start time reached but fewer than 2 players: wait a little longer.
                DateTime giveUp = DateTime.Now.AddMinutes(MaxExtraWaitMinutes);
                bool told = false;
                while (!cancelled && ConnectedCount() < 2 && DateTime.Now < giveUp)
                {
                    if (!told)
                    {
                        Broadcast(Protocol.Build(Cmd.Info, "Waiting for at least 2 players - the quiz starts as soon as another player joins."));
                        told = true;
                    }
                    Monitor.Wait(sync, 1000);
                }
            }

            if (IsCancelled())
            {
                EndRoom(RoomStatus.Cancelled, "The admin cancelled this quiz.");
                return false;
            }

            bool enough;
            List<int> gone = new List<int>();
            lock (sync)
            {
                foreach (PlayerState p in players) if (!p.Connected) gone.Add(p.RoomPlayerId);
                players.RemoveAll(delegate(PlayerState p) { return !p.Connected; });
                enough = players.Count >= 2;
                if (enough)
                {
                    status = RoomStatus.Playing;
                    Rerank();
                    foreach (PlayerState p in players) p.PreviousRank = p.Rank;
                }
            }
            foreach (int id in gone) Database.DeleteRoomPlayer(id);
            if (!enough)
            {
                EndRoom(RoomStatus.Cancelled, "Not enough players joined (at least 2 are needed).");
                return false;
            }

            Database.SetRoomStatus(record.Id, RoomStatus.Playing, null);
            Database.AddGameEvent(record.Id, "GAME_START", PlayerCount + " players");
            ServerLog.Game("[" + Code + "] Quiz started with " + PlayerCount + " players.");
            lock (sync)
            {
                Broadcast(Protocol.Build(Cmd.GameStart, totalQuestions, MarksPerQuestion));
                BroadcastPlayers();
            }
            Thread.Sleep(GetReadySeconds * 1000);
            return true;
        }

        private void PlayOneQuestion(QuestionRecord q)
        {
            int roundNumber;
            PlayerState winner;
            bool skipped;
            List<object[]> toSave;
            string answer = q.Answer.Split(';')[0].Trim();

            lock (sync)
            {
                // ---- 1. ask (new balloon for everybody) ----
                round++;
                roundNumber = round;
                currentQuestion = q;
                seconds = Timing.SecondsFor(q.Difficulty);
                roundWinner = null;
                skipNow = false;
                attempts.Clear();
                foreach (PlayerState p in players) { p.TriesUsed = 0; p.Balloon = BalloonState.Full; }
                roundStart = DateTime.Now;
                roundOpen = true;

                // ---- 2. up to 3 timers: restart while nobody has got it right ----
                for (timerRound = 1; timerRound <= TimerRounds; timerRound++)
                {
                    roundDeadline = DateTime.Now.AddSeconds(seconds);
                    if (timerRound == 1)
                    {
                        foreach (PlayerState p in players) if (p.Connected) SendCurrentQuestion(p.Connection, p);
                        foreach (ConnectionHandler a in admins) SendCurrentQuestion(a, null);
                    }
                    else
                    {
                        Broadcast(Protocol.Build(Cmd.TimerRestart, roundNumber, seconds, timerRound));
                        ServerLog.Game("[" + Code + "] Q" + roundNumber + ": nobody got it yet - timer " + timerRound + " of " + TimerRounds + ".");
                    }

                    while (roundOpen && !cancelled)
                    {
                        int ms = (int)(roundDeadline - DateTime.Now).TotalMilliseconds;
                        if (ms <= 0) break;
                        Monitor.Wait(sync, ms);
                    }
                    if (!roundOpen || cancelled) break;   // solved, everybody popped, skipped or ended
                }
                roundOpen = false;
                if (cancelled) return;

                // ---- 3. marks: only the first correct answer ----
                winner = roundWinner;
                skipped = skipNow && winner == null;
                foreach (PlayerState p in players)
                {
                    p.PreviousRank = p.Rank;
                    p.LastGained = 0;
                    if (p != winner) p.Balloon = BalloonState.Popped;
                }
                if (winner != null)
                {
                    winner.Marks += MarksPerQuestion;
                    winner.Solved++;
                    winner.TotalSolveMs += winnerMs;
                    winner.LastGained = MarksPerQuestion;
                }
                Rerank();

                Broadcast(Protocol.Build(Cmd.RoundResult, roundNumber, winner != null ? winner.Nickname : "", answer,
                                         winner != null ? MarksPerQuestion : 0,
                                         winner != null ? (winnerMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) : ""));
                if (skipped) Broadcast(Protocol.Build(Cmd.Info, "The admin skipped this question."));
                BroadcastPlayers();
                toSave = new List<object[]>(attempts);
            }

            // ---- 4. save the question in the database ----
            foreach (object[] a in toSave)
            {
                bool correct = (bool)a[2];
                Database.SavePlayerAnswer(record.Id, (int)a[0], roundNumber, q.Id, (string)a[1], correct, (int)a[3], correct ? MarksPerQuestion : 0);
            }
            Database.RecordQuestionResult(q.Id, winner != null);
            Database.AddGameEvent(record.Id, winner != null ? "SOLVED" : (skipped ? "SKIPPED" : "BONGO"),
                                  "Question " + roundNumber + " (#" + q.Id + ")" + (winner != null ? " solved by " + winner.Nickname : "") + ", " + toSave.Count + " answers");
            if (winner == null) ServerLog.Game("[" + Code + "] Q" + roundNumber + ": BONGO - nobody solved it.");

            Thread.Sleep(ResultSeconds * 1000);
        }

        /// <summary>Between questions: the position of EVERY player.</summary>
        private void ShowStandings()
        {
            lock (sync) Broadcast(StandingsLine());
            Thread.Sleep(StandingsSeconds * 1000);
        }

        private void FinishGame()
        {
            if (IsCancelled())
            {
                EndRoom(RoomStatus.Cancelled, "The admin cancelled this quiz.");
                return;
            }

            List<PlayerState> ranking;
            bool bongo;
            lock (sync)
            {
                Rerank();
                ranking = new List<PlayerState>(players);
                ranking.Sort(ComparePlayers);
                int top = ranking.Count > 0 ? ranking[0].Marks : 0;
                int atTop = 0;
                foreach (PlayerState p in ranking) if (p.Marks == top) atTop++;
                bongo = top == 0 || atTop > 1;      // everybody lost, or a draw for first place

                List<object> fields = new List<object>();
                fields.Add(bongo ? 1 : 0);
                fields.Add(ranking.Count);
                foreach (PlayerState p in ranking)
                {
                    fields.Add(p.Nickname);
                    fields.Add(p.Marks);
                    fields.Add(p.Solved);
                    fields.Add(p.Rank);
                }
                status = RoomStatus.Finished;
                Broadcast(Protocol.Build(Cmd.FinalResults, fields.ToArray()));
            }

            foreach (PlayerState p in ranking)
                Database.SaveRoomPlayer(p.RoomPlayerId, p.Marks, p.Solved, p.Rank, false);
            string winnerName = bongo ? "BONGO" : ranking[0].Nickname;
            Database.SetRoomStatus(record.Id, RoomStatus.Finished, winnerName);
            Database.AddGameEvent(record.Id, "GAME_OVER", bongo ? "BONGO (no winner)" : winnerName + " won with " + ranking[0].Marks + " marks");
            ServerLog.Good("[" + Code + "] FINISHED - " + (bongo ? "BONGO!" : "winner: " + winnerName));

            lock (sync) DetachEveryoneLocked();
        }

        private void EndRoom(string newStatus, string reason)
        {
            lock (sync)
            {
                status = newStatus;
                Broadcast(Protocol.Build(Cmd.RoomCancelled, reason));
                ServerLog.Warn("[" + Code + "] Room " + newStatus.ToLower() + ": " + reason);
                DetachEveryoneLocked();
            }
            try
            {
                Database.SetRoomStatus(record.Id, newStatus, null);
                Database.AddGameEvent(record.Id, newStatus.ToUpper(), reason);
            }
            catch (Exception ex) { ServerLog.Error("[" + Code + "] Could not save room status: " + ex.Message); }
        }

        private void DetachEveryoneLocked()
        {
            foreach (PlayerState p in players) if (p.Connected) p.Connection.LeftRoom(this);
            foreach (ConnectionHandler a in admins) a.LeftRoom(this);
            admins.Clear();
        }

        // ==================================================================
        //  Helpers (call only while holding the lock)
        // ==================================================================

        private bool IsCancelled() { lock (sync) return cancelled; }
        private bool IsStopping() { lock (sync) return cancelled || endNow; }

        private PlayerState FindPlayer(string nickname)
        {
            foreach (PlayerState p in players)
                if (string.Equals(p.Nickname, nickname, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        private PlayerState FindPlayer(ConnectionHandler handler)
        {
            foreach (PlayerState p in players) if (p.Connection == handler) return p;
            return null;
        }

        private int ConnectedCount()
        {
            int n = 0;
            foreach (PlayerState p in players) if (p.Connected) n++;
            return n;
        }

        /// <summary>Ends the question early when every connected player has used all 3 tries.</summary>
        private void CheckEveryoneOut()
        {
            if (!roundOpen) return;
            foreach (PlayerState p in players)
                if (p.Connected && p.TriesUsed < TriesPerQuestion) return;
            roundOpen = false;
            Monitor.PulseAll(sync);
        }

        private static int ComparePlayers(PlayerState a, PlayerState b)
        {
            if (a.Marks != b.Marks) return b.Marks.CompareTo(a.Marks);
            if (a.TotalSolveMs != b.TotalSolveMs) return a.TotalSolveMs.CompareTo(b.TotalSolveMs);
            return a.JoinOrder.CompareTo(b.JoinOrder);
        }

        /// <summary>Players with the same marks share the same position (1, 1, 3 ...).</summary>
        private void Rerank()
        {
            List<PlayerState> sorted = new List<PlayerState>(players);
            sorted.Sort(ComparePlayers);
            for (int i = 0; i < sorted.Count; i++)
                sorted[i].Rank = (i > 0 && sorted[i].Marks == sorted[i - 1].Marks) ? sorted[i - 1].Rank : i + 1;
        }

        private void SendJoined(ConnectionHandler h, bool admin, string nickname)
        {
            int secondsToStart = (int)Math.Ceiling((record.StartTime - DateTime.Now).TotalSeconds);
            if (secondsToStart < 0) secondsToStart = 0;
            h.Send(Protocol.Build(Cmd.RoomJoined, record.Code, record.Name, record.LevelId, totalQuestions,
                                  secondsToStart, admin ? 1 : 0, status, nickname, link));
        }

        /// <summary>Sends the question WITHOUT the answer. Admins also get the answer (ANSWER_KEY).</summary>
        private void SendCurrentQuestion(ConnectionHandler h, PlayerState p)
        {
            int secondsLeft = (int)Math.Ceiling((roundDeadline - DateTime.Now).TotalSeconds);
            if (secondsLeft < 1) secondsLeft = 1;
            h.Send(Protocol.Build(Cmd.Question, round, totalQuestions, currentQuestion.Text, secondsLeft,
                                  MarksPerQuestion, currentQuestion.Difficulty, timerRound));
            if (p == null) h.Send(Protocol.Build(Cmd.AnswerKey, round, currentQuestion.Answer.Split(';')[0].Trim()));
            else if (p.TriesUsed > 0) h.Send(Protocol.Build(Cmd.AnswerWrong, round, TriesPerQuestion - p.TriesUsed));
        }

        private static string BalloonLine(PlayerState p)
        {
            return Protocol.Build(Cmd.Balloon, p.Nickname, p.Balloon, p.TriesUsed);
        }

        private string PlayersLine()
        {
            List<PlayerState> sorted = new List<PlayerState>(players);
            sorted.Sort(ComparePlayers);
            List<object> fields = new List<object>();
            fields.Add(sorted.Count);
            foreach (PlayerState p in sorted)
            {
                fields.Add(p.Nickname);
                fields.Add(p.Marks);
                fields.Add(p.Connected ? 1 : 0);
            }
            return Protocol.Build(Cmd.Players, fields.ToArray());
        }

        private string StandingsLine()
        {
            List<PlayerState> sorted = new List<PlayerState>(players);
            sorted.Sort(ComparePlayers);
            List<object> fields = new List<object>();
            fields.Add(round);
            fields.Add(totalQuestions);
            fields.Add(sorted.Count);
            foreach (PlayerState p in sorted)
            {
                fields.Add(p.Nickname);
                fields.Add(p.Marks);
                fields.Add(p.LastGained);
                fields.Add(p.Rank);
                fields.Add(p.PreviousRank);
            }
            return Protocol.Build(Cmd.Standings, fields.ToArray());
        }

        private void BroadcastPlayers() { Broadcast(PlayersLine()); }

        private void SendToAdmins(string line)
        {
            foreach (ConnectionHandler a in admins) a.Send(line);
        }

        /// <summary>Sends one message to every connected player and every watching admin.</summary>
        private void Broadcast(string line)
        {
            foreach (PlayerState p in players) if (p.Connected) p.Connection.Send(line);
            foreach (ConnectionHandler a in admins) a.Send(line);
        }
    }
}
