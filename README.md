# SBMultiplayer

Unofficial multiplayer mod for [STUNTBOOST](https://store.steampowered.com/app/2999500/STUNTBOOST/). Ride the same map at the same time as other players, see them as ghosts (no collisions), and race for the best time, play rounds, cups and eliminations, or a game of hide and seek.

Works on Linux (native version of the game) and Windows.

✅ **Your times still count**: with the agreement of STUNTBOOST's developers, runs made with the mod are uploaded to the official leaderboards and saved as personal records, like without it. Exceptions: Hide and Seek, runs after using the spectator camera on a map (restart the map), and any other Harmony mod loaded alongside. The game's own checks (Modding Mode, changed gameplay settings...) still apply. Turn it off in F8 > Settings > Interface. The developers can pause uploads for the mod at any time through [leaderboards.json](leaderboards.json), read by the mod from GitHub every 15 minutes (nothing about you is sent).

## Features

**Game modes**
- **Free**: no rounds, everyone rides wherever they want.
- **Time Attack**: unlimited retries during the round, the best time wins.
- **Rounds**: synchronized start with a countdown, one attempt per round, points by race time.
- **Cup**: like Rounds; reach the points limit to become a finalist, then win a round to win the cup.
- **Elimination**: the slowest players are knocked out each round, until one is left.
- **Hide and Seek**: the mice hide, then get frozen in place, and the hunter has to find them before time runs out.

**Playing together**
- **Ghosts** of the other players in real time, smoothly interpolated, with their own skin, their name and their distance.
- **Official levels and Workshop maps**: the round's map loads automatically (Workshop maps are downloaded if needed).
- **Live leaderboard**, checkpoint splits against your best run, match points, and an **end-of-game screen** with the winner.
- **Spectator mode**: watch through any player's camera, or fly around with the free camera. Eliminated players spectate automatically.
- **Chat** with commands: find a player (`/locate`), join their map (`/goto`), ignore someone (`/ignore`)... and a **player list** showing everyone's map, status and points.

**Hosting**
- **Host from the game** in one click: you're the admin of your game, no extra program needed.
- **Steam friends join in one click**, without opening any port (Steam P2P). Tick "Public game (anyone on Steam)" and **anyone can find and join it**.
- **SBLB lobbies**: create a lobby on the community website [sblb.thefanclub.cc](https://sblb.thefanclub.cc) right from the game and share its code, or join one with a code. Nobody opens a port.
- **Public SBLB lobbies** are listed in Join a game, and the website's lobby page can make the running game join a lobby in one click.
- **Tournaments** on SBLB: hosts create and run cups from the game, with a roster locked to Steam accounts, time checks always enforced, standings after every round and a tournament leaderboard.
- **Dedicated server** (console app, no game needed) for servers that run all the time or for tournaments: map rotation, password, results export (JSON/CSV).
- **Admin panel in game**: change the mode, maps, timings and points; skip, restart, pause or extend a round; kick players.
- **Time checks for competitions**: each finish comes with the run's replay and the game settings; the server checks them (right map, same time, standard physics settings, normal game speed) and keeps the replays of the best times. After a tournament, `verify-replays` re-simulates them with the game to prove every time ([details](docs/replay-verification.md)).

**And also**
- Interface in **English or French**, following the game's language.
- **New version notice** in the chat when an update is out.
- **Versions stay compatible**: players and servers don't have to update at the same time.
- Light on bandwidth: you only receive the players on your own map.

<!-- Screenshots: to be added after the next group session. -->

## Installation

1. Download `SBMultiplayer-<version>.zip` from the latest [release](../../releases/latest).
2. Copy the `SBMultiplayer` folder into the game folder (Steam > STUNTBOOST > right click > Manage > Browse local files).
3. In Steam > STUNTBOOST > Properties > Launch options, paste the line for your system and replace the path with the real path to your game folder:
   - **Linux**:
     ```
     DOTNET_STARTUP_HOOKS="/path/to/STUNTBOOST/SBMultiplayer/SBMultiplayer.Client.dll" %command%
     ```
   - **Windows** (no space before `&&`):
     ```
     cmd /c "set DOTNET_STARTUP_HOOKS=C:\path\to\STUNTBOOST\SBMultiplayer\SBMultiplayer.Client.dll&& %command%"
     ```
     With a default Steam install, `C:\path\to\STUNTBOOST` is `C:\Program Files (x86)\Steam\steamapps\common\STUNTBOOST`.
4. Start the game. The game's "modded" notice shows "SBMultiplayer" with its version: the mod is loaded.

**Updating**: overwrite the files in the `SBMultiplayer` folder; your settings are kept. Since 0.19.0, newer versions play with older ones: a feature the other side doesn't have yet is simply hidden.

**Uninstalling**: remove the launch option (and the `SBMultiplayer` folder if you like).

## Getting started

Press **F8** to open the multiplayer menu (F8 again closes it), or click **Multiplayer** in the game's main menu or pause menu, then:
- **Join a game**: the list shows your Steam friends who are hosting and the public games (one click, nothing to set up); below it, type an **SBLB lobby code** (a lobby of the community website) or a **server address** (`address:port`). If the game has a password, you're asked for it;
- **Host a game**: your own game on your PC, or an SBLB lobby with a code to share. You're its admin (choose the mode and maps in F8 > Admin).

| Key | Action |
|---|---|
| **F8** | Multiplayer menu (connection, host, spectate, settings, admin) |
| **F7** | Go to the current round's map |
| **Enter** | Chat (`/help` lists the commands) |
| **Tab** (hold) | Player list |
| **Escape** | Close the end-of-game screen |
| **Left / Right**, **F** | Spectator: switch player, free camera |
| **Right click** | Hide and Seek hunter: catch the mouse you're aiming at |
| **F** (the game's free camera key) | Hide and Seek mouse: lock your hiding place before the hunt |
| **Q** twice or held (the game's restart key) | Rounds, Cup, Elimination: give up the round (one attempt per round) |

Every key of the mod can be changed in **F8 > Keys**.

## Troubleshooting

- **Nothing happens when pressing F8**: check the launch option (the path must point to `SBMultiplayer.Client.dll`; on Windows, no space before `&&`).
- **"Incompatible mod version"**: you or the server runs a version older than 0.19.0. Update to the latest release.
- **The mod says it's inactive after a game update**: the game changed something the mod relies on. Wait for a mod update (the mod tells you when one is out).
- **Can't connect to a friend's server**: use Steam P2P (F8 > Join a game), or see the network section of the [hosting guide](docs/hosting.md).
- **Anything else**: type `/report` in the chat when the problem happens, then open an [issue](../../issues) with your `SBMultiplayer/SBMultiplayer.log` file (F8 > Settings > Open the log folder). It's the only file needed, whether you were hosting or playing: it keeps your last sessions, your settings, a status line every minute, and the game's own log and crash report when something went wrong.

## Hosting a server

The simplest: **F8 > Host a game** in the game. For a dedicated server, grab `SBMultiplayer-Server-<version>.zip` from the [releases](../../releases/latest) (requires the .NET 8 runtime or newer):

```sh
dotnet SBMultiplayer.Server.dll --init-config server.json   # commented template: maps, passwords, durations...
dotnet SBMultiplayer.Server.dll --config server.json
```

The [hosting guide](docs/hosting.md) covers the settings, game modes, admin commands, exported results and the network setup (firewall, port forwarding, Tailscale...).

## Building

Requirements: .NET 8 SDK or newer, and the game installed.

```sh
dotnet build -p:GameDir=/path/to/steamapps/common/STUNTBOOST   # builds and copies the mod into the game folder
./packaging/package.sh                                           # distribution zips in dist/
```

## How it works

The mod is loaded through a [.NET startup hook](https://github.com/dotnet/runtime/blob/main/docs/design/features/host-startup-hook.md) and patches the game in memory with [Harmony](https://github.com/pardeike/Harmony), without modifying any game file. Networking uses [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (UDP), tunneled through Steam networking for Steam P2P games. Technical notes are in [docs/00-reverse-engineering.md](docs/00-reverse-engineering.md), and the compatibility rules of the network protocol in [docs/protocol.md](docs/protocol.md).

## Thanks

A big thank you to **TheFanMan**, who runs [sblb.thefanclub.cc](https://sblb.thefanclub.cc), the community's leaderboard, skin and map hub. He integrated the mod into his server and contributed the SBLB lobbies, so anyone can create a lobby from the game without opening a port. Thanks as well for everything he does for the STUNTBOOST community in general.

Thanks also to everyone who joined the playtests and sent feedback.

## Legal

Code released under the [MIT license](LICENSE). This repository contains no code or assets from the game, and must never contain any (no decompiled code).
STUNTBOOST is the property of Byting Games. This project is not affiliated with or endorsed by the studio.
