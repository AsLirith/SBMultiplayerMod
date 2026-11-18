# Replay verification (prototype)

Re-simulates STUNTBOOST replays (`.rpl`) with the real game to check that a run is genuine: the recorded inputs must
put the board exactly (bit for bit) where the file says it was on every update, reach the goal, and give the claimed time.
A replay whose time was edited, or whose trajectory was not produced by its inputs, fails.

It uses the replay test harness that ships with the game (the one its developers use): no Steam, no audio,
100 updates per drawn frame, desync detection forced to `Strict`. The multiplayer mod stays completely off.

## Usage (Linux)

```sh
cd /path/to/STUNTBOOST
DOTNET_STARTUP_HOOKS=/path/to/SBMultiplayer/SBMultiplayer.Client.dll \
SBMULTI_VERIFY=/tmp/report.jsonl \
./STUNTBOOST replay1.rpl replay2.rpl some/folder/
```

- One JSON line per replay is appended to the report, then the game quits.
- Several replays: one game process per replay (the game can't be created twice in one process).
- Workshop maps are read from `steamapps/workshop/content/2999500` next to the game, or from `SBMULTI_VERIFY_WORKSHOP`.
  The installed map version must be the one the replay was recorded on.
- `SBMULTI_VERIFY_SHOW=1` keeps the window visible.

## Verdicts

| Verdict | Meaning |
|---|---|
| `valid` | inputs reproduce the recorded trajectory, the goal is reached, re-simulated time = file time |
| `time-mismatch` | trajectory reproduced, goal reached, but the file claims another time |
| `desync` | the replayed board left the recorded trajectory (edited or forged file, or another game/physics/map version) |
| `no-goal` | the file claims a finish but the re-simulation doesn't reach the goal |
| `dnf` | the file doesn't claim a finish (abandoned attempt): nothing to verify |
| `not-checked` | the game didn't start its desync detection for this replay |
| `error` | unreadable replay, unknown map or map version, crash |
