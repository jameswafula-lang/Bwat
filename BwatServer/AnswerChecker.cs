/*
 * Bwat Server / AnswerChecker.cs
 *
 * Decides if a player's answer is correct. Only the SERVER checks answers -
 * the client never receives the correct answer before the round ends, so a
 * player cannot cheat by reading the network messages.
 *
 *  - Several accepted answers can be stored separated by ';'  (e.g. "15/4;3.75")
 *  - Numbers are compared by value: "0.5", ".5", "1/2" and "2/4" are all equal.
 *  - Text answers ignore case and spaces.
 */
using System;
using System.Globalization;

namespace Bwat.Server
{
    public static class AnswerChecker
    {
        private const double Tolerance = 0.01;

        public static bool IsCorrect(string given, string expected)
        {
            if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(expected)) return false;
            string g = Normalize(given);
            if (g.Length == 0) return false;

            foreach (string option in expected.Split(';'))
            {
                string e = Normalize(option);
                if (e.Length == 0) continue;
                if (g == e) return true;

                double gv, ev;
                if (TryParseNumber(g, out gv) && TryParseNumber(e, out ev))
                {
                    if (Math.Abs(gv - ev) <= Tolerance) return true;
                }
            }
            return false;
        }

        private static string Normalize(string s)
        {
            return s.Trim().ToLower().Replace(" ", "").Replace(",", "");
        }

        /// <summary>Reads "12", "-3.5", ".5" or a fraction like "7/8" / "-1/2".</summary>
        public static bool TryParseNumber(string s, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            int slash = s.IndexOf('/');
            if (slash > 0)
            {
                double top, bottom;
                if (!double.TryParse(s.Substring(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out top)) return false;
                if (!double.TryParse(s.Substring(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out bottom)) return false;
                if (bottom == 0) return false;
                value = top / bottom;
                return true;
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
