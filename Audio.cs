using StardewValley;
using StardewValley.Characters;

namespace StardewDeckBridge;

internal static class Audio
{
    private static readonly Dictionary<string, int> LastBeforeMute = new();

    public static Dictionary<string, object?> Read() => new()
    {
        ["music"] = Percent(Game1.options.musicVolumeLevel),
        ["sound"] = Percent(Game1.options.soundVolumeLevel),
        ["ambient"] = Percent(Game1.options.ambientVolumeLevel),
        ["footsteps"] = Percent(Game1.options.footstepVolumeLevel),
    };

    private static int Percent(float level) => (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100);

    public static string? Change(string? channel, int? delta, int? set, bool toggleMute)
    {
        int which;
        float level;
        switch (channel)
        {
            case "music": which = Options.musicVolume; level = Game1.options.musicVolumeLevel; break;
            case "sound": which = Options.soundVolume; level = Game1.options.soundVolumeLevel; break;
            case "ambient": which = Options.ambientVolume; level = Game1.options.ambientVolumeLevel; break;
            case "footsteps": which = Options.footstepVolume; level = Game1.options.footstepVolumeLevel; break;
            default: return "no such slider";
        }

        int current = Percent(level), target;
        if (toggleMute)
        {
            if (current > 0) { LastBeforeMute[channel] = current; target = 0; }
            else target = LastBeforeMute.TryGetValue(channel, out var before) && before > 0 ? before : 50;
        }
        else if (set is int s) target = s;
        else if (delta is int d) target = current + d;
        else return "nothing to change";

        Game1.options.changeSliderOption(which, Math.Clamp(target, 0, 100));
        return null;
    }

    public static List<Dictionary<string, object?>> Pets()
    {
        var list = new List<Dictionary<string, object?>>();
        int today = Game1.Date.TotalDays;
        foreach (var pet in Utility.getAllPets())
        {
            bool petted = pet.lastPetDay.TryGetValue(Game1.player.UniqueMultiplayerID, out int day) && day == today;
            bool? bowl = null;
            try { bowl = pet.GetPetBowl()?.watered.Value; } catch { }
            list.Add(new Dictionary<string, object?> { ["name"] = pet.displayName, ["type"] = pet.petType.Value, ["petted"] = petted, ["bowlFilled"] = bowl });
        }
        return list;
    }
}
