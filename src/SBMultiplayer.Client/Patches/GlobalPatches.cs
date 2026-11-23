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
/// top-left corner), and refuses the run (no upload, no local save) if that list isn't empty. It detects our Harmony
/// library as "mod: 0Harmony": the developers allow runs with the mod, so that reason is removed when
/// <see cref="LeaderboardUploads.BlockReason"/> has nothing against the run, and replaced by the mod's own reason otherwise.
/// </summary>
[HarmonyPatch(typeof(RunUploadBlockers), nameof(RunUploadBlockers.GetReasons))]
internal static class ModdedNoticePatch
{
    private static void Postfix(List<string> __result)
    {
        int index = __result.IndexOf("mod: 0Harmony");
        if (index < 0)
        {
            return; // the game skipped its mod check ("submit with cheats", dev_ accounts)
        }
        if (LeaderboardUploads.BlockReason() is string reason)
        {
            __result[index] = reason;
        }
        else
        {
            __result.RemoveAt(index);
        }
    }
}
