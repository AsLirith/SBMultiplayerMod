using System.Reflection;
using BytingLib;
using HarmonyLib;
using SBMultiplayer.Client.Net;

namespace SBMultiplayer.Client.Patches;

/// <summary>
/// Every level played creates an SE.Scene3DIngame (internal class, trivial constructor).
/// Our objects are added to that scene: it then broadcasts the events to them
/// (IUpdate, ICheckpointStorage, IGoalReached...) without any other patch.
///
/// Note: GlobalGame.CreateIngameCustom is NOT patched: the method is too big
/// and Harmony fails to rewrite it ("Incorrect code generation for exception block").
/// </summary>
[HarmonyPatch]
internal static class IngameScenePatch
{
    private static readonly Type SceneType = AccessTools.TypeByName("SE.Scene3DIngame");

    private static readonly Func<object, bool> IsReplay =
        obj => (bool)AccessTools.PropertyGetter(SceneType, "IsReplay").Invoke(obj, null)!;

    private static MethodBase TargetMethod() => AccessTools.GetDeclaredConstructors(SceneType).Single();

    private static void Postfix(object __instance)
    {
        try
        {
            var scene = (Scene)__instance;
            if (IsReplay(scene))
            {
                ModLog.Info("Replay scene: nothing to inject");
                return;
            }

            scene.Add(new MultiplayerSceneHook(scene));
            if (ModConfig.Current.DebugProbe)
            {
                scene.Add(new DebugProbe(scene));
            }
            if (ModConfig.Current.DebugDelayedSelfGhost)
            {
                scene.Add(new Ghosts.DelayedSelfGhost(scene));
            }
        }
        catch (Exception e)
        {
            ModLog.Error("IngameScenePatch.Postfix", e);
        }
    }
}
