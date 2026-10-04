using StardewModdingAPI;
using StardewValley;
using StardewValley.Internal;
using StardewValley.Locations;
using SObject = StardewValley.Object;

namespace StardewDeckBridge;

internal static class Wants
{
    public static bool NeededByBundle(string qid) => BundleReader.Needed.Contains(qid);

    public static bool NeverShipped(string qid)
    {
        if (!qid.StartsWith("(O)")) return false;
        string id = qid[3..];
        if (!Game1.objectData.TryGetValue(id, out var data) || data.ExcludeFromShippingCollection) return false;
        return SObject.isPotentialBasicShipped(id, data.Category, data.Type) && !Game1.player.basicShipped.ContainsKey(id);
    }

    public static bool NeverCaught(string qid)
    {
        if (!qid.StartsWith("(O)")) return false;
        var data = ItemRegistry.GetDataOrErrorItem(qid);
        return !data.IsErrorItem && data.Category == SObject.FishCategory && !Game1.player.fishCaught.ContainsKey(qid);
    }

    public static bool InBag(string qid) => Game1.player.Items.Any(i => i is not null && i.QualifiedItemId == qid);
}

internal static class CartReader
{
    public static Dictionary<string, object?>? Cache { get; private set; }

    private static List<(string Qid, string Name, int Price, int Stock)> stock = new();
    private static int stockDay = -1;
    private static int lastTry = -100000;

    private static List<(string Qid, string Name, int Price, int Stock)> ReadStock()
    {
        var list = new List<(string, string, int, int)>();
        foreach (var (salable, info) in ShopBuilder.GetShopStock("Traveler"))
        {
            if (salable is null) continue;
            list.Add((salable.QualifiedItemId, salable.DisplayName, info.Price, info.Stock));
            if (list.Count >= 40) break;
        }
        return list;
    }

    public static string Describe()
    {
        if (!Context.IsWorldReady) return "Load a save first.";
        var sb = new System.Text.StringBuilder();
        sb.Append($"Cart today: {CartToday()}. ");
        try
        {
            var shops = DataLoader.Shops(Game1.content);
            sb.Append(shops.TryGetValue("Traveler", out var data) ? $"Data/Shops has 'Traveler' with {data.Items?.Count ?? 0} entries. " : "Data/Shops has no 'Traveler' shop! ");
            var read = ReadStock();
            sb.Append($"GetShopStock returned {read.Count}: {string.Join(", ", read.Take(12).Select(i => $"{i.Name} {i.Price}g"))}");
        }
        catch (Exception ex) { sb.Append($"GetShopStock threw: {ex}"); }
        return sb.ToString();
    }

    public static void RefreshDay(IMonitor monitor)
    {
        if (!Context.IsWorldReady) { Cache = null; return; }
        try
        {
            stock = new();
            stockDay = Game1.Date.TotalDays;
            lastTry = Game1.ticks;
            if (CartToday())
            {
                stock = ReadStock();
                if (stock.Count == 0) monitor.LogOnce("The traveling cart is here but the game gave no stock for shop 'Traveler'; will try again. Type deck_cart in this console for details.", LogLevel.Debug);
                else monitor.Log($"Traveling cart: {stock.Count} items read.", LogLevel.Trace);
            }
        }
        catch (Exception ex) { monitor.Log($"Traveling cart failed: {ex}", LogLevel.Debug); }
        RefreshFlags(monitor);
    }

    public static void RefreshFlags(IMonitor monitor)
    {
        if (!Context.IsWorldReady) { Cache = null; return; }
        try
        {
            if (stockDay != Game1.Date.TotalDays) { RefreshDay(monitor); return; }
            bool today = CartToday();
            if (today && stock.Count == 0 && Game1.ticks - lastTry > 600) { RefreshDay(monitor); return; }
            int weekday = Game1.dayOfMonth % 7;
            int nextIn = today ? 0 : weekday == 6 ? 1 : 5 - weekday;

            var items = new List<Dictionary<string, object?>>();
            int wanted = 0;
            foreach (var (qid, name, price, count) in stock)
            {
                bool bundle = Wants.NeededByBundle(qid), unshipped = Wants.NeverShipped(qid), uncaught = Wants.NeverCaught(qid);
                if (bundle || unshipped || uncaught) wanted++;
                items.Add(new Dictionary<string, object?>
                {
                    ["id"] = qid, ["name"] = name, ["price"] = price, ["stock"] = count >= int.MaxValue ? null : count,
                    ["bundle"] = bundle, ["unshipped"] = unshipped, ["uncaught"] = uncaught, ["inBag"] = Wants.InBag(qid),
                    ["affordable"] = Game1.player.Money >= price,
                });
            }
            items.Sort((a, b) => Rank(b).CompareTo(Rank(a)));
            Cache = new Dictionary<string, object?> { ["today"] = today, ["nextIn"] = today ? 0 : nextIn, ["wanted"] = wanted, ["items"] = items };
        }
        catch (Exception ex) { monitor.Log($"Traveling cart flags failed: {ex.Message}", LogLevel.Trace); }
    }

