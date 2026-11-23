using System.Reflection;
using HarmonyLib;
using SBMultiplayer.Client.Net;
using SE;

namespace SBMultiplayer.Client.Patches;

/// <summary>
/// One-attempt rounds: the game's restart key (Q) must be pressed twice quickly or held, so a single press
/// doesn't give up the round. Handled where the game reads that key (SE.Restarter), the pause menu is left alone.
/// </summary>
[HarmonyPatch]
internal static class RestartKeyPatch
{
    private static readonly Type? RestarterType = AccessTools.TypeByName("SE.Restarter");
    private static readonly FieldInfo? InputField = RestarterType != null ? AccessTools.Field(RestarterType, "input") : null;

    private static bool Prepare() => RestarterType != null && InputField != null && TargetMethod() != null;

    private static MethodBase? TargetMethod() => RestarterType != null ? AccessTools.Method(RestarterType, "UpdateMayRestart") : null;

    private static bool Prefix(object __instance)
    {
        try
        {
            if (!StartGate.IsSingleAttemptRun || InputField!.GetValue(__instance) is not InputOutgame input || !input.RestartLevel.Pressed)
            {
                return true;
            }
            return StartGate.ConfirmRestartPress();
        }
        catch (Exception e)
        {
            ModLog.Error("RestartKeyPatch", e);
            return true;
        }
    }
}

/// <summary>Restarting the level on purpose (confirmed key, pause menu): every way in, in case one gets inlined.</summary>
[HarmonyPatch]
internal static class RestartOnPurposePatch
{
    private static readonly string[] Names = { "RestartLevel", "RewindToStart", "LoadFirstCheckpointLater" };

    private static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(CheckpointManager)).Where(m => Names.Contains(m.Name)).Cast<MethodBase>();

    private static void Prefix()
    {
        try
        {
            StartGate.OnRestartOnPurpose();
        }
        catch (Exception e)
        {
            ModLog.Error("RestartOnPurposePatch", e);
        }
    }
}

/// <summary>A checkpoint is loaded (death, respawn, restart): before the game resets its timer.</summary>
[HarmonyPatch(typeof(CheckpointManager), "LoadCheckpointNow")]
internal static class LoadCheckpointPatch
{
    private static readonly AccessTools.FieldRef<CheckpointManager, LevelTimer> TimerRef =
        AccessTools.FieldRefAccess<CheckpointManager, LevelTimer>("levelTimer");

    private static void Prefix(CheckpointManager __instance, ICheckpointInfo? cpInfo)
    {
        try
        {
            bool first = cpInfo is FirstCheckpoint;
            StartGate.OnLoadCheckpoint(__instance, first, TimerRef(__instance));
            if (first && !Spectator.IsWatching)
            {
                LeaderboardUploads.FreeCameraGranted = false; // fresh start, free camera back to the level's own setting
            }
            HideSeek.OnRespawn();
        }
        catch (Exception e)
        {
            ModLog.Error("LoadCheckpointPatch", e);
        }
    }
}
