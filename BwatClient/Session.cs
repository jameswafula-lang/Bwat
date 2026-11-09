/*
 * Bwat Client / Session.cs
 * The connection to the server and who is using this client, shared by all forms.
 * Also remembers the last server address in a small text file.
 */
using System;
using System.IO;
using System.Windows.Forms;
using Bwat.Common;

namespace Bwat.Client
{
    public static class Session
    {
        public static ServerConnection Connection;

        // set when an admin logs in
        public static int AdminId;
        public static string AdminUsername;
        public static string AdminName;

        // set when a player joins a room
        public static string Nickname;

        public static bool IsAdmin { get { return AdminId > 0; } }

        public static void ClearAdmin()
        {
            AdminId = 0;
            AdminUsername = null;
            AdminName = null;
        }

        // ---------------- remembered server address ----------------

        private static string SettingsFile
        {
            get { return Path.Combine(Application.StartupPath, "bwat_client.txt"); }
        }

        public static string LoadServerAddress()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string text = File.ReadAllText(SettingsFile).Trim();
                    if (text.Length > 0) return text;
                }
            }
            catch (Exception) { }
            return "127.0.0.1:" + Protocol.DefaultPort;
        }

        public static void SaveServerAddress(string address)
        {
            try { File.WriteAllText(SettingsFile, address); }
            catch (Exception) { /* not important */ }
        }

        /// <summary>Reads "host" or "host:port" (also accepts a pasted https:// address). Returns false if the text is not valid.</summary>
        public static bool ParseAddress(string text, out string host, out int port)
        {
            host = null;
            port = Protocol.DefaultPort;
            text = (text ?? "").Trim();
            if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) text = text.Substring(8);
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) text = text.Substring(7);
            text = text.TrimEnd('/');
            if (text.Length == 0) return false;
            int colon = text.LastIndexOf(':');
            if (colon > 0)
            {
                host = text.Substring(0, colon).Trim();
                if (!int.TryParse(text.Substring(colon + 1), out port) || port < 1 || port > 65535) return false;
            }
            else { host = text; port = Protocol.DefaultPortFor(host); }
            return host.Length > 0;
        }

        /// <summary>Connects (or re-uses the connection) to host:port. Must be called from the UI thread.</summary>
        public static bool EnsureConnected(string host, int port, MessageHandler onMessage, DisconnectHandler onDisconnect, out string error)
        {
            error = null;
            ServerConnection c = Connection;
            if (c != null && c.IsConnected && string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase) && c.Port == port) return true;
            if (c != null)
            {
                c.MessageReceived -= onMessage;
                c.Disconnected -= onDisconnect;
                c.Close();
            }
            c = new ServerConnection();
            if (!c.Connect(host, port, out error)) return false;
            c.MessageReceived += onMessage;
            c.Disconnected += onDisconnect;
            Connection = c;
            return true;
        }
    }
}
