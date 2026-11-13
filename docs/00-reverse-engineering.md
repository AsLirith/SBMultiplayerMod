# Étape 0 — Reverse engineering de STUNTBOOST

Décompilé le 2026-11-14 avec `ilspycmd` 11.2 (sources dans `_decompiled/`, **ne jamais versionner ni redistribuer**).
Les numéros de ligne renvoient à `_decompiled/STUNTBOOST/SE/` et changeront à chaque mise à jour du jeu : chercher par nom.

## Vue d'ensemble

| Élément | Valeur |
|---|---|
| Runtime | .NET 8.0.30 CoreCLR, self-contained (`STUNTBOOST` = apphost) |
| Moteur | MonoGame + `BytingLib` (lib maison du studio, décompilée aussi) |
| Taille | `STUNTBOOST.dll` ≈ 121k lignes, `BytingLib.dll` ≈ 48k lignes |
| Obfuscation | Aucune |
| Tick | Fixe, **60 Hz** (`Program.cs` : `new TargetGameSpeed(1.0 / 60.0, ...)`) |
| Steam | `Steamworks.NET` ; aucun usage de `SteamNetworking` / `SteamMatchmaking` |

## Injection du mod

- `STUNTBOOST.runtimeconfig.json` ne désactive **pas** les startup hooks, donc la cible est `DOTNET_STARTUP_HOOKS=<chemin>/SBMultiplayer.dll %command%` dans les options de lancement Steam.
- `libhostfxr.so` est une build custom (`0.0.0-dev`) et `libhostfxr.so.bak` l'officielle 8.0.30. ✅ Ça ne gêne pas : le startup hook et Harmony 2.3.6 se chargent bien (testé le 2026-11-14, natif Linux).
- File d'exécution sur le thread principal : `BytingLib.MainThread`. `GlobalGame.DoNextUpdate` (queue) sert à réinjecter les messages réseau dans la boucle de jeu.

## Détection de mods (`RunUploadBlockers.cs`)

- Bloque l'envoi des runs si l'une de ces assemblies est chargée : `0Harmony`, `BepInEx*`, `MelonLoader`, `MonoMod.*`. Même chose si `SaveState.Gameplay.ModdingMode` (option officielle « Modding Mode ») est active, ou si certains réglages de gameplay ne sont pas ceux par défaut.
- Utilisé par `GlobalGame.SubmitRunIfRecord`. Quand c'est bloqué, la run n'est **ni envoyée, ni enregistrée en local** (« run not stored or uploaded »).
- Conséquence : avec Harmony chargé, les leaderboards officiels sont protégés automatiquement (c'est ce qu'on veut). En revanche, le joueur ne garde pas ses records perso : le mod devra gérer ses propres temps.

## Boucle de jeu

- `Program.Main` → `MyGameWrapper : BytingLib.GameWrapper : MonoGame.Game` → `GlobalGame : GamePrototype`.
- `GlobalGame.UpdateIteration(GameTime)` → `UpdateIterationInner` : un appel par tick. Elle appelle `stuff.ForEach(IUpdate)`, la scène courante, puis `steam?.Update()`.
- `GlobalGame.ChangeScene(Func<Scene>)` change de scène. Le champ `currentScene` contient la scène active.

## Architecture des scènes : les interfaces diffusées

Une scène appelle `scene.ForEach((IXxx f) => ...)` sur tous ses objets. **Ajouter à la scène un objet qui implémente ces interfaces suffit pour recevoir les événements, sans patch :**

| Interface | Appelée quand |
|---|---|
| `IUpdate.Update()` | à chaque tick |
| `ICheckpointStorage.StoreCheckpoint(info)` | checkpoint pris (`CheckpointManager.ActivateCheckpoint`) |
| `ICheckpointStorage.LoadCheckpoint(info)` | respawn ; `info is FirstCheckpoint` signifie **restart de la run** |
| `IGoalReached.GoalReached()` | arrivée franchie |
| `IDrawWorldSkinRender`, `IBeforeDraw(popup, extrapolation)` | rendu 3D (utilisé par `GhostBoard`) |

### Sémantique observée en jeu (test du 2026-11-14, niveau 1)

| Événement reçu par notre objet | Signification |
|---|---|
| `StoreCheckpoint(FirstCheckpoint)` | **départ de la run** (juste après la création de la scène) |
| `StoreCheckpoint(Checkpoint ou sous-classe)` | checkpoint franchi. Les types varient selon le niveau (ex. `LvlAccelerateAndSteerSilentCheckpoint`), donc tester `!(info is FirstCheckpoint)` |
| `LoadCheckpoint(FirstCheckpoint)` | restart rapide **dans la même scène** |
| `LoadCheckpoint(autre)` | respawn au dernier checkpoint (chute, mort...) |
| `GoalReached()` | arrivée |

- **Rejouer depuis l'écran de fin recrée une nouvelle `Scene3DIngame`.** L'état réseau doit donc vivre dans un singleton global, et les objets de scène ne servent qu'à l'affichage et à la capture.
- Il y a un délai de quelques secondes (intro ou chargement) entre la création de la scène et les premiers `Update()`.

## Construction d'un niveau

