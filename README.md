## Features

See every player on your server live on a map in your browser, without joining the game. This plugin feeds a free companion web page, **LiveMap.html**, which connects to your server's WebRCON and draws:

- Live player positions on your server's own map, with grid squares, monuments, view direction and team colours
- Online, sleeping and dead players, with health, vehicle and team
- Movement trails for the last 10, 30 or 60 minutes
- Death markers: who died where, killed by whom or what, with which weapon and from how far
- Bases: every tool cupboard, its owner, building size and who has access
- Game events: patrol helicopter and Bradley (with health), Chinook, cargo ship, cargo plane, supply drops, locked crates with a hacking countdown, travelling vendor, and Deep Sea ghost ships, islands and floating cities, with alerts when they arrive, leave or get destroyed (and by whom)
- Heatmap of activity, deaths or PvP kills for the last hour, last 24 hours or since the wipe
- Live chat (including team chat), with replies as SERVER
- Join, leave and death alerts with sounds, plus a clickable events list
- Server health: FPS, entities, memory and uptime

**The plugin does nothing visible on its own: you need the web page.** Download `LiveMap.html` from GitHub: **https://github.com/Garry-Marshall/AdminLiveMap**

No web server, database or extra software is needed. The page talks to your Rust server directly over WebRCON.

## Getting started

1. Install this plugin.
2. Download `LiveMap.html` from the GitHub link above and save it on your computer.
3. Double-click the file, fill in your server address, RCON port and RCON password, and press **Connect**.

Open the page as a file on your own computer. It does not work from a website, because browsers block secure (`https://`) pages from opening WebRCON's unencrypted connection.

On the first connect, the server draws the map image once. It may pause for a few seconds. After that the image is cached until the map changes.

## Permissions

This plugin has no permissions. Its commands only work from RCON and the server console, never from a player's in-game F1 console.

## Commands

This plugin provides console commands for RCON and the server console.

For you:

- `livemap.map` -- Says whether the map image is ready
- `livemap.map rerender` -- Draws the map image again (at most once every 5 minutes, because drawing pauses the server for a few seconds)
- `livemap.benchmark [online] [sleepers] [bases]` -- Load test with made-up data (default 600 / 10,000 / 10,000): shows how long each answer takes to build and how big it is, plus the numbers for your server right now. Pauses the server for about a third of a second.

Used by the web page (they answer with JSON; you can also call them over RCON for your own tools):

- `livemap.players` -- Online players and game events, plus counters that tell the page when sleepers or deaths changed
- `livemap.sleepers` -- Offline sleepers
- `livemap.deaths <id>` -- Deaths newer than the given death number (`0` for all)
- `livemap.trails` -- Recent movement of all players, thinned out on busy servers (typed in the server console, a short summary instead)
- `livemap.trails <steamid>` -- One player's full trail
- `livemap.bases` -- Tool cupboards, owners, building sizes and who has access
- `livemap.heat <activity|deaths|pvp> <hour|day|wipe>` -- Heatmap counts per map square (typed in the server console, a short summary instead)
- `livemap.mapdata` -- The map image (about 1 MB of text) and monument names

## Configuration

```json
{
  "Map image scale (0.5 = one pixel per 2 metres)": 0.5,
  "Map image ocean border (pixels)": 250,
  "Trail point interval (seconds)": 5.0,
  "Trail minimum movement between points (metres)": 3.0,
  "Keep trails for (minutes)": 60,
  "Max trail points sent at once (older points are thinned out above this)": 20000,
  "Keep deaths for (minutes)": 120,
  "Max deaths kept": 500,
  "Send sleepers to the page": true,
  "Send bases to the page": true,
  "Base scan time per step (milliseconds, 5 steps a second)": 2.0,
  "Collect heatmap data": true,
  "Heatmap squares across the map (changing it starts the heatmap from zero)": 128,
  "Heatmap activity counts players standing still": false
}
```

- **Map image scale:** higher means sharper, but a bigger image and a longer one-time pause when it is drawn.
- **Trails:** kept in memory, so a reload or restart clears them. Above the point limit, the page gets every 2nd, 3rd... point; the trail of a player you click is always sent in full.
- **Deaths:** saved in `oxide/data/AdminLiveMap/deaths.json`, up to the maximum.
- **Send sleepers / Send bases:** `false` switches those layers off completely.
- **Base scan time per step:** how long building sizes may be measured, five times a second. Lower is gentler; a full pass then takes longer.
- **Heatmap:** the last 24 hours and the totals since the wipe are saved in `oxide/data/AdminLiveMap/heatmap.json` and reset on a wipe; the last hour is kept in memory only. With "counts players standing still" off, AFK players don't make their base light up.
- **Map image:** cached in `oxide/data/AdminLiveMap/map.json` (about 1 MB) and replaced automatically when the map changes.

## FAQ

**Is my RCON password safe?**
The page never stores it in the file. It is kept in your browser only if you tick "Remember". RCON itself sends the password unencrypted, so use the page on the server's own computer or your home network, or through a VPN.

**Does it affect performance?**
Hardly, even on big servers. Every 2 seconds the page only asks for online players (about 1 ms of server time with 600 online). The page gets the full list of sleepers when it connects, and after that only asks for it again when it changed (at most once a minute); deaths are only sent when there is a new one. Answers are briefly cached and shared, so several open pages don't add load. Bases are read from the game's own building list (no world scans, no entity spawn hooks), and building sizes are measured a few at a time (2 ms, five times a second) only while a page is open, so the server never pauses for it. Game events are found once when the plugin loads (about 10 ms) and then noted as the game spawns them, so there are no world scans. The heatmap adds one counter per player every few seconds and is only sent while it is switched on in the page (about 5 ms, once a minute). The only noticeable cost is drawing the map image: once per map, and a forced redraw is allowed at most once every 5 minutes. Run `livemap.benchmark` to see the numbers for your server.

**Does the page cope with hundreds of players?**
Yes. With more than 30 online it stops popping up and beeping for every join, leave, death and chat (they are still listed), with more than 50 it draws only the selected player's trail, and thousands of sleepers and bases become small squares when zoomed out. The player list has a search box and shows 100 players at a time.

**How is this different from existing map plugins?**
Plugins such as Admin Radar show players to admins inside the game, and others put markers on the in-game map. This plugin is for watching the server from **outside** the game: a free page in your browser, with nothing to host, no third-party service and no account. It also combines live positions, movement trails, deaths, bases, chat, join/leave alerts and server health in one view.

**Can I use it from another computer?**
Yes, as long as that computer can reach your server's RCON port. Fill in the server's IP address instead of `127.0.0.1`.