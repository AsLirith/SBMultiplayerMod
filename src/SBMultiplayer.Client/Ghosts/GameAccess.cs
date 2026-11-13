using BytingLib;
using HarmonyLib;
using SE;

namespace SBMultiplayer.Client.Ghosts;

/// <summary>Accès aux membres non publics du jeu, résolus une seule fois.</summary>
internal static class GameAccess
{
    public const int TicksPerSecond = 60;

    public static readonly Func<Board, GhostState> GetGhostState =
        AccessTools.MethodDelegate<Func<Board, GhostState>>(AccessTools.Method(typeof(Board), "GetGhostState"));

    // Scene3D est internal : on lit sa propriété TransparencyBatch par réflexion.
    private static readonly System.Reflection.MethodInfo TransparencyBatchGetter =
        AccessTools.PropertyGetter(AccessTools.TypeByName("SE.Scene3D"), "TransparencyBatch");

    public static TransparencyBatchSwapper GetTransparencyBatch(Scene scene) =>
        (TransparencyBatchSwapper)TransparencyBatchGetter.Invoke(scene, null)!;
}