- `GlobalGame.CreateIngameCustom(ReplayFileAndName? viewReplay, bool startMission, bool exhibitionReplay)` (≈ ligne 14544, environ 2000 lignes) construit toute la scène `Scene3DIngame` et la renvoie.
  ⛔ **Ne pas la patcher** : Harmony échoue à la réécrire (`InvalidOperationException: Incorrect code generation for exception block`, testé le 2026-11-14). Règle générale : éviter de patcher les méthodes géantes de `GlobalGame`.
  ✅ **Point d'accroche retenu : postfix sur le constructeur de `SE.Scene3DIngame`** (internal, 4 lignes, un seul site de création). On ajoute nos objets à la scène et on lit `IsReplay` pour ignorer les replays.
- Variables locales utiles dans cette méthode : `scene`, `board` (`Board`), `playerContainer`, `levelTime` (`LevelTimer`), `checkpointManager`, `boardNpcAssets`, `zFightingIndex`.
  Ce sont des locales, pas des champs : il faudra les retrouver dans la scène (`scene` contient les objets) ou ajouter un patch plus ciblé.
- Lancer un niveau par code : `GlobalGame.StartLevel(Level level, bool startMission, bool quickRetry)` (privée, ≈ ligne 10873). Elle gère aussi les maps custom via `StartCustomMap`. Retrouver un `Level` officiel : `levelOrder.GetLevelByID(id)`.
  ⚠️ Dépend de `currentChallenge`, à valider à l'étape 4.

## Identité des niveaux

- Officiel : `LevelKey { uint Id; uint Version; }`, construit par `new LevelKey(level)`.
- Custom/Workshop : `Level.CustomMap`, `Level.CustomMapKey` (= `OnlineKey`), plus `CustomMapCatalogue`.
- Le protocole réseau identifiera une map par `(Id, Version)` ou `(CustomMapKey, Version)`.

## Ghosts (base des joueurs distants)

- `GhostState { Vector3 Position; Vector3 Orientation /* pitch, yaw, roll */; Vector3 CamPosition; Vector3 CamOrientation; }`
- **`Board.GetGhostState()`** (`internal`) donne l'état du joueur local en un appel. C'est ce que le replay enregistre à chaque frame (`ReplayRecorderSource.cs:33`). **C'est exactement le paquet réseau à envoyer.**
- `ReplayFrame { InputIngameState Input; GhostState State; uint WasFrozenButTickingInFrames; bool Rewind; }` : les replays stockent les inputs **et** les états.
- Rendu : `GhostBoard : BoardVisual` dessine uniquement la planche. Transform = `CreateFromYawPitchRoll(o.Y, o.X, o.Z) * CreateTranslation(pos)`, avec une interpolation entre `ReplayFrame` et `ReplayFrameNext` selon l'`extrapolation` reçue dans `BeforeDraw`.
  `BoardVisual.DrawInternalBase(...)` est `protected`, donc on peut **écrire notre propre `NetGhostBoard : BoardVisual`** alimenté par un buffer réseau au lieu d'un `ReplayInput`.
- Création des ghosts : dans `CreateIngameCustom`, fonction locale `ShowGhostBoard` (≈ ligne 15393). Arguments à reproduire : `boardNpcAssets`, `settings.Graphics.Player.BoardRealShadow`, `++zFightingIndex`, `scene.TransparencyBatch`, `SaveState.Gameplay.GhostOpacityResult`, `settings.Graphics.GhostDepthBias`.

## Course : chrono, checkpoints, arrivée

- `LevelTimer : RunTimer`, avec `Counter` (`LevelTimeData` : `MS`, `TotalMS`, `RestartCount`, `Checkpoints`).
- `CheckpointManager` : `ActivateCheckpoint`, `RestartLevel`, `GoalReached`, `IsInFirstCheckpoint`, et l'événement `OnBeforeRewindToFirstCheckpoint`.
- `LevelGoal.Finish(float)` → fonction locale `GoalReached` dans `CreateIngameCustom` (≈ ligne 15826). Elle diffuse `IGoalReached`, appelle `levelTime.HaltTimerCompletely()`, puis rembobine la fraction de frame.
- Il faudra probablement **désactiver ou remplacer l'écran de fin** (`LevelFinished` → popup de sélection de niveau) en multi, pour enchaîner directement sur un restart.

## Steam / identité

- Le jeu utilise `SteamUser.GetAuthTicketForWebApi(null)` (`Steam.cs`) pour son propre backend (`Server.cs`, client HTTP).
- La validation côté serveur via l'API Web Steam (`AuthenticateUserTicket`) exige une **clé éditeur**, donc elle n'est pas accessible sans le studio.
- Pistes pour les tournois : Steam OpenID (connexion web, accessible à tous) ou un token par joueur fourni par l'organisateur.
- Le jeu a son propre `uint UserID`, distinct du SteamID.

## Prochaine étape (1)

1. Créer `SBMultiplayer.Client` (net8.0, référence `Lib.Harmony`, références aux DLL du jeu en `Private=false`).
2. Classe `StartupHook.Initialize()` qui logge, attend que `GlobalGame` existe, puis applique les patches Harmony.
3. Premier patch : postfix sur `GlobalGame.CreateIngameCustom` qui ajoute à la scène un objet `IUpdate` loggant `Board.GetGhostState()` chaque seconde.
4. Lancer le jeu sous Linux avec `DOTNET_STARTUP_HOOKS` et vérifier les logs, ce qui valide aussi la question du `libhostfxr` custom.
