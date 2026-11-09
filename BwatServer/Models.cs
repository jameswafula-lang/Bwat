/*
 * Bwat Server / Models.cs
 * Simple classes that hold one row of data from the database.
 */
using System;
using System.Collections.Generic;

namespace Bwat.Server
{
    /// <summary>An admin account (players do not have accounts - they join with a nickname).</summary>
    public class UserRecord
    {
        public int Id;
        public string Username;
        public string DisplayName;
        public string PasswordHash;
        public string Salt;
        public bool IsActive;
        public string CreatedAt;
    }

    public class QuestionRecord
    {
        public int Id;
        public int SubjectId;
        public int LevelId;
        public string Text;
        public string Answer;         // accepted answers separated by ';' (the first one is shown after the round)
        public int Difficulty;        // 1 = easy (30 s), 2 = medium (45 s), 3 = hard (60 s)
        public bool DifficultyManual; // true if the admin fixed the difficulty (the engine will not change it)
        public int TimesAsked;        // times the question was played
        public int TimesCorrect;      // times somebody solved it
    }

    public class RoomRecord
    {
        public int Id;
        public string Code;
        public string Name;
        public int SubjectId;
        public int LevelId;
        public int MaxPlayers;
        public DateTime StartTime;
        public string Status;
        public int CreatedBy;
        public List<QuestionRecord> Questions = new List<QuestionRecord>();
    }

    /// <summary>Room status values stored in the Rooms table.</summary>
    public static class RoomStatus
    {
        public const string Waiting = "Waiting";
        public const string Playing = "Playing";
        public const string Finished = "Finished";
        public const string Cancelled = "Cancelled";
        public const string Interrupted = "Interrupted";
    }
}
