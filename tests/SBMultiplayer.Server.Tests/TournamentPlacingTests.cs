using SBMultiplayer.Protocol;
using SBMultiplayer.Server;

namespace SBMultiplayer.Server.Tests;

public class TournamentPlacingTests
{
    private static StandingEntry E(string name, int points, StandingStatus status = StandingStatus.Normal)
        => new(name, points, 1, status);

    private static string[] Names(IEnumerable<StandingEntry> s) => s.Select(e => e.Name).ToArray();

    [Fact]
    public void Rounds_ByPointsThenName()
    {
        var order = TournamentPlacing.Order(GameMode.Rounds,
            new[] { E("A", 10), E("C", 30), E("B", 30) }, Array.Empty<string>());
        Assert.Equal(new[] { "B", "C", "A" }, Names(order));
    }

    [Theory]
    [InlineData(GameMode.TimeAttack)]
    [InlineData(GameMode.HideAndSeek)]
    public void TimeAttack_And_HideAndSeek_SameAsRounds(GameMode mode)
    {
        var order = TournamentPlacing.Order(mode,
            new[] { E("A", 10), E("C", 30), E("B", 30) }, Array.Empty<string>());
        Assert.Equal(new[] { "B", "C", "A" }, Names(order));
    }

    [Fact]
    public void Cup_WinnerFirstThenFinalistsThenPoints()
    {
        var order = TournamentPlacing.Order(GameMode.Cup,
            new[] { E("A", 90), E("B", 100, StandingStatus.Finalist), E("C", 100, StandingStatus.Winner) },
            Array.Empty<string>());
        Assert.Equal(new[] { "C", "B", "A" }, Names(order));
    }

    [Fact]
    public void Elimination_WinnerThenSurvivorsThenReverseKnockOutOrder()
    {
        var order = TournamentPlacing.Order(GameMode.Elimination,
            new[]
            {
                E("X", 0, StandingStatus.Eliminated), E("Y", 0, StandingStatus.Eliminated),
                E("Z", 0, StandingStatus.Eliminated), E("S", 0), E("W", 0, StandingStatus.Winner),
            },
            new[] { "X", "Y", "Z" });
        Assert.Equal(new[] { "W", "S", "Z", "Y", "X" }, Names(order));
    }

    [Fact]
    public void Elimination_EliminatedMissingFromOrder_RanksBelowEveryListedOne()
    {
        var order = TournamentPlacing.Order(GameMode.Elimination,
            new[] { E("A", 0, StandingStatus.Eliminated), E("X", 0, StandingStatus.Eliminated), E("Y", 0, StandingStatus.Eliminated) },
            new[] { "X", "Y" });
        Assert.Equal(new[] { "Y", "X", "A" }, Names(order));
    }
}
