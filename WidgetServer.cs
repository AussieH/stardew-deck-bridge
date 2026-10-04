using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StardewDeckBridge;

internal sealed class WidgetServer : IDisposable
{
    public const int DefaultPort = 52818;
    public const int Protocol = 1;
    public const string KeyHeader = "X-Stardew-Deck";
    public const int MaxHeadBytes = 8 * 1024;
    public const int MaxBodyBytes = 8 * 1024;
    public const int MaxMessageBytes = 8 * 1024;
    public const int MaxConnections = 16;
    public const int MaxQueued = 32;
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public readonly int Port;
    private readonly byte[] keyBytes;
    private readonly string mod;
    private readonly Action<string> log;
    private readonly int commandTimeoutMs;
    private readonly int heartbeatMs;
    private readonly int readTimeoutMs;
    private readonly List<TcpListener> listeners = new();
    private readonly List<Socket> open = new();
    private readonly List<WebSocketConnection> sockets = new();
    private readonly object gate = new();
    private Timer? heartbeat;
    private volatile bool running;
    private int connections;
    private long lastTickMs;

    public ConcurrentQueue<WidgetCommand> Incoming { get; } = new();

    public volatile bool InWorld;

    public volatile bool ActionsAllowed = true;

    public WidgetServer(int port, string key, string mod, Action<string> log, int commandTimeoutMs = 4000, int heartbeatMs = 2000, int readTimeoutMs = 5000)
    {
        this.Port = port;
        this.keyBytes = Encoding.UTF8.GetBytes(key);
        this.mod = mod;
        this.log = log;
        this.commandTimeoutMs = commandTimeoutMs;
        this.heartbeatMs = heartbeatMs;
        this.readTimeoutMs = readTimeoutMs;
    }

    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public int WebSocketCount { get { lock (this.gate) return this.sockets.Count; } }

    public void Tick(bool inWorld, bool actions)
    {
        this.InWorld = inWorld;
        this.ActionsAllowed = actions;
        Interlocked.Exchange(ref this.lastTickMs, NowMs());
    }

    private bool Ticking => NowMs() - Interlocked.Read(ref this.lastTickMs) < 1500;

