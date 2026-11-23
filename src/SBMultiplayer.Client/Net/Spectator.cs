using System.Reflection;
using BytingLib;
using HarmonyLib;
using Microsoft.Xna.Framework;
using SBMultiplayer.Client.Ghosts;
using SE;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Spectator view: while spectating (or eliminated), the player stays frozen on the start line and the
/// scene's camera shows what the followed player sees (their own camera, sent with their board state).
/// The game's free camera (F) is allowed too. Game thread only.
/// </summary>
internal static class Spectator
{
    private static readonly FieldInfo FlyTogglerField = AccessTools.Field(typeof(GlobalGame), "flyCameraToggler");
    private static readonly PropertyInfo CanFlyProperty = AccessTools.Property(AccessTools.TypeByName("SE.FlyCameraToggler"), "CanFly");
    private static readonly Type? FlyCameraType = AccessTools.TypeByName("SE.FlyCamera");
    private static readonly MethodInfo? FlyCameraTeleport = FlyCameraType != null ? AccessTools.Method(FlyCameraType, "Teleport") : null;

    /// <summary>Our camera was the one shown last frame; its pose then (world transform).</summary>
    private static bool followShownLastFrame;
    private static Matrix lastFollowTransform;

    private static readonly FollowCamera Camera = new();
    private static IngameCameras? cameraOwner;
    private static object? flyToggler;
    private static bool flyWasAllowed;

    /// <summary>Id of the followed player, or null.</summary>
    public static int? TargetId { get; private set; }

    /// <summary>Spectating by choice, or eliminated: watch the others instead of playing.</summary>
    public static bool IsWatching
    {
        get
        {
            MultiplayerClient client = MultiplayerClient.Instance;
            return client.IsReady && (client.IsSpectating || StartGate.IsEliminated || HideSeek.IsWatcher);
        }
    }

