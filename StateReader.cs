using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewDeckBridge;

internal static class StateReader
{
    private static readonly string[] Weekdays = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] Seasons = { "spring", "summer", "fall", "winter" };

    public static Dictionary<string, object?> FarmCounts { get; private set; } = EmptyFarm();

    public static Dictionary<string, object?> EmptyFarm() => new()
    {
        ["cropsReady"] = null, ["cropsUnwatered"] = null, ["machinesReady"] = null, ["animalProducts"] = null, ["animalsUnpetted"] = null,
    };

    public static Dictionary<string, object?> Read(IMonitor monitor)
    {
        var s = new Dictionary<string, object?>
        {
            ["inWorld"] = Context.IsWorldReady,
            ["paused"] = null, ["multiplayer"] = null, ["players"] = null,
            ["time"] = null, ["day"] = null, ["season"] = null, ["year"] = null, ["weekday"] = null,
            ["weather"] = null, ["weatherTomorrow"] = null, ["festival"] = null,
            ["player"] = null, ["birthdays"] = Array.Empty<object>(), ["birthdaysTomorrow"] = Array.Empty<string>(),
            ["farm"] = EmptyFarm(), ["mail"] = null, ["quests"] = null,
            ["calendar"] = Array.Empty<object>(), ["friends"] = Array.Empty<object>(), ["zoom"] = null,
            ["audio"] = null, ["pets"] = Array.Empty<object>(), ["todos"] = Array.Empty<object>(), ["mailbox"] = Array.Empty<object>(),
            ["summary"] = null, ["skills"] = Array.Empty<object>(), ["buffs"] = Array.Empty<object>(), ["farmers"] = Array.Empty<object>(),
            ["cart"] = null, ["shipping"] = null, ["museum"] = null, ["questList"] = Array.Empty<object>(), ["events"] = Array.Empty<object>(),
            ["cheats"] = Cheats.State(),
        };
        if (!Context.IsWorldReady || Game1.player is null) return s;

        Try(monitor, "clock", () =>
        {
            s["paused"] = Game1.paused || !Game1.shouldTimePass();
            s["multiplayer"] = Context.IsMultiplayer;
            s["players"] = Game1.getOnlineFarmers().Count;
            s["time"] = Game1.timeOfDay;
            s["day"] = Game1.dayOfMonth;
            s["season"] = Game1.currentSeason;
            s["year"] = Game1.year;
            s["weekday"] = Weekdays[Game1.dayOfMonth % 7];
            s["zoom"] = Math.Round(Game1.options.desiredBaseZoomLevel, 2);
        });
        Try(monitor, "weather", () =>
        {
            s["weather"] = CurrentWeather();
            s["weatherTomorrow"] = WeatherName(Game1.weatherForTomorrow);
        });
        Try(monitor, "festival", () => s["festival"] = Festival());
        Try(monitor, "player", () => s["player"] = PlayerInfo(Game1.player));
        Try(monitor, "birthdays", () =>
        {
            var (today, tomorrow) = Birthdays();
            s["birthdays"] = today;
            s["birthdaysTomorrow"] = tomorrow;
        });
        Try(monitor, "journal", () =>
        {
            s["mail"] = Game1.player.mailbox.Count;
            s["mailbox"] = MailTitles();
            s["quests"] = Game1.player.questLog.Count;
        });
        Try(monitor, "friends", () =>
        {
            var friends = Friends();
            s["friends"] = friends;
            EventReader.RefreshIfFriendshipChanged(monitor, friends.Sum(f => (int)f["points"]!));
        });
        Try(monitor, "mine", () => s["mine"] = MineReader.Read());
        Try(monitor, "audio", () => s["audio"] = Audio.Read());
        Try(monitor, "pets", () => s["pets"] = Audio.Pets());
        Try(monitor, "skills", () => s["skills"] = SkillReader.Read());
        Try(monitor, "buffs", () => s["buffs"] = BuffReader.Read());
        Try(monitor, "farmers", () => s["farmers"] = PlayerReader.Read());
        s["todos"] = Todos.Snapshot();
        s["farm"] = FarmCounts;
        s["calendar"] = Calendar;
        s["fishing"] = FishReader.Cache;
        s["bundles"] = BundleReader.Cache;
        s["summary"] = DayReader.Cache;
        s["cart"] = CartReader.Cache;
        s["shipping"] = ShippingReader.Cache;
        s["museum"] = MuseumReader.Cache;
        s["questList"] = QuestReader.Cache;
        s["events"] = EventReader.Cache;
        return s;
    }

    public static List<Dictionary<string, object?>> Calendar { get; private set; } = new();

    public static void RefreshCalendar(IMonitor monitor, int days = 28)
    {
        if (!Context.IsWorldReady) { Calendar = new(); return; }
        try
        {
            var dates = Game1.content.Load<Dictionary<string, string>>("Data\\Festivals\\FestivalDates");
            var passive = DataLoader.PassiveFestivals(Game1.content);
            var villagers = new List<NPC>();
            Utility.ForEachVillager(npc => { if (npc.CanSocialize && npc.Birthday_Season is not null) villagers.Add(npc); return true; }, false);

            var result = new List<Dictionary<string, object?>>();
            int day = Game1.dayOfMonth, seasonIndex = Game1.seasonIndex, year = Game1.year;
            for (int offset = 0; offset < days; offset++)
            {
                if (offset > 0 && ++day > 28) { day = 1; seasonIndex++; if (seasonIndex > 3) { seasonIndex = 0; year++; } }
                string season = Seasons[seasonIndex];
                var events = new List<Dictionary<string, object?>>();
                void Add(string kind, string name) => events.Add(new Dictionary<string, object?> { ["kind"] = kind, ["name"] = name });

                if (dates.TryGetValue(season + day, out var festival)) Add("festival", festival);
                foreach (var data in passive.Values)
                {
                    if (!data.ShowOnCalendar || (int)data.Season != seasonIndex || day < data.StartDay || day > data.EndDay) continue;
                    Add("festival", StardewValley.TokenizableStrings.TokenParser.ParseText(data.DisplayName));
                }
                foreach (var npc in villagers)
                    if (npc.Birthday_Day == day && npc.Birthday_Season!.Equals(season, StringComparison.OrdinalIgnoreCase))
                        Add("birthday", npc.displayName);

                int weekday = day % 7;
                if (weekday == 5 || weekday == 0) Add("cart", "Traveling cart");
                if (weekday == 0) Add("tv", "Queen of Sauce");
                if (weekday == 3) Add("tv", "Queen of Sauce rerun");

                result.Add(new Dictionary<string, object?>
                {
                    ["offset"] = offset, ["day"] = day, ["season"] = season, ["year"] = year, ["weekday"] = Weekdays[weekday], ["events"] = events,
                });
            }
            Calendar = result;
        }
        catch (Exception ex)
        {
            monitor.Log($"Calendar failed: {ex.Message}", LogLevel.Trace);
        }
    }

    private const int MaxFriends = 150;

    private static List<Dictionary<string, object?>> Friends()
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var name in Game1.player.friendshipData.Keys)
        {
            if (list.Count >= MaxFriends) break;
            var npc = Game1.getCharacterFromName(name, true, false);
            if (npc is null || !npc.CanSocialize) continue;
            var f = Game1.player.friendshipData[name];
            string status = f.Status switch
            {
                FriendshipStatus.Dating => "dating",
                FriendshipStatus.Engaged => "engaged",
                FriendshipStatus.Married => f.RoommateMarriage ? "roommate" : "married",
                FriendshipStatus.Divorced => "divorced",
                _ => "friend",
            };
            int maxHearts = status is "married" or "roommate" ? 14 : npc.datable.Value && status == "friend" ? 8 : 10;
            list.Add(new Dictionary<string, object?>
            {
                ["id"] = npc.Name,
                ["name"] = npc.displayName,
                ["points"] = f.Points,
                ["hearts"] = Math.Min(maxHearts, f.Points / 250),
                ["maxHearts"] = maxHearts,
                ["status"] = status,
                ["datable"] = npc.datable.Value,
                ["talked"] = f.TalkedToToday,
                ["giftsToday"] = f.GiftsToday,
                ["giftsWeek"] = f.GiftsThisWeek,
                ["birthday"] = npc.isBirthday(),
                ["location"] = ModEntry.Config.ShowVillagerLocations ? npc.currentLocation?.DisplayName : null,
            });
        }
        return list;
    }

    private static List<Dictionary<string, object?>> MailTitles()
    {
        var list = new List<Dictionary<string, object?>>();
        Dictionary<string, string>? mail = null;
        try { mail = Game1.content.Load<Dictionary<string, string>>("Data\\mail"); } catch { }
        foreach (string key in Game1.player.mailbox)
        {
            if (list.Count >= 10) break;
            string? title = null;
            if (mail is not null && mail.TryGetValue(key, out var text))
            {
                int at = text.LastIndexOf("[#]", StringComparison.Ordinal);
                if (at >= 0) title = text[(at + 3)..].Trim();
            }
            list.Add(new Dictionary<string, object?> { ["title"] = string.IsNullOrWhiteSpace(title) ? null : title });
        }
        return list;
    }

    private static string CurrentWeather()
    {
        if (Game1.isGreenRain) return "greenRain";
        if (Game1.isLightning) return "storm";
        if (Game1.isRaining) return "rain";
        if (Game1.isSnowing) return "snow";
        if (Game1.isDebrisWeather) return "wind";
        return "sun";
    }

    private static string WeatherName(string? id) => id switch
    {
        null or "" or "Sun" => "sun",
        "Rain" => "rain",
        "Storm" => "storm",
        "Snow" => "snow",
        "Wind" => "wind",
        "GreenRain" => "greenRain",
        "Festival" => "festival",
        "Wedding" => "wedding",
        _ => id,
    };

    private static Dictionary<string, object?>? Festival()
    {
        var dates = Game1.content.Load<Dictionary<string, string>>("Data\\Festivals\\FestivalDates");
        foreach (var (offset, today) in new[] { (0, true), (1, false) })
        {
            int day = Game1.dayOfMonth + offset;
            int seasonIndex = Game1.seasonIndex;
            if (day > 28) { day = 1; seasonIndex = (seasonIndex + 1) % 4; }
            string key = Seasons[seasonIndex] + day;
            if (!dates.TryGetValue(key, out var name)) continue;

            int? start = null, end = null;
            try
            {
                var data = Game1.content.Load<Dictionary<string, string>>("Data\\Festivals\\" + key);
                if (data.TryGetValue("conditions", out var cond))
                {
                    var parts = cond.Split('/');
                    if (parts.Length > 1)
                    {
                        var times = parts[1].Split(' ');
                        if (times.Length >= 2 && int.TryParse(times[0], out var a) && int.TryParse(times[1], out var b)) { start = a; end = b; }
                    }
                }
            }
            catch { }

            return new Dictionary<string, object?> { ["name"] = name, ["today"] = today, ["start"] = start, ["end"] = end };
        }
        return null;
    }

    private static Dictionary<string, object?> PlayerInfo(Farmer p)
    {
        var tool = p.CurrentTool;
        var can = tool as WateringCan;
        return new Dictionary<string, object?>
        {
            ["name"] = p.Name,
            ["money"] = p.Money,
            ["energy"] = (int)Math.Round(p.Stamina),
            ["maxEnergy"] = p.MaxStamina,
            ["health"] = p.health,
            ["maxHealth"] = p.maxHealth,
            ["luck"] = Math.Round(p.DailyLuck, 4),
            ["luckText"] = LuckText(p.DailyLuck),
            ["location"] = p.currentLocation?.DisplayName,
            ["tool"] = tool?.DisplayName ?? p.CurrentItem?.DisplayName,
            ["toolLevel"] = tool?.UpgradeLevel,
            ["water"] = can?.WaterLeft,
            ["maxWater"] = can?.waterCanMax,
            ["toolbarSlot"] = p.CurrentToolIndex,
            ["skills"] = new Dictionary<string, int>
            {
                ["farming"] = p.FarmingLevel, ["fishing"] = p.FishingLevel, ["foraging"] = p.ForagingLevel,
                ["mining"] = p.MiningLevel, ["combat"] = p.CombatLevel,
            },
            ["deepestMine"] = p.deepestMineLevel,
        };
    }

    public static string LuckText(double luck) => luck switch
    {
        > 0.07 => "very lucky",
        > 0.02 => "lucky",
        >= -0.02 => "neutral",
        >= -0.07 => "unlucky",
        _ => "very unlucky",
    };

    private static (List<Dictionary<string, object?>> Today, List<string> Tomorrow) Birthdays()
    {
        var today = new List<Dictionary<string, object?>>();
        var tomorrow = new List<string>();
        int tDay = Game1.dayOfMonth + 1;
        int tSeason = Game1.seasonIndex;
        if (tDay > 28) { tDay = 1; tSeason = (tSeason + 1) % 4; }

        Utility.ForEachVillager(npc =>
        {
            if (!npc.CanSocialize || npc.Birthday_Season is null) return true;
            string season = npc.Birthday_Season.ToLowerInvariant();
            if (npc.Birthday_Day == Game1.dayOfMonth && season == Game1.currentSeason)
            {
                bool gifted = Game1.player.friendshipData.TryGetValue(npc.Name, out var f) && f.GiftsToday > 0;
                today.Add(new Dictionary<string, object?> { ["name"] = npc.displayName, ["gifted"] = gifted });
            }
            else if (npc.Birthday_Day == tDay && season == Seasons[tSeason])
            {
                tomorrow.Add(npc.displayName);
            }
            return true;
        });
        return (today, tomorrow);
    }

    public static void RefreshFarmCounts(IMonitor monitor)
    {
        if (!Context.IsWorldReady) { FarmCounts = EmptyFarm(); return; }
        try
        {
            int cropsReady = 0, unwatered = 0, machines = 0, products = 0, unpetted = 0;
            var farm = Game1.getFarm();
            var places = new List<GameLocation> { farm };
            var greenhouse = Game1.getLocationFromName("Greenhouse");
            if (greenhouse is not null) places.Add(greenhouse);
            foreach (Building b in farm.buildings)
                if (b.indoors.Value is { } inside) places.Add(inside);

            foreach (var loc in places)
            {
                foreach (var feature in loc.terrainFeatures.Values)
                {
                    if (feature is not HoeDirt dirt || dirt.crop is null) continue;
                    if (dirt.readyForHarvest()) cropsReady++;
                    else if (dirt.needsWatering() && dirt.state.Value != HoeDirt.watered && !loc.IsRainingHere() && !dirt.crop.dead.Value) unwatered++;
                }
                foreach (var obj in loc.objects.Values)
                {
                    if (obj is IndoorPot pot && pot.hoeDirt.Value?.crop is not null)
                    {
                        if (pot.hoeDirt.Value.readyForHarvest()) cropsReady++;
                        else if (pot.hoeDirt.Value.needsWatering() && pot.hoeDirt.Value.state.Value != HoeDirt.watered) unwatered++;
                        continue;
                    }
                    if (obj.bigCraftable.Value && obj.readyForHarvest.Value && obj.heldObject.Value is not null) machines++;
                    else if (loc is AnimalHouse && !obj.bigCraftable.Value && IsAnimalProduct(obj)) products++;
                }
            }
            foreach (var animal in farm.getAllFarmAnimals())
            {
                if (!animal.wasPet.Value) unpetted++;
            }

            FarmCounts = new Dictionary<string, object?>
            {
                ["cropsReady"] = cropsReady, ["cropsUnwatered"] = unwatered, ["machinesReady"] = machines,
                ["animalProducts"] = products, ["animalsUnpetted"] = unpetted,
            };
        }
        catch (Exception ex)
        {
            monitor.Log($"Farm counts failed: {ex.Message}", LogLevel.Trace);
        }
    }

    private static bool IsAnimalProduct(SObject obj) =>
        obj.Category is SObject.EggCategory or SObject.MilkCategory or SObject.sellAtPierresAndMarnies;

    private static void Try(IMonitor monitor, string what, Action read)
    {
        try { read(); }
        catch (Exception ex) { monitor.LogOnce($"Could not read {what}: {ex.Message}", LogLevel.Trace); }
    }
}
