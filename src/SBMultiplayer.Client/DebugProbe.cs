using BytingLib;
using HarmonyLib;
using SE;

namespace SBMultiplayer.Client;

/// <summary>
/// Debug (step 1): checks that an object added to the scene receives ticks and race events,
/// and that the player's state can be read (what is sent over the network).
/// </summary>
internal sealed class DebugProbe : IUpdate, ICheckpointStorage, IGoalReached
{
    private static readonly Func<Board, GhostState> GetGhostState = Ghosts.GameAccess.GetGhostState;

    private readonly Scene scene;
    private Board? board;
    private int tick;
    private int checkpointCount;
    private bool warnedNoBoard;

    public DebugProbe(Scene scene)
    {
        this.scene = scene;
    }

    public void Update()
    {
        tick++;
        board ??= scene.AllThings.OfType<Board>().FirstOrDefault();

        if (tick % 60 != 0)
        {
            return;
        }

        if (board == null)
        {
            if (!warnedNoBoard)
            {
                warnedNoBoard = true;
                ModLog.Warn($"tick {tick}: no Board found in the scene");
            }
            return;
        }

        GhostState s = GetGhostState(board);
        ModLog.Info($"tick {tick} pos=({s.Position.X:F2}, {s.Position.Y:F2}, {s.Position.Z:F2}) " +
                    $"rot=({s.Orientation.X:F2}, {s.Orientation.Y:F2}, {s.Orientation.Z:F2})");
    }

    public void StoreCheckpoint(ICheckpointInfo checkpointInfo)
    {
        checkpointCount++;
        ModLog.Info($"Checkpoint reached ({checkpointInfo.GetType().Name}), total {checkpointCount}");
    }

    public void LoadCheckpoint(ICheckpointInfo checkpointInfo)
    {
        if (checkpointInfo is FirstCheckpoint)
        {
            checkpointCount = 0;
            ModLog.Info("Run restarted");
        }
        else
        {
            ModLog.Info($"Respawn at checkpoint ({checkpointInfo.GetType().Name})");
        }
    }

    public void GoalReached()
    {
        ModLog.Info($"FINISH! (tick {tick})");
    }
}
