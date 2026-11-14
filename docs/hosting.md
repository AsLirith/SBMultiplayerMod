# Hosting a server

## Running it

Either download `SBMultiplayer-Server-<version>.zip` from the [releases](https://github.com/AsLirith/SBMultiplayerMod/releases/latest) (requires the .NET 8 runtime or newer):

```sh
dotnet SBMultiplayer.Server.dll --maps o:1,o:2,o:3 --round 300
```

or build it from source:

```sh
dotnet run --project src/SBMultiplayer.Server -c Release -- --maps o:1,o:2,o:3 --round 300
```

| Option | Default | Description |
|---|---|---|
| `--port <n>` | 24800 | UDP port |
| `--max-players <n>` | 32 | Maximum number of players |
| `--maps <list>` | (none) | Map rotation, e.g. `o:1,o:2,o:57` (official level IDs). Without it, the server runs in free mode: no rounds, everyone rides wherever they want |
| `--round <s>` | 300 | Round duration in seconds |
| `--results <s>` | 15 | Results screen duration in seconds |
| `--echo` | off | Sends each player their own board back with a delay, as a fake "Echo" player, to test alone |
| `--echo-delay <s>` | 2 | Echo delay in seconds |

Console commands: `list` (connected players), `round` (current round), `next` (skip to the next phase: round → results → next map), `quit`.

A player's current map key (e.g. `o:1:2001`) is shown in the server console when they join a level. Use `o:<id>` in `--maps` to accept any version of that level.

## Network

1. **Firewall** (Linux with ufw): `sudo ufw allow 24800/udp`
2. **Router**: forward **UDP** port 24800 to the server's local IP.
3. Players enter `PUBLIC_IP:24800` as the server address in the in-game menu (F8), or in `SBMultiplayer/SBMultiplayer.json`.

### ⚠️ PC connected through both Ethernet AND Wi-Fi

The server's replies leave through the interface of the default route (usually Ethernet). If the router forwards the port to the **Wi-Fi** IP, players reach the server but never receive its replies. In the server log, this shows as `Connection from …` lines followed by `Disconnection of  (#n): Reconnect` every ~5 s, without a player name.

Check which interface is used with `ip route get <player IP>`, and forward the port to that `src` address. You can also just turn the Wi-Fi off.

### Without port forwarding

If your router is behind CGNAT, or port forwarding isn't possible: install [Tailscale](https://tailscale.com/) (or ZeroTier) on the server and on the players' machines, then use the server's Tailscale IP.

## Tested

2026-11-14: 3 players (Linux, over the internet), 6 official maps. Smooth, with only the intended 100 ms interpolation delay.
