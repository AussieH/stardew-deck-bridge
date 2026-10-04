using StardewValley;
using StardewValley.Locations;
using StardewValley.Monsters;

namespace StardewDeckBridge;

internal static class MineReader
{
    private static readonly Dictionary<string, string> Nodes = new()
    {
        ["751"] = "copper", ["849"] = "copper",
        ["290"] = "iron", ["850"] = "iron",
        ["764"] = "gold", ["VolcanoGoldNode"] = "gold",
        ["765"] = "iridium",
        ["95"] = "radioactive",
        ["843"] = "cinder", ["844"] = "cinder",
        ["2"] = "gems", ["4"] = "gems", ["6"] = "gems", ["8"] = "gems", ["10"] = "gems", ["12"] = "gems", ["14"] = "gems", ["44"] = "gems",
        ["46"] = "mystic",
        ["75"] = "geodes", ["76"] = "geodes", ["77"] = "geodes", ["819"] = "geodes",
        ["BasicCoalNode0"] = "coal", ["BasicCoalNode1"] = "coal", ["VolcanoCoalNode0"] = "coal", ["VolcanoCoalNode1"] = "coal",
    };

    private static readonly string[] Kinds = { "copper", "iron", "gold", "iridium", "radioactive", "cinder", "gems", "mystic", "geodes", "coal", "stone" };

    public static Dictionary<string, object?>? Read()
    {
        var loc = Game1.currentLocation;
        if (loc is MineShaft mine)
        {
            int level = mine.mineLevel;
            string area = level == 77377 ? "quarry" : level > 120 ? "skull" : "mines";
            string type =
                mine.isQuarryArea ? "quarry" :
                mine.isDinoArea ? "dino" :
                mine.isSlimeArea ? "slime" :
                mine.isMonsterArea ? "infested" :
                mine.mustKillAllMonstersToAdvance() ? "infested" :
                "normal";
            return new Dictionary<string, object?>
            {
                ["area"] = area,
                ["level"] = area == "skull" ? level - 120 : level,
                ["type"] = type,
                ["monsters"] = CountMonsters(loc),
                ["stones"] = mine.stonesLeftOnThisLevel,
                ["ladder"] = mine.ladderHasSpawned,
                ["mustKill"] = mine.mustKillAllMonstersToAdvance(),
                ["elevator"] = area == "mines" && level % 5 == 0,
                ["deepest"] = MineShaft.lowestLevelReached,
                ["nodes"] = ModEntry.Config.ShowMineOres ? CountNodes(loc) : null,
                ["oresHidden"] = !ModEntry.Config.ShowMineOres,
            };
        }
        if (loc is VolcanoDungeon volcano)
        {
            return new Dictionary<string, object?>
            {
                ["area"] = "volcano",
                ["level"] = volcano.level.Value,
                ["type"] = volcano.isMushroomLevel() ? "mushroom" : volcano.isMonsterLevel() ? "infested" : "normal",
                ["monsters"] = CountMonsters(loc),
                ["stones"] = null,
                ["ladder"] = null,
                ["mustKill"] = false,
                ["elevator"] = false,
                ["deepest"] = MineShaft.lowestLevelReached,
                ["nodes"] = ModEntry.Config.ShowMineOres ? CountNodes(loc) : null,
                ["oresHidden"] = !ModEntry.Config.ShowMineOres,
            };
        }
        return null;
    }

    private static int CountMonsters(GameLocation loc) => loc.characters.Count(c => c is Monster);

    private static Dictionary<string, int> CountNodes(GameLocation loc)
    {
        var counts = Kinds.ToDictionary(k => k, _ => 0);
        foreach (var obj in loc.objects.Values)
        {
            if (obj.bigCraftable.Value) continue;
            if (Nodes.TryGetValue(obj.ItemId, out var kind)) counts[kind]++;
            else if (obj.Name == "Stone") counts["stone"]++;
        }
        return counts;
    }
}
