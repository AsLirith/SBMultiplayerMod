# SBMultiplayer

Unofficial multiplayer mod for [STUNTBOOST](https://store.steampowered.com/app/2999500/STUNTBOOST/), in a "time attack" style: everyone rides the same map at the same time, sees the other players as ghosts (no collisions), and fights for the best time of the round.

> Early version, already playable with friends. Tested on Linux (native version of the game). Windows: not confirmed yet, feedback welcome.

## Features

- **Dedicated server** (console app, no game needed): map rotation, timed rounds, live leaderboard, results screen.
- **Other players' ghosts** in real time, smoothly interpolated, with **their own skin** and **their name** when they're close.
- **Round map loaded automatically**, instant retry when you cross the finish line instead of the end screen.
- **In-game menu (F8)**: server address, name, connect/disconnect, other players' visibility and opacity, name tags, round panel size.
- **F7** to rejoin the round's map. On-screen notifications for connection, players, rounds and your times.

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

See [docs/hosting.md](docs/hosting.md) (firewall, port forwarding, Tailscale…), or grab the server zip from the [releases](../../releases/latest).

```sh
dotnet run --project src/SBMultiplayer.Server -c Release -- --maps o:1,o:2,o:3 --round 300
```

Main options: `--port` (default 24800, UDP), `--maps` (rotation of official level IDs), `--round` and `--results` (durations in seconds), `--max-players`. Console commands: `list`, `round`, `next`, `quit`.

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
