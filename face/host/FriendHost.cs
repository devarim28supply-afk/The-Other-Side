using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// Serves the face page on this PC only, for the friend zip. The LiveKit
// account secret is not in that zip: friend.json holds one room pass.
static class FriendHost
{
    static readonly object FrameGate = new();
    public static void Start(string friendJsonPath)
    {
        var www = Path.Combine(AppContext.BaseDirectory, "www");
        var pass = File.ReadAllText(friendJsonPath);
        var feed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "soundfeed.txt"));
        using (var doc = JsonDocument.Parse(pass))
        {
            if (!doc.RootElement.TryGetProperty("url", out _) || !doc.RootElement.TryGetProperty("token", out _))
                throw new InvalidOperationException("friend.json is missing the room pass");
            if (doc.RootElement.TryGetProperty("feed", out var feedProp))
            {
                var given = feedProp.GetString();
                if (!string.IsNullOrWhiteSpace(given)) feed = given;
            }
        }
        var listener = new TcpListener(IPAddress.Loopback, 5192);
        listener.Start();
        var thread = new Thread(() => Accept(listener, www, feed, pass)) { IsBackground = true, Name = "face-host" };
        thread.Start();
    }

    static void Accept(TcpListener listener, string www, string feed, string pass)
    {
        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { return; }
            var t = new Thread(() =>
            {
                try { Serve(client, www, feed, pass); }
                catch { }
            }) { IsBackground = true };
            t.Start();
        }
    }

    static void Serve(TcpClient client, string www, string feed, string pass)
    {
        using (client)
        {
            client.ReceiveTimeout = 8000;
            client.SendTimeout = 8000;
            using var stream = client.GetStream();
            string req;
            try { req = ReadHeaders(stream); }
            catch { return; }
            var line = req.Split('\n')[0];
            var parts = line.Split(' ');
            if (parts.Length < 2) return;
            var rawPath = parts[1];
            var path = rawPath.Split('?')[0];
            if (path == "/track" && parts[0] == "POST")
            {
                TakeTrack(stream, req, rawPath);
                return;
            }
            if (path == "/frame" && parts[0] == "POST")
            {
                SaveFrame(stream, req, rawPath);
                return;
            }
            if (path == "/api/shape")
            {
                // My shape, written by the game when I press Right Arrow. The page
                // publishes it to the other player.
                var mine = "diamond";
                try
                {
                    var f = Path.Combine(ShareHexForm.PlateDir(), "shape_self.txt");
                    if (File.Exists(f) && File.ReadAllText(f).Trim() == "hex") mine = "hex";
                }
                catch { }
                Write(stream, "text/plain", mine);
                return;
            }
            if (path == "/api/slide")
            {
                // My last Up Arrow press, written by the game. The page publishes it.
                var mine = "";
                try
                {
                    var f = Path.Combine(ShareHexForm.PlateDir(), "slide_self.txt");
                    if (File.Exists(f)) mine = File.ReadAllText(f).Trim();
                }
                catch { }
                Write(stream, "text/plain", mine);
                return;
            }
            if (path == "/slide" && parts[0] == "POST")
            {
                // The other player's Up Arrow press, for the slide window to see.
                var v = QueryFlag(rawPath, "v");
                if (v.Length == 0 || v.Length > 40 || !v.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                {
                    Write(stream, "text/plain", "bad slide", 404);
                    return;
                }
                try
                {
                    var dir = ShareHexForm.PlateDir();
                    Directory.CreateDirectory(dir);
                    var finalPath = Path.Combine(dir, "slide_remote.txt");
                    var temp = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temp, v);
                    lock (FrameGate) File.Move(temp, finalPath, true);
                    Write(stream, "text/plain", "ok");
                }
                catch
                {
                    Write(stream, "text/plain", "busy", 503);
                }
                return;
            }
            if (path == "/shape" && parts[0] == "POST")
            {
                // The other player's shape, for the game to read.
                var v = QueryFlag(rawPath, "v");
                if (v != "hex" && v != "diamond")
                {
                    Write(stream, "text/plain", "bad shape", 404);
                    return;
                }
                try
                {
                    var dir = ShareHexForm.PlateDir();
                    Directory.CreateDirectory(dir);
                    var finalPath = Path.Combine(dir, "shape_remote.txt");
                    var temp = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temp, v);
                    lock (FrameGate) File.Move(temp, finalPath, true);
                    Write(stream, "text/plain", "ok");
                }
                catch
                {
                    Write(stream, "text/plain", "busy", 503);
                }
                return;
            }
            if (path == "/api/health")
            {
                Write(stream, "application/json", """{"ok":true,"room":"the-other-side"}""");
                return;
            }
            if (path == "/api/token")
            {
                Write(stream, "application/json", pass);
                return;
            }
            if (path == "/feed")
            {
                StreamFeed(stream, feed);
                return;
            }
            var rel = path == "/" ? "index.html" : path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var file = Path.GetFullPath(Path.Combine(www, rel));
            if (!file.StartsWith(Path.GetFullPath(www), StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                Write(stream, "text/plain", "not found", 404);
                return;
            }
            var bytes = File.ReadAllBytes(file);
            WriteBytes(stream, TypeOf(file), bytes);
        }
    }

    static void TakeTrack(NetworkStream stream, string headers, string rawPath)
    {
        var len = ContentLength(headers);
        if (len < 200 || len > 2_000_000)
        {
            Write(stream, "text/plain", "bad frame", 404);
            return;
        }
        var body = ReadBody(stream, len);
        if (body == null)
        {
            Write(stream, "text/plain", "bad frame", 404);
            return;
        }
        var mirror = QueryFlag(rawPath, "mir") != "0";
        FaceTrack.Submit(body, mirror);
        Write(stream, "text/plain", "ok");
    }

    static string QueryFlag(string rawPath, string key)
    {
        var q = rawPath.Contains('?') ? rawPath[(rawPath.IndexOf('?') + 1)..] : "";
        foreach (var pair in q.Split('&'))
        {
            var bits = pair.Split('=', 2);
            if (bits.Length == 2 && bits[0] == key) return Uri.UnescapeDataString(bits[1]);
        }
        return "";
    }

    static int ContentLength(string headers)
    {
        var len = 0;
        foreach (var h in headers.Split('\n'))
        {
            if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(h.Split(':')[1].Trim(), out len);
        }
        return len;
    }

    static byte[]? ReadBody(NetworkStream stream, int len)
    {
        var body = new byte[len];
        var got = 0;
        while (got < len)
        {
            var n = stream.Read(body, got, len - got);
            if (n <= 0) break;
            got += n;
        }
        return got == len ? body : null;
    }

    static void SaveFrame(NetworkStream stream, string headers, string rawPath)
    {
        var id = "";
        var q = rawPath.Contains('?') ? rawPath[(rawPath.IndexOf('?') + 1)..] : "";
        foreach (var pair in q.Split('&'))
        {
            var bits = pair.Split('=', 2);
            if (bits.Length == 2 && bits[0] == "id") id = Uri.UnescapeDataString(bits[1]);
        }
        if (id != "remote" && id != "local")
        {
            Write(stream, "text/plain", "bad id", 404);
            return;
        }
        var len = ContentLength(headers);
        if (len < 200 || len > 2_000_000)
        {
            Write(stream, "text/plain", "bad frame", 404);
            return;
        }
        var body = ReadBody(stream, len);
        if (body == null)
        {
            Write(stream, "text/plain", "bad frame", 404);
            return;
        }
        // Beside the FacePlate script: the game reads remote.jpg from there.
        var dir = ShareHexForm.PlateDir();
        Directory.CreateDirectory(dir);
        var finalPath = Path.Combine(dir, id + ".jpg");
        var temp = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, body);
            lock (FrameGate) File.Move(temp, finalPath, true);
            Write(stream, "text/plain", "ok");
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            try { Write(stream, "text/plain", "busy", 503); } catch { }
        }
    }

    static void StreamFeed(NetworkStream stream, string feed)
    {
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\n\r\n: ok\r\n\r\n");
        stream.Write(head);
        long pos = 0;
        if (File.Exists(feed)) pos = new FileInfo(feed).Length;
        var carry = "";
        var until = DateTime.UtcNow.AddHours(8);
        while (DateTime.UtcNow < until && stream.CanWrite)
        {
            try
            {
                if (File.Exists(feed))
                {
                    var len = new FileInfo(feed).Length;
                    if (len < pos) { pos = 0; carry = ""; }
                    if (len > pos)
                    {
                        using var fs = new FileStream(feed, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        fs.Seek(pos, SeekOrigin.Begin);
                        var buf = new byte[len - pos];
                        fs.ReadExactly(buf);
                        pos = len;
                        var text = carry + Encoding.UTF8.GetString(buf);
                        var lines = text.Split('\n');
                        carry = lines[^1];
                        foreach (var raw in lines[..^1])
                        {
                            var packet = Encoding.UTF8.GetBytes("data: " + raw.TrimEnd('\r') + "\n\n");
                            stream.Write(packet);
                        }
                    }
                }
                stream.Write(Encoding.ASCII.GetBytes(": ping\n\n"));
                Thread.Sleep(200);
            }
            catch { return; }
        }
    }

    static string ReadHeaders(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < 8192)
        {
            if (stream.Read(one, 0, 1) <= 0) break;
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break;
        }
        return sb.ToString();
    }

    static void Write(NetworkStream stream, string type, string body, int code = 200)
        => WriteBytes(stream, type, Encoding.UTF8.GetBytes(body), code);

    static void WriteBytes(NetworkStream stream, string type, byte[] body, int code = 200)
    {
        var reason = code == 200 ? "OK" : "Not Found";
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        stream.Write(head);
        stream.Write(body);
    }

    static string TypeOf(string file)
    {
        if (file.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return "text/html; charset=utf-8";
        if (file.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
            return "text/javascript; charset=utf-8";
        if (file.EndsWith(".css", StringComparison.OrdinalIgnoreCase)) return "text/css; charset=utf-8";
        return "application/octet-stream";
    }
}
