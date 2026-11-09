/*
 * Bwat Client / HttpPollTransport.cs
 *
 * The last way to reach the online server, for networks that block WebSockets but allow
 * normal web pages: it uses only ordinary HTTPS requests (HttpWebRequest), with the same
 * proxy settings as the web browser.
 *   - Open():      POST /poll/open           -> session id
 *   - SendLine():  POST /poll/send?sid=...   (one request line in the body)
 *   - ReadLine():  GET  /poll/recv?sid=...   waits up to 20 s for lines from the server
 * The server side is BwatServer/HttpPolling.cs.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Bwat.Client
{
    public class HttpPollTransport
    {
        private string baseUrl;
        private string host;
        private int port;
        private bool tls;
        private IPAddress[] knownAddresses;   // set when Windows' DNS could not find the server (NetHelpers.cs)
        private string sid;
        private readonly Queue<string> pending = new Queue<string>();
        private volatile bool closed;

        /// <summary>Starts a session. Throws an exception with the reason on failure.</summary>
        public void Open(string host, int port, bool tls, int waitSeconds)
        {
            Open(host, port, tls, waitSeconds, null);
        }

        public void Open(string host, int port, bool tls, int waitSeconds, IPAddress[] addresses)
        {
            this.host = host;
            this.port = port;
            this.tls = tls;
            knownAddresses = addresses;
            if (ServicePointManager.DefaultConnectionLimit < 8) ServicePointManager.DefaultConnectionLimit = 8;
            bool defaultPort = (tls && port == 443) || (!tls && port == 80);
            baseUrl = (tls ? "https://" : "http://") + host + (defaultPort ? "" : ":" + port) + "/poll/";
            sid = Request("POST", "open", "", waitSeconds * 1000).Trim();
            if (sid.Length == 0) throw new IOException("the server did not start a session");
        }

        /// <summary>Sends one request line (tries twice).</summary>
        public void SendLine(string line)
        {
            try { Request("POST", "send?sid=" + sid, line, 20000); }
            catch (Exception ex)
            {
                if (IsGone(ex) || closed) throw;
                Request("POST", "send?sid=" + sid, line, 20000);
            }
        }

        /// <summary>Returns the next line from the server, or null when the session has ended.</summary>
        public string ReadLine()
        {
            int failures = 0;
            while (pending.Count == 0)
            {
                if (closed) return null;
                string body;
                try
                {
                    body = Request("GET", "recv?sid=" + sid, null, 45000);
                    failures = 0;
                }
                catch (Exception ex)
                {
                    if (closed || IsGone(ex)) return null;
                    if (++failures >= 6) return null;      // about half a minute without the server
                    Thread.Sleep(2000);
                    continue;
                }
                foreach (string l in body.Split('\n'))
                {
                    string t = l.TrimEnd('\r');
                    if (t.Length > 0) pending.Enqueue(t);
                }
            }
            return pending.Dequeue();
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            string id = sid;
            if (id == null) return;
            // Tell the server, without making the window wait.
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Request("POST", "close?sid=" + id, "", 3000); } catch { }
            });
        }

        private static bool IsGone(Exception e)
        {
            HttpStatusException hs = e as HttpStatusException;
            if (hs != null) return hs.Status == 410 || hs.Status == 404;
            WebException ex = e as WebException;
            if (ex == null) return false;
            HttpWebResponse r = ex.Response as HttpWebResponse;
            return r != null && (r.StatusCode == HttpStatusCode.Gone || r.StatusCode == HttpStatusCode.NotFound);
        }

        private string Request(string method, string path, string body, int timeoutMs)
        {
            if (knownAddresses != null)
                return RawHttp.Request(knownAddresses, host, port, tls, method, "/poll/" + path, body, timeoutMs, null).Body;

            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(baseUrl + path);
            r.Method = method;
            r.Timeout = timeoutMs;
            r.ReadWriteTimeout = timeoutMs;
            r.KeepAlive = true;
            r.UserAgent = "BwatClient";
            r.ServicePoint.Expect100Continue = false;
            r.Headers["Cache-Control"] = "no-cache";
            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                r.ContentType = "text/plain; charset=utf-8";
                r.ContentLength = bytes.Length;
                using (Stream s = r.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
            using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}
