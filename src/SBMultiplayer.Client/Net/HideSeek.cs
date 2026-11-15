using System.Reflection;
using BytingLib;
using HarmonyLib;
using Microsoft.Xna.Framework;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.UI;
using SBMultiplayer.Protocol;
using SE;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Hide and Seek, client side. The server picks the hunter and keeps the score; here:
/// - the hunter is held on the start line during the hiding time (see <see cref="StartGate"/>), doesn't see the
///   mice meanwhile, never sees name tags and can't use the free camera;
/// - once the hiding time is over, each mouse is frozen where it is, as soon as it touches a surface. Freezing uses
///   the game's own free camera, which pauses the run in place: the mouse can fly around or follow the hunter, and
///   can't resume (<see cref="KeepFrozenPatch"/>);
/// - the hunter finds a mouse by aiming at it and pressing the game's grind input (left click by default):
///   within range, near the center of the screen, and not behind a wall.
/// Game thread only.
/// </summary>
internal static class HideSeek
{
    public enum Role
    {
        /// <summary>Not in this round (arrived late, spectating), or not a Hide and Seek round.</summary>
        None,
        Hunter,
        Mouse,
    }

    private static readonly FieldInfo FlyTogglerField = AccessTools.Field(typeof(GlobalGame), "flyCameraToggler");
    private static readonly MethodInfo SetFlyMethod = AccessTools.Method(AccessTools.TypeByName("SE.FlyCameraToggler"), "SetFly");
    private static readonly AccessTools.FieldRef<GlobalGame, InputOutgame> InputRef =
        AccessTools.FieldRefAccess<GlobalGame, InputOutgame>("inputOutgame");
    private static readonly AccessTools.FieldRef<Board, PlayerContainer> BoardPlayerContainer =
        AccessTools.FieldRefAccess<Board, PlayerContainer>("playerContainer");

    /// <summary>Height aimed at above a board's position (the player is about 1 unit = 1 cm tall), in cm.</summary>
    private const float BoardCenterHeight = 0.5f;

    /// <summary>A frozen mouse whose board moves this far (restart from the pause menu...) left its hiding place, in cm.</summary>
    private const float LeftHidingPlaceDistance = 5;

    private static Scene? scene;
    private static Board? board;

    private static int frozenRound = -1;
    private static Vector3 frozenAt;
    private static bool forfeitSent;
    private static double nextTagAt;

    /// <summary>While true, the game's free camera can't be closed: the mouse stays frozen in its hiding place.</summary>
    public static bool KeepFrozen { get; private set; }

    /// <summary>A frozen mouse asked to follow a player with the spectator camera (instead of the free camera).</summary>
    public static bool FollowRequested { get; set; }

    /// <summary>Called by the scene hook once the player's board is known.</summary>
    public static void Attach(Scene levelScene, Board playerBoard)
    {
        scene = levelScene;
        board = playerBoard;
    }

    private static MultiplayerClient Client => MultiplayerClient.Instance;

    private static int LocalId => Client.LocalId ?? -1;

    /// <summary>A Hide and Seek round is being played on the map we're on, and its roles are known.</summary>
    public static bool IsActive
    {
        get
        {
            RoundInfo info = Client.Round.Current.Info;
            return info.Mode == GameMode.HideAndSeek && info.Phase == RoundPhase.Playing && Client.IsOnRoundMap
                && Client.Round.HideSeek.RoundId == info.RoundId;
        }
    }

    public static Role LocalRole => RoleOf(LocalId);

    public static Role RoleOf(int id)
    {
        if (!IsActive)
        {
            return Role.None;
        }
        RoundState.HideSeekState hs = Client.Round.HideSeek;
        return hs.Hunters.Contains(id) ? Role.Hunter : hs.Mice.Contains(id) ? Role.Mouse : Role.None;
    }

    public static bool IsFound(int id) => IsActive && Client.Round.HideSeek.Found.Contains(id);

    /// <summary>Local time at which the hunt starts (end of the hiding time), once the round has started.</summary>
    public static double? SeekStart
    {
        get
        {
            RoundState.Snapshot round = Client.Round.Current;
            return round.GoAt is double goAt ? goAt + round.Info.HideSeconds : null;
        }
    }

    public static bool IsHiding => IsActive && SeekStart is double s && NetClock.Now < s;

    public static bool IsHunting => IsActive && SeekStart is double s && NetClock.Now >= s;

    /// <summary>When the hunter is released from the start line (the spectators' "GO").</summary>
    public static double? ReleaseTime(Role role) =>
        role == Role.Hunter ? SeekStart : Client.Round.Current.GoAt;

    /// <summary>Watches the round instead of playing: not in it, or found (when found mice don't become hunters).</summary>
    public static bool IsWatcher
    {
        get
        {
            if (!IsActive)
            {
                return false;
            }
            Role role = LocalRole;
            return role == Role.None || (role == Role.Mouse && IsFound(LocalId) && !KeepFrozen);
        }
    }

