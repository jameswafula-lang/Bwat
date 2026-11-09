/*
 * Bwat Client / NetHelpers.cs
 *
 * Help for networks where Windows cannot look up the server's name (DNS).
 * Web browsers often still work there, because they use their own "secure DNS".
 *
 * DnsFallback  - finds the server's IP address without Windows' DNS:
 *                1. asks the public DNS servers 8.8.8.8 (Google) and 1.1.1.1 (Cloudflare) directly (UDP port 53),
 *                2. if that is blocked too, asks Cloudflare over HTTPS (DNS-over-HTTPS), like a browser does.
 * RawHttp      - one HTTPS request to a known IP address (TcpClient + SslStream), so the
 *                secure connection is still checked against the real server name.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace Bwat.Client
{
    public static class DnsFallback
    {
        private static readonly string[] PublicDns = { "8.8.8.8", "1.1.1.1", "8.8.4.4", "1.0.0.1" };

        /// <summary>Returns the IPv4 addresses of host without using Windows' DNS. Throws if nothing works.</summary>
        public static IPAddress[] Resolve(string host)
        {
            List<string> problems = new List<string>();
            foreach (string server in PublicDns)
            {
                try
                {
                    IPAddress[] a = QueryUdp(server, host, 3000);
                    if (a.Length > 0) return a;
                    problems.Add(server + ": no address");
                }
                catch (Exception ex) { problems.Add(server + ": " + ex.Message); }
            }
            try
            {
                IPAddress[] a = QueryHttps(host);
                if (a.Length > 0) return a;
                problems.Add("secure DNS: no address");
            }
            catch (Exception ex) { problems.Add("secure DNS: " + DirectWebSocket.Deepest(ex)); }
            throw new IOException("the name " + host + " could not be found (" + string.Join("; ", problems.ToArray()) + ")");
        }

        /// <summary>A plain DNS question ("A" record) sent straight to a public DNS server.</summary>
        public static IPAddress[] QueryUdp(string dnsServer, string host, int timeoutMs)
        {
            List<byte> q = new List<byte>();
            Random rnd = new Random();
            q.Add((byte)rnd.Next(256)); q.Add((byte)rnd.Next(256));   // id
            q.Add(0x01); q.Add(0x00);                                   // standard query, recursion desired
            q.Add(0); q.Add(1); q.Add(0); q.Add(0); q.Add(0); q.Add(0); q.Add(0); q.Add(0);
            foreach (string label in host.TrimEnd('.').Split('.'))
            {
                byte[] b = Encoding.ASCII.GetBytes(label);
                q.Add((byte)b.Length);
                q.AddRange(b);
            }
            q.Add(0);
            q.Add(0); q.Add(1);   // type A
            q.Add(0); q.Add(1);   // class IN
            byte[] packet = q.ToArray();

            using (UdpClient udp = new UdpClient())
            {
                udp.Client.ReceiveTimeout = timeoutMs;
                udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Parse(dnsServer), 53));
                IPEndPoint from = null;
                byte[] r = udp.Receive(ref from);
                if (r.Length < 12 || r[0] != packet[0] || r[1] != packet[1]) throw new IOException("bad DNS answer");
                int answers = (r[6] << 8) | r[7];
                int pos = 12;
                pos = SkipName(r, pos) + 4;                         // the question
                List<IPAddress> result = new List<IPAddress>();
                for (int i = 0; i < answers && pos < r.Length; i++)
                {
                    pos = SkipName(r, pos);
                    int type = (r[pos] << 8) | r[pos + 1];
                    int length = (r[pos + 8] << 8) | r[pos + 9];
                    pos += 10;
                    if (type == 1 && length == 4)
                        result.Add(new IPAddress(new byte[] { r[pos], r[pos + 1], r[pos + 2], r[pos + 3] }));
                    pos += length;
                }
                return result.ToArray();
            }
        }

        private static int SkipName(byte[] r, int pos)
        {
            while (pos < r.Length)
            {
                int len = r[pos];
                if (len == 0) return pos + 1;
                if ((len & 0xC0) == 0xC0) return pos + 2;   // compressed name (pointer)
                pos += len + 1;
            }
            return pos;
        }

        /// <summary>DNS-over-HTTPS to Cloudflare, addressed by IP so that no DNS is needed for it.</summary>
        public static IPAddress[] QueryHttps(string host)
        {
            string json = RawHttp.Request(new IPAddress[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.0.0.1") },
                                          "cloudflare-dns.com", 443, true, "GET",
                                          "/dns-query?name=" + Uri.EscapeDataString(host) + "&type=A",
                                          null, 8000, "Accept: application/dns-json\r\n").Body;
            List<IPAddress> result = new List<IPAddress>();
            foreach (Match m in Regex.Matches(json, "\"type\"\\s*:\\s*1\\s*,[^}]*?\"data\"\\s*:\\s*\"([0-9.]+)\""))
            {
                IPAddress a;
                if (IPAddress.TryParse(m.Groups[1].Value, out a)) result.Add(a);
            }
            return result.ToArray();
        }
    }

    /// <summary>The answer to a RawHttp request.</summary>
    public class RawResponse
    {
        public int Status;
        public string Body;
    }

    /// <summary>Thrown when the server answers with an error status (e.g. 410 = session ended).</summary>
    public class HttpStatusException : IOException
    {
        public readonly int Status;
        public HttpStatusException(int status) : base("the server answered HTTP " + status) { Status = status; }
    }

    public static class RawHttp
    {
        /// <summary>Opens a TCP (+TLS) connection to one of the addresses. TLS is checked against the real host name.</summary>
        public static Stream Open(IPAddress[] addresses, string host, int port, bool tls, int timeoutMs, out TcpClient tcp)
        {
            tcp = null;
            string lastError = "";
            foreach (IPAddress address in addresses)
            {
                TcpClient c = new TcpClient(address.AddressFamily);
                try
                {
                    IAsyncResult r = c.BeginConnect(address, port, null, null);
                    if (!r.AsyncWaitHandle.WaitOne(6000)) { c.Close(); lastError = address + ": no answer (blocked?)"; continue; }
                    c.EndConnect(r);
                    tcp = c;
                    break;
                }
                catch (Exception ex) { c.Close(); lastError = address + ": " + ex.Message; }
            }
            if (tcp == null) throw new IOException("TCP connection to port " + port + " failed (" + lastError + ")");
            tcp.NoDelay = true;
            tcp.ReceiveTimeout = timeoutMs;
            tcp.SendTimeout = timeoutMs;
            Stream stream = tcp.GetStream();
            if (!tls) return stream;
            SslStream ssl = new SslStream(stream, false);
            try { ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false); }
            catch (Exception ex)
            {
                tcp.Close();
                throw new IOException("secure (TLS) connection failed - check the computer's date and time (" + DirectWebSocket.Deepest(ex) + ")");
            }
            return ssl;
        }

        /// <summary>One HTTP/1.1 request on its own connection. Throws HttpStatusException for a status other than 2xx.</summary>
        public static RawResponse Request(IPAddress[] addresses, string host, int port, bool tls, string method,
                                          string pathAndQuery, string body, int timeoutMs, string extraHeaders)
        {
            TcpClient tcp;
            Stream s = Open(addresses, host, port, tls, timeoutMs, out tcp);
            try
            {
                byte[] content = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
                bool defaultPort = (tls && port == 443) || (!tls && port == 80);
                string head = method + " " + pathAndQuery + " HTTP/1.1\r\n" +
                              "Host: " + host + (defaultPort ? "" : ":" + port) + "\r\n" +
                              "User-Agent: BwatClient\r\n" +
                              "Cache-Control: no-cache\r\n" +
                              (extraHeaders ?? "") +
                              (body != null ? "Content-Type: text/plain; charset=utf-8\r\nContent-Length: " + content.Length + "\r\n" : "") +
                              "Connection: close\r\n\r\n";
                byte[] h = Encoding.ASCII.GetBytes(head);
                s.Write(h, 0, h.Length);
                if (content.Length > 0) s.Write(content, 0, content.Length);
                s.Flush();

                string status = ReadLine(s);
                if (status == null) throw new IOException("the server closed the connection");
                string[] parts = status.Split(' ');
                int code = parts.Length > 1 ? int.Parse(parts[1]) : 0;
                int length = -1;
                bool chunked = false;
                string line;
                while ((line = ReadLine(s)) != null && line.Length > 0)
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out length);
                    if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && value.ToLowerInvariant().Contains("chunked")) chunked = true;
                }

                MemoryStream data = new MemoryStream();
                if (chunked)
                {
                    while (true)
                    {
                        string sizeLine = ReadLine(s);
                        if (sizeLine == null) break;
                        int semi = sizeLine.IndexOf(';');
                        if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                        int size = Convert.ToInt32(sizeLine.Trim(), 16);
                        if (size == 0) break;
                        Copy(s, data, size);
                        ReadLine(s);   // the \r\n after each chunk
                    }
                }
                else if (length >= 0) Copy(s, data, length);
                else
                {
                    byte[] buf = new byte[4096];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) data.Write(buf, 0, n);
                }
                if (code < 200 || code > 299) throw new HttpStatusException(code);
                RawResponse resp = new RawResponse();
                resp.Status = code;
                resp.Body = Encoding.UTF8.GetString(data.ToArray());
                return resp;
            }
            finally { tcp.Close(); }
        }

        private static void Copy(Stream s, Stream target, int count)
        {
            byte[] buf = new byte[4096];
            while (count > 0)
            {
                int n = s.Read(buf, 0, Math.Min(buf.Length, count));
                if (n <= 0) throw new IOException("the connection was closed early");
                target.Write(buf, 0, n);
                count -= n;
            }
        }

        private static string ReadLine(Stream s)
        {
            List<byte> bytes = new List<byte>();
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) return bytes.Count > 0 ? Encoding.ASCII.GetString(bytes.ToArray()) : null;
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }
    }
}
