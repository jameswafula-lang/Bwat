/*
 * Bwat Server / DifficultyEngine.cs
 *
 * The "smart difficulty engine" rates every question:
 *      1 = Easy   2 = Medium   3 = Hard
 * The rating sets the TIME for a question (easy 30 s, medium 45 s, hard 60 s) and every
 * quiz is played from easy to hard. (Every question is worth the same 5 marks.)
 *
 * It works in two stages:
 *
 *  1. FIRST GUESS (EstimateInitial) - when a question is added, the engine
 *     reads it and scores features that usually make a math question harder:
 *     number of operations, big numbers, fractions/decimals, powers and roots,
 *     unknowns (x), long word problems, and non-whole-number answers.
 *
 *  2. LEARNING FROM PLAY (Recalculate) - every time the question is played,
 *     the server records whether somebody solved it in time. Once a question has
 *     been played 5+ times, the real solve rate is blended with the first
 *     guess. The more often it is played, the more the real results count.
 *     So questions that players find hard automatically move later in the quiz.
 *
 * Admins can switch this off for a question by choosing a fixed difficulty.
 */
using System;

namespace Bwat.Server
{
    public static class DifficultyEngine
    {
        public const int MinPlaysBeforeLearning = 5;
        public const int PlaysForFullTrust = 20;

        public static int EstimateInitial(int levelId, string text, string answer)
        {
            if (text == null) text = "";
            if (answer == null) answer = "";
            string t = text.ToLower();
            int score = 0;

            // 1. How many operations does the question contain?
            int operations = 0;
            foreach (char c in t)
                if (c == '+' || c == '-' || c == '×' || c == '*' || c == '÷' || c == '/' || c == '^' || c == '%')
                    operations++;
            if (operations >= 2) score += 1;
            if (operations >= 3) score += 1;

            // 2. Big numbers are harder to work with mentally.
            int digitsInARow = 0, longestNumber = 0;
            foreach (char c in t)
            {
                if (char.IsDigit(c)) { digitsInARow++; if (digitsInARow > longestNumber) longestNumber = digitsInARow; }
                else digitsInARow = 0;
            }
            if (longestNumber >= 3) score += 1;
            if (longestNumber >= 5) score += 1;

            // 3. Special topics.
            string[] hardWords = { "fraction", "decimal", "percent", "%", "√", "root", "power", "^", "²", "³",
                                   "solve", "x =", "derivative", "dy/dx", "∫", "integral", "log", "ln", "matrix",
                                   "determinant", "probability", "limit", "sequence", "term", "ratio", "mod" };
            int topicHits = 0;
            foreach (string w in hardWords)
                if (t.Contains(w)) topicHits++;
            if (topicHits >= 1) score += 1;
            if (topicHits >= 3) score += 1;

            // 4. Word problems take longer to read and understand.
            if (t.Length > 45) score += 1;

            // 5. Answers that are not whole numbers are harder to get exactly right.
            string a = answer.Split(';')[0].Trim();
            if (a.Contains(".") || a.Contains("/") || a.StartsWith("-")) score += 1;

            if (score == 0) return 1;
            if (score <= 2) return 2;
            return 3;
        }

        /// <summary>Blends the first guess with the real solve rate from played games.</summary>
        public static int Recalculate(QuestionRecord q)
        {
            if (q.TimesAsked < MinPlaysBeforeLearning)
                return q.Difficulty;

            double solveRate = (double)q.TimesCorrect / q.TimesAsked;
            int observed;
            if (solveRate >= 0.75) observed = 1;
            else if (solveRate >= 0.40) observed = 2;
            else observed = 3;

            int initial = EstimateInitial(q.LevelId, q.Text, q.Answer);
            double trust = Math.Min(q.TimesAsked, PlaysForFullTrust) / (double)PlaysForFullTrust;
            int blended = (int)Math.Round(initial * (1 - trust) + observed * trust, MidpointRounding.AwayFromZero);
            if (blended < 1) blended = 1;
            if (blended > 3) blended = 3;
            return blended;
        }
    }
}