    /// <summary>The local player must not see this player's board right now.</summary>
    public static bool HideFromLocal(RemotePlayer p)
    {
        if (!IsActive)
        {
            return false;
        }
        Role me = LocalRole;
        Role them = RoleOf(p.Id);
        if (them == Role.None)
        {
            return true; // not in this round (arrived late): waiting on the start line, out of the game
        }
        if (them == Role.Mouse && me == Role.Hunter && IsHiding)
        {
            return true; // the hunter doesn't see where the mice go
        }
        if (them == Role.Mouse && me == Role.Mouse && Client.Round.Current.Info.Infection && !IsFound(LocalId) && !IsFound(p.Id))
        {
            // Infection: a mouse can become a hunter, so it must not know where the others hid.
            return true;
        }
        // Found mice leave the game (unless they became hunters).
        return them == Role.Mouse && IsFound(p.Id);
    }

    /// <summary>Hide and Seek round on our map: the finish line does nothing (no teleport back to the start).</summary>
    public static bool BlocksFinish
    {
        get
        {
            RoundInfo info = Client.Round.Current.Info;
            return info.Mode == GameMode.HideAndSeek && info.Phase != RoundPhase.Free && Client.IsOnRoundMap;
        }
    }

    /// <summary>Name tags: never for the hunter.</summary>
    public static bool HidesNameTags => LocalRole == Role.Hunter;

    /// <summary>Free camera: forced off for hunters and mice still hiding; frozen mice get it from the freeze itself.</summary>
    public static bool BlocksFreeCamera => LocalRole is Role.Hunter or Role.Mouse;

    /// <summary>Called every global tick.</summary>
    public static void Tick()
    {
        int roundId = Client.Round.Current.Info.RoundId;
        if (frozenRound != -1 && frozenRound != roundId)
        {
            // New round: the map is reloaded, the old freeze no longer applies.
            KeepFrozen = false;
            FollowRequested = false;
            frozenRound = -1;
        }
        if (IsHiding && LocalRole == Role.Mouse && frozenRound != roundId && LockPressed())
        {
            // Lock the hiding place before the hunt (the board never stops by itself): like prop hunt.
            bool ready = board != null && scene != null && ReferenceEquals(scene, GameAccess.CurrentScene);
            if (ready && NoMenuOnTop(scene!) && IsOnGround(board!))
            {
                Freeze(roundId);
            }
            else
            {
                Notifications.Warn(Tr.HsLandToLock);
            }
        }
        if (!IsActive || SeekStart is not double seekStart || NetClock.Now < seekStart)
        {
            return;
        }

        Role role = LocalRole;
        bool sceneOk = board != null && scene != null && ReferenceEquals(scene, GameAccess.CurrentScene);
        if (role == Role.Mouse && frozenRound != roundId && sceneOk)
        {
            // Frozen only on a surface, never in mid-air (unless they never land). Not while a menu is open (pause...):
            // the freeze view would open on top of it and lock it. The game is paused meanwhile, the board doesn't move.
            if (NoMenuOnTop(scene!) && (IsOnGround(board!) || NetClock.Now - seekStart > HideSeekRules.MaxAirSecondsBeforeFreeze))
            {
                Freeze(roundId);
            }
        }
        else if (role == Role.Hunter && KeepFrozen)
        {
            // Infection: found, now a hunter. The run resumes from the hiding place.
            KeepFrozen = false;
            FollowRequested = false;
            SetFly(false);
            Notifications.Warn(Tr.HsYouAreHunterNow);
        }

        if (KeepFrozen && !forfeitSent && sceneOk && !IsFound(LocalId)
            && Vector3.Distance(GameAccess.GetGhostState(board!).Position, frozenAt) > LeftHidingPlaceDistance)
        {
            // Restarted from the pause menu (or similar): the hiding place is lost, counts as found.
            forfeitSent = true;
            Client.SendTag(roundId, LocalId);
        }

        if (role == Role.Hunter && sceneOk && NetClock.Now >= nextTagAt && TagPressed())
        {
            nextTagAt = NetClock.Now + 0.25;
            try
            {
                TryTag(roundId);
            }
            catch (Exception e)
            {
                ModLog.Error("Hide and Seek: tag failed", e);
            }
        }
    }

    private static readonly Type? ClickToStartSceneType = AccessTools.TypeByName("SE.ClickToStartScene");

    /// <summary>Nothing over the level but, at most, the start-line popup (no pause menu, no mod menu).</summary>
    private static bool NoMenuOnTop(BytingLib.Scene level)
    {
        if (UI.PopupMenu.AnyOpen)
        {
            return false;
        }
        BytingLib.Scene top = level.GetTopmostScene();
        return ReferenceEquals(top, level) || (ClickToStartSceneType?.IsInstanceOfType(top) ?? false);
    }

