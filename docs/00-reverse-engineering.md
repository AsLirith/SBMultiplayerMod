# STUNTBOOST reverse engineering notes

Notes gathered while building the mod, from a local decompilation of the game (`ilspycmd` 11.2, 2026-11-14).
The decompiled sources are **never committed or redistributed**; to read them, decompile your own copy of the game into `_decompiled/` (git-ignored).
Line numbers refer to `_decompiled/STUNTBOOST/SE/` and change with every game update: search by name instead.

## Overview

| Item | Value |
|---|---|
| Runtime | .NET 8.0.30 CoreCLR, self-contained (`STUNTBOOST` = apphost) |
| Engine | MonoGame + `BytingLib` (the studio's in-house library) |
| Size | `STUNTBOOST.dll` ≈ 121k lines, `BytingLib.dll` ≈ 48k lines |
| Obfuscation | None |
| Tick | Fixed **60 Hz** (`Program.cs`: `new TargetGameSpeed(1.0 / 60.0, ...)`) |
| World scale | 1 unit = the game's "cm" = one board length (measured: 15 boards in 3 s at 5 cm/s; speeds are shown in "cm/s" = units per tick × 60; the player is about 1 unit tall) |
| Steam | `Steamworks.NET`; no use of `SteamNetworking` / `SteamMatchmaking` |

## Loading the mod

- `STUNTBOOST.runtimeconfig.json` does **not** disable startup hooks, so `DOTNET_STARTUP_HOOKS=<path>/SBMultiplayer.Client.dll %command%` in the Steam launch options works.
- `libhostfxr.so` is a custom build (`0.0.0-dev`), and `libhostfxr.so.bak` is the official 8.0.30. ✅ Not a problem: the startup hook and Harmony 2.3.6 load fine (tested on native Linux).
- Main-thread dispatch: `BytingLib.MainThread`, and the `GlobalGame.DoNextUpdate` queue.

## Mod detection (`RunUploadBlockers.cs`)

- Run submission is blocked if any of these assemblies is loaded: `0Harmony`, `BepInEx*`, `MelonLoader`, `MonoMod.*`. Same if `SaveState.Gameplay.ModdingMode` (official "Modding Mode" option) is on, or if some gameplay settings differ from their defaults.
- Used by `GlobalGame.SubmitRunIfRecord`. When blocked, the run is **neither uploaded nor saved locally** ("run not stored or uploaded").
- Since 1.4.0 (agreed with the developers), the mod's postfix removes the "mod: 0Harmony" reason when `Net/LeaderboardUploads.BlockReason()` has nothing against the run: uploads on in the mod's settings, remote switch `leaderboards.json` (allowRunUploads, minimumVersion) allowing it, every Harmony patch owned by the mod, no Hide and Seek round, free camera not unlocked by the spectator mode since the last fresh start. Otherwise it's replaced by the mod's reason. The game's other reasons are never touched. The notice is built once per level scene (and on Modding Mode changes); submission calls `GetReasons` again at the finish.

## Game loop

- `Program.Main` → `MyGameWrapper : BytingLib.GameWrapper : MonoGame.Game` → `GlobalGame : GamePrototype`.
- `GlobalGame.UpdateIteration(GameTime)` → `UpdateIterationInner`: once per tick. It runs `stuff.ForEach(IUpdate)` (global objects + current scene), then `steam?.Update()`.
- `GlobalGame.GetTopmostScene()` (3 lines) is called by the engine **once per tick, right after the update**, whatever the scene. ✅ The mod postfixes it as its "global tick" (menus included), which also captures the `GlobalGame` instance.
- `GlobalGame.ChangeScene(Func<Scene>)` switches scenes; the `currentScene` field holds the active one.
- `GlobalGame.stuff` (`StuffDisposable`) holds global objects. Its `IDrawBatch2` items are drawn **on top of every scene** (`DrawIteration`), which is where the mod's round panel and notifications live.
- The game **pauses when its window loses focus**, and the mod pauses with it.

## Scene architecture: broadcast interfaces

A scene calls `scene.ForEach((IXxx f) => ...)` on all its objects. **Adding an object implementing these interfaces to the scene is enough to receive the events, without any patch:**

| Interface | Called when |
|---|---|
| `IUpdate.Update()` | every tick |
| `ICheckpointStorage.StoreCheckpoint(info)` | checkpoint taken (`CheckpointManager.ActivateCheckpoint`) |
| `ICheckpointStorage.LoadCheckpoint(info)` | respawn; `info is FirstCheckpoint` means **the run restarted** |
| `IGoalReached.GoalReached()` | finish line crossed |
| `IDrawWorldSkinRender`, `IBeforeDraw(popup, extrapolation)` | 3D rendering (used by `GhostBoard`) |
| `IDrawBatch.DrawBatch(spriteBatch, extrapolation)` | 2D drawing at the end of the scene's rendering (not inside a `Begin`/`End`) |

`Scene.ForEach` iterates by index and tolerates objects being added or removed during the iteration. Removed `IDisposable` objects are disposed, and so is everything left when the scene is disposed.

### Behaviour observed in game

| Event received by our object | Meaning |
|---|---|
| `StoreCheckpoint(FirstCheckpoint)` | **start of the run** (right after the scene is created) |
| `StoreCheckpoint(Checkpoint or subclass)` | checkpoint crossed. Types vary per level (e.g. `LvlAccelerateAndSteerSilentCheckpoint`), so test `!(info is FirstCheckpoint)` |
| `LoadCheckpoint(FirstCheckpoint)` | quick restart **in the same scene** |
| `LoadCheckpoint(other)` | respawn at the last checkpoint (fall, death…) |
| `GoalReached()` | finish |

- **Retrying from the end screen creates a new `Scene3DIngame`.** Network state must live in a global singleton; scene objects only display and capture.
- **The scene is drawn before its first `Update()`** (intro, waiting for the start, after a reset): anything shown during that time must be initialized from the draw path too.
- **After the finish, the scene stops updating** until the next retry. The timer is also adjusted (fraction of a tick) *after* `IGoalReached` is broadcast: the mod reads the final time at the end of the same tick, from its global tick.

## Building a level

- `GlobalGame.CreateIngameCustom(ReplayFileAndName? viewReplay, bool startMission, bool exhibitionReplay)` (≈ 2000 lines) builds the whole `Scene3DIngame` and returns it.
  ⛔ **Don't patch it**: Harmony fails to rewrite it (`InvalidOperationException: Incorrect code generation for exception block`). General rule: avoid patching `GlobalGame`'s giant methods.
  ✅ **Hook point used: postfix on the `SE.Scene3DIngame` constructor** (internal, 4 lines, single creation site). The mod adds its objects to the scene and reads `IsReplay` to skip replays.
- Useful locals in that method (not fields: find them in `scene.AllThings`): `board` (`Board`), `playerContainer`, `levelTime` (`LevelTimer`), `checkpointManager`, `boardNpcAssets`.
- `Board` → private field `playerContainer` → private field `level` gives the current `Level`.
- Starting a level from code: `GlobalGame.SetCurrentChallenge(Challenge.FromNormalLevel(level, GetMaxNonSpoileryMedal))` then `GlobalGame.StartLevel(Level level, bool startMission, bool quickRetry)` (both private). `StartLevel` also handles custom maps through `StartCustomMap`. Official levels: `levelOrder.GetLevelByID(id)`.
- End screen: `GlobalGame.LevelFinished(LevelFinishedStats?)` (small). The mod prefixes it to retry immediately on the round's map.
- Level code shown in menus (A1, A5…): `Challenge.LevelToKey(level)`; display name: `level.DisplayName`.

## Level identity

- Official: `LevelKey { uint Id; uint Version; }`, built with `new LevelKey(level)`. The mod uses `o:<id>:<version>`.
- Custom/Workshop: `Level.CustomMap`, `Level.CustomMapKey` (= `OnlineKey`), and `CustomMapCatalogue`. The mod uses `c:<OnlineKey>` (loading not supported yet).

## Ghosts (base for remote players)

- `GhostState { Vector3 Position; Vector3 Orientation /* pitch, yaw, roll */; Vector3 CamPosition; Vector3 CamOrientation; }`
- **`Board.GetGhostState()`** (`internal`) returns the local player's state in one call. It's what replays record every frame (`ReplayRecorderSource`). **It's exactly the network packet to send.**
- `ReplayFrame { InputIngameState Input; GhostState State; uint WasFrozenButTickingInFrames; bool Rewind; }`: replays store inputs **and** states.
- Rendering: `GhostBoard : BoardVisual` only draws the board. Transform = `CreateFromYawPitchRoll(o.Y, o.X, o.Z) * CreateTranslation(pos)`, interpolated between two frames with the `extrapolation` given to `BeforeDraw`.
  `BoardVisual.DrawInternalBase(...)` is `protected`, so the mod has its own `NetGhostBoard : BoardVisual` fed by a network buffer.

## Skins

- A skin is just a texture: id > 0 → `Textures/Achievements/Skins/<id>`, id < 0 → `Models/Resources/Board/<-id>`, 0 → default board. The player's choice: `SaveState.Skin.Id`.
- The game applies the current skin **globally** by replacing the value of the shared `Models/Resources/Board/0` texture `Ref` (`GlobalAssets.UpdateBoardTex`). The real default texture is kept in the private field `GlobalAssets.defaultSkin`.
- Per-ghost skins: temporarily swap the board model's texture (`Assets.Model.Value.Textures.Get(0).Image.Tex2D`) while drawing, like `GhostBoard` does for replays, then restore it.

## Race: timer, checkpoints, finish

- `LevelTimer : RunTimer` (present in the scene), with `Counter` (`LevelTimeData`: `MS`, `TotalMS`, `RestartCount`, `Checkpoints`).
- `CheckpointManager`: `ActivateCheckpoint`, `RestartLevel`, `GoalReached`, `IsInFirstCheckpoint`, and the `OnBeforeRewindToFirstCheckpoint` event.
- `LevelGoal.Finish(float)` → local function `GoalReached` in `CreateIngameCustom`: broadcasts `IGoalReached`, calls `levelTime.HaltTimerCompletely()`, then rewinds the fraction of a tick.

## UI

- Options screens are built from `OptionsUI` helpers (`GetCheckboxRow(text, IValue<bool>)`, `GetSliderRow(text, IValue<float>, …)`), a `CanvasScale` from `GlobalGame.CreateCanvas()`, `UIAssets.CreateMainPanel(…)` and `GlobalGame.CreateButton(…)`, then shown with `GlobalGame.ShowPopup(Func<Action, Scene>, Scene, …)`. The mod's F8 menu is built the same way.
- `BytingLib.UI.TextInput` for text fields.
- Fonts: `UIAssets.FontSmall` etc. ⚠️ They lack some characters (no `—`, no `…`, no emoji): drawing an unsupported character throws, so the mod replaces them.
- Current language: `Loca.L.LanguageKey`.

## Steam / identity

- The game uses `SteamUser.GetAuthTicketForWebApi(null)` (`Steam.cs`) for its own backend (`Server.cs`, HTTP client).
- Validating such a ticket server-side with the Steam Web API (`AuthenticateUserTicket`) requires a **publisher key**, so it's not available without the studio.
- Options for tournaments: Steam OpenID (web login, available to anyone) or a per-player token given by the organizer.
- The game has its own `uint UserID`, distinct from the SteamID.
