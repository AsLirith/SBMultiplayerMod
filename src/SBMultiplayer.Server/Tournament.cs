using SBMultiplayer.Protocol;

namespace SBMultiplayer.Server;

/// <param name="HostSteamId">The host's Steam ID. The host can always get back in, and watches if they aren't on the roster.</param>
public sealed record TournamentSettings(string Id, GameMode Mode, List<string> Maps, int RoundsPerMap,
    int MinutesPerMap, int CupPointsLimit, int EliminatedPerRound, string? HostSteamId = null);

public sealed record RosterEntry(string SteamId, string Name);

/// <summary>Resumable state of a tournament. Wins and Flagged are keyed by roster display name.</summary>
public sealed record TournamentSave(List<RosterEntry> Roster, List<StandingEntry> Standings,
    List<string> EliminatedOrder, Dictionary<string, int> Wins, Dictionary<string, int> Flagged,
    int NextMapIndex, int NextRoundInMap, int RoundsCompleted);

public sealed record TournamentPlace(int Place, string SteamId, string Name, int Points, int Wins, int RoundsPlayed, StandingStatus Status, int Flagged);

public enum TournamentReportKind { Started, Round, Final, EndedEarly }

public sealed record TournamentReport(string TournamentId, TournamentReportKind Kind, TournamentSave? Save, List<TournamentPlace> Standings);

public static class TournamentPlacing
{
    /// <summary>Final ranking, best first. Ties on points are broken by name.</summary>
    public static List<StandingEntry> Order(GameMode mode, IReadOnlyList<StandingEntry> standings, IReadOnlyList<string> eliminatedOrder)
    {
        var byPoints = standings
            .OrderByDescending(e => e.Points)
            .ThenBy(e => e.Name, StringComparer.Ordinal);

        switch (mode)
        {
            case GameMode.Cup:
                return byPoints
                    .OrderBy(e => e.Status == StandingStatus.Winner ? 0 : e.Status == StandingStatus.Finalist ? 1 : 2)
                    .ToList();

            case GameMode.Elimination:
                // Winner, then survivors, then the knocked-out players with the last one out first.
                // A knocked-out player with no recorded order goes below every listed one.
                var knockOut = new Dictionary<string, int>();
                for (int i = 0; i < eliminatedOrder.Count; i++) knockOut[eliminatedOrder[i]] = i;
                return byPoints
                    .OrderBy(e => e.Status == StandingStatus.Winner ? 0 : e.Status == StandingStatus.Eliminated ? 2 : 1)
                    .ThenBy(e => e.Status != StandingStatus.Eliminated ? 0 : knockOut.TryGetValue(e.Name, out int k) ? -k : 1)
                    .ToList();

            default:
                return byPoints.ToList();
        }
    }
}
