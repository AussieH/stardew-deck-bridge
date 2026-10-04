# Stardew Deck Bridge changelog

## 1.3.0 (2026-10-03)

- **Tap controls for the Stardew Dashboard iCUE widget** (1.1.0 and later): a small HTTP and WebSocket endpoint on
  `localhost:52818`, bound to 127.0.0.1 and ::1 only, takes the same commands as the Stream Deck keys (open a menu,
  close it, pick a toolbar slot, zoom, the HUD, the to-do list, a screenshot, the volume, and cheats when they are on).
  They run through exactly the same code on the game thread, under the same Allow actions and Enable cheats settings.
  - Only this PC's own programs get in: a request whose Origin is a web site is refused (403, and its CORS preflight
    gets no allow headers), the Host must be `localhost`, `127.0.0.1` or `[::1]` on this port (no DNS rebinding), and a
    command needs the key the mod writes into `state.json` (new each time the game starts), which a web page cannot
    read: the `X-Stardew-Deck` header on a POST, `?key=` on the WebSocket. Bodies, headers and messages are capped at
    8 KB, and no more than 16 connections are open at once.
  - A command the game thread does not reach within 4 seconds (the game paused out of focus, loading) is answered
    "game not answering" and dropped, so it never runs late.
  - `GET /hello` says `{protocol, mod, controls}`; the WebSocket also sends a status heartbeat every 2 seconds
    (connected, inWorld, ticking, actions).
  - New setting **Widget controls (iCUE)** (on by default; Generic Mod Config Menu or `WidgetControls` in
    `config.json`). Saving it in Generic Mod Config Menu starts or stops the endpoint straight away. It needs the state
    file (the key is in it).
- `state.json` gains a `controls` object after the versions: `{"on":false}`, `{"on":true,"port":52818,"key":"..."}`, or
  `{"on":true,"port":52818,"error":"port in use"}`. Its `protocol` stays 1; the rest of the file is unchanged.
- The socket on 52817 and its protocol are unchanged: Stardew Deck for Stream Deck works as before.

## 1.2.1 (2026-10-03)

- **The state file is there as soon as the game starts.** The mod now makes `%APPDATA%\StardewDeck\` and writes
  `state.json` the moment SMAPI loads it, before you pick a save, saying the game is at the title screen. Before, the
  folder appeared only a second into the title screen. iCUE watches only a folder that already exists, so the Stardew
  Dashboard widget could miss one that appeared later; now the folder is there from the first launch with 1.2.1.
- The file's format is unchanged, and so is the last write with `"running": false` when the game closes.

## 1.2.0 (2026-10-03)

- **A state file for the Stardew Dashboard iCUE widget** (CORSAIR XENEON EDGE): the mod also writes
  `%APPDATA%\StardewDeck\state.json`, the same information the Stream Deck plugin gets, once a second and straight
  away on a new day, a warp or a return to the title. iCUE widgets can only read files, not sockets.
  - Written by a background thread (the game thread only hands the state over), whole: a temporary file swapped into
    place, or, while iCUE's file watcher holds the file without delete sharing, rewritten in place after one 5 ms retry.
    The temporary file never stays behind.
  - Kept fresh while the game stands still (paused out of focus, loading) with `"ticking": false`, and written one last
    time with `"running": false` when the game closes.
  - The same Spoilers and Cheats settings as the plugin: villager locations, ores in the mines, uncaught fish and
    unshipped items follow their settings, and cheats stay off unless Enable cheats is on.
  - New setting **State file for iCUE** (on by default; Generic Mod Config Menu or `WriteStateFile` in `config.json`).
- The socket and its protocol are unchanged: Stardew Deck for Stream Deck works as before.

## 1.1.0 (2026-09-13)

- Day summary, skills, buffs, the traveling cart, the shipping collection, quests and special orders, the museum,
  players online, heart events, and the map screenshot.
- Spoilers settings (gift tastes, heart events, uncaught fish, unshipped items, villager locations, ore in the mines,
  off by default) and optional cheats (off by default), both in Generic Mod Config Menu.
- The in-game to-do list (L) always releases the keyboard when it closes.

## 1.0.0 (2026-09-13)

- First release: the clock, weather, gold, energy, reminders, the calendar, friendships and gift tastes, fishing,
  bundles, the mines, mail and the to-do list, over a loopback-only socket (127.0.0.1:52817), and the safe actions
  the keys ask for (menus, toolbar slots, zoom, volume).
