using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VoxtaBridge
{
    /// <summary>
    /// Tiny HTTP/1.1 server on a raw TcpListener (no HttpListener: fewer moving parts under Mono/Wine).
    /// Runs on background threads and must never touch ModApi objects; handlers only read cached data.
    /// </summary>
    public sealed class HttpServer
    {
        public delegate (int status, string body) Handler(string method, string path, string authHeader, string requestBody);

        const int MaxBody = 16 * 1024;

        readonly IPAddress address;
        readonly int port;
        readonly Handler handler;
        readonly Action<string> log;
        TcpListener listener;
        Thread acceptThread;
        volatile bool running;

        public HttpServer(string bind, int port, Handler handler, Action<string> log)
        {
            address = IPAddress.Parse(bind);
            this.port = port;
            this.handler = handler;
            this.log = log;
        }

        public void Start()
        {
            listener = new TcpListener(address, port);
            listener.Start();
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "VoxtaBridge-http" };
            acceptThread.Start();
            log($"listening on {address}:{port}");
        }

        public void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            log("listener stopped");
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception e)
                {
                    if (running) log($"accept failed: {e.Message}");
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    var stream = client.GetStream();
                    var (head, extra) = ReadHead(stream);
                    if (head == null) return;

                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var parts = lines[0].Split(' ');
                    if (parts.Length < 2) { Write(stream, 400, "{\"error\":\"bad request\"}"); return; }

                    string auth = null;
                    int contentLength = 0;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var idx = lines[i].IndexOf(':');
                        if (idx <= 0) continue;
                        var name = lines[i].Substring(0, idx).Trim();
                        var value = lines[i].Substring(idx + 1).Trim();
                        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) auth = value;
                        else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out contentLength);
                    }
                    if (contentLength < 0 || contentLength > MaxBody) { Write(stream, 400, "{\"error\":\"body too large\"}"); return; }
                    var body = ReadBody(stream, extra, contentLength);

                    var path = parts[1];
                    var q = path.IndexOf('?');
                    if (q >= 0) path = path.Substring(0, q);

                    var (status, response) = handler(parts[0], path, auth, body);
                    Write(stream, status, response);
                }
                catch (Exception e)
                {
                    log($"request failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        /// <summary>Header block and whatever body bytes arrived with it.</summary>
        static (string head, byte[] extra) ReadHead(NetworkStream stream)
        {
            var buf = new byte[8192];
            int total = 0;
            while (total < buf.Length)
            {
                int n = stream.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
                for (int i = 3; i < total; i++)
                {
                    if (buf[i - 3] == '\r' && buf[i - 2] == '\n' && buf[i - 1] == '\r' && buf[i] == '\n')
                    {
                        var head = Encoding.ASCII.GetString(buf, 0, i - 3);
                        var extra = new byte[total - (i + 1)];
                        Array.Copy(buf, i + 1, extra, 0, extra.Length);
                        return (head, extra);
                    }
                }
            }
            return (null, null);
        }

        static string ReadBody(NetworkStream stream, byte[] extra, int length)
        {
            if (length <= 0) return "";
            var body = new byte[length];
            var have = Math.Min(extra.Length, length);
            Array.Copy(extra, body, have);
            while (have < length)
            {
                int n = stream.Read(body, have, length - have);
                if (n <= 0) break;
                have += n;
            }
            return Encoding.UTF8.GetString(body, 0, have);
        }

        static void Write(Stream stream, int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            var reason = status == 200 ? "OK" : status == 401 ? "Unauthorized" : status == 404 ? "Not Found" : status == 400 ? "Bad Request"
                : status == 409 ? "Conflict" : status == 503 ? "Service Unavailable" : status == 504 ? "Gateway Timeout" : "Error";
            var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head);
            stream.Write(h, 0, h.Length);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }
}
