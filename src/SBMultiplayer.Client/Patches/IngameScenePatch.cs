using System.Reflection;
using BytingLib;
using HarmonyLib;

namespace SBMultiplayer.Client.Patches;

/// <summary>
/// Chaque niveau joué crée une SE.Scene3DIngame (classe internal, constructeur trivial).
/// On ajoute nos objets à cette scène : elle leur diffuse ensuite les événements
/// (IUpdate, ICheckpointStorage, IGoalReached...) sans autre patch.
///
/// NB : on ne patche PAS GlobalGame.CreateIngameCustom : la méthode est trop grosse
/// et Harmony échoue à la réécrire ("Incorrect code generation for exception block").
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
                ModLog.Info("Scène de replay : rien à injecter");
                return;
            }

            scene.Add(new DebugProbe(scene));
            ModLog.Info("Sonde ajoutée à une nouvelle scène de niveau");
        }
        catch (Exception e)
        {
            ModLog.Error("IngameScenePatch.Postfix", e);
        }
    }
}
