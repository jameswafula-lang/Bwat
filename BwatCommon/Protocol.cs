/*
 * Bwat - Educational Battle Quiz
 * BwatCommon / Protocol.cs
 *
 * The communication protocol shared by the client and the server.
 *
 * Every message is ONE line of text sent over a TCP NetworkStream using
 * StreamWriter.WriteLine() and read with StreamReader.ReadLine()
 * (the same technique as the "TCP Server/Client" lecture example).
 *
 * A line looks like:   COMMAND|field1|field2|field3
 *
 * Because '|' separates the fields, any '|', '\' or new line INSIDE a field
 * is "escaped" before sending and "unescaped" after receiving:
 *      \  ->  \\        |  ->  \p        newline  ->  \n
 */
using System;
using System.Collections.Generic;
using System.Text;

namespace Bwat.Common
{
    public static class Protocol
    {
        public const int DefaultPort = 8765;
        public const char Separator = '|';
        public const string AppName = "Bwat";
        public const string Version = "2.0";
        public const string LinkScheme = "bwat";

        /// <summary>Makes a value safe to put inside one field of a message.</summary>
        public static string Escape(string value)
        {
            if (value == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in value)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '|') sb.Append("\\p");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') { /* dropped */ }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Reverses Escape().</summary>
        public static string Unescape(string value)
        {
            if (value == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\' && i + 1 < value.Length)
                {
                    char next = value[i + 1];
                    if (next == '\\') sb.Append('\\');
                    else if (next == 'p') sb.Append('|');
                    else if (next == 'n') sb.Append('\n');
                    else sb.Append(next);
                    i++;
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Builds one protocol line: COMMAND|f1|f2|...</summary>
        public static string Build(string command, params object[] fields)
        {
            StringBuilder sb = new StringBuilder(command);
            if (fields != null)
            {
                foreach (object f in fields)
                {
                    sb.Append(Separator);
                    sb.Append(Escape(f == null ? "" : Convert.ToString(f, System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
            return sb.ToString();
        }

        /// <summary>Splits a received line into a Message object.</summary>
        public static Message Parse(string line)
        {
            if (line == null) return null;
            string[] parts = line.Split(Separator);
            string[] fields = new string[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
                fields[i - 1] = Unescape(parts[i]);
            return new Message(parts[0].Trim().ToUpper(), fields);
        }

        /// <summary>
        /// Port 443 means "the server is on the internet (Render), connect securely with a WebSocket (wss://)".
        /// Any other port means a plain TCP connection (local network).
        /// </summary>
        public const int SecurePort = 443;

        /// <summary>The port to use when none was typed: Render addresses use 443, everything else 8765.</summary>
        public static int DefaultPortFor(string host)
        {
            return host != null && host.EndsWith(".onrender.com", StringComparison.OrdinalIgnoreCase) ? SecurePort : DefaultPort;
        }

        /// <summary>Makes a room link such as  bwat://192.168.1.20:8765/BWAT-4821</summary>
        public static string MakeLink(string host, int port, string code)
        {
            return LinkScheme + "://" + host + ":" + port + "/" + code;
        }

        /// <summary>
        /// Reads a room link (bwat://host:port/CODE) or just a code (BWAT-4821 or 4821).
        /// host is null when only a code was given. Returns false if the text is not valid.
        /// </summary>
        public static bool ParseLink(string text, out string host, out int port, out string code)
        {
            host = null;
            port = DefaultPort;
            code = null;
            text = (text ?? "").Trim();
            if (text.Length == 0) return false;

            string prefix = LinkScheme + "://";
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = text.Substring(prefix.Length).TrimEnd('/');
                int slash = rest.IndexOf('/');
                if (slash <= 0) return false;
                string address = rest.Substring(0, slash);
                code = rest.Substring(slash + 1).Trim().ToUpper();
                int colon = address.LastIndexOf(':');
                if (colon > 0)
                {
                    host = address.Substring(0, colon);
                    if (!int.TryParse(address.Substring(colon + 1), out port) || port < 1 || port > 65535) return false;
                }
                else { host = address; port = DefaultPortFor(host); }
            }
            else code = text.ToUpper();

            if (code.Length == 4 && char.IsDigit(code[0])) code = "BWAT-" + code;   // typed only the 4 digits
            return code.Length > 0 && (host == null || host.Length > 0);
        }
    }

    /// <summary>One decoded protocol message.</summary>
    public class Message
    {
        private readonly string command;
        private readonly string[] fields;

        public Message(string command, string[] fields)
        {
            this.command = command;
            this.fields = fields;
        }

        public string Command { get { return command; } }
        public string[] Fields { get { return fields; } }
        public int Count { get { return fields.Length; } }

        /// <summary>Returns field i or "" if it does not exist (never crashes).</summary>
        public string Get(int i)
        {
            if (i < 0 || i >= fields.Length) return "";
            return fields[i];
        }

        /// <summary>Returns field i as a number, or the default value if it is not a valid number.</summary>
        public int GetInt(int i, int defaultValue)
        {
            int value;
            if (int.TryParse(Get(i), out value)) return value;
            return defaultValue;
        }

        public override string ToString()
        {
            return command + (fields.Length > 0 ? "|" + string.Join("|", fields) : "");
        }
    }

    /// <summary>All command names used in the protocol (client -> server and server -> client).</summary>
    public static class Cmd
    {
        // ---------- client -> server: anybody ----------
        public const string Ping = "PING";
        public const string Login = "LOGIN";              // username|password        (admins only)
        public const string Logout = "LOGOUT";
        public const string Join = "JOIN";                // code|nickname  (players)   or   code  (logged-in admin = watch)
        public const string Leave = "LEAVE";
        public const string Answer = "ANSWER";            // round|typedAnswer

        // ---------- client -> server: admin only ----------
        public const string QuestionList = "Q_LIST";      // levelId (0 = all)
        public const string QuestionSave = "Q_SAVE";      // id(0=new)|levelId|text|answer|difficulty(0=auto)
        public const string QuestionDelete = "Q_DELETE";  // id
        public const string RoomCreate = "ROOM_CREATE";   // name|levelId|maxPlayers|questionCount|startInMinutes
        public const string RoomList = "ROOM_LIST";
        public const string RoomStart = "ROOM_START";     // code
        public const string RoomCancel = "ROOM_CANCEL";   // code
        public const string RoomSkip = "ROOM_SKIP";       // code        skip the current question
        public const string RoomKick = "ROOM_KICK";       // code|nickname
        public const string RoomEnd = "ROOM_END";         // code        end the game now (final results)
        public const string AdminList = "ADMIN_LIST";
        public const string AdminCreate = "ADMIN_CREATE"; // username|displayName|password
        public const string AdminSetActive = "ADMIN_SET_ACTIVE"; // userId|1/0
        public const string GamesList = "GAMES_LIST";
        public const string GameResults = "GAME_RESULTS"; // code
        public const string AuditList = "AUDIT_LIST";

        // ---------- server -> client ----------
        public const string Welcome = "WELCOME";          // text
        public const string Ok = "OK";                    // command|message|extra
        public const string Error = "ERR";                // command|message
        public const string LoginOk = "LOGIN_OK";         // userId|username|displayName
        public const string ListBegin = "BEGIN";          // listType
        public const string ListRow = "ROW";              // listType|values...
        public const string ListEnd = "END";              // listType
        public const string RoomJoined = "ROOM_JOINED";   // code|name|levelId|questionCount|secondsToStart|watching(1/0)|status|nickname|link
        public const string Players = "PLAYERS";          // count|(nickname|marks|online)*count
        public const string GameStart = "GAME_START";     // totalQuestions|marksPerQuestion
        public const string Question = "QUESTION";        // round|total|text|seconds|marks|difficulty|timerRound(1-3)
        public const string AnswerKey = "ANSWER_KEY";     // round|answer          (admins watching only)
        public const string AnswerWrong = "ANSWER_WRONG"; // round|triesLeft       (only to the player who answered)
        public const string Balloon = "BALLOON";          // nickname|state|triesUsed   (admins watching only)
        public const string TimerRestart = "TIMER_RESTART"; // round|seconds|timerRound
        public const string RoundResult = "ROUND_RESULT"; // round|winnerNickname(empty = BONGO)|answer|marks|seconds
        public const string Standings = "STANDINGS";      // round|total|count|(nickname|marks|gained|rank|previousRank)*count
        public const string FinalResults = "FINAL_RESULTS"; // bongo(1/0)|count|(nickname|marks|solved|place)*count
        public const string Kicked = "KICKED";            // reason
        public const string RoomCancelled = "ROOM_CANCELLED"; // reason
        public const string Info = "INFO";                // text
    }

    /// <summary>Balloon states (one balloon per player per question).</summary>
    public static class BalloonState
    {
        public const string Full = "FULL";          // waiting for an answer
        public const string Shrunk = "SHRUNK";      // wrong answer, tries left
        public const string Floated = "FLOATED";    // solved first: floats away
        public const string Popped = "POPPED";      // 3 wrong answers, time out, or somebody else was first
    }

    /// <summary>List types used with BEGIN / ROW / END.</summary>
    public static class ListType
    {
        public const string Questions = "QUESTIONS";     // id|levelId|text|answer|difficulty|manual|asked|solved
        public const string Rooms = "ROOMS";             // code|name|levelId|status|players|maxPlayers|startTime|questions|link
        public const string Admins = "ADMINS";           // id|username|display|active|createdAt
        public const string Games = "GAMES";             // code|name|levelId|date|status|players|winner|winnerMarks
        public const string GameResults = "GAME_RESULTS"; // place|nickname|marks|solved|removed
        public const string Audit = "AUDIT";             // time|who|action|detail
    }

    /// <summary>The two roles: admins log in with a password, players join as guests with a nickname.</summary>
    public static class Roles
    {
        public const string Admin = "Admin";
        public const string Guest = "Guest";
    }

    /// <summary>The class levels used for questions and rooms (baby class to university).</summary>
    public static class Levels
    {
        private static readonly string[] names =
        {
            "Nursery (Baby-Top)",
            "Lower Primary (P1-P3)",
            "Upper Primary (P4-P7)",
            "O-Level (S1-S4)",
            "A-Level (S5-S6)",
            "University"
        };

        public const int Min = 1;
        public static int Max { get { return names.Length; } }

        public static string Name(int levelId)
        {
            if (levelId < Min || levelId > names.Length) return "Unknown";
            return names[levelId - 1];
        }

        public static bool IsValid(int levelId)
        {
            return levelId >= Min && levelId <= names.Length;
        }

        public static string[] All()
        {
            return (string[])names.Clone();
        }
    }

    /// <summary>The time allowed for a question depends on its difficulty.</summary>
    public static class Timing
    {
        public static int SecondsFor(int difficulty)
        {
            if (difficulty >= 3) return 60;
            if (difficulty == 2) return 45;
            return 30;
        }
    }
}
