using BytingLib;
using HarmonyLib;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SE;

namespace SBMultiplayer.Client.Patches;

/// <summary>
/// GlobalGame.GetTopmostScene() (3 lines) is called once per tick by the engine, right after the
/// game update, whatever the scene: it's our "global tick" (menus included).
/// It also gives us the GlobalGame instance, needed to start levels.
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
            SteamTicket.Listen(); // catches the game's Steam tickets at their real length (tournaments)
            MultiplayerClient.Instance.GlobalTick(GameAccess.CurrentScene);
        }
        catch (Exception e)
        {
            // An exception here would fire on every tick: log it once and switch off.
            failed = true;
            ModLog.Error("GlobalTick disabled after an error", e);
        }
    }
}

/// <summary>
/// During a round, the level end screen (level selection + official leaderboard) is replaced
/// by an immediate retry, to chain attempts like in time attack.
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
                return false; // skip the end screen
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
