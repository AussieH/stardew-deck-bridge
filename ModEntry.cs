using System.Text.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StardewDeckBridge;

public sealed class ModEntry : Mod
{
    private const int Protocol = 1;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static ModConfig Config { get; private set; } = new();

    private ModConfig config = new();
    private BridgeServer? server;
    private StateFile? stateFile;
    private WidgetServer? widget;
    private readonly string widgetKey = WidgetServer.NewKey();
    private string lastState = "";
    private long seq;
    private int ticksSinceSend;
    private int ticksSinceFarm;
    private int ticksSinceFile;
    private bool fileSoon;

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        Config = this.config;
        if (this.config.Port is < 1024 or > 65535)
        {
            this.Monitor.Log($"Port {this.config.Port} is not usable; using 52817.", LogLevel.Warn);
            this.config.Port = 52817;
        }

        var first = StateReader.Read(this.Monitor);
        string firstBody = JsonSerializer.Serialize(first, Json);

        if (this.config.WriteStateFile)
        {
            this.stateFile = new StateFile(StateFile.DefaultDir(), this.ModManifest.Version.ToString(), Game1.version, Constants.ApiVersion.ToString(),
                msg => this.Monitor.Log(msg, LogLevel.Trace));
            this.ApplyWidgetControls();
            this.stateFile.Begin(firstBody);
            this.Monitor.Log($"Writing {this.stateFile.StatePath} for the Stardew Dashboard widget.", LogLevel.Trace);
        }

