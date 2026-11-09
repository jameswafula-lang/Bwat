/*
 * Bwat Server / Program.cs
 *
 * CONCURRENT SERVER (master / slave model from the lecture notes).
 *
 *   MASTER (this Main method)                SLAVES (ConnectionHandler threads)
 *   -------------------------                ----------------------------------
 *   create socket, Bind, Listen              one per connected client:
 *   loop forever:                              read request
 *      Accept() a client          ------->     process it (database, game rules)
 *      create a ConnectionHandler              send response
 *      start a new Thread for it               ... until the client disconnects
 *
 * Usage:  BwatServer.exe [port] [address-for-links]
 *         e.g. BwatServer.exe 8765 192.168.1.20          (players on the same Wi-Fi / VPN)
 *              BwatServer.exe 8765 abc.playit.gg:41234   (players on the internet through a tunnel)
 *         Default: port 8765 and this computer's network address.
 *         The link address can also be saved in link_address.txt next to BwatServer.exe.
 *
 * On Render (cloud) everything comes from environment variables instead:
 *   PORT                      the port to listen on (set by Render)
 *   RENDER_EXTERNAL_HOSTNAME  the public address, e.g. bwat-abc.onrender.com (set by Render)
 *   BWAT_ADMIN_USER / BWAT_ADMIN_PASSWORD   the first admin account (you set these, secretly)
 *   BWAT_DB_PATH              optional database file location (e.g. on a persistent disk)
 */
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Bwat.Common;

namespace Bwat.Server
{
    class Program
    {
        static void Main(string[] args)
        {
            try { Console.Title = "Bwat Server"; Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (Exception) { }

            // Port: command line, or the PORT environment variable (set by Render), or 8765.
            int port = Protocol.DefaultPort;
            string envPort = Environment.GetEnvironmentVariable("PORT");
            if (args.Length > 0)
            {
                if (!int.TryParse(args[0], out port))
                {
                    Console.WriteLine("Usage: BwatServer.exe [port] [address-for-links]");
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(envPort)) int.TryParse(envPort, out port);

            ServerLog.Good("=== Bwat Server " + Protocol.Version + " - Educational Battle Quiz ===");

            // 1. Database
            try
            {
                string dbPath = Environment.GetEnvironmentVariable("BWAT_DB_PATH");
                if (string.IsNullOrEmpty(dbPath)) dbPath = "bwat.db";
                Database.Initialize(dbPath);
                ServerLog.Good("[DB] Database ready (" + dbPath + ").");
            }
            catch (Exception ex)
            {
                ServerLog.Error("[DB] The database could not be opened: " + ex.Message);
                ServerLog.Error("     Check that System.Data.SQLite.dll and the x86/x64 SQLite.Interop.dll folders are next to BwatServer.exe.");
                Console.WriteLine("Press Enter to exit.");
                Console.ReadLine();
                return;
            }

            // 2. Make sure an admin exists (no password is written in the code).
            EnsureAdminAccount();

            // 3. Re-open rooms that were waiting when the server last stopped.
            RoomManager.LoadWaitingRooms();

            // 4. Create the TCP server socket and listen (master).
            Socket server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, port);
                server.Bind(endpoint);
                server.Listen(50);
            }
            catch (SocketException ex)
            {
                ServerLog.Error("Cannot listen on port " + port + ": " + ex.Message);
                ServerLog.Error("Is another server already running? Try: BwatServer.exe 8766");
                Console.WriteLine("Press Enter to exit.");
                Console.ReadLine();
                return;
            }

            ServerLog.Good("Server in listen mode on port " + port + ".");
            RoomManager.Port = port;
            RoomManager.PublicHost = ShowAddresses(port);

            // The address put into room links. Players on other networks (internet) need the PUBLIC
            // address of a tunnel or cloud server. It can be given on the command line or saved in
            // the file link_address.txt next to BwatServer.exe (e.g. abc.ply.gg:41234).
            // Where it comes from (first one found): command line, BWAT_LINK_ADDRESS, Render's own
            // address (RENDER_EXTERNAL_HOSTNAME, players connect securely on port 443), link_address.txt.
            string source = "command line";
            string address = args.Length > 1 ? args[1].Trim() : "";
            if (address.Length == 0) { address = (Environment.GetEnvironmentVariable("BWAT_LINK_ADDRESS") ?? "").Trim(); source = "BWAT_LINK_ADDRESS"; }
            if (address.Length == 0)
            {
                string render = (Environment.GetEnvironmentVariable("RENDER_EXTERNAL_HOSTNAME") ?? "").Trim();
                if (render.Length > 0) { address = render + ":443"; source = "Render"; }
            }
            if (address.Length == 0) { address = ReadLinkAddressFile(); source = "link_address.txt"; }
            if (address.Length > 0)
            {
                ServerLog.Info("Using the link address " + address + " (" + source + ")");
                int colon = address.LastIndexOf(':');
                int linkPort;
                if (colon > 0 && int.TryParse(address.Substring(colon + 1), out linkPort))
                {
                    RoomManager.PublicHost = address.Substring(0, colon);
                    RoomManager.Port = linkPort;
                }
                else RoomManager.PublicHost = address;
            }
            ServerLog.Good("Room links will use: " + Protocol.MakeLink(RoomManager.PublicHost, RoomManager.Port, "BWAT-xxxx"));
            if (address.Length == 0)
                ServerLog.Warn("LOCAL server: these links only work on this network (same Wi-Fi / hotspot).\n" +
                               "          For players on other networks, use the ONLINE server on Render instead -\n" +
                               "          log in with  bwat-xxxx.onrender.com  (see README, section 6).");

            while (true)
            {
                try
                {
                    Socket client = server.Accept();
                    ConnectionHandler handler = new ConnectionHandler(client);
                    Thread thread = new Thread(new ThreadStart(handler.HandleConnection));
                    thread.IsBackground = true;
                    thread.Start();
                }
                catch (Exception ex)
                {
                    ServerLog.Error("Connection failed: " + ex.Message);
                }
            }
        }

