# Admin Live Map for Rust

See every player on your Rust server live on a map, in your browser, without joining the game.

Admin Live Map has two parts:

- **`AdminLiveMap.cs`**, a small Oxide/uMod plugin for your server. It answers questions sent over RCON: where the players are, where they walked, who died where, where the bases are, and what the map looks like.
- **`LiveMap.html`**, a single web page you open on your own computer. It connects to your server's WebRCON, asks the plugin every 2 seconds, and draws everything on the map.

You don't need a web server, a database or any other program. The page talks to your Rust server directly.

## Features

- **Live player positions** on the server's own map image, with grid squares, monument names and the direction each player is looking. Players in the same team share a colour.
- **Online, sleeping and dead players**, plus health, vehicle and team for each player.
- **Trails** showing where players walked in the last 10, 30 or 60 minutes. The server keeps the history, so trails are there even if the page was closed.
- **Death markers** showing who died where, killed by whom or what, with which weapon and from how far.
- **Bases**: every building with a tool cupboard, its owner, its size and who has cupboard access.
- **Game events**: the patrol helicopter and Bradley (with health), Chinook, cargo ship, cargo plane, supply drops, locked crates (with a hacking countdown), the travelling vendor, and Deep Sea ghost ships, islands and floating cities. Alerts when they arrive or leave, when the heli or Bradley is destroyed (and by whom), and when someone starts hacking a crate.
- **Heatmap** of where players move around, where they die, or where they are killed by other players, for the last hour, the last 24 hours or since the wipe.
- **Live chat**, including team chat, with a box to reply to everyone as SERVER.
- **Join, leave and death alerts** with pop-ups and optional sounds, plus an events list where a click jumps to the spot on the map.
- **Server health**: FPS, entity count, memory use and uptime.
- **Follow a player**: click a name and the map keeps them centred. A search box finds anyone by name or Steam ID.

## Requirements

