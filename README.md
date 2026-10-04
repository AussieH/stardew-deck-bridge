# Stardew Deck Bridge

The Stardew Valley half of [Stardew Deck](https://teatimeservers.ca/plugins/stardew-deck), a Stream Deck plugin. It
is a [SMAPI](https://smapi.io) mod: it shares what your farmer can see with the plugin over a socket that only listens
on this computer, writes the same information to a file for the Stardew Dashboard iCUE widget, and runs the few
actions the plugin's keys ask for.

It opens no connection to the internet and accepts connections only from this computer. Cheats are off unless you
turn them on. This repository is here so you can see exactly what it does.

## What it reads

- The clock, day, season, year and weekday; today's and tomorrow's weather; festivals; whether the game is paused.
- Your gold, energy, health, daily luck, location, the tool in your hand and its upgrade level, the watering can's
  water, your toolbar slot, skill levels and progress, and buffs.
- Birthdays today and tomorrow, the calendar ahead, and friendships: hearts, points, talked to today, gifts this week,
  dating status and, if **Villager locations** is on, where each villager is right now.
- Gift tastes for a villager when a key asks, following the **Gift tastes** setting.
- The farm: crops ready and unwatered, machines ready, animal products and animals not yet petted, and your pets.
- Fish you can catch here and now, community center bundles, the mines (floor, and ores on the floor only if
  **Show ore in the mines on tile** is on), mail, quests and special orders, the museum, the shipping collection, the
  traveling cart's stock, heart events waiting, other players online, and the end-of-day summary.
- The game's zoom and volume levels, and the in-game to-do list.

## What it can do

Each action only does what you could already do with the keyboard or mouse, and none of them run while **Allow
actions** is off (information is still shared):

- Open a menu (inventory, skills, social, map, crafting, animals, powers, collections, options, the journal or the
  calendar), or close the one that is open.
- Pick a toolbar slot, step to the next or previous slot, or move to the next toolbar row.
- Change the zoom or a volume channel, mute or unmute.
- Show or hide the HUD; take a map screenshot (saved where the game saves its own).
- Add to, edit, tick off and clear the to-do list. The list also opens in the game with **L** and is kept per save in
  the mod's `data` folder.

The **Cheats** page of the plugin can add gold, refill energy and health, freeze the clock, move the clock forward,
set tomorrow's weather, grow or water crops and warp to a few places. Every one of those is refused unless **Enable
cheats** is on, and it is off by default.

Two console commands come with it: `deck_cart` (what the game reports for the traveling cart) and `deck_todo` (the
to-do list from the SMAPI console).

## The socket

TCP on `127.0.0.1:52817` (the **Port** setting), bound to the loopback address only, and any connection from
elsewhere is closed straight away. Each message is one line of JSON. A client gets a `hello` (protocol, mod, game and
SMAPI versions, whether actions are allowed) and the last state straight away, then a new `state` whenever something
changes, at most four times a second, and a `ping` every 4 seconds even while the game is paused. Requests are
`{"type":"command","id":...,"name":...,"args":{...}}` and each gets a `result`. Lines longer than 64 KB close the
connection.

## The state file

For the Stardew Dashboard widget for the CORSAIR XENEON EDGE (iCUE widgets can read files, not sockets), the mod also
writes `%APPDATA%\StardewDeck\state.json`: the same state the plugin gets, under the same Spoilers and Cheats
settings. It is written once a second and straight away on a new day, a warp or a return to the title, by a background
thread, never by the game thread. Each write is whole: a temporary file swapped into place, or, while another program
holds the file open, rewritten in place. The folder and file are made as soon as SMAPI loads the mod.

`protocol` is the file's format version, then `mod`, `game` and `smapi` versions, `at` (Unix milliseconds), `seq`,
`ticking` (false while the game stands still, paused out of focus or loading), `running` (written false once when the
game closes), `state`, and `"end": true` last. Turn it off with **State file for iCUE**.

## Settings

In [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) if you have it, or in `config.json` in
the mod folder (made the first time the game runs with the mod):

| Setting | `config.json` | Default |
| --- | --- | --- |
| Allow actions | `AllowActions` | on |
| State file for iCUE | `WriteStateFile` | on |
| To-do list key | `TodoMenuKey` | `L` |
| Port | `Port` | `52817` (change it in the plugin's Settings too) |
| Gift tastes | `GiftTastes` | `all` (or `revealed`, `off`) |
| Heart events | `ShowHeartEvents` | on |
| Hide uncaught fish names | `HideUncaughtFish` | off |
| Hide unshipped items | `HideUnshippedItems` | off |
| Villager locations | `ShowVillagerLocations` | on |
| Show ore in the mines on tile | `ShowMineOres` | off |
| Enable cheats | `EnableCheats` | **off** |
| Cheat sounds | `CheatSounds` | on |

## Installing

1. Install [SMAPI](https://smapi.io) 4.0 or newer (Stardew Valley 1.6).
2. Download the zip from the [Stardew Deck page](https://teatimeservers.ca/plugins/stardew-deck), close the game and
   unzip it into the game's `Mods` folder, so you have `Mods\StardewDeckBridge\manifest.json`.
3. Start the game through SMAPI. Its console says `Listening for Stream Deck on 127.0.0.1:52817.`

It works alongside other mods, including large expansions.

## Building

Needs the .NET SDK (6 or newer) and the game with SMAPI installed.
[Pathoschild.Stardew.ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig) finds the game
in the usual Steam and GOG folders:

```
dotnet build -c Release
```

The mod and its zip land in `bin/Release/net6.0`. If the game is somewhere else, point the build at it:

```
dotnet build -c Release -p:GamePath="D:\Games\Stardew Valley"
```

or set `GamePath` in a `Directory.Build.props` beside the project:

```xml
<Project>
  <PropertyGroup>
    <GamePath>D:\Games\Stardew Valley</GamePath>
  </PropertyGroup>
</Project>
```

## Licence

MIT, see `LICENSE`. Stardew Deck is an unofficial fan project, not affiliated with or endorsed by ConcernedApe.
Stardew Valley is a trademark of ConcernedApe LLC.
