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
internal sealed class MultiplayerSceneHook : IUpdate, ICheckpointStorage, IGoalReached, IDrawBatch, IDisposable
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
    private Board? board;
    private string mapKey = "";
    private int tick;
    private ushort segment;
    private bool disposed;
    private Microsoft.Xna.Framework.Vector3 localPosition;
    private readonly MultiplayerOverlay overlay = new();

    public MultiplayerSceneHook(Scene scene)
    {
        this.scene = scene;
    }

    /// <summary>
    /// Trouve la planche du joueur et déclare la map au serveur. Appelé depuis Update ET DrawBatch :
    /// pendant l'intro ou l'attente du départ, la scène est dessinée avant sa première mise à jour.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(board))]
    private bool EnsureBoard()
    {
        if (board != null)
        {
            return true;
        }
        board = scene.AllThings.OfType<Board>().FirstOrDefault();
        if (board == null)
        {
            return false;
        }
        mapKey = ComputeMapKey(board);
        localPosition = GameAccess.GetGhostState(board).Position;
        client.SetMap(this, mapKey);
        return true;
    }

    public void Update()
    {
        if (!EnsureBoard())
        {
            return;
        }

        tick++;
        if (tick % SendEveryTicks == 0)
        {
            GhostState s = GameAccess.GetGhostState(board);
            localPosition = s.Position;
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
            bool shouldShow = UI.ModSettings.ShowOtherPlayers.Value && p.MapKey == mapKey;
            bool shown = ghosts.TryGetValue(p.Id, out var entry) && ReferenceEquals(entry.Player, p);
            if (shouldShow && !shown)
            {
                if (ghosts.ContainsKey(p.Id))
                {
                    RemoveGhost(p.Id); // même id, nouvel objet (reconnexion)
                }
                var ghostBoard = new NetGhostBoard(p, board!.Assets, GameAccess.GetTransparencyBatch(scene), UI.ModSettings.GhostOpacity,
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
        EnsureBoard();
        int roundId = client.Round.Current.Info.RoundId;
        if (checkpointInfo is FirstCheckpoint)
        {
            SplitTracker.RunStarted(mapKey, roundId);
        }
        else if (scene.AllThings.OfType<LevelTimer>().FirstOrDefault() is { } timer)
        {
            SplitTracker.CheckpointTaken(mapKey, roundId, timer.Counter.MS);
        }
    }

    public void GoalReached()
    {
        // Le jeu ajuste le chrono (fraction de tick) juste APRÈS avoir prévenu les IGoalReached, puis
        // arrête de mettre la scène à jour : on confie la lecture du temps final au tick global,
        // qui s'exécute à la fin de ce même tick.
        var round = client.Round.Current;
        LevelTimer? timer = scene.AllThings.OfType<LevelTimer>().FirstOrDefault();
        ModLog.Info($"Arrivée détectée (round {round.Info.RoundId}, {round.Info.Phase}, sur la map du round : {client.IsOnRoundMap})");
        if (timer == null)
        {
            ModLog.Warn("Arrivée : chrono introuvable dans la scène");
            return;
        }
        // Le temps est toujours lu (intermédiaires), mais n'est envoyé au serveur que pendant un round, sur sa map.
        bool counts = client.IsOnRoundMap && round.Info.Phase == RoundPhase.Playing;
        client.QueueFinish(round.Info.RoundId, () => timer.Counter.MS, sendToServer: counts);
    }

    public void DrawBatch(Microsoft.Xna.Framework.Graphics.SpriteBatch spriteBatch, float extrapolation)
    {
        // Pas de condition sur "board" : pendant l'intro ou après un reset, la scène est dessinée
        // avant sa première mise à jour, et le panneau doit déjà s'afficher.
        if (disposed || !client.IsReady)
        {
            return;
        }
        if (EnsureBoard())
        {
            // Les autres joueurs sont aussi visibles pendant l'attente du départ.
            SyncGhosts();
        }
        try
        {
            overlay.Draw(spriteBatch, scene, board == null ? null : localPosition, ghosts.Values.Select(g => g.Player), extrapolation);
        }
        catch (Exception e)
        {
            overlay.Disable(e);
        }
    }

    public void LoadCheckpoint(ICheckpointInfo checkpointInfo)
    {
        // Restart ou respawn : nouveau segment, les autres ne verront pas la planche glisser jusqu'au respawn.
        segment++;
        if (checkpointInfo is FirstCheckpoint)
        {
            SplitTracker.RunStarted(mapKey, client.Round.Current.Info.RoundId);
        }
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
