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
            return client.IsReady && (client.IsSpectating || StartGate.IsEliminated);
        }
    }

    /// <summary>Players that can be followed: on our map, not spectating.</summary>
    private static List<RemotePlayer> Candidates()
    {
        MultiplayerClient client = MultiplayerClient.Instance;
        string map = client.CurrentMapKey;
        return client.Players.Values
            .Where(p => !p.IsSpectator && p.MapKey == map && map != "")
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
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

        if (watching)
        {
            var candidates = Candidates();
            if (Target is not { } target || !candidates.Contains(target))
            {
                TargetId = candidates.FirstOrDefault()?.Id;
            }
        }

        bool showCamera = watching && TargetId != null && cameras != null;
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

        SyncFreeCamera(watching);
    }

    private static void SyncFreeCamera(bool watching)
    {
        object? toggler = GameAccess.Game != null ? FlyTogglerField.GetValue(GameAccess.Game) : null;
        if (toggler != flyToggler)
        {
            flyToggler = toggler; // new level scene: remember its own setting
            flyWasAllowed = toggler != null && (bool)CanFlyProperty.GetValue(toggler)!;
        }
        if (toggler != null)
        {
            CanFlyProperty.SetValue(toggler, watching || flyWasAllowed);
        }
    }

    /// <summary>The followed player's camera. Last known pose if their data stops (map change, lag).</summary>
    private sealed class FollowCamera : ICamera
    {
        private Vector3 position;
        private Vector3 orientation;

        private void Refresh()
        {
            if (Target?.TryGetCamera(out Vector3 p, out Vector3 o) == true)
            {
                position = p;
                orientation = o;
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
