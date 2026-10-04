using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace StardewDeckBridge;

internal static class Commands
{
    private static readonly Dictionary<string, int> MenuTabs = new()
    {
        ["inventory"] = GameMenu.inventoryTab,
        ["skills"] = GameMenu.skillsTab,
        ["social"] = GameMenu.socialTab,
        ["map"] = GameMenu.mapTab,
        ["crafting"] = GameMenu.craftingTab,
        ["animals"] = GameMenu.animalsTab,
        ["powers"] = GameMenu.powersTab,
        ["collections"] = GameMenu.collectionsTab,
        ["options"] = GameMenu.optionsTab,
    };

    private const int MaxGiftItems = 40;

    public static string? Run(string name, JsonElement args, bool allowActions, out object? data)
    {
        data = null;
        if (name == "refresh") return null;
        if (!Context.IsWorldReady) return "not in the world";

        if (name == "giftTastes")
        {
            data = GiftTastes(Str(args, "npc"), out var giftError);
            return giftError ?? (data is null ? "no gift data" : null);
        }

        if (!allowActions) return "actions are off";
        switch (name)
        {
            case "openMenu":
                return OpenMenu(Str(args, "menu"));

            case "closeMenu":
                if (Game1.activeClickableMenu is null) return "no menu open";
                Game1.exitActiveMenu();
                return null;

            case "toolbar":
                if (Game1.activeClickableMenu is not null || Game1.eventUp) return "not now";
                if (Int(args, "slot") is int slot)
                {
                    if (slot < 0 || slot > 11) return "no such slot";
                    Game1.player.CurrentToolIndex = slot;
                    return null;
                }
                var dir = Str(args, "dir");
                if (dir == "next" || dir == "prev")
                {
                    int i = Game1.player.CurrentToolIndex % 12;
                    Game1.player.CurrentToolIndex = (i + (dir == "next" ? 1 : 11)) % 12;
                    return null;
                }
                if (Str(args, "row") == "next")
                {
                    Game1.player.shiftToolbar(true);
                    return null;
                }
                return "bad toolbar request";

            case "zoom":
                return Zoom(Str(args, "dir"));

            case "volume":
                return Audio.Change(Str(args, "channel"), Int(args, "delta"), Int(args, "set"), Str(args, "mute") == "toggle");

            case "todoAdd":
                return Str(args, "text") is { } text ? Todos.Add(text) : Todos.OpenMenu();

            case "todoMenu":
                return Todos.OpenMenu();

            case "todoEdit":
                return Int(args, "id") is int editId ? Todos.Edit(editId, Str(args, "text")) : "no to-do given";

            case "todoToggle":
                return Int(args, "id") is int toggleId ? Todos.Toggle(toggleId) : "no to-do given";

            case "todoRemove":
                return Int(args, "id") is int removeId ? Todos.Remove(removeId) : "no to-do given";

            case "todoClearDone":
                return Todos.ClearDone();

            case "hud":
                Game1.displayHUD = !Game1.displayHUD;
                return null;

            case "cheat":
                return Cheats.Run(Str(args, "what"), Int(args, "amount"), Str(args, "to"));

            case "screenshot":
            {
                if (Game1.activeClickableMenu is not null || Game1.eventUp) return "not now";
                float? scale = Dbl(args, "scale") is double d ? (float)Math.Clamp(d, 0.1, 1) : null;
                string file = Game1.game1.takeMapScreenshot(scale, null, null);
                if (string.IsNullOrEmpty(file)) return "screenshot failed";
                Game1.addHUDMessage(HUDMessage.ForCornerTextbox("Screenshot saved"));
                data = new { file };
                return null;
            }

            default:
                return "unknown command";
        }
    }

    private static string? OpenMenu(string? menu)
    {
        if (menu is null) return "no menu given";
        if (Game1.eventUp || Game1.isFestival() || !Context.CanPlayerMove && Game1.activeClickableMenu is null) return "not now";

        IClickableMenu? open = menu switch
        {
            "journal" or "quests" => new QuestLog(),
            "calendar" => new Billboard(false),
            _ when MenuTabs.TryGetValue(menu, out var tab) => new GameMenu(tab),
            _ => null,
        };
        if (open is null) return "unknown menu";

        if (Game1.activeClickableMenu is not null) Game1.exitActiveMenu();
        Game1.activeClickableMenu = open;
        Game1.playSound("bigSelect");
        return null;
    }

    private static string? Zoom(string? dir)
    {
        const float step = 0.05f, min = 0.75f, max = 2f;
        float current = Game1.options.desiredBaseZoomLevel;
        float next = dir switch
        {
            "in" => Math.Min(max, current + step),
            "out" => Math.Max(min, current - step),
            "reset" => 1f,
            _ => float.NaN,
        };
        if (float.IsNaN(next)) return "bad zoom request";
        Game1.options.desiredBaseZoomLevel = (float)Math.Round(next, 2);
        Game1.game1.refreshWindowSettings();
        return null;
    }

    private static object? GiftTastes(string? npcName, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(npcName)) return null;
        string mode = ModEntry.Config.GiftTastes ?? "all";
        if (mode == "off") { error = "gift tastes are off"; return null; }
        var tastes = DataLoader.NpcGiftTastes(Game1.content);
        if (!tastes.TryGetValue(npcName, out var raw)) return null;
        var fields = raw.Split('/');
        var npc = Game1.getCharacterFromName(npcName, true, false);
        string displayName = npc?.displayName ?? npcName;
        bool revealedOnly = mode == "revealed" && npc is not null;
        int hidden = 0;

        tastes.TryGetValue("Universal_Love", out var universal);
        var personalLoves = Items(fields.Length > 1 ? fields[1] : "", npc, revealedOnly, ref hidden);
        var loveNames = new HashSet<string>(personalLoves.Select(i => (string)i["name"]!));
        var universalLoves = Items(universal ?? "", npc, revealedOnly, ref hidden).Where(i => !loveNames.Contains((string)i["name"]!)).ToList();
        var likes = Items(fields.Length > 3 ? fields[3] : "", npc, revealedOnly, ref hidden);
        return new { npc = displayName, loves = personalLoves, universalLoves, likes, hidden, mode };
    }

    private static List<Dictionary<string, object?>> Items(string ids, NPC? npc, bool revealedOnly, ref int hidden)
    {
        var list = new List<Dictionary<string, object?>>();
        var player = Game1.player;
        foreach (var token in ids.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (list.Count >= MaxGiftItems) break;
            if (int.TryParse(token, out int number) && number < 0)
            {
                if (revealedOnly) { hidden++; continue; }
                string category = SObject.GetCategoryDisplayName(number);
                if (string.IsNullOrEmpty(category)) continue;
                list.Add(new Dictionary<string, object?>
                {
                    ["name"] = "any " + category.ToLowerInvariant(),
                    ["inBag"] = player.Items.Any(i => i is SObject o && o.Category == number),
                });
                continue;
            }
            string qid = ItemRegistry.QualifyItemId(token) ?? token;
            var data = ItemRegistry.GetDataOrErrorItem(qid);
            if (data.IsErrorItem) continue;
            if (revealedOnly && npc is not null && !player.hasGiftTasteBeenRevealed(npc, data.ItemId)) { hidden++; continue; }
            list.Add(new Dictionary<string, object?>
            {
                ["name"] = data.DisplayName,
                ["inBag"] = player.Items.Any(i => i is not null && i.QualifiedItemId == qid),
            });
        }
        return list;
    }

    private static string? Str(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Dbl(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static int? Int(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
