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
- **Live chat**, including team chat, with a box to reply to everyone as SERVER.
- **Join, leave and death alerts** with pop-ups and optional sounds, plus an events list where a click jumps to the spot on the map.
- **Server health**: FPS, entity count, memory use and uptime.
- **Follow a player**: click a name and the map keeps them centred.

## Requirements

- A Rust dedicated server with [Oxide/uMod](https://umod.org).
- **WebRCON** turned on. It is on by default in current Rust (`rcon.web 1`).
- An RCON port and password, set with `+rcon.port` and `+rcon.password` in your server's startup line.
- A modern browser: Chrome, Edge or Firefox.

## Installation

### 1. Install the plugin

Copy `AdminLiveMap.cs` to your server's `oxide/plugins` folder. Oxide loads it automatically. Your server console should show:

```
Loaded plugin Admin Live Map v1.0.0 by Guerisse
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
| Show or hide layers | The tick boxes: grid, monuments, sleepers, names, trails, deaths, bases, sounds |
| Jump to an event | Click a line in the **Events** tab |

| Marker | Meaning |
| --- | --- |
| Filled dot with an arrow | Online. The arrow shows where they are looking. |
| Hollow ring with "(zz)" | Offline sleeper |
| Red X | Dead |
| Skull | Where someone died in the last hour |
| Box with a small square | A base. The square is the tool cupboard. Grey dashed boxes have no player owner (for example event bases). |

## Console commands

The commands only work from RCON and the server console. Players can't use them from the in-game F1 console, because the answers reveal every player's position and base. So there are no permissions to set up.

### Commands for you

| Command | What it does |
| --- | --- |
| `livemap.map` | Says whether the map image is ready |
| `livemap.map rerender` | Draws the map image again, for example after a big map change. Allowed once every 5 minutes, because drawing pauses the server for a few seconds. Reload the page afterwards. |

### Commands the page uses

These answer with JSON. You can send them over RCON yourself, for example to build your own tools.

| Command | Answers with |
| --- | --- |
| `livemap.players` | All players (online and sleeping) and recent deaths |
| `livemap.trails` | Recent movement of online players. Typed in the server console, it prints a short summary instead. |
| `livemap.bases` | Tool cupboards, their owners and building sizes |
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
  "Keep deaths for (minutes)": 120
}
```

- **Map image scale:** higher values give a sharper map, but a bigger image to send and a longer pause when it is drawn. 0.5 suits most maps.
- **Trails:** are kept in memory only. A plugin reload or server restart clears them.
- **Deaths:** are saved in `oxide/data/AdminLiveMap/deaths.json`, so they survive a restart.
- **Map image:** is cached in `oxide/data/AdminLiveMap/map.json` (about 1 MB) and replaced automatically when the map changes.

Reload the plugin after changing the file.

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
