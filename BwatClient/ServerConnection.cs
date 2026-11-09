/*
 * Bwat Client / ServerConnection.cs
 *
 * The client's ONLY link to the system: one connection to the server.
 * (The client never touches the database - it only sends requests.)
 *
 * Two ways to connect, with exactly the same messages:
 *   - Plain TCP (TcpClient + StreamReader/StreamWriter, as in the lecture notes)
 *     for a server on the local network, e.g.  192.168.1.20:8765
 *   - Secure WebSocket (wss://) for the server on the internet (Render),
 *     e.g.  bwat-abc.onrender.com:443  - Render only lets web traffic in.
 *     Each WebSocket text message carries one protocol line.
 *
 * - Send(...) sends one request line.
 * - A background thread reads every line the server sends. The server can send
 *   messages at any time (e.g. "a new question!"), so we cannot just wait for one
 *   reply after each request.
 * - Windows Forms controls may only be changed by the UI thread. So every received
 *   message is handed over to the UI thread with SynchronizationContext.Post().
 * - Lists arrive as BEGIN|type, ROW|type|..., END|type and are collected here
 *   and delivered as one ListReceived event.
 * - A PING every 25 seconds keeps the connection alive through proxies.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Bwat.Common;

namespace Bwat.Client
{
    public delegate void MessageHandler(Message message);
    public delegate void ListHandler(string listType, List<string[]> rows);
    public delegate void DisconnectHandler(string reason);

    public class ServerConnection
    {
        // plain TCP
        private TcpClient client;
        private StreamReader reader;
        private StreamWriter writer;
        // WebSocket (built-in ClientWebSocket, or our own DirectWebSocket as a fallback)
        private ClientWebSocket socket;
        private DirectWebSocket direct;
        private HttpPollTransport poll;

        private Thread readThread;
        private System.Threading.Timer keepAlive;
        private readonly object writeLock = new object();
        private SynchronizationContext uiContext;
        private volatile bool closing;
        private volatile bool connected;
        private bool disconnectRaised;
        private readonly Dictionary<string, List<string[]>> pendingLists = new Dictionary<string, List<string[]>>();

        public event MessageHandler MessageReceived;
        public event ListHandler ListReceived;
        public event DisconnectHandler Disconnected;

        public string Host { get; private set; }
        public int Port { get; private set; }
        public bool UsesWebSocket { get { return socket != null || direct != null || poll != null; } }

        public bool IsConnected
        {
            get { return connected && !closing && !disconnectRaised; }
        }

        /// <summary>Connects to the server. Must be called from the UI thread. Returns false and an error text on failure.</summary>
        public bool Connect(string host, int port, out string error)
        {
            uiContext = SynchronizationContext.Current;
            Host = host;
            Port = port;
            if (port == Protocol.SecurePort)
                return ConnectWebSocket(new Uri("wss://" + host + "/"), out error);
            return ConnectTcp(host, port, out error);
        }

        private bool ConnectTcp(string host, int port, out string error)
        {
            error = null;
            try
            {
                client = new TcpClient();
                IAsyncResult result = client.BeginConnect(host, port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(5000))
                {
                    client.Close();
                    error = "The server at " + host + ":" + port + " did not answer. Check the address and that the server is running.";
                    return false;
                }
                client.EndConnect(result);
                NetworkStream stream = client.GetStream();
                reader = new StreamReader(stream, Encoding.UTF8);
                writer = new StreamWriter(stream, new UTF8Encoding(false));
                StartReading();
                return true;
            }
            catch (SocketException ex)
            {
                error = "Cannot connect to " + host + ":" + port + ". Is the server running? (" + ex.Message + ")";
                return false;
            }
            catch (Exception ex)
            {
                error = "Connection error: " + ex.Message;
                return false;
            }
        }

        // The way that worked last time (0 = WebSocket, 1 = direct WebSocket, 2 = HTTP polling) is tried first next time.
        private static int preferredWay = 0;

        /// <summary>
        /// Connects to the online server. Some networks block parts of the internet, so up to three ways are tried:
        ///   0. WebSocket (ClientWebSocket)                       - the normal way
        ///   1. our own direct WebSocket (DirectWebSocket.cs)     - when Windows' proxy settings or IPv6 get in the way
        ///   2. plain HTTPS requests (HttpPollTransport.cs)       - when the network blocks WebSockets but allows web pages
        /// Public so it can also be tested with ws://.
        /// </summary>
        public bool ConnectWebSocket(Uri uri, out string error)
        {
            error = null;
            if (uiContext == null) uiContext = SynchronizationContext.Current;
            if (Host == null) { Host = uri.Host; Port = uri.Port; }
            bool tls = uri.Scheme == "wss";
            // Render requires TLS 1.2 (older .NET versions do not switch it on by themselves).
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;

            // Can Windows find the server's address (DNS)? On some networks it cannot, although the browser
            // can (browsers use their own "secure DNS"). Then we look the address up ourselves (NetHelpers.cs).
            IPAddress[] addresses = null;
            IPAddress literal;
            if (!IPAddress.TryParse(uri.Host, out literal))
            {
                try { Dns.GetHostAddresses(uri.Host); }
                catch (Exception)
                {
                    // If this fails too, carry on: behind a proxy the proxy looks up the name, not Windows.
                    try { addresses = DnsFallback.Resolve(uri.Host); }
                    catch (Exception) { addresses = null; }
                }
            }

            // First open the server's web page, exactly like a browser. This also wakes up a sleeping free server
            // (up to a minute), so the tries below can be short.
            try
            {
                if (addresses == null) WakeUp(uri, tls);
                else RawHttp.Request(addresses, uri.Host, uri.Port, tls, "GET", "/", null, 75000, null);
            }
            catch (Exception ex)
            {
                error = "This computer cannot reach " + uri.Host + ".\nReason: " + DirectWebSocket.Deepest(ex) +
                        "\nCheck the address and the internet connection.";
                return false;
            }

            List<string> problems = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                int way = (preferredWay + i) % 3;
                if (way == 0 && addresses != null) continue;   // the built-in WebSocket needs Windows' DNS
                try
                {
                    if (way == 0)
                    {
                        socket = new ClientWebSocket();
                        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                        using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                            socket.ConnectAsync(uri, timeout.Token).Wait();
                    }
                    else if (way == 1)
                    {
                        DirectWebSocket d = new DirectWebSocket();
                        try { d.Connect(uri.Host, uri.Port, tls, 15, addresses); }
                        catch { d.Close(); throw; }
                        direct = d;
                    }
                    else
                    {
                        HttpPollTransport p = new HttpPollTransport();
                        p.Open(uri.Host, uri.Port, tls, 30, addresses);
                        poll = p;
                    }
                    preferredWay = way;
                    StartReading();
                    return true;
                }
                catch (Exception ex)
                {
                    try { if (socket != null) socket.Abort(); } catch { }
                    socket = null;
                    direct = null;
                    poll = null;
                    string[] names = { "WebSocket", "direct", "web requests" };
                    problems.Add(names[way] + ": " + DirectWebSocket.Deepest(ex));
                }
            }
            error = "Cannot connect to " + uri.Host + " (the web page works, but the app's connection is blocked).\n" +
                    string.Join("\n", problems.ToArray());
            return false;
        }

        private static void WakeUp(Uri uri, bool tls)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create((tls ? "https://" : "http://") + uri.Host +
                                                                 (uri.IsDefaultPort ? "" : ":" + uri.Port) + "/");
            r.Timeout = 75000;
            r.ReadWriteTimeout = 75000;
            r.UserAgent = "BwatClient";
            using (WebResponse resp = r.GetResponse()) { }
        }

        private void StartReading()
        {
            connected = true;
            readThread = new Thread(new ThreadStart(ReadLoop));
            readThread.IsBackground = true;
            readThread.Name = "Server reader";
            readThread.Start();
            keepAlive = new System.Threading.Timer(delegate { Send(Cmd.Ping); }, null, 25000, 25000);
        }

        /// <summary>Sends one request. Returns false if the connection is broken.</summary>
        public bool Send(string command, params object[] fields)
        {
            if (!connected || closing) return false;
            string line = Protocol.Build(command, fields);
            try
            {
                lock (writeLock)
                {
                    if (poll != null) poll.SendLine(line);
                    else if (direct != null) direct.SendText(line);
                    else if (socket != null)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(line);
                        socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
                    }
                    else
                    {
                        writer.WriteLine(line);
                        writer.Flush();
                    }
                }
                return true;
            }
            catch (Exception)
            {
                RaiseDisconnected("Lost the connection to the server.");
                return false;
            }
        }

        public void Close()
        {
            closing = true;
            connected = false;
            try { if (keepAlive != null) keepAlive.Dispose(); } catch { }
            try { if (client != null) client.Close(); } catch { }
            try { if (socket != null) socket.Abort(); } catch { }
            try { if (direct != null) direct.Close(); } catch { }
            try { if (poll != null) poll.Close(); } catch { }
        }

        // ------------------------------------------------------------------

        private void ReadLoop()
        {
            try
            {
                string line;
                while ((line = ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    Dispatch(Protocol.Parse(line));
                }
            }
            catch (Exception) { /* connection closed or broken */ }

            connected = false;
            try { if (keepAlive != null) keepAlive.Dispose(); } catch { }
            if (!closing)
                RaiseDisconnected("The server closed the connection (server stopped or network problem).");
        }

        /// <summary>Reads the next line: one text line (TCP) or one complete text message (WebSocket).</summary>
        private string ReadLine()
        {
            if (poll != null) return poll.ReadLine();
            if (direct != null) return direct.ReadText();
            if (socket == null) return reader.ReadLine();

            byte[] buffer = new byte[8192];
            using (MemoryStream message = new MemoryStream())
            {
                while (true)
                {
                    WebSocketReceiveResult r = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).Result;
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                    message.Write(buffer, 0, r.Count);
                    if (r.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray()).TrimEnd('\r', '\n');
                }
            }
        }

        private void Dispatch(Message m)
        {
            if (m.Command == Cmd.Ok && m.Get(0) == Cmd.Ping) return;   // keep-alive answer, nothing to show

            if (m.Command == Cmd.ListBegin)
            {
                pendingLists[m.Get(0)] = new List<string[]>();
            }
            else if (m.Command == Cmd.ListRow)
            {
                List<string[]> rows;
                if (pendingLists.TryGetValue(m.Get(0), out rows))
                {
                    string[] row = new string[m.Count - 1];
                    Array.Copy(m.Fields, 1, row, 0, row.Length);
                    rows.Add(row);
                }
            }
            else if (m.Command == Cmd.ListEnd)
            {
                string type = m.Get(0);
                List<string[]> rows;
                if (!pendingLists.TryGetValue(type, out rows)) rows = new List<string[]>();
                pendingLists.Remove(type);
                Post(delegate
                {
                    ListHandler h = ListReceived;
                    if (h != null) h(type, rows);
                });
            }
            else
            {
                Post(delegate
                {
                    MessageHandler h = MessageReceived;
                    if (h != null) h(m);
                });
            }
        }

        private void RaiseDisconnected(string reason)
        {
            Post(delegate
            {
                if (disconnectRaised || closing) return;
                disconnectRaised = true;
                DisconnectHandler h = Disconnected;
                if (h != null) h(reason);
            });
        }

        /// <summary>Runs code on the UI thread.</summary>
        private void Post(Action action)
        {
            if (uiContext != null) uiContext.Post(delegate { action(); }, null);
            else action();
        }
    }
}
