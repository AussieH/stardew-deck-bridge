using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace StardewDeckBridge;

public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);
    void AddParagraph(IManifest mod, Func<string> text);
    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);
    void AddKeybindList(IManifest mod, Func<KeybindList> getValue, Action<KeybindList> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddTextOption(IManifest mod, Func<string> getValue, Action<string> setValue, Func<string> name, Func<string>? tooltip = null, string[]? allowedValues = null, Func<string, string>? formatAllowedValue = null, string? fieldId = null);
}

internal static class ConfigMenu
{
    public static void Register(IModHelper helper, IManifest manifest, Func<ModConfig> get, Action<ModConfig> set, Action saved)
    {
        var gmcm = helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (gmcm is null) return;

        gmcm.Register(manifest, reset: () => set(new ModConfig()), save: () => { helper.WriteConfig(get()); saved(); });
        gmcm.AddSectionTitle(manifest, () => "Stardew Deck Bridge");
        gmcm.AddParagraph(manifest, () => "Shares live game information with the Stardew Deck plugin for Elgato Stream Deck. It only listens on this computer.");
        gmcm.AddBoolOption(manifest, () => get().AllowActions, v => get().AllowActions = v, () => "Allow actions",
            () => "Let Stream Deck keys act in the game: open menus, pick toolbar slots, zoom, change the volume, take screenshots. Information is always shared.");
        gmcm.AddBoolOption(manifest, () => get().WriteStateFile, v => get().WriteStateFile = v, () => "State file for iCUE",
            () => "Also write the same information to %APPDATA%\\StardewDeck\\state.json once a second, for the Stardew Dashboard widget on a CORSAIR XENEON EDGE. Takes effect after the game restarts.");
        gmcm.AddKeybindList(manifest, () => get().TodoMenuKey, v => get().TodoMenuKey = v, () => "To-do list key", () => "Opens the to-do list in the game.");
        gmcm.AddNumberOption(manifest, () => get().Port, v => get().Port = v, () => "Port", () => "The port the Stream Deck plugin connects to on 127.0.0.1. Change it in the plugin's Settings too. Takes effect after the game restarts.", 1024, 65535);

        gmcm.AddSectionTitle(manifest, () => "Spoilers", () => "What the keys may tell you before you would have found out in the game.");
        gmcm.AddTextOption(manifest, () => get().GiftTastes, v => get().GiftTastes = v, () => "Gift tastes",
            () => "What a villager's key shows when pressed. 'Discovered' lists only the tastes you have already learned, as the game's own gift log does.",
            new[] { "all", "revealed", "off" }, v => v switch { "revealed" => "Only tastes I have discovered", "off" => "Nothing", _ => "Every loved and liked gift" });
        gmcm.AddBoolOption(manifest, () => get().ShowHeartEvents, v => get().ShowHeartEvents = v, () => "Heart events",
            () => "Name villagers who have a heart event waiting, and where. Who and where only, never what happens.");
        gmcm.AddBoolOption(manifest, () => get().HideUncaughtFish, v => get().HideUncaughtFish = v, () => "Hide uncaught fish names",
            () => "Show fish you have never caught as ???, like the Collections tab. Where and when they bite still shows.");
        gmcm.AddBoolOption(manifest, () => get().HideUnshippedItems, v => get().HideUnshippedItems = v, () => "Hide unshipped items",
            () => "Do not list the items you have never shipped; the Shipping key keeps the counts.");
        gmcm.AddBoolOption(manifest, () => get().ShowVillagerLocations, v => get().ShowVillagerLocations = v, () => "Villager locations",
            () => "Let villager keys say where each villager is right now.");
        gmcm.AddBoolOption(manifest, () => get().ShowMineOres, v => get().ShowMineOres = v, () => "Show ore in the mines on tile",
            () => "Let the Mines key count the ores, gems and geodes on the floor you are on. Off, it still shows the floor, monsters and rocks.");

        gmcm.AddSectionTitle(manifest, () => "Cheats", () => "Off unless you say so. The Stream Deck's Cheats page does nothing while this is off.");
        gmcm.AddBoolOption(manifest, () => get().EnableCheats, v => get().EnableCheats = v, () => "Enable cheats",
            () => "Lets the Cheats page add gold, refill energy and health, freeze the clock, move time forward, set tomorrow's weather, grow or water every crop, and warp. Weather needs the host in multiplayer.");
        gmcm.AddBoolOption(manifest, () => get().CheatSounds, v => get().CheatSounds = v, () => "Cheat sounds", () => "Play a sound when a cheat key is pressed.");
    }
}
