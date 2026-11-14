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

/// <summary>
/// The game lists what disqualifies a run from the official leaderboards (shown in the "modded" notice in the
/// top-left corner). It detects our Harmony library as "mod: 0Harmony": we show the mod's name and version instead.
/// The list stays non-empty, so runs are still never uploaded.
/// </summary>
[HarmonyPatch(typeof(RunUploadBlockers), nameof(RunUploadBlockers.GetReasons))]
internal static class ModdedNoticePatch
{
    private static void Postfix(List<string> __result)
    {
        int index = __result.IndexOf("mod: 0Harmony");
        if (index >= 0)
        {
            __result[index] = Compatibility.IsCompatible
                ? $"mod: SBMultiplayer v{SBMultiplayer.Client.UI.MultiplayerMenu.Version}"
                : SBMultiplayer.Client.UI.Tr.IncompatibleNotice(SBMultiplayer.Client.UI.MultiplayerMenu.Version);
        }
    }
}
