using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Dernier état de round reçu du serveur. Écrit par le thread réseau, lu par le jeu : on publie des
/// instantanés immuables (remplacement atomique de référence), donc pas de verrou côté lecture.
/// </summary>
internal sealed class RoundState
{
    private volatile Snapshot current = new(new RoundInfo(0, "", RoundPhase.Free, 0, 0, 0, false), 0, new List<LeaderboardEntry>());

    /// <summary>Classement du match (points cumulés), remplacé en bloc par le thread réseau.</summary>
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
        // Nouveau round : l'ancien classement n'a plus de sens.
        var board = info.RoundId == old.Info.RoundId ? old.Leaderboard : new List<LeaderboardEntry>();
        double now = NetClock.Now;
        double? goAt = info.Mode == GameMode.Rounds && info.Phase == RoundPhase.Playing
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