    /// <summary>Players that can be followed: on our map, not spectating, not eliminated (Hide and Seek: hunters first).</summary>
    private static List<RemotePlayer> Candidates()
    {
        MultiplayerClient client = MultiplayerClient.Instance;
        string map = client.CurrentMapKey;
        return client.Players.Values
            .Where(p => !p.IsSpectator && p.MapKey == map && map != "" && !HideSeek.HideFromLocal(p) && !StartGate.IsNameEliminated(p.Name))
            .OrderBy(p => HideSeek.RoleOf(p.Id) == HideSeek.Role.Hunter ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static RemotePlayer? Target =>
        TargetId is int id && MultiplayerClient.Instance.Players.TryGetValue(id, out RemotePlayer? p) ? p : null;

    /// <summary>Follows the next (+1) or previous (-1) player.</summary>
    public static void Cycle(int direction)
    {
        var candidates = Candidates();
        if (candidates.Count == 0)
        {
            TargetId = null;
            return;
        }
        int index = candidates.FindIndex(p => p.Id == TargetId);
        index = index < 0 ? 0 : ((index + direction) % candidates.Count + candidates.Count) % candidates.Count;
        TargetId = candidates[index].Id;
    }

    /// <summary>
    /// Called every frame from the level scene: puts our camera on top of the scene's cameras while watching
    /// (and removes it otherwise), and allows the game's free camera.
    /// </summary>
    public static void Sync(Scene scene)
    {
        IngameCameras? cameras = GameAccess.GetIngameCameras(scene);
        bool watching = IsWatching;
        // Hide and Seek: a frozen mouse can follow a player instead of using the free camera.
        bool following = watching || (HideSeek.KeepFrozen && HideSeek.FollowRequested);

        if (following)
        {
            var candidates = Candidates();
            if (Target is not { } target || !candidates.Contains(target))
            {
                TargetId = candidates.FirstOrDefault()?.Id;
            }
        }

        // No camera from that player yet (e.g. never left the start line): keep the current view rather than
        // looking from the map's origin, under the ground.
        bool showCamera = following && TargetId != null && cameras != null && Camera.HasPose;
        if (cameraOwner != null && (!showCamera || cameraOwner != cameras))
        {
            cameraOwner.Cameras.Remove(Camera);
            cameraOwner = null;
        }
        if (showCamera && cameraOwner == null)
        {
            // Last camera of the list wins. The game's free camera, added later, goes on top of ours.
            cameras!.Cameras.Add(Camera);
            cameraOwner = cameras;
        }
        else if (showCamera && HideSeek.KeepFrozen && cameras!.Cameras.Count > 0 && cameras.Cameras[^1] != Camera)
        {
            // The free camera of the freeze was added after ours: put ours back on top.
            cameras.Cameras.Remove(Camera);
            cameras.Cameras.Add(Camera);
        }

        HandOverToFreeCamera(cameras);
        SyncFreeCamera(watching);
    }

    /// <summary>
    /// Leaving a player's view for the free camera: the free camera starts where that player's camera was,
    /// instead of our own board's place.
    /// </summary>
    private static void HandOverToFreeCamera(IngameCameras? cameras)
    {
        object? top = cameras != null && cameras.Cameras.Count > 0 ? cameras.Cameras[^1] : null;
        if (followShownLastFrame && top != null && FlyCameraType?.IsInstanceOfType(top) == true && FlyCameraTeleport != null)
        {
            try
            {
                FlyCameraTeleport.Invoke(top, new object[] { lastFollowTransform });
            }
            catch (Exception e)
            {
                ModLog.Warn($"Free camera not moved to the followed player: {e.Message}");
            }
        }
        followShownLastFrame = ReferenceEquals(top, Camera);
        if (followShownLastFrame)
        {
            lastFollowTransform = Matrix.Invert(Camera.View);
        }
    }

    private static void SyncFreeCamera(bool watching)
    {
        object? toggler = GameAccess.Game != null ? FlyTogglerField.GetValue(GameAccess.Game) : null;
        if (toggler != flyToggler)
        {
            flyToggler = toggler; // new level scene: remember its own setting
            flyWasAllowed = toggler != null && (bool)CanFlyProperty.GetValue(toggler)!;
            LeaderboardUploads.FreeCameraGranted = false;
        }
        if (toggler != null)
        {
            // Hide and Seek: no free camera for the players of the round (frozen mice get it from the freeze).
            bool canFly = !HideSeek.BlocksFreeCamera && (watching || flyWasAllowed);
            CanFlyProperty.SetValue(toggler, canFly);
            if (canFly && !flyWasAllowed)
            {
                LeaderboardUploads.FreeCameraGranted = true; // runs of this level stay off the leaderboards until a fresh start
            }
        }
    }

    /// <summary>The followed player's camera. Last known pose if their data stops (map change, lag).</summary>
    private sealed class FollowCamera : ICamera
    {
        private Vector3 position;
        private Vector3 orientation;

        /// <summary>The player whose pose is in position/orientation.</summary>
        private int? poseOf;

        /// <summary>A camera pose of the current target is known.</summary>
        public bool HasPose
        {
            get
            {
                Refresh();
                return poseOf != null && poseOf == TargetId;
            }
        }

        private void Refresh()
        {
            if (Target?.TryGetCamera(out Vector3 p, out Vector3 o) == true)
            {
                position = p;
                orientation = o;
                poseOf = TargetId;
            }
        }

        public Vector3 Position
        {
            get
            {
                Refresh();
                return position;
            }
        }

        public Vector3 FocusPoint => Position;

        public Matrix View
        {
            get
            {
                Refresh();
                // Same convention as the game's replay camera (ReplayCamera).
                Matrix rotation = Matrix.CreateFromYawPitchRoll(orientation.Y, orientation.X, orientation.Z);
                return Matrix.CreateLookAt(position, position + rotation.Forward, Vector3.Up);
            }
        }
    }
}
