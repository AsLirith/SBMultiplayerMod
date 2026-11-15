using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Protocol;
using SE;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Synchronized start (Rounds mode). The game freezes the player on the start line with its
/// "click to start" popup (<c>ClickToStartSceneCloser</c>) until they press a key; we keep that popup
/// closed to player input and close it ourselves at the server's start time.
/// Restarting the level after the release means giving up (one attempt per round): the restart key must be
/// pressed twice quickly or held. Dying or respawning at the start before the first checkpoint isn't giving up:
/// the run goes on, and the time lost is added to the finish time.
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

    /// <summary>Restarting the level must be confirmed: second press within this delay...</summary>
    private const double DoublePressSeconds = 0.6;

    /// <summary>...or the key held this long.</summary>
    private const double HoldSeconds = 1.0;

    private static double lastRestartPress = double.NegativeInfinity;
    private static bool holdPending;

    /// <summary>The player restarted the level on purpose (confirmed key, pause menu) during this round's run.</summary>
    private static bool restartedOnPurpose;

    /// <summary>The level scene in which the player died or respawned at the start during the run.</summary>
    private static CheckpointManager? respawnedOn;

    /// <summary>Run time lost by respawning at the start (the game restarts its timer there), added to the finish time.</summary>
    public static uint LostMs { get; private set; }

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
    public static bool IsEliminated => IsNameEliminated(MultiplayerClient.Instance.PlayerName);

    /// <summary>Elimination mode: this player has been eliminated from the current match (they wait on the start line).</summary>
    public static bool IsNameEliminated(string name)
    {
        MultiplayerClient client = MultiplayerClient.Instance;
        return client.Round.Current.Info.Mode == GameMode.Elimination
            && client.Round.Standings.Any(e => e.Name == name && e.Status == StandingStatus.Eliminated);
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
            restartedOnPurpose = false;
            respawnedOn = null;
            LostMs = 0;
            holdPending = false;
        }
    }

    /// <summary>Racing in a one-attempt round (Rounds, Cup, Elimination): restarting the level would give up.</summary>
    public static bool IsSingleAttemptRun =>
        Applies && MultiplayerClient.Instance.Round.Current.Info is { Phase: RoundPhase.Playing, Mode: not GameMode.HideAndSeek }
        && State == RunState.Racing && !Spectator.IsWatching;

    /// <summary>
    /// The restart key was pressed during a one-attempt run. Returns true to restart (second press in a row),
    /// false to wait for a confirmation (second press, or the key held: see <see cref="Tick"/>).
    /// </summary>
    public static bool ConfirmRestartPress()
    {
        double now = NetClock.Now;
        if (now - lastRestartPress <= DoublePressSeconds)
        {
            lastRestartPress = double.NegativeInfinity;
            holdPending = false;
            return true;
        }
        lastRestartPress = now;
        holdPending = true;
        UI.Notifications.Warn(UI.Tr.ConfirmGiveUp(GameAccess.RestartKeyName));
        return false;
    }

    /// <summary>The level is being restarted (confirmed key, pause menu): during the run, that's giving up.</summary>
    public static void OnRestartOnPurpose()
    {
        if (IsSingleAttemptRun)
        {
            restartedOnPurpose = true;
        }
    }

    /// <summary>A checkpoint is being loaded (death, respawn key, restart). <paramref name="timer"/> still has the run's time.</summary>
    public static void OnLoadCheckpoint(CheckpointManager manager, bool firstCheckpoint, LevelTimer? timer)
    {
        if (!firstCheckpoint || restartedOnPurpose || !IsSingleAttemptRun)
        {
            return;
        }
        // Died or respawned before the first checkpoint: the game restarts its timer, so the time lost is kept here.
        uint lost = timer?.Counter.MS ?? 0;
        LostMs += lost;
        respawnedOn = manager;
        UI.Notifications.Info(UI.Tr.BackToStartPenalty(lost / 1000f));
        ModLog.Info($"Respawned at the start during the run: +{Msg.FormatTime(lost)} (total {Msg.FormatTime(LostMs)})");
    }

    /// <summary>
    /// Called from the start-line popup's update, every tick. Returns true to let the game handle the popup
    /// normally, false to keep the player frozen. <paramref name="release"/> closes the popup (start!).
    /// </summary>
    public static bool AllowPopupUpdate(Action release)
    {
        if (Spectator.IsWatching && CheckpointManager is { IsInFirstCheckpoint: true })
        {
            lastHeldTick = tick;
            return false; // spectating or eliminated: stay frozen on the start line, in every mode
        }
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
            {
                // Hide and Seek: the mice leave at the start signal, the hunter after the hiding time.
                double? releaseAt = round.Info.Mode == GameMode.HideAndSeek ? HideSeek.ReleaseTime(HideSeek.LocalRole) : round.GoAt;
                if (round.Info.Phase != RoundPhase.Playing || releaseAt is not double goAt || NetClock.Now < goAt)
                {
                    return false; // waiting for players or for the countdown
                }
                if (NetClock.Now - goAt > LateToleranceSeconds)
                {
                    state = RunState.Late;
                    if (readySentFor == round.Info.RoundId)
                    {
                        // Counted in the race (game in the background at the start...): the round mustn't wait for us.
                        MultiplayerClient.Instance.SendGiveUp(round.Info.RoundId);
                        UI.Notifications.Warn(UI.Tr.MissedStart);
                    }
                    return false;
                }
                state = RunState.Racing;
                ReleasedAt = NetClock.Now;
                ModLog.Info($"Start! (round {round.Info.RoundId}, {(NetClock.Now - goAt) * 1000:0} ms after the signal)");
                release();
                return true;
            }

            case RunState.Racing when round.Info.Mode == GameMode.HideAndSeek:
                return true; // Hide and Seek: back to the start (restart, finish line) is allowed, nothing to give up

            case RunState.Racing when !restartedOnPurpose && ReferenceEquals(respawnedOn, CheckpointManager):
                return true; // died or respawned at the start: the run goes on (the time lost is added)

            case RunState.Racing:
                // Back on the start line after the release (level restarted, map reloaded): giving up this round.
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
        if (holdPending)
        {
            // Restart key held long enough: confirmed.
            if (!IsSingleAttemptRun || GameAccess.Input?.RestartLevel.Down != true)
            {
                holdPending = false;
            }
            else if (NetClock.Now - lastRestartPress >= HoldSeconds)
            {
                holdPending = false;
                lastRestartPress = double.NegativeInfinity;
                CheckpointManager?.RestartLevel();
            }
        }
        RoundInfo info = client.Round.Current.Info;
        if (Applies && info.Phase == RoundPhase.Preparing && IsHeldOnStartLine && readySentFor != info.RoundId && !IsEliminated && !client.IsSpectating)
        {
            readySentFor = info.RoundId;
            client.SendReady(info.RoundId);
            ModLog.Info($"Ready on the start line (round {info.RoundId})");
        }
    }
}