        this.server = new BridgeServer(this.config.Port, msg => this.Monitor.Log(msg, LogLevel.Warn));
        this.server.Hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            protocol = Protocol,
            mod = this.ModManifest.Version.ToString(),
            game = Game1.version,
            smapi = Constants.ApiVersion.ToString(),
            actions = this.config.AllowActions,
        }, Json);
        this.server.LastState = this.StateMessage(first, firstBody);
        if (this.server.Start())
            this.Monitor.Log($"Listening for Stream Deck on 127.0.0.1:{this.config.Port}.", LogLevel.Info);

        Todos.Init(helper, this.Monitor);
        helper.ConsoleCommands.Add("deck_cart", "Stardew Deck: what the game reports for the traveling cart's stock.", (_, _) => this.Monitor.Log(CartReader.Describe(), LogLevel.Info));
        helper.ConsoleCommands.Add("deck_todo", "Stardew Deck to-do list: deck_todo add <text> | done <id> | remove <id> | clear | list", Todos.ConsoleCommand);

        helper.Events.GameLoop.GameLaunched += (_, _) => ConfigMenu.Register(helper, this.ModManifest, () => this.config, c => { this.config = c; Config = c; }, () => { this.ApplyWidgetControls(); if (Context.IsWorldReady) this.RefreshDay(); });
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += (_, _) => { Todos.Load(); DayReader.StartDay(); this.RefreshDay(); this.fileSoon = true; };
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => Todos.Unload();
        helper.Events.GameLoop.DayStarted += (_, _) => { DayReader.StartDay(); this.RefreshDay(); this.fileSoon = true; };
        helper.Events.GameLoop.TimeChanged += (_, e) =>
        {
            FishReader.Refresh(this.Monitor);
            if (e.NewTime % 100 == 0 || e.NewTime % 100 == 30) this.RefreshFarm();
            if (e.NewTime % 100 == 0) EventReader.Refresh(this.Monitor);
        };
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => { this.RefreshDay(); this.SendState(force: true); this.fileSoon = true; };
        helper.Events.Player.Warped += (_, e) => { if (e.IsLocalPlayer) { FishReader.Refresh(this.Monitor); this.fileSoon = true; } };
        helper.Events.Player.InventoryChanged += (_, e) => { if (e.IsLocalPlayer) this.RefreshBag(); };
        helper.Events.Display.MenuChanged += (_, e) =>
        {
            if (e.OldMenu is TodoMenu closed) closed.Unsubscribe();
            if (e.NewMenu is null && Context.IsWorldReady) this.RefreshFarm();
        };
        helper.Events.Input.ButtonsChanged += (_, _) =>
        {
            if (Context.IsPlayerFree && this.config.TodoMenuKey.JustPressed()) Todos.OpenMenu();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.server?.Dispose();
            this.widget?.Dispose();
            this.stateFile?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ApplyWidgetControls()
    {
        if (this.stateFile is null) return;
        if (!this.config.WidgetControls)
        {
            if (this.widget is not null) this.Monitor.Log("Widget controls off: stopped listening on localhost:52818.", LogLevel.Info);
            this.widget?.Dispose();
            this.widget = null;
            this.stateFile.SetControls(StateFile.ControlsJson(false));
            return;
        }
        if (this.widget is not null) return;
        var w = new WidgetServer(WidgetServer.DefaultPort, this.widgetKey, this.ModManifest.Version.ToString(), msg => this.Monitor.Log(msg, LogLevel.Trace));
        w.Tick(Context.IsWorldReady, this.config.AllowActions);
        if (w.Start())
        {
            this.widget = w;
            this.stateFile.SetControls(StateFile.ControlsJson(true, w.Port, key: this.widgetKey));
            this.Monitor.Log($"Listening for the Stardew Dashboard widget on localhost:{w.Port}.", LogLevel.Info);
        }
        else
        {
            w.Dispose();
            this.stateFile.SetControls(StateFile.ControlsJson(true, WidgetServer.DefaultPort, error: "port in use"));
            this.Monitor.Log($"Could not listen for the Stardew Dashboard widget on localhost:{WidgetServer.DefaultPort}: the port is in use. Is another copy of the game running?", LogLevel.Warn);
        }
    }

    private void RefreshBag()
    {
        BundleReader.Refresh(this.Monitor);
        MuseumReader.Refresh(this.Monitor);
        ShippingReader.RefreshBag(this.Monitor);
        CartReader.RefreshFlags(this.Monitor);
    }

    private void RefreshFarm()
    {
        StateReader.RefreshFarmCounts(this.Monitor);
        DayReader.Refresh(this.Monitor);
        QuestReader.Refresh(this.Monitor);
        this.RefreshBag();
        this.ticksSinceFarm = 0;
    }

    private void RefreshDay()
    {
        StateReader.RefreshCalendar(this.Monitor);
        FishReader.Refresh(this.Monitor);
        ShippingReader.RefreshDay(this.Monitor);
        CartReader.RefreshDay(this.Monitor);
        EventReader.Refresh(this.Monitor);
        this.RefreshFarm();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (this.server is null) return;
        TodoMenu.ReleaseIfOrphaned();
        Cheats.Tick();

        while (this.server.Incoming.TryDequeue(out var item))
            this.Handle(item.Client.Send, item.Line, "Stream Deck");

        if (this.widget is { } widget)
        {
            widget.Tick(Context.IsWorldReady, this.config.AllowActions);
            while (widget.Incoming.TryDequeue(out var cmd))
                if (cmd.TryClaim()) this.Handle(cmd.Reply, cmd.Line, "iCUE widget");
        }

        if (++this.ticksSinceFarm >= 600) this.RefreshFarm();

        if (++this.ticksSinceSend >= 15 || this.fileSoon)
        {
            this.ticksSinceSend = 0;
            this.SendState(force: false);
        }

        if (this.stateFile is not null && (this.fileSoon || ++this.ticksSinceFile >= 60))
        {
            this.ticksSinceFile = 0;
            this.fileSoon = false;
            this.stateFile.Post(this.lastState);
        }
    }

    private void SendState(bool force)
    {
        if (this.server is null) return;
        var state = StateReader.Read(this.Monitor);
        string body = JsonSerializer.Serialize(state, Json);
        if (!force && body == this.lastState) return;
        this.lastState = body;
        string message = this.StateMessage(state, body);
        this.server.LastState = message;
        if (this.server.ClientCount > 0) this.server.Broadcast(message);
    }

    private string StateMessage(Dictionary<string, object?> state, string? body = null)
    {
        body ??= JsonSerializer.Serialize(state, Json);
        return "{\"type\":\"state\",\"seq\":" + (++this.seq) + ",\"state\":" + body + "}";
    }

    private void Handle(Action<string> reply, string line, string who)
    {
        long id = 0;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "command") return;
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var parsed)) id = parsed;
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var args = root.TryGetProperty("args", out var a) ? a.Clone() : default;

            if (name == "refresh" && Context.IsWorldReady) this.RefreshFarm();
            string? error = Commands.Run(name, args, this.config.AllowActions, out object? data);
            reply(JsonSerializer.Serialize(new { type = "result", id, ok = error is null, error, data }, Json));
            if (error is null && data is null)
            {
                this.Monitor.Log($"{who}: {name}", LogLevel.Trace);
                this.SendState(force: true);
            }
        }
        catch (JsonException)
        {
            reply(JsonSerializer.Serialize(new { type = "result", id, ok = false, error = "bad message" }, Json));
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Command failed: {ex.Message}", LogLevel.Trace);
            reply(JsonSerializer.Serialize(new { type = "result", id, ok = false, error = "failed in game" }, Json));
        }
    }
}
