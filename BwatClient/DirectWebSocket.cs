/*
 * Bwat Client / DirectWebSocket.cs
 *
 * A small WebSocket client written by hand on top of TcpClient (+ SslStream for wss://).
 *
 * Why: the built-in ClientWebSocket goes through Windows' web settings (system proxy,
 * IPv6 first, ...). On some networks that fails with "Unable to connect to the remote
 * server" even though the internet works. This class connects DIRECTLY:
 *   - looks up the server's addresses and tries IPv4 first, then IPv6,
 *   - opens a TCP connection to port 443 (no proxy),
 *   - starts TLS 1.2 (SslStream) and sends the WebSocket "Upgrade" request,
 *   - then sends/receives text messages (client frames are masked, as RFC 6455 requires).
 * ServerConnection uses it when ClientWebSocket fails.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace Bwat.Client
{
    public class DirectWebSocket
    {
        private TcpClient tcp;
        private Stream stream;
        private readonly object writeLock = new object();
        private readonly Random random = new Random();

        /// <summary>Connects and does the WebSocket handshake. Throws an exception with a clear message on failure.</summary>
        public void Connect(string host, int port, bool useTls, int waitSeconds)
        {
            Connect(host, port, useTls, waitSeconds, null);
        }

        /// <summary>knownAddresses: IP addresses found without Windows' DNS (see NetHelpers.cs), or null.</summary>
        public void Connect(string host, int port, bool useTls, int waitSeconds, IPAddress[] knownAddresses)
        {
            IPAddress[] addresses = knownAddresses;
            if (addresses == null)
            {
                try { addresses = Dns.GetHostAddresses(host); }
                catch (Exception ex) { throw new IOException("the name " + host + " could not be found (DNS: " + ex.Message + ")"); }
            }

            // IPv4 first: broken IPv6 is a common reason for "cannot connect"
            List<IPAddress> ordered = new List<IPAddress>();
            foreach (IPAddress a in addresses) if (a.AddressFamily == AddressFamily.InterNetwork) ordered.Add(a);
            foreach (IPAddress a in addresses) if (a.AddressFamily != AddressFamily.InterNetwork) ordered.Add(a);
            if (ordered.Count == 0) throw new IOException("the name " + host + " has no address");

            stream = RawHttp.Open(ordered.ToArray(), host, port, useTls, waitSeconds * 1000, out tcp);

            // ---- WebSocket handshake ----
            byte[] keyBytes = new byte[16];
            random.NextBytes(keyBytes);
            string key = Convert.ToBase64String(keyBytes);
            string request = "GET / HTTP/1.1\r\n" +
                             "Host: " + host + "\r\n" +
                             "Upgrade: websocket\r\n" +
                             "Connection: Upgrade\r\n" +
                             "Sec-WebSocket-Key: " + key + "\r\n" +
                             "Sec-WebSocket-Version: 13\r\n" +
                             "User-Agent: BwatClient\r\n\r\n";
            byte[] req = Encoding.ASCII.GetBytes(request);
            stream.Write(req, 0, req.Length);
            stream.Flush();

            // A sleeping free server can take up to a minute to answer.
            tcp.ReceiveTimeout = waitSeconds * 1000;
            string status = ReadHeaderLine();
            if (status == null) throw new IOException("the server closed the connection during the handshake");
            string expected = Convert.ToBase64String(SHA1.Create().ComputeHash(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            bool acceptOk = false;
            string line;
            while ((line = ReadHeaderLine()) != null && line.Length > 0)
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals("Sec-WebSocket-Accept", StringComparison.OrdinalIgnoreCase))
                    acceptOk = line.Substring(colon + 1).Trim() == expected;
            }
            if (!status.Contains(" 101")) throw new IOException("the server answered \"" + status + "\" instead of accepting the connection");
            if (!acceptOk) throw new IOException("the server's handshake answer was wrong");
            tcp.ReceiveTimeout = 0;   // from now on wait as long as needed (PINGs keep it alive)
        }

        private string ReadHeaderLine()
        {
            List<byte> bytes = new List<byte>();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return bytes.Count > 0 ? Encoding.ASCII.GetString(bytes.ToArray()) : null;
                if (b == '\n') break;
                if (b != '\r' && bytes.Count < 4096) bytes.Add((byte)b);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        /// <summary>Sends one text message (masked frame).</summary>
        public void SendText(string text)
        {
            SendFrame(0x1, Encoding.UTF8.GetBytes(text));
        }

        private void SendFrame(int opcode, byte[] payload)
        {
            List<byte> frame = new List<byte>();
            frame.Add((byte)(0x80 | opcode));
            if (payload.Length < 126) frame.Add((byte)(0x80 | payload.Length));
            else if (payload.Length < 65536)
            {
                frame.Add(0x80 | 126);
                frame.Add((byte)(payload.Length >> 8));
                frame.Add((byte)payload.Length);
            }
            else
            {
                frame.Add(0x80 | 127);
                for (int i = 7; i >= 0; i--) frame.Add((byte)((long)payload.Length >> (8 * i)));
            }
            byte[] mask = new byte[4];
            lock (random) random.NextBytes(mask);
            frame.AddRange(mask);
            for (int i = 0; i < payload.Length; i++) frame.Add((byte)(payload[i] ^ mask[i % 4]));
            byte[] bytes = frame.ToArray();
            lock (writeLock)
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }

        /// <summary>Reads the next complete text message. Returns null when the connection is closed.</summary>
        public string ReadText()
        {
            MemoryStream message = new MemoryStream();
            while (true)
            {
                byte[] head = ReadExact(2);
                if (head == null) return null;
                bool fin = (head[0] & 0x80) != 0;
                int opcode = head[0] & 0x0F;
                bool masked = (head[1] & 0x80) != 0;
                long length = head[1] & 0x7F;
                if (length == 126) { byte[] l = ReadExact(2); if (l == null) return null; length = (l[0] << 8) | l[1]; }
                else if (length == 127)
                {
                    byte[] l = ReadExact(8); if (l == null) return null;
                    length = 0;
                    for (int i = 0; i < 8; i++) length = (length << 8) | l[i];
                }
                if (length > 16 * 1024 * 1024) return null;
                byte[] mask = masked ? ReadExact(4) : null;
                if (masked && mask == null) return null;
                byte[] payload = ReadExact((int)length);
                if (payload == null) return null;
                if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                if (opcode == 0x8) return null;                                  // close
                if (opcode == 0x9) { SendFrame(0xA, payload); continue; }        // ping -> pong
                if (opcode == 0xA) continue;                                     // pong
                message.Write(payload, 0, payload.Length);                       // text or continuation
                if (fin) return Encoding.UTF8.GetString(message.ToArray()).TrimEnd('\r', '\n');
            }
        }

        private byte[] ReadExact(int count)
        {
            byte[] buffer = new byte[count];
            int done = 0;
            while (done < count)
            {
                int n = stream.Read(buffer, done, count - done);
                if (n <= 0) return null;
                done += n;
            }
            return buffer;
        }

        public void Close()
        {
            try { if (tcp != null) tcp.Close(); } catch { }
        }

        /// <summary>The innermost error message (the real cause).</summary>
        public static string Deepest(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }
    }
}
