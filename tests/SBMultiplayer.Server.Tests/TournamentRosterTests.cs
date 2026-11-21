namespace SBMultiplayer.Server.Tests;

public class TournamentRosterTests
{
    [Fact]
    public void Join_SameNameDifferentSteamId_GetsSuffix()
    {
        var r = new TournamentRoster();
        Assert.Equal("Alex", r.Join("1", "Alex"));
        Assert.Equal("Alex (2)", r.Join("2", "Alex"));
    }

    [Fact]
    public void Join_SameSteamIdTwice_KeepsName()
    {
        var r = new TournamentRoster();
        Assert.Equal("Alex", r.Join("1", "Alex"));
        Assert.Equal("Alex", r.Join("1", "Renamed"));
        Assert.Single(r.Entries);
    }

    [Fact]
    public void Join_StripsHash()
    {
        Assert.Equal("AB", new TournamentRoster().Join("1", "A#B"));
    }

    [Fact]
    public void Join_EmptyName_BecomesPlayer()
    {
        Assert.Equal("Player", new TournamentRoster().Join("1", "#"));
    }

    [Fact]
    public void Lock_KeepsOnlyPresent_And_RefusesOthers()
    {
        var r = new TournamentRoster();
        r.Join("1", "A"); r.Join("2", "B");
        Assert.True(r.CanEnter("9"));
        r.Lock(new[] { "1" });
        Assert.True(r.Locked);
        Assert.True(r.CanEnter("1"));
        Assert.False(r.CanEnter("2"));
        Assert.False(r.CanEnter("9"));
        Assert.Single(r.Entries);
        Assert.Equal("1", r.SteamIdOf("A"));
        Assert.Null(r.SteamIdOf("B"));
    }

    [Fact]
    public void FromSave_IsLocked_AndKeepsNames()
    {
        var r = TournamentRoster.FromSave(new[] { new RosterEntry("1", "Alex"), new RosterEntry("2", "Alex (2)") });
        Assert.True(r.Locked);
        Assert.True(r.CanEnter("2"));
        Assert.False(r.CanEnter("3"));
        Assert.Equal("2", r.SteamIdOf("Alex (2)"));
        Assert.Equal(2, r.Entries.Count);
    }
}
