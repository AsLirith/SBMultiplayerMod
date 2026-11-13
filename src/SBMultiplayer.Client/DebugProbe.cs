using BytingLib;
using HarmonyLib;
using SE;

namespace SBMultiplayer.Client;

/// <summary>
/// Étape 1 : valide qu'un objet ajouté à la scène reçoit bien les ticks et les événements de course,
/// et qu'on sait lire l'état du joueur (ce qu'on enverra plus tard sur le réseau).
/// </summary>
internal sealed class DebugProbe : IUpdate, ICheckpointStorage, IGoalReached
{
    // Board.GetGhostState() est internal : on passe par un délégué Harmony (rapide, résolu une seule fois).
    private static readonly Func<Board, GhostState> GetGhostState =
        AccessTools.MethodDelegate<Func<Board, GhostState>>(AccessTools.Method(typeof(Board), "GetGhostState"));

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
                ModLog.Warn($"tick {tick} : aucun Board trouvé dans la scène");
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
        ModLog.Info($"Checkpoint pris ({checkpointInfo.GetType().Name}), total {checkpointCount}");
    }

    public void LoadCheckpoint(ICheckpointInfo checkpointInfo)
    {
        if (checkpointInfo is FirstCheckpoint)
        {
            checkpointCount = 0;
            ModLog.Info("Restart de la run");
        }
        else
        {
            ModLog.Info($"Respawn au checkpoint ({checkpointInfo.GetType().Name})");
        }
    }

    public void GoalReached()
    {
        ModLog.Info($"ARRIVÉE ! (tick {tick})");
    }
}
