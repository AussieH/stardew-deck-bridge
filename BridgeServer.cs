using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StardewDeckBridge;

internal sealed class BridgeServer : IDisposable
{
    private const int MaxLineBytes = 64 * 1024;
    private const int PingMilliseconds = 4000;

    private readonly int port;
    private readonly Action<string> log;
    private readonly List<Client> clients = new();
    private readonly object gate = new();
    private TcpListener? listener;
    private Thread? acceptThread;
    private Timer? pingTimer;
    private volatile bool running;

    public ConcurrentQueue<(Client Client, string Line)> Incoming { get; } = new();

    public volatile string? Hello;

    public volatile string? LastState;

    public BridgeServer(int port, Action<string> log)
    {
        this.port = port;
        this.log = log;
    }

    public int ClientCount { get { lock (this.gate) return this.clients.Count; } }

    public bool Start()
    {
        try
        {
            this.listener = new TcpListener(IPAddress.Loopback, this.port);
            this.listener.Start();
        }
        catch (SocketException ex)
        {
            this.log($"Could not listen on 127.0.0.1:{this.port} ({ex.SocketErrorCode}). Is another copy of the game running? Change Port in config.json.");
            return false;
        }

        this.running = true;
        this.acceptThread = new Thread(this.AcceptLoop) { IsBackground = true, Name = "StardewDeckBridge accept" };
        this.acceptThread.Start();
        this.pingTimer = new Timer(_ => this.Broadcast("{\"type\":\"ping\"}"), null, PingMilliseconds, PingMilliseconds);
        return true;
    }

    private void AcceptLoop()
    {
        while (this.running)
        {
            TcpClient tcp;
            try { tcp = this.listener!.AcceptTcpClient(); }
            catch { if (!this.running) return; continue; }

            if (tcp.Client.RemoteEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
            {
                tcp.Close();
                continue;
            }

            var client = new Client(tcp, this);
            lock (this.gate) this.clients.Add(client);
            if (this.Hello is { } hello) client.Send(hello);
            if (this.LastState is { } state) client.Send(state);
            client.Start();
        }
    }

    public void Broadcast(string json)
    {
        Client[] snapshot;
        lock (this.gate) snapshot = this.clients.ToArray();
        foreach (var c in snapshot) c.Send(json);
    }

    internal void Remove(Client client)
    {
        lock (this.gate) this.clients.Remove(client);
    }

    public void Dispose()
    {
        this.running = false;
        this.pingTimer?.Dispose();
        try { this.listener?.Stop(); } catch { }
        Client[] snapshot;
        lock (this.gate) snapshot = this.clients.ToArray();
        foreach (var c in snapshot) c.Close();
    }

    internal sealed class Client
    {
        private readonly TcpClient tcp;
        private readonly BridgeServer server;
        private readonly NetworkStream stream;
        private readonly object writeGate = new();
        private volatile bool closed;

        public Client(TcpClient tcp, BridgeServer server)
        {
            this.tcp = tcp;
            this.server = server;
            this.stream = tcp.GetStream();
            tcp.NoDelay = true;
        }

        public void Start()
        {
            new Thread(this.ReadLoop) { IsBackground = true, Name = "StardewDeckBridge client" }.Start();
        }

        private void ReadLoop()
        {
            var buffer = new byte[4096];
            var line = new MemoryStream();
            try
            {
                while (!this.closed)
                {
                    int read = this.stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    for (int i = 0; i < read; i++)
                    {
                        if (buffer[i] == (byte)'\n')
                        {
                            var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).Trim();
                            line.SetLength(0);
                            if (text.Length > 0) this.server.Incoming.Enqueue((this, text));
                        }
                        else if (line.Length < MaxLineBytes)
                        {
                            line.WriteByte(buffer[i]);
                        }
                        else
                        {
                            this.Close();
                            return;
                        }
                    }
                }
            }
            catch { }
            this.Close();
        }

        public void Send(string json)
        {
            if (this.closed) return;
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            try
            {
                lock (this.writeGate) this.stream.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                this.Close();
            }
        }

        public void Close()
        {
            if (this.closed) return;
            this.closed = true;
            try { this.tcp.Close(); } catch { }
            this.server.Remove(this);
        }
    }
}
