using System.Text;
using System.Text.Json;

namespace StardewDeckBridge;

internal sealed class StateFile : IDisposable
{
    public const int FileProtocol = 1;

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public readonly string Dir;
    public readonly string StatePath;
    public readonly string TempPath;

    private readonly string baseHeader;
    private volatile string header;
    private readonly int heartbeatMs;
    private readonly int tickingWithinMs;
    private readonly Action<string>? log;
    private readonly object gate = new();
    private readonly object writeGate = new();
    private readonly AutoResetEvent wake = new(false);
    private string? body;
    private long seq;
    private long postedAt;
    private long lastWriteAt;
    private Thread? thread;
    private volatile bool running;
    private bool loggedFailure;

    public StateFile(string dir, string mod, string game, string smapi, Action<string>? log = null, int heartbeatMs = 1000, int tickingWithinMs = 2500)
    {
        this.Dir = dir;
        this.StatePath = Path.Combine(dir, "state.json");
        this.TempPath = Path.Combine(dir, "state.json.tmp");
        this.baseHeader = "\"protocol\":" + FileProtocol + ",\"mod\":" + JsonSerializer.Serialize(mod) + ",\"game\":" + JsonSerializer.Serialize(game) + ",\"smapi\":" + JsonSerializer.Serialize(smapi);
        this.header = this.baseHeader + ",\"controls\":" + ControlsJson(false);
        this.heartbeatMs = heartbeatMs;
        this.tickingWithinMs = tickingWithinMs;
        this.log = log;
    }

    public static string DefaultDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewDeck");

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string Compose(string header, long at, long seq, bool ticking, bool running, string state)
    {
        var sb = new StringBuilder(state.Length + header.Length + 96);
        sb.Append('{').Append(header)
            .Append(",\"at\":").Append(at)
            .Append(",\"seq\":").Append(seq)
            .Append(",\"ticking\":").Append(ticking ? "true" : "false")
            .Append(",\"running\":").Append(running ? "true" : "false")
            .Append(",\"state\":").Append(state)
            .Append(",\"end\":true}");
        return sb.ToString();
    }

    public int InPlaceWrites { get; private set; }

    public string Header => this.header;

    public static string ControlsJson(bool on, int port = 0, string? key = null, string? error = null)
    {
        if (!on) return "{\"on\":false}";
        var sb = new StringBuilder("{\"on\":true,\"port\":").Append(port);
        if (key is not null) sb.Append(",\"key\":").Append(JsonSerializer.Serialize(key));
        if (error is not null) sb.Append(",\"error\":").Append(JsonSerializer.Serialize(error));
        return sb.Append('}').ToString();
    }

    public void SetControls(string controlsJson)
    {
        this.header = this.baseHeader + ",\"controls\":" + controlsJson;
        if (this.running) this.wake.Set();
    }

    public void Begin(string state)
    {
        if (!string.IsNullOrEmpty(state))
        {
            lock (this.gate)
            {
                this.body = state;
                this.seq++;
                this.postedAt = NowMs();
            }
            this.WriteNow(running: true);
        }
        this.Start();
    }

    public void Start()
    {
        if (this.running) return;
        this.running = true;
        this.thread = new Thread(this.Loop) { IsBackground = true, Name = "StardewDeckBridge state file" };
        this.thread.Start();
    }

    public void Post(string state)
    {
        if (string.IsNullOrEmpty(state)) return;
        lock (this.gate)
        {
            this.body = state;
            this.seq++;
            this.postedAt = NowMs();
        }
        this.wake.Set();
    }

    private void Loop()
    {
        while (this.running)
        {
            bool posted = this.wake.WaitOne(this.heartbeatMs);
            if (!this.running) break;
            if (!posted && NowMs() - Interlocked.Read(ref this.lastWriteAt) < this.heartbeatMs - 50) continue;
            this.WriteNow(running: true);
        }
    }

    public void WriteNow(bool running)
    {
        string? state;
        long s, posted;
        lock (this.gate) { state = this.body; s = this.seq; posted = this.postedAt; }
        if (state is null) return;
        long now = NowMs();
        string json = Compose(this.header, now, s, running && now - posted < this.tickingWithinMs, running, state);
        lock (this.writeGate)
        {
            this.WriteState(json);
            Interlocked.Exchange(ref this.lastWriteAt, now);
        }
    }

    public void WriteState(string json)
    {
        bool swapped = false;
        try
        {
            Directory.CreateDirectory(this.Dir);
            File.WriteAllText(this.TempPath, json, Utf8);
            for (int attempt = 0; attempt < 2 && !swapped; attempt++)
            {
                try
                {
                    if (File.Exists(this.StatePath)) File.Replace(this.TempPath, this.StatePath, null);
                    else File.Move(this.TempPath, this.StatePath);
                    swapped = true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (PlatformNotSupportedException) { break; }
                if (!swapped && attempt == 0) Thread.Sleep(5);
            }
            if (!swapped)
            {
                var bytes = Utf8.GetBytes(json);
                using var fs = new FileStream(this.StatePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(bytes, 0, bytes.Length);
                this.InPlaceWrites++;
            }
            this.loggedFailure = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!this.loggedFailure) this.log?.Invoke($"Could not write {this.StatePath}: {ex.Message}");
            this.loggedFailure = true;
        }
        finally
        {
            if (!swapped)
            {
                try { if (File.Exists(this.TempPath)) File.Delete(this.TempPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public void Dispose()
    {
        if (!this.running && this.thread is null) return;
        this.running = false;
        this.wake.Set();
        this.thread?.Join(2000);
        this.thread = null;
        try { this.WriteNow(running: false); } catch { }
    }
}
