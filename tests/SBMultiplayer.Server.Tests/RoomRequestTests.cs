using System.Collections.Specialized;
using System.Text.Json;
using SBMultiplayer.Protocol;
using SBMultiplayer.Server;

namespace SBMultiplayer.Server.Tests;

public class RoomRequestTests
{
    [Fact]
    public void SteamHeaders_AcceptsSeventeenDigits()
    {
        var ok = new NameValueCollection { ["x-sbm-steam-id"] = "76561199227168782", ["x-sbm-steam-name"] = "Ann" };
        Assert.Equal(("76561199227168782", "Ann"), SteamHeaders.Read(ok));

        var bad = new NameValueCollection { ["x-sbm-steam-id"] = "abc", ["x-sbm-steam-name"] = new string('x', 100) };
        (string? id, string? name) = SteamHeaders.Read(bad);
        Assert.Null(id);
        Assert.Equal(64, name!.Length);

        Assert.Equal((null, null), SteamHeaders.Read(new NameValueCollection()));

        var encoded = new NameValueCollection { ["x-sbm-steam-id"] = "76561199227168782", ["x-sbm-steam-name"] = "Zo%C3%AB%20K" };
        Assert.Equal("Zoë K", SteamHeaders.Read(encoded).SteamName);
    }

    [Fact]
    public void CreateRoomRequest_ParsesTournament()
    {
        const string json = """
            {"id":7,"name":"Cup night","password":null,"adminPassword":"pw","maxPlayers":8,
             "tournament":{"settings":{"id":"t7","mode":"Rounds","maps":["o:1","o:2","o:3","o:4"],"roundsPerMap":2,
             "minutesPerMap":5,"cupPointsLimit":0,"eliminatedPerRound":1},"resume":null}}
            """;
        CreateRoomRequest? req = JsonSerializer.Deserialize(json, ServerJson.Default.CreateRoomRequest);
        Assert.NotNull(req?.Tournament);
        Assert.Equal(GameMode.Rounds, req!.Tournament!.Settings.Mode);
        Assert.Equal(4, req.Tournament.Settings.Maps.Count);
        Assert.Null(req.Tournament.Resume);
    }

    [Fact]
    public void CreateRoomRequest_WithoutTournamentIsOrdinary()
    {
        CreateRoomRequest? req = JsonSerializer.Deserialize(
            """{"id":1,"name":"a","maxPlayers":4}""", ServerJson.Default.CreateRoomRequest);
        Assert.Null(req!.Tournament);
    }

    [Fact]
    public async Task SaveSurvivesReportAndResumeRoundTrip()
    {
        var save = new TournamentSave(
            new List<RosterEntry> { new("76561199227168782", "Zoe K"), new("76561199227168783", "Zoe K (2)") },
            new List<StandingEntry>
            {
                new("Zoe K", 12, 3, StandingStatus.Winner),
                new("Zoe K (2)", 4, 2, StandingStatus.Eliminated),
            },
            new List<string> { "Zoe K (2)" },
            new Dictionary<string, int> { ["Zoe K"] = 2, ["Zoe K (2)"] = 1 },
            new Dictionary<string, int> { ["Zoe K"] = 0, ["Zoe K (2)"] = 3 },
            2, 1, 3);

        string? body = null;
        var handler = new CaptureHandler(b => body = b);
        var reporter = new TournamentReporter(new Uri("http://localhost:9/r"), "0123456789abcdef0123456789abcdef", handler, _ => TimeSpan.Zero);
        await reporter.SendAsync(new TournamentReport("t1", TournamentReportKind.Round, save, new List<TournamentPlace>()));

        using JsonDocument doc = JsonDocument.Parse(body!);
        string saveJson = doc.RootElement.GetProperty("save").GetRawText();
        string request = "{\"id\":1,\"name\":\"a\",\"maxPlayers\":4,\"tournament\":{\"settings\":{\"id\":\"t1\",\"mode\":\"Rounds\",\"maps\":[\"o:1\"],"
            + "\"roundsPerMap\":1,\"minutesPerMap\":1,\"cupPointsLimit\":0,\"eliminatedPerRound\":1},\"resume\":" + saveJson + "}}";
        TournamentSave back = JsonSerializer.Deserialize(request, ServerJson.Default.CreateRoomRequest)!.Tournament!.Resume!;

        Assert.Equal(save.Roster, back.Roster);
        Assert.Equal(save.Standings, back.Standings);
        Assert.Equal(save.EliminatedOrder, back.EliminatedOrder);
        Assert.Equal(save.Wins, back.Wins);
        Assert.Equal(save.Flagged, back.Flagged);
        Assert.Equal(save.NextMapIndex, back.NextMapIndex);
        Assert.Equal(save.NextRoundInMap, back.NextRoundInMap);
        Assert.Equal(save.RoundsCompleted, back.RoundsCompleted);
        Assert.True(TournamentRoomRequest.SaveIsComplete(back));
    }

    [Fact]
    public void IncompleteSaveIsDetected()
    {
        CreateRoomRequest? req = JsonSerializer.Deserialize(
            """{"id":1,"name":"a","maxPlayers":4,"tournament":{"settings":{"id":"t","mode":"Rounds","maps":["o:1"]},"resume":{"nextMapIndex":1}}}""",
            ServerJson.Default.CreateRoomRequest);
        Assert.False(TournamentRoomRequest.SaveIsComplete(req!.Tournament!.Resume!));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Action<string> onBody;
        public CaptureHandler(Action<string> onBody) => this.onBody = onBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onBody(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
