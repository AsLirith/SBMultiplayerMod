using System.Reflection;
using HarmonyLib;
using SBMultiplayer.Client.Net;

namespace SBMultiplayer.Client.Patches;

/// <summary>
/// SE.ClickToStartSceneCloser (internal) closes the start-line popup when the player presses a key.
/// In Rounds mode, <see cref="StartGate"/> decides instead: the popup stays until the start signal.
/// </summary>
[HarmonyPatch]
internal static class StartLinePatch
{
    private static readonly Type CloserType = AccessTools.TypeByName("SE.ClickToStartSceneCloser");
    private static readonly MethodInfo CloseMethod = AccessTools.Method(CloserType, "Close");
    private static bool failed;

    private static MethodBase TargetMethod() => AccessTools.Method(CloserType, "Update");

    private static bool Prefix(object __instance)
    {
        if (failed)
        {
            return true;
        }
        try
        {
            return StartGate.AllowPopupUpdate(() => CloseMethod.Invoke(__instance, null));
        }
        catch (Exception e)
        {
            failed = true; // never leave a player stuck on the start line because of the mod
            ModLog.Error("Synchronized start disabled after an error", e);
            return true;
        }
    }
}
