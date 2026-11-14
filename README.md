# SBMultiplayer

Unofficial multiplayer mod for [STUNTBOOST](https://store.steampowered.com/app/2999500/STUNTBOOST/), in a "time attack" style: everyone rides the same map at the same time, sees the other players as ghosts (no collisions), and fights for the best time of the round.

> Early version, already playable with friends. Tested on Linux (native version of the game). Windows: not confirmed yet, feedback welcome.

## Features

- **Game modes**: **Free** ride, **Time Attack** (unlimited retries, best time), **Rounds** (synchronized start with a countdown, one attempt per round, points by race time), **Cup** (finalists and match point) and **Elimination** (the slowest players are knocked out each round).
- **Server settings editable in game** by admins (mode, map rotation, timings, points), optionally saved to the config file.
- **Host from the game** (F8 > Host a game): you're the admin of your game, no extra program needed.
- **Dedicated server** (console app, no game needed): map rotation, live leaderboard, results screen, match points, results export (JSON/CSV), optional password.
- **Other players' ghosts** in real time, smoothly interpolated, with **their own skin** and **their name** when they're close.
- **Official levels and Workshop maps**: the round's map is loaded automatically (Workshop maps are downloaded if needed), instant retry when you cross the finish line instead of the end screen.
- **In-game menu (F8)**: server address, name, connect/disconnect, other players' visibility and opacity, name tags, round panel size.
- **Checkpoint splits** compared to your best run of the round.
- **End-of-game screen** with the winner and the final standings (end of a Time Attack round, of a Rounds rotation, of a Cup or an Elimination).
- **Spectator mode** (F8 > Spectate): invisible and out of the rounds, watch through any player's own camera (Left / Right to switch) or the game's free camera (F). Eliminated players spectate automatically.
- **Chat (Tab)** with admin commands (`/next`, `/map`, `/kick`… see `/help`).
- **Admin panel (F8 > Admin)**: skip, restart, pause, extend the round, choose the map, kick players, reset the match.
- **F7** to rejoin the round's map. On-screen notifications for connection, players, rounds and your times.
- Interface in **English or French**, following the game's language.

⚠️ **While the mod is loaded, the game does not save any time**: not on the official leaderboards (the game blocks submissions by itself when a mod is detected), and not locally either.

## Installation (players)

1. Download the zip from the latest [release](../../releases/latest).
2. Copy the `SBMultiplayer` folder into the game folder (Steam > STUNTBOOST > Manage > Browse local files).
3. Set the Steam launch options:
   - **Linux**: `DOTNET_STARTUP_HOOKS="/path/to/STUNTBOOST/SBMultiplayer/SBMultiplayer.Client.dll" %command%`
   - **Windows**: `cmd /c "set DOTNET_STARTUP_HOOKS=C:\path\to\STUNTBOOST\SBMultiplayer\SBMultiplayer.Client.dll&& %command%"` (no space before `&&`)
   **Dont forget to replace `C:\path\to` by the real path to your STUNTBOOST files. F.ex : `C:\Program Files (x86)\Steam\steamapps\common\`**
4. Start the game, press **F8** and enter the server address.

To uninstall, remove the launch option. The log file is `SBMultiplayer/SBMultiplayer.log`.

## Hosting a server

The simplest: **F8 > Host a game** in the game. For a dedicated server, see [docs/hosting.md](docs/hosting.md) (firewall, port forwarding, Tailscale…), or grab the server zip from the [releases](../../releases/latest).

```sh
dotnet SBMultiplayer.Server.dll --init-config server.json   # commented template: maps, passwords, durations...
dotnet SBMultiplayer.Server.dll --config server.json
```

Matches, admin commands and exported results are described in the hosting guide.

## Building

Requirements: .NET 8 SDK or newer, and the game installed.

```sh
dotnet build -p:GameDir=/path/to/steamapps/common/STUNTBOOST   # builds and copies the mod into the game folder
./packaging/package.sh                                           # distribution zips in dist/
```

## How it works

The mod is loaded through a [.NET startup hook](https://github.com/dotnet/runtime/blob/main/docs/design/features/host-startup-hook.md) and patches the game in memory with [Harmony](https://github.com/pardeike/Harmony), without modifying any game file. Networking uses [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (UDP). Technical notes are in [docs/00-reverse-engineering.md](docs/00-reverse-engineering.md).

## Legal

Code released under the [MIT license](LICENSE). This repository contains no code or assets from the game, and must never contain any (no decompiled code).
STUNTBOOST is the property of Byting Games. This project is not affiliated with or endorsed by the studio.
