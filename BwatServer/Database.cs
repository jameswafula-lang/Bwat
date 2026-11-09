/*
 * Bwat Server / Database.cs
 *
 * ALL database access of the system happens here, on the server.
 * Clients never connect to the database directly - they send requests to the
 * server, and the server calls these methods (3-tier: client -> server -> database).
 *
 * Database: SQLite (a single file "bwat.db" next to BwatServer.exe, or the path in BWAT_DB_PATH).
 *
 * Security / reliability measures:
 *  - Every query uses PARAMETERS (@name) instead of joining strings,
 *    which prevents SQL injection.
 *  - Admin passwords are never stored - only a salted PBKDF2 hash (PasswordHasher.cs).
 *  - Many client threads use the database at the same time, so every method
 *    takes the same lock (dbLock). Only one thread writes at a time and the
 *    shared data cannot be corrupted.
 *  - Multi-step changes (creating a room with its questions) use a TRANSACTION:
 *    either everything is saved or nothing is.
 */
using System;
using System.Collections.Generic;
#if MONO
// Linux / Docker (Render): Mono's built-in SQLite provider, same classes under the names used below.
using Mono.Data.Sqlite;
using SQLiteConnection = Mono.Data.Sqlite.SqliteConnection;
using SQLiteCommand = Mono.Data.Sqlite.SqliteCommand;
using SQLiteDataReader = Mono.Data.Sqlite.SqliteDataReader;
using SQLiteTransaction = Mono.Data.Sqlite.SqliteTransaction;
#else
// Windows (SharpDevelop / Visual Studio): System.Data.SQLite from the lib folder.
using System.Data.SQLite;
#endif
using System.Globalization;

namespace Bwat.Server
{
    public static class Database
    {
        private static string connectionString;
        private static readonly object dbLock = new object();
        public const int MathSubjectId = 1;
        public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        // ------------------------------------------------------------------
        //  Setup
        // ------------------------------------------------------------------