    public bool Start()
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(address, this.Port) { ExclusiveAddressUse = true };
                listener.Start();
                this.listeners.Add(listener);
            }
            catch (SocketException ex) when (address.Equals(IPAddress.IPv6Loopback) && ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable or SocketError.ProtocolNotSupported)
            {
                try { listener?.Stop(); } catch { }
                this.log("No IPv6 loopback on this PC; the widget endpoint listens on 127.0.0.1 only.");
            }
            catch (SocketException ex)
            {
                try { listener?.Stop(); } catch { }
                this.log($"Could not listen for the widget on {(address.Equals(IPAddress.Loopback) ? "127.0.0.1" : "[::1]")}:{this.Port} ({ex.SocketErrorCode}). Is another copy of the game running?");
                this.StopListeners();
                return false;
            }
        }

        this.running = true;
        foreach (var listener in this.listeners)
            new Thread(this.AcceptLoop) { IsBackground = true, Name = "StardewDeckBridge widget accept" }.Start(listener);
        this.heartbeat = new Timer(_ => this.Beat(), null, this.heartbeatMs, this.heartbeatMs);
        return true;
    }

    private void StopListeners()
    {
        foreach (var l in this.listeners) { try { l.Stop(); } catch { } }
        this.listeners.Clear();
    }

    public void Dispose()
    {
        if (!this.running && this.listeners.Count == 0) return;
        this.running = false;
        this.heartbeat?.Dispose();
        this.StopListeners();
        WebSocketConnection[] ws;
        Socket[] all;
        lock (this.gate) { ws = this.sockets.ToArray(); all = this.open.ToArray(); }
        foreach (var w in ws) w.Close(1001);
        foreach (var s in all) CloseSocket(s);
    }

    private void AcceptLoop(object? state)
    {
        var listener = (TcpListener)state!;
        while (this.running)
        {
            Socket socket;
            try { socket = listener.AcceptSocket(); }
            catch { if (!this.running) return; continue; }

            if (socket.RemoteEndPoint is not IPEndPoint ep || !IPAddress.IsLoopback(ep.Address))
            {
                CloseSocket(socket);
                continue;
            }
            if (Interlocked.Increment(ref this.connections) > MaxConnections)
            {
                Interlocked.Decrement(ref this.connections);
                CloseSocket(socket);
                continue;
            }
            lock (this.gate) this.open.Add(socket);
            new Thread(() =>
            {
                try { this.Serve(socket); }
                catch { }
                finally
                {
                    lock (this.gate) this.open.Remove(socket);
                    Interlocked.Decrement(ref this.connections);
                    CloseSocket(socket);
                }
            }) { IsBackground = true, Name = "StardewDeckBridge widget client" }.Start();
        }
    }

    private static void CloseSocket(Socket s)
    {
        try { s.Shutdown(SocketShutdown.Both); } catch { }
        try { s.Close(); } catch { }
    }

    private void Serve(Socket socket)
    {
        socket.NoDelay = true;
        socket.ReceiveTimeout = this.readTimeoutMs;
        socket.SendTimeout = 5000;
        using var stream = new NetworkStream(socket, ownsSocket: false);

        var buf = new byte[MaxHeadBytes];
        int len = 0, headEnd = -1;
        while (headEnd < 0)
        {
            if (len == buf.Length)
            {
                Respond(stream, 431, ErrorJson("request headers too large"), cors: false);
                return;
            }
            int n;
            try { n = stream.Read(buf, len, buf.Length - len); }
            catch { return; }
            if (n <= 0) return;
            len += n;
            headEnd = IndexOfHeadEnd(buf, len);
        }

        var head = ParseHead(buf.AsSpan(0, headEnd), out int status, out string? error);
        if (head is null)
        {
            Respond(stream, status, ErrorJson(error ?? "bad request"), cors: false);
            return;
        }
        var leftover = buf.AsSpan(headEnd + 4, len - headEnd - 4).ToArray();

        var d = this.Decide(head);
        if (d.Route == Route.Reject)
        {
            if (d.Status == 403) this.log($"Widget endpoint refused {head.Method} {head.Path}: {d.Error} (Origin {head.Header("Origin") ?? "none"}, Host {head.Header("Host") ?? "none"}).");
            Respond(stream, d.Status, ErrorJson(d.Error ?? "refused"), d.Cors, extra: d.Status == 426 ? "Sec-WebSocket-Version: 13\r\n" : null);
            return;
        }
        switch (d.Route)
        {
            case Route.Preflight:
                Respond(stream, 204, null, cors: true, preflight: true);
                return;
            case Route.Hello:
                Respond(stream, 200, this.HelloJson(), cors: true);
                return;
            case Route.Command:
                this.ServeCommand(stream, head, leftover);
                return;
            case Route.WebSocket:
                this.ServeWebSocket(socket, stream, head, leftover);
                return;
        }
    }

    private void ServeCommand(NetworkStream stream, HttpHead head, byte[] leftover)
    {
        int length = int.Parse(head.Header("Content-Length")!);
        var body = new byte[length];
        int got = Math.Min(length, leftover.Length);
        Array.Copy(leftover, body, got);
        while (got < length)
        {
            int n;
            try { n = stream.Read(body, got, length - got); }
            catch { return; }
            if (n <= 0) return;
            got += n;
        }
        string text;
        try { text = StrictUtf8.GetString(body); }
        catch (DecoderFallbackException)
        {
            Respond(stream, 400, ResultJson(0, "bad message"), cors: true);
            return;
        }
        if (!TryParseCommand(text, out long id, out string? why))
        {
            Respond(stream, 400, ResultJson(id, why ?? "bad message"), cors: true);
            return;
        }

        string? reply = null;
        using var done = new ManualResetEventSlim(false);
        var cmd = new WidgetCommand(text, id, json => { reply = json; try { done.Set(); } catch (ObjectDisposedException) { } });
        if (!this.Enqueue(cmd, timeout: false))
        {
            Respond(stream, 503, ResultJson(id, "busy"), cors: true);
            return;
        }
        bool timedOut = false;
        if (!done.Wait(this.commandTimeoutMs))
        {
            if (cmd.TryClaim()) timedOut = true;
            else done.Wait(2000);
        }
        timedOut |= reply is null;
        Respond(stream, timedOut ? 504 : 200, timedOut ? ResultJson(id, "game not answering") : reply, cors: true);
    }

    private bool Enqueue(WidgetCommand cmd, bool timeout)
    {
        if (!this.running || this.Incoming.Count >= MaxQueued) return false;
        this.Incoming.Enqueue(cmd);
        if (timeout)
        {
            Task.Delay(this.commandTimeoutMs).ContinueWith(_ =>
            {
                if (cmd.TryClaim()) cmd.Reply(ResultJson(cmd.Id, "game not answering"));
            }, TaskScheduler.Default);
        }
        return true;
    }

    internal enum Route { Reject, Preflight, Hello, Command, WebSocket }

    internal readonly record struct Decision(Route Route, int Status, string? Error, bool Cors);

    internal Decision Decide(HttpHead head)
    {
        if (!HostAllowed(head.Header("Host"), this.Port)) return new(Route.Reject, 403, "host not allowed", false);
        if (!OriginAllowed(head.Header("Origin"))) return new(Route.Reject, 403, "origin not allowed", false);
        if (head.Path is not ("/hello" or "/command" or "/ws")) return new(Route.Reject, 404, "not found", true);
        return head.Method switch
        {
            "OPTIONS" => new(Route.Preflight, 204, null, true),
            "GET" when head.Path == "/hello" => new(Route.Hello, 200, null, true),
            "GET" when head.Path == "/ws" => this.DecideWebSocket(head),
            "POST" when head.Path == "/command" => this.DecideCommand(head),
            _ => new(Route.Reject, 405, "method not allowed", true),
        };
    }

    private Decision DecideCommand(HttpHead head)
    {
        if (!this.KeyMatches(head.Header(KeyHeader))) return new(Route.Reject, 403, "key", true);
        if (head.Header("Transfer-Encoding") is not null) return new(Route.Reject, 400, "send a Content-Length, not chunks", true);
        var lengthText = head.Header("Content-Length");
        if (lengthText is null) return new(Route.Reject, 411, "Content-Length required", true);
        if (lengthText.Length is 0 or > 9 || !lengthText.All(c => c is >= '0' and <= '9')) return new(Route.Reject, 400, "bad Content-Length", true);
        int length = int.Parse(lengthText);
        if (length > MaxBodyBytes) return new(Route.Reject, 413, "body too large", true);
        if (length == 0) return new(Route.Reject, 400, "empty body", true);
        string type = (head.Header("Content-Type") ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (type is not ("application/json" or "text/plain")) return new(Route.Reject, 415, "send application/json or text/plain", true);
        return new(Route.Command, 200, null, true);
    }

    private Decision DecideWebSocket(HttpHead head)
    {
        if (!string.Equals(head.Header("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase)) return new(Route.Reject, 426, "upgrade required", true);
        var connection = head.Header("Connection") ?? "";
        if (!connection.Split(',').Any(t => t.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase))) return new(Route.Reject, 400, "bad WebSocket handshake", true);
        if (head.Header("Sec-WebSocket-Version") != "13") return new(Route.Reject, 426, "WebSocket version 13 only", true);
        if (!ValidWebSocketKey(head.Header("Sec-WebSocket-Key"))) return new(Route.Reject, 400, "bad WebSocket handshake", true);
        if (!this.KeyMatches(head.QueryValue("key"))) return new(Route.Reject, 403, "key", true);
        return new(Route.WebSocket, 101, null, false);
    }

    internal bool KeyMatches(string? given) => given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), this.keyBytes);

    internal static bool HostAllowed(string? host, int port)
    {
        if (host is null) return false;
        host = host.Trim();
        return host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)
            || host == $"127.0.0.1:{port}"
            || host == $"[::1]:{port}";
    }

    internal static bool OriginAllowed(string? origin) =>
        origin is null
        || origin == "null"
        || origin.Equals("file://", StringComparison.OrdinalIgnoreCase)
        || origin.Equals("file:///", StringComparison.OrdinalIgnoreCase);

    internal static bool ValidWebSocketKey(string? key)
    {
        if (key is null || key.Length != 24) return false;
        var bytes = new byte[18];
        return Convert.TryFromBase64String(key, bytes, out int n) && n == 16;
    }

    internal static string WebSocketAccept(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

    internal static bool TryParseCommand(string text, out long id, out string? error)
    {
        id = 0;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "bad message"; return false; }
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var parsed)) id = parsed;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "command") { error = "not a command"; return false; }
            if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(name.GetString()) || name.GetString()!.Length > 64) { error = "no command name"; return false; }
            if (root.TryGetProperty("args", out var args) && args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) { error = "bad args"; return false; }
            return true;
        }
        catch (JsonException)
        {
            error = "bad message";
            return false;
        }
    }

    internal string HelloJson() => JsonSerializer.Serialize(new
    {
        type = "hello",
        protocol = Protocol,
        mod = this.mod,
        controls = this.ActionsAllowed,
        inWorld = this.InWorld,
    }, Json);

    internal string StatusJson() => JsonSerializer.Serialize(new
    {
        type = "status",
        protocol = Protocol,
        mod = this.mod,
        connected = true,
        inWorld = this.InWorld,
        ticking = this.Ticking,
        actions = this.ActionsAllowed,
    }, Json);

    internal static string ResultJson(long id, string error) =>
        JsonSerializer.Serialize(new { type = "result", id, ok = false, error }, Json);

    private static string ErrorJson(string error) => JsonSerializer.Serialize(new { error }, Json);

    private static void Respond(Stream stream, int status, string? body, bool cors, bool preflight = false, string? extra = null)
    {
        var bytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        if (body is not null) sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n");
        if (cors)
        {
            sb.Append("Access-Control-Allow-Origin: *\r\nVary: Origin\r\n");
            if (preflight)
                sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type, ").Append(KeyHeader).Append("\r\nAccess-Control-Max-Age: 600\r\n");
        }
        if (extra is not null) sb.Append(extra);
        sb.Append("\r\n");
        try
        {
            var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (bytes.Length > 0) stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch { }
    }

    private static string Reason(int status) => status switch
    {
        101 => "Switching Protocols", 200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden",
        404 => "Not Found", 405 => "Method Not Allowed", 411 => "Length Required", 413 => "Payload Too Large",
        415 => "Unsupported Media Type", 426 => "Upgrade Required", 431 => "Request Header Fields Too Large",
        503 => "Service Unavailable", 504 => "Gateway Timeout", 505 => "HTTP Version Not Supported", _ => "Error",
    };

    internal static int IndexOfHeadEnd(byte[] buf, int len)
    {
        for (int i = 3; i < len; i++)
            if (buf[i] == '\n' && buf[i - 1] == '\r' && buf[i - 2] == '\n' && buf[i - 3] == '\r') return i - 3;
        return -1;
    }

    internal static HttpHead? ParseHead(ReadOnlySpan<byte> raw, out int status, out string? error)
    {
        status = 400;
        error = "bad request";
        foreach (byte b in raw)
        {
            if (b == '\r' || b == '\n' || b == '\t' || (b >= 0x20 && b < 0x7F)) continue;
            error = "bad characters in the request";
            return null;
        }
        string text = Encoding.ASCII.GetString(raw);
        var lines = text.Split("\r\n");
        if (lines.Any(l => l.Contains('\n') || l.Contains('\r'))) { error = "bad line ends"; return null; }

        var parts = lines[0].Split(' ');
        if (parts.Length != 3) return null;
        string method = parts[0], target = parts[1], version = parts[2];
        if (method.Length is 0 or > 16 || !method.All(c => c is >= 'A' and <= 'Z')) return null;
        if (version is not ("HTTP/1.1" or "HTTP/1.0")) { status = 505; error = "HTTP/1.1 only"; return null; }
        if (target.Length is 0 or > 2048 || target[0] != '/') return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var single = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Host", "Origin", "Content-Length", "Transfer-Encoding", KeyHeader, "Sec-WebSocket-Key", "Content-Type", "Upgrade" };
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) return null;
            if (line[0] is ' ' or '\t') { error = "folded header lines"; return null; }
            int colon = line.IndexOf(':');
            if (colon <= 0) return null;
            string name = line[..colon];
            if (!name.All(IsTokenChar)) return null;
            string value = line[(colon + 1)..].Trim(' ', '\t');
            if (headers.TryGetValue(name, out var before))
            {
                if (single.Contains(name)) { error = $"more than one {name}"; return null; }
                headers[name] = before + ", " + value;
            }
            else headers[name] = value;
            if (headers.Count > 64) { status = 431; error = "too many headers"; return null; }
        }

        int q = target.IndexOf('?');
        status = 200;
        error = null;
        return new HttpHead(method, target, q < 0 ? target : target[..q], q < 0 ? "" : target[(q + 1)..], version, headers);
    }

    private static bool IsTokenChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private void ServeWebSocket(Socket socket, NetworkStream stream, HttpHead head, byte[] leftover)
    {
        string accept = WebSocketAccept(head.Header("Sec-WebSocket-Key")!);
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");
        try { stream.Write(response, 0, response.Length); stream.Flush(); }
        catch { return; }

        socket.ReceiveTimeout = 0;
        var ws = new WebSocketConnection(stream);
        lock (this.gate) this.sockets.Add(ws);
        try
        {
            ws.Send(this.HelloJson());
            ws.Send(this.StatusJson());
            this.WebSocketLoop(ws, stream, leftover);
        }
        catch { }
        finally
        {
            lock (this.gate) this.sockets.Remove(ws);
            ws.Abort();
        }
    }

    private void WebSocketLoop(WebSocketConnection ws, NetworkStream stream, byte[] leftover)
    {
        var data = new byte[MaxMessageBytes + 14];
        int len = Math.Min(leftover.Length, data.Length);
        Array.Copy(leftover, data, len);
        var message = new MemoryStream();
        bool inMessage = false;

        while (this.running && !ws.Closed)
        {
            while (true)
            {
                var st = TryDecodeFrame(data.AsSpan(0, len), MaxMessageBytes, requireMask: true, out var frame, out ushort closeCode);
                if (st == FrameStatus.Incomplete) break;
                if (st == FrameStatus.Error) { ws.Close(closeCode); return; }
                Buffer.BlockCopy(data, frame.Consumed, data, 0, len - frame.Consumed);
                len -= frame.Consumed;

                switch (frame.Opcode)
                {
                    case 0x1:
                    case 0x0:
                        if ((frame.Opcode == 0x1) == inMessage) { ws.Close(1002); return; }
                        if (frame.Opcode == 0x1) message.SetLength(0);
                        if (message.Length + frame.Payload.Length > MaxMessageBytes) { ws.Close(1009); return; }
                        message.Write(frame.Payload, 0, frame.Payload.Length);
                        inMessage = !frame.Fin;
                        if (frame.Fin)
                        {
                            string text;
                            try { text = StrictUtf8.GetString(message.GetBuffer(), 0, (int)message.Length); }
                            catch (DecoderFallbackException) { ws.Close(1007); return; }
                            this.OnMessage(ws, text);
                        }
                        break;
                    case 0x2:
                        ws.Close(1003);
                        return;
                    case 0x8:
                        ushort code = frame.Payload.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(frame.Payload) : (ushort)1000;
                        ws.Close(frame.Payload.Length == 1 ? (ushort)1002 : code);
                        return;
                    case 0x9:
                        ws.SendFrame(0xA, frame.Payload);
                        break;
                    case 0xA:
                        break;
                }
            }
            int n;
            try { n = stream.Read(data, len, data.Length - len); }
            catch { return; }
            if (n <= 0) return;
            len += n;
        }
    }

    private void OnMessage(WebSocketConnection ws, string text)
    {
        string? type = null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) type = t.GetString();
        }
        catch (JsonException)
        {
            ws.Send(ResultJson(0, "bad message"));
            return;
        }
        switch (type)
        {
            case "command":
                if (!TryParseCommand(text, out long id, out string? error)) { ws.Send(ResultJson(id, error ?? "bad message")); return; }
                if (!this.Enqueue(new WidgetCommand(text, id, ws.Send), timeout: true)) ws.Send(ResultJson(id, "busy"));
                return;
            case "hello":
                ws.Send(this.HelloJson());
                return;
            default:
                ws.Send(JsonSerializer.Serialize(new { type = "error", error = "unknown message" }, Json));
                return;
        }
    }

    private void Beat()
    {
        WebSocketConnection[] ws;
        lock (this.gate) ws = this.sockets.ToArray();
        if (ws.Length == 0) return;
        string status = this.StatusJson();
        foreach (var w in ws) w.Send(status);
    }

    internal enum FrameStatus { Incomplete, Ok, Error }

    internal readonly record struct Frame(bool Fin, int Opcode, byte[] Payload, int Consumed);

    internal static FrameStatus TryDecodeFrame(ReadOnlySpan<byte> buf, int maxPayload, bool requireMask, out Frame frame, out ushort closeCode)
    {
        frame = default;
        closeCode = 0;
        if (buf.Length < 2) return FrameStatus.Incomplete;
        byte b0 = buf[0], b1 = buf[1];
        bool fin = (b0 & 0x80) != 0;
        int opcode = b0 & 0x0F;
        if ((b0 & 0x70) != 0 || opcode is not (0x0 or 0x1 or 0x2 or 0x8 or 0x9 or 0xA)) { closeCode = 1002; return FrameStatus.Error; }
        bool masked = (b1 & 0x80) != 0;
        if (requireMask && !masked) { closeCode = 1002; return FrameStatus.Error; }
        long length = b1 & 0x7F;
        int pos = 2;
        if (length == 126)
        {
            if (buf.Length < 4) return FrameStatus.Incomplete;
            length = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(2, 2));
            pos = 4;
        }
        else if (length == 127)
        {
            if (buf.Length < 10) return FrameStatus.Incomplete;
            ulong l = BinaryPrimitives.ReadUInt64BigEndian(buf.Slice(2, 8));
            if (l > long.MaxValue) { closeCode = 1002; return FrameStatus.Error; }
            length = (long)l;
            pos = 10;
        }
        if (opcode >= 0x8 && (!fin || length > 125)) { closeCode = 1002; return FrameStatus.Error; }
        if (length > maxPayload) { closeCode = 1009; return FrameStatus.Error; }
        Span<byte> mask = stackalloc byte[4];
        if (masked)
        {
            if (buf.Length < pos + 4) return FrameStatus.Incomplete;
            buf.Slice(pos, 4).CopyTo(mask);
            pos += 4;
        }
        if (buf.Length < pos + length) return FrameStatus.Incomplete;
        var payload = buf.Slice(pos, (int)length).ToArray();
        if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
        frame = new Frame(fin, opcode, payload, pos + (int)length);
        return FrameStatus.Ok;
    }

    internal static byte[] EncodeFrame(int opcode, ReadOnlySpan<byte> payload, bool fin = true, byte[]? mask = null)
    {
        int extra = payload.Length <= 125 ? 0 : payload.Length <= 0xFFFF ? 2 : 8;
        var frame = new byte[2 + extra + (mask is null ? 0 : 4) + payload.Length];
        frame[0] = (byte)((fin ? 0x80 : 0) | (opcode & 0x0F));
        int pos = 2;
        if (extra == 0) frame[1] = (byte)payload.Length;
        else if (extra == 2) { frame[1] = 126; BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)payload.Length); pos = 4; }
        else { frame[1] = 127; BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)payload.Length); pos = 10; }
        if (mask is not null)
        {
            frame[1] |= 0x80;
            mask.CopyTo(frame, pos);
            pos += 4;
            for (int i = 0; i < payload.Length; i++) frame[pos + i] = (byte)(payload[i] ^ mask[i & 3]);
        }
        else payload.CopyTo(frame.AsSpan(pos));
        return frame;
    }

    internal static byte[] ClosePayload(ushort code)
    {
        var p = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(p, code);
        return p;
    }

    private sealed class WebSocketConnection
    {
        private readonly NetworkStream stream;
        private readonly object writeGate = new();
        private volatile bool closed;

        public WebSocketConnection(NetworkStream stream) => this.stream = stream;

        public bool Closed => this.closed;

        public void Send(string text) => this.SendFrame(0x1, Encoding.UTF8.GetBytes(text));

        public void SendFrame(int opcode, byte[] payload)
        {
            if (this.closed) return;
            var frame = EncodeFrame(opcode, payload);
            try
            {
                lock (this.writeGate) { this.stream.Write(frame, 0, frame.Length); this.stream.Flush(); }
            }
            catch { this.Abort(); }
        }

        public void Close(ushort code)
        {
            if (this.closed) return;
            this.SendFrame(0x8, ClosePayload(code));
            this.closed = true;
        }

        public void Abort() => this.closed = true;
    }
}

