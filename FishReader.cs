using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Locations;
using StardewValley.TokenizableStrings;
using SObject = StardewValley.Object;

namespace StardewDeckBridge;

internal static class FishReader
{
    private const int MaxDepth = 2;

    public static Dictionary<string, object?>? Cache { get; private set; }

    private sealed record Candidate(SpawnFishData Spawn, string? OuterCondition, Season? OuterSeason, LocationData? Owner, string? BorrowedFrom);

    public static void Refresh(IMonitor monitor)
    {
        if (!Context.IsWorldReady || Game1.currentLocation is null || Game1.player is null) { Cache = null; return; }
        try
        {
            var loc = Game1.currentLocation;
            var player = Game1.player;
            var allData = DataLoader.Locations(Game1.content);
            var data = loc.GetData();

            var candidates = new List<Candidate>();
            if (data?.Fish is not null) Collect(data.Fish, data, null, null, null, allData, candidates, 0);
            if (allData.TryGetValue("Default", out var shared) && shared.Fish is not null) Collect(shared.Fish, shared, null, null, null, allData, candidates, 0);

            var fishData = DataLoader.Fish(Game1.content);
            bool raining = loc.IsRainingHere();
            var seen = new HashSet<string>();
            var rows = new List<(bool Now, bool Caught, bool Legendary, string Name, Dictionary<string, object?> Row)>();

            foreach (var c in candidates)
            {
                var spawn = c.Spawn;
                string qid = ItemRegistry.QualifyItemId(spawn.ItemId) ?? spawn.ItemId;
                if (!qid.StartsWith("(O)")) continue;
                var item = ItemRegistry.GetDataOrErrorItem(qid);
                if (item.IsErrorItem) continue;
                if (item.Category != SObject.FishCategory && !spawn.IsBossFish) continue;
                if (!seen.Add(qid)) continue;

                bool seasonOk = (spawn.Season is null || spawn.Season == Game1.season) && (c.OuterSeason is null || c.OuterSeason == Game1.season);
                bool conditionOk = Check(spawn.Condition, loc, player) && Check(c.OuterCondition, loc, player);
                bool levelOk = player.FishingLevel >= spawn.MinFishingLevel;
                bool timeOk = true, weatherOk = true;
                string? times = null, weather = null;
                if (fishData.TryGetValue(item.ItemId, out var entry))
                {
                    var f = entry.Split('/');
                    if (f.Length > 7 && f[1] != "trap")
                    {
                        times = f[5];
                        weather = f[7];
                        timeOk = InTimeWindows(f[5], Game1.timeOfDay);
                        weatherOk = weather == "both" || (weather == "rainy") == raining;
                    }
                }
                bool now = seasonOk && conditionOk && levelOk && timeOk && weatherOk;
                bool caught = player.fishCaught.ContainsKey(qid);

                string? areaName = null;
                if (spawn.FishAreaId is not null && c.Owner?.FishAreas is not null && c.Owner.FishAreas.TryGetValue(spawn.FishAreaId, out var area))
                    areaName = TokenParser.ParseText(area.DisplayName ?? spawn.FishAreaId);
                areaName ??= c.BorrowedFrom;

                string shownName = caught || !ModEntry.Config.HideUncaughtFish ? item.DisplayName : "???";
                rows.Add((now, caught, spawn.IsBossFish, shownName, new Dictionary<string, object?>
                {
                    ["id"] = qid,
                    ["name"] = shownName,
                    ["legendary"] = spawn.IsBossFish,
                    ["caught"] = caught,
                    ["now"] = now,
                    ["seasonOk"] = seasonOk,
                    ["conditionOk"] = conditionOk,
                    ["levelOk"] = levelOk,
                    ["timeOk"] = timeOk,
                    ["weatherOk"] = weatherOk,
                    ["minLevel"] = spawn.MinFishingLevel,
                    ["chance"] = Math.Round(spawn.Chance, 3),
                    ["area"] = areaName,
                    ["times"] = times,
                    ["weather"] = weather,
                }));
            }

            var ordered = rows
                .OrderByDescending(r => r.Now)
                .ThenBy(r => r.Caught)
                .ThenByDescending(r => r.Legendary)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => r.Row)
                .ToList();

            Cache = new Dictionary<string, object?>
            {
                ["location"] = loc.DisplayName,
                ["fish"] = ordered,
                ["fishingLevel"] = player.FishingLevel,
                ["fishing"] = player.UsingTool && player.CurrentTool is StardewValley.Tools.FishingRod,
            };
        }
        catch (Exception ex)
        {
            monitor.Log($"Fishing info failed: {ex.Message}", LogLevel.Trace);
        }
    }

    private static void Collect(IEnumerable<SpawnFishData> spawns, LocationData owner, string? outerCondition, Season? outerSeason, string? borrowedFrom,
        IDictionary<string, LocationData> allData, List<Candidate> into, int depth)
    {
        foreach (var spawn in spawns)
        {
            string? id = spawn.ItemId?.Trim();
            if (string.IsNullOrEmpty(id)) continue;

            if (id.StartsWith("LOCATION_FISH ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = id.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (depth >= MaxDepth || parts.Length < 2 || !allData.TryGetValue(parts[1], out var other) || other.Fish is null) continue;
                string otherName = other.DisplayName is { Length: > 0 } dn ? TokenParser.ParseText(dn) : parts[1];
                Collect(other.Fish, other, Join(outerCondition, spawn.Condition), spawn.Season ?? outerSeason, otherName, allData, into, depth + 1);
                continue;
            }
            if (id.Contains(' ')) continue;
            into.Add(new Candidate(spawn, outerCondition, outerSeason, owner, borrowedFrom));
        }
    }

    private static string? Join(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) ? b : string.IsNullOrWhiteSpace(b) ? a : $"{a}, {b}";

    private static bool Check(string? condition, GameLocation loc, Farmer player) =>
        string.IsNullOrWhiteSpace(condition) || GameStateQuery.CheckConditions(condition, loc, player, null, null, null, null);

    internal static bool InTimeWindows(string spec, int time)
    {
        var parts = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return true;
        for (int i = 0; i + 1 < parts.Length; i += 2)
            if (int.TryParse(parts[i], out var start) && int.TryParse(parts[i + 1], out var end) && time >= start && time < end)
                return true;
        return false;
    }
}
