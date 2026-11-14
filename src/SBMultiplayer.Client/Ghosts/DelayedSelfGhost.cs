using BytingLib;
using Microsoft.Xna.Framework;
using SE;

namespace SBMultiplayer.Client.Ghosts;

/// <summary>
/// Étape 2 : un ghost qui rejoue la planche du joueur avec un retard fixe.
/// Il valide toute la chaîne d'affichage d'un joueur distant sans réseau :
/// capture de l'état → tampon horodaté → interpolation → NetGhostBoard.
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
            scene.Add(ghost); // la scène le disposera avec elle
            ModLog.Info("Ghost retardé créé");
        }

        tick++;
        GhostState s = GameAccess.GetGhostState(board);
        buffer.Add(new Snapshot(tick / (double)GameAccess.TicksPerSecond, s.Position, s.Orientation, segment));
    }

    public bool TryGetPose(float extrapolation, out Vector3 position, out Vector3 orientation)
    {
        // Le moteur dessine entre le tick précédent et le tick courant : on fait pareil, avec le retard en plus.
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
        // Restart ou respawn : la planche est téléportée, le ghost ne doit pas « glisser » jusqu'au point de respawn.
        segment++;
    }
}
