/*
 * Bwat Server / NetChannels.cs
 *
 * The server accepts TWO kinds of connection on the SAME port:
 *
 *  1. Plain TCP (like the lecture examples): one text line per message.
 *     Used on a local network (same Wi-Fi / hotspot).
 *
 *  2. WebSocket (RFC 6455): a TCP connection that starts with an HTTP "Upgrade"
 *     request. Render (our cloud host) only lets web traffic in, as HTTPS on
 *     port 443, so over the internet the client connects with wss://.
 *     Each WebSocket text message carries exactly one protocol line.
 *
 * The first line a client sends tells which kind it is: a WebSocket client starts with
 * "GET / HTTP/1.1", a Bwat TCP client starts with a command such as "JOIN|...".
 * An ordinary web request (a browser, or Render's health check) gets a short
 * "server is running" page.
 *
 * Both kinds are hidden behind ILineChannel, so the rest of the server does not care.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Bwat.Server
{
    /// <summary>A connection that sends and receives whole protocol lines.</summary>
    public interface ILineChannel
    {
        string ReadLine();              // null when the connection is closed
        void WriteLine(string line);
        string Kind { get; }
    }

    /// <summary>Reads bytes / lines from a stream with its own buffer (so nothing is lost when we switch to WebSocket).</summary>
    public class NetReader
    {
        private readonly Stream stream;
        private readonly byte[] buffer = new byte[8192];
        private int pos, len;

        public NetReader(Stream stream) { this.stream = stream; }

        public int ReadByte()
        {
            if (pos >= len)
            {
                len = stream.Read(buffer, 0, buffer.Length);
                pos = 0;
                if (len <= 0) { len = 0; return -1; }
            }
            return buffer[pos++];
        }

        public bool ReadExact(byte[] target, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int b = ReadByte();
                if (b < 0) return false;
                target[i] = (byte)b;
            }
            return true;
        }

        /// <summary>Reads one UTF-8 line ending with \n (a \r before it is removed). Returns null at the end of the stream.</summary>
        public string ReadLine(int maxBytes)
        {
            List<byte> bytes = new List<byte>();
            while (true)
            {
                int b = ReadByte();
                if (b < 0) return bytes.Count > 0 ? Encoding.UTF8.GetString(bytes.ToArray()) : null;
                if (b == '\n') break;
                if (bytes.Count < maxBytes) bytes.Add((byte)b);
            }
            if (bytes.Count > 0 && bytes[bytes.Count - 1] == '\r') bytes.RemoveAt(bytes.Count - 1);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }

    /// <summary>Plain TCP: one text line per message (StreamWriter.WriteLine, as in the lecture notes).</summary>
    public class TcpLineChannel : ILineChannel
    {
        private readonly NetReader reader;
        private readonly StreamWriter writer;

        public TcpLineChannel(NetReader reader, Stream stream)
        {
            this.reader = reader;
            writer = new StreamWriter(stream, new UTF8Encoding(false));
        }

        public string Kind { get { return "TCP"; } }
        public string ReadLine() { return reader.ReadLine(16000); }

        public void WriteLine(string line)
        {
            writer.WriteLine(line);
            writer.Flush();
        }
    }

    /// <summary>WebSocket: every text message is one protocol line.</summary>
    public class WebSocketLineChannel : ILineChannel
    {
        private readonly NetReader reader;
        private readonly Stream stream;
        private readonly object writeLock = new object();

        public WebSocketLineChannel(NetReader reader, Stream stream)
        {
            this.reader = reader;
            this.stream = stream;
        }

        public string Kind { get { return "WebSocket"; } }

        public string ReadLine()
        {
            List<byte> message = new List<byte>();
            byte[] head = new byte[2];
            while (true)
            {
                if (!reader.ReadExact(head, 2)) return null;
                bool fin = (head[0] & 0x80) != 0;
                int opcode = head[0] & 0x0F;
                bool masked = (head[1] & 0x80) != 0;
                long length = head[1] & 0x7F;
                if (length == 126)
                {
                    byte[] ext = new byte[2];
                    if (!reader.ReadExact(ext, 2)) return null;
                    length = (ext[0] << 8) | ext[1];
                }
                else if (length == 127)
                {
                    byte[] ext = new byte[8];
                    if (!reader.ReadExact(ext, 8)) return null;
                    length = 0;
                    for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
                }
                if (length > 64000) return null;   // far bigger than any Bwat message: refuse

                byte[] mask = new byte[4];
                if (masked && !reader.ReadExact(mask, 4)) return null;
                byte[] payload = new byte[length];
                if (!reader.ReadExact(payload, (int)length)) return null;
                if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                if (opcode == 0x8) { SendFrame(0x8, new byte[0]); return null; }    // close
                if (opcode == 0x9) { SendFrame(0xA, payload); continue; }           // ping -> pong
                if (opcode == 0xA) continue;                                         // pong
                message.AddRange(payload);                                          // text (1) or continuation (0)
                if (fin) return Encoding.UTF8.GetString(message.ToArray()).TrimEnd('\r', '\n');
            }
        }

        public void WriteLine(string line)
        {
            SendFrame(0x1, Encoding.UTF8.GetBytes(line));
        }

        /// <summary>Server frames are never masked (RFC 6455).</summary>
        private void SendFrame(int opcode, byte[] payload)
        {
            using (MemoryStream frame = new MemoryStream())
            {
                frame.WriteByte((byte)(0x80 | opcode));
                if (payload.Length < 126) frame.WriteByte((byte)payload.Length);
                else if (payload.Length <= 0xFFFF)
                {
                    frame.WriteByte(126);
                    frame.WriteByte((byte)(payload.Length >> 8));
                    frame.WriteByte((byte)payload.Length);
                }
                else
                {
                    frame.WriteByte(127);
                    for (int i = 7; i >= 0; i--) frame.WriteByte((byte)((long)payload.Length >> (8 * i)));
                }
                frame.Write(payload, 0, payload.Length);
                byte[] bytes = frame.ToArray();
                lock (writeLock)
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
            }
        }
    }

    /// <summary>Handles a connection that starts with an HTTP request line.</summary>
    public static class HttpGate
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        /// <summary>
        /// Reads the HTTP headers. For a WebSocket upgrade it answers "101 Switching Protocols" and returns a
        /// WebSocket channel. For any other web request it answers with a short page and returns null.
        /// forwardedFor is the real client address when the server runs behind a proxy (e.g. Render).
        /// </summary>
        public static ILineChannel Accept(string requestLine, NetReader reader, Stream stream, out string forwardedFor)
        {
            forwardedFor = null;
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 100; i++)
            {
                string h = reader.ReadLine(8000);
                if (h == null || h.Length == 0) break;
                int colon = h.IndexOf(':');
                if (colon > 0) headers[h.Substring(0, colon).Trim()] = h.Substring(colon + 1).Trim();
            }
            string value;
            if (headers.TryGetValue("X-Forwarded-For", out value)) forwardedFor = value.Split(',')[0].Trim();

            // Bwat "HTTP polling" requests (for networks that block WebSockets) - see HttpPolling.cs
            string[] parts = requestLine.Split(' ');
            string method = parts[0];
            string path = parts.Length > 1 ? parts[1] : "/";
            if (path.StartsWith("/poll/"))
            {
                string postBody = "";
                string lengthText;
                int length;
                if (headers.TryGetValue("Content-Length", out lengthText) && int.TryParse(lengthText, out length) && length > 0)
                {
                    if (length > 64000) length = 64000;
                    byte[] data = new byte[length];
                    if (!reader.ReadExact(data, length)) return null;
                    postBody = Encoding.UTF8.GetString(data);
                }
                PollHub.Handle(method, path, postBody, stream, forwardedFor);
                return null;
            }

            string upgrade, key;
            if (headers.TryGetValue("Upgrade", out upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase) &&
                headers.TryGetValue("Sec-WebSocket-Key", out key))
            {
                string accept;
                using (SHA1 sha = SHA1.Create())
                    accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
                Write(stream, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                              "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
                return new WebSocketLineChannel(reader, stream);
            }

            // An ordinary web request (a browser or Render's health check).
            string body = "Bwat server " + Bwat.Common.Protocol.Version + " is running. Open the Bwat app to play.\n";
            Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " +
                          Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body);
            return null;
        }

        private static void Write(Stream stream, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }
}