- A Rust dedicated server with [Oxide/uMod](https://umod.org).
- **WebRCON** turned on. It is on by default in current Rust (`rcon.web 1`).
- An RCON port and password, set with `+rcon.port` and `+rcon.password` in your server's startup line.
- A modern browser: Chrome, Edge or Firefox.

## Installation

### 1. Install the plugin

Copy `AdminLiveMap.cs` to your server's `oxide/plugins` folder. Oxide loads it automatically. Your server console should show:

```
Loaded plugin Admin Live Map v1.2.0 by Guerisse
```

### 2. Get the page

Download `LiveMap.html` from this repository: open the file and click **Download raw file**. Save it anywhere on your computer.

> [!IMPORTANT]
> Open the page from your own computer by double-clicking the downloaded file. It does **not** work when opened from a website such as GitHub Pages. Browsers block secure (`https://`) web pages from opening the unencrypted connection that Rust's WebRCON uses.

### 3. Connect

1. Double-click `LiveMap.html`.
2. Fill in:
   - **Server address:** `127.0.0.1` if the server runs on the same computer. Otherwise use the server's IP address.
   - **RCON port:** your server's RCON port (often `28016`).
   - **RCON password:** your server's RCON password.
3. Press **Connect**.

The first time, the server draws the map image. This takes a few seconds and the server may pause briefly. It only happens once per map; after that the image is cached.

Tick **Remember on this computer** to skip typing the password next time. It is then stored only in that browser on that computer, never in the HTML file.

## Using the map

| Action | How |
| --- | --- |
| Zoom | Mouse wheel, or the **+** / **−** buttons |
| Move around | Drag the map |
| Show the whole map | The square button |
| Follow a player | Click them in the player list. Click again to stop following. |
| See details | Hover over the map: grid square, plus any base or death marker under the mouse |
| Show or hide layers | The tick boxes: grid, monuments, sleepers, names, trails, deaths, bases, game events, sounds, heatmap |
| Heatmap | Tick **Heatmap**, then choose **Activity**, **All deaths** or **PvP kills**, and **Last hour**, **Last 24 hours** or **Since wipe**. Hover for the numbers; the key at the bottom left explains the colours. |
| Jump to an event | Click a line in the **Events** tab |

| Marker | Meaning |
| --- | --- |
| Filled dot with an arrow | Online. The arrow shows where they are looking. |
| Hollow ring with "(zz)" | Offline sleeper |
| Red X | Dead |
| Skull | Where someone died in the last hour |
| Box with a small square | A base. The square is the tool cupboard. Grey dashed boxes have no player owner (for example event bases). |
| Round icon with a coloured ring | A game event: 🚁 patrol helicopter (red ring) or Chinook (yellow ring), 🚢 cargo ship, tank = Bradley, ✈ cargo plane, 🪂 supply drop, 🔒 locked crate (⏳ being hacked, 🔓 open), 🛒 travelling vendor, 👻 ghost ship, 🏝 Deep Sea island, 🏙 floating city. The small arrow on the ring shows where it is heading. |

## Console commands

The commands only work from RCON and the server console. Players can't use them from the in-game F1 console, because the answers reveal every player's position and base. So there are no permissions to set up.

### Commands for you

| Command | What it does |
| --- | --- |
| `livemap.map` | Says whether the map image is ready |
| `livemap.map rerender` | Draws the map image again, for example after a big map change. Allowed once every 5 minutes, because drawing pauses the server for a few seconds. Reload the page afterwards. |
| `livemap.benchmark [online] [sleepers] [bases]` | Load test: builds every answer with made-up data (default 600 online, 10,000 sleepers, 10,000 bases) and shows the time and size of each, plus the numbers for your server right now. It pauses the server for about a third of a second. |

### Commands the page uses

These answer with JSON. You can send them over RCON yourself, for example to build your own tools.

| Command | Answers with |
| --- | --- |
| `livemap.players` | Online players and game events (helicopters, cargo ship, Bradley, airdrops, locked crates, vendor, Deep Sea), plus counters that tell the page when sleepers or deaths changed |
| `livemap.sleepers` | Offline sleepers |
| `livemap.deaths <id>` | Deaths newer than the given death number (`0` for all) |
| `livemap.trails` | Recent movement of all players, thinned out on busy servers. Typed in the server console, it prints a short summary instead. |
| `livemap.trails <steamid>` | One player's full trail |
| `livemap.bases` | Tool cupboards, their owners, building sizes and who has access |
| `livemap.heat <activity\|deaths\|pvp> <hour\|day\|wipe>` | Heatmap counts per map square. Typed in the server console, it prints a short summary instead. |
| `livemap.mapdata` | The map image (about 1 MB of text) and monument names |

The page also uses three built-in Rust commands: `serverinfo`, `chat.tail` and `say`.

## Configuration

The settings file is `oxide/config/AdminLiveMap.json`:

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

- **Map image scale:** higher values give a sharper map, but a bigger image to send and a longer pause when it is drawn. 0.5 suits most maps.
- **Trails:** are kept in memory only. A plugin reload or server restart clears them. When the page connects it gets all trails at once; above the point limit, every 2nd, 3rd... point is left out. The trail of a player you click is always sent in full.
- **Deaths:** are saved in `oxide/data/AdminLiveMap/deaths.json`, so they survive a restart. Only the newest ones are kept, up to the maximum.
- **Send sleepers / Send bases:** set to `false` to switch those layers off completely.
- **Base scan time per step:** how long the plugin may spend measuring building sizes, five times a second. Lower is gentler on the server; a full pass then takes longer.
- **Heatmap:** activity is counted at each trail point interval, deaths when they happen. The last 24 hours and the totals since the wipe are saved in `oxide/data/AdminLiveMap/heatmap.json` and start from zero after a wipe; the last hour is kept in memory only. 128 squares across means about 33 m per square on a 4250 map. With "counts players standing still" off, AFK players and people inside their base don't make the map light up.
- **Map image:** is cached in `oxide/data/AdminLiveMap/map.json` (about 1 MB) and replaced automatically when the map changes.

Reload the plugin after changing the file.

## Big servers

The plugin and the page are built so the work grows gently with server size.

- **Only what changed:** every 2 seconds the page asks for online players only. All sleepers are sent when the page connects. After that, the page only asks for the list again when it changed (someone fell asleep, woke up or died), at most once a minute; in between it moves players who log off or on itself. Deaths are only sent when there is a new one.
- **Shared answers:** answers are kept for a short time and shared, so several open pages cost about the same as one.
- **Game events cost almost nothing:** the plugin looks for them once when it loads (about 10 ms) and then only notes new helicopters, ships, crates and so on as the game spawns them. The Deep Sea is read from the game's own lists.
- **No pauses for bases:** building sizes are measured a few buildings at a time (2 ms, five times a second) and only while a page is open. On a huge server the first pass can take a few minutes; until then bases show as a box around the cupboard and the footer says "measuring bases ..%".
- **Busy-server mode in the page:** with more than 30 players online, joins, leaves, deaths and chat no longer pop up or beep (they are still listed). With more than 50, only the selected player's trail is drawn; with more than 60, names appear when you zoom in. Thousands of sleepers and bases are drawn as small squares when zoomed out.

Load test results on a test server (`livemap.benchmark`, made-up data, server time to build one answer):

| Answer | How often | 600 online, 10,000 sleepers, 10,000 bases | 250 online, 3,000 sleepers, 3,000 bases |
| --- | --- | --- | --- |
| Online players and game events | every 2 s (shared for 1 s) | 1.2 ms, 51 KB | 0.5 ms, 21 KB |
| Sleepers | when changed, at most once a minute | 7.5 ms, 569 KB | 2.1 ms, 170 KB |
| All trails | once when the page connects | 8.3 ms, 430 KB | 7.6 ms, 418 KB |
| Bases | every 60 s (shared for 30 s) | 22.5 ms, 1.4 MB | 6.4 ms, 441 KB |
| Heatmap (whole 128 × 128 grid in use) | once a minute, only while switched on | 5.5 ms, 64 KB | same |

Run `livemap.benchmark` on your own server to see its numbers.

## Security

- **The page contains no password.** You type it in, and it is only stored in your browser if you tick **Remember**.
- **RCON sends the password unencrypted.** Use the page on the server's own computer or your home network. Don't use it over the open internet unless you know what you are doing (for example, through a VPN).
- **Anyone with your RCON password controls your server.** Share the page freely, but never the password.

## Troubleshooting

| Problem | What to check |
| --- | --- |
| "Could not connect" | Is the server running? Are the address, RCON port and password right? Is WebRCON on (`rcon.web 1`)? Does a firewall block the RCON port? |
| "The plugin is not answering" | Is `AdminLiveMap.cs` in `oxide/plugins`, and did it load? Check the server console or the Oxide log. |
| The map is blank after connecting | Wait a few seconds on the first connect while the server draws the map image. |
| The page says it must be opened from your own computer | You opened it from a website. Download `LiveMap.html` and double-click the file. |
| Updates slow down when the tab is in the background | Browsers slow down hidden tabs. Keep the page in its own window. |

## License

[MIT](LICENSE) © 2026 Guerisse
