using System.Reflection;
using HarmonyLib;
using SE;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Checkpoint splits, shown with the game's own time difference display (<see cref="TimeDiffOnCheckpoints"/>,
/// the one used against ghosts): our best run of the round on this map becomes its "opponent".
/// Global state (scenes are recreated on every retry). Game thread only.
/// </summary>
internal static class SplitTracker
{
    // TimeDiffOnCheckpoints compares every checkpoint with this (readonly, set by the game when the scene is built).
    private static readonly FieldInfo OpponentField = AccessTools.Field(typeof(TimeDiffOnCheckpoints), "opponent");

    private static string context = "";
    private static LevelTimeData? best;

    /// <summary>Forgets the reference when the map or the round changes.</summary>
    private static void EnsureContext(string mapKey, int roundId)
    {
        // Rounds mode: one attempt per round, so the reference is our best run on this map over the match.
        bool rounds = MultiplayerClient.Instance.Round.Current.Info.Mode == SBMultiplayer.Protocol.GameMode.Rounds;
        string key = rounds ? $"rounds|{mapKey}" : $"{roundId}|{mapKey}";
        if (key != context)
        {
            context = key;
            best = null;
        }
    }

    /// <summary>New run (new scene): our best run becomes the opponent of the game's split display.</summary>
    public static void AttachTo(TimeDiffOnCheckpoints? display, string mapKey, int roundId)
    {
        EnsureContext(mapKey, roundId);
        if (display != null && best != null && ModConfig.Current.ShowSplits)
        {
            OpponentField.SetValue(display, best);
        }
    }

    /// <summary>
    /// Finish (called at the end of the tick, once the timer is final): shows the final difference with the
    /// game's display, and keeps the run as the new reference if it's our best. Returns the difference in ms.
    /// </summary>
    public static int? Finished(string mapKey, int roundId, LevelTimer timer, TimeDiffOnCheckpoints? display)
    {
        EnsureContext(mapKey, roundId);
        LevelTimeData run = timer.Counter;
        int? delta = best != null ? (int)run.MS - (int)best.MS : null;

        if (display != null && best != null && ModConfig.Current.ShowSplits)
        {
            display.ShowTimeDiff(best.MS, null); // null checkpoint = final time
        }

        if (best == null || run.MS < best.MS)
        {
            // Copy: the game keeps updating its own LevelTimeData.
            var checkpoints = run.Checkpoints
                .Select(c => new CheckpointTimeAndRestarts(c.CheckpointID, c.MS, new List<uint>(), c.Rewindable))
                .ToList();
            best = new LevelTimeData(run.MS, run.TotalMS, run.RestartCount, checkpoints, ghostHasCheckpointPenalties: true);
        }
        return delta;
    }
}
