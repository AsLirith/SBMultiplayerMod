using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SBMultiplayer.Server;

/// <summary>
/// Pushes tournament reports to the website (SBLB) with a POST carrying the gateway secret. It tries three times, 2 s
/// and then 4 s apart, and gives up quietly. The room keeps its own save, so a lost report only delays the website.
/// </summary>
public sealed class TournamentReporter
{
    private const int Tries = 3;

    private readonly Uri reportUrl;
    private readonly string secret;
    private readonly HttpClient http;
    private readonly Func<int, TimeSpan> backoff;

    public TournamentReporter(Uri reportUrl, string secret, HttpMessageHandler? handler = null, Func<int, TimeSpan>? backoff = null)
    {
        this.reportUrl = reportUrl;
        this.secret = secret;
        this.backoff = backoff ?? (attempt => TimeSpan.FromSeconds(attempt * 2));
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>Sends one report. Never throws.</summary>
    public async Task SendAsync(TournamentReport report)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(ToWire(report), ReportJson.Default.ReportWire);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Tournament report not sent, could not build it: {e.Message}");
            return;
        }

        for (int attempt = 1; attempt <= Tries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, reportUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
                };
                request.Headers.Add("x-gateway-secret", secret);
                using HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
                Console.Error.WriteLine($"Tournament report {report.TournamentId} ({report.Kind}): HTTP {(int)response.StatusCode} (try {attempt}/{Tries})");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Tournament report {report.TournamentId} ({report.Kind}) failed: {e.GetType().Name} (try {attempt}/{Tries})");
            }
            if (attempt < Tries)
            {
                try
                {
                    await Task.Delay(backoff(attempt)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    private static ReportWire ToWire(TournamentReport r) => new(
        r.TournamentId,
        CamelCase(r.Kind.ToString()),
        r.Save,
        r.Standings.Select(p => new PlaceWire(p.Place, p.SteamId, p.Name, p.Points, p.Wins, p.RoundsPlayed,
            CamelCase(p.Status.ToString()), p.Flagged)).ToList());

    private static string CamelCase(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}

internal sealed record PlaceWire(int Place, string SteamId, string Name, int Points, int Wins, int RoundsPlayed, string Status, int Flagged);

internal sealed record ReportWire(string TournamentId, string Kind, TournamentSave? Save, List<PlaceWire> Standings);

/// <summary>Report JSON for the website, with camelCase names. The save keeps PascalCase enum names. The room reads them back in any case.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ReportWire))]
internal partial class ReportJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
