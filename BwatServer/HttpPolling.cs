/*
 * Bwat Server / HttpPolling.cs
 *
 * A third way to reach the server, for networks that let ordinary web pages through
 * but block WebSockets (some school, office and mobile networks do this).
 * It uses nothing but normal HTTPS requests, exactly like a web page:
 *
 *   POST /poll/open              -> starts a session, answers with a session id
 *   POST /poll/send?sid=...      -> the body holds request lines from the client
 *   GET  /poll/recv?sid=...      -> waits up to 20 s and answers with the lines the server
 *                                   has for the client ("long polling")
 *   POST /poll/close?sid=...     -> ends the session
 *
 * Each session is a PollChannel (an ILineChannel), so the SAME ConnectionHandler code
 * runs for it on its own thread - the rest of the server does not know the difference.
 * A session that has not been polled for 60 seconds is closed (the client has gone).
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Bwat.Server
{
    /// <summary>A "connection" made of HTTP requests: lines wait in two queues.</summary>
    public class PollChannel : ILineChannel
    {
        private readonly Queue<string> incoming = new Queue<string>();   // client -> server
        private readonly Queue<string> outgoing = new Queue<string>();   // server -> client
        private readonly object sync = new object();
        private bool closed;
        private DateTime lastSeen = DateTime.UtcNow;

        public const int ExpirySeconds = 60;
        public string Kind { get { return "HTTP polling"; } }
        public bool IsClosed { get { lock (sync) return closed; } }

        /// <summary>Called by the ConnectionHandler thread: waits for the next request line.</summary>
        public string ReadLine()
        {
            lock (sync)
            {
                while (incoming.Count == 0 && !closed)
                {
                    Monitor.Wait(sync, 5000);
                    if ((DateTime.UtcNow - lastSeen).TotalSeconds > ExpirySeconds) closed = true;   // the client has gone
                }
                if (incoming.Count > 0) return incoming.Dequeue();
                return null;
            }
        }

        /// <summary>Called by the server to send a line: it waits until the client polls.</summary>
        public void WriteLine(string line)
        {
            lock (sync)
            {
                if (closed) throw new IOException("session closed");
                outgoing.Enqueue(line);
                Monitor.PulseAll(sync);
            }
        }

        public void Close()
        {
            lock (sync) { closed = true; Monitor.PulseAll(sync); }
        }

        /// <summary>Lines that arrived in a /send request.</summary>
        public void Push(string[] lines)
        {
            lock (sync)
            {
                lastSeen = DateTime.UtcNow;
                foreach (string l in lines) if (l.Length > 0) incoming.Enqueue(l);
                Monitor.PulseAll(sync);
            }
        }

        /// <summary>A /recv request: waits up to waitMs for lines. Returns null if the session is closed.</summary>
        public List<string> Take(int waitMs)
        {
            lock (sync)
            {
                lastSeen = DateTime.UtcNow;
                DateTime until = DateTime.UtcNow.AddMilliseconds(waitMs);
                while (outgoing.Count == 0 && !closed)
                {
                    int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) break;
                    Monitor.Wait(sync, left);
                }
                lastSeen = DateTime.UtcNow;
                if (outgoing.Count == 0 && closed) return null;
                List<string> lines = new List<string>(outgoing);
                outgoing.Clear();
                return lines;
            }
        }
    }

    /// <summary>Keeps the open polling sessions and answers the /poll/ requests.</summary>
    public static class PollHub
    {
        private static readonly Dictionary<string, PollChannel> sessions = new Dictionary<string, PollChannel>();
        private static readonly object sync = new object();
        private const int MaxSessions = 500;
        private const int RecvWaitMs = 20000;

        /// <summary>Answers one /poll/... request and writes the HTTP response.</summary>
        public static void Handle(string method, string path, string body, Stream stream, string remote)
        {
            string query = "";
            int q = path.IndexOf('?');
            if (q >= 0) { query = path.Substring(q + 1); path = path.Substring(0, q); }
            string sid = QueryValue(query, "sid");
            string action = path.Substring("/poll/".Length).ToLowerInvariant();

            if (action == "open" && method == "POST")
            {
                PollChannel channel = new PollChannel();
                byte[] idBytes = new byte[16];
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(idBytes);
                string id = BitConverter.ToString(idBytes).Replace("-", "").ToLowerInvariant();
                lock (sync)
                {
                    RemoveClosed();
                    if (sessions.Count >= MaxSessions) { Respond(stream, 503, "Too many sessions"); return; }
                    sessions[id] = channel;
                }
                ConnectionHandler handler = new ConnectionHandler(channel, (remote ?? "unknown") + " (via proxy)");
                Thread t = new Thread(new ThreadStart(handler.HandleConnection));
                t.IsBackground = true;
                t.Start();
                Respond(stream, 200, id);
                return;
            }

            PollChannel ch;
            lock (sync) sessions.TryGetValue(sid ?? "", out ch);
            if (ch == null || ch.IsClosed) { Respond(stream, 410, "Session ended"); return; }

            if (action == "send" && method == "POST")
            {
                ch.Push(body.Replace("\r", "").Split('\n'));
                Respond(stream, 200, "ok");
            }
            else if (action == "recv")
            {
                List<string> lines = ch.Take(RecvWaitMs);
                if (lines == null) { Respond(stream, 410, "Session ended"); return; }
                Respond(stream, 200, lines.Count == 0 ? "" : string.Join("\n", lines.ToArray()) + "\n");
            }
            else if (action == "close")
            {
                ch.Close();
                Respond(stream, 200, "closed");
            }
            else Respond(stream, 404, "Unknown request");
        }

        private static void RemoveClosed()
        {
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, PollChannel> kv in sessions) if (kv.Value.IsClosed) dead.Add(kv.Key);
            foreach (string k in dead) sessions.Remove(k);
        }

        private static string QueryValue(string query, string name)
        {
            foreach (string part in query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq) == name) return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }

        private static void Respond(Stream stream, int code, string body)
        {
            string reason = code == 200 ? "OK" : code == 404 ? "Not Found" : code == 410 ? "Gone" : "Service Unavailable";
            byte[] content = Encoding.UTF8.GetBytes(body);
            string head = "HTTP/1.1 " + code + " " + reason + "\r\n" +
                          "Content-Type: text/plain; charset=utf-8\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Content-Length: " + content.Length + "\r\n" +
                          "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            stream.Write(h, 0, h.Length);
            stream.Write(content, 0, content.Length);
            stream.Flush();
        }
    }
}