internal sealed class HttpHead
{
    public HttpHead(string method, string target, string path, string query, string version, Dictionary<string, string> headers)
    {
        this.Method = method;
        this.Target = target;
        this.Path = path;
        this.Query = query;
        this.Version = version;
        this.Headers = headers;
    }

    public string Method { get; }
    public string Target { get; }
    public string Path { get; }
    public string Query { get; }
    public string Version { get; }
    public Dictionary<string, string> Headers { get; }

    public string? Header(string name) => this.Headers.TryGetValue(name, out var v) ? v : null;

    public string? QueryValue(string name)
    {
        foreach (var pair in this.Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string k = eq < 0 ? pair : pair[..eq];
            if (k != name) continue;
            try { return Uri.UnescapeDataString(eq < 0 ? "" : pair[(eq + 1)..]); }
            catch (UriFormatException) { return null; }
        }
        return null;
    }
}

internal sealed class WidgetCommand
{
    private readonly Action<string> send;
    private int claimed;

    public WidgetCommand(string line, long id, Action<string> send)
    {
        this.Line = line;
        this.Id = id;
        this.send = send;
    }

    public string Line { get; }
    public long Id { get; }

    public bool TryClaim() => Interlocked.Exchange(ref this.claimed, 1) == 0;

    public void Reply(string json) => this.send(json);
}
