# Replay verification

Re-simulates STUNTBOOST replays (`.rpl`) with the real game to check that a run is genuine: the recorded inputs must
put the board exactly (bit for bit) where the file says it was on every update, reach the goal, and give the claimed time.
A replay whose time was edited, or whose trajectory was not produced by its inputs, fails.

This is the second step of the [time checks](hosting.md#time-checks): the server checks each finish against its replay
(right map, same time, standard settings, normal game speed) and saves the replays of the best times; this tool then
replays them with the game itself, which the server can't do.

It uses the replay test harness that ships with the game (the one its developers use): no Steam, no audio, no visible
window, 100 updates per drawn frame (a 30 s run takes about 3 s, game start included). The multiplayer mod stays off.
A display and a graphics card are still needed (the window is hidden, not headless).

## Usage

The scripts are in the game's `SBMultiplayer` folder (the mod must be installed). Steam doesn't need to be running.

**After a tournament**, give it the server's results folder (`ResultsDirectory`, `results` by default):

```sh
./verify-replays.sh /path/to/results          # Linux
verify-replays.bat C:\path\to\results         # Windows (or drag the folder onto the file)
```

It re-simulates the replay saved for every result of every round and writes `verification.csv` in that folder:
round, map, rank, name, time, the server's check and the re-simulation verdict. The time that counted must be the
re-simulated time plus the time lost at the start (synchronized modes), which the export also records.

**Any replays**: `./verify-replays.sh file.rpl folder/ ...` writes `verification.jsonl` in the current folder (one
JSON line per replay).

Options (environment variables):
- `SBMULTI_VERIFY`: path of the JSON report (set by the scripts).
- `SBMULTI_VERIFY_WORKSHOP`: Workshop maps folder (default: `steamapps/workshop/content/2999500` next to the game).
  The installed map version must be the one the replay was recorded on.
- `SBMULTI_VERIFY_SHOW=1`: keeps the window visible, to watch the re-simulation.
- `SBMULTI_VERIFY_TIMEOUT` (seconds, default 120): a replay the game can't open leaves it in a menu; it's stopped after that.

## Verdicts

| Verdict | Meaning |
|---|---|
| `verified` / `valid` | inputs reproduce the recorded trajectory, the goal is reached, re-simulated time = time that counted |
| `time-mismatch` | trajectory reproduced, goal reached, but another time was claimed (edited file) |
| `penalty-mismatch` | like `valid`, but the file counts more 1 s respawn penalties than the re-simulation (file slower, never faster; seen once, cause unknown) |
| `desync` | the replayed board left the recorded trajectory (edited or forged file, or another game, physics or map version) |
| `no-goal` | the file claims a finish but the re-simulation doesn't reach the goal |
| `dnf` | the file doesn't claim a finish (abandoned attempt): nothing to verify |
| `no replay` | the server has no replay for this result (older mod, or time checks off) |
| `not-checked` | the game didn't start its desync detection for this replay |
| `error` | unreadable replay, unknown map or map version, crash |

## Known limits

- Grind Park (`o:83`) can't be verified: its finish isn't detected by the game's test harness (`no-goal`, without desync).
- Tested on Linux; Windows is untested so far.
- A replay recorded on an older version of a map (or of the game) can't be re-simulated on the current one.
