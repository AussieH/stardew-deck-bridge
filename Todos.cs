using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewDeckBridge;

public sealed class TodoItem
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

public sealed class TodoFile
{
    public int NextId { get; set; } = 1;
    public List<TodoItem> Items { get; set; } = new();
}

internal static class Todos
{
    public const int MaxItems = 60;
    public const int MaxLength = 100;

    private static IModHelper? helper;
    private static IMonitor? monitor;
    private static TodoFile file = new();
    private static string? path;

    public static void Init(IModHelper modHelper, IMonitor modMonitor)
    {
        helper = modHelper;
        monitor = modMonitor;
    }

    public static void Load()
    {
        if (helper is null || !Context.IsWorldReady) return;
        path = $"data/{Game1.uniqueIDForThisGame}-{Game1.player.UniqueMultiplayerID}.json";
        file = helper.Data.ReadJsonFile<TodoFile>(path) ?? new TodoFile();
    }

    public static void Unload()
    {
        file = new TodoFile();
        path = null;
    }

    private static void Save()
    {
        if (helper is null || path is null) return;
        try { helper.Data.WriteJsonFile(path, file); }
        catch (Exception ex) { monitor?.Log($"Could not save the to-do list: {ex.Message}", LogLevel.Warn); }
    }

    public static IReadOnlyList<TodoItem> Items => file.Items;

    public static IEnumerable<object> Snapshot() =>
        file.Items.Select(i => new Dictionary<string, object?> { ["id"] = i.Id, ["text"] = i.Text, ["done"] = i.Done }).ToList();

    public static string? Add(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return "nothing to add";
        if (file.Items.Count >= MaxItems) return "list is full";
        if (text.Length > MaxLength) text = text[..MaxLength];
        file.Items.Add(new TodoItem { Id = file.NextId++, Text = text });
        Save();
        return null;
    }

    public static string? Edit(int id, string? text)
    {
        var item = file.Items.FirstOrDefault(i => i.Id == id);
        if (item is null) return "no such to-do";
        text = (text ?? "").Trim();
        if (text.Length == 0) return "nothing to save";
        item.Text = text.Length > MaxLength ? text[..MaxLength] : text;
        Save();
        return null;
    }

    public static string? Toggle(int id)
    {
        var item = file.Items.FirstOrDefault(i => i.Id == id);
        if (item is null) return "no such to-do";
        item.Done = !item.Done;
        Save();
        return null;
    }

    public static string? Remove(int id)
    {
        if (file.Items.RemoveAll(i => i.Id == id) == 0) return "no such to-do";
        Save();
        return null;
    }

    public static string? ClearDone()
    {
        if (file.Items.RemoveAll(i => i.Done) == 0) return "nothing done yet";
        Save();
        return null;
    }

    public static string? OpenMenu()
    {
        if (Game1.activeClickableMenu is TodoMenu) return null;
        if (!Context.IsPlayerFree) return "not now";
        Game1.activeClickableMenu = new TodoMenu();
        Game1.playSound("bigSelect");
        return null;
    }

    public static void ConsoleCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady) { monitor?.Log("Load a save first.", LogLevel.Info); return; }
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        string? error = verb switch
        {
            "add" => Add(string.Join(' ', args.Skip(1))),
            "done" when args.Length > 1 && int.TryParse(args[1], out var d) => Toggle(d),
            "remove" when args.Length > 1 && int.TryParse(args[1], out var r) => Remove(r),
            "clear" => ClearDone(),
            "list" => null,
            _ => "usage: deck_todo add <text> | done <id> | remove <id> | clear | list",
        };
        if (error is not null) monitor?.Log(error, LogLevel.Info);
        foreach (var item in file.Items) monitor?.Log($"{item.Id,3} [{(item.Done ? "x" : " ")}] {item.Text}", LogLevel.Info);
    }
}
