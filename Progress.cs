using StardewModdingAPI;
using StardewValley;

namespace StardewDeckBridge;

internal static class DayReader
{
    public static Dictionary<string, object?>? Cache { get; private set; }

    private static uint startEarned;
    private static int startMoney;
    private static int startDay = -1;

    public static void StartDay()
    {
        if (!Context.IsWorldReady) return;
        startEarned = Game1.player.totalMoneyEarned;
        startMoney = Game1.player.Money;
        startDay = Game1.Date.TotalDays;
    }

    public static void Refresh(IMonitor monitor)
    {
        if (!Context.IsWorldReady) { Cache = null; return; }
        try
        {
            if (startDay != Game1.Date.TotalDays) StartDay();
            int value = 0, count = 0;
            foreach (var item in Game1.getFarm().getShippingBin(Game1.player))
            {
                if (item is null) continue;
                value += item.sellToStorePrice(-1L) * item.Stack;
                count += item.Stack;
            }
            Cache = new Dictionary<string, object?>
            {
                ["earnedToday"] = (long)Game1.player.totalMoneyEarned - startEarned,
                ["moneyChange"] = Game1.player.Money - startMoney,
                ["binValue"] = value,
                ["binItems"] = count,
                ["daysPlayed"] = Game1.stats.DaysPlayed,
            };
        }
        catch (Exception ex) { monitor.Log($"Day summary failed: {ex.Message}", LogLevel.Trace); }
    }
}

internal static class SkillReader
{
    private static readonly (int Which, string Id, string Name)[] Skills =
    {
        (Farmer.farmingSkill, "farming", "Farming"), (Farmer.miningSkill, "mining", "Mining"), (Farmer.foragingSkill, "foraging", "Foraging"),
        (Farmer.fishingSkill, "fishing", "Fishing"), (Farmer.combatSkill, "combat", "Combat"),
    };

    public static List<Dictionary<string, object?>> Read()
    {
        var p = Game1.player;
        var list = new List<Dictionary<string, object?>>();
        foreach (var (which, id, name) in Skills)
        {
            int level = p.GetUnmodifiedSkillLevel(which);
            int xp = which < p.experiencePoints.Count ? p.experiencePoints[which] : 0;
            int? next = level >= 10 ? null : Farmer.getBaseExperienceForLevel(level + 1);
            int prev = level <= 0 ? 0 : Farmer.getBaseExperienceForLevel(level);
            list.Add(new Dictionary<string, object?>
            {
                ["id"] = id, ["name"] = name, ["level"] = level, ["buffed"] = p.GetSkillLevel(which), ["xp"] = xp, ["next"] = next,
                ["frac"] = next is int n && n > prev ? Math.Round(Math.Clamp((xp - prev) / (double)(n - prev), 0, 1), 3) : 1.0,
            });
        }
        return list;
    }
}

internal static class BuffReader
{
    public static List<Dictionary<string, object?>> Read()
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var buff in Game1.player.buffs.AppliedBuffs.Values)
        {
            if (buff is null || !buff.visible) continue;
            int ms = buff.millisecondsDuration;
            bool endless = ms < 0;
            list.Add(new Dictionary<string, object?>
            {
                ["id"] = buff.id,
                ["name"] = string.IsNullOrWhiteSpace(buff.displayName) ? (buff.displaySource ?? buff.source ?? buff.id) : buff.displayName,
                ["source"] = buff.displaySource ?? buff.source,
                ["secondsLeft"] = endless ? null : ms / 1000,
                ["seconds"] = endless ? null : Math.Max(1, buff.totalMillisecondsDuration / 1000),
                ["effects"] = Effects(buff),
                ["debuff"] = IsDebuff(buff),
            });
        }
        return list.OrderBy(b => b["secondsLeft"] is int s ? s : int.MaxValue).ToList();
    }

    private static string Effects(Buff buff)
    {
        var e = buff.effects;
        var parts = new List<string>();
        void Add(string name, float v) { if (Math.Abs(v) >= 0.5f) parts.Add($"{(v > 0 ? "+" : "")}{v:0} {name}"); }
        Add("Farming", e.FarmingLevel.Value); Add("Mining", e.MiningLevel.Value); Add("Foraging", e.ForagingLevel.Value);
        Add("Fishing", e.FishingLevel.Value); Add("Luck", e.LuckLevel.Value); Add("Combat", e.CombatLevel.Value);
        Add("Speed", e.Speed.Value); Add("Defense", e.Defense.Value); Add("Attack", e.Attack.Value);
        Add("Energy", e.MaxStamina.Value); Add("Magnet", e.MagneticRadius.Value);
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(buff.description)) return buff.description.Split('\n')[0];
        return string.Join(", ", parts);
    }

    private static bool IsDebuff(Buff buff)
    {
        var e = buff.effects;
        return e.Speed.Value < 0 || e.Defense.Value < 0 || e.Attack.Value < 0 || e.FarmingLevel.Value < 0 || e.LuckLevel.Value < 0
            || buff.id is Buff.tipsy or Buff.slimed or Buff.goblinsCurse or Buff.evilEye or Buff.fear or Buff.frozen or Buff.nauseous or Buff.darkness or Buff.weakness;
    }
}

internal static class PlayerReader
{
    public static List<Dictionary<string, object?>> Read()
    {
        var list = new List<Dictionary<string, object?>>();
        if (!Context.IsMultiplayer) return list;
        foreach (var f in Game1.getOnlineFarmers())
        {
            list.Add(new Dictionary<string, object?>
            {
                ["name"] = f.Name,
                ["location"] = f.currentLocation?.DisplayName,
                ["inBed"] = f.isInBed.Value,
                ["me"] = f.IsLocalPlayer,
                ["host"] = f.IsMainPlayer,
                ["energy"] = (int)Math.Round(f.Stamina),
                ["maxEnergy"] = f.MaxStamina,
            });
        }
        return list;
    }
}
