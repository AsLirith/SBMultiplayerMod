using SBMultiplayer.Protocol;

namespace SBMultiplayer.Server.Tests;

public class TournamentLengthTests
{
    [Fact]
    public void Rounds_IsMapsTimesRounds()
        => Assert.Equal(new LengthEstimate(12, null, true), TournamentLength.Estimate(GameMode.Rounds, 4, 3, 0, 0, 0));

    [Fact]
    public void HideAndSeek_IsMapsTimesRounds()
        => Assert.Equal(new LengthEstimate(8, null, true), TournamentLength.Estimate(GameMode.HideAndSeek, 4, 2, 0, 0, 0));

    [Fact]
    public void TimeAttack_IsMapsTimesMinutes()
        => Assert.Equal(new LengthEstimate(4, 20, true), TournamentLength.Estimate(GameMode.TimeAttack, 4, 0, 5, 0, 0));

    [Fact]
    public void Cup_IsOpenEnded()
        => Assert.Equal(new LengthEstimate(null, null, false), TournamentLength.Estimate(GameMode.Cup, 4, 3, 5, 0, 10));

    [Fact]
    public void Elimination_EstimatesFromPlayers()
    {
        Assert.Equal(new LengthEstimate(9, null, false), TournamentLength.Estimate(GameMode.Elimination, 4, 3, 5, 1, 10));
        Assert.Equal(new LengthEstimate(5, null, false), TournamentLength.Estimate(GameMode.Elimination, 4, 3, 5, 2, 10));
    }

    [Fact]
    public void Elimination_ZeroPlayersUsesDefault()
        => Assert.Equal(TournamentLength.Estimate(GameMode.Elimination, 4, 3, 5, 1, TournamentLength.DefaultPlayers),
            TournamentLength.Estimate(GameMode.Elimination, 4, 3, 5, 1, 0));
}
