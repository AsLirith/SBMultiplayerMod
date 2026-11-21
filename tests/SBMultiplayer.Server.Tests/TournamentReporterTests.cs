using System.Net;
using System.Text.Json;
using SBMultiplayer.Protocol;
using SBMultiplayer.Server;

namespace SBMultiplayer.Server.Tests;

public class TournamentReporterTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request, request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status);
        }
    }

    private static TournamentReport Report(TournamentReportKind kind) => new("t1", kind, null, new List<TournamentPlace>
    {
        new(1, "76561199227168782", "Ann", 10, 2, 3, StandingStatus.Winner, 1),
    });

    private const string Secret = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Reporter_PostsJsonWithSecret()
    {
        var handler = new FakeHandler();
        var reporter = new TournamentReporter(new Uri("http://localhost:9/report"), Secret, handler, _ => TimeSpan.Zero);
        await reporter.SendAsync(Report(TournamentReportKind.Final));

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Secret, request.Headers.GetValues("x-gateway-secret").Single());
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal("final", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("t1", doc.RootElement.GetProperty("tournamentId").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("save").ValueKind);
        JsonElement place = doc.RootElement.GetProperty("standings")[0];
        Assert.Equal(1, place.GetProperty("flagged").GetInt32());
        Assert.Equal("76561199227168782", place.GetProperty("steamId").GetString());
        Assert.Equal("winner", place.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Reporter_EndedEarlyKindIsCamelCase()
    {
        var handler = new FakeHandler();
        var reporter = new TournamentReporter(new Uri("http://localhost:9/report"), Secret, handler, _ => TimeSpan.Zero);
        await reporter.SendAsync(Report(TournamentReportKind.EndedEarly));
        using JsonDocument doc = JsonDocument.Parse(handler.Calls[0].Body);
        Assert.Equal("endedEarly", doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Reporter_RetriesThenGivesUp()
    {
        var handler = new FakeHandler { Status = HttpStatusCode.InternalServerError };
        var reporter = new TournamentReporter(new Uri("http://localhost:9/report"), Secret, handler, _ => TimeSpan.Zero);
        await reporter.SendAsync(Report(TournamentReportKind.Round));
        Assert.Equal(3, handler.Calls.Count);
    }
}
