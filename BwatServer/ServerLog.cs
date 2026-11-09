/*
 * Bwat Server / ServerLog.cs
 * Coloured console output so the requests and responses can be SEEN during
 * the demonstration:   <-  request from a client      ->  response to a client
 * Many threads write at the same time, so writing is protected by a lock.
 */
using System;

namespace Bwat.Server
{
    public static class ServerLog
    {
        private static readonly object consoleLock = new object();
        public static bool ShowTraffic = true;

        public static void Info(string text) { Write(ConsoleColor.Gray, text); }
        public static void Good(string text) { Write(ConsoleColor.Green, text); }
        public static void Warn(string text) { Write(ConsoleColor.Yellow, text); }
        public static void Error(string text) { Write(ConsoleColor.Red, text); }
        public static void Game(string text) { Write(ConsoleColor.Magenta, text); }

        public static void Request(string who, string line)
        {
            if (ShowTraffic) Write(ConsoleColor.Cyan, "[" + who + "] <- " + Shorten(line));
        }

        public static void Response(string who, string line)
        {
            if (ShowTraffic) Write(ConsoleColor.DarkGreen, "[" + who + "] -> " + Shorten(line));
        }

        private static string Shorten(string line)
        {
            if (line == null) return "";
            return line.Length > 140 ? line.Substring(0, 140) + "..." : line;
        }

        private static void Write(ConsoleColor color, string text)
        {
            lock (consoleLock)
            {
                ConsoleColor old = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
                Console.ForegroundColor = old;
            }
        }
    }
}
