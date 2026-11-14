using BytingLib;
using Microsoft.Xna.Framework;
using SE;

namespace SBMultiplayer.Client.Ghosts;

/// <summary>
/// Debug (step 2): a ghost replaying the player's board with a fixed delay.
/// It validates the whole remote-player display chain without networking:
/// state capture → timestamped buffer → interpolation → NetGhostBoard.
/// </summary>
internal sealed class DelayedSelfGhost : IUpdate, ICheckpointStorage, IGhostPoseSource
{
    private const double DelaySeconds = 1.5;

    private readonly Scene scene;
    private readonly SnapshotBuffer buffer = new();
    private readonly ValueEvent<float> opacity = new(0.6f);
    private Board? board;
    private NetGhostBoard? ghost;
    private int tick;
    private ushort segment;

    public DelayedSelfGhost(Scene scene)
    {
        this.scene = scene;
    }

    public void Update()
    {
        board ??= scene.AllThings.OfType<Board>().FirstOrDefault();
        if (board == null)
        {
            return;
        }

        if (ghost == null)
        {
            ghost = new NetGhostBoard(this, board.Assets, GameAccess.GetTransparencyBatch(scene), opacity, zFightingIndex: 100);
            scene.Add(ghost); // the scene will dispose of it
            ModLog.Info("Delayed ghost created");
        }

        tick++;
        GhostState s = GameAccess.GetGhostState(board);
        buffer.Add(new Snapshot(tick / (double)GameAccess.TicksPerSecond, s.Position, s.Orientation, segment));
    }

    public bool TryGetPose(float extrapolation, out Vector3 position, out Vector3 orientation)
    {
        // The engine draws between the previous and current tick: do the same, plus the delay.
        double renderTime = (tick - 1 + extrapolation) / GameAccess.TicksPerSecond - DelaySeconds;
        if (renderTime < 0)
        {
            position = default;
            orientation = default;
            return false;
        }
        return buffer.TrySample(renderTime, out position, out orientation);
    }

    public void StoreCheckpoint(ICheckpointInfo checkpointInfo)
    {
    }

    public void LoadCheckpoint(ICheckpointInfo checkpointInfo)
    {
        // Restart or respawn: the board is teleported, the ghost must not slide to the respawn point.
        segment++;
    }
}
