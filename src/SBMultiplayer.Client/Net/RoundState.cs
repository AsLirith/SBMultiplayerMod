using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Last round state received from the server. Written by the network thread, read by the game: immutable
/// snapshots are published (atomic reference swap), so no lock on the reading side.
/// </summary>
internal sealed class RoundState
{
    private volatile Snapshot current = new(new RoundInfo(0, "", RoundPhase.Free, 0, 0, 0, false), 0, new List<LeaderboardEntry>());

    /// <summary>Match standings (accumulated points), replaced as a whole by the network thread.</summary>
    public volatile IReadOnlyList<StandingEntry> Standings = new List<StandingEntry>();

    public sealed record Snapshot(RoundInfo Info, double ReceivedAt, IReadOnlyList<LeaderboardEntry> Leaderboard)
    {
        /// <summary>Rounds mode: local time (<see cref="NetClock"/>) of the start signal, corrected for latency.</summary>
        public double? GoAt { get; init; }

        public double RemainingSeconds => Info.Paused ? Info.RemainingSeconds : Math.Max(0, Info.RemainingSeconds - (NetClock.Now - ReceivedAt));

        public bool IsRound => Info.Phase != RoundPhase.Free;
    }

    public Snapshot Current => current;

    /// <param name="oneWayLatencySeconds">Server → client delay, to compute the start signal time.</param>
    public void SetInfo(RoundInfo info, double oneWayLatencySeconds)
    {
        Snapshot old = current;
        // New round: the old standings no longer make sense.
        var board = info.RoundId == old.Info.RoundId ? old.Leaderboard : new List<LeaderboardEntry>();
        double now = NetClock.Now;
        double? goAt = info.Mode.IsSynchronized() && info.Phase == RoundPhase.Playing
            ? now + info.StartIn - oneWayLatencySeconds
            : null;
        current = new Snapshot(info, now, board) { GoAt = goAt };
    }

    public void SetLeaderboard(int roundId, List<LeaderboardEntry> entries)
    {
        Snapshot old = current;
        if (roundId == old.Info.RoundId)
        {
            current = old with { Leaderboard = entries };
        }
    }

    public void Reset()
    {
        current = new(new RoundInfo(0, "", RoundPhase.Free, 0, 0, 0, false), 0, new List<LeaderboardEntry>());
        Standings = new List<StandingEntry>();
    }
}
