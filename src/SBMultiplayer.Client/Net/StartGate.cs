using SBMultiplayer.Protocol;
using SE;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Synchronized start (Rounds mode). The game freezes the player on the start line with its
/// "click to start" popup (<c>ClickToStartSceneCloser</c>) until they press a key; we keep that popup
/// closed to player input and close it ourselves at the server's start time.
/// Going back to the start after the release means giving up (one attempt per round).
/// Respawns at checkpoints use the same popup and are left alone.
/// Game thread only.
/// </summary>
internal static class StartGate
{
    public enum RunState
    {
        /// <summary>On the start line, waiting for the start signal.</summary>
        Waiting,
        /// <summary>Released at the start: racing.</summary>
        Racing,
        Finished,
        GaveUp,
        /// <summary>Arrived after the start: waits for the next round.</summary>
        Late,
    }

    /// <summary>More than this after the start signal when reaching the start line = too late to race.</summary>
    private const double LateToleranceSeconds = 1.0;

    private static int stateRoundId = -1;
    private static RunState state;
    private static int readySentFor = -1;

    /// <summary>Checkpoint manager of the current level scene (set by the scene hook).</summary>
    public static CheckpointManager? CheckpointManager { get; set; }

    /// <summary>Last global tick during which a start-line popup was on screen.</summary>
    private static long lastHeldTick = -10;

    private static long tick;

    /// <summary>Local time of our release at the start (to show "GO!" briefly).</summary>
    public static double ReleasedAt { get; private set; } = double.NegativeInfinity;

    public static RunState State
    {
        get
        {
            SyncRound();
            return state;
        }
    }

    /// <summary>True if the synchronized start applies right now (Rounds mode, on the round's map).</summary>
    public static bool Applies
    {
        get
        {
            MultiplayerClient client = MultiplayerClient.Instance;
            RoundInfo info = client.Round.Current.Info;
            return info.Mode.IsSynchronized() && info.Phase is RoundPhase.Preparing or RoundPhase.Playing or RoundPhase.Results
                && client.IsOnRoundMap;
        }
    }

    /// <summary>Elimination mode: we've been eliminated from the current match.</summary>
    public static bool IsEliminated
    {
        get
        {
            MultiplayerClient client = MultiplayerClient.Instance;
            if (client.Round.Current.Info.Mode != GameMode.Elimination)
            {
                return false;
            }
            string me = client.PlayerName;
            return client.Round.Standings.Any(e => e.Name == me && e.Status == StandingStatus.Eliminated);
        }
    }

    /// <summary>The player is currently held on the start line (popup on screen).</summary>
    public static bool IsHeldOnStartLine => tick - lastHeldTick <= 2;

    /// <summary>A new round starts from <see cref="RunState.Waiting"/>.</summary>
    private static void SyncRound()
    {
        int id = MultiplayerClient.Instance.Round.Current.Info.RoundId;
        if (id != stateRoundId)
        {
            stateRoundId = id;
            state = RunState.Waiting;
        }
    }

    /// <summary>
    /// Called from the start-line popup's update, every tick. Returns true to let the game handle the popup
    /// normally, false to keep the player frozen. <paramref name="release"/> closes the popup (start!).
    /// </summary>
    public static bool AllowPopupUpdate(Action release)
    {
        if (!Applies || CheckpointManager is not { IsInFirstCheckpoint: true })
        {
            return true; // Time Attack, other map, or respawn at a checkpoint: normal behaviour
        }
        SyncRound();
        lastHeldTick = tick;
        RoundState.Snapshot round = MultiplayerClient.Instance.Round.Current;
        if (IsEliminated)
        {
            return false; // eliminated: watch the others from the start line until the next match
        }

        switch (state)
        {
            case RunState.Waiting:
                if (round.Info.Phase != RoundPhase.Playing || round.GoAt is not double goAt || NetClock.Now < goAt)
                {
                    return false; // waiting for players or for the countdown
                }
                if (NetClock.Now - goAt > LateToleranceSeconds)
                {
                    state = RunState.Late;
                    return false;
                }
                state = RunState.Racing;
                ReleasedAt = NetClock.Now;
                ModLog.Info($"Start! (round {round.Info.RoundId}, {(NetClock.Now - goAt) * 1000:0} ms after the signal)");
                release();
                return true;

            case RunState.Racing:
                // Back on the start line after the release: restarting = giving up this round.
                state = RunState.GaveUp;
                MultiplayerClient.Instance.SendGiveUp(round.Info.RoundId);
                UI.Notifications.Warn(UI.Tr.GaveUp);
                return false;

            default:
                return false; // finished, gave up or late: wait for the next round
        }
    }

    /// <summary>Called by the client when our finish time is sent (Rounds mode).</summary>
    public static void MarkFinished()
    {
        SyncRound();
        if (state == RunState.Racing)
        {
            state = RunState.Finished;
        }
    }

    /// <summary>Called every global tick: tells the server we're ready on the start line (once per round).</summary>
    public static void Tick(MultiplayerClient client)
    {
        tick++;
        RoundInfo info = client.Round.Current.Info;
        if (Applies && info.Phase == RoundPhase.Preparing && IsHeldOnStartLine && readySentFor != info.RoundId && !IsEliminated)
        {
            readySentFor = info.RoundId;
            client.SendReady(info.RoundId);
            ModLog.Info($"Ready on the start line (round {info.RoundId})");
        }
    }
}
