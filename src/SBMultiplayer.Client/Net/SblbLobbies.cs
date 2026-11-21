using System.Net.Http;
using System.Text.Json;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Public SBLB lobbies: rooms made on the community website without a password (a password makes a lobby private,
/// and private lobbies aren't listed). Read from the website's /multiplayer/api/lobbies while "Join a game" is open.
/// The request runs off the game thread; the menu only reads <see cref="Lobbies"/>.
/// </summary>
internal static class SblbLobbies
{
    private const double RefreshSeconds = 10;

    public readonly record struct Lobby(string Code, GameMode? Mode, string ModeText, string MapKey, int Players, int MaxPlayers);

    private static volatile IReadOnlyList<Lobby> lobbies = Array.Empty<Lobby>();
    private static volatile bool searching;
    private static volatile bool available = true;
    private static volatile int listVersion;
    private static double nextRefreshAt;

    /// <summary>Last list read from the website.</summary>
    public static IReadOnlyList<Lobby> Lobbies => lobbies;

    /// <summary>The first request hasn't answered yet.</summary>
    public static bool Searching => searching;

    /// <summary>
    /// False when the website has no lobby list (an older website, or one with lobbies turned off): the section is
    /// hidden then. A network error keeps the last answer.
    /// </summary>
    public static bool Available => available;

    /// <summary>Changes whenever a new answer arrives.</summary>
    public static int ListVersion => listVersion;

    /// <summary>Asks again now (the "Refresh" button, or opening "Join a game").</summary>
    public static void Refresh()
    {
        nextRefreshAt = 0;
        Tick();
    }

    /// <summary>Called while "Join a game" is open: asks the website again every few seconds.</summary>
    public static void Tick()
    {
        double now = NetClock.Now;
        if (searching || now < nextRefreshAt)
        {
            return;
        }
        nextRefreshAt = now + RefreshSeconds;
        searching = true;
        string api = ModConfig.Current.RoomSite.Trim().TrimEnd('/') + "/multiplayer/api/lobbies";
        Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                using HttpResponseMessage response = await http.GetAsync(api);
                if (!response.IsSuccessStatusCode)
                {
                    // 404: this website has no public lobbies. Anything else is likely brief, so keep the last list.
                    if ((int)response.StatusCode == 404)
                    {
                        available = false;
                        lobbies = Array.Empty<Lobby>();
                    }
                    return;
                }
                lobbies = Parse(await response.Content.ReadAsStringAsync());
                available = true;
            }
            catch (Exception e)
            {
                ModLog.Warn($"SBLB lobby list unavailable: {e.Message}");
            }
            finally
            {
                searching = false;
                listVersion++;
            }
        });
    }

    private static IReadOnlyList<Lobby> Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("lobbies", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Lobby>();
        }
        var result = new List<Lobby>();
        foreach (JsonElement item in list.EnumerateArray())
        {
            string code = Str(item, "code");
            if (code == "")
            {
                continue;
            }
            string modeId = Str(item, "modeId");
            GameMode? mode = Enum.TryParse(modeId, out GameMode parsed) ? parsed : null;
            result.Add(new Lobby(code, mode, Str(item, "mode"), Str(item, "map"), Int(item, "players"), Int(item, "maxPlayers")));
        }
        return result;
    }

    // Fields are read leniently: a newer website may add or drop some.
    private static string Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
}
