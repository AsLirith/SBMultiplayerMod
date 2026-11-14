using BytingLib;
using HarmonyLib;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SE;

namespace SBMultiplayer.Client.Patches;

/// <summary>
/// GlobalGame.GetTopmostScene() (3 lignes) est appelée une fois par tick par le moteur, juste après la
/// mise à jour du jeu, quelle que soit la scène : c'est notre « tick global » (menus compris).
/// Elle nous donne aussi l'instance de GlobalGame, nécessaire pour lancer des niveaux.
/// </summary>
[HarmonyPatch(typeof(GlobalGame), "GetTopmostScene")]
internal static class GlobalTickPatch
{
    private static bool failed;

    private static void Postfix(GlobalGame __instance)
    {
        if (failed)
        {
            return;
        }
        try
        {
            GameAccess.Game = __instance;
            MultiplayerClient.Instance.GlobalTick(GameAccess.CurrentScene);
        }
        catch (Exception e)
        {
            // Une exception ici tomberait à chaque tick : on la logge une fois et on coupe.
            failed = true;
            ModLog.Error("GlobalTick désactivé suite à une erreur", e);
        }
    }
}

/// <summary>
/// En round, l'écran de fin de niveau (sélection de niveau + classement officiel) est remplacé
/// par un retry immédiat, pour enchaîner les tentatives comme en time attack.
/// </summary>
[HarmonyPatch(typeof(GlobalGame), "LevelFinished")]
internal static class LevelFinishedPatch
{
    private static bool Prefix()
    {
        try
        {
            if (MultiplayerClient.Instance.IsOnRoundMap)
            {
                GameAccess.RetryCurrentLevel();
                return false; // on saute l'écran de fin
            }
        }
        catch (Exception e)
        {
            ModLog.Error("LevelFinishedPatch", e);
        }
        return true;
    }
}
