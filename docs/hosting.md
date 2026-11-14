# Hosting a server

## Running it

Download `SBMultiplayer-Server-<version>.zip` from the [releases](https://github.com/AsLirith/SBMultiplayerMod/releases/latest) (requires the .NET 8 runtime or newer), or build it from source (`dotnet run --project src/SBMultiplayer.Server -c Release -- <options>`).

The easiest way is a configuration file:

```sh
dotnet SBMultiplayer.Server.dll --init-config server.json   # writes a commented template
# edit server.json (maps, passwords, durations...), then:
dotnet SBMultiplayer.Server.dll --config server.json
```

Every setting can also be given (or overridden) on the command line:

| Option | Config key | Default | Description |
|---|---|---|---|
| `--port <n>` | `Port` | 24800 | UDP port |
| `--max-players <n>` | `MaxPlayers` | 32 | Maximum number of players |
| `--password <text>` | `Password` | (none) | Password required to join |
| `--admin-password <text>` | `AdminPassword` | (none) | Password to use the in-game admin panel (F8 > Admin) |
| `--maps <list>` | `Maps` | (none) | Map rotation, e.g. `o:1,o:2,o:57` (official level IDs). Without it, the server runs in free mode: no rounds, everyone rides wherever they want |
| `--round <s>` | `RoundSeconds` | 300 | Round duration in seconds |
| `--results <s>` | `ResultsSeconds` | 15 | Results screen duration in seconds |
| | `PointsTable` | 10, 8, 6, 5, 4, 3, 2, 1 | Match points by rank at the end of each round |
| | `ResultsDirectory` | `results` | Where results are exported (empty = no export) |
| `--echo` | `Echo` | off | Sends each player their own board back with a delay, as a fake "Echo" player, to test alone |

A player's current map key (e.g. `o:1:2001`) is shown in the server console when they enter a level. Use `o:<id>` in the rotation to accept any version of that level.

## Running a match

At the end of each round, players get **match points** according to their rank (`PointsTable`). The match standings are shown to players on the results screen, and kept until you reset them.

Admins control the match from the game, either with the admin panel (**F8 > Admin**) or with chat commands (**Tab** opens the chat), after logging in with `AdminPassword` (in the panel, or `/login <password>` in the chat). The server console has the same commands:

| Console | Chat | In-game admin panel | Effect |
|---|---|---|---|
| `next` | `/next` | Next (skip) | Ends the round now (results), or skips the results screen |
| `restart` | `/restart` | Restart round | Restarts the current round on the same map (times cleared, no points given) |
| `pause` | `/pause` | Pause / Resume | Freezes or resumes the round timer (players can still ride) |
| `extend <s>` | `/extend [s]` | +1 min | Adds time to the running round (chat: 60 s by default) |
| `map <index>` | `/map <number>` | Go to map | Starts a round on that map of the rotation (console: index from 0, chat: number from 1) |
| `kick <id>` | `/kick <name>` | Kick | Disconnects a player (console: id from `list`; chat: name or start of the name) |
| `resetmatch` | `/resetmatch` | Reset match scores | Clears the match standings |

Everyone can use `/help` in the chat. Chat messages are shown in the server console (`[chat]`), with a limit of 5 messages per 5 seconds per player.

Other console commands: `list` (connected players), `round` (current round), `standings` (match standings), `quit`.

### Exported results

When `ResultsDirectory` is set, the server writes after every round:

- `<date>_round<N>_<map>.json`: rank, name, time and points of every player in that round;
- `match_standings.csv`: the current match standings (rank, name, points, rounds played).

## Network

1. **Firewall** (Linux with ufw): `sudo ufw allow 24800/udp`
2. **Router**: forward **UDP** port 24800 to the server's local IP.
3. Players enter `PUBLIC_IP:24800` as the server address (and the password, if any) in the in-game menu (F8).

### ⚠️ PC connected through both Ethernet AND Wi-Fi

The server's replies leave through the interface of the default route (usually Ethernet). If the router forwards the port to the **Wi-Fi** IP, players reach the server but never receive its replies. In the server log, this shows as `Connection from …` lines followed by `Disconnection of  (#n): Reconnect` every ~5 s, without a player name.

Check which interface is used with `ip route get <player IP>`, and forward the port to that `src` address. You can also just turn the Wi-Fi off.

### Without port forwarding

If your router is behind CGNAT, or port forwarding isn't possible: install [Tailscale](https://tailscale.com/) (or ZeroTier) on the server and on the players' machines, then use the server's Tailscale IP.

## Tested

2026-11-14: 3 players (Linux, over the internet), 6 official maps. Smooth, with only the intended 100 ms interpolation delay.
