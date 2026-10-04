using StardewModdingAPI.Utilities;

namespace StardewDeckBridge;

public sealed class ModConfig
{
    public int Port { get; set; } = 52817;

    public bool AllowActions { get; set; } = true;

    public bool WriteStateFile { get; set; } = true;

    public KeybindList TodoMenuKey { get; set; } = KeybindList.Parse("L");

    public string GiftTastes { get; set; } = "all";

    public bool ShowHeartEvents { get; set; } = true;

    public bool HideUncaughtFish { get; set; } = false;

    public bool HideUnshippedItems { get; set; } = false;

    public bool ShowVillagerLocations { get; set; } = true;

    public bool ShowMineOres { get; set; } = false;

    public bool EnableCheats { get; set; } = false;

    public bool CheatSounds { get; set; } = true;
}
