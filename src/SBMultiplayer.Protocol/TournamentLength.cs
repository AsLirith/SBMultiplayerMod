namespace SBMultiplayer.Protocol;

/// <param name="Rounds">Estimated number of rounds, or null when open-ended.</param>
/// <param name="Minutes">Total minutes (TimeAttack only).</param>
/// <param name="Fixed">True when the length is exact, false when it is only an estimate.</param>
public readonly record struct LengthEstimate(int? Rounds, int? Minutes, bool Fixed);

/// <summary>Length of a tournament. The mod's creation screen and the server share it.</summary>
public static class TournamentLength
{
    /// <summary>Player count to assume until the real one is known.</summary>
    public const int DefaultPlayers = 10;

    public static LengthEstimate Estimate(GameMode mode, int mapCount, int roundsPerMap, int minutesPerMap, int eliminatedPerRound, int players)
    {
        switch (mode)
        {
            case GameMode.Rounds:
            case GameMode.HideAndSeek:
                return new LengthEstimate(mapCount * roundsPerMap, null, true);
            case GameMode.TimeAttack:
                return new LengthEstimate(mapCount, mapCount * minutesPerMap, true);
            case GameMode.Elimination:
                int p = players > 0 ? players : DefaultPlayers;
                int per = Math.Max(1, eliminatedPerRound);
                return new LengthEstimate(Math.Max(0, (p - 1 + per - 1) / per), null, false);
            default:
                return new LengthEstimate(null, null, false);
        }
    }
}