    private static bool IsOnGround(Board b)
    {
        try
        {
            return BoardPlayerContainer(b).Player.State == SE.PlayerState.Ground;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static void Freeze(int roundId)
    {
        frozenRound = roundId;
        frozenAt = GameAccess.GetGhostState(board!).Position;
        forfeitSent = false;
        KeepFrozen = true;
        if (SetFly(true))
        {
            ModLog.Info("Hide and Seek: frozen in the hiding place");
            Notifications.Info(Tr.HsFrozen);
        }
        else
        {
            KeepFrozen = false;
        }
    }

    private static bool SetFly(bool enabled)
    {
        try
        {
            object? toggler = GameAccess.Game != null ? FlyTogglerField.GetValue(GameAccess.Game) : null;
            if (toggler == null)
            {
                return false;
            }
            SetFlyMethod.Invoke(toggler, new object[] { enabled });
            return true;
        }
        catch (Exception e)
        {
            ModLog.Error("Hide and Seek: could not switch the free camera", e);
            return false;
        }
    }

    /// <summary>The game's free camera key (the free camera itself is off for mice while they hide).</summary>
    private static bool LockPressed()
    {
        try
        {
            return GameAccess.Game != null && !GameAccess.IsTyping() && !UI.ChatInput.IsOpen && InputRef(GameAccess.Game).FlyToggle.Pressed;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TagPressed()
    {
        try
        {
            return !GameAccess.IsTyping() && !UI.ChatInput.IsOpen && !UI.PopupMenu.AnyOpen && UI.ModInput.Pressed(ModConfig.Current.CatchKey);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The mouse closest to the center of the screen, in range and in sight, is found.</summary>
    private static void TryTag(int roundId)
    {
        IngameCameras? camera = GameAccess.GetIngameCameras(scene!);
        if (camera == null)
        {
            return;
        }
        Matrix world = Matrix.Invert(camera.View);
        Vector3 origin = world.Translation;
        Vector3 forward = Vector3.Normalize(world.Forward);
        Entities? entities = scene!.AllThings.OfType<Entities>().FirstOrDefault();
        RoundState.HideSeekState hs = Client.Round.HideSeek;
        string map = Client.CurrentMapKey;
        Vector3 hunter = GameAccess.GetGhostState(board!).Position;

        RemotePlayer? best = null;
        float bestScore = float.MaxValue;
        foreach (RemotePlayer p in Client.Players.Values)
        {
            if (!hs.Mice.Contains(p.Id) || hs.Found.Contains(p.Id) || hs.Hunters.Contains(p.Id) || p.IsSpectator || p.MapKey != map
                || !p.TryGetPose(0f, out Vector3 position, out _))
            {
                continue;
            }
            if (Vector3.Distance(hunter, position) > HideSeekRules.TagRange)
            {
                continue; // the hunter's board must be close to the mouse
            }
            Vector3 to = position + Vector3.Up * BoardCenterHeight - origin;
            float distance = to.Length();
            if (distance < 0.01f)
            {
                continue;
            }
            float along = Vector3.Dot(to, forward);
            if (along <= 0f)
            {
                continue; // behind the camera
            }
            // Aim tolerance: some room around the board (hard to click precisely at full speed).
            float offAxis = (to - forward * along).Length();
            if (offAxis > Math.Max(HideSeekRules.AimTolerance, distance * 0.15f))
            {
                continue;
            }
            if (entities != null && entities.GetNearestTriangleRaycastSolid(origin, to / distance, distance - 0.6f, out _) != null)
            {
                continue; // a wall in between
            }
            float score = offAxis / distance;
            if (score < bestScore)
            {
                bestScore = score;
                best = p;
            }
        }
        if (best != null)
        {
            ModLog.Info($"Hide and Seek: found {best.Name}");
            Client.SendTag(roundId, best.Id);
        }
    }
}

/// <summary>
/// Hide and Seek: a frozen mouse can't respawn at a checkpoint or restart (R / Q, pause menu): that would move its
/// board out of its hiding place while the game is paused, and leave it stuck there.
/// </summary>
[HarmonyPatch]
internal static class FrozenRestartPatch
{
    // The entry points (R, Q, pause menu) and the methods they end up in, every overload: blocked at several levels
    // in case the JIT inlined a small one into its caller.
    private static readonly string[] Names =
        { "RestartLevel", "RewindToCheckpoint", "RewindToggle", "RewindToStart", "TimeTravelForward", "LoadCheckpointLater", "LoadFirstCheckpointLater" };

    private static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(CheckpointManager)).Where(m => Names.Contains(m.Name)).Cast<MethodBase>();

    private static bool Prefix() => !HideSeek.KeepFrozen;
}

/// <summary>Hide and Seek: crossing the finish line does nothing (otherwise the round's retry sends the player back to the start).</summary>
[HarmonyPatch(typeof(Goal), "GoalReached")]
internal static class HideSeekFinishPatch
{
    private static bool Prefix() => !HideSeek.BlocksFinish;
}

/// <summary>Hide and Seek: a frozen mouse can't close the game's free camera (which would resume the run).</summary>
[HarmonyPatch]
internal static class KeepFrozenPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("SE.FlyCameraToggler"), "EndFlying");

    private static bool Prefix() => !HideSeek.KeepFrozen;
}
