using StardewModdingAPI;
using StardewValley;

namespace StardewDeckBridge;

internal static class QuestReader
{
    public static List<Dictionary<string, object?>> Cache { get; private set; } = new();

    public static void Refresh(IMonitor monitor)
    {
        if (!Context.IsWorldReady) { Cache = new(); return; }
        try
        {
            var list = new List<Dictionary<string, object?>>();
            foreach (var q in Game1.player.questLog)
            {
                if (q is null || q.IsHidden()) continue;
                string objective = q.currentObjective ?? "";
                if (string.IsNullOrWhiteSpace(objective))
                {
                    var lines = q.GetObjectiveDescriptions();
                    objective = lines is { Count: > 0 } ? lines[0] : "";
                }
                list.Add(new Dictionary<string, object?>
                {
                    ["id"] = q.id.Value, ["kind"] = "quest", ["title"] = q.GetName(), ["objective"] = objective,
                    ["daysLeft"] = q.IsTimedQuest() ? q.GetDaysLeft() : null, ["done"] = q.ShouldDisplayAsComplete(), ["isNew"] = q.ShouldDisplayAsNew(),
                    ["reward"] = q.HasMoneyReward() ? q.GetMoneyReward() : null,
                });
            }
            foreach (var o in Game1.player.team.specialOrders)
            {
                if (o is null || o.IsHidden()) continue;
                var objectives = new List<string>();
                foreach (var obj in o.objectives)
                {
                    if (obj is null) continue;
                    string text = obj.GetDescription();
                    if (obj.ShouldShowProgress()) text += $" ({obj.GetCount()}/{obj.GetMaxCount()})";
                    if (!obj.IsComplete()) objectives.Add(text);
                }
                list.Add(new Dictionary<string, object?>
                {
                    ["id"] = o.questKey.Value, ["kind"] = "order", ["title"] = o.GetName(), ["objective"] = objectives.Count > 0 ? objectives[0] : "ready to turn in",
                    ["daysLeft"] = o.IsTimedQuest() ? o.GetDaysLeft() : null, ["done"] = o.ShouldDisplayAsComplete(), ["isNew"] = o.ShouldDisplayAsNew(),
                    ["reward"] = o.HasMoneyReward() ? o.GetMoneyReward() : null, ["progress"] = $"{o.GetCompleteObjectivesCount()}/{o.objectives.Count}",
                });
            }
            Cache = list.OrderBy(q => (bool)q["done"]! ? 1 : 0).ThenBy(q => q["daysLeft"] is int d ? d : int.MaxValue).Take(40).ToList();
        }
        catch (Exception ex) { monitor.Log($"Quests failed: {ex.Message}", LogLevel.Trace); }
    }
}

internal static class EventReader
{
    public static List<Dictionary<string, object?>> Cache { get; private set; } = new();

    private static int lastPoints = -1;
    private static double lastRefresh;

    public static void RefreshIfFriendshipChanged(IMonitor monitor, int totalPoints)
    {
        if (totalPoints == lastPoints) return;
        double now = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
        if (now - lastRefresh < 3) return;
        lastPoints = totalPoints;
        Refresh(monitor);
    }

    public static void Refresh(IMonitor monitor)
    {
        if (!Context.IsWorldReady || !ModEntry.Config.ShowHeartEvents) { Cache = new(); return; }
        lastRefresh = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
        try
        {
            var player = Game1.player;
            var result = new List<Dictionary<string, object?>>();
            var seen = new HashSet<string>();
            foreach (var loc in Game1.locations)
            {
                if (loc is null) continue;
                Dictionary<string, string>? events;
                try { if (!loc.TryGetLocationEvents(out _, out events) || events is null) continue; }
                catch { continue; }

                foreach (var key in events.Keys)
                {
                    if (!key.Contains("/f ", StringComparison.Ordinal)) continue;
                    var parts = key.Split('/');
                    if (parts.Length < 2 || player.eventsSeen.Contains(parts[0])) continue;

                    string? npc = null;
                    bool ok = true;
                    foreach (var part in parts.Skip(1))
                    {
                        if (!part.StartsWith("f ", StringComparison.Ordinal)) continue;
                        var t = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 1; i + 1 < t.Length; i += 2)
                        {
                            if (!player.friendshipData.TryGetValue(t[i], out var fr) || !int.TryParse(t[i + 1], out var need) || fr.Points < need) { ok = false; break; }
                            npc ??= t[i];
                        }
                        if (!ok) break;
                    }
                    if (!ok || npc is null) continue;

                    string id;
                    try { id = loc.checkEventPrecondition(key, true); }
                    catch { continue; }
                    if (string.IsNullOrEmpty(id) || id == "-1") continue;
                    if (!seen.Add(npc + "|" + loc.Name)) continue;

                    var who = Game1.getCharacterFromName(npc, true, false);
                    result.Add(new Dictionary<string, object?>
                    {
                        ["npc"] = npc, ["name"] = who?.displayName ?? npc, ["location"] = loc.DisplayName,
                        ["hearts"] = player.friendshipData.TryGetValue(npc, out var f) ? f.Points / 250 : 0,
                    });
                    if (result.Count >= 20) break;
                }
                if (result.Count >= 20) break;
            }
            Cache = result;
        }
        catch (Exception ex) { monitor.Log($"Heart events failed: {ex.Message}", LogLevel.Trace); }
    }
}
