/*
 * Bwat Server / RoomManager.cs
 *
 * Keeps the list of rooms that are waiting or playing right now.
 * Many client threads use this list at the same time, so every access is
 * protected by a lock (roomsLock).
 */
using System;
using System.Collections.Generic;
using Bwat.Common;

namespace Bwat.Server
{
    public static class RoomManager
    {
        private static readonly Dictionary<string, GameRoom> rooms = new Dictionary<string, GameRoom>(StringComparer.OrdinalIgnoreCase);
        private static readonly object roomsLock = new object();
        private static readonly Random random = new Random();

        public const int MaxPlayersPerRoom = 40;

        /// <summary>The address players use to reach this server (set by Program at start-up). Used in room links.</summary>
        public static string PublicHost = "127.0.0.1";
        public static int Port = Protocol.DefaultPort;

        public static string LinkFor(string code)
        {
            return Protocol.MakeLink(PublicHost, Port, code);
        }

        /// <summary>When the server starts, rooms that were waiting are loaded again from the database.</summary>
        public static void LoadWaitingRooms()
        {
            foreach (RoomRecord r in Database.LoadWaitingRooms())
            {
                GameRoom room = new GameRoom(r, LinkFor(r.Code));
                lock (roomsLock) rooms[r.Code] = room;
                room.Start();
                ServerLog.Info("[ROOM] Re-opened waiting room " + r.Code + " (" + r.Name + "), starts " + r.StartTime.ToString("HH:mm"));
            }
        }

        public static GameRoom Find(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            lock (roomsLock)
            {
                GameRoom room;
                return rooms.TryGetValue(code.Trim(), out room) ? room : null;
            }
        }

        public static void Remove(GameRoom room)
        {
            lock (roomsLock)
            {
                GameRoom current;
                if (rooms.TryGetValue(room.Code, out current) && current == room)
                    rooms.Remove(room.Code);
            }
        }

        /// <summary>
        /// Validates the admin's settings, picks the questions and creates the room.
        /// Returns the new room code, or sets error and returns null.
        /// </summary>
        public static string CreateRoom(UserRecord admin, string name, int levelId, int maxPlayers,
                                        int questionCount, int startInMinutes, out string error)
        {
            error = null;
            name = (name ?? "").Trim();
            if (name.Length < 1 || name.Length > 60) { error = "Give the room a name (up to 60 characters)."; return null; }
            if (!Levels.IsValid(levelId)) { error = "Choose a valid class level."; return null; }
            if (maxPlayers < 2 || maxPlayers > MaxPlayersPerRoom) { error = "Players must be between 2 and " + MaxPlayersPerRoom + "."; return null; }
            if (questionCount < 1 || questionCount > 50) { error = "Questions must be between 1 and 50."; return null; }
            if (startInMinutes < 0 || startInMinutes > 1440) { error = "Start time must be within the next 24 hours."; return null; }

            int available = Database.CountQuestions(levelId);
            if (available < questionCount)
            {
                error = "Only " + available + " questions exist for " + Levels.Name(levelId) + ". Add more questions or choose fewer.";
                return null;
            }
            // Random questions, then sorted from easy to hard.
            List<QuestionRecord> picked = Database.PickRandomQuestions(levelId, questionCount);
            List<QuestionRecord> ordered = new List<QuestionRecord>();
            for (int d = 1; d <= 3; d++)
                foreach (QuestionRecord q in picked)
                    if (Math.Max(1, Math.Min(3, q.Difficulty)) == d) ordered.Add(q);

            RoomRecord room = new RoomRecord();
            room.Code = NewCode();
            room.Name = name;
            room.SubjectId = Database.MathSubjectId;
            room.LevelId = levelId;
            room.MaxPlayers = maxPlayers;
            room.StartTime = DateTime.Now.AddMinutes(startInMinutes);
            room.Status = RoomStatus.Waiting;
            room.CreatedBy = admin.Id;
            room.Questions = ordered;
            Database.CreateRoom(room);

            GameRoom gameRoom = new GameRoom(room, LinkFor(room.Code));
            lock (roomsLock) rooms[room.Code] = gameRoom;
            gameRoom.Start();
            ServerLog.Good("[ROOM] " + admin.Username + " created room " + room.Code + " '" + name + "' (" +
                           Levels.Name(levelId) + ", " + questionCount + " questions, max " + maxPlayers + " players).");
            ServerLog.Good("       Link: " + gameRoom.Link);
            return room.Code;
        }

        private static string NewCode()
        {
            while (true)
            {
                string code;
                lock (random) code = "BWAT-" + random.Next(1000, 10000);
                if (Find(code) == null && !Database.RoomCodeExists(code)) return code;
            }
        }
    }
}