        /// <summary>Prints the IP addresses that clients can use to connect (lecture: Internet Basics / Dns class).</summary>
        /// <summary>Reads link_address.txt (first non-empty line that is not a # comment). Returns "" if there is none.</summary>
        static string ReadLinkAddressFile()
        {
            try
            {
                string file = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "link_address.txt");
                if (!System.IO.File.Exists(file)) return "";
                foreach (string line in System.IO.File.ReadAllLines(file))
                {
                    string t = line.Trim();
                    if (t.Length > 0 && !t.StartsWith("#")) return t;
                }
            }
            catch (Exception ex) { ServerLog.Warn("Could not read link_address.txt: " + ex.Message); }
            return "";
        }

        /// <summary>Prints the IP addresses clients can use and returns the first network address (for room links).</summary>
        static string ShowAddresses(int port)
        {
            string first = null;
            try
            {
                string hostname = Dns.GetHostName();
                ServerLog.Info("Computer name: " + hostname + ". Clients on the same network can connect to:");
                foreach (IPAddress address in Dns.GetHostEntry(hostname).AddressList)
                {
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                    ServerLog.Info("    " + address + ":" + port);
                    if (first == null) first = address.ToString();
                }
                ServerLog.Info("    127.0.0.1:" + port + "   (clients on this same computer)");
            }
            catch (Exception ex)
            {
                ServerLog.Warn("Could not list IP addresses: " + ex.Message);
            }
            return first ?? "127.0.0.1";
        }

        /// <summary>On the very first start there is no admin yet, so the server asks for one in the console.</summary>
        static void EnsureAdminAccount()
        {
            if (Database.CountActiveAdmins() > 0) return;

            // On a cloud host there is no keyboard: the first admin comes from environment variables
            // (set secretly in the host's dashboard - never written in the code).
            string envUser = (Environment.GetEnvironmentVariable("BWAT_ADMIN_USER") ?? "").Trim();
            string envPass = Environment.GetEnvironmentVariable("BWAT_ADMIN_PASSWORD") ?? "";
            if (envUser.Length > 0 && envPass.Length >= 6)
            {
                if (Database.CreateAdmin(envUser, "Admin " + envUser, envPass) > 0)
                {
                    Database.Audit(envUser, "ADMIN_CREATED", "First admin created from BWAT_ADMIN_USER / BWAT_ADMIN_PASSWORD");
                    ServerLog.Good("Admin '" + envUser + "' created from the environment variables.");
                }
                return;
            }
            if (Console.IsInputRedirected)
            {
                ServerLog.Warn("No admin account exists. Set BWAT_ADMIN_USER and BWAT_ADMIN_PASSWORD (6+ characters) and restart.");
                return;
            }

            Console.WriteLine();
            ServerLog.Warn("No admin account exists yet. Create the first admin now.");
            string username;
            while (true)
            {
                Console.Write("Admin username: ");
                username = (Console.ReadLine() ?? "").Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(username, "^[A-Za-z0-9_]{3,20}$")) break;
                Console.WriteLine("Use 3-20 letters, digits or _.");
            }
            string password;
            while (true)
            {
                Console.Write("Admin password (min 6 characters): ");
                password = ReadPassword();
                if (password.Length >= 6) break;
                Console.WriteLine("Too short.");
            }
            int id = Database.CreateAdmin(username, "Admin " + username, password);
            if (id < 0)
            {
                ServerLog.Error("That admin username already exists (deactivated). Choose another name.");
                EnsureAdminAccount();
                return;
            }
            Database.Audit(username, "ADMIN_CREATED", "First admin created at the server console");
            ServerLog.Good("Admin '" + username + "' created.");
            Console.WriteLine();
        }

        /// <summary>Reads a password without showing it (shows * instead).</summary>
        static string ReadPassword()
        {
            if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    Console.Write("*");
                }
            }
            return sb.ToString();
        }
    }
}
