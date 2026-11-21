# Network protocol and compatibility

Since 0.19.0 (protocol 13), players and servers no longer have to update together: a newer mod plays with an older
server and the other way around. This page lists the rules that keep it that way. Every change to
`src/SBMultiplayer.Protocol/Messages.cs` must follow them.

## The promise

- `ProtocolInfo.Version` (13) only changes for a breaking change. That would be a 2.0, and it's announced as such.
- Every 1.x mod (from 0.19.0) connects to every 1.x server.
- Something one side doesn't know is skipped or hidden. It never cuts the connection.

## Allowed changes (no protocol bump)

**New field in an existing message**: append it **at the end** of the message, after every existing field.
Read it with the `OptionalFields` helpers (`GetIntOr`, `GetStringOr`...). An older peer sends the message without the
field, and LiteNetLib doesn't fail when reading past the end of a packet: it returns garbage. So never read a new
field with a plain `GetInt()`.

**New field in a list element** (players in `Welcome`, `Leaderboard` and `MatchStandings` entries, `States`
entries): every element is written as a block (`BeginBlock`/`EndBlock`, or `BeginShortBlock` for small frequent
elements). Append the field at the end of the block, and read it with the `OptionalFields` helpers on the block's
reader. Older readers skip what they don't know.

**New message type**: add a value to `MessageType`. Older clients and servers ignore unknown types (the server logs
them once per player). If the feature can't work without the other side, add a capability (see below), and check
it before sending or showing the feature.

**New server event or command reply**: add a code to `ServerEvents` / `CommandResults`, and pass an English
`fallback` text to `Msg.Event` / `Msg.Reply`. Older clients show that text instead of the translation.

**New game mode**: add a value to `GameMode` and to `GameModes.Known`. The server announces its modes in `Welcome`, so
admins with a newer mod only see the modes the server can run. A client that doesn't know the server's mode becomes
a spectator automatically and asks the player to update.

**New match setting**: add a property to `MatchSettings` (exchanged as JSON). Admins send only what they changed
(`SettingsPatch.Diff`), and the server merges it into its settings (`SettingsPatch.Merge`). An older admin client
never resets a setting it doesn't know.

**New optional feature**: add a string to the features in `ProtocolInfo.OwnCapabilities`. Clients send theirs in
`Hello`, the server sends its own in `Welcome`. Use `Capabilities.Has("name")` to decide whether to send a new
message or to show a button. Known features: `dnf-fills-quota` (0.21.0, the server knows `MatchSettings.DnfFillsQuota`), `run-proof` (1.2.0: the
server checks finish times, see below).

## Time checks (1.2.0)

- `Finish` gets three fields appended: `lostMs` (part of the time lost by respawning at the start in synchronized modes,
  not in the replay), `runId` (0 = no replay follows) and `nonStandard` (game settings the official leaderboards refuse,
  one per line). A 1.1 server stops reading before them; a 1.1 client's finish gets the defaults.
- `ReplayChunk` (25, C→S): `runId`, total length, offset, then up to 16 KB of the run's `.rpl` file. Sent only to a
  server with `run-proof`, right after the `Finish`, in order.
- `MatchSettings.RunCheck` (`Off`, `Report`, `Enforce`).
- Replies `TIME_REJECTED` (to the player, argument: why) and `TIME_FLAGGED` (to the admins, argument:
  `name\ntime\nreasons`), with an English fallback.
- `ReplayFile` (Protocol project) reads the metadata of a replay without the game.

## Web rooms (0.22.0)

A room made on the website is reached over WebSocket instead of UDP. The protocol is the same: each binary WebSocket
message carries one game message, in the same format. Only the connection differs, so this doesn't change
`ProtocolInfo.Version`.

- The client sends `x-sbm-version` (equal to `ProtocolInfo.ConnectionKey`) and, if the room has one, `x-room-password`.
  The website checks the password itself before it bridges the connection to the game server.
- Refusals use the HTTP status of the handshake (401 wrong password, 404 no room, 426 version, 429 rate limit, 503 full),
  or a WebSocket close reason. The client translates them with the same codes as UDP.
- New reason codes: `ROOM_NOT_FOUND`, `RATE_LIMITED`, `UNREACHABLE`. An older client shows the code as plain text.
- Public lobbies (lobbies without a password) are read from the website's `GET /multiplayer/api/lobbies`, outside the
  game protocol. Each entry has `code`, `mode`, `modeId` (a `GameMode` name), `map` (a map key), `players` and
  `maxPlayers`; read them leniently, since the website may add fields. A website without the list answers 404, and the
  mod then hides the section.

## Tournaments

These additions follow the allowed changes above, so `ProtocolInfo.Version` stays 13. Every new event and reply carries
an English fallback. An older mod can't reach a tournament room anyway, because the website requires a Steam ticket
to join one.

- Admin commands `StartTournament` (9: locks the roster to the players present and not spectating, and starts the first
  round; spectators stay and watch, and the server answers their `Spectate(false)` with `Spectate(own id, true)`) and
  `EndTournament` (10: ends it early, nothing is recorded). Only in tournament rooms; elsewhere the reply is `UNKNOWN`.
- Reason codes `TOURNAMENT_STARTED` (the tournament started and this Steam account isn't on its roster),
  `TOURNAMENT_NEEDS_STEAM` (the connection carries no verified Steam account) and `REPLACED` (the same Steam account
  connected again; the older connection is closed and doesn't retry).
- Events `TOURNAMENT_STARTED` (argument: the host), `TOURNAMENT_FINISHED` (argument: the winner) and
  `TOURNAMENT_ENDED` (argument: the host), with an English fallback.
- In a tournament room the player's name comes from their Steam account (verified by the website), not from `Hello`.

## Breaking changes (to avoid)

- Removing, reordering or changing the type of an existing field.
- Changing the meaning of an existing value (message type, enum value, event code).
- Changing the connection data (`ProtocolInfo.ConnectionData`).
- Making a States entry bigger than 255 bytes, or a States packet bigger than 1023 bytes (LiteNetLib's largest
  unreliable packet before MTU discovery). Use a new message type instead.

## Configuration files

- `SBMultiplayer.json` (mod): `ConfigVersion` gives the file's format. A new format comes with a migration in
  `ModConfig.Migrate`, so settings are never lost. Settings written by a newer version are kept as they are when an
  older version saves the file. An unreadable file is copied to `SBMultiplayer.json.unreadable` before the defaults
  are used.
- `server.json` and `hosted-server.json` (server): existing keys keep their name and meaning. Unknown keys are
  ignored, and a missing key gets its default value.