    private static bool CartToday()
    {
        if (Game1.getLocationFromName("Forest") is not Forest forest) return false;
        try { return forest.ShouldTravelingMerchantVisitToday() || forest.travelingMerchantDay; }
        catch { return forest.travelingMerchantDay; }
    }

    private static int Rank(Dictionary<string, object?> i) => ((bool)i["bundle"]! ? 4 : 0) + ((bool)i["uncaught"]! ? 2 : 0) + ((bool)i["unshipped"]! ? 1 : 0);
}

internal static class ShippingReader
{
    public static Dictionary<string, object?>? Cache { get; private set; }

    private static List<(string Id, string Name, string Kind)> available = new();
    private static int shipped, total, seasonIndex = -1;

    public static void RefreshDay(IMonitor monitor)
    {
        if (!Context.IsWorldReady) { Cache = null; return; }
        try
        {
            seasonIndex = Game1.seasonIndex;
            var candidates = new Dictionary<string, string>();
            void Offer(string? rawId, string kind)
            {
                if (string.IsNullOrWhiteSpace(rawId) || rawId.Contains(' ')) return;
                string qid = ItemRegistry.QualifyItemId(rawId) ?? rawId;
                if (!qid.StartsWith("(O)")) return;
                candidates.TryAdd(qid[3..], kind);
            }

            foreach (var crop in DataLoader.Crops(Game1.content).Values)
                if (crop.Seasons is null || crop.Seasons.Contains(Game1.season)) Offer(crop.HarvestItemId, "crop");

            string season = Game1.currentSeason;
            foreach (var (id, raw) in DataLoader.Fish(Game1.content))
            {
                var f = raw.Split('/');
                if (f.Length > 1 && f[1] == "trap") { Offer(id, "crab pot"); continue; }
                if (f.Length > 6 && (f[6].Contains(season, StringComparison.OrdinalIgnoreCase) || f[6].Contains("all", StringComparison.OrdinalIgnoreCase))) Offer(id, "fish");
            }

            foreach (var loc in DataLoader.Locations(Game1.content).Values)
            {
                if (loc.Forage is null) continue;
                foreach (var spawn in loc.Forage)
                    if (spawn.Season is null || spawn.Season == Game1.season) Offer(spawn.ItemId, "forage");
            }

            shipped = 0; total = 0;
            var list = new List<(string Id, string Name, string Kind)>();
            foreach (var (id, data) in Game1.objectData)
            {
                if (data.ExcludeFromShippingCollection || !SObject.isPotentialBasicShipped(id, data.Category, data.Type)) continue;
                total++;
                if (Game1.player.basicShipped.ContainsKey(id)) { shipped++; continue; }
                if (candidates.TryGetValue(id, out var kind))
                    list.Add((id, ItemRegistry.GetDataOrErrorItem("(O)" + id).DisplayName, kind));
            }
            available = list.OrderBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).Take(80).ToList();
        }
        catch (Exception ex) { monitor.Log($"Shipping collection failed: {ex.Message}", LogLevel.Trace); }
        RefreshBag(monitor);
    }

    public static void RefreshBag(IMonitor monitor)
    {
        if (!Context.IsWorldReady) { Cache = null; return; }
        try
        {
            if (seasonIndex != Game1.seasonIndex) { RefreshDay(monitor); return; }
            var bag = new HashSet<string>(Game1.player.Items.Where(i => i is not null).Select(i => i.QualifiedItemId));
            var items = available
                .Where(i => !Game1.player.basicShipped.ContainsKey(i.Id))
                .Select(i => new Dictionary<string, object?> { ["id"] = "(O)" + i.Id, ["name"] = i.Name, ["kind"] = i.Kind, ["inBag"] = bag.Contains("(O)" + i.Id) })
                .OrderByDescending(i => (bool)i["inBag"]!)
                .ToList();
            bool hide = ModEntry.Config.HideUnshippedItems;
            Cache = new Dictionary<string, object?>
            {
                ["shipped"] = shipped + (available.Count - items.Count), ["total"] = total, ["available"] = items.Count, ["hidden"] = hide,
                ["items"] = hide ? new List<Dictionary<string, object?>>() : items,
            };
        }
        catch (Exception ex) { monitor.Log($"Shipping bag failed: {ex.Message}", LogLevel.Trace); }
    }
}

internal static class MuseumReader
{
    public static Dictionary<string, object?>? Cache { get; private set; }

    public static void Refresh(IMonitor monitor)
    {
        if (!Context.IsWorldReady) { Cache = null; return; }
        try
        {
            if (Game1.getLocationFromName("ArchaeologyHouse") is not LibraryMuseum museum) { Cache = null; return; }
            var names = new List<string>();
            foreach (var item in Game1.player.Items)
            {
                if (item is null || names.Count >= 12 || !museum.isItemSuitableForDonation(item)) continue;
                if (!names.Contains(item.DisplayName)) names.Add(item.DisplayName);
            }
            Cache = new Dictionary<string, object?>
            {
                ["donated"] = museum.museumPieces.Length,
                ["total"] = LibraryMuseum.totalArtifacts,
                ["inBag"] = names,
            };
        }
        catch (Exception ex) { monitor.Log($"Museum failed: {ex.Message}", LogLevel.Trace); }
    }
}
