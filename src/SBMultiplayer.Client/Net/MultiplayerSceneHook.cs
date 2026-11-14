using BytingLib;
using HarmonyLib;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Protocol;
using SE;
using PlayerState = SBMultiplayer.Protocol.PlayerState;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Added to every level scene. Sends our board's state and shows a ghost
/// for every remote player on the same map.
/// </summary>
internal sealed class MultiplayerSceneHook : IUpdate, ICheckpointStorage, IGoalReached, IDrawBatch, IDisposable
{
    /// <summary>One send every 2 ticks = 30 states per second.</summary>
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
    /// Finds the player's board and declares the map to the server. Called from Update AND DrawBatch:
    /// during the intro or while waiting at the start, the scene is drawn before its first update.
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
        // Synchronized start (Rounds mode) needs to know whether a start-line popup is a start or a respawn.
        StartGate.CheckpointManager = scene.AllThings.OfType<CheckpointManager>().FirstOrDefault();
        HideSeek.Attach(scene, board);
        // Splits: the game's own time difference display compares with our best run of the round.
        SplitTracker.AttachTo(scene.AllThings.OfType<TimeDiffOnCheckpoints>().FirstOrDefault(), mapKey, client.Round.Current.Info.RoundId);
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
            SendState();
        }

        SyncGhosts();
    }

    private double nextFrozenStateAt;

    private void SendState()
    {
        GhostState s = GameAccess.GetGhostState(board!);
        localPosition = s.Position;
        client.SendState(new PlayerState(NetClock.Now,
            new Vec3(s.Position.X, s.Position.Y, s.Position.Z),
            new Vec3(s.Orientation.X, s.Orientation.Y, s.Orientation.Z),
            segment,
            new Vec3(s.CamPosition.X, s.CamPosition.Y, s.CamPosition.Z),
            new Vec3(s.CamOrientation.X, s.CamOrientation.Y, s.CamOrientation.Z)));
    }

    /// <summary>Creates or removes ghosts to match the list of players on our map.</summary>
    private void SyncGhosts()
    {
        foreach (RemotePlayer p in client.Players.Values)
        {
            bool shouldShow = UI.ModSettings.ShowOtherPlayers.Value && p.MapKey == mapKey && !p.IsSpectator && !HideSeek.HideFromLocal(p);
            bool shown = ghosts.TryGetValue(p.Id, out var entry) && ReferenceEquals(entry.Player, p);
            if (shouldShow && !shown)
            {
                if (ghosts.ContainsKey(p.Id))
                {
                    RemoveGhost(p.Id); // same id, new object (reconnection)
                }
                var ghostBoard = new NetGhostBoard(p, board!.Assets, GameAccess.GetTransparencyBatch(scene), UI.ModSettings.GhostOpacity,
                    zFightingIndex: 200 + (p.Id % 1000));
                scene.Add(ghostBoard);
                ghosts[p.Id] = (p, ghostBoard);
                ModLog.Info($"Ghost shown for {p.Name} (#{p.Id})");
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
            scene.Remove(entry.Board); // the scene calls Dispose()
            ModLog.Info($"Ghost removed for {entry.Player.Name} (#{id})");
        }
    }

    public void StoreCheckpoint(ICheckpointInfo checkpointInfo)
    {
        EnsureBoard(); // the run starts (FirstCheckpoint) before the scene's first update
    }

    public void GoalReached()
    {
        // The game adjusts the timer (tick fraction) right AFTER notifying the IGoalReached, then
        // stops updating the scene: reading the final time is left to the global tick,
        // which runs at the end of this same tick.
        var round = client.Round.Current;
        LevelTimer? timer = scene.AllThings.OfType<LevelTimer>().FirstOrDefault();
        ModLog.Info($"Finish detected (round {round.Info.RoundId}, {round.Info.Phase}, on the round's map: {client.IsOnRoundMap})");
        if (timer == null)
        {
            ModLog.Warn("Finish: timer not found in the scene");
            return;
        }
        // The time is always read (splits), but only sent to the server during a round, on its map.
        bool counts = client.IsOnRoundMap && round.Info.Phase == RoundPhase.Playing && round.Info.Mode != GameMode.HideAndSeek;
        client.QueueFinish(round.Info.RoundId, timer, scene.AllThings.OfType<TimeDiffOnCheckpoints>().FirstOrDefault(), sendToServer: counts);
    }

    public void DrawBatch(Microsoft.Xna.Framework.Graphics.SpriteBatch spriteBatch, float extrapolation)
    {
        // No condition on "board": during the intro or after a reset, the scene is drawn
        // before its first update, and the panel must already show.
        if (disposed || !client.IsReady)
        {
            return;
        }
        if (EnsureBoard())
        {
            // Other players are also visible while waiting at the start.
            SyncGhosts();
            if (HideSeek.KeepFrozen && NetClock.Now >= nextFrozenStateAt)
            {
                // Frozen mouse: the scene no longer updates, but the server and newcomers still need our position.
                nextFrozenStateAt = NetClock.Now + 0.5;
                SendState();
            }
        }
        Spectator.Sync(scene); // spectator camera on top of the scene's cameras (also under the start-line popup)
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
        // Restart or respawn: new segment, the others won't see the board slide to the respawn point.
        segment++;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        // Ghosts belong to the scene, which disposes of them itself.
        ghosts.Clear();
        client.SetMap(this, "");
    }

    /// <summary>Stable map identifier, identical for every player who has the same version.</summary>
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
