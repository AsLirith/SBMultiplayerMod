using BytingLib;
using HarmonyLib;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Protocol;
using SE;
using PlayerState = SBMultiplayer.Protocol.PlayerState;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Ajouté à chaque scène de niveau. Envoie l'état de notre planche et affiche un ghost
/// pour chaque joueur distant présent sur la même map.
/// </summary>
internal sealed class MultiplayerSceneHook : IUpdate, ICheckpointStorage, IDisposable
{
    /// <summary>Un envoi tous les 2 ticks = 30 états par seconde.</summary>
    private const int SendEveryTicks = 2;

    private static readonly AccessTools.FieldRef<Board, PlayerContainer> BoardPlayerContainer =
        AccessTools.FieldRefAccess<Board, PlayerContainer>("playerContainer");

    private static readonly AccessTools.FieldRef<PlayerContainer, Level> ContainerLevel =
        AccessTools.FieldRefAccess<PlayerContainer, Level>("level");

    private readonly Scene scene;
    private readonly MultiplayerClient client = MultiplayerClient.Instance;
    private readonly Dictionary<int, (RemotePlayer Player, NetGhostBoard Board)> ghosts = new();
    private readonly ValueEvent<float> ghostOpacity = new(0.5f);
    private Board? board;
    private string mapKey = "";
    private int tick;
    private ushort segment;
    private bool disposed;

    public MultiplayerSceneHook(Scene scene)
    {
        this.scene = scene;
    }

    public void Update()
    {
        client.EnsureConnected();

        if (board == null)
        {
            board = scene.AllThings.OfType<Board>().FirstOrDefault();
            if (board == null)
            {
                return;
            }
            mapKey = ComputeMapKey(board);
            client.SetMap(this, mapKey);
        }

        tick++;
        if (tick % SendEveryTicks == 0)
        {
            GhostState s = GameAccess.GetGhostState(board);
            client.SendState(new PlayerState(NetClock.Now,
                new Vec3(s.Position.X, s.Position.Y, s.Position.Z),
                new Vec3(s.Orientation.X, s.Orientation.Y, s.Orientation.Z),
                segment));
        }

        SyncGhosts();
    }

    /// <summary>Crée ou retire les ghosts pour coller à la liste des joueurs présents sur notre map.</summary>
    private void SyncGhosts()
    {
        foreach (RemotePlayer p in client.Players.Values)
        {
            bool shouldShow = p.MapKey == mapKey;
            bool shown = ghosts.TryGetValue(p.Id, out var entry) && ReferenceEquals(entry.Player, p);
            if (shouldShow && !shown)
            {
                if (ghosts.ContainsKey(p.Id))
                {
                    RemoveGhost(p.Id); // même id, nouvel objet (reconnexion)
                }
                var ghostBoard = new NetGhostBoard(p, board!.Assets, GameAccess.GetTransparencyBatch(scene), ghostOpacity,
                    zFightingIndex: 200 + (p.Id % 1000));
                scene.Add(ghostBoard);
                ghosts[p.Id] = (p, ghostBoard);
                ModLog.Info($"Ghost affiché pour {p.Name} (#{p.Id})");
            }
            else if (!shouldShow && shown)
            {
                RemoveGhost(p.Id);
            }
        }

        if (ghosts.Count > 0)
        {
            foreach (int id in ghosts.Keys.Where(id => !client.Players.TryGetValue(id, out var p) || !ReferenceEquals(p, ghosts[id].Player)).ToList())
            {
                RemoveGhost(id);
            }
        }
    }

    private void RemoveGhost(int id)
    {
        if (ghosts.Remove(id, out var entry))
        {
            scene.Remove(entry.Board); // la scène appelle Dispose()
            ModLog.Info($"Ghost retiré pour {entry.Player.Name} (#{id})");
        }
    }

    public void StoreCheckpoint(ICheckpointInfo checkpointInfo)
    {
    }

    public void LoadCheckpoint(ICheckpointInfo checkpointInfo)
    {
        // Restart ou respawn : nouveau segment, les autres ne verront pas la planche glisser jusqu'au respawn.
        segment++;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        // Les ghosts appartiennent à la scène, qui les dispose elle-même.
        ghosts.Clear();
        client.SetMap(this, "");
    }

    /// <summary>Identifiant stable d'une map, identique pour tous les joueurs qui ont la même version.</summary>
    private static string ComputeMapKey(Board board)
    {
        Level level = ContainerLevel(BoardPlayerContainer(board));
        if (!level.IsCustom)
        {
            return $"o:{level.ID}:{level.Version}";
        }
        return level.CustomMapKey is { } onlineKey
            ? $"c:{onlineKey}"
            : $"l:{level.Name}:{level.Version}";
    }
}