        /// <summary>Opens (or creates) the database file, creates the tables and adds the starting data.</summary>
        public static void Initialize(string fileName)
        {
            connectionString = "Data Source=" + fileName + ";Version=3;";
            lock (dbLock)
            {
                using (SQLiteConnection con = Open())
                {
                    string[] tables =
                    {
                        @"CREATE TABLE IF NOT EXISTS Levels(
                            Id INTEGER PRIMARY KEY,
                            Name TEXT NOT NULL UNIQUE)",
                        @"CREATE TABLE IF NOT EXISTS Subjects(
                            Id INTEGER PRIMARY KEY,
                            Name TEXT NOT NULL UNIQUE)",
                        @"CREATE TABLE IF NOT EXISTS Admins(
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                            DisplayName TEXT NOT NULL,
                            PasswordHash TEXT NOT NULL,
                            Salt TEXT NOT NULL,
                            IsActive INTEGER NOT NULL DEFAULT 1,
                            CreatedAt TEXT NOT NULL)",
                        @"CREATE TABLE IF NOT EXISTS Questions(
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            SubjectId INTEGER NOT NULL REFERENCES Subjects(Id),
                            LevelId INTEGER NOT NULL REFERENCES Levels(Id),
                            Text TEXT NOT NULL,
                            Answer TEXT NOT NULL,
                            Difficulty INTEGER NOT NULL DEFAULT 1,
                            DifficultyManual INTEGER NOT NULL DEFAULT 0,
                            TimesAsked INTEGER NOT NULL DEFAULT 0,
                            TimesCorrect INTEGER NOT NULL DEFAULT 0,
                            IsDeleted INTEGER NOT NULL DEFAULT 0,
                            CreatedBy INTEGER REFERENCES Admins(Id),
                            CreatedAt TEXT NOT NULL)",
                        @"CREATE TABLE IF NOT EXISTS Rooms(
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Code TEXT NOT NULL UNIQUE,
                            Name TEXT NOT NULL,
                            SubjectId INTEGER NOT NULL REFERENCES Subjects(Id),
                            LevelId INTEGER NOT NULL REFERENCES Levels(Id),
                            MaxPlayers INTEGER NOT NULL,
                            StartTime TEXT NOT NULL,
                            Status TEXT NOT NULL,
                            CreatedBy INTEGER REFERENCES Admins(Id),
                            Winner TEXT,
                            EndedAt TEXT)",
                        @"CREATE TABLE IF NOT EXISTS RoomQuestions(
                            RoomId INTEGER NOT NULL REFERENCES Rooms(Id),
                            QuestionId INTEGER NOT NULL REFERENCES Questions(Id),
                            OrderNo INTEGER NOT NULL,
                            PRIMARY KEY(RoomId, OrderNo))",
                        @"CREATE TABLE IF NOT EXISTS RoomPlayers(
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            RoomId INTEGER NOT NULL REFERENCES Rooms(Id),
                            Nickname TEXT NOT NULL,
                            JoinedAt TEXT NOT NULL,
                            Marks INTEGER NOT NULL DEFAULT 0,
                            Solved INTEGER NOT NULL DEFAULT 0,
                            Placement INTEGER NOT NULL DEFAULT 0,
                            Removed INTEGER NOT NULL DEFAULT 0,
                            UNIQUE(RoomId, Nickname))",
                        @"CREATE TABLE IF NOT EXISTS PlayerAnswers(
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            RoomId INTEGER NOT NULL REFERENCES Rooms(Id),
                            RoomPlayerId INTEGER NOT NULL REFERENCES RoomPlayers(Id),
                            RoundNo INTEGER NOT NULL,
                            QuestionId INTEGER NOT NULL REFERENCES Questions(Id),
                            Answer TEXT NOT NULL,
                            IsCorrect INTEGER NOT NULL,
                            TimeMs INTEGER NOT NULL,
                            Marks INTEGER NOT NULL,
                            AnsweredAt TEXT NOT NULL)",
                        @"CREATE TABLE IF NOT EXISTS GameEvents(
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            RoomId INTEGER NOT NULL REFERENCES Rooms(Id),
                            EventTime TEXT NOT NULL,
                            EventType TEXT NOT NULL,
                            Detail TEXT)",
                        @"CREATE TABLE IF NOT EXISTS AuditLog(
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            EventTime TEXT NOT NULL,
                            Who TEXT,
                            Action TEXT NOT NULL,
                            Detail TEXT)"
                    };
                    foreach (string sql in tables)
                        Execute(con, sql);

                    // Starting data: levels, subjects and the question bank.
                    for (int i = Bwat.Common.Levels.Min; i <= Bwat.Common.Levels.Max; i++)
                        Execute(con, "INSERT OR IGNORE INTO Levels(Id, Name) VALUES(@id, @name)",
                                "@id", i, "@name", Bwat.Common.Levels.Name(i));
                    Execute(con, "INSERT OR IGNORE INTO Subjects(Id, Name) VALUES(1, 'Mathematics')");

                    if ((long)Scalar(con, "SELECT COUNT(*) FROM Questions") == 0)
                        SeedQuestions(con);

                    // A game that was running when the server stopped cannot continue.
                    Execute(con, "UPDATE Rooms SET Status=@s WHERE Status=@p",
                            "@s", RoomStatus.Interrupted, "@p", RoomStatus.Playing);
                }
            }
        }

        private static void SeedQuestions(SQLiteConnection con)
        {
            using (SQLiteTransaction tx = con.BeginTransaction())
            {
                foreach (string[] q in QuestionBank.Questions)
                {
                    int level = int.Parse(q[0]);
                    int difficulty = DifficultyEngine.EstimateInitial(level, q[1], q[2]);
                    Execute(con, @"INSERT INTO Questions(SubjectId, LevelId, Text, Answer, Difficulty, CreatedAt)
                                   VALUES(@s, @l, @t, @a, @d, @c)",
                            "@s", MathSubjectId, "@l", level, "@t", q[1], "@a", q[2], "@d", difficulty, "@c", Now());
                }
                tx.Commit();
            }
            Console.WriteLine("[DB] Question bank created with " + QuestionBank.Questions.Length + " math questions.");
        }

        // ------------------------------------------------------------------
        //  Small helpers
        // ------------------------------------------------------------------

        private static SQLiteConnection Open()
        {
            SQLiteConnection con = new SQLiteConnection(connectionString);
            con.Open();
            Execute(con, "PRAGMA foreign_keys = ON");
            return con;
        }

        public static string Now()
        {
            return DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>Creates a command and adds parameters given as pairs: "@name", value, "@name2", value2 ...</summary>
        private static SQLiteCommand MakeCommand(SQLiteConnection con, string sql, object[] args)
        {
            SQLiteCommand cmd = new SQLiteCommand(sql, con);
            for (int i = 0; i + 1 < args.Length; i += 2)
                cmd.Parameters.AddWithValue((string)args[i], args[i + 1] ?? DBNull.Value);
            return cmd;
        }

        private static int Execute(SQLiteConnection con, string sql, params object[] args)
        {
            using (SQLiteCommand cmd = MakeCommand(con, sql, args))
                return cmd.ExecuteNonQuery();
        }

        private static object Scalar(SQLiteConnection con, string sql, params object[] args)
        {
            using (SQLiteCommand cmd = MakeCommand(con, sql, args))
                return cmd.ExecuteScalar();
        }

        private static long LastId(SQLiteConnection con)
        {
            return (long)Scalar(con, "SELECT last_insert_rowid()");
        }

        /// <summary>Runs a SELECT and turns every row into a string array.</summary>
        private static List<string[]> Rows(string sql, params object[] args)
        {
            List<string[]> rows = new List<string[]>();
            lock (dbLock)
            using (SQLiteConnection con = Open())
            using (SQLiteCommand cmd = MakeCommand(con, sql, args))
            using (SQLiteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    string[] row = new string[r.FieldCount];
                    for (int i = 0; i < r.FieldCount; i++) row[i] = ToStr(r[i]);
                    rows.Add(row);
                }
            }
            return rows;
        }

        private static int ToInt(object o) { return o == null || o is DBNull ? 0 : Convert.ToInt32(o); }
        private static string ToStr(object o) { return o == null || o is DBNull ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }

        // ------------------------------------------------------------------
        //  Admin accounts
        // ------------------------------------------------------------------

        private static UserRecord ReadUser(SQLiteDataReader r)
        {
            UserRecord u = new UserRecord();
            u.Id = ToInt(r["Id"]);
            u.Username = ToStr(r["Username"]);
            u.DisplayName = ToStr(r["DisplayName"]);
            u.PasswordHash = ToStr(r["PasswordHash"]);
            u.Salt = ToStr(r["Salt"]);
            u.IsActive = ToInt(r["IsActive"]) == 1;
            u.CreatedAt = ToStr(r["CreatedAt"]);
            return u;
        }

        public static UserRecord GetAdmin(string username)
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
            using (SQLiteCommand cmd = MakeCommand(con, "SELECT * FROM Admins WHERE Username=@u", new object[] { "@u", username }))
            using (SQLiteDataReader r = cmd.ExecuteReader())
            {
                return r.Read() ? ReadUser(r) : null;
            }
        }

        /// <summary>Creates an admin. Returns the new id, or -1 if the username already exists.</summary>
        public static int CreateAdmin(string username, string displayName, string password)
        {
            string salt = PasswordHasher.NewSalt();
            string hash = PasswordHasher.Hash(password, salt);
            lock (dbLock)
            using (SQLiteConnection con = Open())
            {
                if ((long)Scalar(con, "SELECT COUNT(*) FROM Admins WHERE Username=@u", "@u", username) > 0) return -1;
                Execute(con, @"INSERT INTO Admins(Username, DisplayName, PasswordHash, Salt, IsActive, CreatedAt)
                               VALUES(@u, @d, @h, @s, 1, @c)",
                        "@u", username, "@d", displayName, "@h", hash, "@s", salt, "@c", Now());
                return (int)LastId(con);
            }
        }

        public static int CountActiveAdmins()
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
                return ToInt(Scalar(con, "SELECT COUNT(*) FROM Admins WHERE IsActive=1"));
        }

        /// <summary>id|username|display|active|createdAt</summary>
        public static List<string[]> ListAdmins()
        {
            return Rows("SELECT Id, Username, DisplayName, IsActive, CreatedAt FROM Admins ORDER BY Username");
        }

        public static bool SetAdminActive(int id, bool active)
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
                return Execute(con, "UPDATE Admins SET IsActive=@a WHERE Id=@id", "@a", active ? 1 : 0, "@id", id) == 1;
        }

        // ------------------------------------------------------------------
        //  Questions
        // ------------------------------------------------------------------

        private static QuestionRecord ReadQuestion(SQLiteDataReader r)
        {
            QuestionRecord q = new QuestionRecord();
            q.Id = ToInt(r["Id"]);
            q.SubjectId = ToInt(r["SubjectId"]);
            q.LevelId = ToInt(r["LevelId"]);
            q.Text = ToStr(r["Text"]);
            q.Answer = ToStr(r["Answer"]);
            q.Difficulty = ToInt(r["Difficulty"]);
            q.DifficultyManual = ToInt(r["DifficultyManual"]) == 1;
            q.TimesAsked = ToInt(r["TimesAsked"]);
            q.TimesCorrect = ToInt(r["TimesCorrect"]);
            return q;
        }

        private static List<QuestionRecord> Questions(SQLiteConnection con, string sql, object[] args)
        {
            List<QuestionRecord> list = new List<QuestionRecord>();
            using (SQLiteCommand cmd = MakeCommand(con, sql, args))
            using (SQLiteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read()) list.Add(ReadQuestion(r));
            }
            return list;
        }

        public static List<QuestionRecord> ListQuestions(int levelId)
        {
            string sql = "SELECT * FROM Questions WHERE IsDeleted=0" + (levelId > 0 ? " AND LevelId=@l" : "") + " ORDER BY LevelId, Id";
            lock (dbLock)
            using (SQLiteConnection con = Open())
                return Questions(con, sql, new object[] { "@l", levelId });
        }

        /// <summary>Adds a question. difficulty 0 means "let the difficulty engine decide".</summary>
        public static int AddQuestion(int levelId, string text, string answer, int difficulty, int createdBy)
        {
            bool manual = difficulty >= 1 && difficulty <= 3;
            int d = manual ? difficulty : DifficultyEngine.EstimateInitial(levelId, text, answer);
            lock (dbLock)
            using (SQLiteConnection con = Open())
            {
                Execute(con, @"INSERT INTO Questions(SubjectId, LevelId, Text, Answer, Difficulty, DifficultyManual, CreatedBy, CreatedAt)
                               VALUES(@s, @l, @t, @a, @d, @m, @by, @c)",
                        "@s", MathSubjectId, "@l", levelId, "@t", text, "@a", answer, "@d", d,
                        "@m", manual ? 1 : 0, "@by", createdBy, "@c", Now());
                return (int)LastId(con);
            }
        }

        public static bool UpdateQuestion(int id, int levelId, string text, string answer, int difficulty)
        {
            bool manual = difficulty >= 1 && difficulty <= 3;
            int d = manual ? difficulty : DifficultyEngine.EstimateInitial(levelId, text, answer);
            lock (dbLock)
            using (SQLiteConnection con = Open())
                return Execute(con, @"UPDATE Questions SET LevelId=@l, Text=@t, Answer=@a, Difficulty=@d, DifficultyManual=@m
                                      WHERE Id=@id AND IsDeleted=0",
                               "@l", levelId, "@t", text, "@a", answer, "@d", d, "@m", manual ? 1 : 0, "@id", id) == 1;
        }

        /// <summary>Soft delete: the question is hidden but old games that used it stay valid.</summary>
        public static bool DeleteQuestion(int id)
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
                return Execute(con, "UPDATE Questions SET IsDeleted=1 WHERE Id=@id AND IsDeleted=0", "@id", id) == 1;
        }

        public static List<QuestionRecord> PickRandomQuestions(int levelId, int count)
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
                return Questions(con, "SELECT * FROM Questions WHERE IsDeleted=0 AND LevelId=@l ORDER BY RANDOM() LIMIT @n",
                                 new object[] { "@l", levelId, "@n", count });
        }

        public static int CountQuestions(int levelId)
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
                return ToInt(Scalar(con, "SELECT COUNT(*) FROM Questions WHERE IsDeleted=0 AND LevelId=@l", "@l", levelId));
        }

        /// <summary>Records whether a played question was solved, then lets the difficulty engine re-rate it.</summary>
        public static void RecordQuestionResult(int questionId, bool solved)
        {
            if (questionId <= 0) return;
            lock (dbLock)
            using (SQLiteConnection con = Open())
            {
                Execute(con, "UPDATE Questions SET TimesAsked=TimesAsked+1, TimesCorrect=TimesCorrect+@c WHERE Id=@id",
                        "@c", solved ? 1 : 0, "@id", questionId);
                List<QuestionRecord> q = Questions(con, "SELECT * FROM Questions WHERE Id=@id", new object[] { "@id", questionId });
                if (q.Count == 1 && !q[0].DifficultyManual)
                {
                    int newDifficulty = DifficultyEngine.Recalculate(q[0]);
                    if (newDifficulty != q[0].Difficulty)
                        Execute(con, "UPDATE Questions SET Difficulty=@d WHERE Id=@id", "@d", newDifficulty, "@id", questionId);
                }
            }
        }

        // ------------------------------------------------------------------
        //  Rooms, players and answers
        // ------------------------------------------------------------------

        public static bool RoomCodeExists(string code)
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
                return ToInt(Scalar(con, "SELECT COUNT(*) FROM Rooms WHERE Code=@c", "@c", code)) > 0;
        }

        /// <summary>Saves a new room and its question list in ONE transaction.</summary>
        public static int CreateRoom(RoomRecord room)
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
            using (SQLiteTransaction tx = con.BeginTransaction())
            {
                Execute(con, @"INSERT INTO Rooms(Code, Name, SubjectId, LevelId, MaxPlayers, StartTime, Status, CreatedBy)
                               VALUES(@code, @name, @s, @l, @max, @start, @status, @by)",
                        "@code", room.Code, "@name", room.Name, "@s", room.SubjectId, "@l", room.LevelId, "@max", room.MaxPlayers,
                        "@start", room.StartTime.ToString(TimeFormat, CultureInfo.InvariantCulture), "@status", room.Status, "@by", room.CreatedBy);
                int roomId = (int)LastId(con);
                for (int i = 0; i < room.Questions.Count; i++)
                    Execute(con, "INSERT INTO RoomQuestions(RoomId, QuestionId, OrderNo) VALUES(@r, @q, @o)",
                            "@r", roomId, "@q", room.Questions[i].Id, "@o", i + 1);
                tx.Commit();
                room.Id = roomId;
                return roomId;
            }
        }

        /// <summary>Loads rooms that are still waiting to start (used when the server restarts).</summary>
        public static List<RoomRecord> LoadWaitingRooms()
        {
            List<RoomRecord> rooms = new List<RoomRecord>();
            lock (dbLock)
            using (SQLiteConnection con = Open())
            {
                using (SQLiteCommand cmd = MakeCommand(con, "SELECT * FROM Rooms WHERE Status=@s", new object[] { "@s", RoomStatus.Waiting }))
                using (SQLiteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        RoomRecord room = new RoomRecord();
                        room.Id = ToInt(r["Id"]);
                        room.Code = ToStr(r["Code"]);
                        room.Name = ToStr(r["Name"]);
                        room.SubjectId = ToInt(r["SubjectId"]);
                        room.LevelId = ToInt(r["LevelId"]);
                        room.MaxPlayers = ToInt(r["MaxPlayers"]);
                        room.StartTime = DateTime.ParseExact(ToStr(r["StartTime"]), TimeFormat, CultureInfo.InvariantCulture);
                        room.Status = ToStr(r["Status"]);
                        room.CreatedBy = ToInt(r["CreatedBy"]);
                        rooms.Add(room);
                    }
                }
                foreach (RoomRecord room in rooms)
                    room.Questions = Questions(con, @"SELECT q.* FROM RoomQuestions rq JOIN Questions q ON q.Id = rq.QuestionId
                                                      WHERE rq.RoomId=@r ORDER BY rq.OrderNo", new object[] { "@r", room.Id });
            }
            return rooms;
        }

        public static void SetRoomStatus(int roomId, string status, string winner)
        {
            bool ended = status == RoomStatus.Finished || status == RoomStatus.Cancelled || status == RoomStatus.Interrupted;
            lock (dbLock)
            using (SQLiteConnection con = Open())
                Execute(con, "UPDATE Rooms SET Status=@s, Winner=@w, EndedAt=@e WHERE Id=@id",
                        "@s", status, "@w", (object)winner ?? DBNull.Value, "@e", ended ? (object)Now() : DBNull.Value, "@id", roomId);
        }

        /// <summary>Adds a guest player to a room and returns the new RoomPlayers id.</summary>
        public static int AddRoomPlayer(int roomId, string nickname)
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
            {
                Execute(con, "INSERT INTO RoomPlayers(RoomId, Nickname, JoinedAt) VALUES(@r, @n, @t)", "@r", roomId, "@n", nickname, "@t", Now());
                return (int)LastId(con);
            }
        }

        /// <summary>A player who left the lobby before the start is removed again (so the nickname is free).</summary>
        public static void DeleteRoomPlayer(int roomPlayerId)
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
                Execute(con, "DELETE FROM RoomPlayers WHERE Id=@id AND NOT EXISTS (SELECT 1 FROM PlayerAnswers WHERE RoomPlayerId=@id)", "@id", roomPlayerId);
        }

        public static void SaveRoomPlayer(int roomPlayerId, int marks, int solved, int placement, bool removed)
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
                Execute(con, "UPDATE RoomPlayers SET Marks=@m, Solved=@s, Placement=@p, Removed=@r WHERE Id=@id",
                        "@m", marks, "@s", solved, "@p", placement, "@r", removed ? 1 : 0, "@id", roomPlayerId);
        }

        /// <summary>Stores one typed answer (every attempt, right or wrong) - the answer history of every game.</summary>
        public static void SavePlayerAnswer(int roomId, int roomPlayerId, int round, int questionId, string answer, bool correct, int timeMs, int marks)
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
                Execute(con, @"INSERT INTO PlayerAnswers(RoomId, RoomPlayerId, RoundNo, QuestionId, Answer, IsCorrect, TimeMs, Marks, AnsweredAt)
                               VALUES(@r, @p, @n, @q, @a, @c, @t, @m, @at)",
                        "@r", roomId, "@p", roomPlayerId, "@n", round, "@q", questionId, "@a", answer,
                        "@c", correct ? 1 : 0, "@t", timeMs, "@m", marks, "@at", Now());
        }

        public static void AddGameEvent(int roomId, string type, string detail)
        {
            lock (dbLock)
            using (SQLiteConnection con = Open())
                Execute(con, "INSERT INTO GameEvents(RoomId, EventTime, EventType, Detail) VALUES(@r, @t, @e, @d)",
                        "@r", roomId, "@t", Now(), "@e", type, "@d", detail);
        }

        /// <summary>code|name|levelId|status|players|maxPlayers|startTime|questions  (the link is added by the handler)</summary>
        public static List<string[]> ListRooms(int limit)
        {
            return Rows(@"SELECT r.Code, r.Name, r.LevelId, r.Status,
                                 (SELECT COUNT(*) FROM RoomPlayers p WHERE p.RoomId = r.Id) AS Players,
                                 r.MaxPlayers, r.StartTime,
                                 (SELECT COUNT(*) FROM RoomQuestions q WHERE q.RoomId = r.Id) AS QCount
                          FROM Rooms r ORDER BY r.Id DESC LIMIT @n", "@n", limit);
        }

        /// <summary>Past games: code|name|levelId|date|status|players|winner|winnerMarks</summary>
        public static List<string[]> ListGames(int limit)
        {
            return Rows(@"SELECT r.Code, r.Name, r.LevelId, r.StartTime, r.Status,
                                 (SELECT COUNT(*) FROM RoomPlayers p WHERE p.RoomId = r.Id) AS Players,
                                 IFNULL(r.Winner, ''),
                                 IFNULL((SELECT MAX(Marks) FROM RoomPlayers p WHERE p.RoomId = r.Id), 0)
                          FROM Rooms r WHERE r.Status <> @w ORDER BY r.Id DESC LIMIT @n", "@w", RoomStatus.Waiting, "@n", limit);
        }

        /// <summary>Final standings of one game: place|nickname|marks|solved|removed</summary>
        public static List<string[]> GameResults(string code)
        {
            return Rows(@"SELECT p.Placement, p.Nickname, p.Marks, p.Solved, p.Removed
                          FROM RoomPlayers p JOIN Rooms r ON r.Id = p.RoomId
                          WHERE r.Code=@c ORDER BY CASE WHEN p.Placement = 0 THEN 9999 ELSE p.Placement END, p.Nickname", "@c", code);
        }

        // ------------------------------------------------------------------
        //  Audit log
        // ------------------------------------------------------------------

        public static void Audit(string who, string action, string detail)
        {
            try
            {
                lock (dbLock)
                using (SQLiteConnection con = Open())
                    Execute(con, "INSERT INTO AuditLog(EventTime, Who, Action, Detail) VALUES(@t, @w, @a, @d)",
                            "@t", Now(), "@w", who, "@a", action, "@d", detail);
            }
            catch (Exception ex)
            {
                // The audit log must never crash the server.
                Console.WriteLine("[DB] Audit write failed: " + ex.Message);
            }
        }

        /// <summary>time|who|action|detail (newest first)</summary>
        public static List<string[]> ListAudit(int limit)
        {
            return Rows("SELECT EventTime, Who, Action, Detail FROM AuditLog ORDER BY Id DESC LIMIT @n", "@n", limit);
        }
    }
}
