using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace StardewDeckBridge;

internal static class Cheats
{
    public static bool TimeFrozen { get; private set; }

    private static readonly Dictionary<string, (string Location, int X, int Y, string Name)> Warps = new()
    {
        ["farm"] = ("Farm", 64, 15, "Farm"),
        ["town"] = ("Town", 43, 57, "Pelican Town"),
        ["beach"] = ("Beach", 38, 4, "Beach"),
        ["mountain"] = ("Mountain", 31, 20, "Mountain"),
        ["forest"] = ("Forest", 34, 13, "Cindersap Forest"),
        ["mine"] = ("Mine", 13, 10, "The Mines"),
        ["desert"] = ("Desert", 35, 43, "Calico Desert"),
        ["railroad"] = ("Railroad", 29, 58, "Railroad"),
    };

    public static Dictionary<string, object?> State() => new()
    {
        ["enabled"] = ModEntry.Config.EnableCheats,
        ["timeFrozen"] = TimeFrozen && ModEntry.Config.EnableCheats,
    };

    public static void Tick()
    {
        if (!TimeFrozen) return;
        if (!ModEntry.Config.EnableCheats || !Context.IsWorldReady) { TimeFrozen = false; return; }
        Game1.gameTimeInterval = 0;
    }

    public static string? Run(string? what, int? amount, string? to)
    {
        if (!ModEntry.Config.EnableCheats) return "cheats are off";
        var player = Game1.player;
        switch (what)
        {
            case "money":
            {
                int add = amount ?? 1000;
                player.Money = Math.Max(0, player.Money + add);
                Sound("purchase");
                return null;
            }
            case "heal":
                player.Stamina = player.MaxStamina;
                player.health = player.maxHealth;
                Sound("healSound");
                return null;

            case "freeze":
                TimeFrozen = !TimeFrozen;
                Sound(TimeFrozen ? "coldSpell" : "wand");
                return null;

            case "time":
            {
                if (Game1.eventUp || Game1.activeClickableMenu is not null) return "not now";
                int minutes = amount ?? 60;
                int next = AddMinutes(Game1.timeOfDay, minutes);
                if (next < 600 || next > 2550) return "keep it between 6 am and 1:50 am";
                int steps = Math.Abs(minutes) / 10;
                if (minutes > 0) for (int i = 0; i < steps; i++) Game1.performTenMinuteClockUpdate();
                else Game1.timeOfDay = next;
                return null;
            }
            case "weather":
            {
                if (!Context.IsMainPlayer) return "host only";
                string id = (to ?? "sun").ToLowerInvariant() switch
                {
                    "rain" => Game1.weather_rain, "storm" => Game1.weather_lightning, "snow" => Game1.weather_snow,
                    "wind" => Game1.weather_debris, "green" or "greenrain" => Game1.weather_green_rain, _ => Game1.weather_sunny,
                };
                Game1.weatherForTomorrow = id;
                Game1.netWorldState.Value.WeatherForTomorrow = id;
                Sound("thunder_small");
                return null;
            }
            case "grow":
            case "water":
            {
                if (!Context.IsMainPlayer && !Context.IsWorldReady) return "not now";
                int n = 0;
                foreach (var loc in CropLocations())
                {
                    foreach (var feature in loc.terrainFeatures.Values)
                    {
                        if (feature is not HoeDirt dirt) continue;
                        if (what == "water") { if (dirt.crop is not null) { dirt.state.Value = HoeDirt.watered; n++; } }
                        else if (dirt.crop is not null && !dirt.crop.fullyGrown.Value && !dirt.crop.dead.Value) { dirt.crop.growCompletely(); n++; }
                    }
                    foreach (var obj in loc.objects.Values)
                    {
                        if (obj is not StardewValley.Objects.IndoorPot pot || pot.hoeDirt.Value?.crop is null) continue;
                        if (what == "water") { pot.hoeDirt.Value.state.Value = HoeDirt.watered; n++; }
                        else if (!pot.hoeDirt.Value.crop.fullyGrown.Value) { pot.hoeDirt.Value.crop.growCompletely(); n++; }
                    }
                }
                Sound(what == "water" ? "wateringCan" : "reward");
                return n == 0 ? "no crops found" : null;
            }
            case "warp":
            {
                if (!Context.IsPlayerFree) return "not now";
                if (to is null || !Warps.TryGetValue(to.ToLowerInvariant(), out var w)) return "no such place";
                if (w.Location == "Desert" && !Game1.MasterPlayer.mailReceived.Contains("ccVault")) return "bus not fixed yet";
                if (Game1.getLocationFromName(w.Location) is null) return "no such place";
                Game1.warpFarmer(w.Location, w.X, w.Y, false);
                Sound("wand");
                return null;
            }
            default:
                return "unknown cheat";
        }
    }

    private static void Sound(string cue)
    {
        if (ModEntry.Config.CheatSounds) Game1.playSound(cue);
    }

    private static int AddMinutes(int time, int minutes)
    {
        int total = (time / 100) * 60 + time % 100 + minutes;
        return (total / 60) * 100 + total % 60;
    }

    private static IEnumerable<GameLocation> CropLocations()
    {
        yield return Game1.getFarm();
        if (Game1.getLocationFromName("Greenhouse") is { } greenhouse) yield return greenhouse;
        if (Game1.getLocationFromName("IslandWest") is { } island) yield return island;
    }
}
