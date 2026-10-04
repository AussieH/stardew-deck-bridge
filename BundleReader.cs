using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using SObject = StardewValley.Object;

namespace StardewDeckBridge;

internal static class BundleReader
{
    public static Dictionary<string, object?>? Cache { get; private set; }

    public static HashSet<string> Needed { get; private set; } = new();

    public static void Refresh(IMonitor monitor)
    {
        if (!Context.IsWorldReady || Game1.player is null) { Cache = null; Needed = new(); return; }
        try
        {
            var player = Game1.player;
            var needed = new HashSet<string>();
            bool joja = Game1.MasterPlayer.mailReceived.Contains("JojaMember");
            var data = Game1.netWorldState.Value.BundleData;
            if (Game1.getLocationFromName("CommunityCenter") is not CommunityCenter cc || data is null || data.Count == 0)
            {
                Cache = new Dictionary<string, object?> { ["available"] = false, ["joja"] = joja, ["complete"] = false, ["rooms"] = Array.Empty<object>() };
                return;
            }

            var progress = cc.bundlesDict();
            var rooms = new SortedDictionary<int, (string Id, List<Dictionary<string, object?>> Bundles)>();

            foreach (var (key, value) in data)
            {
                var keyParts = key.Split('/');
                if (keyParts.Length < 2 || !int.TryParse(keyParts[1], out int index)) continue;
                string roomId = keyParts[0];
                int roomNumber = CommunityCenter.getAreaNumberFromName(roomId);

                var f = value.Split('/');
                string name = f.Length > 6 && f[6].Length > 0 ? f[6] : f[0];
                var tokens = f.Length > 2 ? f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
                int itemCount = tokens.Length / 3;
                progress.TryGetValue(index, out var done);

                var items = new List<Dictionary<string, object?>>();
                int doneCount = 0, readyCount = 0;
                for (int i = 0; i < itemCount; i++)
                {
                    string id = tokens[i * 3];
                    int.TryParse(tokens[i * 3 + 1], out int qty);
                    int.TryParse(tokens[i * 3 + 2], out int quality);
                    bool isDone = done is not null && i < done.Length && done[i];
                    if (isDone) doneCount++;

                    string itemName;
                    bool inBag;
                    if (id == "-1")
                    {
                        itemName = $"{qty:N0}g";
                        inBag = !isDone && player.Money >= qty;
                    }
                    else
                    {
                        string qid = ItemRegistry.QualifyItemId(id) ?? id;
                        itemName = ItemRegistry.GetDataOrErrorItem(qid).DisplayName;
                        inBag = !isDone && CountInBag(player, qid, quality) >= Math.Max(1, qty);
                        if (!isDone && !cc.isBundleComplete(index)) needed.Add(qid);
                    }
                    if (inBag) readyCount++;
                    items.Add(new Dictionary<string, object?>
                    {
                        ["id"] = id, ["name"] = itemName, ["qty"] = qty, ["quality"] = quality, ["done"] = isDone, ["inBag"] = inBag,
                    });
                }

                int required = f.Length > 4 && int.TryParse(f[4], out var r) && r > 0 ? Math.Min(r, itemCount) : itemCount;
                bool complete = cc.isBundleComplete(index) || doneCount >= required;

                if (!rooms.TryGetValue(roomNumber, out var room))
                    rooms[roomNumber] = room = (roomId, new List<Dictionary<string, object?>>());
                room.Bundles.Add(new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["name"] = name,
                    ["room"] = roomId,
                    ["required"] = required,
                    ["done"] = Math.Min(doneCount, required),
                    ["ready"] = complete ? 0 : readyCount,
                    ["complete"] = complete,
                    ["items"] = items,
                });
            }

            var roomList = rooms.Select(kv => new Dictionary<string, object?>
            {
                ["id"] = kv.Value.Id,
                ["name"] = kv.Key >= 0 ? CommunityCenter.getAreaDisplayNameFromNumber(kv.Key) : kv.Value.Id,
                ["complete"] = kv.Key >= 0 && kv.Key < cc.areasComplete.Count && cc.areasComplete[kv.Key],
                ["bundles"] = kv.Value.Bundles.OrderBy(b => (int)b["index"]!).ToList(),
            }).ToList();

            Needed = needed;
            Cache = new Dictionary<string, object?>
            {
                ["available"] = !joja,
                ["joja"] = joja,
                ["complete"] = cc.areAllAreasComplete(),
                ["rooms"] = roomList,
            };
        }
        catch (Exception ex)
        {
            monitor.Log($"Bundle info failed: {ex.Message}", LogLevel.Trace);
        }
    }

    private static int CountInBag(Farmer player, string qualifiedId, int minQuality)
    {
        int count = 0;
        foreach (var item in player.Items)
        {
            if (item is null || item.QualifiedItemId != qualifiedId) continue;
            if (item is SObject obj && obj.Quality < minQuality) continue;
            count += item.Stack;
        }
        return count;
    }
}
